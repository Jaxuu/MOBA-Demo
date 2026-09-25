using UnityEngine;
using UnityEngine.AI;
using MOBA.Components;
using MOBA.Core;

namespace MOBA.Controllers
{
    /// <summary>
    /// 玩家指令控制器：把「鼠标右键输入」翻译成「组件调用」。
    ///
    /// 职责边界：
    /// 1. 输入层：采集右键点击 → 射线拾取 → 判定这是「移动指令」还是「攻击指令」；
    /// 2. 执行层：持有攻击指令期间，每帧推进「靠近 → 进入射程 → 停下输出」这条最小闭环；
    /// 3. 不直接操作 NavMeshAgent（统一走 MovementComponent.MoveTo / Stop），
    ///    不自己结算伤害（统一走 CombatComponent.TryAttack），也不自己判断敌我
    ///    （统一走 TargetingComponent.IsEnemy），保证与 AI 单位共用同一套规则。
    ///
    /// 【为什么这里会出现「执行器」】英雄刻意不挂 EntityAIController（见 HeroController 的说明），
    /// 因此 FSM 的 Chase/Attack 两态不会为英雄工作。玩家下达的攻击指令需要一个执行者，
    /// 本类就承担这一最小职责。它与 FSM 的分工是明确的：
    ///   · FSM = 自主决策（自己索敌、自己决定追不追、有牵引极限与防卡死）；
    ///   · 本类 = 执行玩家显式指定的唯一目标，不索敌、不自动换目标、不设牵引极限
    ///     （玩家指令的语义就是「打这个」，追到天涯海角也是玩家的选择）。
    /// 两者互斥使用，绝不会挂在同一个单位上，因此不存在指令互相覆盖的问题。
    ///
    /// 【指令的存放处】攻击指令直接存放在 TargetingComponent.CurrentTarget 里，
    /// 本类【不】另存一份目标引用——全项目「当前目标」只能有一个存放处，否则迟早出现两份数据不一致。
    ///
    /// 输入方案说明：这里使用旧输入系统 Input.GetMouseButtonDown。
    /// 若项目在 Player Settings &gt; Active Input Handling 中只勾选了 "Input System Package (New)"，
    /// 该 API 会抛 InvalidOperationException，此时需改为 "Both" 或把本类改写为基于 InputAction 的实现。
    /// </summary>
    [DisallowMultipleComponent]
    public class PlayerCommandController : MonoBehaviour
    {
        [Header("受控单位")]
        [Tooltip("当前被玩家操控的实体（玩家英雄）。留空时自动取同一 GameObject 上的 EntityBase。")]
        [SerializeField] private EntityBase controlledEntity;

        [Header("地面检测")]
        [Tooltip("地面所在的 Layer 名称，需与 Edit > Project Settings > Tags and Layers 中的命名完全一致。")]
        [SerializeField] private string groundLayerName = "Ground";

        [Tooltip("参与指令拾取的 Layer。默认 Physics.DefaultRaycastLayers（已排除 Ignore Raycast 层）。" +
                 "必须包含单位所在的 Layer 与地面 Layer，否则对应的指令拾取不到。")]
        [SerializeField] private LayerMask commandLayers = Physics.DefaultRaycastLayers;

        [Tooltip("射线最大检测距离；过小会导致远端点不到地面。")]
        [SerializeField] private float maxRayDistance = 500f;

        [Header("落点修正")]
        [Tooltip("开启后会把点击点投射到最近的 NavMesh 可通行区域，避免点到障碍物内部或地图外时单位卡住。")]
        [SerializeField] private bool snapToNavMesh = true;

        [Tooltip("投射搜索半径：点击点在此半径内找不到 NavMesh 时视为无效指令。")]
        [SerializeField] private float navMeshSampleDistance = 1f;

