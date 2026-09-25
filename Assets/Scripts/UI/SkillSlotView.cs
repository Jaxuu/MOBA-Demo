using UnityEngine;
using UnityEngine.UI;
using MOBA.Skills;

namespace MOBA.UI
{
    /// <summary>
    /// 单个技能槽视图（阶段七）：图标 + 冷却径向遮罩 + 秒数 + 蓝耗 + 按键提示。
    ///
    /// 职责边界：只读 <see cref="SkillComponent"/> 的公开查询接口（GetSkillData / GetCooldownRemaining），
    /// 【不】参与施法校验，也【不】扣蓝起 CD —— 那些是 SkillComponent 的唯一职责。
    ///
    /// 【冷却为什么可以每帧查询，而血量/蓝量必须事件驱动】
    /// README 的「禁止每帧轮询」针对的是【数值】：血量与蓝量都有变化事件，轮询等于白读。
    /// 冷却不同——它是一个随时间递减的连续量，没有任何"变化事件"（每次变化都发事件等于每帧发一次）。
    /// 要满足验收标准「CD 遮罩进度与实际可释放时刻误差 ≤ 0.05s」，唯一可靠的方式就是每帧读一次
    /// GetCooldownRemaining：它内部只是一次 float 减法 + 一次比较，零分配、零物理查询。
    ///
    /// 【真正的 GC 陷阱在秒数文本】每帧 text = remaining.ToString("F1") 会在 60FPS 下每秒产生 60 个字符串，
    /// 直接顶掉"UI 稳态 GC ≈ 0 B/帧"的验收目标。因此秒数只在【整秒跳变】时写一次。
    /// </summary>
    [DisallowMultipleComponent]
    public class SkillSlotView : MonoBehaviour
    {
        [Header("槽位")]
        [Tooltip("本视图对应的技能槽位。由一键组装工具注入（Q / W 各一个）。")]
        [SerializeField] private SkillSlot slot = SkillSlot.Q;

        [Header("子元素（由一键组装工具注入）")]
        [Tooltip("技能图标。SkillData.Icon 为空时保留工具注入的占位白图。")]
        [SerializeField] private Image iconImage;

        [Tooltip("冷却遮罩（Image.type = Filled / Radial360），fillAmount = 剩余冷却 / 总冷却。")]
        [SerializeField] private Image cooldownOverlay;

        [Tooltip("冷却剩余秒数文本。")]
        [SerializeField] private Text cooldownText;

        [Tooltip("蓝耗文本。")]
        [SerializeField] private Text costText;

        [Tooltip("按键提示文本（Q / W）。")]
        [SerializeField] private Text keyText;

        [Header("图标着色")]
        [Tooltip("技能就绪且蓝量足够时的图标颜色。")]
        [SerializeField] private Color readyIconColor = new Color(1f, 1f, 1f, 1f);

        [Tooltip("蓝量不足时的图标颜色。")]
        [SerializeField] private Color insufficientManaColor = new Color(0.45f, 0.5f, 0.65f, 1f);

        [Tooltip("英雄死亡时的图标颜色。")]
        [SerializeField] private Color deadIconColor = new Color(0.32f, 0.32f, 0.32f, 1f);

        /// <summary>绑定的技能组件（可能为 null：英雄没配技能）。</summary>
        private SkillComponent boundSkills;

        /// <summary>绑定时取到的技能配置，用于图标 / 蓝耗 / 总冷却。</summary>
        private SkillData boundData;

        /// <summary>上一次写入文本的整秒数。-1 表示"还没写过"，保证首次必定写入。</summary>
        private int lastDisplayedSecond = -1;

        /// <summary>上一次写入遮罩的填充比例，避免无意义地重建网格。</summary>
        private float lastOverlayAmount = -1f;

        /// <summary>遮罩当前是否显示。</summary>
        private bool isOverlayVisible;

        /// <summary>上一次的图标状态：0 = 就绪，1 = 蓝量不足，2 = 死亡。用于避免每帧重复写颜色。</summary>
        private int lastIconState = -1;

        /// <summary>静态信息（图标 / 蓝耗 / 按键）是否已写入。</summary>
        private bool hasAppliedStaticInfo;

        /// <summary>本视图对应的槽位（只读）。</summary>
        public SkillSlot Slot => slot;

