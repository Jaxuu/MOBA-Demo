using System.Collections;
using UnityEngine;
using MOBA.Components;
using MOBA.Core;
using MOBA.Gameplay;

namespace MOBA.AI
{
    /// <summary>
    /// AI 控制器：状态的【上下文】与 FSM 的驱动者。
    ///
    /// 职责边界（对应计划书 3.2「EntityAIController：收集环境条件并驱动状态机，不直接实现状态行为」）：
    /// 1. 持有 EntityBase / LanePath / StateMachine 三个引用，并把它们暴露给各个状态（状态通过本类拿上下文）；
    /// 2. 负责创建并持有五个具体状态实例、决定起始状态、驱动 StateMachine.Tick()；
    /// 3. 提供【状态跳转阈值】与【目标有效性校验】两个共享能力——
    ///    它们是多个状态都要用的公共判断，集中在此处才能保证口径一致（尤其是防震荡的滞回阈值）；
    /// 4. 监听 EntityBase 的死亡事件，把状态机强制切到 DeadState——死亡是唯一由事件驱动、
    ///    而非由状态自身判断的切换，因为"生命归零"只有 HealthComponent 知道；
    /// 5. 本类【不实现任何具体行为】：不移动、不攻击、不索敌，这些都在状态里编排。
    /// </summary>
    [DisallowMultipleComponent]
    public class EntityAIController : MonoBehaviour
    {
        [Header("依赖")]
        [Tooltip("本单位的实体身份入口。留空时自动从同一 GameObject 上获取。")]
        [SerializeField] private EntityBase entity;

        [Tooltip("推进路线。优先由 InitializeAI(LanePath) 注入；预制体上的单位也可直接在 Inspector 指定。")]
        [SerializeField] private LanePath lanePath;

        [Header("AI 参数")]
        [Tooltip("【防震荡】放弃追击的距离系数：与目标的距离超过「索敌半径 × 该系数」时放弃追击、返回路线推进。\n" +
                 "必须 ≥ 1：追击是在索敌半径内发起的，若退出阈值也取索敌半径，目标飘到半径边缘时\n" +
                 "就会每帧在 Move ↔ Chase 之间反复切换。")]
        [Min(1f)]
        [SerializeField] private float chaseAbandonRangeFactor = 1.5f;

        [Tooltip("【防震荡】退出攻击的距离系数（滞回上界）：与目标的距离超过「攻击距离 × 该系数」时才退回追击。\n" +
                 "必须 ≥ 1 且应大于 1：进入攻击的阈值是「攻击距离」，退出阈值必须更大，两者之间形成滞回死区，\n" +
                 "否则目标在攻击距离边缘轻微移动就会造成 Chase ↔ Attack 每帧震荡。")]
        [Min(1f)]
        [SerializeField] private float attackExitRangeFactor = 1.15f;

        [Tooltip("追击时的重新寻路阈值（单位）：目标相对上次寻路位置移动超过该距离，才重新下达移动指令。\n" +
                 "设为 0 表示每帧都重新寻路（不推荐：NavMeshAgent.SetDestination 每次都会重新算路）。")]
        [Min(0f)]
        [SerializeField] private float chaseRepathDistance = 0.25f;

        [Tooltip("牵引极限系数：与「追击起点锚点」的距离超过「索敌半径 × 该系数」时，强制放弃目标返回兵线。\n" +
                 "用途：拦住「目标一直贴着本单位跑、把本单位牵着离开兵线」的情形——\n" +
                 "这种场景下与目标的距离始终很小，只靠 chaseAbandonRangeFactor 是拦不住的。\n" +
                 "必须 ≥ 1（否则小于索敌半径，正常追击都会被判定为出界）。")]
        [Min(1f)]
        [SerializeField] private float chaseLeashRangeFactor = 2f;

        [Tooltip("感知节流间隔（秒）：FindNearestEnemy 内部是一次 Physics.OverlapSphere（每次分配一个数组），\n" +
                 "按计划书第 6 条「AI 感知采用固定周期检测」在此统一节流，避免每个单位每帧做物理范围查询。\n" +
                 "填 0 表示每帧检测（仅调试用）。")]
        [Min(0f)]
        [SerializeField] private float detectionInterval = 0.25f;

        [Tooltip("【防卡死】判定为「卡住」的速度平方阈值：实际速度的平方低于它即视为没有在移动。\n" +
                 "默认 0.1 对应约 0.32 米/秒。")]
        [Min(0f)]
        [SerializeField] private float stagnationVelocitySqrThreshold = 0.1f;

