using UnityEngine;
using MOBA.Components;
using MOBA.Core;
using MOBA.Gameplay;

namespace MOBA.AI
{
    /// <summary>
    /// 推进状态：小兵的默认状态，沿 LanePath 的节点依次前进，途中发现敌人则转追击。
    ///
    /// 职责边界（对应计划书 3.4「MoveState：沿路线或命令位置移动，并周期性检查敌人」）：
    /// 1. 只做两件事——「感知敌人」与「按路径点推进」，具体移动由 MovementComponent 执行；
    /// 2. 不自己计算路径、不自己驱动 NavMeshAgent，只调用 MovementComponent.MoveTo / HasReachedDestination；
    /// 3. 不判断"该不该打"，那是 ChaseState 的事；
    /// 4. 额外承担一个收尾动作：抵达路径点时清除 ChaseState 的追击起点锚点
    ///    （"回到兵线"这一事实只有本状态知道，锚点的清除时机因此必须落在这里，详见 Tick 中的说明）。
    ///
    /// 关键状态：currentWaypointIndex 是【跨状态切换保留】的实例字段。
    /// 小兵追击结束返回路线时，必须从原来推进到的那一段继续，而不是从起点重走
    /// （计划书阶段三验收："目标死亡或超出最大追击范围后，小兵返回路线继续推进"）。
    /// </summary>
    public class MoveState : IState
    {
        /// <summary>状态上下文。所有组件访问都经它拿到，状态本身不缓存任何组件引用。</summary>
        private readonly EntityAIController context;

        /// <summary>
        /// 当前正在前往的路径点索引。
        /// 语义：它总是指向"尚未到达的那个点"——到达后立刻 +1，因此不会出现"索引指向已到过的点"。
        /// </summary>
        private int currentWaypointIndex;

        /// <summary>
        /// 是否已经为当前路径点下达过移动指令。
        /// 用途：避免每帧重复调用 MoveTo。NavMeshAgent.SetDestination 每次都会重新算路，
        /// 每帧调用既浪费又会让代理在路径上抖动；而"是否已下达"无法从代理状态可靠反推
        /// （到达目的地后 agent.hasPath 仍为 true），因此必须自己记录。
        /// </summary>
        private bool hasIssuedMoveForCurrentWaypoint;

        /// <summary>是否已就"缺少移动组件"报过警告，防止每帧刷屏。</summary>
        private bool hasReportedMissingMovement;

        /// <summary>是否已就"缺少路线"报过警告，防止每帧刷屏。</summary>
        private bool hasReportedMissingLane;

        /// <summary>
        /// 本轮推进已经「几乎没在动」的累计时长（秒）。
        /// 到达路径点、重新下达指令或被判定卡住后清零——它衡量的是「连续僵持了多久」，
        /// 因此中途一旦动起来就必须归零，否则会把"走走停停"误判成卡死。
        /// </summary>
        private float stagnationTimer;

        /// <summary>是否已就「被卡住」报过警告，防止同一单位反复卡住时刷屏。</summary>
        private bool hasReportedStagnation;

        /// <summary>
        /// 构造状态。
        /// </summary>
        /// <param name="context">所属 AI 控制器，必须非空。</param>
        public MoveState(EntityAIController context)
        {
            this.context = context;
        }

        /// <summary>当前目标路径点索引，供调试视图读取。</summary>
        public int CurrentWaypointIndex => currentWaypointIndex;

        /// <summary>
        /// 是否还有未走完的路径点。
        /// 供 EntityAIController.HasRemainingWaypoints 转发给 IdleState 使用——
        /// IdleState 必须先确认这里还有活可干，才切回推进状态，否则两者会每帧互切。
        /// </summary>
        public bool HasRemainingWaypoints
        {
            get
            {
                LanePath lane = context != null ? context.Lane : null;
                if (lane == null || lane.Waypoints == null)
                {
                    return false;
                }

                return currentWaypointIndex < lane.Waypoints.Count;
            }
        }

        /// <summary>
        /// 进入推进状态。
        /// 必须把"已下达移动指令"标记清掉：上一轮追击结束时 AttackState 调用过 MovementComponent.Stop()，
        /// 代理已被冻结且路径已清空；若不清标记，本状态会以为"指令已下达"而永远不再下令，小兵就此卡死在原地。
        /// </summary>
        public void Enter()
        {
            hasIssuedMoveForCurrentWaypoint = false;
            stagnationTimer = 0f;
        }

