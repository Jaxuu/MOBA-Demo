using UnityEngine;
using MOBA.Components;
using MOBA.Data;
using MOBA.Skills;

namespace MOBA.Core
{
    /// <summary>
    /// 实体根对象：所有可战斗单位（英雄 / 小兵 / 防御塔 / 基地）的统一入口。
    ///
    /// 职责边界（严格遵循计划书「避免上帝类」）：
    /// 1. 持有身份信息（阵营、单位类型）与静态配置数据（EntityStatsData）引用；
    /// 2. 聚合核心组件的引用，让其他模块可以经 EntityBase 拿到组件，而不必各自 GetComponent；
    /// 3. 只负责「初始化」——把配置数据注入到组件，不参与任何移动、战斗、寻敌的运行时逻辑；
    /// 4. 实现 ITargetable，作为"可被索敌"的统一身份出口（索敌/攻击方只认接口，不认具体单位类型）。
    ///
    /// 为什么本类【不】加 [RequireComponent(typeof(MovementComponent))]：
    /// RequireComponent 约束的是"同一个 GameObject 上必然存在的组件"，
    /// 而防御塔与基地（EntityType.Tower / Base）按计划书就是没有移动组件的固定建筑。
    /// 若强行声明，Unity 会在挂载时给塔和基地也自动补上 MovementComponent + NavMeshAgent，
    /// 反而制造出"会走的防御塔"这种错误配置。因此这里改用运行期按类型校验（见 ValidateDependencies）。
    /// </summary>
    [DisallowMultipleComponent]
    public class EntityBase : MonoBehaviour, ITargetable
    {
        [Header("身份与阵营")]
        [Tooltip("阵营，目标合法性判断依据（仅阵营不同的单位互为敌人）。")]
        [SerializeField] private TeamType team = TeamType.Player;

        [Tooltip("单位类型，用于区分行为策略（英雄/小兵可移动，塔与基地为固定建筑）。")]
        [SerializeField] private EntityType entityType = EntityType.Hero;

        [Tooltip("是否可被选为目标。死亡、隐身、出生保护等状态下应由控制逻辑置为 false。")]
        [SerializeField] private bool isSelectable = true;

        [Header("静态配置（只读模板）")]
        [Tooltip("指向 Assets/ScriptableObjects/EntityStats 下的配置资产；运行时不得写回该资产。")]
        [SerializeField] private EntityStatsData statsData;

        /// <summary>所属阵营。只读，运行时不可变更（如后续需要换阵营，再单独提供接口）。同时满足 ITargetable.Team 契约。</summary>
        public TeamType Team => team;

        /// <summary>单位类型。只读。</summary>
        /// 注意：属性名与枚举类型名同名（C# 允许），若在本类内部需要引用枚举类型本身，
        /// 请先赋给局部变量或使用全限定名（MOBA.Core.EntityType.X），避免被属性名遮蔽。
        public EntityType EntityType => entityType;

        /// <summary>静态配置数据引用。可能为 null（尚未配置资产），使用方需自行判空。</summary>
        public EntityStatsData StatsData => statsData;

        /// <summary>
        /// 移动组件引用。公开只读：外部可以调用其方法（MoveTo / Stop），但不能替换该引用。
        /// 固定建筑（防御塔、基地）没有移动组件，此时该属性为 null，使用前必须判空。
        /// </summary>
        public MovementComponent Movement { get; private set; }

        /// <summary>
        /// 生命组件引用。公开只读。
        /// 计划书阶段四要求小兵、防御塔、基地复用同一套生命与伤害接口，因此所有可战斗单位都应挂载它。
        /// 未挂载时为 null，使用前必须判空。
        /// </summary>
        public HealthComponent Health { get; private set; }

        /// <summary>
        /// 索敌组件引用。公开只读。
        /// 注意：防御塔也需要索敌（固定位置索敌），因此它并非"可移动单位专属组件"。
        /// 未挂载时为 null，使用前必须判空。
        /// </summary>
        public TargetingComponent Targeting { get; private set; }