        /// <summary>
        /// 绑定技能组件并写入静态信息（图标 / 蓝耗 / 按键）。
        /// 允许传 null（英雄没配技能组件）——此时本视图保持占位外观，Tick 直接返回。
        /// </summary>
        /// <param name="skills">英雄的技能组件。</param>
        public void Bind(SkillComponent skills)
        {
            boundSkills = skills;
            boundData = skills != null ? skills.GetSkillData(slot) : null;

            // 复位缓存：换绑之后所有"值没变就不写"的判断都必须重新评估一次。
            lastDisplayedSecond = -1;
            lastOverlayAmount = -1f;
            lastIconState = -1;
            hasAppliedStaticInfo = false;

            ApplyStaticInfo();
        }

        /// <summary>
        /// 每帧刷新冷却遮罩、秒数与图标着色。由 <see cref="HeroHUDView"/> 在它的 Update 里调用。
        /// </summary>
        /// <param name="ownerAlive">英雄是否存活（死亡时图标置灰）。</param>
        /// <param name="currentMana">英雄当前法力，用于"蓝量不足"着色。</param>
        public void Tick(bool ownerAlive, float currentMana)
        {
            if (boundSkills == null)
            {
                return;
            }

            float totalCooldown = boundData != null ? boundData.Cooldown : 0f;
            float remaining = boundSkills.GetCooldownRemaining(slot);
            float fillAmount = totalCooldown > 0f ? Mathf.Clamp01(remaining / totalCooldown) : 0f;

            // ---------- 冷却遮罩 ----------
            if (cooldownOverlay != null)
            {
                bool shouldShow = fillAmount > 0f;

                if (isOverlayVisible != shouldShow)
                {
                    isOverlayVisible = shouldShow;
                    cooldownOverlay.enabled = shouldShow;
                }

                if (shouldShow && !Mathf.Approximately(lastOverlayAmount, fillAmount))
                {
                    lastOverlayAmount = fillAmount;
                    cooldownOverlay.fillAmount = fillAmount;
                }
            }

            // ---------- 秒数文本：只在整秒跳变时写 ----------
            int seconds = remaining > 0f ? Mathf.CeilToInt(remaining) : 0;

            if (seconds != lastDisplayedSecond)
            {
                lastDisplayedSecond = seconds;

                if (cooldownText != null)
                {
                    cooldownText.text = seconds > 0 ? seconds.ToString() : string.Empty;
                }
            }

            // ---------- 图标着色 ----------
            if (iconImage != null)
            {
                bool insufficient = boundData != null &&
                                    boundData.ManaCost > 0f &&
                                    currentMana < boundData.ManaCost;

                int iconState = !ownerAlive ? 2 : (insufficient ? 1 : 0);

                if (iconState != lastIconState)
                {
                    lastIconState = iconState;

                    if (iconState == 2)
                    {
                        iconImage.color = deadIconColor;
                    }
                    else if (iconState == 1)
                    {
                        iconImage.color = insufficientManaColor;
                    }
                    else
                    {
                        iconImage.color = readyIconColor;
                    }
                }
            }
        }

        /// <summary>
        /// 写入一次性的静态信息：图标（SkillData.Icon，为空时保留占位白图）、蓝耗、按键提示。
        /// 这些值来自 ScriptableObject 配置，运行期不会变，因此只在 Bind 时写一次。
        /// </summary>
        private void ApplyStaticInfo()
        {
            if (hasAppliedStaticInfo)
            {
                return;
            }

            hasAppliedStaticInfo = true;

            // 字体兜底：uGUI 的 Text 在没有字体时不报错、只是什么都不画（静默失效），
            // 这里保证"工具没注入成功"时运行期也能自己补上。
            UIFontProvider.EnsureFont(cooldownText);
            UIFontProvider.EnsureFont(costText);
            UIFontProvider.EnsureFont(keyText);

            if (iconImage != null && boundData != null && boundData.Icon != null)
            {
                iconImage.sprite = boundData.Icon;
            }

            if (costText != null)
            {
                costText.text = boundData != null ? $"{boundData.ManaCost:0} 蓝" : string.Empty;
            }

            if (keyText != null)
            {
                keyText.text = DescribeSlot(slot);
            }
        }

        /// <summary>
        /// 槽位 → 按键提示文本。
        /// 刻意用 switch 而不是 slot.ToString()：枚举名一旦被重命名，界面上的按键提示会跟着变，
        /// 而玩家看到的按键与代码里的枚举名本来就是两件事（例如日后改名 SkillSlot.Slot1 时不该影响界面）。
        /// </summary>
        /// <param name="target">槽位。</param>
        /// <returns>按键提示文本。</returns>
        private static string DescribeSlot(SkillSlot target)
        {
            switch (target)
            {
                case SkillSlot.Q:
                    return "Q";
                case SkillSlot.W:
                    return "W";
                case SkillSlot.E:
                    return "E";
                case SkillSlot.R:
                    return "R";
                default:
                    return "?";
            }
        }
    }
}
