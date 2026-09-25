using UnityEngine;
using UnityEngine.AI;

namespace MOBA.Components
{
    /// <summary>
    /// 移动组件：本项目里【唯一】负责驱动 NavMeshAgent 的模块。
    ///
    /// 设计约束（来自计划书「每个脚本只承担一个明确职责」）：
    /// 1. 其他模块（EntityBase / FSM 状态 / 玩家指令层）只能通过本类的公开方法下达「移动意图」，
    ///    不允许自己持有 NavMeshAgent 去调 SetDestination，否则寻路状态会被多处写入而互相打架。
    /// 2. 本类不判断「为什么要移动」（那是 FSM 与 PlayerCommandController 的职责），只回答「怎么移动」。
    /// 3. 本类不缓存任何配置数据，速度由外部（EntityBase）在初始化阶段注入。
    ///
    /// 依赖声明：RequireComponent 会在挂载本组件时自动补上 NavMeshAgent——
    /// "能移动的单位必然需要寻路代理"属于硬依赖，适合用 RequireComponent 强约束。
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    [DisallowMultipleComponent]
    public class MovementComponent : MonoBehaviour
    {
        [Header("调试")]
        [Tooltip("选中该对象时，在 Scene 视图中绘制当前寻路路径与目的地。")]
        [SerializeField] private bool drawPathGizmos = true;

        /// <summary>当前 GameObject 上的寻路代理，由 Awake 缓存。</summary>
        private NavMeshAgent agent;

        /// <summary>
        /// 是否已经通过 MoveTo 下达过目的地。
        /// 用途：区分「从未下令」（应视为未到达）与「已到达目的地」两种状态——
        /// 只靠 remainingDistance 无法区分这两者（未下令时 remainingDistance 为 0）。
        /// </summary>
        private bool hasDestination;

        /// <summary>是否已就"缺少 NavMeshAgent"报过错误，用于防止每帧重复刷日志。</summary>
        private bool hasReportedMissingAgent;

        /// <summary>
        /// 是否已就"代理不在 NavMesh 上"报过警告。
        /// 必要性：MoveTo 失败后调用方（MoveState / ChaseState）会每帧重试，不加这个标记会刷屏。
        /// </summary>
        private bool hasReportedNotOnNavMesh;

        /// <summary>是否已就"目标点无法寻路"报过警告，理由同上。</summary>
        private bool hasReportedUnreachableDestination;

        /// <summary>代理是否可用（存在、已启用、且被放置在 NavMesh 上）。所有查询方法都会先做这层防护。</summary>
        private bool IsAgentUsable => agent != null && agent.enabled && agent.isOnNavMesh;

        /// <summary>当前移动速度（单位/秒）。只读，写入请走 SetMoveSpeed()。</summary>
        public float Speed => agent != null ? agent.speed : 0f;

        /// <summary>是否仍持有未走完的路径。</summary>
        public bool HasPath => agent != null && agent.hasPath;

        /// <summary>距离当前目的地剩余距离；无有效路径时返回 0。</summary>
        public float RemainingDistance => agent != null ? agent.remainingDistance : 0f;

        /// <summary>当前帧的实际移动速度向量，供表现层（如 AnimationComponent）判断是否在移动。</summary>
        public Vector3 CurrentVelocity => agent != null ? agent.velocity : Vector3.zero;

        /// <summary>
        /// 缓存 NavMeshAgent 引用。
        /// 放在 Awake 中是为了让同一帧稍后执行的 Start（例如 EntityBase.ApplyStats）能安全地读写速度，
        /// 因为 Unity 保证「所有 Awake 都先于任何 Start 执行」。
        /// </summary>
        private void Awake()
        {
            agent = GetComponent<NavMeshAgent>();

            if (agent == null)
            {
                // 正常情况下 RequireComponent 已保证存在，这里仅作为防御性检查（例如组件被运行时移除）。
                // 用 LogError 而不是静默返回：缺少代理属于致命配置错误，必须让使用者在 Console 立刻看到。
                hasReportedMissingAgent = true;
                Debug.LogError(
                    $"[MovementComponent] {name} 上找不到 NavMeshAgent 组件，所有移动功能将失效。" +
                    "请检查该组件是否被运行时移除，或该对象是否由代码动态创建（此时需手动 AddComponent）。", this);
            }
        }

