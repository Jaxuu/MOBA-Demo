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

        [Tooltip("当前护盾值（阶段六）。护盾优先吸收伤害，耗尽或由 BuffComponent 到期清除。")]
        [Min(0f)]
        [SerializeField] private float currentShield = 0f;

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
        /// 护盾变化事件（阶段六）：参数为当前护盾值。
        /// 与血量分开广播而不是塞进 OnHealthChanged：护盾条与血条是两个 UI 元素，
        /// 混在一起会让阶段七的血条订阅方每次都要判断"这次变化是不是护盾引起的"。
        /// </summary>
        public event Action<float> OnShieldChanged;

        /// <summary>
        /// 受伤事件（阶段七新增）：参数依次为（<b>本次实际从生命值中扣除的伤害</b>, 被护盾吸收的伤害, 伤害来源）。
        ///
        /// 【为什么必须新增这个事件】README §3.4 的事件契约里写着「OnDamaged → 受击特效 + 伤害飘字 + 血条刷新」，
        /// 但本组件此前只有 OnHealthChanged（当前血, 最大血）——订阅方只能拿到"结果值"，拿不到"这一下打了多少"。
        /// 想自己算差值也不行：伤害被护盾完全吸收时血量根本不变，差值恒为 0，飘字会彻底消失。
        ///
        /// 【为什么第一个参数是"实际扣减量"而不是"伤害量"】阶段七验收标准要求
        /// 「飘字数值与 TakeDamage 实际扣血量一致」，因此这里传的是 <c>扣血前的血量 - 扣血后的血量</c>
        /// （已夹到 0，不含溢出部分）。若将来要改成显示"溢出伤害"（打 100 只扣 10 时显示 100），
        /// 把传入值换成 remainingDamage 即可，消费方一行都不用改。
        ///
        /// 【为什么把发送者放在第一个参数】遵循 .NET 事件惯例（sender 在前）。
        /// 更实际的理由：伤害飘字管理器为每个单位订阅的是同一个回调，有了发送者就不必为每个单位各建一个闭包——
        /// 伤害是高频事件，按单位建闭包会在每次订阅时产生一次堆分配，直接顶掉"稳态 GC ≈ 0"的验收目标。
        /// </summary>
        public event Action<HealthComponent, float, float, EntityBase> OnDamaged;

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

        /// <summary>当前护盾值（只读）。</summary>
        public float CurrentShield => currentShield;

        /// <summary>
        /// 最近一次伤害的来源实体（只读），可能为 null（来源未知，例如测试代码或环境伤害）。
        /// 阶段七的击杀播报与计分板依赖它做伤害归属。
        /// </summary>
        public EntityBase LastDamageSource { get; private set; }

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

            // 护盾与伤害来源都属于"一次生命周期的状态"，必须一并复位：
            // 否则重生的单位会带着上一轮的护盾与击杀归属，阶段七的计分板会算错账。
            currentShield = 0f;
            LastDamageSource = null;

            isDead = false;

            // 主动广播一次：让在 Initialize 之前就已完成订阅的血条 UI 能立刻拿到初始值。
            RaiseHealthChanged();
            RaiseShieldChanged();
        }

        /// <summary>
        /// 受到伤害（IDamageable 契约成员，无来源版本）。
        /// 语义等价于"来源未知"，直接转发给带来源的重载，保证结算逻辑只有一份。
        /// </summary>
        /// <param name="amount">伤害数值，应为正数。</param>
        public void TakeDamage(float amount)
        {
            TakeDamage(amount, null);
        }

        /// <summary>
        /// 受到伤害（IDamageable 契约成员，带来源版本）。
        ///
        /// 结算顺序：记录来源 → 护盾吸收 → 数值扣减 → 广播血量变化 → 判定死亡并广播死亡事件。
        /// 这个顺序是刻意的：
        ///  · 护盾必须在扣血之前，这是"护盾优先吸收伤害"的唯一实现处；
        ///  · UI 会先收到"血量变成 0"，再收到"死亡"，不会出现血条还显示残血就已经死了的观感问题。
        ///
        /// 【为什么护盾放在本组件而不是 BuffComponent】护盾是伤害管线的一个环节，必须与扣血在同一处，
        /// 否则"先扣盾还是先扣血"会出现两套顺序；BuffComponent 只负责护盾的到期清除（调用 ClearShield）。
        /// </summary>
        /// <param name="amount">伤害数值，应为正数。</param>
        /// <param name="source">伤害来源实体，允许为 null（表示来源未知）。</param>
        public void TakeDamage(float amount, EntityBase source)
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

            // 记录来源放在最前面：即使这一下被护盾全部吸收、血量没变，也应当算作"受到过这次攻击"，
            // 否则阶段七会出现"护盾挡下的伤害不计入交战记录"的漏账。
            LastDamageSource = source;

            float remainingDamage = amount;

            // 本次被护盾吸收掉的伤害。刻意提到 if 外面声明：OnDamaged 要把"吸收了多少"一并广播出去
            // （护盾挡下的那一下同样是"打中了"，飘字与阶段八的受击特效都需要有反馈）。
            float shieldAbsorbed = 0f;

            // ---- 第一道：护盾优先吸收 ----
            if (currentShield > 0f)
            {
                shieldAbsorbed = Mathf.Min(currentShield, remainingDamage);
                currentShield -= shieldAbsorbed;
                remainingDamage -= shieldAbsorbed;

                if (logHealthChanges)
                {
                    Debug.Log(
                        $"[HealthComponent] {name} 护盾吸收 {shieldAbsorbed:F1} 点伤害：护盾 {currentShield:F1}，剩余伤害 {remainingDamage:F1}", this);
                }

                RaiseShieldChanged();
            }

            if (remainingDamage <= 0f)
            {
                // 伤害被护盾完全吸收：血量不变，因此【不广播血量变化、不判定死亡】。
                // 这不是"漏了一次事件"——血量确实没有变化，广播反而会让血条做无意义的重绘。
                //
                // 但 OnDamaged 必须广播（实际扣减量为 0）：否则"被护盾挡下的那一击"在表现层彻底消失，
                // 玩家会以为技能没打中。阶段七的飘字据此画一条灰蓝色的"吸收 N"。
                RaiseDamaged(0f, shieldAbsorbed, source);
                return;
            }

            // ---- 第二道：扣血 ----
            float healthBefore = currentHealth;

            // 用 Max 夹到 0：避免出现过大的单次伤害导致生命值为负数（后续如果做伤害统计会很难看）。
            currentHealth = Mathf.Max(0f, currentHealth - remainingDamage);

            // 实际扣减量 = 扣血前后之差（已夹到 0，不含溢出部分）。必须在扣血之后、广播之前算好。
            float healthDamage = healthBefore - currentHealth;

            if (logHealthChanges)
            {
                Debug.Log(
                    $"[HealthComponent] {name} 受到 {remainingDamage:F1} 点伤害：{healthBefore:F1} → {currentHealth:F1} / {maxHealth:F1}", this);
            }

            RaiseHealthChanged();

            // 顺序刻意是「血量变化 → 受伤 → 死亡」：与 README §3.4 的事件顺序一致，
            // 也保证表现层先播受击反馈、再播死亡表现，不会出现"血条还是满的就已经死了"。
            RaiseDamaged(healthDamage, shieldAbsorbed, source);

            if (currentHealth <= 0f)
            {
                // 先置死亡标记再广播：即使监听者在 OnDied 回调里又调用了 TakeDamage，也会被上面的闸门挡掉。
                isDead = true;

                // 死亡即清盾：留着护盾值没有意义，且会让阶段七的血条在尸体上画出一截护盾条。
                if (currentShield > 0f)
                {
                    currentShield = 0f;
                    RaiseShieldChanged();
                }

                OnDied?.Invoke();
            }
        }

        /// <summary>
        /// 设置护盾值（覆盖式）。护盾值会被夹到非负，且死亡状态下不生效。
        /// </summary>
        /// <param name="value">新的护盾值；传 0 等价于清除护盾。</param>
        public void SetShield(float value)
        {
            if (isDead)
            {
                return;
            }

            float clamped = Mathf.Max(0f, value);
            if (Mathf.Approximately(currentShield, clamped))
            {
                return;
            }

            currentShield = clamped;
            RaiseShieldChanged();
        }

        /// <summary>
        /// 累加护盾值。用于"多个来源叠加护盾"的场景（V1 只有单个技能护盾，走 SetShield 即可）。
        /// </summary>
        /// <param name="value">要增加的护盾值，非正数忽略。</param>
        public void AddShield(float value)
        {
            if (value <= 0f)
            {
                return;
            }

            SetShield(currentShield + value);
        }

        /// <summary>
        /// 清除护盾。由 BuffComponent 在护盾 buff 到期时调用——护盾的"时长"归 BuffComponent 管，
        /// 护盾的"数值"归本组件管，两边各管一件事，互不越界。
        /// </summary>
        public void ClearShield()
        {
            SetShield(0f);
        }

        /// <summary>
        /// 复活：把本组件复位到"刚初始化完"的状态（满血、无盾、无伤害来源、存活）。
        ///
        /// 【为什么必须新增方法而不是复用 Initialize】Initialize 的注释已经写明「只应在实体生成时调用一次」——
        /// 它同时承担"首次生成"的语义（由 EntityBase.ApplyStats 调用）。复活是"死亡之后的重置"，
        /// 与"生成"是两条不同的路径，复用会让"谁在什么时候初始化"变得不可推理。
        ///
        /// 【为什么把 LastDamageSource 也清掉】它是"本次生命的伤害归属"，跨生命周期残留会让
        /// 阶段七的击杀播报把上一轮的击杀者算到这一轮（与 Initialize 里清它是同一个理由）。
        ///
        /// 死亡是单向的（isDead 一旦置位就不再接受伤害与治疗），复活是它唯一的逆操作，
        /// 因此只应由 HeroController.Revive 这一条路径调用。
        /// </summary>
        public void Revive()
        {
            // 兜底：万一最大生命值被配成 0，复活后会立刻再次判定死亡（死循环式的"复活即阵亡"）。
            if (maxHealth <= 0f)
            {
                maxHealth = Mathf.Max(1f, defaultMaxHealth);
            }

            isDead = false;
            currentHealth = maxHealth;
            currentShield = 0f;
            LastDamageSource = null;

            // 主动广播：血条与护盾条不必自己判断"该不该刷新"，订阅方永远只做"收到就刷"。
            RaiseHealthChanged();
            RaiseShieldChanged();
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

        /// <summary>护盾变化的统一广播入口，与 RaiseHealthChanged 同思路：保证任何护盾变化都会被通知。</summary>
        private void RaiseShieldChanged()
        {
            OnShieldChanged?.Invoke(currentShield);
        }

        /// <summary>
        /// 受伤事件的统一广播入口，与 RaiseHealthChanged 同思路。
        /// 集中在一处是为了保证「任何一次真正结算过的伤害都会广播」——
        /// 包括"被护盾全吸收、血量没变"那一条分支（那里实际扣减量为 0，但依然是一次命中）。
        /// </summary>
        /// <param name="healthDamage">本次实际从生命值中扣除的伤害（0 表示被护盾全吸收或溢出）。</param>
        /// <param name="shieldAbsorbed">本次被护盾吸收的伤害。</param>
        /// <param name="source">伤害来源，允许为 null。</param>
        private void RaiseDamaged(float healthDamage, float shieldAbsorbed, EntityBase source)
        {
            OnDamaged?.Invoke(this, healthDamage, shieldAbsorbed, source);
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

            // 护盾同理：负护盾会让 TakeDamage 的 min 计算出现诡异结果。
            currentShield = Mathf.Max(0f, currentShield);
        }
    }
}
