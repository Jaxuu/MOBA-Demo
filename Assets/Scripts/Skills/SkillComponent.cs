using System;
using UnityEngine;
using MOBA.Components;
using MOBA.Core;

namespace MOBA.Skills
{
    /// <summary>
    /// 技能组件：技能释放的【唯一入口】，负责"这一次施法能不能放、放了之后走哪条路径"。
    ///
    /// 职责边界：
    /// 1. 只做校验与分派：校验链（自身状态 → 配置 → CD → 蓝量 → 距离 → 目标合法性）
    ///    → 扣蓝起 CD → 前摇 → 按施法类型分派给弹道或范围场；
    /// 2. 【不结算任何效果】：伤害、护盾、减速、眩晕一律交给 SkillEffectResolver，
    ///    这样"弹道扣血"与"AOE 扣血"共用同一条规则，不会分叉；
    /// 3. 【不引用任何表现资源】：弹道的生成交给 ProjectileSpawner（它才是持有预制体的那一层），
    ///    本类只广播事件。这是 README §3.4「逻辑层禁止引用表现资源」的落地方式；
    /// 4. 不搜索目标：目标由输入层（PlayerSkillController）给定，本类只校验它是否合法。
    ///
    /// 运行时状态（各槽位冷却、是否正在前摇）全部保存在本实例中，绝不写回 SkillData 资产。
    ///
    /// 【前摇是逻辑计时，不是动画时长】castEndTime 用 Time.time 绝对时间点（与 CombatComponent.nextAttackTime
    /// 同一模式），到点即释放。动画只做事后视觉匹配——README §3.4 第 3 条要求"逻辑层不等待动画"，
    /// 因此动画被裁剪、掉帧、特效池耗尽都不会改变技能的实际生效时刻。
    /// </summary>
    [DisallowMultipleComponent]
    public class SkillComponent : MonoBehaviour
    {
        [Header("技能配置（优先由 EntityBase 从 EntityStatsData 注入；此处为 Inspector 兜底）")]
        [Tooltip("四个槽位的技能配置，下标 = (int)SkillSlot（0=Q / 1=W / 2=E / 3=R）。\n" +
                 "用数组而不是四个独立字段：槽位数量与 SkillSlot 的显式编号强绑定，" +
                 "数组下标天然表达这层关系，日后新增槽位时不会出现「补了枚举却忘了补字段」的漏改。\n" +
                 "留空（null）的槽位表示该技能未配置，TryCast 会拒绝并告警一次。")]
        [SerializeField] private SkillData[] skillSlots = new SkillData[SlotCount];

        [Header("调试")]
        [Tooltip("在 Console 输出每次成功施法的技能名与消耗。")]
        [SerializeField] private bool logCastEvents = true;

        /// <summary>
        /// 槽位数量。与 SkillSlot 的显式编号强绑定（枚举值直接用作数组下标）。
        /// 若日后新增槽位，这里与 SkillSlot 必须同时改，否则会下标越界。
        /// </summary>
        private const int SlotCount = 4;

        /// <summary>
        /// 施法距离的容差（单位）。
        /// 必要性：距离是"中心点对中心点"计算的，而玩家看到的是模型边缘；
        /// 加上模型半径与 NavMesh 吸附的误差，严格比较会让"看起来就在射程边缘"的目标被拒绝，
        /// 手感上表现为"技能时灵时不灵"。0.25 米约等于一个英雄身位半径，肉眼不可察。
        /// </summary>
        private const float CastRangeTolerance = 0.25f;

        /// <summary>
        /// 各槽位的下次可释放时间（Time.time 基准）。
        /// 用"绝对时间点"而不是"剩余冷却秒数"：后者需要每帧递减，组件被禁用时会漏减；
        /// 前者只在释放时写一次，天然不受帧率与启停影响。
        /// </summary>
        private readonly float[] nextCastTime = new float[SlotCount];

        /// <summary>是否已就"某槽位未配置技能"告警过，按槽位区分，避免重复刷屏。</summary>
        private readonly bool[] hasWarnedMissingData = new bool[SlotCount];

        /// <summary>是否已就"缺少 EntityBase"报过错误，防止每次施法都刷日志。</summary>
        private bool hasReportedMissingOwner;

        /// <summary>
        /// 是否已就"模板技能被实例配置挡住"告警过（每个组件只报一次）。
        /// 见 WarnIfTemplateIgnored 的说明：它是一个"值得被看见但不应刷屏"的状态。
        /// </summary>
        private bool hasWarnedTemplateSkillIgnored;

        // ---- 延迟解析的组件引用（兼容"代码动态创建、组件挂载顺序不可控"的场景）----
        private EntityBase owner;
        private ManaComponent mana;
        private HealthComponent health;
        private MovementComponent movement;
        private CombatComponent combat;
        private TargetingComponent targeting;
        private BuffComponent buff;
        private ProjectileSpawner projectileSpawner;

        // ---- 前摇状态 ----
        private bool isCasting;
        private SkillSlot castingSlot = SkillSlot.Q;
        private SkillData castingData;
        private ITargetable castingTarget;
        private Vector3 castingGroundPoint;
        private float castEndTime;

        /// <summary>本次前摇是否成功上过锁。用于保证解锁与上锁严格配对，不会误解别人的锁。</summary>
        private bool hasLockedControls;

        /// <summary>是否已订阅死亡事件。</summary>
        private bool hasSubscribedDeath;

        /// <summary>
        /// 施法开始事件（校验通过、扣蓝起 CD 之后立即广播）。
        /// 表现层用它播施法动画与施法特效——这也是"前摇期间有视觉反馈"的来源。
        /// 拒绝路径（校验失败）不会广播任何事件，因此"被拒绝时不播动画"是结构上保证的。
        /// </summary>
        public event Action<SkillSlot, SkillData> OnCastStarted;

