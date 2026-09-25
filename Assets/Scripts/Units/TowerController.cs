using UnityEngine;
using MOBA.Components;
using MOBA.Core;

namespace MOBA.Units
{
    /// <summary>
    /// 防御塔控制器：固定建筑，不移动，自动攻击进入交战半径的敌方单位。
    ///
    /// 职责边界（README 3.3 / 计划书阶段四）：
    /// 1. 只做三件事——「按固定周期索敌」「把目标交给 CombatComponent 结算伤害」「死亡时脱离战斗」；
    /// 2. 不驱动移动（防御塔按设计没有 MovementComponent，见 EntityBase.RequiresMovement 的说明），
    ///    因此【不要】给防御塔挂 EntityAIController：FSM 的 MoveState 会在缺少移动组件时报错；
    /// 3. 不实现自己的目标合法性判断——目标是否合法（存活 / 可选中 / 敌方）一律由
    ///    TargetingComponent 负责，本类只负责"什么时候问它"；
    /// 4. 交战半径 = CombatComponent.AttackRange（AttackData.AttackRange），【不使用】独立的索敌半径。
    ///    固定建筑不能追击，「能锁定的范围」必须等于「能打到的范围」，否则会出现"锁了却打不到"的目标抖动。
    ///    配套：EntityBase.ApplyStats 对防御塔会把 TargetingComponent 的搜索半径也注入成攻击距离。
    ///
    /// 与 EntityAIController 的关系：两者是互斥的替代实现。可移动单位用 FSM 编排
    /// Idle/Move/Chase/Attack/Dead 五态，固定建筑没有走位需求，用本类的直线逻辑即可，不必为它引入状态机。
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(EntityBase))]
    public class TowerController : MonoBehaviour
    {
        [Header("索敌节奏")]
        [Tooltip("索敌检测间隔（秒）。FindNearestEnemy 内部是一次 Physics.OverlapSphere，会产生 GC Alloc，" +
                 "因此按计划书第 6 条「AI 感知采用固定周期检测」的要求做节流。填 0 表示每帧检测（仅调试用）。")]
        [Min(0f)]
        [SerializeField] private float detectionInterval = 0.25f;

        [Header("死亡处理")]
        [Tooltip("死亡后是否销毁自身 GameObject。默认 false：只停止攻击逻辑并退出索敌，" +
                 "保留物体便于观察死亡表现与排查问题；确认不需要后再改为 true。")]
        [SerializeField] private bool destroyOnDeath = false;

        [Header("调试")]
        [Tooltip("在 Console 输出防御塔被摧毁的记录（只输出一次）。")]
        [SerializeField] private bool logDeathEvents = true;

        /// <summary>实体身份入口。由 [RequireComponent] 保证存在，但仍做判空以给出明确报错。</summary>
        private EntityBase entity;

        /// <summary>生命组件。订阅 OnDied 用，必须存在，否则防御塔死亡后不会停止攻击。</summary>
        private HealthComponent health;

        /// <summary>索敌组件。防御塔的"眼睛"，必须存在。</summary>
        private TargetingComponent targeting;

        /// <summary>战斗组件。防御塔的"手"，必须存在。</summary>
        private CombatComponent combat;

        /// <summary>索敌计时器（秒）。归零时执行一次检测并重置，实现"固定周期"而非每帧。</summary>
        private float detectionTimer;

        /// <summary>
        /// 依赖是否已解析完成。
        /// 单独用一个标记而不是只依赖 enabled：若外部在运行期把本组件重新 enabled（Inspector 勾选、脚本赋值），
        /// Update 会再次执行，此时 targeting / combat 可能仍为 null。用标记兜底可彻底避免空引用。
        /// </summary>
        private bool hasInitialized;

        /// <summary>死亡收尾是否已执行。用于保证 OnDied 回调与"订阅时已死亡"的补偿路径不会重复执行。</summary>
        private bool hasHandledDeath;

        /// <summary>是否已被摧毁（只读），供调试视图与 MatchController 读取。</summary>
        public bool IsDestroyed => hasHandledDeath;

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
        /// 解析组件依赖并订阅死亡事件。
        /// 放在 Start 的原因同上：Unity 保证所有 Awake 先于任何 Start 执行，此时所有组件必定已挂载完成。
        /// </summary>
        private void Start()
        {
            ResolveComponents();
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
        /// 每帧驱动。四步顺序不可调换：
        /// 校验失效目标 → 校验是否超出交战半径 → 周期索敌 → 攻击。
        /// 先剔除旧目标再索敌，可以避免"刚被清掉的目标又被重新锁定"造成的目标抖动。
        /// </summary>
        private void Update()
        {
            if (!hasInitialized || hasHandledDeath)
            {
                return;
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

            // ---------- 3. 周期性索敌 ----------
            // 只有这一步会触发 Physics.OverlapSphere（有 GC Alloc），所以必须节流，不能每帧无条件调用。
            detectionTimer -= Time.deltaTime;
            if (detectionTimer <= 0f)
            {
                detectionTimer = detectionInterval;
                AcquireTarget();
                currentTarget = targeting.CurrentTarget;
            }

            // ---------- 4. 攻击 ----------
            // 目标在交战半径内、但可能仍在攻击距离外：TryAttack 内部会做距离与冷却判定，
            // 不满足条件时返回 false 且不产生任何副作用，因此这里无需重复判断距离。
            if (currentTarget != null)
            {
                combat.TryAttack(currentTarget);
            }
        }

        /// <summary>
        /// 执行一次索敌并更新当前目标。
        /// 策略：始终锁定半径内"最近"的合法敌人——防御塔不移动，"最近"是稳定且符合直觉的选择
        /// （先打威胁最直接的那个）。FindNearestEnemy 已按索敌半径过滤，因此它返回 null
        /// 就等价于"当前半径内没有可打的敌人"，此时清空目标即可。
        /// </summary>
        private void AcquireTarget()
        {
            ITargetable nearest = targeting.FindNearestEnemy();

            if (nearest == null)
            {
                // 范围内已无敌方（含"原目标已走出半径"的情况），清空目标。
                // 目标本就为 null 时 ClearTarget 是空操作，不会产生多余日志。
                targeting.ClearTarget();
                return;
            }

            // SetTarget 内部会再走一遍完整校验；传入与当前相同的目标时直接返回，不会重复打印日志。
            targeting.SetTarget(nearest);
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

        /// <summary>
        /// 死亡收尾：清空目标并停用逻辑 → 退订事件 → 可选销毁。
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

            // 1. 清空目标并停止每帧逻辑（禁用组件比销毁物体更轻，也便于事后排查）。
            if (targeting != null)
            {
                targeting.ClearTarget();
            }

            enabled = false;

            // 2. 退订：防御塔只死亡一次，留着订阅没有意义，还会让 HealthComponent 的委托链白占引用。
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

            // 3. 可选的物体销毁。放在最后执行：Destroy 之后本帧内本组件的方法仍可能被调用（Unity 延迟到帧末真正销毁），
            //    所以前面的收尾动作必须先做完。
            if (destroyOnDeath)
            {
                Destroy(gameObject);
            }
        }

        /// <summary>
        /// 兜底退订。物体在存活状态下被销毁（换场景、手动删除）时同样需要解绑，否则 HealthComponent 会持有已销毁组件的引用。
        /// </summary>
        private void OnDestroy()
        {
            if (health != null)
            {
                health.OnDied -= HandleDied;
            }
        }
    }
}
