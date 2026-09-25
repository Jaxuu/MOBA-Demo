using System.Collections.Generic;
using UnityEngine;

namespace MOBA.Gameplay
{
    /// <summary>
    /// 兵线路径：以有序节点列表描述一条推进路线的纯数据容器（对应计划书 2 中
    /// "Gameplay/LanePath.cs —— 路线节点数据与访问"）。
    ///
    /// 职责边界：
    /// 1. 本类【只提供数据访问】，不含任何推进逻辑。小兵何时前进、何时停下打人，
    ///    由 FSM 的 MoveState 决定；本类不参与决策，也不引用 NavMeshAgent。
    /// 2. 本类不关心阵营方向：蓝方与红方各自持有一条 LanePath，
    ///    通过"节点顺序相反"来表达推进方向，避免在数据层引入方向分支。
    /// 3. 节点用 Transform 引用而非 Vector3 坐标，是为了让关卡同学在 Scene 视图里
    ///    直接拖拽调整路线，改完立即生效，不需要回写任何资产数据。
    /// </summary>
    [DisallowMultipleComponent]
    public class LanePath : MonoBehaviour
    {
        [Header("路线节点")]
        [Tooltip("按行进顺序排列的路线节点，索引 0 为起点、末尾为终点。\n" +
                 "顺序即推进方向；反向兵线请另建一个对象并倒序排列节点。")]
        public List<Transform> Waypoints = new List<Transform>();

        [Header("调试")]
        [Tooltip("在 Scene 视图中绘制路线折线与节点球，便于摆放与核对节点顺序。")]
        [SerializeField] private bool drawLaneGizmos = true;

        /// <summary>
        /// 是否已就"非法索引"报过警告。
        /// 用途：GetWaypointPosition 会被 FSM 每帧调用，若节点配置错误会导致每帧刷屏；
        /// 这里用一次性标记把日志量压到 1 条，同时仍能在 Console 中看到问题存在。
        /// </summary>
        private bool hasReportedInvalidIndex;

        /// <summary>
        /// 取指定索引节点的世界坐标。
        ///
        /// 越界保护策略：索引非法或节点引用为空时，返回【本对象自身的位置】而非 Vector3.zero。
        /// 原因：调用方通常会把返回值直接交给 MovementComponent.MoveTo()。
        /// 返回自身位置等价于"原地不动"，是一个安全且可观测的降级行为；
        /// 而返回 Vector3.zero 会让单位朝世界原点猛冲，把一个配置错误升级成难以排查的诡异行为。
        /// </summary>
        /// <param name="index">节点索引，取值范围 [0, Waypoints.Count)。</param>
        /// <returns>节点的世界坐标；索引非法时返回本对象位置。</returns>
        public Vector3 GetWaypointPosition(int index)
        {
            if (Waypoints == null || index < 0 || index >= Waypoints.Count)
            {
                ReportInvalidIndexOnce(index);
                return transform.position;
            }

            Transform waypoint = Waypoints[index];

            // 列表里可能存在未赋值（None）的槽位——Inspector 中留空即为此情况。
            // 与索引越界同等对待，避免 NullReferenceException 打断整个 AI 更新循环。
            if (waypoint == null)
            {
                ReportInvalidIndexOnce(index);
                return transform.position;
            }

            return waypoint.position;
        }

        /// <summary>
        /// 报告一次非法索引警告，防止在每帧调用路径上刷屏。
        /// 记录首次出错的索引，便于直接定位是哪个槽位配置有误。
        /// </summary>
        /// <param name="index">本次访问的非法索引。</param>
        private void ReportInvalidIndexOnce(int index)
        {
            if (hasReportedInvalidIndex)
            {
                return;
            }

            hasReportedInvalidIndex = true;
            int count = Waypoints != null ? Waypoints.Count : 0;
            Debug.LogWarning(
                $"[LanePath] {name} 收到非法路线节点索引 {index}（当前节点数 {count}），" +
                "已降级为返回本对象位置。请检查 Waypoints 列表是否存在空槽位或索引越界。", this);
        }

        /// <summary>
        /// Scene 视图可视化：按顺序连接节点并标记每个节点的位置。
        /// 仅在选中该对象时绘制，避免多个 LanePath 同时绘制把视图画满。
        /// </summary>
        private void OnDrawGizmosSelected()
        {
            if (!drawLaneGizmos || Waypoints == null || Waypoints.Count == 0)
            {
                return;
            }

            // 抬升 0.1 米避免折线被地面 Z-Fighting 吃掉。
            Vector3 lift = Vector3.up * 0.1f;

            for (int i = 0; i < Waypoints.Count; i++)
            {
                Transform waypoint = Waypoints[i];

                // 跳过空槽位，否则 Gizmos 绘制会抛异常中断后续节点。
                if (waypoint == null)
                {
                    continue;
                }

                // 黄色小球 = 路径节点；起点/终点用不同颜色区分，方便核对推进方向。
                Gizmos.color = i == 0 ? Color.green
                             : i == Waypoints.Count - 1 ? Color.red
                             : Color.yellow;
                Gizmos.DrawWireSphere(waypoint.position + lift, 0.5f);

                // 青色连线 = 相邻节点的连接关系（仅连到下一个有效节点）。
                if (i + 1 < Waypoints.Count && Waypoints[i + 1] != null)
                {
                    Gizmos.color = Color.cyan;
                    Gizmos.DrawLine(waypoint.position + lift, Waypoints[i + 1].position + lift);
                }
            }
        }
    }
}