        /// <summary>
        /// 技能释放事件（前摇结束、效果真正生效时广播）。
        /// 表现层用它播释放特效与音效。只有真的生成了弹道 / 范围场才会广播，
        /// 因此"没打出去却播了特效"不会发生。
        /// </summary>
        public event Action<SkillSlot, SkillData> OnSpellReleased;

        /// <summary>当前是否正在施法前摇中（只读）。BuffComponent 解除眩晕前会检查它，避免误解前摇的锁。</summary>
        public bool IsCasting => isCasting;

        /// <summary>
        /// 最近一次【成功释放】的技能所锁定的目标（只读）；非指向性 / 自身施法为 null。
        ///
        /// 【为什么需要它】OnSpellReleased 的参数只有（槽位, 技能配置），而表现层要把特效放到
        /// "被打的那个单位"身上就必须知道目标是谁。这与 HealthComponent.LastDamageSource 是同一个模式：
        /// 把"最近一次事件的有效载荷"以只读属性暴露出来，订阅方在事件回调里读它 ——
        /// 逻辑层不需要知道有谁在订阅，也不产生任何反向依赖（表现层只读，绝不回写）。
        ///
        /// 【为什么不能读 TargetingComponent.CurrentTarget 代替】那是【普攻】的目标。
        /// 玩家完全可以右键锁定 A、把技能丢给鼠标下的 B，两者不是同一件事。
        /// </summary>
        public ITargetable LastCastTarget { get; private set; }

        /// <summary>
        /// 最近一次【成功释放】的技能所用的地面落点（只读）；指向性 / 自身施法时为施法者自身位置。
        ///
        /// 【为什么需要它（阶段九新增）】表现层要在"范围场的真实位置"播地面特效（脚下的圈）。
        /// 非指向性技能的落点由输入层给定（鼠标位置），SkillComponent 是唯一知道它的地方；
        /// 而 OnSpellReleased 的参数只有（槽位, 技能配置），不带落点。
        /// 与 LastCastTarget 是同一个模式：把"最近一次事件的有效载荷"以只读属性暴露出来，
        /// 逻辑层不需要知道有谁在订阅，也不产生任何反向依赖。
        /// </summary>
        public Vector3 LastCastGroundPoint { get; private set; }

        #region 组件解析

        private EntityBase Owner
        {
            get
            {
                if (owner == null)
                {
                    owner = GetComponent<EntityBase>();
                }

                return owner;
            }
        }

        private ManaComponent Mana
        {
            get
            {
                if (mana == null)
                {
                    mana = GetComponent<ManaComponent>();
                }

                return mana;
            }
        }

        private HealthComponent Health
        {
            get
            {
                if (health == null)
                {
                    health = GetComponent<HealthComponent>();
                }

                return health;
            }
        }

        private MovementComponent Movement
        {
            get
            {
                if (movement == null)
                {
                    movement = GetComponent<MovementComponent>();
                }

                return movement;
            }
        }

        private CombatComponent Combat
        {
            get
            {
                if (combat == null)
                {
                    combat = GetComponent<CombatComponent>();
                }

                return combat;
            }
        }

        private TargetingComponent Targeting
        {
            get
            {
                if (targeting == null)
                {
                    targeting = GetComponent<TargetingComponent>();
                }

                return targeting;
            }
        }

        private BuffComponent Buff
        {
            get
            {
                if (buff == null)
                {
                    buff = GetComponent<BuffComponent>();
                }

                return buff;
            }
        }

        /// <summary>
        /// 弹道生成器（延迟解析）。
        /// 属性名刻意叫 Spawner 而不是 ProjectileSpawner：后者与类型同名，会命中 C# 的
        /// 「Color Color」规则（规范允许，但同一标识符在类型上下文与表达式上下文里含义不同，
        /// 可读性差且极易改错）。项目既有约定是彻底避开该规则（见 EntityBase 的同类说明）。
        /// </summary>
        private ProjectileSpawner Spawner
        {
            get
            {
                if (projectileSpawner == null)
                {
                    projectileSpawner = GetComponent<ProjectileSpawner>();
                }

                return projectileSpawner;
            }
        }

        #endregion

        private void Awake()
        {
            // 只做一次尝试性缓存，不做校验：这些组件都是"允许为空"的可选依赖
            // （例如没有法力的单位、没有移动能力的建筑），缺失的后果由各自的使用点处理。
            owner = GetComponent<EntityBase>();
            mana = GetComponent<ManaComponent>();
            health = GetComponent<HealthComponent>();
            movement = GetComponent<MovementComponent>();
            combat = GetComponent<CombatComponent>();
            targeting = GetComponent<TargetingComponent>();
            buff = GetComponent<BuffComponent>();
            projectileSpawner = GetComponent<ProjectileSpawner>();
        }

        private void Start()
        {
            EnsureDeathSubscription();
        }

        private void OnDestroy()
        {
            if (hasSubscribedDeath && health != null)
            {
                health.OnDied -= HandleOwnerDied;
            }
        }

        /// <summary>
        /// 订阅死亡事件：死亡是 V1 唯一的"打断前摇"路径（README 明确不做主动打断 / 取消 / 连招）。
        /// </summary>
        private void EnsureDeathSubscription()
        {
            if (hasSubscribedDeath)
            {
                return;
            }

            HealthComponent resolvedHealth = Health;
            if (resolvedHealth == null)
            {
                return;
            }

            resolvedHealth.OnDied += HandleOwnerDied;
            hasSubscribedDeath = true;

            // 订阅时可能已经死亡（代码动态创建后立刻被击杀）：事件订阅不会补发，主动补一次。
            if (resolvedHealth.IsDead && isCasting)
            {
                HandleOwnerDied();
            }
        }