        [Tooltip("【防卡死】连续「没在移动」多久判定为被卡住（秒），超时后强制重新下达一次移动指令。")]
        [Min(0.1f)]
        [SerializeField] private float stagnationDuration = 1f;

        [Header("尸体清理（白盒期）")]
        [Tooltip("单位死亡后尸体在场景里停留多久（秒），到期销毁整个 GameObject。\n" +
                 "为什么必须销毁：本 Demo 的小兵由 MinionSpawner 持续生成，一局会产生成百上千个；\n" +
                 "尸体若只停逻辑不销毁，长时间对局会持续堆积 GameObject 与组件（内存与层级双双膨胀）。\n" +
                 "填 0 表示死亡当帧（下一帧）就销毁。")]
        [Min(0f)]
        [SerializeField] private float corpseLingerSeconds = 2f;

        [Tooltip("在 Console 输出「尸体已销毁」的记录。一局会产生成百上千个小兵，" +
                 "排查尸体是否真的被清理时打开，平时建议关闭以免刷屏。")]
        [SerializeField] private bool logDeathCleanup = false;

        /// <summary>FSM 核心引擎，在 Awake 中实例化（纯 C# 类，不需要挂在 GameObject 上）。</summary>
        private StateMachine stateMachine;

        /// <summary>五个具体状态实例。由本类统一创建并持有，保证"一个实体一份状态实例"，避免状态之间共享可变数据。</summary>
        private IdleState idleState;
        private MoveState moveState;
        private ChaseState chaseState;
        private AttackState attackState;
        private DeadState deadState;

        /// <summary>已成功订阅死亡事件的生命组件引用，用于在销毁时精确退订（避免订阅对象变化后退错对象）。</summary>
        private HealthComponent subscribedHealth;

        /// <summary>
        /// 尸体延时销毁的协程句柄。
        /// 作用有两个：① 防止死亡事件重复到达时启动两个销毁协程；
        /// ② 让"是否已经安排了销毁"这件事可被查询（调试时很实用）。
        /// </summary>
        private Coroutine corpseCleanupRoutine;

        /// <summary>是否已完成 AI 初始化。作用与 EntityBase.hasInitialized 一致：防止 Start 与显式初始化重复执行。</summary>
        private bool hasInitialized;

        /// <summary>是否已就"请求追击但单位不具备追击能力"报过警告，防止每帧刷屏。</summary>
        private bool hasReportedCannotChase;

        /// <summary>是否已就"已被牵引出界、拒绝追击"报过警告，防止每帧刷屏。</summary>
        private bool hasReportedBeyondLeash;

        /// <summary>
        /// 距离下次允许执行物理索敌查询的剩余时间（秒）。
        /// 由 Update 每帧递减，只有归零时 TryDetectEnemy 才真正调用 FindNearestEnemy。
        /// </summary>
        private float detectionTimer;

        /// <summary>
        /// 上一次节流检测的结果缓存。
        /// 【可能已失效】——目标可能在两次检测之间死亡、被销毁、被策反或跑出索敌半径，
        /// 因此每次读取前都必须重新校验（见 IsCachedDetectionUsable），绝不能直接当作有效目标返回。
        /// </summary>
        private ITargetable cachedDetectionResult;

        /// <summary>实体身份入口，供各状态读取阵营、组件引用与配置。</summary>
        public EntityBase Owner => entity;

        /// <summary>
        /// 推进路线。供 MoveState 依次取路径点。
        /// 属性名刻意不叫 LanePath：避免与类型名同名触发 C# 的 "Color Color" 规则，降低阅读歧义。
        /// </summary>
        public LanePath Lane => lanePath;

        /// <summary>状态机，供调试视图读取 CurrentState。</summary>
        public StateMachine StateMachine => stateMachine;

        /// <summary>是否已完成 AI 初始化。</summary>
        public bool IsInitialized => hasInitialized;

        #region 状态跳转阈值（供各状态共享）

        /// <summary>本单位当前攻击距离；无战斗组件或未配置攻击数据时为 0（表示无法攻击）。</summary>
        public float AttackRange
        {
            get
            {
                CombatComponent combat = entity != null ? entity.Combat : null;
                return combat != null ? combat.AttackRange : 0f;
            }
        }

        /// <summary>
        /// 是否具备攻击能力。
        /// 用途：追击的唯一目的是进入攻击距离，因此无攻击能力时不应进入追击状态。
        /// </summary>
        public bool CanAttack => AttackRange > 0f;

