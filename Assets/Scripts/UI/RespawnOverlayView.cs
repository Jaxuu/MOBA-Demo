using UnityEngine;
using UnityEngine.UI;
using MOBA.Gameplay;
using MOBA.Units;

namespace MOBA.UI
{
    /// <summary>
    /// 复活倒计时遮罩（阶段七）：英雄阵亡后压暗屏幕、显示"已阵亡"与倒计时秒数。
    ///
    /// 职责边界：
    /// 1. 只订阅 <see cref="MatchController"/> 的两个跳变事件（倒计时开始 / 复活完成），
    ///    倒计时数字每帧读 <see cref="MatchController.RespawnRemaining"/>；
    /// 2. 【阶段八】只对【玩家英雄】的事件作出反应——MatchController 现在为全部 10 个英雄广播倒计时，
    ///    若不过滤，任意一个 AI 英雄阵亡都会在玩家屏幕上弹出「已阵亡」遮罩；
    /// 3. 【不】自己计时、【不】决定复活时长、【不】碰英雄 —— 它只是一个显示层。
    ///
    /// 【为什么本组件挂在常驻激活的物体上，而不是挂在遮罩面板上】
    /// 若本组件挂在会被 SetActive(false) 的面板上，面板一关，Update 就停了，
    /// 于是"复活完成后要把面板关掉"这件事永远等不到执行。因此：
    /// 本组件挂在常驻物体上，只负责开关子节点 panelRoot。
    ///
    /// 【秒数文本的 GC】与技能冷却同理：只在整秒跳变时写一次，绝不每帧 ToString。
    /// </summary>
    [DisallowMultipleComponent]
    public class RespawnOverlayView : MonoBehaviour
    {
        [Header("数据来源（由一键组装工具注入）")]
        [Tooltip("对局控制器：复活倒计时的唯一编排者。")]
        [SerializeField] private MatchController matchController;

        [Header("子元素（由一键组装工具注入）")]
        [Tooltip("会被整体开关的遮罩面板（含标题与倒计时文本）。")]
        [SerializeField] private GameObject panelRoot;

        [Tooltip("标题文本（默认显示「已阵亡」）。")]
        [SerializeField] private Text titleText;

        [Tooltip("倒计时秒数文本。")]
        [SerializeField] private Text countdownText;

        [Header("文案")]
        [Tooltip("阵亡标题文案。")]
        [SerializeField] private string titleContent = "已阵亡";

        /// <summary>上一次写入的整秒数，-1 表示尚未写过。</summary>
        private int lastDisplayedSecond = -1;

        /// <summary>遮罩当前是否显示。</summary>
        private bool isShowing;

        /// <summary>
        /// 订阅事件并补齐当前状态。
        /// 订阅后必须主动查一次 <see cref="MatchController.IsHeroRespawning"/>：
        /// 本视图可能在对局已经开始之后才被启用（调试时很常见），那次广播不会补发。
        /// </summary>
        private void OnEnable()
        {
            // 字体兜底：uGUI 的 Text 在没有字体时不报错、只是什么都不画（静默失效）。
            UIFontProvider.EnsureFont(titleText);
            UIFontProvider.EnsureFont(countdownText);

            if (matchController == null)
            {
                Debug.LogWarning(
                    $"[RespawnOverlayView] {name} 未指定 MatchController，复活倒计时不会显示。" +
                    "请执行 MOBA Demo/一键组装测试战场 重新生成 UI。", this);
                Hide();
                return;
            }

            matchController.OnRespawnCountdownStarted += HandleCountdownStarted;
            matchController.OnRespawnCompleted += HandleRespawnCompleted;

            if (matchController.IsHeroRespawning)
            {
                Show(matchController.RespawnRemaining);
            }
            else
            {
                Hide();
            }
        }

        /// <summary>退订（不主动隐藏：物体被禁用时遮罩自然不显示）。</summary>
        private void OnDisable()
        {
            if (matchController != null)
            {
                matchController.OnRespawnCountdownStarted -= HandleCountdownStarted;
                matchController.OnRespawnCompleted -= HandleRespawnCompleted;
            }

            isShowing = false;
            lastDisplayedSecond = -1;
        }

        /// <summary>
        /// 每帧刷新倒计时数字。
        /// 对局结束时立即收掉遮罩：那时 MatchResultView 会显示结算界面，两层遮罩叠在一起既看不清也没意义。
        /// </summary>
        private void Update()
        {
            if (!isShowing)
            {
                return;
            }

            if (matchController == null || matchController.IsMatchOver)
            {
                Hide();
                return;
            }

            // 至少显示 1：倒计时最后一帧的剩余量可能是 0.03 秒，CeilToInt 会得到 1，
            // 但若恰好为 0 就会闪出"0 秒"再被隐藏，因此这里夹一个下限。
            int seconds = Mathf.Max(1, Mathf.CeilToInt(matchController.RespawnRemaining));

            if (seconds == lastDisplayedSecond)
            {
                return;
            }

            lastDisplayedSecond = seconds;

            if (countdownText != null)
            {
                countdownText.text = seconds.ToString();
            }
        }

        /// <summary>
        /// 倒计时开始：显示遮罩并立刻写一次秒数（避免第一帧空白或显示 0）。
        ///
        /// 【阶段八：只对玩家英雄响应】MatchController 现在为场上全部 10 个英雄广播倒计时事件
        /// （AI 英雄同样要复活）。若不过滤，任何一个小兵规模的 AI 英雄阵亡都会在玩家屏幕上
        /// 弹出"已阵亡"遮罩——那不是玩家自己的死亡，弹出遮罩是明确的错误反馈。
        /// 过滤放在视图侧而不是 MatchController 侧：事件契约保持不变，
        /// 将来若要做"队友阵亡提示"，只需再订阅一次并换一种呈现方式。
        /// </summary>
        /// <param name="hero">阵亡的英雄。</param>
        /// <param name="duration">倒计时总时长（秒）。</param>
        private void HandleCountdownStarted(HeroController hero, float duration)
        {
            if (matchController == null || !ReferenceEquals(hero, matchController.PlayerHero))
            {
                return;
            }

            Show(duration);
        }

        /// <summary>复活完成：收掉遮罩（同样只对玩家英雄响应，与 HandleCountdownStarted 对称）。</summary>
        /// <param name="hero">已复活的英雄。</param>
        private void HandleRespawnCompleted(HeroController hero)
        {
            if (matchController == null || !ReferenceEquals(hero, matchController.PlayerHero))
            {
                return;
            }

            Hide();
        }

        /// <summary>显示遮罩并写入首个秒数。</summary>
        /// <param name="remaining">剩余秒数。</param>
        private void Show(float remaining)
        {
            isShowing = true;

            if (panelRoot != null && !panelRoot.activeSelf)
            {
                panelRoot.SetActive(true);
            }

            if (titleText != null)
            {
                titleText.text = titleContent;
            }

            int seconds = Mathf.Max(1, Mathf.CeilToInt(remaining));
            lastDisplayedSecond = seconds;

            if (countdownText != null)
            {
                countdownText.text = seconds.ToString();
            }
        }

        /// <summary>隐藏遮罩。</summary>
        private void Hide()
        {
            isShowing = false;
            lastDisplayedSecond = -1;

            if (panelRoot != null && panelRoot.activeSelf)
            {
                panelRoot.SetActive(false);
            }
        }
    }
}