        /// <summary>
        /// 战斗组件引用。公开只读。
        /// 未挂载时为 null（例如无攻击能力的纯装饰单位），使用前必须判空。
        /// </summary>
        public CombatComponent Combat { get; private set; }

        /// <summary>
        /// 法力组件引用（阶段六）。公开只读。
        /// 未挂载时为 null（小兵、防御塔、基地按设计都没有法力），使用前必须判空。
        /// </summary>
        public ManaComponent Mana { get; private set; }

        /// <summary>
        /// 技能组件引用（阶段六）。公开只读。
        /// 未挂载时为 null（只有英雄有技能），使用前必须判空。
        /// </summary>
        public SkillComponent Skills { get; private set; }

        #region ITargetable 实现

        /// <summary>
        /// 目标位置载体（ITargetable 契约成员）。
        /// 返回实体根节点的 Transform：距离判定、朝向、追击都以此为准，
        /// 这样即使模型挂在子节点上，也不会出现"索敌按根节点、攻击按子节点"的口径不一致。
        /// </summary>
        public Transform TargetTransform => transform;

        /// <summary>
        /// 当前是否仍是合法目标（ITargetable 契约成员）。
        /// 把"存活 + 可选中 + 对象未被销毁"三类判断聚合在一处，调用方只需检查这一个属性，
        /// 避免各处在不同地方漏检某一项（计划书验收标准要求"不能攻击已死亡或已销毁目标"）。
        /// </summary>
        public bool IsValidTarget
        {
            get
            {
                // 1. 对象已被销毁：即使 C# 引用不为空，Unity 重载的 == 运算符也能识别"已销毁"对象，
                //    这种引用不应再被当作合法目标（否则后续访问组件会抛 MissingReferenceException）。
                if (this == null)
                {
                    return false;
                }

                // 2. 自身或父级被禁用：不应再作为可攻击目标。
                if (!gameObject.activeInHierarchy)
                {
                    return false;
                }

                // 3. 没有生命组件 = 不可受伤，作为目标没有意义。
                if (Health == null)
                {
                    return false;
                }

                // 4. 已死亡：这条是"目标死亡后攻击与寻路立即停止"的关键依据。
                if (Health.IsDead)
                {
                    return false;
                }

                // 5. 被显式标记为不可选中（隐身、出生保护、死亡表现中等）。
                if (!isSelectable)
                {
                    return false;
                }

                return true;
            }
        }

        /// <summary>当前是否可被选为目标（只读），供调试视图读取。</summary>
        public bool IsSelectable => isSelectable;

        /// <summary>
        /// 设置可选中状态。
        /// 典型用途：进入死亡状态时置 false（计划书 DeadState 要求"禁用选中与碰撞"），
        /// 或实现出生保护、隐身等机制。
        /// </summary>
        /// <param name="selectable">是否可被选为目标。</param>
        public void SetSelectable(bool selectable)
        {
            isSelectable = selectable;
        }

        #endregion

        /// <summary>
        /// 本实体是否属于"必须能移动"的单位类型。
        /// 用于把"漏挂组件"和"建筑本就没有移动组件"两种情况区分开，避免误报或漏报。
        /// </summary>
        private bool RequiresMovement
        {
            get
            {
                // 本类存在与枚举同名的属性 EntityType，直接写 EntityType.Hero 会命中 C# 的 "Color Color" 规则
                // （规范允许该写法并解析为静态枚举成员，但可读性差）。这里统一用全限定名，彻底避开该规则。
                EntityType type = entityType;
                return type == MOBA.Core.EntityType.Hero || type == MOBA.Core.EntityType.Minion;
            }
        }

        /// <summary>
        /// 本实体是否属于"按设计应当具备攻击能力"的单位类型。
        /// 用途与 <see cref="RequiresMovement"/> 完全对称：把"漏挂组件"和"本就没有该能力"区分开。
        /// 依据 README 3.3：基地是"不可移动、不可攻击实体，仅作为胜负判定的载体"，
        /// 因此基地缺 CombatComponent 属于正确配置，不应在 Console 报出误导性的"该单位不会发起攻击"警告。
        /// </summary>
        private bool RequiresCombat
        {
            get
            {
                EntityType type = entityType;
                return type != MOBA.Core.EntityType.Base;
            }
        }