        [Header("攻击指令")]
        [Tooltip("追击时「目标移动多少距离才重新寻路」的阈值。\n" +
                 "NavMeshAgent.SetDestination 每次都会重新算路，逐帧重下代价明显；\n" +
                 "填 0 表示每帧重算（不推荐）。")]
        [Min(0f)]
        [SerializeField] private float chaseRepathDistance = 0.25f;

        [Header("调试")]
        [Tooltip("在 Scene 视图中绘制点击射线，便于确认 Layer 配置是否正确。")]
        [SerializeField] private bool drawDebugRay = true;

        [Tooltip("在 Console 输出每次成功下达的移动指令及目标坐标。")]
        [SerializeField] private bool logMoveCommands = true;

        [Tooltip("在 Console 输出每次成功下达的攻击指令。")]
        [SerializeField] private bool logAttackCommands = true;

        [Tooltip("在 Scene 视图中绘制最近一次移动指令的落点标记与当前攻击目标连线。")]
        [SerializeField] private bool drawCommandGizmos = true;

        /// <summary>
        /// 射线拾取缓冲区。预分配后复用，避免每次点击都分配数组（本项目要求稳态 0 GC Alloc）。
        /// 容量 16 对「一条穿过战场的点击射线」而言非常宽裕。
        /// </summary>
        private readonly RaycastHit[] hitBuffer = new RaycastHit[16];

        /// <summary>缓存的主相机。Camera.main 内部是带标签的查找，不该放在每帧调用。</summary>
        private Camera mainCamera;

        /// <summary>地面 Layer 的索引。用于判断某个命中是不是地面，而不是每帧调 LayerMask.NameToLayer（会做字符串解析）。</summary>
        private int groundLayerIndex;

        /// <summary>初始化是否成功。任一必需依赖缺失都会置为 false，Update 直接短路，避免每帧刷错误日志。</summary>
        private bool isInitialized;

        /// <summary>是否已经提示过「缺少移动组件」，用于防止对建筑类实体重复告警。</summary>
        private bool hasWarnedMissingMovement;

        /// <summary>是否已经提示过「缺少索敌组件」（没有它就无法判断敌我，攻击指令不可用）。</summary>
        private bool hasWarnedMissingTargeting;

        /// <summary>是否已经提示过「缺少战斗组件」（没有它就无法结算伤害）。</summary>
        private bool hasWarnedMissingCombat;

        /// <summary>是否已经提示过「射线缓冲区已满」。</summary>
        private bool hasWarnedHitBufferFull;

        /// <summary>是否已下达过至少一条移动指令，决定 Gizmos 是否需要绘制落点标记。</summary>
        private bool hasLastCommand;

        /// <summary>最近一次射线命中的原始地面点（未做 NavMesh 吸附），用于可视化「点击位置 vs 实际落点」的偏差。</summary>
        private Vector3 lastHitPoint;

        /// <summary>最近一次实际下达给单位的最终目标点（可能经过 NavMesh 吸附）。</summary>
        private Vector3 lastCommandPoint;

        /// <summary>当前攻击指令对应的目标。仅用于识别「目标是否换人了」，真正的指令存放处仍是 TargetingComponent。</summary>
        private ITargetable chaseTarget;

        /// <summary>是否已针对当前目标成功下达过一次追击指令。失败时不置位，下一帧自然重试（与 FSM 的「已下令」标记同一思路）。</summary>
        private bool hasIssuedChase;

        /// <summary>上一次下达追击指令时的目标位置，用于按 chaseRepathDistance 判断是否需要重新寻路。</summary>
        private Vector3 lastChaseDestination;

        /// <summary>进入攻击距离后是否已经调用过 Stop()。避免每帧重复清路径。</summary>
        private bool hasStoppedForAttack;

        /// <summary>当前受控实体，供外部（例如选人逻辑）读取。</summary>
        public EntityBase ControlledEntity => controlledEntity;

        /// <summary>最近一次移动指令的目标点，供调试视图或自动化测试读取。</summary>
        public Vector3 LastCommandPoint => lastCommandPoint;