        /// <summary>
        /// 报告一次"代理缺失"错误，避免在 Update 驱动的调用路径上每帧刷屏。
        /// </summary>
        private void ReportMissingAgentOnce()
        {
            if (hasReportedMissingAgent)
            {
                return;
            }

            hasReportedMissingAgent = true;
            Debug.LogError($"[MovementComponent] {name} 的 NavMeshAgent 引用为空，本次指令被忽略。", this);
        }

        /// <summary>
        /// 设置移动速度（写入 NavMeshAgent.speed）。
        /// 由 EntityBase 在初始化时用 EntityStatsData.MoveSpeed 调用，
        /// 这样策划改配置资产即可改手感，不需要改任何代码。
        /// </summary>
        /// <param name="speed">目标速度，单位/秒；负值会被夹到 0。</param>
        public void SetMoveSpeed(float speed)
        {
            if (agent == null)
            {
                ReportMissingAgentOnce();
                return;
            }

            // NavMeshAgent.speed 允许 0，但负值没有意义，这里做一次保护。
            agent.speed = Mathf.Max(0f, speed);
        }

        /// <summary>
        /// 下达移动指令：把代理的目的地设置为指定世界坐标。
        /// 调用后代理会自行绕开 NavMesh 上的障碍物寻路，本类不做任何路径计算。
        /// </summary>
        /// <param name="destination">世界坐标下的目标点，必须位于 NavMesh 上或可由其投射到 NavMesh。</param>
        /// <returns>
        /// 本次指令是否真的被代理接受。返回 false 表示代理不存在、不在 NavMesh 上或目标点不可达，
        /// 此时【不会】产生任何移动。
        ///
        /// 为什么必须有返回值：调用方（FSM 状态）需要据此判断"这次算不算已经下过令"。
        /// 若把失败的指令也算作已下令，单位会永久停在该目标点前不再重试——即"寻路卡死"
        /// （阶段三验收要求"无空引用异常或明显状态卡死"）。有了返回值，失败时调用方保持未下令状态，
        /// 下一帧自然重试，NavMesh 就绪或障碍被清除后即可自愈。
        /// </returns>
        public bool MoveTo(Vector3 destination)
        {
            if (agent == null)
            {
                ReportMissingAgentOnce();
                return false;
            }

            // 代理未被放置在 NavMesh 上时调用 SetDestination 会打印引擎错误，这里提前拦截。
            if (!agent.isOnNavMesh)
            {
                ReportNotOnNavMeshOnce();
                return false;
            }

            // Stop() 会把 isStopped 置为 true，重新下令时必须先恢复，否则代理不会移动。
            agent.isStopped = false;

            // SetDestination 返回 false 表示目标点无法投射到 NavMesh（例如点到地图外或不可达区域）。
            if (!agent.SetDestination(destination))
            {
                ReportUnreachableDestinationOnce(destination);
                hasDestination = false;
                return false;
            }

            hasDestination = true;
            return true;
        }

        /// <summary>
        /// 报告一次"代理不在 NavMesh 上"。
        /// 用一次性标记的原因：MoveState / ChaseState 在指令失败后会每帧重试，
        /// 不加标记会在一秒内刷出几十条同样的警告（项目日志约定：重复告警只报一次，防刷屏）。
        /// </summary>
        private void ReportNotOnNavMeshOnce()
        {
            if (hasReportedNotOnNavMesh)
            {
                return;
            }

            hasReportedNotOnNavMesh = true;

            Debug.LogWarning(
                $"[MovementComponent] {name} 当前不在 NavMesh 上，MoveTo 被忽略。" +
                "最常见原因是出生点未落在已烘焙的 NavMesh 上（NavMeshAgent 会同时报 " +
                "not close enough to the NavMesh）。该警告只输出一次，后续相同情况不再重复打印。", this);
        }

        /// <summary>
        /// 报告一次"目标点无法寻路"。
        /// 与上一条的区别：代理本身在网格上，但目标点不可达（点到地图外、障碍物内部或孤立区域）。
        /// 同样用一次性标记防刷屏。
        /// </summary>
        /// <param name="destination">本次未能寻路到的目标点。</param>
        private void ReportUnreachableDestinationOnce(Vector3 destination)
        {
            if (hasReportedUnreachableDestination)
            {
                return;
            }

            hasReportedUnreachableDestination = true;

            Debug.LogWarning(
                $"[MovementComponent] {name} 无法寻路到 {destination}（可能不在 NavMesh 上）。" +
                "该警告只输出一次，后续相同情况不再重复打印。", this);
        }

