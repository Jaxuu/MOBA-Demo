using UnityEngine;
using UnityEngine.SceneManagement;
using MOBA.Core;
using MOBA.Gameplay;
using MOBA.Units;

namespace MOBA.UI
{
    /// <summary>
    /// 对局结算视图：基地被摧毁后，在屏幕正中显示胜负结果，并提供「重新加载场景」按钮。
    /// （计划书第 2 节目录结构中的 UI/MatchResultView.cs）
    ///
    /// 职责边界：
    /// 1. 只做两件事——「显示胜负结果」与「重开对局」，不参与任何对局逻辑；
    /// 2. 不自己判断胜负：结果来自 BaseCoreController.OnBaseDestroyed 广播，
    ///    「被摧毁方 → 获胜方」的换算复用 MatchController.GetOpponentTeam（这条规则只能有一处实现）；
    /// 3. 与 Debug/MatchDebugView 的分工：后者是「随手挂上就能用」的调试工具（OnGUI 按钮直接秒基地），
    ///    本类才是验收要求的正式结算界面。两者互不依赖。
    ///
    /// 阵营与颜色的约定：玩家方 = 蓝方，敌方 = 红方（沿用主流 MOBA 的蓝/红方说法）。
    /// 文案与颜色都暴露在 Inspector 上，若项目后续改用别的叫法，改配置即可，不必改代码。
    ///
    /// 为什么用 OnGUI 而不是 UGUI：本 Demo 仍处于「以胶囊体/方块代替模型」的表现阶段，
    /// 为验证玩法闭环引入 Canvas / EventSystem / 预制体没有收益；OnGUI 挂上即可用、摘掉不留痕。
    /// 后续接入正式美术资源时，把本类替换成 UGUI 实现即可，对外契约（订阅 OnBaseDestroyed + 显示结果）不变。
    /// </summary>
    [DisallowMultipleComponent]
    public class MatchResultView : MonoBehaviour
    {
        [Header("数据来源")]
        [Tooltip("对局控制器（可选）。填了它之后，本视图在 Start 时能从 MatchController 的权威状态补齐结果——" +
                 "用于「本视图在对局结束之后才被启用」的情况（那时事件早已广播过，订阅不会补发）。")]
        [SerializeField] private MatchController matchController;

        [Header("界面")]
        [Tooltip("胜负文案的字号。")]
        [Min(20)]
        [SerializeField] private int resultFontSize = 72;

        [Tooltip("结算遮罩的不透明度：0 表示不压暗战场，1 表示全黑。用于让结算文字成为视觉焦点。")]
        [Range(0f, 1f)]
        [SerializeField] private float overlayAlpha = 0.55f;

        [Tooltip("玩家方（蓝方）获胜时的文案颜色。")]
        [SerializeField] private Color playerWinColor = new Color(0.35f, 0.65f, 1f);

        [Tooltip("敌方（红方）获胜时的文案颜色。")]
        [SerializeField] private Color enemyWinColor = new Color(1f, 0.4f, 0.35f);

        [Tooltip("胜负文案距离屏幕垂直中心向上偏移的像素数。")]
        [SerializeField] private float bannerOffsetY = 60f;

        [Tooltip("「重新加载场景」按钮的尺寸（像素）。")]
        [SerializeField] private Vector2 reloadButtonSize = new Vector2(280f, 60f);

        [Header("调试")]
        [Tooltip("在 Console 输出结算显示与重新加载的记录。")]
        [SerializeField] private bool logResultEvents = true;

        /// <summary>
        /// 胜负文案样式。
        /// 缓存而不每帧新建：GUIStyle 的构造会访问 GUI.skin（只能在 OnGUI 内调用），
        /// 且每帧 new 一个 GUIStyle 会产生不必要的 GC 垃圾。
        /// </summary>
        private GUIStyle resultStyle;

        /// <summary>对局是否已结束。只认第一次基地被摧毁，与 MatchController 的判定口径保持一致。</summary>
        private bool isMatchOver;

        /// <summary>获胜阵营。对局未结束时为 Neutral（表示「尚未决出」）。</summary>
        private TeamType winnerTeam = TeamType.Neutral;

        /// <summary>订阅基地摧毁事件。放在 OnEnable：从物体被启用起就应具备接收能力。</summary>
        private void OnEnable()
        {
            BaseCoreController.OnBaseDestroyed += HandleBaseDestroyed;
        }

        /// <summary>
        /// 注销基地摧毁事件。
        /// 必要性：OnBaseDestroyed 是【静态】事件，其委托链的生命周期属于类型而非实例，
        /// 不主动注销就会让已禁用/已销毁的本视图一直被持有并继续接收回调（内存泄漏 + 重复响应）。
        /// </summary>
        private void OnDisable()
        {
            BaseCoreController.OnBaseDestroyed -= HandleBaseDestroyed;
        }

        /// <summary>
        /// 「订阅后必须主动读一次当前值」：若本视图在对局结束后才被启用（调试时很常见——出问题了才想起来挂上），
        /// 那次广播早已发生且不会补发，界面会一直不显示结果。
        /// 这里从 MatchController 的权威状态补齐，而不是自己再扫一遍场景找基地
        /// （全场景扫描既昂贵，又表达不出「对局状态」这个语义）。
        /// </summary>
        private void Start()
        {
            if (matchController == null || !matchController.IsMatchOver)
            {
                return;
            }

            isMatchOver = true;
            winnerTeam = matchController.WinnerTeam;

            if (logResultEvents)
            {
                Debug.Log(
                    $"[MatchResultView] 启用时对局已结束，已从 MatchController 补齐结果：{DescribeWinner()}。", this);
            }
        }