        #region 配置注入与查询

        /// <summary>
        /// 注入 Q / W 两槽配置（阶段六的旧重载，保留给旧调用点）。
        /// 语义与四参重载完全一致：**实例整体接管，模板只服务全新单位**（见四参重载的说明）。
        /// </summary>
        /// <param name="q">Q 槽位缺省配置，允许为 null。</param>
        /// <param name="w">W 槽位缺省配置，允许为 null。</param>
        public void Initialize(SkillData q, SkillData w)
        {
            Initialize(q, w, null, null);
        }

        /// <summary>
        /// 注入四槽技能配置（阶段八：玩家英雄扩为 Q / W / E / R 四槽）。
        ///
        /// 【规则：实例整体接管，模板只在"全新单位"时提供 —— 这是实机打回后修正的一条硬规则】
        /// 只要本组件的四个槽位里**已经有任意一个**配置，就认为技能由【实例】提供
        /// （玩家 = 英雄预制体直挂的盖伦四件套；AI = 工具按技能池抽签后的按实例注入），
        /// 此时**模板（EntityStatsData）完全不参与**，一个槽位都不会写。
        /// 只有四个槽位全空的全新单位，才由模板整体提供四槽。
        ///
        /// 【为什么必须是"整体接管"而不是"只补空" —— 实机打回的根因链】
        /// 上一版是"传进来的非 null 就写"，于是运行期出现了**两个写入者**：
        ///   · 工具在【编辑期】把 AI 英雄的技能池抽签结果写进实例的 skillSlots（正确的）；
        ///   · `EntityBase.ApplyStats` 在【运行期 Start】又拿共享的 `EntityStatsData` 调本方法，
        ///     把 10 个英雄（含 AI）的四个槽全部改写成玩家那套盖伦 QWER —— 因为 10 个英雄共用
        ///     同一份 HeroStats 资产，而它里面配着盖伦的四件套。
        /// 结果：编辑期的注入**确实写进了场景**（编辑器里看 Inspector 完全正确），
        /// 却在运行时被静默覆盖 —— 工具侧的任何"读回校验"都查不出这种错，因为它在 Start 才发生。
        ///
        /// 【为什么不能退一步改成"只补空槽"】那样虽然保住了已填的槽位，却会给 AI 英雄**空着的槽位**
        /// 补上玩家专属技能：只要技能池抽到的技能少于 4 个（或场景里的实例是上一版工具写的、
        /// E / R 还是空的），AI 立刻又会长出盖伦的大风车与大宝剑。"任意一槽非空即整体接管"
        /// 把这条缝彻底封死：**AI 英雄的技能槽永远只可能来自技能池注入。**
        /// </summary>
        /// <param name="q">Q 槽位模板配置，允许为 null。</param>
        /// <param name="w">W 槽位模板配置，允许为 null。</param>
        /// <param name="e">E 槽位模板配置，允许为 null。</param>
        /// <param name="r">R 槽位模板配置，允许为 null。</param>
        public void Initialize(SkillData q, SkillData w, SkillData e, SkillData r)
        {
            if (HasInstanceSkillData())
            {
                // 实例已接管：模板一律不写，但"模板里配了技能却被忽略"这件事要留痕（见该方法的说明）。
                WarnIfTemplateIgnored(q, w, e, r);
                return;
            }

            // 四个槽位全空 = 全新单位（旧预制体 / 测试用单位）：由模板整体提供。
            SetSkillData(SkillSlot.Q, q);
            SetSkillData(SkillSlot.W, w);
            SetSkillData(SkillSlot.E, e);
            SetSkillData(SkillSlot.R, r);
        }

