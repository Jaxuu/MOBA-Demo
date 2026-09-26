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
        /// <summary>
        /// 到达停靠距离的设计值（米）。字段默认值与 AutoSceneBuilder 写预制体时都取它 ——
        /// 【为什么要有这个常量】同一个设计值此前在"字段初始值"与"工具注入值"两处各写一遍，
        /// 改一处漏一处就会出现"英雄 0.5 / 小兵 0（工具没写）"这种只在实机上表现为
        /// 「小兵仍然假卡死」的不一致。设计值只能有一个来源。
        /// </summary>
        public const float DesignArrivalStoppingDistance = 0.5f;

        [Header("调试")]
        [Tooltip("选中该对象时，在 Scene 视图中绘制当前寻路路径与目的地。")]
        [SerializeField] private bool drawPathGizmos = true;

        [Header("代理参数（阶段八：窄道拥堵与假卡死的底层修复）")]
        [Tooltip("到达目的地的停靠距离（米）。0 表示必须精确站到目标点上。\n" +
                 "【为什么不能是 0】「到达路径点」的判据是 remainingDistance ≤ stoppingDistance。\n" +
                 "取 0 时，代理必须精确抵达，而桥面收窄到 14 米后塔洞两侧只有 5.2 米通行带，\n" +
                 "被邻居推挤的单位速度长期不归零 → 永远判不到达 → 停滞检测每秒重下一次无效指令。\n" +
                 "取 0.5 米让「到点」在拥堵下也能成立，同时 0.5 远小于最小攻击距离（小兵 2.0 / 英雄 2.5），\n" +
                 "因此不会出现「停在射程外永远打不到目标」的问题。")]
        [Min(0f)]
        [SerializeField] private float arrivalStoppingDistance = DesignArrivalStoppingDistance;

        [Tooltip("避让优先级（0~99）。值【越小】优先级越高，优先级高的代理会挤开优先级低的。\n" +
                 "【为什么必须差异化】所有代理都取同一个值时，Unity 的局部避让是对称的：\n" +
                 "两个单位迎面撞上会互相顶住、谁也过不去（窄道里表现为整队卡死）。\n" +
                 "由注入方按阵营/序号给出不同值即可打破对称：英雄（30 段）高于小兵（60 段），\n" +
                 "于是小兵会给英雄让路，而小兵之间也因取值不同而不会顶死。")]
        [Range(0, 99)]
        [SerializeField] private int avoidancePriority = 50;

        [Header("运行时状态（只读，仅供 Inspector 观察调试）")]
        [Tooltip("移动锁。为 true 时拒绝一切新的移动指令并强制停下，由 BuffComponent（眩晕）与 SkillComponent（施法前摇）控制。")]
        [SerializeField] private bool movementLocked = false;

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

        /// <summary>当前是否处于移动锁状态（只读），供调试与测试断言使用。</summary>
        public bool IsMovementLocked => movementLocked;

        /// <summary>
        /// 设置移动锁（阶段六新增）。
        ///
        /// 【为什么需要它】眩晕与施法前摇都要求"这段时间内不许位移"。若不加这个闸门，
        /// 就得在每一个下达移动指令的地方（玩家指令层 1 处 + FSM 的 MoveState / ChaseState / AttackState 共 3 处）
        /// 各写一次"是否被眩晕 / 是否在施法"的判断——漏掉任何一处，就会出现"眩晕中还能被点走"的破绽。
        /// 闸门放在唯一的移动出口（MoveTo）上，就不可能漏。
        ///
        /// 语义：加锁时立即停止当前寻路（清掉残留路径，避免"锁上了但还在滑行"）；
        /// 解锁时不自动恢复移动——调用方需要重新下达指令，这与 MoveTo 失败后调用方重试的既有约定一致。
        ///
        /// 【注意】本锁与"死亡"是两回事：死亡走 HeroController/DeadState 的 agent.enabled = false，
        /// 那是物理层面的彻底封禁，不经过本锁。
        /// </summary>
        /// <param name="locked">true = 锁住移动；false = 解锁。</param>
        public void SetMovementLocked(bool locked)
        {
            if (movementLocked == locked)
            {
                return;
            }

            movementLocked = locked;

            if (locked)
            {
                // 加锁即刹车：否则代理会继续沿上一次的目的地滑行到终点，眩晕看起来完全没生效。
                Stop();
            }
        }

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
                return;
            }

            // ---- 代理参数在这里统一写入，而不是依赖预制体上的序列化值 ----
            // 【为什么要由代码兜住】这两个值都属于"错了会静默失效"的一类：
            //   · stoppingDistance = 0 → 拥堵时永远判不到达（症状是"整队卡在兵线上发呆"，Console 只有告警）；
            //   · 避让优先级全同    → 窄道里对称顶死（症状是"单位互相卡住不动"，同样没有任何报错）。
            // 而预制体资产是仓库里长期存在的文件，一旦有人在 Inspector 里手工改过、
            // 或引擎升级改变了缺省填充行为，序列化值就不再可靠。在 Awake 里显式写入，
            // 才能保证"每次组装后这两个值一定等于设计值"（与"场景装配必须自动化"这条铁律一致）。
            //
            // 【为什么放在 Awake 而不是 Start】Awake 早于所有 Start，也早于 EntityBase.ApplyStats
            // （后者在 Start 里写 speed），因此这里写入的参数不会被任何后续注入覆盖。
            agent.stoppingDistance = Mathf.Max(0f, arrivalStoppingDistance);
            agent.avoidancePriority = Mathf.Clamp(avoidancePriority, 0, 99);
        }

        /// <summary>
        /// 设置避让优先级（阶段八新增）。由注入方按"阵营 + 序号"给出不同的值，打破局部避让的对称性。
        ///
        /// 【为什么允许运行期修改而不是只在 Awake 读一次】小兵是运行时由 MinionSpawner 批量生成的，
        /// 生成方需要在 Initialize 之后立刻给出各自的优先级；走公开方法比反射写私有字段可靠得多
        /// （反射写不会被 Unity 序列化系统感知，也不会同步到 NavMeshAgent）。
        /// </summary>
        /// <param name="priority">避让优先级，会被夹到 0~99。</param>
        public void SetAvoidancePriority(int priority)
        {
            avoidancePriority = Mathf.Clamp(priority, 0, 99);

            if (agent != null)
            {
                agent.avoidancePriority = avoidancePriority;
            }
        }

        /// <summary>
        /// 启用 / 禁用寻路代理（阶段八自审补齐）。
        ///
        /// 【为什么必须由本类提供这个入口】本类的职责边界写得很明确：
        /// "本项目里【唯一】负责驱动 NavMeshAgent 的模块，其他模块不允许自己持有 NavMeshAgent"。
        /// 但死亡冻结（DeadState）与英雄死亡收尾（HeroController.HandleDied）此前都是自己
        /// `GetComponent&lt;NavMeshAgent&gt;()` 再写 `enabled = false` —— 三处各自持有同一个引擎组件，
        /// 正是这条边界要防的分叉：任何一处改了禁用条件（例如"禁用的同时还要清路径"），
        /// 另外两处不会跟着变。
        ///
        /// 【幂等】重复设置同一状态不产生额外操作；代理缺失时静默返回（建筑没有代理是正常配置）。
        /// </summary>
        /// <param name="value">true = 启用代理；false = 禁用（尸体退出导航网格的局部避让计算）。</param>
        public void SetAgentEnabled(bool value)
        {
            if (agent == null || agent.enabled == value)
            {
                return;
            }

            agent.enabled = value;
        }

        /// <summary>
        /// 把单位瞬移到指定位置（阶段八自审补齐，供复活搬运使用）。
        ///
        /// 【为什么"先启用代理再 Warp"这件事必须封装在这里】顺序反了的症状是"复活了，但人还躺在原地"，
        /// 而且不报任何错（NavMeshAgent 在禁用状态下 Warp 不生效）。此前这个顺序约定写在
        /// HeroController.Revive 的注释里、靠调用方记性维持；收进本类之后，顺序成为实现细节，
        /// 调用方不可能弄反。
        ///
        /// 【失败降级】Warp 返回 false（目标点不在 NavMesh 上）时退化为直接写 Transform + nextPosition：
        /// 至少让单位出现在目标点，同时把问题明确报出来，而不是让它在原地复活。
        /// </summary>
        /// <param name="position">目标世界坐标。</param>
        /// <returns>Warp 成功返回 true；失败（已降级为直接设坐标）返回 false。</returns>
        public bool WarpTo(Vector3 position)
        {
            if (agent == null)
            {
                ReportMissingAgentOnce();
                return false;
            }

            // 顺序不可颠倒：禁用状态下 Warp 不生效。
            agent.enabled = true;

            if (agent.Warp(position))
            {
                if (!agent.isOnNavMesh)
                {
                    Debug.LogError(
                        $"[MovementComponent] {name} 瞬移到 {position} 后代理不在 NavMesh 上，" +
                        "该单位将无法寻路移动。请检查目标点是否在已烘焙的 NavMesh 覆盖范围内。", this);
                }

                return true;
            }

            transform.position = position;
            agent.nextPosition = position;

            Debug.LogError(
                $"[MovementComponent] {name} 无法通过 NavMeshAgent.Warp 到达 {position}" +
                "（通常意味着该点不在已烘焙的 NavMesh 上），已退化为直接设置坐标。" +
                "请检查目标点是否落在地面覆盖范围内、以及 NavMesh 是否已烘焙。", this);

            return false;
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
            // 移动锁闸门放在最前面（先于 agent 判空）：被锁住的单位不应再产生任何寻路行为，
            // 也不该因为"代理不在 NavMesh 上"而反复刷告警——那会让真正的配置问题被噪声淹没。
            // 返回 false 且不报错：这是"当前不允许移动"的正常语义，调用方（FSM / 玩家指令）会自然重试，
            // 锁一解除即可自愈（与 MoveTo 失败重试的既有设计一致）。
            if (movementLocked)
            {
                return false;
            }

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