        /// <summary>
        /// 是否具备追击能力：既能攻击（追击的目的），又能移动（靠近的手段），二者缺一不可。
        ///
        /// 为什么必须同时检查移动组件：若单位能攻击但无法移动（例如被误挂 AI 的防御塔——
        /// 计划书规定防御塔与基地没有 MovementComponent），ChaseState 会在"距离大于攻击距离"时
        /// 判定自己无法靠近而退回推进状态，而 MoveState 下一帧又因为发现敌人而再次请求追击，
        /// 于是形成 Move → Chase → Move 的每帧互切。把闸门放在这里可以一次性堵死该通道。
        /// </summary>
        public bool CanChase
        {
            get
            {
                if (!CanAttack)
                {
                    return false;
                }

                return entity != null && entity.Movement != null;
            }
        }

        /// <summary>进入攻击状态的距离阈值（滞回下界）。</summary>
        public float AttackEnterDistance => AttackRange;

        /// <summary>
        /// 退出攻击状态的距离阈值（滞回上界）。
        /// 用 Mathf.Max 夹住系数而不是只依赖 Inspector 的 [Min]：
        /// 系数一旦被改成小于 1，退出阈值就会小于进入阈值，滞回死区消失、震荡立刻回来，
        /// 这是必须由代码兜住的不变量，不能只靠编辑器约束。
        /// </summary>
        public float AttackExitDistance => AttackRange * Mathf.Max(1f, attackExitRangeFactor);

        /// <summary>本单位当前索敌半径；无索敌组件时为 0。供状态判断与调试视图读取。</summary>
        public float DetectionRadius
        {
            get
            {
                TargetingComponent targeting = entity != null ? entity.Targeting : null;
                return targeting != null ? targeting.DetectionRadius : 0f;
            }
        }

        /// <summary>放弃追击的距离系数（只读），供调试视图复算最大追击半径，保证可视化与判定用同一份参数。</summary>
        public float ChaseAbandonRangeFactor => Mathf.Max(1f, chaseAbandonRangeFactor);

        /// <summary>放弃追击的距离阈值（滞回上界）。同样用 Mathf.Max 兜住"系数必须 ≥ 1"这一不变量。</summary>
        public float ChaseAbandonDistance => DetectionRadius * ChaseAbandonRangeFactor;

        /// <summary>牵引极限系数（只读），供调试视图复算牵引极限，保证可视化与判定用同一份参数。</summary>
        public float ChaseLeashRangeFactor => Mathf.Max(1f, chaseLeashRangeFactor);

        /// <summary>
        /// 牵引极限距离：与追击起点锚点的距离超过它即强制放弃目标、返回兵线。
        /// 与 ChaseAbandonDistance 的区别：后者量的是"与目标的距离"（追不上就放弃），
        /// 前者量的是"离开追击起点的距离"（被牵着走太远就放弃）。两者是互相补充的两个闸门。
        /// </summary>
        public float ChaseLeashDistance => DetectionRadius * ChaseLeashRangeFactor;

        /// <summary>
        /// 当前是否已被牵引出界（详见 <see cref="ChaseState.IsBeyondChaseLeash"/>）。
        /// 转发给追击状态：锚点必须由状态自己持有（它是"本轮追击"的局部事实），
        /// 但"是否出界"是推进状态决定要不要重新追击的环境条件，因此经控制器统一暴露。
        /// </summary>
        public bool IsBeyondChaseLeash => chaseState != null && chaseState.IsBeyondChaseLeash;

        /// <summary>
        /// 清除追击起点锚点，允许开始新一轮追击。
        /// 由 MoveState 在"真正回到兵线"（抵达当前路径点）时调用，是锚点唯一的清除时机。
        /// </summary>
        public void ClearChaseStartPos()
        {
            chaseState?.ClearChaseStartPos();
        }

        /// <summary>追击时的重新寻路距离阈值。</summary>
        public float ChaseRepathDistance => Mathf.Max(0f, chaseRepathDistance);

        /// <summary>
        /// 推进状态是否还有未走完的路径点。
        /// 用途：IdleState 判断"是否值得切回推进状态"。若不加这个判断，
        /// MoveState 走完路线切到 Idle、Idle 又立刻切回 Move，两者会每帧互切。
        /// </summary>
        public bool HasRemainingWaypoints => moveState != null && moveState.HasRemainingWaypoints;

        #endregion

        #region 感知节流（供各状态共享）

        /// <summary>感知节流间隔（秒），供调试视图核对。</summary>
        public float DetectionInterval => Mathf.Max(0f, detectionInterval);

