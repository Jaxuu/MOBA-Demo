using System;
using UnityEngine;
using MOBA.Core;

namespace MOBA.Components
{
    /// <summary>
    /// 法力组件：管理当前法力、消耗判定与随时间回复，是技能蓝耗的唯一数据源。
    ///
    /// 职责边界：
    /// 1. 只管"有多少蓝、够不够、扣多少"，不管"这个技能该不该放"（那是 SkillComponent 的校验链）；
    /// 2. 不回写 ScriptableObject：maxMana / regenPerSecond 由 EntityBase 在 Start 阶段从 EntityStatsData 注入；
    /// 3. 事件只广播"变化结果"，不广播"为什么变化"——UI 不需要知道是消耗还是回复。
    ///
    /// 【为什么扣蓝只提供 TrySpend 而没有独立的 Spend】扣蓝必须与"是否够"是同一个原子判断，
    /// 否则调用方写成 if (HasEnough(c)) { Spend(c); } 就会在两个方法之间留下"够 → 被别人扣掉 → 仍扣"
    /// 的竞态窗口（本项目虽然单线程，但同一个技能校验链里可能有多处消耗，防患于未然）。
    /// </summary>
    [DisallowMultipleComponent]
    public class ManaComponent : MonoBehaviour
    {
        [Header("默认值（未由 EntityBase 注入时使用）")]
        [Tooltip("兜底最大法力值：当外部没有调用 Initialize 注入配置时使用，保证组件可单独挂载测试。")]
        [Min(0f)]
        [SerializeField] private float defaultMaxMana = 300f;

        [Header("运行时状态（只读，仅供 Inspector 观察调试）")]
        [Tooltip("最大法力值，由 Initialize() 注入；不要手动改这里，改配置资产。")]
        [SerializeField] private float maxMana = 300f;

        [Tooltip("当前法力值。运行时状态，绝不写回 ScriptableObject。")]
        [SerializeField] private float currentMana = 300f;

        [Tooltip("每秒法力回复量，由 Initialize() 注入；0 表示不自动回复。")]
        [SerializeField] private float regenPerSecond = 0f;

        [Header("调试")]
        [Tooltip("在 Console 输出每次法力消耗与回复的数值变化。")]
        [SerializeField] private bool logManaChanges = false;

        /// <summary>
        /// 法力变化事件：参数依次为（当前法力, 最大法力）。
        /// 与 HealthComponent.OnHealthChanged 同构：把分子分母一起给 UI，避免 UI 自己去找配置。
        /// </summary>
        public event Action<float, float> OnManaChanged;

        /// <summary>
        /// 上一次广播时的法力值。
        /// 用途：法力是【连续回复】的，若每帧都广播，阶段七的法力条会以 60 次/秒的频率重绘，
        /// 而视觉上完全看不出区别。这里只在"变化超过阈值"或"刚好回满"时广播一次。
        /// </summary>
        private float lastBroadcastMana;

        /// <summary>法力变化的广播阈值（点）。取值兼顾 UI 精度与事件频率：0.5 点约等于一条血条上的 0.2%。</summary>
        private const float BroadcastThreshold = 0.5f;

        /// <summary>生命组件（可能为 null）。用于"死亡后停止回复"。</summary>
        private HealthComponent health;

        /// <summary>当前法力值（只读）。</summary>
        public float CurrentMana => currentMana;

        /// <summary>最大法力值（只读）。</summary>
        public float MaxMana => maxMana;

        /// <summary>法力百分比（0~1，只读），供法力条 UI 直接使用。</summary>
        public float ManaPercent => maxMana > 0f ? Mathf.Clamp01(currentMana / maxMana) : 0f;

        /// <summary>每秒回复量（只读）。</summary>
        public float RegenPerSecond => regenPerSecond;

        /// <summary>
        /// 缓存生命组件引用。
        /// 只做一次尝试性缓存，不做校验：动态创建单位时组件可能后挂，
        /// 缺失的后果仅仅是"死亡后仍会回复法力"，不值得为此报错打断初始化。
        /// </summary>
        private void Awake()
        {
            health = GetComponent<HealthComponent>();

            if (maxMana <= 0f)
            {
                maxMana = Mathf.Max(0f, defaultMaxMana);
            }

            // 预制体上的 currentMana 默认是 0，这里视为"尚未初始化"并补满，避免一进场景技能全灰。
            if (currentMana <= 0f)
            {
                currentMana = maxMana;
            }

            lastBroadcastMana = currentMana;
        }

