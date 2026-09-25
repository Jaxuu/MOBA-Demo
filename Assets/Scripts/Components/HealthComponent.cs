using System;
using UnityEngine;
using MOBA.Core;

namespace MOBA.Components
{
    /// <summary>
    /// 生命组件：管理当前生命值、伤害结算与死亡事件，是 <see cref="IDamageable"/> 的唯一实现者。
    ///
    /// 职责边界（对应计划书 3.2）：
    /// 1. 只负责"数值与状态"——扣血、回血、死亡判定、事件广播；
    /// 2. 不负责表现（死亡动画、销毁物体），也不负责战斗节奏（攻击冷却）；
    ///    死亡后做什么由监听 OnDied 的控制器决定（例如 DeadState、MatchController 判定胜负）。
    /// 3. 配置数据由 EntityBase 在 Start 阶段通过 Initialize() 注入，本组件不直接引用 ScriptableObject。
    ///
    /// 状态机（只有两个状态）：
    ///   存活 --(currentHealth 归零)--> 死亡（不可逆）
    /// 死亡是单向的：一旦进入死亡状态，后续 TakeDamage / Heal 一律忽略，保证死亡事件只触发一次。
    /// </summary>
    [DisallowMultipleComponent]
    public class HealthComponent : MonoBehaviour, IDamageable
    {
        [Header("默认值（未由 EntityBase 注入时使用）")]
        [Tooltip("兜底最大生命值：当外部没有调用 Initialize 注入配置时使用，保证组件可单独挂载测试。")]
        [Min(1f)]
        [SerializeField] private float defaultMaxHealth = 100f;

        [Header("运行时状态（只读，仅供 Inspector 观察调试）")]
        [Tooltip("最大生命值，由 Initialize() 注入；不要手动改这里，改配置资产。")]
        [SerializeField] private float maxHealth = 100f;

        [Tooltip("当前生命值。运行时状态，绝不写回 ScriptableObject。")]
        [SerializeField] private float currentHealth = 100f;

        [Header("调试")]
        [Tooltip("在 Console 输出每次受伤/治疗的数值变化，用于验证伤害结算。")]
        [SerializeField] private bool logHealthChanges = false;

        /// <summary>
        /// 血量变化事件：参数依次为（当前血量, 最大血量）。
        /// 为什么把最大血量一起传：血条 UI 需要同时知道分子和分母，
        /// 若只传当前值，UI 还得自己去找配置，反而增加耦合。
        /// </summary>
        public event Action<float, float> OnHealthChanged;

        /// <summary>
        /// 死亡事件：生命值归零时广播，且保证只广播一次。
        /// 监听方（控制器、MatchController、UI）在此做死亡处理，不要在本组件里写表现逻辑。
        /// </summary>
        public event Action OnDied;

        /// <summary>死亡标记。用独立字段而不是用 currentHealth &lt;= 0 判断，是为了让"死亡只触发一次"这件事显式且可靠。</summary>
        private bool isDead;

        /// <summary>当前生命值（只读）。</summary>
        public float CurrentHealth => currentHealth;

        /// <summary>最大生命值（只读）。</summary>
        public float MaxHealth => maxHealth;

        /// <summary>生命百分比（0~1），供血条 UI 直接使用，避免每处都重复写除法与除零保护。</summary>
        public float HealthPercent => maxHealth > 0f ? Mathf.Clamp01(currentHealth / maxHealth) : 0f;

        /// <summary>是否已死亡（IDamageable 契约成员）。</summary>
        public bool IsDead => isDead;

        /// <summary>
        /// 兜底初始化：若外部未调用 Initialize（例如单独把本组件拖到测试物体上），
        /// 用 Inspector 里的默认值把生命填满，避免出现 0/0 生命导致立刻判定死亡。
        /// </summary>
        private void Awake()
        {
            if (maxHealth <= 0f)
            {
                maxHealth = Mathf.Max(1f, defaultMaxHealth);
            }

            // 预制体上的 currentHealth 默认是 0，这里视为"尚未初始化"并补满。
            if (currentHealth <= 0f)
            {
                currentHealth = maxHealth;
            }
        }

