using UnityEngine;
using MOBA.Components;
using MOBA.Core;
using MOBA.Skills;

namespace MOBA.Controllers
{
    /// <summary>
    /// AI 英雄技能决策器（阶段八新增）：只负责"什么时候放哪个技能"，其余一律不管。
    ///
    /// 职责边界（README 4.5）：
    /// 1. 【只做技能决策】按节流周期扫描已分配技能：CD 就绪 + 蓝够 + 按 SkillCastType 选目标 / 落点 / 自身
    ///    → 调 SkillComponent.TryCast；
    /// 2. 【绝不直接写 Movement / Combat】移动、追击、平A 全部由 EntityAIController 的 FSM 负责。
    ///    这是与"玩家英雄不挂 FSM"完全对称的另一半裁决：
    ///    · 玩家英雄：指令层写 Movement/Combat，因此不挂 FSM；
    ///    · AI 英雄：FSM 写 Movement/Combat，因此本类不碰它们。
    ///    两边都靠"同一个单位上只有一条写入路径"来避免互相覆盖指令。
    /// 3. 不自己判断冷却与蓝量——那是 SkillComponent.TryCast 的前置校验链，
    ///    本类只做"值不值得试一次"的粗筛（节流 + 射程 + 距离），拒绝原因由 TryCast 通过 failReason 给出。
    ///
    /// 【与玩家输入层的对称性】本类与 PlayerSkillController 是同一件事的两条实现：
    /// 都是"把施法意图翻译成 TryCast 调用"，都【不做】任何规则判定。
    /// 因此"玩家按 R 打不出去"与"AI 想放 R 打不出去"的失败原因永远是同一套字符串。
    ///
    /// 【阶段八第二步：本类已实装】第一步只挂了个骨架（依赖校验 + 节流驱动），
    /// 第二步填充 EvaluateSkills 并置 enableSkillDecisions = true。
    /// 装配清单因此没有任何变化 —— 这正是"先把装配问题清零、再填逻辑"这条策略的收益。
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(EntityBase))]
    public class HeroAIController : MonoBehaviour
    {
        [Header("依赖")]
        [Tooltip("本单位的实体身份入口。留空时自动从同一 GameObject 上获取。")]
        [SerializeField] private EntityBase entity;

        [Header("技能决策")]
        [Tooltip("是否启用技能决策。工具会在 AI 英雄实例上显式注入 true（见 AutoSceneBuilder.CreateHeroInstance）。")]
        [SerializeField] private bool enableSkillDecisions = true;

        [Tooltip("技能决策的节流间隔（秒）。技能尝试会做目标筛选与射程比较，" +
                 "9 个 AI 英雄每帧各算一次没有意义，按固定周期评估更省也更稳定。")]
        [Min(0.05f)]
        [SerializeField] private float decisionInterval = 0.5f;

        [Tooltip("技能起手距离的宽松系数：只有当目标距离小于「技能施法距离 × 该系数」时才尝试释放。" +
                 "大于 1 是为了给「目标正在移动」留余量——按精确射程筛会把「刚好能打到的目标」全部漏掉。")]
        [Min(1f)]
        [SerializeField] private float castRangeSlack = 1.15f;

        [Tooltip("「自身增益类」技能的起手交战距离（米）：只有当前目标在这个距离内才会用。\n" +
                 "为什么需要它：Self 技能（护盾 / 加速 / 强化普攻）没有目标，若不加这道闸门，" +
                 "AI 会在兵线上一边走一边把所有增益空放掉，真正打起来时反而全在冷却。")]
        [Min(1f)]
        [SerializeField] private float selfCastEngageRange = 12f;

        [Tooltip("友方治疗类技能的施放阈值（血量百分比）：只治疗血量低于该比例的友方。\n" +
                 "1 表示不设阈值。留一点余量（默认 0.9）是为了避免「人人满血时也去放治疗」这种纯浪费。")]
        [Range(0.05f, 1f)]
        [SerializeField] private float allyHealHealthThreshold = 0.9f;

        [Tooltip("在 Console 输出每次成功施法的技能名与目标。9 个 AI 英雄的施法频率不高（受 CD 限制），" +
                 "默认开启；被拒绝的原因只对每个槽位各报一次，避免刷屏。")]
        [SerializeField] private bool logSkillDecisions = true;

        /// <summary>技能组件。技能的唯一施放入口，必须存在（否则 AI 英雄没有任何技能可放）。</summary>
        private SkillComponent skills;

        /// <summary>索敌组件。技能选目标时读它，必须存在。</summary>
        private TargetingComponent targeting;

        /// <summary>法力组件。允许缺失（没配法力的英雄，其技能蓝耗应当为 0）。</summary>
        private ManaComponent mana;

        /// <summary>生命组件。用于死亡闸门，允许缺失。</summary>
        private HealthComponent health;

        /// <summary>状态容器。用于眩晕 / 沉默闸门，允许缺失。</summary>
        private BuffComponent buff;

        /// <summary>距离下次允许评估技能的剩余时间（秒）。归零时执行一次评估并重置。</summary>
        private float decisionTimer;

        /// <summary>是否已完成初始化（依赖校验通过）。未完成时 Update 直接短路。</summary>
        private bool hasInitialized;

        /// <summary>是否已就"缺少 SkillComponent"报过错误，防止每帧刷屏。</summary>
        private bool hasReportedMissingSkills;

        /// <summary>是否已就"缺少 TargetingComponent"报过错误，防止每帧刷屏。</summary>
        private bool hasReportedMissingTargeting;

        /// <summary>
        /// 各槽位是否已报告过"粗筛通过但被 TryCast 拒绝"的原因。
        /// 为什么每个槽位只报一次：冷却/蓝量不足会持续存在，每次评估都打一条等于每 0.5 秒刷一行，
        /// 9 个英雄会把 Console 彻底淹掉。首次那一条已经足够定位配置问题（例如射程填小了）。
        /// </summary>
        private readonly bool[] hasLoggedRejection = new bool[SlotCount];

        /// <summary>槽位数量。与 SkillSlot 的显式编号强绑定（枚举值直接用作数组下标）。</summary>
        private const int SlotCount = 4;

        /// <summary>决策节流间隔（只读），供调试视图核对。</summary>
        public float DecisionInterval => Mathf.Max(0.05f, decisionInterval);

        /// <summary>技能起手距离的宽松系数（只读）。</summary>
        public float CastRangeSlack => Mathf.Max(1f, castRangeSlack);

        /// <summary>技能决策是否已启用（只读），供调试与验收核对。</summary>
        public bool IsSkillDecisionEnabled => enableSkillDecisions;

        /// <summary>实体身份入口（只读）。</summary>
        public EntityBase Owner => entity;

        /// <summary>
        /// 只取实体引用。
        /// 不在这里读 EntityBase 的组件属性：同一 GameObject 上多个组件的 Awake 顺序不确定，
        /// 而且用代码动态创建单位时其他组件可能尚未 AddComponent，此时那些属性会是 null。
        /// 组件解析统一放到 Start / InitializeAI，与项目既定的"Awake 取引用、Start 做注入"约定一致。
        /// </summary>
        private void Awake()
        {
            if (entity == null)
            {
                entity = GetComponent<EntityBase>();
            }

            if (entity == null)
            {
                // 没有实体身份就无法读取阵营与组件，本组件完全无法工作，属于致命配置错误。
                Debug.LogError(
                    $"[HeroAIController] {name} 上找不到 EntityBase，技能决策已禁用。" +
                    "请确保 EntityBase 与 HeroAIController 挂在同一个 GameObject 上。", this);
                enabled = false;
            }
        }

        /// <summary>
        /// 生命周期兜底：预制体 / 场景实例上的英雄不需要任何外部代码调用，由这里自动完成初始化。
        /// 放在 Start 而不是 Awake：EntityBase 在它自己的 Start 里才完成配置注入
        /// （生命值、移速、索敌半径、攻击配置、技能槽），技能决策必须等这些就绪后再启用。
        /// </summary>
        private void Start()
        {
            if (hasInitialized)
            {
                // 外部（工具 / 自动化测试）已显式调用过 InitializeAI。
                return;
            }

            InitializeAI();
        }

        /// <summary>
        /// 初始化入口：解析依赖并做一次性校验。
        /// 调用时机要求：必须在 EntityBase.Initialize(...) 之后调用，
        /// 否则读到的技能槽与索敌半径都还是注入前的默认值。
        /// </summary>
        public void InitializeAI()
        {
            if (hasInitialized)
            {
                return;
            }

            if (entity == null)
            {
                entity = GetComponent<EntityBase>();
            }

            if (entity == null)
            {
                Debug.LogError($"[HeroAIController] {name} 缺少 EntityBase 引用，技能决策初始化失败。", this);
                enabled = false;
                return;
            }

            // 用 GetComponent 直接取，而不是读 entity.Skills / entity.Targeting 属性：
            // 那些属性返回的是 EntityBase 在【自己的 Awake】里缓存的引用，而同一物体上组件的 Start 顺序不确定，
            // EntityBase.Start 可能晚于本组件的 Start，届时属性里还可能是 null。
            skills = entity.GetComponent<SkillComponent>();
            targeting = entity.GetComponent<TargetingComponent>();
            mana = entity.GetComponent<ManaComponent>();
            health = entity.GetComponent<HealthComponent>();
            buff = entity.GetComponent<BuffComponent>();

            if (skills == null)
            {
                // 缺技能组件 = 这个 AI 英雄一个技能都放不出来，属于装配缺陷，必须报错而不是静默。
                ReportMissingSkillsOnce();
            }

            if (targeting == null)
            {
                // 缺索敌组件 = 技能没有目标可选（但仍可能放"以自身为中心"的技能），因此只报错不禁用。
                ReportMissingTargetingOnce();
            }

            // 运行期技能自证（阶段八实机修复新增）。
            //
            // 【为什么必须在运行期打这条日志 —— 这是被实机打回逼出来的】
            // 上一轮工具的收尾校验会读回"AI 英雄实例的 skillSlots"，并报告"36 个技能全部来自技能池"，
            // 但实机里 9 个 AI 依然在放盖伦的 QWER —— 因为覆盖发生在【运行期 Start】
            // （EntityBase.ApplyStats 用共享的 HeroStats 调 SkillComponent.Initialize），
            // 而编辑器期的读回校验只能看到"磁盘上写了什么"，看不到"运行时被谁改掉了"。
            // 因此把"这个 AI 英雄此刻手上到底是哪四个技能"直接打出来：
            // 它是**运行期**的、**每个英雄一条**的确定性证据，任何"注入失效 / 被覆盖"都会立刻暴露。
            LogResolvedSkills();

            hasInitialized = true;
        }

        /// <summary>
        /// 输出本 AI 英雄最终生效的四个技能槽（运行期证据）。
        ///
        /// 只在初始化时打一条，因此不存在刷屏问题（9 个 AI 英雄 = 9 行）。
        /// 槽位为空时用 Warning 点名——空的槽位意味着"那个键按下去没反应"，
        /// 而它在此前是完全静默的（SkillComponent 只在真正施法被拒时才告警）。
        /// </summary>
        private void LogResolvedSkills()
        {
            if (skills == null)
            {
                return;
            }

            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            int emptyCount = 0;

            for (int i = 0; i < SlotCount; i++)
            {
                if (i > 0)
                {
                    builder.Append(" / ");
                }

                SkillSlot slot = (SkillSlot)i;
                SkillData data = skills.GetSkillData(slot);

                if (data == null)
                {
                    emptyCount++;
                    builder.Append(slot).Append("=空");
                    continue;
                }

                builder.Append(slot).Append('=').Append(data.DisplayName);
            }

            if (emptyCount > 0)
            {
                Debug.LogWarning(
                    $"[HeroAIController] {name} 的技能槽不完整（{builder}）：有 {emptyCount} 个槽位是空的，" +
                    "对应的按键不会有任何反应。请重新执行「MOBA Demo/一键组装测试战场」检查技能池分配。", this);
                return;
            }

            Debug.Log($"[HeroAIController] {name} 技能槽已就绪：{builder}（按实例注入，与玩家专属 QWER 无关）", this);
        }

        /// <summary>
        /// 驱动技能决策。放在 Update 中每帧调用，但只有节流到点时才真正评估。
        /// </summary>
        private void Update()
        {
            if (!hasInitialized || !enableSkillDecisions)
            {
                return;
            }

            decisionTimer -= Time.deltaTime;
            if (decisionTimer > 0f)
            {
                return;
            }

            decisionTimer = DecisionInterval;

            EvaluateSkills();
        }

        /// <summary>
        /// 技能决策（阶段八第二步实装）。
        ///
        /// 执行顺序：
        ///   ① 自身状态闸门 —— 死亡 / 眩晕 / 沉默 / 前摇中一律不评估（TryCast 也会拦，
        ///      但在这里拦掉可以省下每次评估的目标筛选开销，也让日志只反映"真正尝试过"的施法）；
        ///   ② 两个 pass 扫描槽位（见下方"为什么分两 pass"），命中即施放并结束本次评估；
        ///   ③ 粗筛（CD / 蓝量 / 距离）只是为了少调一次 TryCast，最终判定权仍在 SkillComponent。
        ///
        /// 【为什么每次评估最多放一个技能】技能配置允许 castTime = 0（瞬发），
        /// 若循环里连续施放，AI 会在同一帧把两三个技能一起丢出去 —— 这既不像人，
        /// 也让"技能前摇锁住移动"这条既有机制形同虚设。一次一个，剩下的交给 0.5 秒后的下一轮。
        ///
        /// 【为什么分两个 pass 而不是按槽位顺序一把扫】
        /// 支援类（治疗 / 自身增益）必须先于攻击类评估。反例很具体：
        /// 某个 AI 英雄被分到 Q = 火球、W = 治疗术。若按槽位顺序扫，只要敌人进了射程，
        /// Q 就永远先成功施放并结束本轮评估，W 一辈子轮不到 —— 治疗技能形同虚设。
        /// 先支援后输出，才让"血少就奶、否则就打"这个直觉成立。
        /// </summary>
        private void EvaluateSkills()
        {
            if (skills == null)
            {
                return;
            }

            if (!CanDecideNow())
            {
                return;
            }

            // pass 1：支援类（友方目标，或"自身增益"——Self 且不生成范围场）。
            if (EvaluateSlots(IsSupportSkill))
            {
                return;
            }

            // pass 2：攻击类（其余全部：敌方指向性、AOE、以自身为中心的伤害场）。
            EvaluateSlots(data => !IsSupportSkill(data));
        }

        /// <summary>
        /// 是否处于"可以决定施法"的状态。
        /// 与 SkillComponent.TryCast 的第一段校验链逐条对齐（死亡 / 眩晕 / 沉默 / 前摇中），
        /// 但目的不同：那边是"拒绝施法"，这边是"干脆不去筛目标"，因此不算重复实现规则。
        /// </summary>
        private bool CanDecideNow()
        {
            if (health != null && health.IsDead)
            {
                return false;
            }

            if (buff != null && (buff.IsStunned || buff.IsSilenced))
            {
                return false;
            }

            return !skills.IsCasting;
        }

        /// <summary>
        /// 某个技能是否属于"支援类"：目标为友方，或是自身增益（Self 且不生成范围场）。
        /// 判据刻意与 SkillComponent.Release 的分派条件一致 —— 那边也是用 spawnZoneAtSelf
        /// 区分"自身增益"与"以自身为中心的范围场"（不能改用 areaDuration：它有非零默认值）。
        /// </summary>
        /// <param name="data">技能配置。</param>
        /// <returns>属于支援类返回 true。</returns>
        private static bool IsSupportSkill(SkillData data)
        {
            if (data.TargetsAlly)
            {
                return true;
            }

            return data.CastType == SkillCastType.Self && !data.SpawnZoneAtSelf;
        }

        /// <summary>
        /// 按槽位顺序扫描满足 <paramref name="filter"/> 的技能，施放第一个成功的那一个。
        /// </summary>
        /// <param name="filter">本轮要评估哪些技能。</param>
        /// <returns>本次确实施放了一个技能返回 true。</returns>
        private bool EvaluateSlots(System.Func<SkillData, bool> filter)
        {
            for (int i = 0; i < SlotCount; i++)
            {
                SkillSlot slot = (SkillSlot)i;

                SkillData data = skills.GetSkillData(slot);
                if (data == null || !filter(data))
                {
                    continue;
                }

                if (TryCastSlot(slot, data))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 尝试施放某个槽位：粗筛（CD / 蓝 / 距离）→ 按施法形态选目标 → 调 TryCast。
        /// </summary>
        /// <param name="slot">技能槽位。</param>
        /// <param name="data">技能配置。</param>
        /// <returns>确实进入了施法流程返回 true。</returns>
        private bool TryCastSlot(SkillSlot slot, SkillData data)
        {
            // ---- 粗筛一：冷却 ----
            // 真正的判定权仍在 TryCast（它会再查一次），这里只是为了不白做目标筛选。
            if (skills.GetCooldownRemaining(slot) > 0f)
            {
                return false;
            }

            // ---- 粗筛二：蓝量 ----
            if (data.ManaCost > 0f && mana != null && !mana.HasEnough(data.ManaCost))
            {
                return false;
            }

            // 起手距离 = 施法距离 × 宽松系数。宽松系数是为了兜住"目标正在移动"——
            // 按精确射程筛会把"刚好能打到的目标"全部漏掉，而 TryCast 本身有 0.25 米容差。
            float maxDistance = data.CastRange * CastRangeSlack;

            ITargetable target = null;
            Vector3 groundPoint = transform.position;

            switch (data.CastType)
            {
                case SkillCastType.Self:
                    // 自身增益：必须处在交战中才有意义（见 selfCastEngageRange 的说明）。
                    // 以自身为中心的伤害场（E 审判）属于攻击类，同样要求交战。
                    if (!HasEngagementTarget())
                    {
                        return false;
                    }

                    break;

                case SkillCastType.UnitTarget:
                    if (data.TargetsAlly)
                    {
                        // 友方治疗 / 护盾：取血量百分比最低的那个（施法者自己也算候选）。
                        target = targeting != null
                            ? targeting.FindLowestHealthAllyInRange(allyHealHealthThreshold)
                            : null;
                    }
                    else
                    {
                        target = ResolveEnemyTarget(maxDistance);
                    }

                    if (target == null)
                    {
                        return false;
                    }

                    groundPoint = target.TargetTransform.position;
                    break;

                case SkillCastType.GroundPoint:
                    // 落点选在当前敌方目标身上。没有敌人就不放 —— 空放 AOE 只会白白吃冷却与蓝耗，
                    // 对 AI 的观感毫无帮助（这一点与玩家的"预判落点"不同，AI 不做预判）。
                    ITargetable zoneTarget = ResolveEnemyTarget(maxDistance);
                    if (zoneTarget == null)
                    {
                        return false;
                    }

                    groundPoint = zoneTarget.TargetTransform.position;
                    break;
            }

            if (skills.TryCast(slot, groundPoint, target, out string failReason))
            {
                if (logSkillDecisions)
                {
                    Debug.Log(
                        $"[HeroAIController] {name} 施放 {data.DisplayName}（{slot}）" +
                        $"｜形态 {data.CastType}｜目标 {DescribeTarget(target)}", this);
                }

                return true;
            }

            // 走到这里说明"粗筛通过但 TryCast 拒绝"（距离 / 目标合法性 / 状态在两次判断之间变化）。
            // 每个槽位只报一次，避免每 0.5 秒刷一行。
            int index = (int)slot;
            if (logSkillDecisions && !hasLoggedRejection[index])
            {
                hasLoggedRejection[index] = true;
                Debug.Log(
                    $"[HeroAIController] {name} 尝试施放 {data.DisplayName}（{slot}）被拒绝：{failReason}" +
                    "（同一槽位的原因只报一次）", this);
            }

            return false;
        }

        /// <summary>
        /// 选一个敌方目标：优先用 FSM 已锁定的当前目标（那是全项目"当前目标"的唯一存放处），
        /// 它在射程外时才退化为"在索敌半径内找一个最近的"。
        ///
        /// 【为什么优先当前目标】FSM 的当前目标就是 AI 正在追击/攻击的那一个，
        /// 技能打在同一个目标上才符合"集中火力"的直觉；若每次都取最近，
        /// 会出现"平A 打 A、技能打 B"的分裂行为。
        /// </summary>
        /// <param name="maxDistance">可接受的最大距离（含宽松系数）。</param>
        /// <returns>可用目标；没有则返回 null。</returns>
        private ITargetable ResolveEnemyTarget(float maxDistance)
        {
            if (targeting == null)
            {
                return null;
            }

            // 先校验并清理失效目标：接口引用在对象被销毁后不会变 null，直接读会抛异常。
            targeting.ClearInvalidTarget();

            ITargetable current = targeting.CurrentTarget;
            if (IsUsableTarget(current, maxDistance))
            {
                return current;
            }

            ITargetable nearest = targeting.FindNearestEnemy();
            return IsUsableTarget(nearest, maxDistance) ? nearest : null;
        }

        /// <summary>
        /// 目标是否可用且在给定距离内。
        /// 三项检查与 SkillComponent 的校验链保持同一口径（存活/可选中 + 未销毁 + Transform 有效），
        /// 但【不重复实现敌我规则】：目标来源本身就已经是"索敌组件筛过的敌方"。
        /// </summary>
        /// <param name="target">待检查目标。</param>
        /// <param name="maxDistance">可接受的最大距离。</param>
        /// <returns>可用返回 true。</returns>
        private bool IsUsableTarget(ITargetable target, float maxDistance)
        {
            if (target == null)
            {
                return false;
            }

            if (target is UnityEngine.Object unityObject && unityObject == null)
            {
                return false;
            }

            if (!target.IsValidTarget)
            {
                return false;
            }

            Transform targetTransform = target.TargetTransform;
            if (targetTransform == null)
            {
                return false;
            }

            return (targetTransform.position - transform.position).sqrMagnitude <= maxDistance * maxDistance;
        }

        /// <summary>
        /// 当前是否处在"值得开增益"的交战中：已锁定目标，且它与本单位的距离在
        /// <see cref="selfCastEngageRange"/> 之内。
        ///
        /// 【为什么读 CurrentTarget 而不是自己去索敌】目标的唯一存放处是 TargetingComponent，
        /// 而 FSM 的 MoveState 在任何敌人进入【索敌半径】时就会写入它（即使因为"不值得脱线"而没去追）。
        /// 因此 CurrentTarget 天然表达了"周围有敌人"，本类不需要（也不应该）再写一份感知逻辑。
        /// </summary>
        private bool HasEngagementTarget()
        {
            if (targeting == null)
            {
                return false;
            }

            targeting.ClearInvalidTarget();

            ITargetable current = targeting.CurrentTarget;
            if (current == null)
            {
                return false;
            }

            Transform targetTransform = current.TargetTransform;
            if (targetTransform == null)
            {
                return false;
            }

            float range = Mathf.Max(1f, selfCastEngageRange);
            return (targetTransform.position - transform.position).sqrMagnitude <= range * range;
        }

        /// <summary>生成目标的简短描述，仅用于日志。</summary>
        private static string DescribeTarget(ITargetable target)
        {
            if (target == null)
            {
                return "自身 / 无目标";
            }

            if (target is Component component)
            {
                return $"{component.name}({target.Team})";
            }

            return $"ITargetable({target.Team})";
        }

        /// <summary>就"缺少 SkillComponent"报错一次。用一次性标记防刷屏。</summary>
        private void ReportMissingSkillsOnce()
        {
            if (hasReportedMissingSkills)
            {
                return;
            }

            hasReportedMissingSkills = true;

            Debug.LogError(
                $"[HeroAIController] {name} 上找不到 SkillComponent，该 AI 英雄无法释放任何技能。" +
                "请检查英雄预制体（一键组装工具会在其上自动挂载并注入技能槽）。", this);
        }

        /// <summary>就"缺少 TargetingComponent"报错一次。用一次性标记防刷屏。</summary>
        private void ReportMissingTargetingOnce()
        {
            if (hasReportedMissingTargeting)
            {
                return;
            }

            hasReportedMissingTargeting = true;

            Debug.LogError(
                $"[HeroAIController] {name} 上找不到 TargetingComponent，技能无法选取目标" +
                "（仅「以自身为中心」的技能仍可释放）。请检查英雄预制体配置。", this);
        }
    }
}