        /// <summary>
        /// 停止寻路：清空当前路径并冻结代理。
        /// 典型调用时机：进入攻击距离后停下输出、单位死亡时停止移动。
        /// </summary>
        public void Stop()
        {
            if (agent == null)
            {
                ReportMissingAgentOnce();
                return;
            }

            // 代理已被禁用（例如单位死亡时 DeadState 关掉了 NavMeshAgent）：
            // 不再对引擎做任何操作，只清理自己的状态标记，避免对已禁用的代理调用 ResetPath /
            // 写 isStopped 触发引擎告警（冻结战场时会遍历到已经死掉的单位，这条分支必然会被走到）。
            if (!agent.enabled)
            {
                hasDestination = false;
                return;
            }

            // 先清路径再置停止位：只置 isStopped 会保留旧路径，重新启动时可能瞬间回拉。
            if (agent.hasPath)
            {
                agent.ResetPath();
            }

            agent.isStopped = true;
            hasDestination = false;
        }

        /// <summary>
        /// 判断是否已经到达目的地。
        /// 判定为 true 需同时满足四个条件，避免"看起来到了但还在滑动"或"路径尚未算完"的误判：
        /// 1. 代理可用；2. 曾下达过目的地；3. 路径已计算完成；4. 剩余距离进入停止阈值且实际速度已归零。
        /// </summary>
        /// <returns>已到达返回 true；从未下达目的地、路径计算中或仍在移动均返回 false。</returns>
        public bool HasReachedDestination()
        {
            if (!IsAgentUsable)
            {
                return false;
            }

            if (!hasDestination)
            {
                return false;
            }

            // pathPending 为 true 表示寻路结果还没算完，此时 remainingDistance 是无效值。
            if (agent.pathPending)
            {
                return false;
            }

            if (agent.remainingDistance > agent.stoppingDistance)
            {
                return false;
            }

            // 到达阈值后仍可能因惯性/避让在滑动，速度未归零就不算真正到达，否则状态机会提前切走。
            if (agent.hasPath && agent.velocity.sqrMagnitude > 0.01f)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Scene 视图可视化：绘制当前寻路路径折线、目的地与停止阈值。
        /// 仅在选中该对象时绘制（OnDrawGizmosSelected），避免多个单位同时绘制把视图画满。
        /// </summary>
        private void OnDrawGizmosSelected()
        {
            if (!drawPathGizmos || agent == null)
            {
                return;
            }

            // 编辑模式下代理可能尚未被放置在 NavMesh 上，此时读取 path / destination 无意义。
            if (!Application.isPlaying && !agent.isOnNavMesh)
            {
                return;
            }

            // 抬升 0.1 米避免折线被地面 Z-Fighting 吃掉，导致肉眼看不到。
            Vector3 lift = Vector3.up * 0.1f;

            if (agent.hasPath)
            {
                Vector3[] corners = agent.path.corners;

                // 青色折线 = 代理当前持有的完整寻路路径。
                Gizmos.color = Color.cyan;
                for (int i = 0; i < corners.Length - 1; i++)
                {
                    Gizmos.DrawLine(corners[i] + lift, corners[i + 1] + lift);
                }

                // 从自身位置连到路径起点，表示"从当前站位接入这条路径"。
                if (corners.Length > 0)
                {
                    Gizmos.DrawLine(transform.position + lift, corners[0] + lift);
                }
            }

            // 绿色线框球 = 最终目的地。
            Gizmos.color = Color.green;
            Gizmos.DrawWireSphere(agent.destination, 0.5f);

            // 黄色小球 = 代理当前正在朝其移动的路径拐点（steeringTarget），用于判断是否在绕障碍。
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(agent.steeringTarget, 0.25f);

            // 橙色球 = 停止阈值（stoppingDistance），进入该半径即视为到达，用于核对 HasReachedDestination 的触发位置。
            Gizmos.color = new Color(1f, 0.5f, 0f, 0.6f);
            Gizmos.DrawWireSphere(agent.destination, agent.stoppingDistance);
        }
    }
}