        /// <summary>
        /// 初始化法力值。由 EntityBase 在 Start 阶段用 EntityStatsData 调用。
        /// 约定：调用后会把法力填满，因此只应在实体生成时调用一次。
        /// </summary>
        /// <param name="newMaxMana">配置中的最大法力值。</param>
        /// <param name="newRegenPerSecond">配置中的每秒回复量。</param>
        public void Initialize(float newMaxMana, float newRegenPerSecond)
        {
            maxMana = Mathf.Max(0f, newMaxMana);
            regenPerSecond = Mathf.Max(0f, newRegenPerSecond);
            currentMana = maxMana;
            lastBroadcastMana = currentMana;

            // 主动广播一次：让在 Initialize 之前就完成订阅的法力条 UI 立刻拿到初始值
            // （与 HealthComponent.Initialize 同一约定）。
            RaiseManaChanged();
        }

        /// <summary>
        /// 判断法力是否足够支付某个消耗。
        /// </summary>
        /// <param name="cost">消耗量；非正数视为免费，恒返回 true。</param>
        /// <returns>足够返回 true。</returns>
        public bool HasEnough(float cost)
        {
            return cost <= 0f || currentMana >= cost;
        }

        /// <summary>
        /// 尝试支付法力。这是本组件唯一的扣蓝入口：判定与扣减在同一个方法内完成，不留竞态窗口。
        /// </summary>
        /// <param name="cost">消耗量；非正数视为免费，直接成功。</param>
        /// <returns>支付成功返回 true；法力不足返回 false 且【不做任何修改】。</returns>
        public bool TrySpend(float cost)
        {
            if (cost <= 0f)
            {
                return true;
            }

            if (currentMana < cost)
            {
                return false;
            }

            currentMana -= cost;

            if (logManaChanges)
            {
                Debug.Log($"[ManaComponent] {name} 消耗 {cost:F1} 点法力，剩余 {currentMana:F1} / {maxMana:F1}", this);
            }

            RaiseManaChanged();
            return true;
        }

        /// <summary>
        /// 把法力补满（阶段七新增：英雄复活时调用）。
        ///
        /// 与 HealthComponent.Revive 对称：刻意【不】复用 Initialize —— 后者承担"首次生成"的语义
        /// （由 EntityBase.ApplyStats 调用，且会连回复速率一起重置），复活只是"补满当前值"这一件事。
        /// 主动广播一次，法力条不必自己判断该不该刷新。
        /// </summary>
        public void RestoreFull()
        {
            currentMana = maxMana;
            RaiseManaChanged();
        }

        /// <summary>
        /// 随时间回复法力。
        /// 三个提前返回条件：没配回复量、已死亡、已满蓝。
        /// 死亡后不回复是刻意的——README 的复活流程会在重生时重置状态，不需要"尸体慢慢回蓝"。
        /// </summary>
        private void Update()
        {
            if (regenPerSecond <= 0f)
            {
                return;
            }

            if (currentMana >= maxMana)
            {
                return;
            }

            if (health != null && health.IsDead)
            {
                return;
            }

            currentMana = Mathf.Min(maxMana, currentMana + regenPerSecond * Time.deltaTime);

            // 节流广播：只在变化超过阈值或刚好回满时通知 UI（理由见 lastBroadcastMana 的注释）。
            if (currentMana >= maxMana || Mathf.Abs(currentMana - lastBroadcastMana) >= BroadcastThreshold)
            {
                RaiseManaChanged();
            }
        }

        /// <summary>
        /// 统一的事件广播入口，与 HealthComponent.RaiseHealthChanged 同思路：
        /// 集中在一处才能保证"任何法力变化都会通知 UI"，不会出现某条分支忘了广播。
        /// </summary>
        private void RaiseManaChanged()
        {
            lastBroadcastMana = currentMana;
            OnManaChanged?.Invoke(currentMana, maxMana);
        }

        /// <summary>
        /// 编辑器内数值合法性纠正：防止在 Inspector 里手滑填出非法数值。
        /// </summary>
        private void OnValidate()
        {
            defaultMaxMana = Mathf.Max(0f, defaultMaxMana);
            maxMana = Mathf.Max(0f, maxMana);
            regenPerSecond = Mathf.Max(0f, regenPerSecond);
            currentMana = Mathf.Clamp(currentMana, 0f, maxMana);
        }
    }
}
