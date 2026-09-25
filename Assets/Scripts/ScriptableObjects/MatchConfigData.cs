using UnityEngine;

namespace MOBA.Data
{
    /// <summary>
    /// 对局全局配置模板（只读）。
    /// 职责边界：只存放对局级别的静态参数；对局运行期状态（当前阶段、阶段计时器、胜负结果）
    /// 由 MatchController 实例持有，严禁写回本资产。
    /// 实现范围说明：计划书 3.3 还列出「胜负目标 / 默认阵营配置」，当前 MVP 既无多模式也无多阵营配置，
    /// 按「不提前造轮子」原则只实现真正有消费方的字段，待真正需要时再按增量方式补充，
    /// 避免留下永远不会被读取的僵尸配置。
    /// </summary>
    [CreateAssetMenu(
        fileName = "MatchConfigData_New",
        menuName = "MOBA/Match Config Data",
        order = 3)]
    public class MatchConfigData : ScriptableObject
    {
        [Header("开局流程")]
        [Tooltip("开局出兵延迟（秒）。对局进入运行阶段后先等待该时长再启动首波小兵生成，" +
                 "给玩家留出观察战场、熟悉操作的时间。")]
        [Min(0f)]
        [SerializeField] private float startDelay = 5f;

        [Header("复活规则")]
        [Tooltip("英雄死亡后到重生的等待时长（秒）。阶段七的复活倒计时与 RespawnOverlayView 都读这个值。\n" +
                 "0 表示下一帧立即复活（仍会走一帧延迟，不会在 OnDied 回调里同步复活）。\n" +
                 "注意：对局结束后不再复活，该配置不再生效。")]
        [Min(0f)]
        [SerializeField] private float respawnTime = 8f;

        /// <summary>开局出兵延迟（秒）。</summary>
        public float StartDelay => startDelay;

        /// <summary>英雄复活等待时长（秒）。由 MatchController 读取并统筹倒计时。</summary>
        public float RespawnTime => respawnTime;
    }
}