        /// <summary>
        /// 绘制结算界面。
        /// 对局未结束时不绘制任何内容——本视图只在结算阶段出现，不占用战斗中的屏幕空间。
        /// </summary>
        private void OnGUI()
        {
            if (!isMatchOver)
            {
                return;
            }

            // GUIStyle 必须在 OnGUI 内惰性构造（构造时会访问 GUI.skin）。
            EnsureStyles();

            // ---------- 全屏半透明遮罩 ----------
            // 压暗战场，让结算文字成为视觉焦点。alpha 为 0 时跳过，省掉一次全屏绘制。
            if (overlayAlpha > 0f)
            {
                Color previousColor = GUI.color;
                GUI.color = new Color(0f, 0f, 0f, overlayAlpha);
                GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
                GUI.color = previousColor;
            }

            // ---------- 屏幕正中：胜负文案 ----------
            const float bannerWidth = 1000f;
            const float bannerHeight = 140f;

            float bannerLeft = (Screen.width - bannerWidth) * 0.5f;
            float bannerTop = (Screen.height - bannerHeight) * 0.5f - bannerOffsetY;

            resultStyle.normal.textColor = winnerTeam == TeamType.Player ? playerWinColor : enemyWinColor;
            GUI.Label(new Rect(bannerLeft, bannerTop, bannerWidth, bannerHeight), BuildResultText(), resultStyle);

            // ---------- 重新加载场景按钮 ----------
            float buttonWidth = Mathf.Max(80f, reloadButtonSize.x);
            float buttonHeight = Mathf.Max(24f, reloadButtonSize.y);

            Rect buttonRect = new Rect(
                (Screen.width - buttonWidth) * 0.5f,
                bannerTop + bannerHeight + 24f,
                buttonWidth,
                buttonHeight);

            if (GUI.Button(buttonRect, "重新加载场景"))
            {
                ReloadScene();
            }
        }

        /// <summary>惰性创建 GUI 样式。必须在 OnGUI 内调用（构造 GUIStyle 会访问 GUI.skin）。</summary>
        private void EnsureStyles()
        {
            if (resultStyle != null)
            {
                return;
            }

            resultStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = resultFontSize,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };
        }

        /// <summary>拼装胜负文案，例如「蓝方胜利」。</summary>
        private string BuildResultText()
        {
            return $"{DescribeWinner()}胜利";
        }

        /// <summary>把获胜阵营转成界面文案（玩家方 = 蓝方，敌方 = 红方）。</summary>
        private string DescribeWinner()
        {
            switch (winnerTeam)
            {
                case TeamType.Player:
                    return "蓝方";
                case TeamType.Enemy:
                    return "红方";
                default:
                    // 本 Demo 没有中立阵营，走到这里说明结果尚未决出（正常不会显示）。
                    return "中立";
            }
        }

        /// <summary>
        /// 重新加载场景，等价于「重开一局」。
        ///
        /// 用场景重载而不是"复位所有单位状态"：本 Demo 的静态状态（BaseCoreController.OnBaseDestroyed、
        /// EntityRegistry）都已按 SubsystemRegistration 做了重置，重载场景即可得到干净的一局，
        /// 不需要为每个单位写一套可逆的冻结/解冻逻辑。
        /// </summary>
        private void ReloadScene()
        {
            // 防御性检查：Build Settings 里一个场景都没有时，LoadScene(0) 会抛异常。
            // 这里提前拦下并给出可操作的提示，而不是让玩家点出一个报错。
            if (SceneManager.sceneCountInBuildSettings <= 0)
            {
                Debug.LogWarning(
                    "[MatchResultView] Build Settings 中没有任何场景，无法重新加载。" +
                    "请先把 Gameplay 场景加入 File > Build Settings 的 Scenes In Build 列表。", this);
                return;
            }

            if (logResultEvents)
            {
                Debug.Log("[MatchResultView] 重新加载场景（Build Settings 索引 0），开始新一局。", this);
            }

            SceneManager.LoadScene(0);
        }

        /// <summary>
        /// 基地摧毁回调：记录结果并换算获胜方。
        /// 只认第一次的原因与 MatchController 一致：两座基地可能在极短时间内相继归零，
        /// 后一次不应覆盖先一次的胜负。
        /// </summary>
        /// <param name="destroyedTeam">被摧毁基地的阵营，由 BaseCoreController 广播。</param>
        private void HandleBaseDestroyed(TeamType destroyedTeam)
        {
            if (isMatchOver)
            {
                return;
            }

            isMatchOver = true;

            // 复用 MatchController 的换算规则，避免「被摧毁方 → 获胜方」这条规则在两处各写一遍。
            winnerTeam = MatchController.GetOpponentTeam(destroyedTeam);

            if (logResultEvents)
            {
                Debug.Log($"[MatchResultView] 对局结束，结算界面显示：{BuildResultText()}。", this);
            }
        }
    }
}