        /// <summary>当前是否存在未完成的攻击指令，供调试视图读取。</summary>
        public bool HasAttackOrder => chaseTarget != null;

        /// <summary>
        /// 缓存相机、解析地面 Layer，并做一次性依赖校验。
        /// 把「配置错误」集中在初始化阶段报出，比在 Update 里每帧报错更容易定位问题。
        /// </summary>
        private void Awake()
        {
            mainCamera = Camera.main;
            if (mainCamera == null)
            {
                Debug.LogError("[PlayerCommandController] 场景中找不到 MainCamera（请确认相机带有 MainCamera 标签）。输入控制已禁用。", this);
                enabled = false;
                return;
            }

            // NameToLayer 在 Layer 不存在时返回 -1，必须显式判断，否则后续判断会静默失效。
            int groundLayer = LayerMask.NameToLayer(groundLayerName);
            if (groundLayer < 0)
            {
                Debug.LogError(
                    $"[PlayerCommandController] 未找到名为 \"{groundLayerName}\" 的 Layer。" +
                    "请到 Edit > Project Settings > Tags and Layers 中新增该 Layer，并把它分配给地面对象。输入控制已禁用。", this);
                enabled = false;
                return;
            }

            groundLayerIndex = groundLayer;

            // 地面 Layer 被排除在拾取掩码之外时，移动指令永远拾取不到地面（表现为「右键完全没反应」）。
            // 这属于纯配置问题且毫无报错，必须在这里显式拦下。
            if ((commandLayers.value & (1 << groundLayerIndex)) == 0)
            {
                Debug.LogError(
                    $"[PlayerCommandController] Command Layers 未包含地面 Layer「{groundLayerName}」，" +
                    "移动指令将永远拾取不到地面。请把该 Layer 勾选进 Command Layers。输入控制已禁用。", this);
                enabled = false;
                return;
            }

            // 同物体自解析：本控制器既可挂在英雄身上（一键组装工具的默认做法），
            // 也可挂在场景级管理器上，通过 Inspector 或 SetControlledEntity 指定受控单位。
            if (controlledEntity == null)
            {
                controlledEntity = GetComponent<EntityBase>();
            }

            if (controlledEntity == null)
            {
                // 允许运行时再通过 SetControlledEntity 赋值（例如对局开始时才生成英雄），因此这里只警告不报错。
                Debug.LogWarning("[PlayerCommandController] 尚未指定受控实体，在赋值前不会响应任何输入。", this);
            }

            isInitialized = true;
        }

        /// <summary>
        /// 每帧处理：先采集输入（新指令当帧生效），再推进已有的攻击指令。
        /// </summary>
        private void Update()
        {
            if (!isInitialized)
            {
                return;
            }

            // 右键 = 指令键（点地面为移动，点敌方单位为攻击）。1 代表鼠标右键，0 为左键，2 为中键。
            if (Input.GetMouseButtonDown(1))
            {
                HandleRightClick();
            }

            ExecuteAttackOrder();
        }

