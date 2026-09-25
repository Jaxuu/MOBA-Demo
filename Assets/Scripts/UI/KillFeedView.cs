using UnityEngine;
using UnityEngine.UI;
using MOBA.Core;
using MOBA.Gameplay;

namespace MOBA.UI
{
    /// <summary>
    /// 击杀播报（阶段七）：屏幕顶部的消息队列，新消息在最上、整体下移，到时间自动淡出。
    ///
    /// 职责边界：只订阅 <see cref="MatchStatsTracker.OnKillLogged"/> 并把它显示出来，
    /// 【不】自己统计击杀（那是 MatchStatsTracker 的唯一职责）。
    ///
    /// 【为什么用固定槽位而不是对象池】播报条目数量是固定的（本 Demo 取 5 条），
    /// 场景里预生成 5 个 Text 循环复用最省：不需要池、不需要 Instantiate、也不会出现"池耗尽时播报丢失"。
    /// 这与血条/飘字（数量随单位数变化）不同——那两者才需要池。
    ///
    /// 【为什么不用 VerticalLayoutGroup】布局组在内容变化时会触发布局重建（LayoutRebuilder），
    /// 那是一次带分配的整棵树计算。槽位坐标由一键组装工具一次性摆好（固定 anchoredPosition），
    /// 本类只改 text 与 color，稳态零分配。
    ///
    /// 【字符串分配】每条播报拼一次字符串（"蓝方英雄 击杀了 红方英雄"）。
    /// 这是"每次事件固有分配"：一场对局几十条，且不在每帧路径上。
    /// </summary>
    [DisallowMultipleComponent]
    public class KillFeedView : MonoBehaviour
    {
        [Header("数据来源（由一键组装工具注入）")]
        [Tooltip("击杀统计与播报事件源。")]
        [SerializeField] private MatchStatsTracker statsTracker;

        [Header("槽位（由一键组装工具注入，自顶向下）")]
        [Tooltip("固定数量的播报文本槽位：索引 0 在最上方（最新），依次向下。")]
        [SerializeField] private Text[] entryTexts;

        [Header("时序")]
        [Tooltip("一条播报保持完全可见的时长（秒），之后开始淡出。")]
        [Min(0f)]
        [SerializeField] private float fadeDelay = 3f;

        [Tooltip("淡出过程时长（秒）。")]
        [Min(0.01f)]
        [SerializeField] private float fadeDuration = 0.5f;

        [Header("配色")]
        [Tooltip("「我方」阵营的击杀颜色。")]
        [SerializeField] private Color allyKillColor = new Color(0.45f, 0.85f, 1f, 1f);

        [Tooltip("「敌方」阵营的击杀颜色。")]
        [SerializeField] private Color enemyKillColor = new Color(1f, 0.5f, 0.4f, 1f);

        [Tooltip("无归属阵亡（无人击杀 / 同阵营误伤）的颜色。")]
        [SerializeField] private Color neutralKillColor = new Color(0.75f, 0.75f, 0.75f, 1f);

        [Header("阵营")]
        [Tooltip("「我方」阵营，用于决定播报颜色。一键组装工具按英雄阵营注入。")]
        [SerializeField] private TeamType localTeam = TeamType.Player;

        /// <summary>每个槽位已存在的时长；小于 0 表示空槽。</summary>
        private float[] entryElapsed;

        /// <summary>每个槽位的基准颜色（不含 alpha），淡出时只改 alpha。</summary>
        private Color[] entryColors;

        /// <summary>槽位数量是否已经初始化（数组在 Awake 里按 entryTexts 长度分配）。</summary>
        private bool isInitialized;

        /// <summary>是否已就"没有配置播报槽位"告警过一次。</summary>
        private bool hasReportedMissingEntries;

        /// <summary>订阅播报事件。</summary>
        private void OnEnable()
        {
            EnsureInitialized();

            if (statsTracker != null)
            {
                statsTracker.OnKillLogged += HandleKillLogged;
            }
            else
            {
                Debug.LogWarning(
                    $"[KillFeedView] {name} 未指定 MatchStatsTracker，击杀播报不会出现。" +
                    "请执行 MOBA Demo/一键组装测试战场 重新生成 UI。", this);
            }
        }

        /// <summary>退订并清空全部槽位。</summary>
        private void OnDisable()
        {
            if (statsTracker != null)
            {
                statsTracker.OnKillLogged -= HandleKillLogged;
            }

            ClearAllEntries();
        }

        /// <summary>
        /// 每帧推进各槽位的计时与淡出。
        /// 只在"已占用且进入淡出阶段"的槽位上写颜色，空槽位直接跳过。
        /// </summary>
        private void Update()
        {
            // 两个判空都要有：槽位为空时 isInitialized 会保持 false，而 entryElapsed 也可能是 null。
            if (!isInitialized || entryElapsed == null || entryTexts == null)
            {
                return;
            }

            float deltaTime = Time.deltaTime;

            for (int i = 0; i < entryElapsed.Length; i++)
            {
                if (entryElapsed[i] < 0f)
                {
                    continue;
                }

                entryElapsed[i] += deltaTime;

                if (entryElapsed[i] < fadeDelay)
                {
                    continue;
                }

                float alpha = 1f - Mathf.InverseLerp(fadeDelay, fadeDelay + fadeDuration, entryElapsed[i]);

                if (alpha <= 0f)
                {
                    ClearEntry(i);
                    continue;
                }

                Text text = entryTexts[i];
                if (text == null)
                {
                    continue;
                }

                Color baseColor = entryColors[i];
                text.color = new Color(baseColor.r, baseColor.g, baseColor.b, alpha);
            }
        }