        /// <summary>
        /// 是否已完成初始化。
        /// 用途：代码显式调用过 Initialize() 之后，生命周期里的 Start 不再重复注入——
        /// 重复注入会把已经受过伤的单位重新回满血（HealthComponent.Initialize 会重置生命），属于致命副作用。
        /// </summary>
        private bool hasInitialized;

        /// <summary>
        /// Unity 生命周期入口：只做组件引用缓存。
        /// Unity 保证「所有 Awake 都先于任何 Start 执行」，所以在这里取引用，
        /// 可以确保稍后的 Start 阶段（包括其他组件的 Start）拿到的一定是已初始化好的对象。
        /// </summary>
        private void Awake()
        {
            CacheComponents();
        }

        /// <summary>
        /// 登记到实体注册表，使对局层（MatchController）能在对局结束时统一冻结战场。
        ///
        /// 放在 OnEnable 而不是 Awake：物体被临时禁用时也应自动退出清单，
        /// 语义与「该单位当前不参与对局」一致；且 Unity 保证物体被销毁前一定先走 OnDisable，
        /// 因此清单里不会残留已销毁的实体（残留会导致冻结战场时访问到已销毁对象）。
        /// </summary>
        private void OnEnable()
        {
            EntityRegistry.Register(this);
        }

        /// <summary>从实体注册表注销。与 OnEnable 成对，保证清单始终只含「存活且已启用」的实体。</summary>
        private void OnDisable()
        {
            EntityRegistry.Unregister(this);
        }

        /// <summary>
        /// 缓存/补全自身组件引用。
        /// 为什么单独抽成方法并允许重复调用：用代码动态创建单位时（MinionSpawner、自动化测试），
        /// AddComponent&lt;EntityBase&gt;() 会立刻触发 Awake，而此时其他组件往往还没挂上，
        /// 于是 Awake 里缓存到的引用会永久为 null。Initialize() 会再调用一次本方法把引用补全。
        /// </summary>
        private void CacheComponents()
        {
            // 只取自身组件，不向子节点查找，避免层级变动导致引用失效或误抓。
            // 注意：这里的 null 是"允许为空"的语义（不同单位类型的组件组合不同），
            // 因此不在此处报错，而是在 ValidateDependencies 里按 EntityType 分情况校验。
            Movement = GetComponent<MovementComponent>();
            Health = GetComponent<HealthComponent>();
            Targeting = GetComponent<TargetingComponent>();
            Combat = GetComponent<CombatComponent>();

            // 阶段六新增：法力与技能同样是"允许为空"的可选能力（小兵/建筑没有它们）。
            Mana = GetComponent<ManaComponent>();
            Skills = GetComponent<SkillComponent>();
        }

        /// <summary>
        /// 生命周期初始化：校验依赖 + 把静态配置数据注入到各组件。
        /// 放在 Start 而不是 Awake，是因为 MovementComponent 需要在它自己的 Awake 里先缓存 NavMeshAgent，
        /// 而同一 GameObject 上多个组件的 Awake 执行顺序并不确定——放到 Start 可彻底规避这个时序问题。
        /// </summary>
        private void Start()
        {
            if (hasInitialized)
            {
                // 外部已经显式 Initialize 过（动态创建的单位），不再重复注入。
                return;
            }

            InitializeInternal();
        }

        /// <summary>
        /// 运行时初始化入口。预制体上的单位走正常的 Start 流程，不需要调用本方法；
        /// 本方法供"用代码动态创建的单位"使用（例如自动化测试、后续的 MinionSpawner）。
        /// 调用时机要求：必须在所有 AddComponent 完成之后调用，否则 CacheComponents 仍然补不全引用。
        /// </summary>
        /// <param name="stats">静态配置资产，允许为 null（为 null 时沿用 Inspector 上已有的引用）。</param>
        public void Initialize(EntityStatsData stats)
        {
            if (stats != null)
            {
                statsData = stats;
            }

            InitializeInternal();
        }

