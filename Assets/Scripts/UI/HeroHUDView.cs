using UnityEngine;
using UnityEngine.UI;
using MOBA.Components;
using MOBA.Core;
using MOBA.Skills;
using MOBA.Units;

namespace MOBA.UI
{
    /// <summary>
    /// 玩家 HUD（阶段七）：头像 + 生命条 + 法力条 + Q/W 技能槽。
    ///
    /// 职责边界：
    /// 1. 只读英雄的数据（生命 / 法力 / 技能冷却），【不】写任何战斗数值、【不】拦截输入；
    /// 2. 数值走事件订阅（OnHealthChanged / OnManaChanged / OnDied / OnHeroRespawned），
    ///    只有技能冷却每帧查询（理由见 SkillSlotView 的类注释）；
    /// 3. 组件解析放在 Start 且用 GetComponent 直取，不读 EntityBase 的组件属性缓存
    ///    （同一物体上组件的 Start 顺序不确定，属性里可能还是 null——项目既有的硬性约定）。
    ///
    /// 【"死亡时技能不可点"由谁保证】不是本视图。本视图只是把图标置灰，
    /// 真正拦住施法的是 SkillComponent.TryCast 校验链的第一环（自身状态：死亡）。
    /// 表现层不参与规则判定，这是铁律。
    /// </summary>
    [DisallowMultipleComponent]
    public class HeroHUDView : MonoBehaviour
    {
        [Header("数据来源（由一键组装工具注入）")]
        [Tooltip("被展示的玩家英雄。")]
        [SerializeField] private HeroController hero;

        [Header("子元素（由一键组装工具注入）")]
        [SerializeField] private Image portraitImage;
        [SerializeField] private Image healthFill;
        [SerializeField] private Image manaFill;
        [SerializeField] private Text healthText;
        [SerializeField] private Text manaText;
        [SerializeField] private SkillSlotView qSlot;
        [SerializeField] private SkillSlotView wSlot;

        [Header("头像配色")]
        [Tooltip("存活时的头像底色。")]
        [SerializeField] private Color alivePortraitColor = new Color(0.3f, 0.62f, 1f, 1f);

        [Tooltip("死亡时的头像底色（置灰）。")]
        [SerializeField] private Color deadPortraitColor = new Color(0.25f, 0.25f, 0.25f, 1f);

        /// <summary>生命组件。必需依赖，缺失即禁用本视图。</summary>
        private HealthComponent health;

        /// <summary>法力组件。允许缺失（没配法力的英雄）。</summary>
        private ManaComponent mana;

        /// <summary>技能组件。允许缺失（没配技能的英雄），缺失时技能槽保持占位外观。</summary>
        private SkillComponent skills;

        /// <summary>英雄当前是否已死亡（用于技能槽置灰）。</summary>
        private bool isDead;

        /// <summary>上一次写入的生命整数文本值，-1 表示尚未写过。</summary>
        private int lastDisplayedHealth = -1;

        /// <summary>上一次写入的法力整数文本值。</summary>
        private int lastDisplayedMana = -1;

        /// <summary>订阅事件、绑定技能槽，并主动读一次当前值。</summary>
        private void Start()
        {
            if (hero == null)
            {
                Debug.LogError(
                    $"[HeroHUDView] {name} 未指定英雄，HUD 不会显示任何数据。" +
                    "请执行 MOBA Demo/一键组装测试战场 重新生成 UI。", this);
                enabled = false;
                return;
            }

            EntityBase entity = hero.Entity;
            if (entity == null)
            {
                Debug.LogError($"[HeroHUDView] {name} 指定的英雄没有 EntityBase，HUD 不可用。", this);
                enabled = false;
                return;
            }

            // 用 GetComponent 直取而不是读 entity.Health / entity.Mana：
            // 那些属性是 EntityBase 在它自己的 Awake 里缓存的，而同一物体上组件的 Start 顺序不确定。
            health = entity.GetComponent<HealthComponent>();
            mana = entity.GetComponent<ManaComponent>();
            skills = entity.GetComponent<SkillComponent>();

            if (health == null)
            {
                Debug.LogError(
                    $"[HeroHUDView] {name} 对应的英雄没有 HealthComponent，生命条不会显示。" +
                    "请检查英雄预制体配置。", this);
                enabled = false;
                return;
            }

            health.OnHealthChanged += HandleHealthChanged;
            health.OnDied += HandleHeroDied;

            if (mana != null)
            {
                mana.OnManaChanged += HandleManaChanged;
            }

            hero.OnHeroRespawned += HandleHeroRespawned;

            // 字体兜底：uGUI 的 Text 在没有字体时不报错、只是什么都不画（静默失效）。
            UIFontProvider.EnsureFont(healthText);
            UIFontProvider.EnsureFont(manaText);

            if (qSlot != null)
            {
                qSlot.Bind(skills);
            }

            if (wSlot != null)
            {
                wSlot.Bind(skills);
            }

            // 订阅后主动读一次当前值：英雄可能在 HUD 初始化之前就已经初始化完（甚至已经受过伤），
            // 事件不会补发（与 HeroController「订阅时已死亡」的补偿路径同一思路）。
            HandleHealthChanged(health.CurrentHealth, health.MaxHealth);

            if (mana != null)
            {
                HandleManaChanged(mana.CurrentMana, mana.MaxMana);
            }

            // 订阅时可能已经死亡（场景里预置了已阵亡的英雄 / 被代码立刻击杀）：
            // 事件早已广播过且不会补发，必须主动同步一次，否则头像会以"活着"的样子显示一具尸体。
            isDead = health.IsDead;
            ApplyPortraitColor(isDead);
        }