        /// <summary>
        /// 处理一次右键点击：射线拾取 → 判定指令类型 → 下达指令。
        ///
        /// 判定规则：在全部命中里分别找「最近的敌方单位」与「最近的地面落点」，
        /// 谁离相机更近就以谁为准（单位站在地面上时，射线先穿过单位碰撞体再打到地面，
        /// 因此「点到单位」与「点到单位脚下的地面」能靠命中距离自然区分开）。
        /// </summary>
        private void HandleRightClick()
        {
            // 空引用保护：未指定受控实体时直接忽略输入，不抛异常也不刷日志（Awake 已警告过一次）。
            if (controlledEntity == null)
            {
                return;
            }

            // 已死亡的受控单位不再响应任何指令（README 3.1：英雄死亡后「不触发自动复活，原地静止」）。
            // 为什么必须显式拦：死亡收尾只是停止了寻路，代理与输入通道的关闭存在先后，
            // 若这里不拦，玩家仍可能把尸体拖着满地图跑，与「原地静止」的需求直接冲突。
            HealthComponent controlledHealth = controlledEntity.Health;
            if (controlledHealth != null && controlledHealth.IsDead)
            {
                return;
            }

            // 空引用保护：防御塔、基地等固定建筑没有移动组件，此时忽略指令。
            if (controlledEntity.Movement == null)
            {
                ReportMissingMovementOnce();
                return;
            }

            // 相机可能在运行期被销毁/替换（例如切场景），这里再兜一层空引用保护，避免每帧抛异常。
            if (mainCamera == null)
            {
                Debug.LogError("[PlayerCommandController] 主相机引用已失效（可能被销毁），输入控制已禁用。", this);
                enabled = false;
                return;
            }

            // 由屏幕点构造射线：MOBA 是顶视角，必须用透视/正交相机配合屏幕坐标做世界拾取。
            Ray ray = mainCamera.ScreenPointToRay(Input.mousePosition);

            if (drawDebugRay)
            {
                // 在 Scene 视图画出射线，方便直观确认是否打到了目标或地面。
                Debug.DrawRay(ray.origin, ray.direction * maxRayDistance, Color.yellow, 0.5f);
            }

            // 用 NonAlloc 版本：缓冲区复用，点击不产生 GC Alloc。
            int hitCount = Physics.RaycastNonAlloc(
                ray, hitBuffer, maxRayDistance, commandLayers, QueryTriggerInteraction.Ignore);

            if (hitCount >= hitBuffer.Length)
            {
                // 命中数顶到缓冲区上限，说明结果可能被截断（极端场景：相机贴脸或大量碰撞体重叠）。
                ReportHitBufferFullOnce();
            }

            EntityBase nearestEnemy = null;
            float nearestEnemySqrDistance = float.MaxValue;
            bool hasGroundPoint = false;
            Vector3 groundPoint = Vector3.zero;
            float nearestGroundSqrDistance = float.MaxValue;

            for (int i = 0; i < hitCount; i++)
            {
                RaycastHit hit = hitBuffer[i];
                Collider hitCollider = hit.collider;

                // 命中对象可能在本次扫描期间被销毁（理论上不会，但 Unity 的重载 == 能识别这种情况）。
                if (hitCollider == null)
                {
                    continue;
                }

                // 用 GetComponentInParent：单位碰撞体常挂在模型子节点上，而 EntityBase 位于根节点。
                EntityBase hitEntity = hitCollider.GetComponentInParent<EntityBase>();

                if (hitEntity != null)
                {
                    // 点到自己：忽略（不做自我移动，也不做自我攻击）。
                    if (hitEntity == controlledEntity)
                    {
                        continue;
                    }

                    // 点到友方：V1 没有友方指令（无治疗/增益/跟随），忽略该命中并继续看它身后的地面。
                    if (!IsEnemy(hitEntity))
                    {
                        continue;
                    }

                    float enemySqrDistance = (hit.point - ray.origin).sqrMagnitude;
                    if (enemySqrDistance < nearestEnemySqrDistance)
                    {
                        nearestEnemySqrDistance = enemySqrDistance;
                        nearestEnemy = hitEntity;
                    }

                    // 单位碰撞体不作为地面落点，继续扫描。
                    continue;
                }

                if (hitCollider.gameObject.layer != groundLayerIndex)
                {
                    continue;
                }

                float groundSqrDistance = (hit.point - ray.origin).sqrMagnitude;
                if (groundSqrDistance < nearestGroundSqrDistance)
                {
                    nearestGroundSqrDistance = groundSqrDistance;
                    groundPoint = hit.point;
                    hasGroundPoint = true;
                }
            }

            // 决策：敌方单位挡在地面之前 → 攻击指令；否则落到地面 → 移动指令。
            if (nearestEnemy != null && (!hasGroundPoint || nearestEnemySqrDistance <= nearestGroundSqrDistance))
            {
                IssueAttackOrder(nearestEnemy);
                return;
            }

            if (hasGroundPoint)
            {
                IssueMoveOrder(groundPoint);
                return;
            }

            // 什么都没命中（点到天空盒 / UI / Ignore Raycast 层）：静默忽略即可。
        }