        /// <summary>按槽位数量分配并行数组。槽位由工具生成，运行期不会变。</summary>
        private void EnsureInitialized()
        {
            if (isInitialized)
            {
                return;
            }

            if (entryTexts == null || entryTexts.Length == 0)
            {
                // 刻意【不】把 isInitialized 置为 true：置了之后本方法就再也不会重试，
                // 而调用方（Update / HandleKillLogged）依赖 isInitialized 判断"数组可用"，
                // 一旦置 true 却留下 null 的 entryElapsed，下一帧就会空引用。
                if (!hasReportedMissingEntries)
                {
                    hasReportedMissingEntries = true;
                    Debug.LogWarning(
                        $"[KillFeedView] {name} 没有配置任何播报槽位，击杀播报不会显示。" +
                        "请执行 MOBA Demo/一键组装测试战场 重新生成 UI。", this);
                }

                return;
            }

            isInitialized = true;
            entryElapsed = new float[entryTexts.Length];
            entryColors = new Color[entryTexts.Length];

            // 字体兜底：uGUI 的 Text 在没有字体时不报错、只是什么都不画（静默失效）。
            for (int i = 0; i < entryTexts.Length; i++)
            {
                UIFontProvider.EnsureFont(entryTexts[i]);
            }

            ClearAllEntries();
        }

        /// <summary>
        /// 新播报入队：整体下移一格，最新的一条占据最上方槽位。
        /// 下移而不是"找空槽"，是为了让时间顺序在视觉上始终是自上而下（最新在顶）。
        /// 代价是最旧的条目被顶掉（FIFO），这正是队列语义。
        /// </summary>
        /// <param name="record">击杀记录。</param>
        private void HandleKillLogged(KillRecord record)
        {
            EnsureInitialized();

            if (!isInitialized || entryTexts == null || entryTexts.Length == 0)
            {
                return;
            }

            // 从下往上搬：倒序保证"搬运目标"总是尚未被覆盖的空位。
            for (int i = entryTexts.Length - 1; i >= 1; i--)
            {
                MoveEntry(i - 1, i);
            }

            ApplyEntry(0, record);
        }

        /// <summary>把一条记录写进指定槽位，并重置它的计时。</summary>
        /// <param name="index">槽位索引。</param>
        /// <param name="record">击杀记录。</param>
        private void ApplyEntry(int index, KillRecord record)
        {
            Text text = entryTexts[index];
            if (text == null)
            {
                return;
            }

            Color baseColor = ResolveColor(record);

            entryColors[index] = baseColor;
            entryElapsed[index] = 0f;

            text.text = BuildMessage(record);
            text.color = baseColor;
        }

        /// <summary>把 from 槽位的文本、颜色与计时搬到 to 槽位（不清空 from，由后续写入覆盖）。</summary>
        /// <param name="from">源索引。</param>
        /// <param name="to">目标索引。</param>
        private void MoveEntry(int from, int to)
        {
            Text source = entryTexts[from];
            Text target = entryTexts[to];

            if (target == null)
            {
                return;
            }

            if (source == null || entryElapsed[from] < 0f)
            {
                // 源是空槽 → 目标也置空（保持"空槽永远在下方"的连续性）。
                ClearEntry(to);
                return;
            }

            target.text = source.text;
            target.color = source.color;

            entryColors[to] = entryColors[from];
            entryElapsed[to] = entryElapsed[from];
        }

        /// <summary>清空一个槽位（文本置空、计时标记为空）。</summary>
        /// <param name="index">槽位索引。</param>
        private void ClearEntry(int index)
        {
            entryElapsed[index] = -1f;

            Text text = entryTexts[index];
            if (text != null)
            {
                // 置空字符串而不是 null：语义更明确，也避免 Text 内部再做一次 null 判断。
                text.text = string.Empty;
            }
        }

        /// <summary>清空全部槽位。</summary>
        private void ClearAllEntries()
        {
            if (entryTexts == null)
            {
                return;
            }

            for (int i = 0; i < entryTexts.Length; i++)
            {
                if (entryElapsed != null)
                {
                    entryElapsed[i] = -1f;
                }

                Text text = entryTexts[i];
                if (text != null)
                {
                    text.text = string.Empty;
                }
            }
        }

        /// <summary>
        /// 按"击杀者阵营 vs 我方阵营"决定播报颜色：
        /// 我方击杀 = 冷色（好消息），敌方击杀 = 暖色，无归属 = 灰色。
        /// </summary>
        /// <param name="record">击杀记录。</param>
        /// <returns>基准颜色。</returns>
        private Color ResolveColor(KillRecord record)
        {
            if (!record.HasKiller)
            {
                return neutralKillColor;
            }

            return record.KillerTeam == localTeam ? allyKillColor : enemyKillColor;
        }

        /// <summary>拼装播报文案，例如「蓝方英雄 击杀了 红方英雄」或「蓝方英雄 阵亡」。</summary>
        /// <param name="record">击杀记录。</param>
        /// <returns>播报文本。</returns>
        private static string BuildMessage(KillRecord record)
        {
            if (!record.HasKiller)
            {
                return $"{MatchStatsTracker.DescribeTeamName(record.VictimTeam)}{record.VictimName} 阵亡";
            }

            return $"{MatchStatsTracker.DescribeTeamName(record.KillerTeam)}{record.KillerName}" +
                   $" 击杀了 {MatchStatsTracker.DescribeTeamName(record.VictimTeam)}{record.VictimName}";
        }
    }
}
