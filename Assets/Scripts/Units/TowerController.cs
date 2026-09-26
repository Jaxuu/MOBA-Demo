using System.Collections.Generic;
using UnityEngine;
using MOBA.Components;
using MOBA.Core;
using MOBA.Data;
using MOBA.Skills;

namespace MOBA.Units
{
    /// <summary>
    /// 防御塔控制器：固定建筑，不移动，按【仇恨优先级】锁定一个目标并以【弹道】攻击。
    ///
    /// 职责边界（README 2.1.3 / 3.3，阶段八落地 6B 塔仇恨）：
    /// 1. 只做四件事——「按固定周期索敌」「按优先级选目标」「把目标交给弹道」「死亡时脱离战斗」；
    /// 2. 不驱动移动（防御塔按设计没有 MovementComponent，见 EntityBase.RequiresMovement 的说明），
    ///    因此【不要】给防御塔挂 EntityAIController：FSM 的 MoveState 会在缺少移动组件时报错；
    /// 3. 不实现自己的目标合法性判断——目标是否合法（存活 / 可选中 / 敌方）一律由
    ///    TargetingComponent 负责，本类只负责"什么时候问它"与"按什么优先级选"；
    /// 4. 交战半径 = CombatComponent.AttackRange（AttackData.AttackRange），【不使用】独立的索敌半径。
    ///    固定建筑不能追击，「能锁定的范围」必须等于「能打到的范围」，否则会出现"锁了却打不到"的目标抖动。
    ///    配套：EntityBase.ApplyStats 对防御塔会把 TargetingComponent 的搜索半径也注入成攻击距离。
    ///
    /// 【仇恨优先级（阶段八，README 2.1.3 三条规则）】
    ///   规则 1：默认优先级「小兵 &gt; 英雄 &gt; 其它敌方单位」，同档内取最近者。
    ///           这条刻意不是"取最近的敌人"——若不把小兵排在英雄前面，塔会一直盯着最靠前的英雄打，
    ///           小兵反而安全推塔，与 MOBA 的塔防语义相反。
    ///   规则 2：敌方英雄在交战半径内【攻击己方英雄】时，仇恨立刻转移到该英雄（"英雄抗塔"）。
    ///           实现方式：本类订阅【己方英雄】的 HealthComponent.OnDamaged，
    ///           从事件里拿到伤害来源，确认它是"半径内的敌方英雄"后置为强制仇恨目标。
    ///   规则 3：强制仇恨目标死亡 / 离开交战半径 / 变为非法目标 → 立即回到规则 1 重新选择
    ///           （把索敌计时器清零，因此重选是当帧发生的，不会白等一个节流周期）。
    ///
    /// 【为什么"小兵优先"与"英雄抗塔"不会互相打架】强制仇恨目标一旦置位就【跳过】默认优先级选择，
    /// 直到它自己失效为止；这正是"塔被激怒后死盯该英雄"的语义。
    ///
    /// 【节流不变量】规则 1 的目标重评估只在 detectionInterval（≤ 0.25 秒）到点时执行一次，
    /// 禁止每帧重算（每次重算都是一次物理范围查询）。规则 2 是事件驱动，不受节流限制——
    /// "立刻转移仇恨"按需求必须是立即的。规则 3 的失效校验只是属性比较与一次平方距离运算，
    /// 不含物理查询，因此可以每帧执行。
    ///
    /// 【同一时刻单目标】本类只维护 TargetingComponent 里的一个 CurrentTarget，
    /// 每次攻击也只发一发弹道，结构上不存在"同时打两个"的可能。
    ///
    /// 与 EntityAIController 的关系：两者是互斥的替代实现。可移动单位用 FSM 编排
    /// Idle/Move/Chase/Attack/Dead 五态，固定建筑没有走位需求，用本类的直线逻辑即可，不必为它引入状态机。
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(EntityBase))]
    public class TowerController : MonoBehaviour
    {
        [Header("索敌节奏")]
        [Tooltip("索敌检测间隔（秒）。防御塔的目标重评估按该周期执行（需求要求 ≤ 0.25 秒），" +
                 "期间不做任何物理范围查询。填 0 表示每帧检测（仅调试用）。")]
        [Min(0f)]
        [SerializeField] private float detectionInterval = 0.25f;

        [Header("仇恨优先级（阶段八）")]
        [Tooltip("开启「英雄抗塔」：敌方英雄在交战半径内攻击己方英雄时，塔立刻把仇恨转移到该英雄。")]
        [SerializeField] private bool enableHeroAggroTransfer = true;

        [Header("普攻弹道（阶段八）")]
        [Tooltip("塔普攻弹道的飞行速度（米/秒）。要有可见飞行时间，但不能慢到让塔显得迟钝。" +
                 "取 14 与技能火球同速：6.5 米射程下约 0.46 秒到位，既看得见弹道又不拖沓。")]
        [Min(0.1f)]
        [SerializeField] private float projectileSpeed = 14f;

        [Tooltip("塔普攻弹道的命中半径（米）。")]
        [Min(0.05f)]
        [SerializeField] private float projectileRadius = 0.4f;

        [Tooltip("塔普攻弹道的最大飞行距离余量（米）：实际最大飞行距离 = 交战半径 + 该余量。" +
                 "用途是兜住「目标不可达时弹道永生」——余量太小会在目标刚好贴边时把弹道提前回收。")]
        [Min(0f)]
        [SerializeField] private float projectileMaxTravelMargin = 6f;

        [Header("死亡处理")]
        [Tooltip("死亡后是否销毁自身 GameObject。默认 false：只停止攻击逻辑并退出索敌，" +
                 "保留物体便于观察死亡表现与排查问题；确认不需要后再改为 true。")]
        [SerializeField] private bool destroyOnDeath = false;

        [Header("调试")]
        [Tooltip("在 Console 输出防御塔被摧毁的记录（只输出一次）。")]
        [SerializeField] private bool logDeathEvents = true;

        [Tooltip("在 Console 输出仇恨转移记录（英雄抗塔触发时输出一次）。排查仇恨优先级时打开。")]
        [SerializeField] private bool logAggroTransfers = true;

        /// <summary>实体身份入口。由 [RequireComponent] 保证存在，但仍做判空以给出明确报错。</summary>
        private EntityBase entity;

        /// <summary>生命组件。订阅 OnDied 用，必须存在，否则防御塔死亡后不会停止攻击。</summary>
        private HealthComponent health;

        /// <summary>索敌组件。防御塔的"眼睛"，必须存在。</summary>
        private TargetingComponent targeting;

        /// <summary>战斗组件。防御塔的"手"，必须存在（攻击距离与冷却的唯一来源）。</summary>
        private CombatComponent combat;

        /// <summary>
        /// 弹道生成器。允许缺失（会走降级路径：立即结算伤害），但必须告警一次——
        /// 缺它就意味着"塔的普攻不再是弹道"，属于与本阶段需求直接冲突的配置缺陷。
        /// </summary>
        private ProjectileSpawner projectileSpawner;

        /// <summary>索敌计时器（秒）。归零时执行一次目标重评估并重置，实现"固定周期"而非每帧。</summary>
        private float detectionTimer;

        /// <summary>
        /// 依赖是否已解析完成。
        /// 单独用一个标记而不是只依赖 enabled：若外部在运行期把本组件重新 enabled（Inspector 勾选、脚本赋值），
        /// Update 会再次执行，此时 targeting / combat 可能仍为 null。用标记兜底可彻底避免空引用。
        /// </summary>
        private bool hasInitialized;

        /// <summary>死亡收尾是否已执行。用于保证 OnDied 回调与"订阅时已死亡"的补偿路径不会重复执行。</summary>
        private bool hasHandledDeath;

        /// <summary>
        /// 强制仇恨目标（"英雄抗塔"的产物）。为 null 表示走默认优先级。
        /// 一旦置位就跳过默认优先级选择，直到它死亡 / 离开交战半径 / 变为非法目标。
        /// </summary>
        private ITargetable forcedAggroTarget;

        /// <summary>
        /// 优先级筛选用的候选缓冲。跨帧复用（每次重评估前 Clear），稳态零分配。
        /// 用 List 而不是数组：候选数量随战场密度变化，List 的扩容只发生在极少数峰值帧。
        /// </summary>
        private readonly List<ITargetable> candidateBuffer = new List<ITargetable>(32);

        /// <summary>
        /// 已订阅受伤事件的【己方英雄】生命组件清单。
        /// 必须记录订阅对象：退订时重新 GetComponent 可能拿到另一个实例（对象被替换），留下悬空订阅。
        /// </summary>
        private readonly List<HealthComponent> subscribedAlliedHeroes = new List<HealthComponent>();

        /// <summary>是否已就"缺少 ProjectileSpawner"告警过，防止每次攻击都刷日志。</summary>
        private bool hasReportedMissingSpawner;

        /// <summary>是否已就"弹道发不出去、已降级为即时结算"告警过。</summary>
        private bool hasReportedProjectileFallback;

        /// <summary>是否已被摧毁（只读），供调试视图与 MatchController 读取。</summary>
        public bool IsDestroyed => hasHandledDeath;

        /// <summary>当前强制仇恨目标（只读），供调试与验收核对"仇恨是否真的转移了"。</summary>
        public ITargetable ForcedAggroTarget => forcedAggroTarget;

        /// <summary>
        /// 当前交战半径（只读）。
        ///
        /// 取值来源：CombatComponent.AttackRange（即 AttackData.AttackRange）。
        /// 防御塔【不再】使用独立的索敌半径——它不能移动，「能锁定的范围」必须等于「能打到的范围」。
        /// 若两者不一致（常见配置是索敌半径大于攻击距离），塔会锁住一个刚好打不到的目标，
        /// 下一个检测周期又把它清掉，形成目标抖动，而且会「占着」目标位，
        /// 导致射程内真正能打到的敌人迟迟拿不到仇恨。
        ///
        /// 配套约定：EntityBase.ApplyStats 会按单位类型把 TargetingComponent 的索敌半径
        /// 也注入成攻击距离（防御塔分支），因此「搜索范围」与「命中范围」由同一份数据驱动。
        /// </summary>
        public float EngagementRange => combat != null ? combat.AttackRange : 0f;

        /// <summary>
        /// 只取实体身份。
        /// 不在这里读 entity.Health / Targeting / Combat：同一 GameObject 上多个组件的 Awake 顺序不确定，
        /// 而且用代码动态创建单位时其他组件可能尚未 AddComponent，此时 EntityBase 缓存到的引用会是 null。
        /// 组件解析统一放到 Start（见 ResolveComponents），与项目既定的"Awake 取引用、Start 做注入"约定一致。
        /// </summary>
        private void Awake()
        {
            entity = GetComponent<EntityBase>();
        }

        /// <summary>
        /// 解析组件依赖、订阅死亡事件与"己方英雄受伤"事件。
        /// 放在 Start 的原因同上：Unity 保证所有 Awake 先于任何 Start 执行，此时所有组件必定已挂载完成。
        ///
        /// 【为什么英雄订阅放在 Start 而不是 OnEnable】订阅需要遍历 EntityRegistry 找己方英雄，
        /// 而 Registry 的登记发生在各实体的 OnEnable；场景加载时全部 OnEnable 都先于任何 Start，
        /// 因此 Start 时刻的 Registry 快照是完整的。放在 OnEnable 反而会漏掉"比自己先登记的英雄"。
        /// </summary>
        private void Start()
        {
            ResolveComponents();

            if (hasInitialized)
            {
                SubscribeAlliedHeroes();
            }
        }

        /// <summary>
        /// 组件解析 + 依赖校验 + 事件订阅。
        /// 任何一项必需组件缺失都会让防御塔"看起来存在但完全不工作"，因此用 LogError 明确报出并禁用自身，
        /// 而不是等到运行中抛空引用异常（那样 Console 里会刷出成片的 NullReferenceException，反而更难定位）。
        /// </summary>
        private void ResolveComponents()
        {
            if (entity == null)
            {
                Debug.LogError(
                    $"[TowerController] {name} 上找不到 EntityBase，无法判断阵营与读写组件，防御塔功能不可用。" +
                    "请确保 EntityBase 与 TowerController 挂在同一个 GameObject 上。", this);
                enabled = false;
                return;
            }

            // 刻意用 GetComponent 直接取，而不是读 entity.Health / entity.Targeting / entity.Combat 属性：
            // 那些属性返回的是 EntityBase 在【自己的 Awake】里缓存的引用，而同一物体上组件的 Start 顺序同样不确定——
            // EntityBase.Start 可能晚于本组件的 Start，届时属性里还可能是 null（详见项目记忆中"动态创建单位的硬性顺序"）。
            health = entity.GetComponent<HealthComponent>();
            targeting = entity.GetComponent<TargetingComponent>();
            combat = entity.GetComponent<CombatComponent>();

            // 弹道生成器是"可选但强烈建议"的依赖：缺它不会让塔瘫痪（会降级为即时结算），
            // 因此不参与下面的必需依赖校验，只在首次攻击时告警一次。
            projectileSpawner = entity.GetComponent<ProjectileSpawner>();

            if (health == null)
            {
                Debug.LogError(
                    $"[TowerController] {name} 未挂载 HealthComponent，防御塔既不会受伤也不会触发死亡处理。" +
                    "请检查预制体配置。", this);
                enabled = false;
                return;
            }

            if (targeting == null)
            {
                Debug.LogError(
                    $"[TowerController] {name} 未挂载 TargetingComponent，防御塔无法索敌。" +
                    "请检查预制体配置。", this);
                enabled = false;
                return;
            }

            if (combat == null)
            {
                Debug.LogError(
                    $"[TowerController] {name} 未挂载 CombatComponent，防御塔无法攻击。" +
                    "请检查预制体配置。", this);
                enabled = false;
                return;
            }

            health.OnDied += HandleDied;

            hasInitialized = true;

            // 订阅时可能已经死亡（用代码创建后立刻被击杀、或场景里预置了"已摧毁的塔"）：
            // 那种情况下 OnDied 早已广播过，事件订阅不会补发，必须主动补一次死亡收尾，否则塔会继续攻击。
            if (health.IsDead)
            {
                HandleDied();
            }
        }

        /// <summary>
        /// 每帧驱动。分支顺序不可调换：
        /// 强制仇恨（英雄抗塔）→ 默认优先级的即时校验 → 距离校验 → 周期重评估 → 攻击。
        /// 先剔除旧目标再索敌，可以避免"刚被清掉的目标又被重新锁定"造成的目标抖动。
        /// </summary>
        private void Update()
        {
            if (!hasInitialized || hasHandledDeath)
            {
                return;
            }

            // ---------- 0. 强制仇恨：英雄抗塔 ----------
            // 优先级最高：置位期间完全跳过默认优先级选择（规则 2 覆盖规则 1）。
            if (forcedAggroTarget != null)
            {
                if (ValidateForcedAggro())
                {
                    targeting.SetTarget(forcedAggroTarget);
                    FireProjectile(forcedAggroTarget);
                    return;
                }

                // 强制目标失效（死亡 / 离场 / 非法）：清掉它并把索敌计时器清零，
                // 让规则 1 的重选【当帧】发生，而不是白等一个节流周期（需求：≤ 0.25 秒内回到小兵）。
                forcedAggroTarget = null;
                detectionTimer = 0f;
            }

            // ---------- 1. 即时校验：目标已死亡 / 被销毁 / 阵营变化 ----------
            // ClearInvalidTarget 是全项目唯一的目标合法性判断入口（内部复用 IsEnemy + IsValidTarget），
            // 本类不得自己重写这套判断，否则迟早出现"索敌认为无效、攻击认为有效"的口径分叉。
            // 该方法内部没有任何物理查询，只是一次属性检查，因此可以每帧执行。
            targeting.ClearInvalidTarget();

            ITargetable currentTarget = targeting.CurrentTarget;

            // ---------- 2. 距离校验：目标走出交战半径 ----------
            // 固定建筑无法追击，目标一旦离开交战半径就必须立刻放弃，否则会一直"占着"目标位、
            // 导致射程内新出现的敌人无法被锁定。同样只是一次平方距离比较，可以每帧执行。
            if (currentTarget != null && IsBeyondEngagementRange(currentTarget))
            {
                targeting.ClearTarget();
                currentTarget = null;
            }

            // ---------- 3. 周期性目标重评估（按仇恨优先级） ----------
            // 只有这一步会触发物理范围查询，所以必须节流，不能每帧无条件调用。
            detectionTimer -= Time.deltaTime;
            if (detectionTimer <= 0f)
            {
                detectionTimer = Mathf.Max(0.02f, detectionInterval);
                AcquireTarget();
                currentTarget = targeting.CurrentTarget;
            }

            // ---------- 4. 攻击 ----------
            // 目标在交战半径内、但可能仍在攻击距离外：TryCommitAttack 内部会做距离与冷却判定，
            // 不满足条件时返回 false 且不产生任何副作用，因此这里无需重复判断距离。
            if (currentTarget != null)
            {
                FireProjectile(currentTarget);
            }
        }

        /// <summary>
        /// 校验强制仇恨目标是否仍然有效：对象未销毁 → 仍是合法目标 → 仍是敌人 → 仍在交战半径内。
        /// 四项全部通过才算有效。任一不通过都返回 false，由 Update 负责清除并立即重选。
        /// </summary>
        private bool ValidateForcedAggro()
        {
            // 接口引用在 GameObject 被销毁后不会变成 null，必须先做存活检查再访问其属性。
            if (forcedAggroTarget is UnityEngine.Object unityObject && unityObject == null)
            {
                return false;
            }

            if (!forcedAggroTarget.IsValidTarget)
            {
                return false;
            }

            if (targeting == null || !targeting.IsEnemy(forcedAggroTarget))
            {
                return false;
            }

            return !IsBeyondEngagementRange(forcedAggroTarget);
        }

        /// <summary>
        /// 执行一次目标重评估并更新当前目标（规则 1：小兵 &gt; 英雄 &gt; 其它，同档取最近）。
        ///
        /// 用 CollectEnemiesInRange 一次物理查询拿到全部候选，再在本类内按优先级分档比较——
        /// 若改成"按类型各查一次"，6 座塔每 0.25 秒就要多做一倍的物理查询。
        /// </summary>
        private void AcquireTarget()
        {
            candidateBuffer.Clear();
            int count = targeting.CollectEnemiesInRange(candidateBuffer);

            if (count == 0)
            {
                // 范围内已无敌方（含"原目标已走出半径"的情况），清空目标。
                // 目标本就为 null 时 ClearTarget 是空操作，不会产生多余日志。
                targeting.ClearTarget();
                return;
            }

            ITargetable best = PickByPriority();

            if (best == null)
            {
                targeting.ClearTarget();
                return;
            }

            // SetTarget 内部会再走一遍完整校验；传入与当前相同的目标时直接返回，不会重复打印日志。
            targeting.SetTarget(best);
        }

        /// <summary>
        /// 按仇恨优先级从候选缓冲里挑一个目标：小兵 &gt; 英雄 &gt; 其它敌方单位，同档内取最近者。
        ///
        /// 分档而不是"直接取最近"是刻意的：塔若总打最近的敌人，先冲上来的英雄会一直吸引火力，
        /// 小兵反而可以安全推塔，与 MOBA 的塔防语义相反。
        /// </summary>
        /// <returns>选中的目标；候选全部失效时返回 null。</returns>
        private ITargetable PickByPriority()
        {
            ITargetable nearestMinion = null;
            ITargetable nearestHero = null;
            ITargetable nearestOther = null;

            float minionSqrDistance = float.MaxValue;
            float heroSqrDistance = float.MaxValue;
            float otherSqrDistance = float.MaxValue;

            Vector3 origin = transform.position;

            for (int i = 0; i < candidateBuffer.Count; i++)
            {
                ITargetable candidate = candidateBuffer[i];

                // 候选已在 CollectEnemiesInRange 里校验过 Transform 非空，这里仍判一次：
                // 对象可能在同一次 Update 的两次访问之间被销毁（Unity 的 == 能识别这种情况）。
                Transform candidateTransform = candidate.TargetTransform;
                if (candidateTransform == null)
                {
                    continue;
                }

                float sqrDistance = (candidateTransform.position - origin).sqrMagnitude;

                // 非 EntityBase 的 ITargetable 实现（目前只有测试桩）归入"其它"档，
                // 因此借用 Base 这一档位，而不是默认按小兵处理（那会让测试桩莫名获得最高优先级）。
                EntityType type = candidate is EntityBase candidateEntity
                    ? candidateEntity.EntityType
                    : EntityType.Base;

                switch (type)
                {
                    case EntityType.Minion:
                        if (sqrDistance < minionSqrDistance)
                        {
                            minionSqrDistance = sqrDistance;
                            nearestMinion = candidate;
                        }
                        break;

                    case EntityType.Hero:
                        if (sqrDistance < heroSqrDistance)
                        {
                            heroSqrDistance = sqrDistance;
                            nearestHero = candidate;
                        }
                        break;

                    default:
                        if (sqrDistance < otherSqrDistance)
                        {
                            otherSqrDistance = sqrDistance;
                            nearestOther = candidate;
                        }
                        break;
                }
            }

            if (nearestMinion != null)
            {
                return nearestMinion;
            }

            if (nearestHero != null)
            {
                return nearestHero;
            }

            return nearestOther;
        }

        /// <summary>
        /// 判断目标是否已超出交战半径。
        /// 交战半径直接取 CombatComponent.AttackRange（即 AttackData.AttackRange）——
        /// 对不能移动的固定建筑来说，「看得见」必须等于「打得到」，两者用同一个值才不会有口径分叉。
        /// 距离用平方比较，省掉开方。
        /// </summary>
        private bool IsBeyondEngagementRange(ITargetable target)
        {
            Transform targetTransform = target.TargetTransform;
            if (targetTransform == null)
            {
                // Transform 取不到说明目标对象状态异常，按"超出范围"处理，交给后续流程清空。
                return true;
            }

            // combat 已在 ResolveComponents 中校验非空（否则本组件会被禁用），这里再兜一层，
            // 避免运行期组件被移除时抛出空引用。
            if (combat == null)
            {
                return true;
            }

            float range = combat.AttackRange;
            return (targetTransform.position - transform.position).sqrMagnitude > range * range;
        }

        #region 普攻弹道（阶段八）

        /// <summary>
        /// 发起一次普攻：提交攻击（消耗冷却）→ 发射弹道 → 命中时由弹道结算伤害。
        ///
        /// 【为什么先 TryCommitAttack 再生成弹道】攻击节奏（冷却）与"目标能不能打"的裁决
        /// 必须留在 CombatComponent 这一处；塔只负责"按节奏把这一发送出去"。
        /// 若塔自己写冷却，攻击节奏就有了两个写入方，迟早出现"塔的攻速与 AttackData 配置对不上"。
        /// </summary>
        /// <param name="target">攻击目标。</param>
        private void FireProjectile(ITargetable target)
        {
            if (!combat.TryCommitAttack(target))
            {
                return;
            }

            float damage = combat.AttackData != null ? combat.AttackData.Damage : 0f;

            if (projectileSpawner != null)
            {
                // 最大飞行距离 = 交战半径 + 余量：弹道至少要能飞到交战半径的边界，
                // 否则"目标刚好贴边"时会被提前回收，表现为"塔锁了却打不到"。
                float maxTravel = EngagementRange + Mathf.Max(0f, projectileMaxTravelMargin);

                Projectile projectile = projectileSpawner.SpawnAttack(
                    entity, target, damage, projectileSpeed, projectileRadius, maxTravel);

                if (projectile != null)
                {
                    return;
                }
            }
            else
            {
                ReportMissingSpawnerOnce();
            }

            // 降级路径：弹道发不出去（未挂 ProjectileSpawner / 预制体缺失且关闭了兜底）。
            // 这里立刻结算伤害而不是让这一发空放，保证塔在最差配置下仍是有威胁的建筑；
            // 代价是失去"命中时结算"的观感，因此必须显式告警——它属于配置缺陷，不该被静默接受。
            ReportProjectileFallbackOnce();

            if (damage > 0f)
            {
                SkillEffectResolver.ApplyDamage(target, damage, entity);
            }
        }

        #endregion

        #region 仇恨转移（英雄抗塔）

        /// <summary>
        /// 订阅【己方英雄】的受伤事件（规则 2 的数据来源）。
        ///
        /// 【为什么只订阅英雄、不订阅小兵】需求把转移条件明确限定为"敌方英雄在塔下攻击【己方英雄】"。
        /// 订阅全部己方单位会让塔被"任何英雄打任何小兵"激怒，与需求不符；
        /// 而且单位数量随兵线持续增长，订阅成本与退订复杂度都会失控。
        ///
        /// 【为什么在 Start 里做】见 Start 的注释：场景加载时所有实体的 OnEnable 都先于任何 Start，
        /// 因此这里的 Registry 快照是完整的。本阶段 10 个英雄全部是场景预置对象，不存在"开局后新出英雄"。
        /// </summary>
        private void SubscribeAlliedHeroes()
        {
            EntityBase[] snapshot = EntityRegistry.Snapshot();

            for (int i = 0; i < snapshot.Length; i++)
            {
                EntityBase candidate = snapshot[i];

                // 快照里的对象可能已被销毁；Unity 重载的 == 能识别这种情况。
                if (candidate == null || candidate == entity)
                {
                    continue;
                }

                if (candidate.Team != entity.Team || candidate.EntityType != EntityType.Hero)
                {
                    continue;
                }

                HealthComponent candidateHealth = candidate.GetComponent<HealthComponent>();
                if (candidateHealth == null)
                {
                    continue;
                }

                // 先退订再订阅：本方法理论上只执行一次，但保持与 EntityAIController 同一写法，
                // 重复调用时不会造成同一次受伤触发多遍处理。
                candidateHealth.OnDamaged -= HandleAlliedHeroDamaged;
                candidateHealth.OnDamaged += HandleAlliedHeroDamaged;

                subscribedAlliedHeroes.Add(candidateHealth);
            }
        }

        /// <summary>退订全部己方英雄的受伤事件。幂等，可被 OnDisable 与 OnDestroy 重复调用。</summary>
        private void UnsubscribeAlliedHeroes()
        {
            for (int i = 0; i < subscribedAlliedHeroes.Count; i++)
            {
                HealthComponent subscribed = subscribedAlliedHeroes[i];
                if (subscribed == null)
                {
                    continue;
                }

                subscribed.OnDamaged -= HandleAlliedHeroDamaged;
            }

            subscribedAlliedHeroes.Clear();
        }

        /// <summary>
        /// 己方英雄受伤回调：若伤害来源是【交战半径内的敌方英雄】，立刻把仇恨转移到它。
        ///
        /// 参数顺序遵循 HealthComponent.OnDamaged 的契约：
        /// （受击者, 实际扣减生命值, 被护盾吸收的伤害, 伤害来源）。
        /// 这里刻意不看伤害数值——"被护盾全吸收"同样是一次攻击，同样应当激怒塔。
        /// </summary>
        /// <param name="victim">受击的己方英雄。</param>
        /// <param name="healthDamage">本次实际扣减的生命值。</param>
        /// <param name="shieldAbsorbed">本次被护盾吸收的伤害。</param>
        /// <param name="source">伤害来源（伤害归属方），允许为 null。</param>
        private void HandleAlliedHeroDamaged(
            HealthComponent victim, float healthDamage, float shieldAbsorbed, EntityBase source)
        {
            if (!enableHeroAggroTransfer || hasHandledDeath || source == null)
            {
                return;
            }

            if (entity == null || entity.Team == TeamType.Neutral)
            {
                return;
            }

            // 规则 2 的三个必要条件：来源是敌方 → 是英雄 → 在本塔的交战半径内。
            if (source.Team == entity.Team || source.Team == TeamType.Neutral)
            {
                return;
            }

            if (source.EntityType != EntityType.Hero)
            {
                return;
            }

            if (IsBeyondEngagementRange(source))
            {
                return;
            }

            // 已经是同一个目标就不重复处理（避免每一下普攻都打一条日志）。
            if (ReferenceEquals(forcedAggroTarget, source))
            {
                return;
            }

            forcedAggroTarget = source;

            if (logAggroTransfers)
            {
                // 受击者是 HealthComponent（OnDamaged 事件的发送者），它不实现 ITargetable，
                // 因此这里直接用对象名，而不是复用只接受 ITargetable 的 DescribeTarget ——
                // 后者会报 CS1503（参数类型不匹配），是编译期就能拦下的错误。
                string victimName = victim != null ? victim.name : "未知单位";

                Debug.Log(
                    $"[TowerController] {name} 触发英雄抗塔：{victimName} 被 {DescribeTarget(source)} 攻击，" +
                    "仇恨已立即转移（跳过默认的小兵优先级）。", this);
            }
        }

        #endregion

        /// <summary>
        /// 死亡收尾：清空目标与强制仇恨、退订事件、停用逻辑 → 可选销毁。
        /// 全部动作都在本方法内一次完成（而不是放到 Update 里轮询 hasHandledDeath），
        /// 因为死亡是单向终态，事件回调是唯一且确定的触发点。
        /// </summary>
        private void HandleDied()
        {
            if (hasHandledDeath)
            {
                // OnDied 本身已保证只广播一次，这里是给"订阅时已死亡"的补偿路径兜底，防止两处都触发。
                return;
            }

            hasHandledDeath = true;

            // 1. 清空目标与强制仇恨，并停止每帧逻辑（禁用组件比销毁物体更轻，也便于事后排查）。
            forcedAggroTarget = null;

            if (targeting != null)
            {
                targeting.ClearTarget();
            }

            // 2. 退订己方英雄的受伤事件：塔已死，继续接收只会做无用功（方法入口虽有 hasHandledDeath 闸门，
            //    但让委托链一直挂着已禁用组件本身就不干净）。
            UnsubscribeAlliedHeroes();

            enabled = false;

            // 3. 退订死亡事件：防御塔只死亡一次，留着订阅没有意义，还会让 HealthComponent 的委托链白占引用。
            //    注意这里【不】调用 entity.SetSelectable(false) 与【不】禁用碰撞体：
            //    - 选中：IsValidTarget 已经用 Health.IsDead 兜住了"已死亡的塔不再是合法目标"，
            //      再置一次属于功能冗余；防御塔不跑 FSM，没有 DeadState 那条路径，因此两者不构成口径分叉。
            //    - 碰撞体：防御塔是场景里的固定建筑，保留其物理存在（供点击/射线检测）更符合直觉；
            //      而移动单位走 DeadState，那里会禁用碰撞体，因为它死亡后没有理由再占据物理空间
            //      与别人的索敌候选列表（详见 DeadState.Enter 的说明）。
            if (health != null)
            {
                health.OnDied -= HandleDied;
            }

            if (logDeathEvents)
            {
                Debug.Log($"[TowerController] {name} 已被摧毁，已停止攻击并退出索敌。", this);
            }

            // 4. 可选的物体销毁。放在最后执行：Destroy 之后本帧内本组件的方法仍可能被调用（Unity 延迟到帧末真正销毁），
            //    所以前面的收尾动作必须先做完。
            if (destroyOnDeath)
            {
                Destroy(gameObject);
            }
        }

        /// <summary>
        /// 兜底退订。两条路径都要覆盖：
        ///  · 组件被禁用（死亡收尾、对局结束冻结战场）→ OnDisable；
        ///  · 物体在存活状态下被销毁（换场景、手动删除）→ OnDestroy。
        /// 漏掉任一条，HealthComponent 都会持有已失效组件的引用。
        /// </summary>
        private void OnDisable()
        {
            UnsubscribeAlliedHeroes();
        }

        /// <summary>物体销毁时的最终退订（幂等：UnsubscribeAlliedHeroes 内部会清空清单）。</summary>
        private void OnDestroy()
        {
            UnsubscribeAlliedHeroes();

            if (health != null)
            {
                health.OnDied -= HandleDied;
            }
        }

        /// <summary>生成目标的简短描述，仅用于日志。</summary>
        private static string DescribeTarget(ITargetable target)
        {
            if (target is Component component)
            {
                return $"{component.name}({target.Team})";
            }

            return $"ITargetable({target.Team})";
        }

        /// <summary>就"缺少 ProjectileSpawner"告警一次。用一次性标记避免每次攻击刷一条日志。</summary>
        private void ReportMissingSpawnerOnce()
        {
            if (hasReportedMissingSpawner)
            {
                return;
            }

            hasReportedMissingSpawner = true;

            Debug.LogError(
                $"[TowerController] {name} 未挂载 ProjectileSpawner，塔的普攻无法发射弹道，" +
                "已降级为即时结算伤害。请执行 MOBA Demo/一键组装测试战场 重新生成防御塔。", this);
        }

        /// <summary>就"弹道发不出去、已降级"告警一次。</summary>
        private void ReportProjectileFallbackOnce()
        {
            if (hasReportedProjectileFallback)
            {
                return;
            }

            hasReportedProjectileFallback = true;

            Debug.LogWarning(
                $"[TowerController] {name} 本次普攻未能生成弹道（弹道预制体缺失或生成器不可用），" +
                "已降级为命中即结算。塔仍可攻击，但失去了弹道的可见飞行时间——" +
                "请检查 Assets/Prefabs/VFX/Projectile.prefab 与塔上的 ProjectileSpawner 配置。", this);
        }
    }
}
