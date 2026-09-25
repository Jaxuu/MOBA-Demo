using UnityEngine;
using UnityEngine.UI;
using MOBA.Core;
using MOBA.Gameplay;

namespace MOBA.UI
{
    /// <summary>
    /// 计分板（阶段七）：双方击杀数 / 死亡数实时显示。
    ///
    /// 职责边界：只读 <see cref="MatchStatsTracker"/> 的计数并显示，
    /// 【不】自己统计（唯一计数器是 MatchStatsTracker）——这样"击杀数与统计完全一致、无重复计数"
    /// 是结构性保证，而不是靠两处各自算对。
    ///
    /// 刷新时机：只在 OnScoreChanged 广播时重写文本（一场对局几十次），稳态零分配。
    /// 两个文本槽位的颜色按"我方 / 敌方"区分，左侧固定为我方阵营（与玩家的观看视角一致）。
    /// </summary>
    [DisallowMultipleComponent]
    public class ScoreboardView : MonoBehaviour
    {
        [Header("数据来源（由一键组装工具注入）")]
        [Tooltip("击杀统计（唯一计数器）。")]
        [SerializeField] private MatchStatsTracker statsTracker;

        [Header("文本槽位（由一键组装工具注入）")]
        [Tooltip("左侧计分文本，固定显示「我方」阵营。")]
        [SerializeField] private Text leftScoreText;

        [Tooltip("右侧计分文本，固定显示「敌方」阵营。")]
        [SerializeField] private Text rightScoreText;

        [Header("配色")]
        [Tooltip("我方计分文本颜色。")]
        [SerializeField] private Color allyTextColor = new Color(0.45f, 0.8f, 1f, 1f);

        [Tooltip("敌方计分文本颜色。")]
        [SerializeField] private Color enemyTextColor = new Color(1f, 0.5f, 0.4f, 1f);

        [Header("阵营")]
        [Tooltip("「我方」阵营。一键组装工具按英雄阵营注入。")]
        [SerializeField] private TeamType localTeam = TeamType.Player;

        /// <summary>订阅计分变化并立即刷新一次（补上"订阅之前已经发生过的击杀"）。</summary>
        private void OnEnable()
        {
            // 字体兜底：uGUI 的 Text 在没有字体时不报错、只是什么都不画（静默失效）。
            UIFontProvider.EnsureFont(leftScoreText);
            UIFontProvider.EnsureFont(rightScoreText);

            if (statsTracker == null)
            {
                Debug.LogWarning(
                    $"[ScoreboardView] {name} 未指定 MatchStatsTracker，计分板不会显示数据。" +
                    "请执行 MOBA Demo/一键组装测试战场 重新生成 UI。", this);
                return;
            }

            statsTracker.OnScoreChanged += HandleScoreChanged;

            // 主动刷一次：本视图可能在对局已经开始之后才被启用（调试时很常见），
            // 那些早已广播过的计分变化不会补发（与 MatchResultView 从权威状态补齐结果同一思路）。
            Refresh();
        }

        /// <summary>退订。</summary>
        private void OnDisable()
        {
            if (statsTracker != null)
            {
                statsTracker.OnScoreChanged -= HandleScoreChanged;
            }
        }

        /// <summary>计分变化回调。</summary>
        private void HandleScoreChanged()
        {
            Refresh();
        }

        /// <summary>
        /// 重写两个计分文本。
        /// 左侧固定为我方阵营，右侧用 <see cref="MatchController.GetOpponentTeam"/> 取对立阵营
        /// （这条换算规则全项目只有一处实现，不在这里另写一遍）。
        /// </summary>
        private void Refresh()
        {
            if (statsTracker == null)
            {
                return;
            }

            ApplyScoreText(leftScoreText, localTeam, allyTextColor);
            ApplyScoreText(rightScoreText, MatchController.GetOpponentTeam(localTeam), enemyTextColor);
        }

        /// <summary>把某阵营的计分写进指定文本。</summary>
        /// <param name="text">目标文本。</param>
        /// <param name="team">阵营。</param>
        /// <param name="color">文本颜色。</param>
        private void ApplyScoreText(Text text, TeamType team, Color color)
        {
            if (text == null)
            {
                return;
            }

            int kills = statsTracker.GetKills(team);
            int deaths = statsTracker.GetDeaths(team);

            text.color = color;
            text.text = $"{MatchStatsTracker.DescribeTeamName(team)}  击杀 {kills} · 死亡 {deaths}";
        }
    }
}