        /// <summary>
        /// 运行时初始化入口（含身份配置）。
        /// 预制体上的单位由 Inspector 配好阵营与类型；代码创建的单位没有 Inspector 可配，故通过本重载传入。
        /// </summary>
        /// <param name="stats">静态配置资产。</param>
        /// <param name="newTeam">阵营。</param>
        /// <param name="newEntityType">单位类型。</param>
        public void Initialize(EntityStatsData stats, TeamType newTeam, EntityType newEntityType)
        {
            team = newTeam;
            entityType = newEntityType;
            Initialize(stats);
        }

        /// <summary>
        /// 初始化主流程：补全组件引用 → 校验依赖 → 注入配置。
        /// 三步顺序不能调换：引用不全就无法校验，未校验就注入会把错误静默吞掉。
        /// </summary>
        private void InitializeInternal()
        {
            // 先补全组件引用：动态创建的单位在 Awake 时组件可能还没挂齐。
            CacheComponents();

            ValidateDependencies();
            ApplyStats();

            hasInitialized = true;
        }

        /// <summary>
        /// 运行期依赖校验：把"会导致功能不可用"的问题用 LogError 明确抛出，
        /// 而不是等到空引用异常（NullReferenceException）在战斗中途炸出来。
        /// 日志只在初始化时输出一次，不会污染运行期 Console。
        /// </summary>
        private void ValidateDependencies()
        {
            if (statsData == null)
            {
                // 配置缺失 = 生命值、移速等全部无法初始化，属于致命问题，必须 LogError。
                Debug.LogError(
                    $"[EntityBase] {name} 未配置 EntityStatsData，属性注入被跳过（单位将使用组件自身默认值）。" +
                    "请在 Inspector 的 Stats Data 字段指定配置资产。", this);
            }

            if (Movement == null && RequiresMovement)
            {
                // 英雄/小兵没有移动组件 = 漏挂组件，属于配置错误。
                Debug.LogError(
                    $"[EntityBase] {name} 的类型为 {entityType}，必须挂载 MovementComponent 与 NavMeshAgent，" +
                    "当前缺失将导致该单位无法移动。请检查预制体配置。", this);
            }
            else if (Movement != null && !RequiresMovement)
            {
                // 反向校验：塔/基地挂了移动组件属于配置错误，计划书要求它们使用固定位置索敌。
                Debug.LogWarning(
                    $"[EntityBase] {name} 的类型为 {entityType}，按设计不应挂载 MovementComponent，" +
                    "请确认是否为误挂（防御塔/基地使用固定位置索敌）。", this);
            }

            if (Health == null)
            {
                // 阶段二起所有可战斗单位都应挂载 HealthComponent；用 Warning 而不是 Error，
                // 是为了兼容阶段一遗留的、尚未补齐组件的测试单位，避免一进场景就满屏红色报错。
                Debug.LogWarning(
                    $"[EntityBase] {name} 未挂载 HealthComponent，该单位不会受伤，也无法被作为攻击目标结算伤害。", this);
            }

            if (Combat == null)
            {
                if (RequiresCombat)
                {
                    // 没有战斗组件 = 该单位无法攻击。英雄/小兵/防御塔缺它都是配置遗漏，
                    // 但只提示不报错，以兼容阶段二遗留的、无攻击能力的静态测试单位。
                    Debug.LogWarning($"[EntityBase] {name} 未挂载 CombatComponent，该单位不会发起攻击。", this);
                }
                // 基地走到这里属于正常配置（README 3.3：基地不可攻击），刻意不输出任何日志。
            }
            else if (!RequiresCombat)
            {
                // 反向校验：基地挂了战斗组件属于误配，会让"不可攻击的基地"意外参战。
                Debug.LogWarning(
                    $"[EntityBase] {name} 的类型为 {entityType}，按设计不应挂载 CombatComponent，" +
                    "请确认是否为误挂（基地仅作为胜负判定载体）。", this);
            }

            // 阶段六：只有英雄按设计拥有技能（小兵与建筑无技能是正常配置）。
            // 因此这里用 Warning 且限定 Hero 类型——若不加类型条件，一场景的小兵都会刷同一条告警。
            if (Skills == null && entityType == MOBA.Core.EntityType.Hero)
            {
                Debug.LogWarning(
                    $"[EntityBase] {name} 的类型为 Hero，但未挂载 SkillComponent，该英雄无法释放任何技能。" +
                    "请检查预制体配置（一键组装工具会在英雄预制体上自动挂载并注入 Q/W 配置）。", this);
            }

            // 碰撞体检查：FindNearestEnemy 的实现是 Physics.OverlapSphere + GetComponentInParent<ITargetable>，
            // 因此【没有 Collider 的单位永远不会出现在任何一次索敌结果里】——而且不会抛任何异常，
            // 表现为「敌人就在旁边却互相看不见」，是排查成本最高的一类配置错误。
            // 用 LogWarning 而不是 LogError：它不致命（单位仍可移动、仍可被脚本直接攻击），只是无法被锁定。
            // 用 GetComponentInChildren 而不是 GetComponent：碰撞体常挂在模型子节点上，
            // 而 EntityBase 位于根节点（FindNearestEnemy 也是向上查找，两者口径一致）。
            if (GetComponentInChildren<Collider>() == null)
            {
                Debug.LogWarning(
                    $"[EntityBase] {name} 及其子节点上没有 Collider，该单位无法被索敌锁定" +
                    "（FindNearestEnemy 依赖 Physics.OverlapSphere，需要碰撞体参与物理查询）。" +
                    "请为预制体补上碰撞体。", this);
            }
        }

