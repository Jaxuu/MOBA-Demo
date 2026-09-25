using UnityEngine;
using UnityEngine.AI;
using MOBA.Components;
using MOBA.Core;

namespace MOBA.Controllers
{
    /// <summary>
    /// 玩家指令控制器：把「鼠标输入」翻译成「组件调用」。
    ///
    /// 职责边界：
    /// 1. 只负责采集输入 → 转成世界坐标 → 调用 MovementComponent.MoveTo()；
    /// 2. 不直接操作 NavMeshAgent，不修改任何组件内部状态；
    /// 3. 不处理选中目标、攻击指令（那是阶段二的职责，后续在此扩展或拆出独立控制器）。
    ///
    /// 输入方案说明：这里使用旧输入系统 Input.GetMouseButtonDown。
    /// 若项目在 Player Settings > Active Input Handling 中只勾选了 "Input System Package (New)"，
    /// 该 API 会抛 InvalidOperationException，此时需改为 "Both" 或把本类改写为基于 InputAction 的实现。
    ///
    /// 为什么本类【不】加 [RequireComponent(typeof(EntityBase))]：
    /// RequireComponent 只能约束"同一个 GameObject 上必须存在的组件"，
    /// 而本控制器设计为挂在场景级管理器对象上、通过 Inspector 引用指向英雄（便于对局开始时再赋值、以及后续切换英雄）。
    /// 若强行声明 RequireComponent(EntityBase)，Unity 会要求控制器与英雄必须同物体，反而限制了对局流程的可扩展性。
    /// 因此这里改用 Inspector 引用 + Awake 空引用校验来保证安全。
    /// </summary>
    [DisallowMultipleComponent]
    public class PlayerCommandController : MonoBehaviour
    {
        [Header("受控单位")]
        [Tooltip("当前被玩家操控的实体（玩家英雄）。未赋值时本控制器不会发出任何指令。")]
        [SerializeField] private EntityBase controlledEntity;

        [Header("地面检测")]
        [Tooltip("地面所在的 Layer 名称，需与 Edit > Project Settings > Tags and Layers 中的命名完全一致。")]
        [SerializeField] private string groundLayerName = "Ground";

        [Tooltip("射线最大检测距离；过小会导致远端点不到地面。")]
        [SerializeField] private float maxRayDistance = 500f;

        [Header("落点修正")]
        [Tooltip("开启后会把点击点投射到最近的 NavMesh 可通行区域，避免点到障碍物内部或地图外时单位卡住。")]
        [SerializeField] private bool snapToNavMesh = true;

        [Tooltip("投射搜索半径：点击点在此半径内找不到 NavMesh 时视为无效指令。")]
        [SerializeField] private float navMeshSampleDistance = 1f;

        [Header("调试")]
        [Tooltip("在 Scene 视图中绘制点击射线，便于确认 Layer 配置是否正确。")]
        [SerializeField] private bool drawDebugRay = true;

        [Tooltip("在 Console 输出每次成功下达的移动指令及目标坐标。")]
        [SerializeField] private bool logMoveCommands = true;

        [Tooltip("在 Scene 视图中绘制最近一次移动指令的落点标记与连线。")]
        [SerializeField] private bool drawCommandGizmos = true;

        /// <summary>缓存的主相机。Camera.main 内部是带标签的查找，不该放在每帧调用。</summary>
        private Camera mainCamera;

        /// <summary>地面 Layer 的位掩码。用位掩码而不是每帧调 LayerMask.GetMask（后者会做字符串解析）。</summary>
        private int groundLayerMask;

        /// <summary>初始化是否成功。任一必需依赖缺失都会置为 false，Update 直接短路，避免每帧刷错误日志。</summary>
        private bool isInitialized;

        /// <summary>是否已经提示过"缺少移动组件"，用于防止对建筑类实体重复告警。</summary>
        private bool hasWarnedMissingMovement;

        /// <summary>是否已下达过至少一条指令，决定 Gizmos 是否需要绘制。</summary>
        private bool hasLastCommand;

        /// <summary>最近一次射线命中的原始地面点（未做 NavMesh 吸附），用于可视化"点击位置 vs 实际落点"的偏差。</summary>
        private Vector3 lastHitPoint;

        /// <summary>最近一次实际下达给单位的最终目标点（可能经过 NavMesh 吸附）。</summary>
        private Vector3 lastCommandPoint;

        /// <summary>当前受控实体，供外部（例如选人逻辑）读取。</summary>
        public EntityBase ControlledEntity => controlledEntity;

        /// <summary>最近一次移动指令的目标点，供调试视图或自动化测试读取。</summary>
        public Vector3 LastCommandPoint => lastCommandPoint;

        /// <summary>
        /// 缓存相机、解析地面 Layer，并做一次性依赖校验。
        /// 把"配置错误"集中在初始化阶段报出，比在 Update 里每帧报错更容易定位问题。
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

            // NameToLayer 在 Layer 不存在时返回 -1，必须显式判断，否则掩码会变成 1 << -1 而静默失效。
            int groundLayer = LayerMask.NameToLayer(groundLayerName);
            if (groundLayer < 0)
            {
                Debug.LogError(
                    $"[PlayerCommandController] 未找到名为 \"{groundLayerName}\" 的 Layer。" +
                    "请到 Edit > Project Settings > Tags and Layers 中新增该 Layer，并把它分配给地面对象。输入控制已禁用。", this);
                enabled = false;
                return;
            }

            groundLayerMask = 1 << groundLayer;

            if (controlledEntity == null)
            {
                // 允许运行时再通过 SetControlledEntity 赋值（例如对局开始时才生成英雄），因此这里只警告不报错。
                Debug.LogWarning("[PlayerCommandController] 尚未指定受控实体，在赋值前不会响应任何输入。", this);
            }