        /// <summary>
        /// 节流后的敌情感知入口：各状态统一调用本方法，而不是自己去调 TargetingComponent.FindNearestEnemy。
        ///
        /// 为什么必须统一走这里：FindNearestEnemy 内部是一次 Physics.OverlapSphere（每次分配一个数组），
        /// 计划书第 6 条明确要求「AI 感知采用固定周期检测，而非所有单位每帧执行物理范围查询」。
        /// 把节流做在控制器上，状态就只是「读取结果」，不会各自实现一份间隔逻辑而互相不一致。
        ///
        /// 返回的敌情必须同时满足三条才算有效：阵营为敌、IsValidTarget（存活/未销毁/可选中）、
        /// 以及【仍在索敌半径内】。第三条最容易被忽略，但它是防震荡的关键——
        /// 缓存是上一个检测周期的结果，若目标已经跑出索敌半径仍被当作「发现敌人」返回，
        /// ChaseState 因超出追击极限放弃后，MoveState 会立刻用同一份缓存重新锁定它，
        /// 于是形成 Chase ↔ Move 的每帧互切（正是 FindNearestEnemy 的半径过滤原本要拦下的情形）。
        /// </summary>
        /// <param name="enemy">校验通过的敌人；没有则输出 null。</param>
        /// <returns>当前存在可攻击的敌人返回 true。</returns>
        public bool TryDetectEnemy(out ITargetable enemy)
        {
            enemy = null;

            EntityBase owner = entity;
            if (owner == null)
            {
                return false;
            }

            TargetingComponent targeting = owner.Targeting;
            if (targeting == null)
            {
                return false;
            }

            if (detectionTimer <= 0f)
            {
                // 到点：执行一次真实物理查询并重置计时器。
                // 用 Mathf.Max 兜住 0：间隔填 0 时若不夹住，计时器会一直为 0，等于退化成每帧查询。
                detectionTimer = Mathf.Max(0.02f, detectionInterval);
                cachedDetectionResult = targeting.FindNearestEnemy();
            }
            else if (cachedDetectionResult != null && !IsCachedDetectionUsable(targeting))
            {
                // 未到点，但缓存已经失效（目标在两次检测之间死亡 / 被销毁 / 被策反 / 跑出半径）：
                // 立刻补查一次。否则单位会空等最多一个检测周期才重新索敌，
                // 表现为「目标早就死了，本单位却还在原地发呆」。
                cachedDetectionResult = targeting.FindNearestEnemy();
            }

            if (!IsCachedDetectionUsable(targeting))
            {
                // 明确置空：避免把一个已经失效的引用长期挂在控制器上（它可能已被销毁）。
                cachedDetectionResult = null;
                return false;
            }

            enemy = cachedDetectionResult;
            return true;
        }

        /// <summary>
        /// 校验缓存的检测结果是否仍然可用。
        /// 三项检查都不做物理查询，只是属性比较与一次平方距离运算，因此可以每帧调用。
        /// </summary>
        /// <param name="targeting">本单位的索敌组件，调用方已保证非空。</param>
        private bool IsCachedDetectionUsable(TargetingComponent targeting)
        {
            if (cachedDetectionResult == null)
            {
                return false;
            }

            // 阵营 + 存活/未销毁/可选中：复用索敌组件的唯一判定入口，不在这里重写一遍规则。
            if (!targeting.IsEnemy(cachedDetectionResult) || !cachedDetectionResult.IsValidTarget)
            {
                return false;
            }

            Transform targetTransform = cachedDetectionResult.TargetTransform;
            if (targetTransform == null)
            {
                return false;
            }

            // 仍在索敌半径内 —— 与 FindNearestEnemy 的过滤条件保持完全一致。
            float radius = targeting.DetectionRadius;
            return (targetTransform.position - transform.position).sqrMagnitude <= radius * radius;
        }

        #endregion

        #region 卡死检测（供各状态共享）

        /// <summary>判定为卡住所需的最短持续时间（秒）。用 Mathf.Max 夹住下限，避免被配成 0 后每帧重下指令。</summary>
        public float StagnationDuration => Mathf.Max(0.1f, stagnationDuration);

        /// <summary>卡死判定的速度平方阈值。</summary>
        public float StagnationVelocitySqrThreshold => Mathf.Max(0f, stagnationVelocitySqrThreshold);

        /// <summary>
        /// 判断单位这一刻是否「几乎没在动」。
        ///
        /// 职责边界：本方法只回答「此刻速度是否低于阈值」，不回答「卡了多久」——
        /// 持续时间由调用方（状态）用自己的计时器累加。因为「本轮推进/追击已经僵持多久」
        /// 是状态内部的局部事实，放在控制器里会让两个状态互相污染对方的计时器。
        /// </summary>
        /// <param name="movement">本单位的移动组件，允许为 null（固定建筑）。</param>
        /// <returns>速度低于阈值返回 true。</returns>
        public bool IsStagnant(MovementComponent movement)
        {
            if (movement == null)
            {
                return false;
            }

            return movement.CurrentVelocity.sqrMagnitude < StagnationVelocitySqrThreshold;
        }