        /// <summary>四个槽位里是否已经有任意一个配置（= 技能由实例提供）。</summary>
        /// <returns>任意一槽非空返回 true。</returns>
        private bool HasInstanceSkillData()
        {
            for (int i = 0; i < SlotCount; i++)
            {
                if (GetSkillData((SkillSlot)i) != null)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 若模板里配了与实例不同的技能，报一次警告（每个组件只报一次）。
        ///
        /// 【为什么"被忽略"也必须报出来】"编辑器里配了技能、实机却不生效、Console 一片安静"
        /// 正是本次实机打回最难排查的形态。这条警告把那个状态变成可见的：
        /// 它说明有人把技能填回了共享属性资产，而那份值不会生效（技能已改为由 SkillComponent 承载）。
        /// 每个组件只报一次：10 个英雄最多 10 行，不会刷屏。
        /// </summary>
        /// <param name="q">Q 槽位模板配置。</param>
        /// <param name="w">W 槽位模板配置。</param>
        /// <param name="e">E 槽位模板配置。</param>
        /// <param name="r">R 槽位模板配置。</param>
        private void WarnIfTemplateIgnored(SkillData q, SkillData w, SkillData e, SkillData r)
        {
            if (hasWarnedTemplateSkillIgnored)
            {
                return;
            }

            SkillData[] templateValues = { q, w, e, r };

            for (int i = 0; i < templateValues.Length; i++)
            {
                SkillSlot slot = (SkillSlot)i;
                SkillData template = templateValues[i];

                if (template == null)
                {
                    continue;
                }

                SkillData instance = GetSkillData(slot);

                // 模板值与实例值指向同一份资产 → 完全等价，不算"被忽略"。
                if (ReferenceEquals(template, instance))
                {
                    continue;
                }

                hasWarnedTemplateSkillIgnored = true;

                string instanceText = instance != null ? instance.DisplayName : "（空）";

                Debug.LogWarning(
                    $"[SkillComponent] {name} 的 {slot} 槽由实例提供（{instanceText}），" +
                    $"EntityStatsData 里的 {template.DisplayName} 被忽略 —— 规则是【实例整体接管，模板只服务全新单位】。\n" +
                    "  · 正常情况：AI 英雄的技能来自技能池按实例注入，玩家英雄来自预制体直挂，两者都不该被共享模板覆盖。\n" +
                    "  · 若你确实想通过 EntityStatsData 改技能：那四个字段已不再承载技能（一键组装会清空它们并给出说明），" +
                    "请直接改 Skills/ 下的技能资产，或按实例注入。（同一组件只提示一次）", this);
                return;
            }
        }

        /// <summary>
        /// 写入单个槽位的配置（传 null 表示"不改动该槽位"）。
        ///
        /// 【为什么需要它】AI 英雄的技能是【按实例】注入的（9 个 AI 英雄的技能互不相同，
        /// 共享的 EntityStatsData 无法表达），注入方拿到的是一个个具体的槽位与资产，
        /// 走这个入口比"拼一个四元素数组再整体覆盖"更不容易误伤其它槽位。
        ///
        /// 【它是低层写入器，不做任何优先级判断】"实例整体接管"那条规则在
        /// <see cref="HasInstanceSkillData"/> 与 <see cref="Initialize(SkillData, SkillData, SkillData, SkillData)"/>
        /// 里；本方法"写了就是写了"，
        /// 因为它服务的是"按实例注入"这条最具体的通道。
        /// </summary>
        /// <param name="slot">目标槽位。</param>
        /// <param name="data">技能配置，允许为 null（表示不改动）。</param>
        /// <returns>确实写入了返回 true。</returns>
        public bool SetSkillData(SkillSlot slot, SkillData data)
        {
            if (data == null)
            {
                return false;
            }

            int index = (int)slot;
            if (index < 0 || index >= SlotCount)
            {
                return false;
            }

            // 数组可能因为序列化数据比 SlotCount 短而越界（例如手工把数组长度改小了），
            // 这里补长而不是抛 IndexOutOfRange —— 装配错误应该在编辑器里被发现，
            // 而不是让运行时的每一次施法都炸掉。
            if (skillSlots == null || skillSlots.Length != SlotCount)
            {
                SkillData[] resized = new SkillData[SlotCount];

                if (skillSlots != null)
                {
                    int copyCount = Mathf.Min(skillSlots.Length, SlotCount);
                    for (int i = 0; i < copyCount; i++)
                    {
                        resized[i] = skillSlots[i];
                    }
                }

                skillSlots = resized;
            }

            skillSlots[index] = data;
            return true;
        }

        /// <summary>
        /// 取指定槽位的技能配置；未配置返回 null。
        /// 公开出来是给输入层（PlayerSkillController / HeroAIController）用的——
        /// 它们需要先知道施法类型，才能决定"这次该拾取敌方单位、友方单位还是地面落点"。
        /// </summary>
        /// <param name="slot">技能槽位。</param>
        /// <returns>技能配置；未配置返回 null。</returns>
        public SkillData GetSkillData(SkillSlot slot)
        {
            int index = (int)slot;
            if (skillSlots == null || index < 0 || index >= skillSlots.Length)
            {
                return null;
            }

            return skillSlots[index];
        }

        /// <summary>
        /// 查询指定槽位的冷却剩余秒数；已就绪返回 0。
        /// README 阶段六明确要求"技能冷却剩余时间可查询"，阶段七的 HUD 冷却遮罩直接读这里。
        /// </summary>
        /// <param name="slot">技能槽位。</param>
        /// <returns>剩余冷却秒数（≥ 0）。</returns>
        public float GetCooldownRemaining(SkillSlot slot)
        {
            int index = (int)slot;
            if (index < 0 || index >= SlotCount)
            {
                return 0f;
            }

            return Mathf.Max(0f, nextCastTime[index] - Time.time);
        }

        #endregion

        #region 施法入口

        /// <summary>
        /// 尝试释放技能。全项目唯一的技能释放入口。
        ///
        /// 校验链（严格按 README §3.5 / 计划书阶段六的顺序，任一失败立即返回 false 且【零副作用】）：
        ///   ① 施法者状态（死亡 / 眩晕 / 正在前摇）
        ///   ② 槽位配置存在
        ///   ③ 冷却就绪
        ///   ④ 蓝量充足
        ///   ⑤ 施法距离（指向性量到目标单位，非指向性量到落点）
        ///   ⑥ 目标合法性（复用 EntityBase.IsValidTarget + TargetingComponent.IsEnemy）
        /// 全部通过后才：扣蓝 → 起 CD → 上前摇 → 广播 OnCastStarted。
        ///
        /// 【为什么"拒绝路径零副作用"是结构上保证的】扣蓝与写 CD 都排在整条校验链之后，
        /// 因此"CD 中 / 蓝不足 / 超距 / 目标非法"四条路径既不会扣蓝、也不会起 CD、更不会广播事件——
        /// 表现层收不到事件，自然"不播动画、不生成特效"。验收项无需额外代码来保证。
        /// </summary>
        /// <param name="slot">技能槽位。</param>
        /// <param name="groundPoint">地面落点（非指向性技能使用；指向性技能可传任意值）。</param>
        /// <param name="target">目标单位（指向性技能使用；非指向性技能传 null）。</param>
        /// <param name="failReason">
        /// 失败原因；成功时为 null。
        ///
        /// 【为什么要有这个出参】技能被拒绝时只返回一个 false，使用者看到的就是"按了没反应"——
        /// 分不清是冷却、没蓝、距离不够还是目标选错了，而这四者的处理方式完全不同。
        /// 因此这里把每个拒绝分支的结论都写成人话，并带上可量化的信息
        /// （剩余冷却秒数、所需与当前法力、实际距离与射程、目标阵营）。
        /// 出参是 string 而不是枚举：日志要的是"一句能直接读懂的话"，
        /// 用枚举还得在调用方再拼一次文案，等于把同一件事写两遍。
        /// </param>
        /// <returns>是否成功进入施法流程（true 表示已扣蓝、已起 CD、已开始前摇）。</returns>
        public bool TryCast(SkillSlot slot, Vector3 groundPoint, ITargetable target, out string failReason)
        {
            failReason = null;

            int index = (int)slot;
            if (index < 0 || index >= SlotCount)
            {
                failReason = $"非法技能槽位（{(int)slot}）";
                Debug.LogWarning($"[SkillComponent] {name} 收到非法的技能槽位 {(int)slot}，施法已忽略。", this);
                return false;
            }

            // ---- ⓪ 身份依赖（README §6.2 依赖自校验）----
            // 没有 EntityBase 就没有阵营，敌我校验与伤害归属都无从谈起；而 Owner 为 null 的后果是
            // **静默失效**：弹道类技能会因 ProjectileSpawner.Spawn(source == null) 直接返回 null，
            // 表现为"技能放不出去（连蓝和 CD 都没动）且 Console 一片安静"——最难排查的一类症状。
            //
            // 校验方式刻意用"延迟解析 + 首次使用时 LogError 一次"，而不是在 Awake 里校验：
            // 用代码动态创建单位时（MinionSpawner / Stage6AutoTester）组件挂载顺序不可控，
            // EntityBase 完全可能晚于本组件挂上，Awake 校验会对这种合法用法误报。
            // 这与 CombatComponent.Owner / TargetingComponent 的处理方式逐条一致。
            if (Owner == null)
            {
                ReportMissingOwnerOnce();
                failReason = "缺少 EntityBase（无法判断阵营与伤害归属）";
                return false;
            }

            // ---- ① 施法者自身状态 ----
            if (IsDead)
            {
                failReason = "施法者已死亡";
                return false;
            }

            BuffComponent resolvedBuff = Buff;
            if (resolvedBuff != null && resolvedBuff.IsStunned)
            {
                // 眩晕中不能施法。状态本身是正常的，因此只写进 failReason，不额外打日志（否则会刷屏）。
                failReason = "眩晕中无法施法";
                return false;
            }

            // ---- ①b 沉默（阶段八新增）----
            // 沉默是"禁止施法"的唯一实现处：它不像眩晕那样去抢移动/攻击锁，
            // 因此这里就是它全部的效力所在（见 BuffType.Silence 的说明）。
            // 位置放在眩晕之后、前摇之前：沉默与眩晕是两种独立状态，同时存在时先报更"重"的那一个。
            if (resolvedBuff != null && resolvedBuff.IsSilenced)
            {
                failReason = "被沉默，无法施法";
                return false;
            }

            if (isCasting)
            {
                // V1 不做技能排队 / 连招（README §3.5 设计边界），前摇期间的新指令一律忽略。
                failReason = $"前摇硬直中（{castingSlot} 正在施法，{Mathf.Max(0f, castEndTime - Time.time):F2}s 后释放）";
                return false;
            }

            // ---- ② 槽位配置存在 ----
            SkillData data = GetSkillData(slot);
            if (data == null)
            {
                failReason = $"{slot} 槽位未配置技能（SkillData 为空）";
                WarnMissingSkillDataOnce(index, slot);
                return false;
            }

            // ---- ③ 冷却就绪 ----
            float cooldownRemaining = GetCooldownRemaining(slot);
            if (cooldownRemaining > 0f)
            {
                failReason = $"冷却中（还需 {cooldownRemaining:F1}s）";
                return false;
            }

            // ---- ④ 蓝量充足 ----
            ManaComponent resolvedMana = Mana;
            if (data.ManaCost > 0f)
            {
                if (resolvedMana == null)
                {
                    failReason = "缺少 ManaComponent（无法扣除法力）";
                    return false;
                }

                if (!resolvedMana.HasEnough(data.ManaCost))
                {
                    failReason = $"法力不足（需要 {data.ManaCost:F0}，当前 {resolvedMana.CurrentMana:F0}）";
                    return false;
                }
            }

            // ---- ⑤ + ⑥ 施法距离与目标合法性 ----
            Vector3 aimPoint;
            if (!ValidateAim(data, groundPoint, target, out aimPoint, out failReason))
            {
                return false;
            }

            // ---- 校验全部通过，从这里开始才产生副作用 ----

            // 扣蓝与起 CD 都放在前摇【之前】：MOBA 惯例是"施法瞬间即进入冷却"，
            // 且 V1 只有死亡能打断前摇，因此不存在"需要返还蓝量与冷却"的场景。
            if (data.ManaCost > 0f && resolvedMana != null)
            {
                resolvedMana.TrySpend(data.ManaCost);
            }

            nextCastTime[index] = Time.time + data.Cooldown;

            // ---- ⑦ 进入前摇 ----
            castingSlot = slot;
            castingData = data;
            castingTarget = target;
            castingGroundPoint = groundPoint;
            isCasting = true;
            castEndTime = Time.time + Mathf.Max(0f, data.CastTime);

            LockControlsForCast();

            if (logCastEvents)
            {
                Debug.Log(
                    $"[SkillComponent] {name} 开始施法：{data.DisplayName}（{slot}）" +
                    $"｜前摇 {data.CastTime:F2}s｜蓝耗 {data.ManaCost:F0}｜冷却 {data.Cooldown:F1}s" +
                    $"｜落点 {aimPoint.ToString("F2")}", this);
            }

            // 广播施法开始：表现层在这里播施法动画与施法特效。
            OnCastStarted?.Invoke(slot, data);

            // 瞬发（前摇为 0）当帧就释放：若交给 Update 至少要等一帧，
            // 与"配置了 0 秒前摇"的语义不符，手感上会有难以解释的一帧延迟。
            if (castEndTime <= Time.time)
            {
                Release();
            }

            return true;
        }

        /// <summary>
        /// 校验施法距离与目标合法性，失败时给出可读原因。
        /// </summary>
        /// <param name="data">技能配置。</param>
        /// <param name="groundPoint">地面落点。</param>
        /// <param name="target">目标单位。</param>
        /// <param name="aimPoint">输出：用于距离计算的判定点（指向性为目标位置，非指向性为落点）。</param>
        /// <param name="failReason">输出：失败原因；通过时为 null。</param>
        /// <returns>通过返回 true。</returns>
        private bool ValidateAim(
            SkillData data, Vector3 groundPoint, ITargetable target, out Vector3 aimPoint, out string failReason)
        {
            aimPoint = groundPoint;
            failReason = null;

            // ---- 自身施法：既不需要目标也不需要落点 ----
            // 直接返回 true 并把判定点设为自身位置。三条语义都在这里一次性说清：
            //   ① 没有"目标合法性"这回事（自己永远合法）；
            //   ② 没有"距离太远"这回事（施法者到自己恒为 0 米）——
            //      若沿用下面的距离校验，策划一旦把 castRange 填小就会得到一个永远放不出来的技能；
            //   ③ 判定点取自身位置，日志与范围场的中心都以此为准。
            if (data.CastType == SkillCastType.Self)
            {
                aimPoint = transform.position;
                return true;
            }

            if (data.CastType == SkillCastType.UnitTarget)
            {
                if (!TryValidateTarget(target, data, out failReason))
                {
                    // 目标非法（友方 / 敌方 / 已死亡 / 不可选中 / 已被销毁）——由输入层的拾取规则保证
                    // 大多数情况下不会走到这里，但技能入口必须自己再拦一次：
                    // 目标可能在"按下按键"与"技能组件收到指令"之间失效。
                    return false;
                }

                Transform targetTransform = target.TargetTransform;
                if (targetTransform == null)
                {
                    failReason = "目标状态异常（取不到 Transform）";
                    return false;
                }

                aimPoint = targetTransform.position;
            }

            // 施法距离：统一量"施法者 → 判定点"。
            float distance = Vector3.Distance(transform.position, aimPoint);
            float maxDistance = data.CastRange + CastRangeTolerance;

            if (distance > maxDistance)
            {
                // 报出【实际距离】与【射程】而不是只说"距离太远"：
                // 玩家看到「落点 11.2m > 射程 10.0m」能立刻判断该往前站多少，看到"距离太远"只能靠猜。
                string aimLabel = data.CastType == SkillCastType.UnitTarget ? "目标" : "落点";
                failReason = $"距离太远（{aimLabel} {distance:F1}m > 射程 {data.CastRange:F1}m）";
                return false;
            }

            return true;
        }

        /// <summary>
        /// 目标是否可用于指向性技能；不可用时给出【分门别类的原因】。
        ///
        /// 存活与可选中两项复用 EntityBase.IsValidTarget（全项目唯一入口），
        /// 敌我规则复用 TargetingComponent.IsEnemy；本方法只额外负责"把为什么不行说清楚"。
        /// 之所以要先单独做一次存活检查再调 IsValidTarget：接口引用在 GameObject 被销毁后不会变成 null，
        /// 直接访问 IsValidTarget 虽然内部有兜底，但那样就只能得到"已失效"这个笼统结论，
        /// 而"目标已被销毁"和"目标已死亡"对排查来说是完全不同的两件事。
        ///
        /// 【阶段八：阵营规则由 SkillData.TargetsAlly 决定】伤害/控制类只认敌方，治疗/护盾类只认友方。
        /// 这条规则必须由数据驱动而不是由本类猜：同一个 UnitTarget 形态既可能是斩杀也可能是治疗，
        /// 只有技能配置自己知道它想要谁。敌我判定仍然复用 TargetingComponent.IsEnemy（唯一入口），
        /// 因此"友方"= 非中立且与施法者同阵营，与"敌方"严格互补。
        /// </summary>
        /// <param name="target">待校验目标。</param>
        /// <param name="data">技能配置（决定目标该是敌方还是友方）。</param>
        /// <param name="failReason">输出：失败原因；通过时为 null。</param>
        /// <returns>可用于施法返回 true。</returns>
        private bool TryValidateTarget(ITargetable target, SkillData data, out string failReason)
        {
            failReason = null;

            if (target == null)
            {
                failReason = data.TargetsAlly
                    ? "缺少目标（该技能需要锁定一个友方单位）"
                    : "缺少目标（指向性技能需要锁定一个敌方单位）";
                return false;
            }

            if (target is UnityEngine.Object unityObject && unityObject == null)
            {
                failReason = "目标已被销毁";
                return false;
            }

            if (!target.IsValidTarget)
            {
                failReason = "目标已失效（已死亡 / 不可选中）";
                return false;
            }

            TargetingComponent resolvedTargeting = Targeting;
            bool isEnemy = resolvedTargeting != null
                ? resolvedTargeting.IsEnemy(target)
                : IsEnemyFallback(target);

            // 友方判定 = "非中立 且 非敌方"。刻意用 isEnemy 取反而不是再写一遍阵营比较：
            // 中立单位的 isEnemy 已经是 false，若直接取反就会把中立单位当成友方放行。
            bool isAlly = target.Team != TeamType.Neutral && !isEnemy;

            if (data.TargetsAlly)
            {
                if (!isAlly)
                {
                    failReason = $"目标非法（{target.Team} 阵营，不是友方单位）";
                    return false;
                }

                return true;
            }

            if (!isEnemy)
            {
                failReason = $"目标非法（{target.Team} 阵营，不是敌方单位）";
                return false;
            }

            return true;
        }

        /// <summary>
        /// 没有索敌组件时的敌我退化判断。
        /// 规则与 CombatComponent / PlayerCommandController / SkillComponent 的其它路径逐条对齐
        /// （含中立阵营排除），避免出现"能锁定却不能放技能"这类规则分叉。
        /// </summary>
        private bool IsEnemyFallback(ITargetable target)
        {
            EntityBase resolvedOwner = Owner;
            if (resolvedOwner == null)
            {
                return false;
            }

            return target.Team != TeamType.Neutral && target.Team != resolvedOwner.Team;
        }

        #endregion

        #region 前摇与释放

        /// <summary>
        /// 前摇计时。到点即释放。
        /// 用"绝对时间点比较"而不是"累加 deltaTime"：后者在帧率波动下会有累积误差，
        /// 且组件被禁用时会漏累（表现为"前摇永远走不完"）。
        /// </summary>
        private void Update()
        {
            if (!isCasting)
            {
                return;
            }

            if (Time.time < castEndTime)
            {
                return;
            }

            Release();
        }

        /// <summary>
        /// 释放技能：按施法类型分派到弹道或范围场。
        /// </summary>
        private void Release()
        {
            SkillSlot slot = castingSlot;
            SkillData data = castingData;
            ITargetable target = castingTarget;
            Vector3 groundPoint = castingGroundPoint;

            // 先清前摇状态再结算：结算过程中会触发伤害与死亡回调，
            // 那些回调（例如 BuffComponent 解除眩晕时检查 IsCasting）不该看到"仍在施法中"的中间态。
            ClearCastState();

            bool released = false;

            switch (data.CastType)
            {
                case SkillCastType.UnitTarget:
                    if (data.HasProjectile)
                    {
                        ProjectileSpawner spawner = Spawner;
                        if (spawner != null)
                        {
                            released = spawner.Spawn(Owner, target, data) != null;
                        }
                        else
                        {
                            // 这是配置错误（英雄预制体必须挂 ProjectileSpawner），必须报错而不是静默失败——
                            // 否则表现为"技能放了、蓝也扣了、但什么都没有飞出来"。
                            Debug.LogError(
                                $"[SkillComponent] {name} 未挂载 ProjectileSpawner，{data.DisplayName} 无法生成弹道。" +
                                "请为一键组装工具的英雄预制体补齐该组件。", this);
                        }
                    }
                    else
                    {
                        // 瞬发指向性：直接结算（V1 无技能使用此分支，保留它是为了让 SkillData 的
                        // 「指向性 + 无弹道」组合有明确行为，而不是静默什么都不做）。
                        released = SkillEffectResolver.Apply(Owner, target, data);
                    }

                    break;

                case SkillCastType.GroundPoint:
                    released = AreaEffectZone.Spawn(Owner, data, groundPoint) != null;
                    break;

                case SkillCastType.Self:
                    // 自身施法有两条落地路径，由【显式开关 spawnZoneAtSelf】分派：
                    //   · true  → 在施法者脚下生成一个范围场（E 审判；followCaster 时每帧跟随）；
                    //   · false → 直接把主效果作用于自己（Q 强化普攻 / W 护盾 / 疾行术）。
                    //
                    // 【为什么不用「areaDuration > 0」来分派 —— 这是实机踩出来的】
                    // areaDuration 的默认值是 4（地面 AOE 需要它），因此任何没有显式清零的 Self 技能
                    // 都会被误判成范围场：按 Q 不给自己加 buff，反而在脚下生成一个"对敌人施加
                    // 强化普攻"的怪圈，而且不报任何错。施法意图必须由显式字段表达，
                    // 不能从另一个字段的数值默认值反推。
                    if (data.SpawnZoneAtSelf)
                    {
                        released = AreaEffectZone.Spawn(Owner, data, transform.position) != null;
                    }
                    else
                    {
                        // 目标就是施法者自己：EntityBase 实现了 ITargetable，
                        // 而 Apply 内部的 IsUsable 只要求"存活 + 可选中"，不会因为"目标是友方"而拒绝。
                        released = SkillEffectResolver.Apply(Owner, Owner, data);
                    }

                    break;
            }

            // 只有真的生成了效果载体才广播释放事件：表现层据此播特效，因此"没飞出去却播了特效"不会发生。
            if (released)
            {
                // 记录本次锁定的目标供表现层读取（见 LastCastTarget 的说明）。
                // 必须在广播【之前】写入：订阅方是在事件回调里同步读它的。
                LastCastTarget = target;

                // 落点同理（见 LastCastGroundPoint）：自身施法取自身位置，其余取输入层给定的落点。
                LastCastGroundPoint = data.CastType == SkillCastType.Self ? transform.position : groundPoint;

                OnSpellReleased?.Invoke(slot, data);
            }
        }

        /// <summary>
        /// 清空前摇状态并归还控制锁。
        /// </summary>
        private void ClearCastState()
        {
            isCasting = false;
            castingData = null;
            castingTarget = null;
            castingGroundPoint = Vector3.zero;
            castEndTime = 0f;

            UnlockControlsAfterCast();
        }

        /// <summary>
        /// 死亡打断：前摇期间施法者阵亡 → 作废本次施法。
        /// 注意蓝量与冷却【不返还】：这与 MOBA 惯例一致（技能已经放出去了），
        /// 且 V1 没有"取消施法"的玩家操作，不存在被恶意利用的空间。
        /// </summary>
        private void HandleOwnerDied()
        {
            if (!isCasting)
            {
                return;
            }

            if (logCastEvents)
            {
                Debug.Log($"[SkillComponent] {name} 在施法前摇中阵亡，本次施法已作废（蓝量与冷却不返还）。", this);
            }

            ClearCastState();
        }

        #endregion

        #region 控制锁（与 BuffComponent 共用）

        /// <summary>
        /// 前摇期间锁住移动与攻击。
        /// 必要性：不锁的话，玩家可以在 0.25 秒前摇里把英雄点走，弹道却从新位置飞出——
        /// 视觉与逻辑都对不上；同时"施法时不能动"也是 MOBA 的基本手感。
        ///
        /// 【阶段八：前摇为 0 的技能【不】上锁 —— 这是实机手感的一个硬要求】
        /// 上锁的第一步是 Movement.Stop()，它会清掉当前路径；而随后的解锁（同一帧内发生，
        /// 因为 castEndTime &lt;= Time.time 会立刻 Release）**并不会恢复那条路径**。
        /// 于是"边走边按 Q"会得到一个非常突兀的结果：英雄立刻停在原地不再前进，
        /// 玩家必须重新点一下地板才会继续走 —— 瞬发技能反而成了"刹车"。
        /// 对 AI 英雄更糟：FSM 的"已下达移动指令"标记仍是 true，它要等 1 秒的停滞检测
        /// 才会重新下令，表现为"每放一次增益就原地卡住一秒"。
        ///
        /// 因此只在【真的有前摇】时才上锁：没有硬直，就没有需要保护的时间窗。
        /// </summary>
        private void LockControlsForCast()
        {
            if (castingData == null || castingData.CastTime <= 0f)
            {
                return;
            }

            MovementComponent resolvedMovement = Movement;
            if (resolvedMovement != null)
            {
                // 先停下再上锁：避免前摇期间沿旧路径滑行（上锁本身也会 Stop，这里显式调用是为了
                // 让"施法瞬间立刻停下"不依赖 SetMovementLocked 的实现细节）。
                resolvedMovement.Stop();
                resolvedMovement.SetMovementLocked(true);
            }

            CombatComponent resolvedCombat = Combat;
            if (resolvedCombat != null)
            {
                resolvedCombat.SetAttackLocked(true);
            }

            hasLockedControls = true;
        }

        /// <summary>
        /// 前摇结束，归还控制锁。
        ///
        /// 【为什么要检查眩晕】眩晕与施法前摇共用同一把锁。若这里无条件解锁，会出现
        /// "眩晕在前摇期间生效 → 前摇结束把眩晕的锁也解了 → 被眩晕的单位开始自由移动"的破绽。
        /// BuffComponent.ReleaseControlLocks 里有一处对称的判断（它解锁前会检查 IsCasting），
        /// 两边合起来保证：锁只在【两个持有者都放开】之后才真正解除。
        /// </summary>
        private void UnlockControlsAfterCast()
        {
            if (!hasLockedControls)
            {
                return;
            }

            hasLockedControls = false;

            BuffComponent resolvedBuff = Buff;
            if (resolvedBuff != null && resolvedBuff.IsStunned)
            {
                // 眩晕仍持有锁，交由 BuffComponent 在到期时释放。
                return;
            }

            MovementComponent resolvedMovement = Movement;
            if (resolvedMovement != null)
            {
                resolvedMovement.SetMovementLocked(false);
            }

            CombatComponent resolvedCombat = Combat;
            if (resolvedCombat != null)
            {
                resolvedCombat.SetAttackLocked(false);
            }
        }

        #endregion

        /// <summary>是否已死亡（生命组件缺失时视为未死亡）。</summary>
        private bool IsDead
        {
            get
            {
                HealthComponent resolvedHealth = Health;
                return resolvedHealth != null && resolvedHealth.IsDead;
            }
        }

        /// <summary>就"槽位未配置技能"告警一次。</summary>
        private void WarnMissingSkillDataOnce(int index, SkillSlot slot)
        {
            if (hasWarnedMissingData[index])
            {
                return;
            }

            hasWarnedMissingData[index] = true;
            Debug.LogWarning(
                $"[SkillComponent] {name} 的 {slot} 槽位没有配置 SkillData，该技能无法释放。" +
                "请在 EntityStatsData 或本组件的 Inspector 上指定配置资产（一键组装工具会自动注入 Q/W）。", this);
        }

        /// <summary>
        /// 就"缺少 EntityBase"报错一次（README §6.2：缺失必需依赖必须 LogError，不允许静默失败）。
        /// 用一次性标记防刷屏：这个错误会持续存在，每次施法都刷一条只会淹掉其它信息。
        /// </summary>
        private void ReportMissingOwnerOnce()
        {
            if (hasReportedMissingOwner)
            {
                return;
            }

            hasReportedMissingOwner = true;
            Debug.LogError(
                $"[SkillComponent] {name} 上找不到 EntityBase，无法判断阵营，所有技能都放不出来。" +
                "请确保 EntityBase 与 SkillComponent 挂在同一个 GameObject 上。", this);
        }
    }
}