            isInitialized = true;
        }

        /// <summary>
        /// 每帧检测鼠标右键点击，命中地面则向受控单位下达移动指令。
        /// </summary>
        private void Update()
        {
            if (!isInitialized)
            {
                return;
            }

            // 右键 = 移动指令。1 代表鼠标右键，0 为左键，2 为中键。
            if (!Input.GetMouseButtonDown(1))
            {
                return;
            }

            // 空引用保护：未指定受控实体时直接忽略输入，不抛异常也不刷日志（Awake 已警告过一次）。
            if (controlledEntity == null)
            {
                return;
            }

            // 已死亡的受控单位不再响应移动指令（README 3.1：英雄死亡后「不触发自动复活，原地静止」）。
            // 为什么必须显式拦：DeadState 只是停止了寻路，代理本身仍然可用，
            // 若这里不拦，玩家可以继续右键把尸体拖着满地图跑，与「原地静止」的需求直接冲突。
            HealthComponent controlledHealth = controlledEntity.Health;
            if (controlledHealth != null && controlledHealth.IsDead)
            {
                return;
            }

            // 空引用保护：防御塔、基地等固定建筑没有移动组件，此时忽略移动指令。
            if (controlledEntity.Movement == null)
            {
                if (!hasWarnedMissingMovement)
                {
                    hasWarnedMissingMovement = true;
                    Debug.LogWarning($"[PlayerCommandController] 受控实体 {controlledEntity.name} 没有 MovementComponent，无法执行移动指令。", this);
                }
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
                // 在 Scene 视图画出射线，方便直观确认是否打到了地面 Layer。
                Debug.DrawRay(ray.origin, ray.direction * maxRayDistance, Color.yellow, 0.5f);
            }

            // 只检测地面 Layer：地面是静态碰撞体，用一次 Physics.Raycast 比物理范围查询便宜得多。
            if (!Physics.Raycast(ray, out RaycastHit hit, maxRayDistance, groundLayerMask, QueryTriggerInteraction.Ignore))
            {
                // 点到天空盒、UI 或 Layer 配错的对象，都走这里，静默忽略即可。
                return;
            }

            Vector3 destination = hit.point;

            if (snapToNavMesh)
            {
                // 点击点可能落在障碍物内部、桥下或 NavMesh 之外，直接交给 SetDestination 会失败。
                // 这里先把落点吸附到最近的可通行点，从源头保证指令一定可执行。
                if (NavMesh.SamplePosition(hit.point, out NavMeshHit navHit, navMeshSampleDistance, NavMesh.AllAreas))
                {
                    destination = navHit.position;
                }
                else
                {
                    Debug.LogWarning($"[PlayerCommandController] 点击位置 {hit.point} 附近 {navMeshSampleDistance} 米内没有可行走区域，指令已忽略。", this);
                    return;
                }
            }

            // 记录落点用于 Gizmos 可视化（原始点击点与最终落点分开存，方便对比吸附偏差）。
            lastHitPoint = hit.point;
            lastCommandPoint = destination;
            hasLastCommand = true;

            if (logMoveCommands)
            {
                // 输出目标坐标便于在 Console 直接核对：如果坐标明显异常（例如 y 值突变、离点击点很远），
                // 基本可以断定是 Layer 配错或 NavMesh 未烘焙。
                Debug.Log(
                    $"[PlayerCommandController] 移动指令 → {controlledEntity.name} | 目标点 {destination.ToString("F2")} " +
                    $"| 点击点 {hit.point.ToString("F2")} | 距离 {Vector3.Distance(hit.point, destination):F2}m", this);
            }

            // 最终只通过组件的公开方法下达意图，寻路细节由 MovementComponent 负责。
            controlledEntity.Movement.MoveTo(destination);
        }

        /// <summary>
        /// 运行时切换受控单位。典型用途：对局开始时英雄才被实例化，或后续做英雄切换。
        /// </summary>
        /// <param name="entity">新的受控实体，允许传 null 表示解除控制。</param>
        public void SetControlledEntity(EntityBase entity)
        {
            controlledEntity = entity;
            hasWarnedMissingMovement = false;
        }

        /// <summary>
        /// Scene 视图可视化：绘制最近一次移动指令的落点。
        /// 说明：这些数据只存在于运行期内存中，退出播放模式后标记会消失（Unity 的 Gizmos 不持久化运行时状态）。
        /// </summary>
        private void OnDrawGizmos()
        {
            if (!drawCommandGizmos || !hasLastCommand)
            {
                return;
            }

            // 抬升 0.1 米，避免标记被地面 Z-Fighting 吃掉。
            Vector3 lift = Vector3.up * 0.1f;

            // 黄色线框球 = 鼠标原始点击点（未吸附）。
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(lastHitPoint + lift, 0.25f);

            // 绿色线框球 = 实际下达给单位的目标点（吸附后）。
            Gizmos.color = Color.green;
            Gizmos.DrawWireSphere(lastCommandPoint + lift, 0.4f);

            // 两球之间的连线 = NavMesh 吸附造成的偏差；理想情况下这条线应该很短。
            Gizmos.color = Color.magenta;
            Gizmos.DrawLine(lastHitPoint + lift, lastCommandPoint + lift);

            // 从受控单位连到目标点，直观展示"这一指令让单位往哪走"。
            if (controlledEntity != null)
            {
                Gizmos.color = Color.cyan;
                Gizmos.DrawLine(controlledEntity.transform.position + lift, lastCommandPoint + lift);
            }
        }
    }
}