        /// <summary>
        /// 每帧只驱动两个技能槽（冷却没有事件，只能查询）。
        /// 生命/法力文本都在事件回调里写，这里一行都不碰。
        /// </summary>
        private void Update()
        {
            if (health == null)
            {
                return;
            }

            // 没有法力组件时传 float.MaxValue：蓝耗判定恒为"够"，不会误染色。
            float currentMana = mana != null ? mana.CurrentMana : float.MaxValue;

            if (qSlot != null)
            {
                qSlot.Tick(!isDead, currentMana);
            }

            if (wSlot != null)
            {
                wSlot.Tick(!isDead, currentMana);
            }
        }

        /// <summary>退订全部事件。漏掉退订会让已销毁的 HUD 继续被英雄的事件持有。</summary>
        private void OnDisable()
        {
            if (health != null)
            {
                health.OnHealthChanged -= HandleHealthChanged;
                health.OnDied -= HandleHeroDied;
            }

            if (mana != null)
            {
                mana.OnManaChanged -= HandleManaChanged;
            }

            if (hero != null)
            {
                hero.OnHeroRespawned -= HandleHeroRespawned;
            }
        }

        /// <summary>生命变化：刷新血条填充与"当前/最大"文本。</summary>
        /// <param name="current">当前生命。</param>
        /// <param name="max">最大生命。</param>
        private void HandleHealthChanged(float current, float max)
        {
            if (healthFill != null)
            {
                healthFill.fillAmount = max > 0f ? Mathf.Clamp01(current / max) : 0f;
            }

            if (healthText == null)
            {
                return;
            }

            int displayed = Mathf.RoundToInt(current);

            // 只在整数部分变化时写文本：伤害事件本身不频繁，但复活/治疗可能连续触发多次，
            // 这里的判断让文本写入次数与"玩家能看到的数字变化"一一对应。
            if (displayed == lastDisplayedHealth)
            {
                return;
            }

            lastDisplayedHealth = displayed;
            healthText.text = $"{displayed}/{Mathf.RoundToInt(max)}";
        }

        /// <summary>法力变化：刷新蓝条填充与文本。ManaComponent 内部已按 0.5 点做节流广播。</summary>
        /// <param name="current">当前法力。</param>
        /// <param name="max">最大法力。</param>
        private void HandleManaChanged(float current, float max)
        {
            if (manaFill != null)
            {
                manaFill.fillAmount = max > 0f ? Mathf.Clamp01(current / max) : 0f;
            }

            if (manaText == null)
            {
                return;
            }

            int displayed = Mathf.RoundToInt(current);

            if (displayed == lastDisplayedMana)
            {
                return;
            }

            lastDisplayedMana = displayed;
            manaText.text = $"{displayed}/{Mathf.RoundToInt(max)}";
        }

        /// <summary>英雄死亡：头像置灰，技能槽在下一帧 Update 里跟着置灰。</summary>
        private void HandleHeroDied()
        {
            isDead = true;
            ApplyPortraitColor(true);
        }

        /// <summary>
        /// 英雄复活：头像恢复彩色。
        /// 生命/法力条不需要在这里手动刷新——HealthComponent.Revive 与 ManaComponent.RestoreFull
        /// 都会主动广播一次，本视图的事件回调已经把它们刷好了。
        /// </summary>
        /// <param name="respawnedHero">复活的英雄（本视图只关心"它活了"这一件事）。</param>
        private void HandleHeroRespawned(HeroController respawnedHero)
        {
            isDead = false;
            ApplyPortraitColor(false);
        }

        /// <summary>应用头像底色（存活彩色 / 死亡置灰）。</summary>
        /// <param name="dead">是否已死亡。</param>
        private void ApplyPortraitColor(bool dead)
        {
            if (portraitImage != null)
            {
                portraitImage.color = dead ? deadPortraitColor : alivePortraitColor;
            }
        }
    }
}