        /// <summary>
        /// 下达移动指令。
        /// 落点会先吸附到最近的 NavMesh 可行走区域：点击点可能落在障碍物内部、桥下或 NavMesh 之外，
        /// 直接交给 SetDestination 会失败，从源头保证指令一定可执行。
        /// </summary>
        /// <param name="hitPoint">射线与地面的原始交点。</param>
        private void IssueMoveOrder(Vector3 hitPoint)
        {
            Vector3 destination = hitPoint;

            if (snapToNavMesh)
            {
                if (!NavMesh.SamplePosition(hitPoint, out NavMeshHit navHit, navMeshSampleDistance, NavMesh.AllAreas))
                {
                    Debug.LogWarning($"[PlayerCommandController] 点击位置 {hitPoint} 附近 {navMeshSampleDistance} 米内没有可行走区域，移动指令已忽略。", this);
                    return;
                }

                destination = navHit.position;
            }

            // 移动指令会撤销尚未完成的攻击指令（MOBA 语义：移动即放弃当前目标）。
            // 只在指令真正下达时才撤销——无效点击（点到地图外）不应顺手清掉玩家已有的攻击指令。
            ClearAttackOrder();

            // 记录落点用于 Gizmos 可视化（原始点击点与最终落点分开存，方便对比吸附偏差）。
            lastHitPoint = hitPoint;
            lastCommandPoint = destination;
            hasLastCommand = true;

            if (logMoveCommands)
            {
                // 输出目标坐标便于在 Console 直接核对：如果坐标明显异常（例如 y 值突变、离点击点很远），
                // 基本可以断定是 Layer 配错或 NavMesh 未烘焙。
                Debug.Log(
                    $"[PlayerCommandController] 移动指令 → {controlledEntity.name} | 目标点 {destination.ToString("F2")} " +
                    $"| 点击点 {hitPoint.ToString("F2")} | 距离 {Vector3.Distance(hitPoint, destination):F2}m", this);
            }

            // 最终只通过组件的公开方法下达意图，寻路细节由 MovementComponent 负责。
            // 返回值表示代理是否接受该目标：失败时 MovementComponent 内部已有一次性告警，这里不重复打印。
            controlledEntity.Movement.MoveTo(destination);
        }

        /// <summary>
        /// 下达攻击指令：把目标写入 TargetingComponent（全项目「当前目标」的唯一存放处）。
        /// 目标合法性（敌方 / 存活 / 可选中 / 未被销毁）由 SetTarget 内部统一校验，本类不重写这套规则。
        /// </summary>
        /// <param name="target">点击到的敌方单位。</param>
        private void IssueAttackOrder(EntityBase target)
        {
            if (target == null)
            {
                return;
            }

            TargetingComponent targeting = controlledEntity.Targeting;
            if (targeting == null)
            {
                ReportMissingTargetingOnce();
                return;
            }

            targeting.SetTarget(target);

            if (!ReferenceEquals(targeting.CurrentTarget, target))
            {
                // SetTarget 校验未通过（不是敌人 / 已死亡 / 不可选中），指令未生效。
                // SetTarget 自身会打印告警，这里不再重复。
                return;
            }

            // 新指令：作废上一次的追击进度，强制下一帧重新下达一次移动指令。
            chaseTarget = target;
            hasIssuedChase = false;
            hasStoppedForAttack = false;

            if (logAttackCommands)
            {
                Debug.Log(
                    $"[PlayerCommandController] 攻击指令 → {controlledEntity.name} | 目标 {target.name}（阵营 {target.Team}）", this);
            }
        }