        /// <summary>
        /// 每帧驱动：先感知敌人，无敌人则沿路线推进。
        /// </summary>
        public void Tick()
        {
            if (context == null)
            {
                return;
            }

            EntityBase owner = context.Owner;
            if (owner == null)
            {
                return;
            }

            // ---------- 1. 感知敌人：有敌人就转追击 ----------
            // 索敌交给控制器【节流后】的入口（内部按 detectionInterval 限制物理查询频率），
            // 状态自己不写物理代码，也不各自实现一份间隔逻辑。
            TargetingComponent targeting = owner.Targeting;
            if (targeting != null && context.TryDetectEnemy(out ITargetable enemy))
            {
                // 先写入目标再请求切换：ChaseState 的 Tick 会直接读 TargetingComponent.CurrentTarget，
                // 顺序颠倒会让追击的第一帧读不到目标而立刻退回推进状态，形成 Move ↔ Chase 每帧震荡。
                targeting.SetTarget(enemy);

                if (context.TryEnterChaseState())
                {
                    // 切换成功 → 本帧职责结束，不再下达移动指令。
                    return;
                }

                // 切换被拒绝（本单位不具备追击能力 / 已被牵引出界，见 EntityAIController.TryEnterChaseState）
                // → 敌人不构成威胁，继续沿路线推进，不做任何额外处理。
            }

            // ---------- 2. 沿路线推进 ----------
            MovementComponent movement = owner.Movement;
            if (movement == null)
            {
                // 防御塔、基地按设计没有移动组件，正常不会进入本状态；
                // 真发生了说明配置有误（例如给固定建筑挂了 AI），提示一次并停在此状态。
                ReportMissingMovementOnce();
                return;
            }

            LanePath lane = context.Lane;
            if (lane == null || lane.Waypoints == null || lane.Waypoints.Count == 0)
            {
                ReportMissingLaneOnce();
                return;
            }

            // 所有路径点都已走完 → 交给 IdleState 等待。
            // IdleState 会在确认 HasRemainingWaypoints 为 false 后停留在待机，不会立刻把状态切回来。
            if (currentWaypointIndex >= lane.Waypoints.Count)
            {
                context.TryEnterIdleState();
                return;
            }

            // ---------- 3. 尚未为当前路径点下达移动指令 → 下达一次 ----------
            if (!hasIssuedMoveForCurrentWaypoint)
            {
                // 【必须用 MoveTo 的返回值作为标记，不能无条件置 true】
                // MoveTo 返回 false 表示代理不在 NavMesh 上或该路径点不可达，本次没有产生任何移动。
                // 若此时仍把标记置为 true，本状态将永远不会再次下令，小兵会永久卡死在该路径点
                // （阶段三验收明确要求"无明显状态卡死"）。保持 false 让下一帧自然重试，
                // NavMesh 就绪或障碍被清除后即可自愈；失败时的日志由 MovementComponent 以
                // 一次性告警输出，不会因为重试而刷屏。
                hasIssuedMoveForCurrentWaypoint = movement.MoveTo(lane.GetWaypointPosition(currentWaypointIndex));
                return;
            }

            // ---------- 4. 已到达当前路径点 → 前进到下一个点 ----------
            // 刻意把"推进索引"与"为新点下达指令"拆到相邻两帧完成：
            // 单帧内既推进又下令会让日志与 Gizmos 的观察结果错位，排查寻路问题时容易误判。
            if (movement.HasReachedDestination())
            {
                currentWaypointIndex++;
                hasIssuedMoveForCurrentWaypoint = false;
                stagnationTimer = 0f;

                // 抵达路径点 = 本单位已真正回到兵线，此时才允许清除追击起点锚点、重新获得追击资格。
                // 为什么必须放在这里而不是 Enter()：若进入推进状态就清除，单位在"返回兵线途中"
                // 就会立刻重新获得追击资格，被同一个敌人再次牵走，牵引极限将完全失效。
                context.ClearChaseStartPos();
                return;
            }

            // ---------- 5. 停滞检测：被堵住时强制重新下达移动指令 ----------
            // 覆盖的场景：目标路径点被另一个单位长期占住，NavMeshAgent 的局部避让绕不开，
            // 于是"已下令但永远到不了"。这类卡死 HasReachedDestination 永远返回 false，
            // 没有任何超时机制就会永久僵持（阶段三验收要求"无明显状态卡死"）。
            if (context.IsStagnant(movement))
            {
                stagnationTimer += Time.deltaTime;

                if (stagnationTimer >= context.StagnationDuration)
                {
                    // 清掉"已下令"标记 → 下一帧走回第 3 步重新下达一次 MoveTo。
                    // 重新下达会让代理重算一次路径，若堵路的单位已经走开即可自愈。
                    stagnationTimer = 0f;
                    hasIssuedMoveForCurrentWaypoint = false;
                    ReportStagnationOnce();
                }

                return;
            }

            // 动起来了：清空僵持计时，避免"走走停停"被累计成卡死。
            stagnationTimer = 0f;
        }

        /// <summary>
        /// 离开推进状态。
        /// 无需清理：本状态没有订阅事件，唯一持有的数据是路径点索引，而它必须保留到下次进入。
        /// </summary>
        public void Exit()
        {
        }

        /// <summary>报告一次"缺少移动组件"的警告。</summary>
        private void ReportMissingMovementOnce()
        {
            if (hasReportedMissingMovement)
            {
                return;
            }

            hasReportedMissingMovement = true;

            Debug.LogError(
                $"[MoveState] {context.name} 没有 MovementComponent，无法沿路线推进。" +
                "请检查该单位是否本就不应挂载 AI（防御塔/基地使用固定位置逻辑）。", context);
        }

        /// <summary>报告一次"缺少路线"的警告。</summary>
        private void ReportMissingLaneOnce()
        {
            if (hasReportedMissingLane)
            {
                return;
            }

            hasReportedMissingLane = true;

            Debug.LogWarning(
                $"[MoveState] {context.name} 未配置有效的 LanePath（引用为空或没有任何节点），" +
                "无法沿路线推进，将停在原地等待敌人。请检查 EntityAIController 的 Lane 字段。", context);
        }

        /// <summary>
        /// 报告一次"被卡住"。
        /// 用一次性标记的原因：单位可能被长期堵住，每 1 秒重下一次指令就会每 1 秒打一条日志。
        /// 出现这条警告通常意味着：路径点被其他单位占住，或该路径点不在已烘焙的 NavMesh 上。
        /// </summary>
        private void ReportStagnationOnce()
        {
            if (hasReportedStagnation)
            {
                return;
            }

            hasReportedStagnation = true;

            Debug.LogWarning(
                $"[MoveState] {context.name} 连续 {context.StagnationDuration:F1} 秒几乎未移动，判定为被卡住，" +
                "已强制重新下达移动指令。若该警告反复出现，请检查路径点是否被其他单位长期占住、" +
                "或该点是否落在已烘焙的 NavMesh 上。", context);
        }
    }
}