        /// <summary>
        /// 将 EntityStatsData 中的数值应用到各功能组件。
        /// 所有"配置 → 组件"的注入都集中在这里，保证数据流只有一条，便于排查"为什么数值没生效"。
        /// </summary>
        private void ApplyStats()
        {
            if (statsData == null)
            {
                // 上面 ValidateDependencies 已报错，这里静默返回，避免同一问题打两条日志。
                return;
            }

            // 防御塔、基地等固定建筑没有 MovementComponent，属于正常情况，直接跳过即可。
            if (Movement != null)
            {
                // 通过组件公开方法写入，而不是直接改 NavMeshAgent.speed：
                // NavMeshAgent 被封装在 MovementComponent 内部，保证速度只有一条写入路径。
                Movement.SetMoveSpeed(statsData.MoveSpeed);
            }

            if (Health != null)
            {
                // 用配置资产里的最大生命值初始化生命组件（同时会把生命填满并广播一次血量变化）。
                // 注意：这次广播发生在 Start 阶段，血条 UI 若在自己的 Start 里订阅，可能订阅不到，
                // 因此 UI 订阅后必须主动读一次 CurrentHealth / MaxHealth 做同步。
                Health.Initialize(statsData.MaxHealth);
            }

            if (Combat != null)
            {
                // 攻击配置优先取自 EntityStatsData.Attack；配置里留空时，CombatComponent 会保留
                // 它在 Inspector 上直挂的 AttackData（见 CombatComponent.Initialize 的约定）。
                //
                // 【顺序说明】Combat 必须先于 Targeting 注入：下面算防御塔的索敌半径时要读
                // Combat.AttackRange，若放在后面就会读到注入前的旧值（0 或 Inspector 上的残留配置）。
                Combat.Initialize(statsData.Attack);
            }

            if (Targeting != null)
            {
                // 索敌半径按单位类型分两种取法：
                //
                //  - 可移动单位（英雄 / 小兵）：用配置的 DetectionRange —— 它们需要「先看见、再靠近」，
                //    索敌半径大于攻击距离是合理设计。
                //  - 固定建筑（防御塔）：直接取攻击距离。建筑不能追击，「能锁定的范围」必须等于
                //    「能打到的范围」；若两者不一致（常见配置是索敌半径大于攻击距离），
                //    塔会锁住一个刚好打不到的目标、下一个检测周期又把它清掉，形成目标抖动，
                //    而且会「占着」目标位，让射程内真正能打到的敌人迟迟拿不到仇恨。
                //
                //    这里读 Combat.AttackRange 而不是 statsData.Attack.AttackRange，是为了兼容
                //    「配置资产里没填 Attack、改为在 CombatComponent 上直挂 AttackData」的预制体
                //    （见 CombatComponent.Initialize 的约定），两种情况都能取到正确值。
                //
                // 这条规则只在这里实现一次：TowerController 只负责读取 CombatComponent.AttackRange，
                // 不再持有独立的索敌半径，避免同一件事出现两个数据源。
                float detectionRadius = statsData.DetectionRange;

                if (entityType == MOBA.Core.EntityType.Tower && Combat != null)
                {
                    detectionRadius = Combat.AttackRange;
                }

                Targeting.SetDetectionRadius(detectionRadius);
            }

            // 阶段六：法力与技能。顺序上先法力后技能——SkillComponent 施法时要读 ManaComponent 判蓝量，
            // 虽然它用的是运行期组件引用（不受注入顺序影响），但让数据流顺序与依赖关系一致，
            // 排查"为什么技能放不出来"时不必再考虑时序。
            if (Mana != null)
            {
                Mana.Initialize(statsData.MaxMana, statsData.ManaRegenPerSecond);
            }

            if (Skills != null)
            {
                // 与 CombatComponent.Initialize 同一约定：传 null 时保留 Inspector 上直挂的引用，
                // 而不是把它清空——这样"配置资产里没填技能"的旧预制体仍可靠直挂方式工作。
                Skills.Initialize(statsData.SkillQ, statsData.SkillW);
            }
        }

