using UnityEngine;
using MOBA.Components;
using MOBA.Core;
using MOBA.Gameplay;
using MOBA.Units;

// 【命名空间说明 · 必读】本文件位于 Assets/Scripts/Debug/，但命名空间刻意【不】取 MOBA.Debug，
// 而是 MOBA.Debugging —— 这是本项目"命名空间 = MOBA.<目录名>"约定的一处必要例外。
//
// 原因：C# 的名字查找中，外层命名空间的成员【优先于】using 导入。
// 一旦存在命名空间 MOBA.Debug，那么 MOBA 下所有文件里的 `Debug.Log(...)`
// 都会把 `Debug` 解析成这个命名空间，从而报 CS0234，一次性打挂整个项目的编译
// （本项目实测波及 14 个文件、67 处调用）。因此目录名保持 Debug/，命名空间用 MOBA.Debugging。
namespace MOBA.Debugging
{
    /// <summary>
    /// 对局调试视图：在屏幕上直接摧毁任意一方的基地，用于快速验证"基地归零 → 停止出兵 → 判定胜负"
    /// 这条闭环，而不必等待小兵慢慢推塔（完整推塔链路在验收时往往要跑好几分钟）。
    ///
    /// 职责边界：
    /// 1. 本类只做两件事——"显示对局状态"与"按需对基地造成致命伤害"，不参与任何对局逻辑；
    /// 2. 不自己判定胜负，胜负状态完全来自 BaseCoreController.OnBaseDestroyed 广播，
    ///    获胜方换算复用 MatchController.GetOpponentTeam，保证与 MatchController 的口径一致；
    /// 3. 属于调试工具，可随时挂上或摘下（挂在场景中任意空物体上即可），不影响正式流程。
    ///
    /// 为什么用 FindObjectsOfType 而不是让策划手动拖引用：
    /// 调试脚本的价值在于"随手挂上去就能用"。它只在点击按钮时执行（不是每帧），
    /// 因此全场景扫描的开销完全可以接受，换来的是零配置。
    /// </summary>
    [DisallowMultipleComponent]
    public class MatchDebugView : MonoBehaviour
    {
        [Header("调试伤害")]
        [Tooltip("点击按钮时对目标基地造成的伤害值。默认 9999，足以秒掉任何合理配置的基地血量。")]
        [Min(1f)]
        [SerializeField] private float debugDamage = 9999f;

        [Header("界面")]
        [Tooltip("顶部状态文字的字号。")]
        [Min(10)]
        [SerializeField] private int statusFontSize = 34;

        [Tooltip("对局进行中时状态文字的颜色。")]
        [SerializeField] private Color inProgressColor = new Color(1f, 0.85f, 0.2f);

        [Tooltip("对局结束时状态文字的颜色。")]
        [SerializeField] private Color finishedColor = new Color(0.3f, 1f, 0.4f);

        [Tooltip("顶部状态栏的高度（像素）。")]
        [SerializeField] private float statusBarHeight = 56f;

        /// <summary>
        /// 顶部状态文字的样式。
        /// 缓存而不每帧新建：GUIStyle 的构造会访问 GUI.skin（只能在 OnGUI 内调用），
        /// 且每帧 new 一个 GUIStyle 会产生不必要的 GC 垃圾。
        /// </summary>
        private GUIStyle statusStyle;

        /// <summary>对局是否已结束。只记录第一次基地被摧毁，与 MatchController 的判定口径保持一致。</summary>
        private bool isMatchOver;

        /// <summary>被摧毁基地的阵营，用于状态文案。</summary>
        private TeamType destroyedTeam = TeamType.Neutral;

        /// <summary>获胜阵营。对局未结束时为 TeamType.Neutral（表示"尚未决出"）。</summary>
        private TeamType winnerTeam = TeamType.Neutral;

        /// <summary>订阅基地摧毁事件（与 MatchController 一样放在 OnEnable，保证启用期间才能收到广播）。</summary>
        private void OnEnable()
        {
            BaseCoreController.OnBaseDestroyed += HandleBaseDestroyed;
        }

        /// <summary>
        /// 注销事件监听。
        /// 必要性：OnBaseDestroyed 是静态事件，委托链的生命周期属于类型而非实例。
        /// 不注销的话，场景重载后旧的（已销毁的）MatchDebugView 仍留在委托链上，
        /// 下一次基地被摧毁时会回调到已销毁对象，直接抛 MissingReferenceException，同时造成内存泄漏。
        /// </summary>
        private void OnDisable()
        {
            BaseCoreController.OnBaseDestroyed -= HandleBaseDestroyed;
        }

        /// <summary>
        /// 与"订阅后必须主动读一次当前值"同一思路：
        /// 若本视图是在某座基地已被摧毁之后才被启用（调试时很常见——出问题了才想起来挂上），
        /// 那次广播早已发生且不会补发，状态会一直错误地显示为"进行中"。
        /// 这里主动扫一次场景把状态对齐到真实情况。只在 Start 执行一次，不是每帧。
        /// </summary>
        private void Start()
        {
            BaseCoreController[] bases = FindObjectsOfType<BaseCoreController>();

            for (int i = 0; i < bases.Length; i++)
            {
                BaseCoreController candidate = bases[i];

                // 空槽位与已销毁对象都会命中 Unity 重载过的 == null。
                if (candidate == null || !candidate.IsDestroyed)
                {
                    continue;
                }

                HandleBaseDestroyed(candidate.Team);
                return;
            }
        }