        #endregion

        /// <summary>
        /// Unity 生命周期入口：实例化状态机 + 获取实体引用。
        ///
        /// 只做这两件事，不在这里碰 entity.Health / entity.Targeting：
        /// 同一 GameObject 上多个组件的 Awake 执行顺序不确定，此时 EntityBase 可能尚未缓存好组件引用。
        /// 订阅死亡事件因此放到 InitializeAI（初始化注入点）中执行。
        /// </summary>
        private void Awake()
        {
            stateMachine = new StateMachine();

            if (entity == null)
            {
                entity = GetComponent<EntityBase>();
            }

            if (entity == null)
            {
                // 没有实体身份就无法读取阵营、组件与配置，AI 完全无法工作，属于致命配置错误。
                Debug.LogError(
                    $"[EntityAIController] {name} 上找不到 EntityBase，AI 已禁用。" +
                    "请确保 EntityBase 与 EntityAIController 挂在同一个 GameObject 上。", this);

                // 致命错误直接停用组件：避免每帧空转，也避免刷出大量后续报错。
                enabled = false;
            }
        }

        /// <summary>
        /// 生命周期兜底：预制体上的单位不需要任何外部代码调用，由这里自动完成 AI 初始化。
        /// 之所以放在 Start 而不是 Awake：EntityBase 在它自己的 Start 里才完成配置注入
        /// （生命值、移速、索敌半径、攻击配置），AI 必须等这些就绪后再初始化，
        /// 否则状态读到的攻击距离/索敌半径都是默认值，会直接导致状态跳转判断失准。
        /// </summary>
        private void Start()
        {
            if (hasInitialized)
            {
                // 动态创建的单位（MinionSpawner / 自动化测试）已显式调用过 InitializeAI。
                return;
            }

            InitializeAI(lanePath);
        }

        /// <summary>
        /// AI 初始化入口：创建五个状态 → 订阅死亡事件 → 启动状态机。
        /// 调用时机要求：必须在 EntityBase.Initialize(...) 之后调用，
        /// 否则状态读到的是尚未注入配置的组件（攻击距离为 0 会让 ChaseState 完全不可用）。
        /// </summary>
        /// <param name="path">本单位的推进路线，传 null 表示沿用 Inspector 上已配置的引用。</param>
        public void InitializeAI(LanePath path)
        {
            if (hasInitialized)
            {
                // 重复初始化会把已经推进到中途的状态机重置回起始状态（小兵会被打回起点），
                // 属于会破坏游戏表现的副作用，因此明确拒绝并提示调用方。
                Debug.LogWarning(
                    $"[EntityAIController] {name} 的 InitializeAI 被重复调用，本次调用已忽略。", this);
                return;
            }

            if (path != null)
            {
                lanePath = path;
            }

            if (entity == null)
            {
                Debug.LogError($"[EntityAIController] {name} 缺少 EntityBase 引用，AI 初始化失败。", this);
                enabled = false;
                return;
            }

            // 步骤 1：创建五个状态实例。每个状态都把本控制器作为上下文持有，
            // 从而能经 Owner 访问组件、经 TryEnterXxxState 请求切换。
            idleState = new IdleState(this);
            moveState = new MoveState(this);
            chaseState = new ChaseState(this);
            attackState = new AttackState(this);
            deadState = new DeadState(this);

            // 步骤 2：订阅死亡事件。必须在启动状态机之前完成，
            // 否则存在"状态机已跑起来、但死亡事件还没订阅上"的漏事件窗口。
            SubscribeDeathEvent();

            // 步骤 3：启动状态机。计划书要求初始状态为 MoveState（小兵出生即沿路线推进）。
            stateMachine.Initialize(moveState);

            hasInitialized = true;

            // 初始化日志无条件输出一次（每个单位仅一条），用于确认"路线是否真的注入成功"——
            // 这是阶段三验收"无敌人时能依次经过所有路线节点"最容易出问题的一环。
            Debug.Log(
                $"[EntityAIController] {name} AI 初始化完成，起始状态：{moveState.GetType().Name}" +
                $"{DescribeLane()}", this);

            // 步骤 4：兜底——若在初始化之前单位就已经死亡，OnDied 事件早已发出且不会再补发，
            // 这里主动补一次状态切换，避免"死掉的单位仍在推进"。与血条 UI"订阅后必须主动读一次当前血量"同一思路。
            if (entity.Health != null && entity.Health.IsDead)
            {
                Debug.LogWarning(
                    $"[EntityAIController] {name} 在 AI 初始化时已处于死亡状态，直接切入 DeadState。", this);
                SwitchState(deadState);

                // 兜底路径同样要清理尸体：否则"开局就死在场景里的单位"会永远立在那里，且永不销毁。
                HandleCorpseCleanup();
            }
        }

