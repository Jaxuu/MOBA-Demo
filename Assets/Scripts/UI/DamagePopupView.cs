using UnityEngine;
using UnityEngine.UI;

namespace MOBA.UI
{
    /// <summary>
    /// 伤害飘字（阶段七）：单个飘字的显示与生命周期，由 <see cref="DamagePopupManager"/> 池化并驱动。
    ///
    /// 【为什么本类没有 Update】32 个飘字若各自挂一个 MonoBehaviour.Update，就是 32 次每帧回调
    /// （Unity 的每帧消息派发本身有固定开销）。改由管理器在一个循环里 Tick 全部飘字：
    /// 1 个 Update 驱动 32 个对象，且"什么时候该回收"这件事集中在一处，不可能漏回收。
    ///
    /// 【为什么数值文本只在 Play 时写一次】Text.text 的赋值会触发文本网格重建 + 字符串分配。
    /// 飘字的数字是固定的（不会变），因此只写一次；每帧只改 color 与 transform（不产生分配）。
    ///
    /// 【动画：上浮 + 缩放脉冲 + 淡出】
    /// 上浮用 easeOutQuad（先快后慢）——匀速上浮看起来像"被吊上去"，先快后慢才像"被打出来"。
    /// </summary>
    [DisallowMultipleComponent]
    public class DamagePopupView : MonoBehaviour
    {
        [Header("子元素（由一键组装工具注入）")]
        [Tooltip("数字文本。字体由工具注入内置字体，工具拿不到时由 DamagePopupManager 在运行期兜底创建系统字体。")]
        [SerializeField] private Text label;

        [Header("动画")]
        [Tooltip("单条飘字的总存活时长（秒）。")]
        [Min(0.05f)]
        [SerializeField] private float lifetime = 0.8f;

        [Tooltip("整个生命周期内向上飘的距离（米）。")]
        [Min(0f)]
        [SerializeField] private float riseHeight = 1.1f;

        [Tooltip("从生命周期的百分之多少开始淡出（0.6 = 前 60% 保持不透明，后 40% 淡出）。")]
        [Range(0f, 1f)]
        [SerializeField] private float fadeStartRatio = 0.6f;

        [Tooltip("缩放脉冲的峰值倍数（1.15 = 最多放大到 115%，随后回到 100%）。")]
        [Min(1f)]
        [SerializeField] private float popScale = 1.15f;

        [Header("配色")]
        [Tooltip("普通伤害（实际扣减的生命值）的颜色。")]
        [SerializeField] private Color damageColor = new Color(1f, 0.35f, 0.25f, 1f);

        [Tooltip("护盾吸收（伤害被完全挡下）的颜色。")]
        [SerializeField] private Color shieldColor = new Color(0.55f, 0.8f, 1f, 1f);

        /// <summary>飘字的世界锚点（受击位置），上浮在这个点之上进行。</summary>
        private Vector3 anchor;

        /// <summary>已经过的秒数。</summary>
        private float elapsed;

        /// <summary>本条的基准颜色（不含 alpha），淡出时只改 alpha。</summary>
        private Color baseColor = Color.white;

        /// <summary>是否正在播放。</summary>
        private bool isPlaying;

        /// <summary>是否正在播放（只读），供管理器判断。</summary>
        public bool IsPlaying => isPlaying;

        /// <summary>
        /// 开始播放一条飘字。由管理器在取到池对象后立刻调用。
        /// </summary>
        /// <param name="worldPosition">受击位置（世界坐标）。</param>
        /// <param name="content">要显示的数字文本。</param>
        /// <param name="isShieldAbsorbed">true 表示这条是"护盾吸收"（灰蓝字），false 表示普通扣血（红字）。</param>
        public void Play(Vector3 worldPosition, string content, bool isShieldAbsorbed)
        {
            anchor = worldPosition;
            elapsed = 0f;
            isPlaying = true;
            baseColor = isShieldAbsorbed ? shieldColor : damageColor;

            if (label != null)
            {
                label.text = content;
            }

            transform.position = anchor;
            ApplyProgress(0f);
        }

        /// <summary>
        /// 推进一帧。由管理器在它的循环里调用（本类没有自己的 Update）。
        /// </summary>
        /// <param name="deltaTime">本帧时长。</param>
        /// <returns>true 表示播放结束，管理器应当把它归还池中。</returns>
        public bool Tick(float deltaTime)
        {
            if (!isPlaying)
            {
                return true;
            }

            elapsed += deltaTime;

            float progress = lifetime > 0f ? Mathf.Clamp01(elapsed / lifetime) : 1f;
            ApplyProgress(progress);

            if (progress >= 1f)
            {
                isPlaying = false;
                return true;
            }

            return false;
        }

        /// <summary>
        /// 按进度（0~1）刷新位置、缩放与透明度。
        /// 只写属性，不做任何分配 —— 这是"飘字全开时稳态 GC ≈ 0"的关键。
        /// </summary>
        /// <param name="progress">生命周期进度，0 = 刚出现，1 = 该回收。</param>
        private void ApplyProgress(float progress)
        {
            // 上浮：easeOutQuad。先快后慢，符合"被打出来"的观感。
            float rise = 1f - (1f - progress) * (1f - progress);
            transform.position = anchor + Vector3.up * (riseHeight * rise);

            // 缩放脉冲：前 30% 由 0.8 冲到峰值，后 70% 回到 1。给一点"打中"的力度感。
            const float punchRatio = 0.3f;
            float scale;

            if (progress < punchRatio)
            {
                scale = Mathf.Lerp(0.8f, popScale, progress / punchRatio);
            }
            else
            {
                scale = Mathf.Lerp(popScale, 1f, (progress - punchRatio) / (1f - punchRatio));
            }

            transform.localScale = new Vector3(scale, scale, scale);

            if (label != null)
            {
                float alpha = progress <= fadeStartRatio
                    ? 1f
                    : 1f - Mathf.InverseLerp(fadeStartRatio, 1f, progress);

                label.color = new Color(baseColor.r, baseColor.g, baseColor.b, alpha);
            }
        }

        /// <summary>
        /// 被池取出（SetActive(true)）时补一次字体兜底：
        /// uGUI 的 Text 在没有字体时不报错、只是什么都不画（静默失效），
        /// 而飘字是纯数字，一旦不可见就完全查不出问题。
        /// EnsureFont 内部会先判空，正常路径上不会触发文本重建。
        /// </summary>
        private void OnEnable()
        {
            UIFontProvider.EnsureFont(label);
        }

        /// <summary>
        /// 被池归还（SetActive(false)）时复位：清空文本，避免下次取用时先闪一下上一条的数字。
        /// 这也是池只需要 SetActive 就能安全复用的原因之一。
        /// </summary>
        private void OnDisable()
        {
            isPlaying = false;
            elapsed = 0f;

            if (label != null)
            {
                // 置空字符串（而不是 null）：Text 收到 null 会自己转成空串，但显式写出来意图更清楚。
                label.text = string.Empty;
            }
        }
    }
}