        /// <summary>
        /// 推进当前的攻击指令，实现「靠近 → 进入射程 → 停下输出」的最小闭环。
        ///
        /// 每帧固定三步：
        /// 1. 校验目标是否仍然有效（复用 ClearInvalidTarget，全项目唯一的目标合法性入口）；
        /// 2. 已在射程内 → 停一次并调用 TryAttack（冷却与距离由 CombatComponent 内部判断）；
        /// 3. 在射程外 → 追击（目标移动超过阈值或尚未成功下令时重新寻路）。
        /// </summary>
        private void ExecuteAttackOrder()
        {
            if (controlledEntity == null)
            {
                return;
            }

            TargetingComponent targeting = controlledEntity.Targeting;
            if (targeting == null)
            {
                return;
            }

            // 目标合法性校验：内部含「已销毁 / 已死亡 / 不可选中 / 阵营变化」四项判断，且不含任何物理查询，可每帧调用。
            targeting.ClearInvalidTarget();

            ITargetable target = targeting.CurrentTarget;

            if (target == null)
            {
                // 没有指令（或指令刚被判定失效）：清掉追击进度，本帧无事可做。
                ResetChaseState();
                return;
            }

            Transform targetTransform = target.TargetTransform;
            if (targetTransform == null)
            {
                // Transform 取不到说明目标对象状态异常，按指令失效处理。
                ClearAttackOrder();
                return;
            }

            if (!ReferenceEquals(target, chaseTarget))
            {
                // 换了目标：上一次的追击进度作废，必须重新下达一次移动指令。
                chaseTarget = target;
                hasIssuedChase = false;
                hasStoppedForAttack = false;
            }

            CombatComponent combat = controlledEntity.Combat;
            if (combat == null)
            {
                ReportMissingCombatOnce();
                ClearAttackOrder();
                return;
            }

            // 移动组件在这里就解析并拦下，而不是等到「射程外」分支再判：
            // 射程内分支要调用 movement.Stop()，若等到后面才判空，一个没有移动组件的单位
            // （例如把本控制器挂在防御塔上、或目标由外部代码 SetTarget 写入）会在射程内每帧抛空引用。
            // 提前到分支之前，两条路径共用同一次判空。
            MovementComponent movement = controlledEntity.Movement;
            if (movement == null)
            {
                ReportMissingMovementOnce();
                ClearAttackOrder();
                return;
            }

            // 用平方距离比较，省掉一次开方。
            float sqrDistance = (targetTransform.position - transform.position).sqrMagnitude;
            float attackRange = combat.AttackRange;

            if (sqrDistance <= attackRange * attackRange)
            {
                // 进入射程：先停一次（清掉残留路径，避免一边输出一边被旧路径拖走），再交由 CombatComponent 结算。
                if (!hasStoppedForAttack)
                {
                    movement.Stop();
                    hasStoppedForAttack = true;
                }

                // TryAttack 内部处理冷却与距离判定，不满足条件时返回 false 且无任何副作用，因此这里无需重复判断。
                combat.TryAttack(target);
                return;
            }

            // 记住「刚才是否已经停下输出」：进入射程时调过 Stop()，它会清空代理的路径与目的地，
            // 因此一旦重新落到射程外，必须无条件重下一次移动指令 ——
            // 否则会出现「目标只微移出射程（位移小于 chaseRepathDistance）→ 既不满足重寻路阈值、
            // 代理又已经没有目的地 → 英雄原地站着打不着也追不上」的静默卡死。
            bool wasAttackingInPlace = hasStoppedForAttack;
            hasStoppedForAttack = false;

            Vector3 targetPosition = targetTransform.position;
            float repathDistance = Mathf.Max(0f, chaseRepathDistance);

            // 需要重新寻路的三种情况：刚才停下输出过、尚未成功下达过指令（含上一次失败）、目标已移动超过阈值。
            bool needsRepath = wasAttackingInPlace || !hasIssuedChase ||
                (targetPosition - lastChaseDestination).sqrMagnitude > repathDistance * repathDistance;

            if (needsRepath)
            {
                if (movement.MoveTo(targetPosition))
                {
                    hasIssuedChase = true;
                    lastChaseDestination = targetPosition;
                }

                // MoveTo 失败时不置位，下一帧自然重试（代理被移出 NavMesh 等瞬时问题可自愈）。
                return;
            }

            // 已经走到目标点却仍打不到：目标多半在障碍物另一侧或落在不可达区域。
            // 继续每帧重下指令只会空转，这里放弃指令并告警一次，把决定权交回玩家。
            if (movement.HasReachedDestination())
            {
                Debug.LogWarning(
                    $"[PlayerCommandController] {controlledEntity.name} 已抵达 {targetPosition.ToString("F2")} " +
                    $"但仍不在攻击距离内（目标可能位于障碍物另一侧或不可达区域），攻击指令已放弃。", this);
                ClearAttackOrder();
            }
        }