        /// <summary>
        /// 驱动状态机。放在 Update 中每帧调用——状态自身的 Tick 只做轻量判断与组件方法调用。
        /// </summary>
        private void Update()
        {
            // 感知节流的计时器统一在这里递减（而不是在 TryDetectEnemy 内部），
            // 这样即使某帧被多次调用也不会重复扣减，节流间隔才是准确的。
            detectionTimer -= Time.deltaTime;

            stateMachine?.Tick();
        }

        /// <summary>
        /// 组件销毁时退订死亡事件。
        /// 必要性：事件订阅会让 HealthComponent 持有本控制器的引用。若本组件先于实体被销毁而忘记退订，
        /// 之后实体死亡时会回调到一个已销毁的控制器，抛出 MissingReferenceException。
        /// </summary>
        private void OnDestroy()
        {
            UnsubscribeDeathEvent();
        }

        #region 目标有效性校验（供 Chase / Attack 状态共享）

        /// <summary>
        /// 取当前锁定的有效目标。
        ///
        /// 返回 false 表示"没有可用目标"，涵盖：未锁定、GameObject 已被销毁、已死亡、不可选中、阵营变化（被策反）。
        /// 校验失败时会【顺手清空】索敌组件里的当前目标——失效目标不应残留在组件中，
        /// 否则调用方每帧都会重复读到同一个脏数据。
        ///
        /// 实现上直接复用 TargetingComponent.ClearInvalidTarget()，而不是在本类重写一套合法性规则：
        /// 目标合法性（存活 / 未被销毁 / 可选中 / 阵营）在本项目要求"只有一处判断入口"，
        /// 在控制器里再实现一遍，迟早会出现两处口径不一致，
        /// 进而导致"追击认为目标有效、攻击认为无效"的状态来回切换。
        /// </summary>
        /// <param name="target">校验通过时输出当前目标，否则为 null。</param>
        /// <returns>存在有效目标返回 true。</returns>
        public bool TryGetValidTarget(out ITargetable target)
        {
            target = null;

            EntityBase owner = entity;
            if (owner == null)
            {
                return false;
            }

            TargetingComponent targeting = owner.Targeting;
            if (targeting == null)
            {
                return false;
            }

            // 校验 + 清理交给索敌组件完成（内部已包含"对象是否已被销毁"的接口存活检查）。
            targeting.ClearInvalidTarget();

            ITargetable current = targeting.CurrentTarget;
            if (current == null)
            {
                return false;
            }

            target = current;
            return true;
        }

        #endregion

        #region 状态切换请求（供各状态实现调用）

        /// <summary>请求切换到待机状态。由 MoveState（路线走完）与 AttackState（目标失效）调用。</summary>
        /// <returns>确实发生了状态切换返回 true。</returns>
        public bool TryEnterIdleState()
        {
            return SwitchState(idleState);
        }

        /// <summary>请求切换到推进状态。由 IdleState（无敌人）与 ChaseState（放弃追击）调用。</summary>
        /// <returns>确实发生了状态切换返回 true。</returns>
        public bool TryEnterMoveState()
        {
            return SwitchState(moveState);
        }

        /// <summary>
        /// 请求切换到追击状态。由 IdleState / MoveState 在"发现敌人"时调用。
        /// 调用前必须已把目标写入 TargetingComponent，否则追击状态读不到目标。
        ///
        /// 【防震荡闸门】不具备追击能力时直接拒绝（见 CanChase）：
        /// 放行只会让 ChaseState 立刻退回推进状态，与"发现敌人就转追击"构成每帧互切。
        /// </summary>
        /// <returns>确实发生了状态切换返回 true；被拒绝返回 false。</returns>
        public bool TryEnterChaseState()
        {
            if (!CanChase)
            {
                ReportCannotChaseOnce();
                return false;
            }

            // 【牵引闸门】已被牵引出界时拒绝重新追击。
            // 必要性：ChaseState 判定出界后会清空目标并回到 MoveState，而 MoveState 下一帧
            // 又会立刻感知到同一个敌人并再次请求追击。若这里放行，就会形成
            // Move → Chase（判定出界）→ Move → Chase 的每帧互切，牵引极限等于形同虚设。
            // 拒绝之后单位会老实走回兵线，抵达路径点时由 MoveState 清除锚点，重新获得追击资格。
            if (IsBeyondChaseLeash)
            {
                ReportBeyondLeashOnce();
                return false;
            }

            return SwitchState(chaseState);
        }