        /// <summary>
        /// 编辑器内校验：在 Inspector 修改数值或挂载组件时提示缺失引用，把问题提前暴露在编辑期而不是运行期。
        /// 这里只报 Warning（编辑器提示），运行期的致命问题由 ValidateDependencies 用 LogError 兜底。
        ///
        /// 【为什么校验要延迟到编辑器空闲时再执行】
        /// Unity 在 AddComponent 的【同一瞬间】就会同步调用 OnValidate，而一键组装工具（AutoSceneBuilder）
        /// 的顺序必然是「先 AddComponent、后注入 statsData」——那一刻 statsData 必然是 null，
        /// 于是每次组装都会刷出几条「Stats Data 未赋值」的假告警（双方基地 + 英雄预制体各一条），
        /// 而注入其实在几行之后就完成了。这类假告警比"没有校验"更糟：
        /// 它会训练使用者忽略 Console，而本项目整套流程都依赖 Console 暴露真实的配置错误。
        ///
        /// 因此编辑器下改为挂一个 delayCall：等工具那一整段同步流程走完、注入真正落盘之后再校验。
        /// 若那时仍为空，说明确实是漏配，告警照常输出——校验能力一点没少，只是不再被瞬时状态误触发。
        /// 构建后的运行时（非编辑器）没有这个瞬时窗口，直接同步校验即可。
        /// </summary>
        private void OnValidate()
        {
#if UNITY_EDITOR
            // 先退订再订阅：在 Inspector 里拖数值会高频触发 OnValidate，
            // 不去重会堆积出一串重复回调（每个都在下一帧执行一次同样的检查）。
            UnityEditor.EditorApplication.delayCall -= ValidateStatsDataAssigned;
            UnityEditor.EditorApplication.delayCall += ValidateStatsDataAssigned;
#else
            ValidateStatsDataAssigned();
#endif
        }

        /// <summary>
        /// Stats Data 缺口的实际校验体。
        /// 编辑器下由 delayCall 调用，可能晚于 OnValidate 若干帧，因此必须自己判空——
        /// 对象可能在延迟期间被销毁（一键组装工具生成的临时预制体对象正是如此，保存后立刻 DestroyImmediate）。
        /// </summary>
        private void ValidateStatsDataAssigned()
        {
            if (this == null)
            {
                return;
            }

            if (statsData == null)
            {
                Debug.LogWarning($"[EntityBase] {name} 的 Stats Data 未赋值，运行时将无法完成属性注入。", this);
            }
        }
    }
}