        /// <summary>
        /// 撤销当前攻击指令：清空目标并复位追击进度。
        /// 注意【不】停止移动——调用方（移动指令）会立刻下达新的目的地；
        /// 目标死亡而指令自然失效时保留既有路径，英雄会走到目标最后的位置停下，这是 MOBA 里符合直觉的表现。
        /// </summary>
        private void ClearAttackOrder()
        {
            ResetChaseState();

            if (controlledEntity == null)
            {
                return;
            }

            TargetingComponent targeting = controlledEntity.Targeting;
            if (targeting != null)
            {
                targeting.ClearTarget();
            }
        }

        /// <summary>复位追击进度（目标引用 + 已下令标记 + 已停止标记）。</summary>
        private void ResetChaseState()
        {
            chaseTarget = null;
            hasIssuedChase = false;
            hasStoppedForAttack = false;
        }

        /// <summary>
        /// 敌我校验。优先复用 TargetingComponent 的规则，保证「能锁定」和「能攻击」用的是同一套判断；
        /// 没有索敌组件时退化为直接比较阵营，但【中立阵营的排除必须保持一致】——
        /// 否则会出现「拾取认为不是敌人、攻击却打得出去」的规则分叉（README 2.2：不可攻击中立单位）。
        /// </summary>
        private bool IsEnemy(EntityBase candidate)
        {
            if (candidate == null || controlledEntity == null)
            {
                return false;
            }

            TargetingComponent targeting = controlledEntity.Targeting;
            if (targeting != null)
            {
                return targeting.IsEnemy(candidate);
            }

            // 退化路径：与 CombatComponent.IsEnemy 的退化规则逐条对齐。
            if (candidate.Team == TeamType.Neutral)
            {
                return false;
            }

            return candidate.Team != controlledEntity.Team;
        }

        /// <summary>
        /// 运行时切换受控单位。典型用途：对局开始时英雄才被实例化，或后续做英雄切换。
        /// </summary>
        /// <param name="entity">新的受控实体，允许传 null 表示解除控制。</param>
        public void SetControlledEntity(EntityBase entity)
        {
            controlledEntity = entity;

            // 换人等于丢弃旧单位的全部指令状态：不清理会让新单位继承旧单位的追击进度。
            ResetChaseState();
            hasWarnedMissingMovement = false;
            hasWarnedMissingTargeting = false;
            hasWarnedMissingCombat = false;
        }

        #region 一次性告警

        /// <summary>报告一次「受控实体没有移动组件」。用一次性标记防刷屏。</summary>
        private void ReportMissingMovementOnce()
        {
            if (hasWarnedMissingMovement)
            {
                return;
            }

            hasWarnedMissingMovement = true;
            Debug.LogWarning(
                $"[PlayerCommandController] 受控实体 {controlledEntity.name} 没有 MovementComponent，无法执行移动指令。", this);
        }

        /// <summary>报告一次「受控实体没有索敌组件」。没有它就无法判断敌我，攻击指令不可用。</summary>
        private void ReportMissingTargetingOnce()
        {
            if (hasWarnedMissingTargeting)
            {
                return;
            }

            hasWarnedMissingTargeting = true;
            Debug.LogWarning(
                $"[PlayerCommandController] 受控实体 {controlledEntity.name} 没有 TargetingComponent，" +
                "无法判断敌我，攻击指令不可用。请为该单位补齐索敌组件。", this);
        }