        /// <summary>请求切换到攻击状态。由 ChaseState 在"进入攻击距离"时调用。</summary>
        /// <returns>确实发生了状态切换返回 true。</returns>
        public bool TryEnterAttackState()
        {
            return SwitchState(attackState);
        }

        /// <summary>
        /// 状态切换的唯一出口：统一处理"状态机未就绪"的降级，并把"是否真的切了"反馈给调用方。
        /// 之所以不把 stateMachine.ChangeState 直接暴露给状态使用：
        /// 状态需要据此决定本帧是否还要继续做后续逻辑（例如"转追击被拒绝则继续推进"），
        /// 而 ChangeState 自身不返回任何信息。
        ///
        /// 注意：本类【不再输出状态切换日志】——状态变化的可视化与日志由 Debug/FSMDebugView 统一承担，
        /// 避免同一事件在两处重复打印（单位一多，Console 会立刻不可读）。
        /// </summary>
        /// <param name="targetState">目标状态实例。</param>
        /// <returns>确实发生了状态切换返回 true。</returns>
        private bool SwitchState(IState targetState)
        {
            // 状态机尚未启动（InitializeAI 未被调用或初始化失败）时不存在"当前状态"可供切换，
            // 静默返回而不是报错——未初始化属于合法的启动时序。
            // targetState 为 null 属防御性检查：五个状态在 InitializeAI 中一并创建，正常流程不会为 null。
            if (!stateMachine.IsInitialized || targetState == null)
            {
                return false;
            }

            // 重复切入同一状态视为"未发生切换"，直接返回 false。
            // 提前判断而不是依赖 StateMachine 内部忽略：调用方需要的是准确的"有没有切"。
            if (ReferenceEquals(stateMachine.CurrentState, targetState))
            {
                return false;
            }

            stateMachine.ChangeState(targetState);
            return true;
        }

        #endregion

        #region 死亡事件编排

        /// <summary>
        /// 订阅实体死亡事件。
        /// 采用"先退订再订阅"的写法，保证本方法重复调用时不会造成同一次死亡触发多遍处理。
        /// </summary>
        private void SubscribeDeathEvent()
        {
            HealthComponent health = entity.Health;

            if (health == null)
            {
                Debug.LogError(
                    $"[EntityAIController] {name} 的 EntityBase 上没有 HealthComponent，" +
                    "无法监听死亡事件——该单位死亡后 AI 不会停止。请为可战斗单位补齐生命组件。", this);
                return;
            }

            health.OnDied -= HandleOwnerDied;
            health.OnDied += HandleOwnerDied;

            // 记录订阅对象：退订时必须对同一个实例操作，若此处不记录、退订时重新 GetComponent，
            // 一旦引用在运行期被替换就会退订到另一个对象上，留下悬空订阅。
            subscribedHealth = health;
        }

        /// <summary>退订实体死亡事件。</summary>
        private void UnsubscribeDeathEvent()
        {
            if (subscribedHealth == null)
            {
                return;
            }

            subscribedHealth.OnDied -= HandleOwnerDied;
            subscribedHealth = null;
        }

        /// <summary>
        /// 死亡事件回调：强制切入 DeadState。
        /// 这里【不做任何前置条件判断】——不检查当前处于哪个状态、不检查是否已经死亡，
        /// 因为死亡必须无条件生效。重复触发由 StateMachine.ChangeState 自身的"同一状态忽略"逻辑兜住，
        /// 因此即使 HealthComponent 出现重复广播，也只会执行一次 DeadState.Enter。
        /// </summary>
        private void HandleOwnerDied()
        {
            SwitchState(deadState);

            // 视觉收尾：立刻隐藏肉身 + 安排延时销毁（详见 HandleCorpseCleanup）。
            // 放在 SwitchState 之后：先让逻辑上的死亡收尾（DeadState.Enter 停寻路/禁碰撞/禁代理）完成，
            // 再动物体外观，两者互不依赖，但顺序固定下来后行为更容易推理。
            HandleCorpseCleanup();
        }