        /// <summary>
        /// 绘制界面：顶部大字号状态 + 两个摧毁按钮。
        /// 用 OnGUI 而不是 UGUI：调试工具不需要 Canvas / EventSystem / 预制体，
        /// 挂上就能用，摘掉不留痕。
        /// </summary>
        private void OnGUI()
        {
            EnsureStyles();

            // ---------- 顶部状态栏 ----------
            float statusWidth = Mathf.Min(900f, Screen.width - 40f);
            float statusLeft = (Screen.width - statusWidth) * 0.5f;

            statusStyle.normal.textColor = isMatchOver ? finishedColor : inProgressColor;
            GUI.Label(new Rect(statusLeft, 12f, statusWidth, statusBarHeight), BuildStatusText(), statusStyle);

            // ---------- 两个摧毁按钮 ----------
            const float buttonWidth = 220f;
            const float buttonHeight = 48f;
            const float buttonGap = 20f;

            float buttonsTotalWidth = buttonWidth * 2f + buttonGap;
            float buttonLeft = (Screen.width - buttonsTotalWidth) * 0.5f;
            float buttonTop = 12f + statusBarHeight + 16f;

            if (GUI.Button(new Rect(buttonLeft, buttonTop, buttonWidth, buttonHeight), "摧毁玩家基地"))
            {
                DamageBaseOfTeam(TeamType.Player);
            }

            if (GUI.Button(new Rect(buttonLeft + buttonWidth + buttonGap, buttonTop, buttonWidth, buttonHeight), "摧毁敌方基地"))
            {
                DamageBaseOfTeam(TeamType.Enemy);
            }

            // 提示：对局结束后按钮仍可点击，便于验证"两座基地先后被摧毁时只认第一次"这一行为。
            GUI.Label(
                new Rect(buttonLeft, buttonTop + buttonHeight + 6f, buttonsTotalWidth, 24f),
                $"调试伤害 {debugDamage:F0}｜对局结束后按钮仍可点击，用于验证胜负只认第一次");
        }

        /// <summary>惰性创建 GUI 样式。必须在 OnGUI 内调用（构造 GUIStyle 会访问 GUI.skin）。</summary>
        private void EnsureStyles()
        {
            if (statusStyle != null)
            {
                return;
            }

            statusStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = statusFontSize,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };
        }

        /// <summary>拼装顶部状态文案。</summary>
        private string BuildStatusText()
        {
            if (!isMatchOver)
            {
                return "对局进行中";
            }

            return $"对局结束 —— {DescribeTeam(destroyedTeam)}基地被摧毁，{DescribeTeam(winnerTeam)}获胜";
        }

        /// <summary>把阵营枚举转成便于阅读的中文描述，仅用于界面与日志。</summary>
        private static string DescribeTeam(TeamType team)
        {
            switch (team)
            {
                case TeamType.Player:
                    return "玩家方";
                case TeamType.Enemy:
                    return "敌方";
                default:
                    return "中立方";
            }
        }

        /// <summary>
        /// 对指定阵营的基地造成调试伤害。
        /// 之所以"直接调 HealthComponent.TakeDamage"而不是新增一条调试专用的击杀接口：
        /// TakeDamage 是 IDamageable 的唯一伤害入口，走它才能完整触发
        /// "扣血 → 广播血量变化 → 判定死亡 → 广播 OnDied → BaseCoreController 广播 OnBaseDestroyed"
        /// 这条真实链路，验证的才是真正的胜负闭环，而不是一条只在调试脚本里成立的捷径。
        /// </summary>
        /// <param name="team">要摧毁的基地阵营。</param>
        private void DamageBaseOfTeam(TeamType team)
        {
            BaseCoreController[] bases = FindObjectsOfType<BaseCoreController>();

            for (int i = 0; i < bases.Length; i++)
            {
                BaseCoreController candidate = bases[i];

                if (candidate == null || candidate.Team != team)
                {
                    continue;
                }

                if (candidate.IsDestroyed)
                {
                    // 该阵营的基地已经没了，找下一个（正常场景里只有一个）。
                    continue;
                }

                HealthComponent health = candidate.GetComponent<HealthComponent>();

                if (health == null)
                {
                    Debug.LogWarning(
                        $"[MatchDebugView] 基地 {candidate.name} 未挂载 HealthComponent，无法造成伤害。", this);
                    continue;
                }

                Debug.Log(
                    $"[MatchDebugView] 对基地 {candidate.name}（{DescribeTeam(team)}）造成 {debugDamage:F0} 点调试伤害。", this);

                health.TakeDamage(debugDamage);
                return;
            }

            Debug.LogWarning(
                $"[MatchDebugView] 场景中找不到存活的{DescribeTeam(team)}基地（BaseCoreController）。" +
                "请确认场景里已放置对应阵营的基地。", this);
        }

        /// <summary>
        /// 基地摧毁回调：记录第一次结果并换算获胜方。
        /// 只认第一次的原因与 MatchController 一致：两座基地可能在极短时间内相继归零，
        /// 后一次不应覆盖先一次的胜负。
        /// </summary>
        private void HandleBaseDestroyed(TeamType destroyed)
        {
            if (isMatchOver)
            {
                return;
            }

            isMatchOver = true;
            destroyedTeam = destroyed;

            // 复用 MatchController 的换算规则，避免"被摧毁方 → 获胜方"这条规则在两处各写一遍。
            winnerTeam = MatchController.GetOpponentTeam(destroyed);
        }
    }
}