        /// <summary>报告一次「受控实体没有战斗组件」。</summary>
        private void ReportMissingCombatOnce()
        {
            if (hasWarnedMissingCombat)
            {
                return;
            }

            hasWarnedMissingCombat = true;
            Debug.LogWarning(
                $"[PlayerCommandController] 受控实体 {controlledEntity.name} 没有 CombatComponent，攻击指令无法结算伤害。", this);
        }

        /// <summary>报告一次「射线缓冲区已满」，说明拾取结果可能被截断。</summary>
        private void ReportHitBufferFullOnce()
        {
            if (hasWarnedHitBufferFull)
            {
                return;
            }

            hasWarnedHitBufferFull = true;
            Debug.LogWarning(
                $"[PlayerCommandController] 单次点击的射线命中数达到缓冲区上限（{hitBuffer.Length}），" +
                "拾取结果可能被截断。若确实存在大量碰撞体重叠，请调大 hitBuffer 容量。", this);
        }

        #endregion

        /// <summary>
        /// Scene 视图可视化：绘制移动指令落点与当前攻击目标。
        /// 说明：这些数据只存在于运行期内存中，退出播放模式后标记会消失（Unity 的 Gizmos 不持久化运行时状态）。
        /// </summary>
        private void OnDrawGizmos()
        {
            if (!drawCommandGizmos)
            {
                return;
            }

            // 抬升 0.1 米，避免标记被地面 Z-Fighting 吃掉。
            Vector3 lift = Vector3.up * 0.1f;

            if (hasLastCommand)
            {
                // 黄色线框球 = 鼠标原始点击点（未吸附）。
                Gizmos.color = Color.yellow;
                Gizmos.DrawWireSphere(lastHitPoint + lift, 0.25f);

                // 绿色线框球 = 实际下达给单位的目标点（吸附后）。
                Gizmos.color = Color.green;
                Gizmos.DrawWireSphere(lastCommandPoint + lift, 0.4f);

                // 两球之间的连线 = NavMesh 吸附造成的偏差；理想情况下这条线应该很短。
                Gizmos.color = Color.magenta;
                Gizmos.DrawLine(lastHitPoint + lift, lastCommandPoint + lift);

                // 从受控单位连到目标点，直观展示「这一指令让单位往哪走」。
                if (controlledEntity != null)
                {
                    Gizmos.color = Color.cyan;
                    Gizmos.DrawLine(controlledEntity.transform.position + lift, lastCommandPoint + lift);
                }
            }

            DrawAttackOrderGizmos(lift);
        }

        /// <summary>
        /// 绘制当前攻击指令：受控单位 → 目标 的红色连线，以及目标身上的标记。
        /// 注意编辑模式下 EntityBase.Awake 不会执行，其组件属性恒为 null，因此这里必须判空——
        /// 否则一进入编辑器就会抛空引用（这也是本段逻辑不放在 OnDrawGizmosSelected 的原因：
        /// 玩家需要随时看到「我的英雄正在打谁」，而不是选中它才看得到）。
        /// </summary>
        private void DrawAttackOrderGizmos(Vector3 lift)
        {
            if (controlledEntity == null)
            {
                return;
            }

            TargetingComponent targeting = controlledEntity.Targeting;
            ITargetable target = targeting != null ? targeting.CurrentTarget : null;
            if (target == null)
            {
                return;
            }

            Transform targetTransform = target.TargetTransform;
            if (targetTransform == null)
            {
                return;
            }

            Gizmos.color = Color.red;
            Gizmos.DrawLine(controlledEntity.transform.position + lift, targetTransform.position + lift);
            Gizmos.DrawWireSphere(targetTransform.position + lift, 0.5f);
        }
    }
}