        /// <summary>
        /// 尸体清理（白盒期）：先让尸体立刻从画面上消失，再安排一次延时销毁。
        ///
        /// 【为什么"立刻隐藏"与"延时销毁"要分两步】
        /// 隐藏是即时的视觉反馈（战场上不该立着一排尸体）；而销毁需要一个短暂的停留期：
        /// 死亡瞬间的伤害飘字与击杀播报都还在播，飘字的锚点就在这个单位身上，
        /// 若当帧就把物体删掉，表现层的上下文会突然消失。因此先"看起来死了"，再"真的消失"。
        ///
        /// 【为什么不用 Destroy(gameObject, delay) 这个重载】它无法被查询、也无法取消，
        /// 排查时看不到"到底有没有安排销毁"。用协程可以显式记下句柄，便于防重入与调试。
        /// </summary>
        private void HandleCorpseCleanup()
        {
            if (entity == null)
            {
                return;
            }

            // 1. 立刻隐藏肉身（含全部子节点）。与英雄走同一个工具方法，保证两边的口径一致。
            EntityVisuals.SetRenderersEnabled(entity.gameObject, false);

            // 2. 安排延时销毁。
            //    已有句柄说明已经安排过：死亡事件本身只广播一次，但"初始化兜底"与"事件回调"两条路径
            //    都指向这里，这个判断让它们天然幂等。
            if (corpseCleanupRoutine == null)
            {
                corpseCleanupRoutine = StartCoroutine(DestroyCorpseAfterDelay());
            }
        }

        /// <summary>
        /// 延时销毁尸体，把它彻底移出内存与层级。
        ///
        /// 【必须用 yield break 而不是 return】本方法是迭代器（方法体内有 yield return），
        /// C# 不允许在迭代器块里写普通的 return;（会报 CS1622）。
        ///
        /// 【为什么不需要"物体是否已被销毁"的判空】物体被销毁时 Unity 会自动停止它的协程，
        /// 因此 yield 之后的代码只在物体仍然存活时才会执行。
        /// </summary>
        private IEnumerator DestroyCorpseAfterDelay()
        {
            yield return new WaitForSeconds(Mathf.Max(0f, corpseLingerSeconds));

            corpseCleanupRoutine = null;

            if (logDeathCleanup)
            {
                Debug.Log($"[EntityAIController] {name} 的尸体停留 {corpseLingerSeconds:F1} 秒结束，已销毁。", this);
            }

            // 销毁整个单位：EntityBase.OnDisable 会自动注销注册表，血条归还池、飘字与击杀统计同步退订。
            Destroy(gameObject);
        }

        #endregion

        /// <summary>
        /// 报告一次"请求追击但单位不具备追击能力"。
        /// 用一次性标记的原因：该请求可能每帧发生（敌人一直在索敌半径内），不加标记会刷屏。
        /// </summary>
        private void ReportCannotChaseOnce()
        {
            if (hasReportedCannotChase)
            {
                return;
            }

            hasReportedCannotChase = true;

            Debug.LogWarning(
                $"[EntityAIController] {name} 请求进入追击，但本单位不具备追击能力，已跳过追击。" +
                "追击需要同时满足两点：有有效攻击距离（CombatComponent 且已配置 AttackData）、" +
                "有 MovementComponent（用于靠近目标）。缺少任一项时进入追击都只会立刻退回，" +
                "并与推进状态形成每帧互切，因此在此拦下。", this);
        }

        /// <summary>
        /// 报告一次"已被牵引出界、拒绝追击"。
        /// 用一次性标记的原因：出界期间敌人可能一直待在索敌半径内，该请求会每帧发生。
        /// 这里用 Warning 而不是 Log：它属于"合法但可疑"的状态——单位会先走回兵线，
        /// 若这条日志反复出现在同一单位上，说明牵引极限配得过小，需要调整系数。
        /// </summary>
        private void ReportBeyondLeashOnce()
        {
            if (hasReportedBeyondLeash)
            {
                return;
            }

            hasReportedBeyondLeash = true;

            Debug.LogWarning(
                $"[EntityAIController] {name} 已被牵引出界（距追击起点超过 {ChaseLeashDistance:F1}），" +
                "拒绝重新进入追击，将先返回兵线。抵达路径点后会重新获得追击资格。", this);
        }

        /// <summary>生成路线配置的简短描述，仅用于初始化日志，便于确认路线是否真的注入成功。</summary>
        private string DescribeLane()
        {
            if (lanePath == null)
            {
                return "，未配置 LanePath（推进状态将无路可走）。";
            }

            int waypointCount = lanePath.Waypoints != null ? lanePath.Waypoints.Count : 0;
            return $"，路线 {lanePath.name}（{waypointCount} 个节点）。";
        }
    }
}