        /// <summary>
        /// 初始化生命值。由 EntityBase 在 Start 阶段用 EntityStatsData.MaxHealth 调用。
        /// 约定：调用后会把生命填满、清除死亡标记，因此只应在实体生成时调用一次；
        /// 若需要"复活"，应新增独立方法而不是复用本方法，避免误用导致死亡状态被意外重置。
        /// </summary>
        /// <param name="newMaxHealth">配置中的最大生命值。</param>
        public void Initialize(float newMaxHealth)
        {
            if (newMaxHealth <= 0f)
            {
                Debug.LogWarning(
                    $"[HealthComponent] {name} 收到的最大生命值 {newMaxHealth} 不合法，改用默认值 {defaultMaxHealth}。" +
                    "请检查 EntityStatsData 配置。", this);
                newMaxHealth = Mathf.Max(1f, defaultMaxHealth);
            }

            maxHealth = newMaxHealth;
            currentHealth = maxHealth;
            isDead = false;

            // 主动广播一次：让在 Initialize 之前就已完成订阅的血条 UI 能立刻拿到初始值。
            RaiseHealthChanged();
        }

        /// <summary>
        /// 受到伤害（IDamageable 契约成员）。
        /// 结算顺序：数值扣减 → 广播血量变化 → 判定死亡并广播死亡事件。
        /// 这个顺序是刻意的：UI 会先收到"血量变成 0"，再收到"死亡"，不会出现血条还显示残血就已经死了的观感问题。
        /// </summary>
        /// <param name="amount">伤害数值，应为正数。</param>
        public void TakeDamage(float amount)
        {
            if (amount <= 0f)
            {
                // 0 或负数伤害基本都来自配置错误，直接告警并忽略，避免出现"负伤害回血"的诡异行为。
                Debug.LogWarning($"[HealthComponent] {name} 收到非正数伤害 {amount}，已忽略。", this);
                return;
            }

            // 死亡后不再结算：这是"死亡只触发一次"以及"死亡后不再被鞭尸"的第一道闸门。
            if (isDead)
            {
                return;
            }

            float healthBefore = currentHealth;

            // 用 Max 夹到 0：避免出现过大的单次伤害导致生命值为负数（后续如果做伤害统计会很难看）。
            currentHealth = Mathf.Max(0f, currentHealth - amount);

            if (logHealthChanges)
            {
                Debug.Log($"[HealthComponent] {name} 受到 {amount:F1} 点伤害：{healthBefore:F1} → {currentHealth:F1} / {maxHealth:F1}", this);
            }

            RaiseHealthChanged();

            if (currentHealth <= 0f)
            {
                // 先置死亡标记再广播：即使监听者在 OnDied 回调里又调用了 TakeDamage，也会被上面的闸门挡掉。
                isDead = true;
                OnDied?.Invoke();
            }
        }

        /// <summary>
        /// 治疗。计划书 3.2 将"治疗"列入本组件职责，故一并实现（数值逻辑与伤害对称）。
        /// </summary>
        /// <param name="amount">治疗量，应为正数。</param>
        public void Heal(float amount)
        {
            if (amount <= 0f)
            {
                Debug.LogWarning($"[HealthComponent] {name} 收到非正数治疗量 {amount}，已忽略。", this);
                return;
            }

            // 死亡不可逆：死亡状态下不允许治疗，避免出现"尸体回血复活"的状态错乱。
            if (isDead)
            {
                return;
            }

            // 已经满血时直接返回，省掉一次无意义的事件广播（血条 UI 不必重绘）。
            if (currentHealth >= maxHealth)
            {
                return;
            }

            float healthBefore = currentHealth;
            currentHealth = Mathf.Min(maxHealth, currentHealth + amount);

            if (logHealthChanges)
            {
                Debug.Log($"[HealthComponent] {name} 恢复 {amount:F1} 点生命：{healthBefore:F1} → {currentHealth:F1} / {maxHealth:F1}", this);
            }

            RaiseHealthChanged();
        }

        /// <summary>
        /// 统一的事件广播入口。
        /// 集中在一处是为了保证"任何血量变化都会广播"，不会出现某条分支忘了通知 UI 的情况。
        /// </summary>
        private void RaiseHealthChanged()
        {
            OnHealthChanged?.Invoke(currentHealth, maxHealth);
        }

        /// <summary>
        /// 编辑器内校验：防止在 Inspector 里手滑填出非法数值（负数、超过上限）。
        /// 只做数值合法性纠正，不改变运行时状态语义。
        /// </summary>
        private void OnValidate()
        {
            defaultMaxHealth = Mathf.Max(1f, defaultMaxHealth);
            maxHealth = Mathf.Max(1f, maxHealth);

            // 编辑期把当前生命夹在 [0, 最大生命] 内，避免序列化出越界值。
            currentHealth = Mathf.Clamp(currentHealth, 0f, maxHealth);
        }
    }
}
