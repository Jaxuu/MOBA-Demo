using System;
using UnityEngine;
using MOBA.Core;
using MOBA.Skills;

namespace MOBA.Components
{
    /// <summary>
    /// 状态效果容器（Buff/Debuff）：管理减速、眩晕、护盾的【时长与刷新策略】。
    ///
    /// 职责边界：
    /// 1. 只管"状态挂了多久、什么时候到期、到期要恢复什么"，不管"效果数值怎么算"；
    /// 2. 移速的写入只经 <see cref="MovementComponent.SetMoveSpeed"/>，禁止直接改 NavMeshAgent.speed；
    /// 3. 眩晕只通过 MovementComponent / CombatComponent 的"锁"实现，不自己拦截移动与攻击的调用；
    /// 4. 护盾的【数值】在 HealthComponent（它才在伤害管线里），本组件只持有护盾的【到期时间】；
    /// 5. 不订阅表现层，只广播事件。
    ///
    /// 【刷新策略（V1 定稿）】同类型效果"只刷新时长 + 取更强数值"，**绝不叠加**。
    /// 依据 README 阶段六验收：「重复施加按既定策略（刷新时长）执行，不产生叠加失控」。
    /// 为什么不叠加：一旦允许叠加，移速就要维护"多个减速修饰器的聚合"，
    /// 那就变成了 README 明确排除的"完整属性修饰器聚合体系"。
    ///
    /// 【为什么减速要快照 baseSpeed】减速的语义是"从原速按比例降下来"，所以必须知道原速是多少。
    /// 快照只在"当前没有减速"时做一次——若每个来源各自快照，A 记下 6、B 记下 3.6，
    /// 到期恢复顺序不同会得到 6 或 3.6，是典型的静默错误。
    /// </summary>
    [DisallowMultipleComponent]
    public class BuffComponent : MonoBehaviour
    {
        [Header("调试")]
        [Tooltip("在 Console 输出状态效果的施加与到期记录。")]
        [SerializeField] private bool logBuffEvents = false;

        [Header("运行时状态（只读，仅供 Inspector 观察调试）")]
        [Tooltip("当前是否处于减速状态。")]
        [SerializeField] private bool slowed = false;

        [Tooltip("当前减速比例 0~1。")]
        [SerializeField] private float slowPercent = 0f;

        [Tooltip("减速到期时间（Time.time 基准）。")]
        [SerializeField] private float slowEndTime = 0f;

        [Tooltip("当前是否处于眩晕状态。")]
        [SerializeField] private bool stunned = false;

        [Tooltip("眩晕到期时间（Time.time 基准）。")]
        [SerializeField] private float stunEndTime = 0f;

        [Tooltip("当前是否挂着一个会到期的护盾。")]
        [SerializeField] private bool shieldActive = false;

        [Tooltip("护盾到期时间（Time.time 基准）。")]
        [SerializeField] private float shieldEndTime = 0f;

        [Tooltip("当前是否处于加速状态（阶段八新增）。")]
        [SerializeField] private bool hasted = false;

        [Tooltip("当前加速比例（0.3 = 移速 130%）。")]
        [SerializeField] private float hastePercent = 0f;

        [Tooltip("加速到期时间（Time.time 基准）。")]
        [SerializeField] private float hasteEndTime = 0f;

        [Tooltip("当前是否处于沉默状态（阶段八新增）：禁止施法，不影响移动与普攻。")]
        [SerializeField] private bool silenced = false;

        [Tooltip("沉默到期时间（Time.time 基准）。")]
        [SerializeField] private float silenceEndTime = 0f;

        [Tooltip("当前是否挂着「强化下一次普攻」（阶段八新增）。")]
        [SerializeField] private bool empowerActive = false;

        [Tooltip("强化普攻的额外伤害。")]
        [SerializeField] private float empowerBonusDamage = 0f;

        [Tooltip("强化普攻命中后对目标施加的沉默时长（秒）；0 = 不附带沉默。")]
        [SerializeField] private float empowerSilenceDuration = 0f;

        [Tooltip("强化普攻的兜底超时时刻（Time.time 基准）。")]
        [SerializeField] private float empowerEndTime = 0f;

        [Tooltip("移速快照是否有效。减速与加速共用同一份原速快照，因此用一个标记表示「当前有没有移速修饰」。")]
        [SerializeField] private bool hasSpeedSnapshot = false;

        [Tooltip("减速 / 加速前的移速快照，用于到期精确恢复。")]
        [SerializeField] private float baseSpeed = 0f;

        /// <summary>实体身份入口（延迟解析），用于取阵营等身份信息。</summary>
        private EntityBase owner;

        /// <summary>移动组件（可能为 null：建筑没有移动能力）。</summary>
        private MovementComponent movement;

        /// <summary>战斗组件（可能为 null：基地没有攻击能力）。</summary>
        private CombatComponent combat;

        /// <summary>生命组件（可能为 null）。</summary>
        private HealthComponent health;

        /// <summary>技能组件（可能为 null）。用于在解除眩晕前确认"施法前摇是否还持有同一把锁"。</summary>
        private SkillComponent skills;

        /// <summary>是否已订阅死亡事件，防止重复订阅。</summary>
        private bool hasSubscribedDeath;

        /// <summary>状态效果被施加时广播：参数为（效果类型, 持续时长）。供阶段七的表现层订阅。</summary>
        public event Action<BuffType, float> OnBuffApplied;

        /// <summary>状态效果到期或被清除时广播：参数为效果类型。</summary>
        public event Action<BuffType> OnBuffExpired;

        /// <summary>当前是否处于减速状态（只读），供调试与测试断言使用。</summary>
        public bool IsSlowed => slowed;

        /// <summary>当前减速比例（只读）；未减速时返回 0。</summary>
        public float CurrentSlowPercent => slowed ? slowPercent : 0f;

        /// <summary>当前是否处于眩晕状态（只读）。</summary>
        public bool IsStunned => stunned;

        /// <summary>当前是否挂着会到期的护盾（只读）。</summary>
        public bool IsShieldActive => shieldActive;

        /// <summary>当前是否处于加速状态（只读）。</summary>
        public bool IsHasted => hasted;

        /// <summary>当前加速比例（只读）；未加速时返回 0。</summary>
        public float CurrentHastePercent => hasted ? hastePercent : 0f;

        /// <summary>当前是否处于沉默状态（只读）。SkillComponent 的施法校验链读它。</summary>
        public bool IsSilenced => silenced;

        /// <summary>当前是否挂着「强化下一次普攻」（只读）。</summary>
        public bool IsEmpowered => empowerActive;

        /// <summary>减速 / 加速前的移速快照（只读），供测试断言"到期是否精确恢复"。</summary>
        public float BaseSpeed => baseSpeed;

        #region 组件解析（延迟解析，兼容动态创建）

        private EntityBase Owner
        {
            get
            {
                if (owner == null)
                {
                    owner = GetComponent<EntityBase>();
                }

                return owner;
            }
        }

        /// <summary>
        /// 移动组件（延迟解析）。
        /// 为什么不用 Awake 缓存后就固定：用代码动态创建单位时（MinionSpawner / 自动化测试）
        /// 组件挂载顺序不可控，本组件的 Awake 可能早于 MovementComponent 的 AddComponent，
        /// 那样缓存到的就是 null 且永远不会更新。首次访问补一次查找后即命中缓存，运行期无额外开销。
        /// </summary>
        private MovementComponent Movement
        {
            get
            {
                if (movement == null)
                {
                    movement = GetComponent<MovementComponent>();
                }

                return movement;
            }
        }

        private CombatComponent Combat
        {
            get
            {
                if (combat == null)
                {
                    combat = GetComponent<CombatComponent>();
                }

                return combat;
            }
        }

        private HealthComponent Health
        {
            get
            {
                if (health == null)
                {
                    health = GetComponent<HealthComponent>();
                }

                return health;
            }
        }

        private SkillComponent Skills
        {
            get
            {
                if (skills == null)
                {
                    skills = GetComponent<SkillComponent>();
                }

                return skills;
            }
        }

        #endregion

        private void Awake()
        {
            owner = GetComponent<EntityBase>();
            movement = GetComponent<MovementComponent>();
            combat = GetComponent<CombatComponent>();
            health = GetComponent<HealthComponent>();
            skills = GetComponent<SkillComponent>();
        }

        private void Start()
        {
            EnsureDeathSubscription();
        }

        /// <summary>
        /// 订阅死亡事件。
        /// 死亡必须清掉一切状态效果：否则"被减速致死的单位"会把减速状态带进下一轮生命周期，
        /// 而且眩晕持有的移动/攻击锁会一直挂着（尸体虽然不动，但复活后会立刻体现为无法移动）。
        /// </summary>
        private void EnsureDeathSubscription()
        {
            if (hasSubscribedDeath)
            {
                return;
            }

            HealthComponent resolved = Health;
            if (resolved == null)
            {
                return;
            }

            resolved.OnDied += HandleOwnerDied;
            hasSubscribedDeath = true;

            // 订阅时可能已经死亡（代码动态创建后立刻被击杀）：事件订阅不会补发，必须主动补一次清理。
            if (resolved.IsDead)
            {
                HandleOwnerDied();
            }
        }

        private void OnDisable()
        {
            // 物体被禁用时同样要归还状态：否则"禁用 → 重新启用"会让移速停在减速值上。
            ClearAllBuffs();
        }

        private void OnDestroy()
        {
            if (hasSubscribedDeath && health != null)
            {
                health.OnDied -= HandleOwnerDied;
            }
        }

        private void HandleOwnerDied()
        {
            ClearAllBuffs();
        }

        #region 施加效果

        /// <summary>
        /// 施加减速。
        /// </summary>
        /// <param name="percent">减速比例 0~1（0.4 = 移速降到 60%）。非正数忽略。</param>
        /// <param name="duration">持续时长（秒）。非正数忽略。</param>
        public void ApplySlow(float percent, float duration)
        {
            if (percent <= 0f || duration <= 0f)
            {
                return;
            }

            float clamped = Mathf.Clamp01(percent);

            // 首次施加前先取原速快照（唯一快照点，见类注释）。
            // 快照必须在下述分支【之前】取：已在减速时 Movement.Speed 已经是减速后的值，
            // 那时再快照就会把减速值当成原速，到期"恢复"到减速值上（静默的永久减速）。
            EnsureSpeedSnapshot();

            if (slowed)
            {
                // 已在减速中：刷新时长 + 取更强的一次，不叠加、也不让已有的强减速被弱减速顶掉。
                slowPercent = Mathf.Max(slowPercent, clamped);
                slowEndTime = Mathf.Max(slowEndTime, Time.time + duration);
            }
            else
            {
                slowPercent = clamped;
                slowEndTime = Time.time + duration;
                slowed = true;
            }

            ApplySpeedModifier();

            if (logBuffEvents)
            {
                Debug.Log(
                    $"[BuffComponent] {name} 被减速 {slowPercent * 100f:F0}%（原速 {baseSpeed:F2} → " +
                    $"{(Movement != null ? Movement.Speed : 0f):F2}），剩余 {slowEndTime - Time.time:F2}s", this);
            }

            OnBuffApplied?.Invoke(BuffType.Slow, duration);
        }

        /// <summary>
        /// 施加加速（阶段八新增）。
        ///
        /// 【刷新策略与减速完全一致】只刷新时长 + 取更强的一次，绝不叠加。
        /// 与减速共用同一份原速快照，两者的系数在 ApplySpeedModifier 里【相乘】：
        /// 同时挂着 40% 减速与 30% 加速时，移速 = 原速 × 0.6 × 1.3。
        /// 相乘而不是相加，是为了让"减速后加速"与"加速后减速"得到同一个结果（可交换），
        /// 相加则会出现顺序不同、结果不同（且都等于原速）的诡异表现。
        /// </summary>
        /// <param name="percent">加速比例（0.3 = 移速 130%）。非正数忽略。</param>
        /// <param name="duration">持续时长（秒）。非正数忽略。</param>
        public void ApplyHaste(float percent, float duration)
        {
            if (percent <= 0f || duration <= 0f)
            {
                return;
            }

            EnsureSpeedSnapshot();

            float newEndTime = Time.time + duration;

            if (hasted)
            {
                hastePercent = Mathf.Max(hastePercent, percent);
                hasteEndTime = Mathf.Max(hasteEndTime, newEndTime);
            }
            else
            {
                hastePercent = percent;
                hasteEndTime = newEndTime;
                hasted = true;
            }

            ApplySpeedModifier();

            if (logBuffEvents)
            {
                Debug.Log(
                    $"[BuffComponent] {name} 被加速 {hastePercent * 100f:F0}%（原速 {baseSpeed:F2} → " +
                    $"{(Movement != null ? Movement.Speed : 0f):F2}），剩余 {hasteEndTime - Time.time:F2}s", this);
            }

            OnBuffApplied?.Invoke(BuffType.Haste, duration);
        }

        /// <summary>
        /// 施加沉默（阶段八新增）：禁止施法，不影响移动与普攻。
        ///
        /// 【为什么不上控制锁】见 BuffType.Silence 的说明：沉默只封"施法"这一条通道，
        /// 拦截点在 SkillComponent.TryCast 的自身状态校验链上（那里本来就是唯一的判定点）。
        /// 这样它就不需要与眩晕、施法前摇抢同一把锁，三种来源互相解锁的破绽也就不会出现。
        /// </summary>
        /// <param name="duration">持续时长（秒）。非正数忽略。</param>
        public void ApplySilence(float duration)
        {
            if (duration <= 0f)
            {
                return;
            }

            // 刷新策略：取更晚的结束时间（不叠加、也不缩短已有的长沉默）。
            float newEndTime = Time.time + duration;
            silenceEndTime = silenced ? Mathf.Max(silenceEndTime, newEndTime) : newEndTime;
            silenced = true;

            if (logBuffEvents)
            {
                Debug.Log($"[BuffComponent] {name} 被沉默，剩余 {silenceEndTime - Time.time:F2}s", this);
            }

            OnBuffApplied?.Invoke(BuffType.Silence, duration);
        }

        /// <summary>
        /// 挂上「强化下一次普攻」（阶段八新增，Q 致命打击的主效果）。
        ///
        /// 【为什么放在 BuffComponent 而不是 CombatComponent】它是一层"挂在单位身上的状态"，
        /// 与护盾/加速同类：有数值、有时长、有到期兜底、死亡要清。
        /// CombatComponent 只负责在结算伤害时把它取走（TryConsumeEmpowerNextAttack），
        /// 自己不持有任何状态——这保证"状态只有一个存放处"，不会出现两处各记一份而互相不一致。
        /// </summary>
        /// <param name="bonusDamage">额外伤害，非正数忽略。</param>
        /// <param name="silenceDuration">命中后对目标施加的沉默时长（秒）；0 = 不附带沉默。</param>
        /// <param name="duration">兜底超时（秒）；非正数忽略（必须给一个正数，否则等于永久挂着）。</param>
        public void ApplyEmpowerNextAttack(float bonusDamage, float silenceDuration, float duration)
        {
            if (bonusDamage <= 0f || duration <= 0f)
            {
                return;
            }

            // 刷新策略：取更强的额外伤害（不叠加）+ 刷新时长。
            // 重复释放只刷新不叠加失控 —— 这是 README 对状态效果的一贯要求。
            empowerBonusDamage = empowerActive ? Mathf.Max(empowerBonusDamage, bonusDamage) : bonusDamage;
            empowerSilenceDuration = empowerActive
                ? Mathf.Max(empowerSilenceDuration, silenceDuration)
                : silenceDuration;
            empowerEndTime = empowerActive ? Mathf.Max(empowerEndTime, Time.time + duration) : Time.time + duration;
            empowerActive = true;

            if (logBuffEvents)
            {
                Debug.Log(
                    $"[BuffComponent] {name} 获得强化普攻（+{empowerBonusDamage:F1} 伤害" +
                    $"{(empowerSilenceDuration > 0f ? $"，命中沉默 {empowerSilenceDuration:F1}s" : string.Empty)}），" +
                    $"剩余 {empowerEndTime - Time.time:F2}s", this);
            }

            OnBuffApplied?.Invoke(BuffType.EmpowerNextAttack, duration);
        }

        /// <summary>
        /// 取走「强化下一次普攻」。由 CombatComponent 在【一次普攻成功提交之后】调用，
        /// 返回 true 时调用方应把额外伤害叠加进这一下，并按 silenceDuration 沉默目标。
        ///
        /// 【为什么是"取走"而不是"读取"】语义就是"命中即消耗"：一次强化只能作用于一次普攻。
        /// 拆成 TryGet + Consume 两步会让调用方有机会忘记第二步（表现为"一次强化打了一整场"），
        /// 因此把判断与清除合成一个原子操作，用错的可能性在结构上被消除。
        /// </summary>
        /// <param name="bonusDamage">输出：额外伤害。</param>
        /// <param name="silenceDuration">输出：命中后应施加的沉默时长（0 = 不沉默）。</param>
        /// <returns>本次确实取走了一层强化返回 true。</returns>
        public bool TryConsumeEmpowerNextAttack(out float bonusDamage, out float silenceDuration)
        {
            bonusDamage = 0f;
            silenceDuration = 0f;

            if (!empowerActive)
            {
                return false;
            }

            bonusDamage = empowerBonusDamage;
            silenceDuration = empowerSilenceDuration;

            ClearEmpower();

            return true;
        }

        /// <summary>
        /// 施加眩晕：禁止移动与攻击，到期自动解除。
        /// </summary>
        /// <param name="duration">持续时长（秒）。非正数忽略。</param>
        public void ApplyStun(float duration)
        {
            if (duration <= 0f)
            {
                return;
            }

            // 刷新策略：取更晚的结束时间——既不叠加时长，也不让新来的短眩晕缩短已有的长眩晕。
            float newEndTime = Time.time + duration;
            stunEndTime = stunned ? Mathf.Max(stunEndTime, newEndTime) : newEndTime;
            stunned = true;

            LockControls();

            if (logBuffEvents)
            {
                Debug.Log($"[BuffComponent] {name} 被眩晕，剩余 {stunEndTime - Time.time:F2}s", this);
            }

            OnBuffApplied?.Invoke(BuffType.Stun, duration);
        }

        /// <summary>
        /// 施加会到期的护盾。
        /// 数值写入 HealthComponent（它才在伤害管线里），本组件只记时长，到期调用 ClearShield。
        /// </summary>
        /// <param name="value">护盾值，非正数忽略。</param>
        /// <param name="duration">持续时长（秒），非正数忽略（要永久护盾请直接调 HealthComponent.AddShield）。</param>
        public void ApplyShield(float value, float duration)
        {
            if (value <= 0f || duration <= 0f)
            {
                return;
            }

            HealthComponent resolved = Health;
            if (resolved == null)
            {
                Debug.LogWarning(
                    $"[BuffComponent] {name} 上找不到 HealthComponent，护盾无法施加。", this);
                return;
            }

            // 刷新策略：取更强的护盾值（不叠加），并刷新到期时间。
            bool wasActive = shieldActive;
            float target = wasActive ? Mathf.Max(resolved.CurrentShield, value) : value;
            resolved.SetShield(target);

            shieldActive = true;
            shieldEndTime = wasActive ? Mathf.Max(shieldEndTime, Time.time + duration) : Time.time + duration;

            if (logBuffEvents)
            {
                Debug.Log($"[BuffComponent] {name} 获得护盾 {target:F1}，剩余 {shieldEndTime - Time.time:F2}s", this);
            }

            OnBuffApplied?.Invoke(BuffType.Shield, duration);
        }

        #endregion

        #region 到期处理

        /// <summary>
        /// 统一的状态到期检查。
        /// 用"绝对到期时间 + Time.time 比较"而不是"每帧递减剩余秒数"：
        /// 后者在组件被禁用时会漏减，前者天然不受帧率与启停影响（与 CombatComponent.nextAttackTime 同一模式）。
        /// </summary>
        private void Update()
        {
            float now = Time.time;

            if (slowed && now >= slowEndTime)
            {
                ExpireSlow();
            }

            if (stunned && now >= stunEndTime)
            {
                ExpireStun();
            }

            if (shieldActive && now >= shieldEndTime)
            {
                ExpireShield();
            }

            if (hasted && now >= hasteEndTime)
            {
                ExpireHaste();
            }

            if (silenced && now >= silenceEndTime)
            {
                ExpireSilence();
            }

            if (empowerActive && now >= empowerEndTime)
            {
                // 超时作废：强化普攻攒着不放没有意义，且会让"下一次普攻"变成一个无法预期的行为。
                ClearEmpower();

                if (logBuffEvents)
                {
                    Debug.Log($"[BuffComponent] {name} 的强化普攻已超时作废", this);
                }

                OnBuffExpired?.Invoke(BuffType.EmpowerNextAttack);
            }
        }

        private void ExpireSlow()
        {
            slowed = false;
            slowPercent = 0f;

            // 精确恢复快照值（不是"按比例加回去"，避免浮点误差累积）。
            ApplySpeedModifier();
            ReleaseSpeedSnapshotIfIdle();

            if (logBuffEvents)
            {
                Debug.Log($"[BuffComponent] {name} 减速已解除，移速恢复至 {baseSpeed:F2}", this);
            }

            OnBuffExpired?.Invoke(BuffType.Slow);
        }

        /// <summary>
        /// 加速到期：恢复移速。
        /// 与 ExpireSlow 完全对称——两个修饰器共用一份快照，因此"恢复"这件事也只有一份实现
        /// （ApplySpeedModifier 会按当前还挂着哪些修饰器重算），到期顺序不会影响最终结果。
        /// </summary>
        private void ExpireHaste()
        {
            hasted = false;
            hastePercent = 0f;

            ApplySpeedModifier();
            ReleaseSpeedSnapshotIfIdle();

            if (logBuffEvents)
            {
                Debug.Log($"[BuffComponent] {name} 加速已解除，移速恢复至 {baseSpeed:F2}", this);
            }

            OnBuffExpired?.Invoke(BuffType.Haste);
        }

        private void ExpireSilence()
        {
            silenced = false;
            silenceEndTime = 0f;

            if (logBuffEvents)
            {
                Debug.Log($"[BuffComponent] {name} 沉默已解除", this);
            }

            OnBuffExpired?.Invoke(BuffType.Silence);
        }

        /// <summary>清除强化普攻状态（消耗与超时共用）。</summary>
        private void ClearEmpower()
        {
            empowerActive = false;
            empowerBonusDamage = 0f;
            empowerSilenceDuration = 0f;
            empowerEndTime = 0f;
        }

        private void ExpireStun()
        {
            stunned = false;
            ReleaseControlLocks();

            if (logBuffEvents)
            {
                Debug.Log($"[BuffComponent] {name} 眩晕已解除", this);
            }

            OnBuffExpired?.Invoke(BuffType.Stun);
        }

        private void ExpireShield()
        {
            shieldActive = false;
            shieldEndTime = 0f;

            if (Health != null)
            {
                Health.ClearShield();
            }

            if (logBuffEvents)
            {
                Debug.Log($"[BuffComponent] {name} 护盾已到期移除", this);
            }

            OnBuffExpired?.Invoke(BuffType.Shield);
        }

        /// <summary>
        /// 清空全部状态效果并归还一切副作用（移速、控制锁）。
        /// 调用时机：死亡、物体被禁用。
        /// </summary>
        public void ClearAllBuffs()
        {
            bool hadStun = stunned;
            bool hadShield = shieldActive;
            bool hadSpeedModifier = slowed || hasted;

            slowed = false;
            slowPercent = 0f;
            slowEndTime = 0f;

            stunned = false;
            stunEndTime = 0f;

            shieldActive = false;
            shieldEndTime = 0f;

            hasted = false;
            hastePercent = 0f;
            hasteEndTime = 0f;

            silenced = false;
            silenceEndTime = 0f;

            ClearEmpower();

            // 护盾数值在 HealthComponent，只有它存在时才需要清（死亡时 HealthComponent 已自行清过）。
            if (hadShield && Health != null && !Health.IsDead)
            {
                Health.ClearShield();
            }

            // 恢复移速：死亡时不必恢复（不会再移动），但禁用再启用时必须恢复，故统一处理。
            if (hadSpeedModifier && !IsDead)
            {
                ApplySpeedModifier();
            }

            // 快照作废：所有修饰器都已清除，留着快照会让下一次减速/加速误用旧的原速。
            hasSpeedSnapshot = false;

            // 眩晕持有的锁必须归还——注意这里【不】判断施法前摇：死亡/禁用场景下前摇也已经作废。
            if (hadStun)
            {
                if (Movement != null)
                {
                    Movement.SetMovementLocked(false);
                }

                if (Combat != null)
                {
                    Combat.SetAttackLocked(false);
                }
            }
        }

        #endregion

        #region 控制锁

        /// <summary>
        /// 上锁：眩晕期间禁止移动与攻击。
        /// 只通过组件的公开方法操作，不直接碰 NavMeshAgent。
        /// </summary>
        private void LockControls()
        {
            if (Movement != null)
            {
                // 先 Stop 再上锁：上锁本身也会 Stop，这里显式调用是为了让"眩晕瞬间立刻停下"这件事不依赖 SetMovementLocked 的实现细节。
                Movement.Stop();
                Movement.SetMovementLocked(true);
            }

            if (Combat != null)
            {
                Combat.SetAttackLocked(true);
            }
        }

        /// <summary>
        /// 解锁：眩晕结束。
        ///
        /// 【为什么要检查施法前摇】施法前摇与眩晕共用同一把锁（MovementComponent.SetMovementLocked /
        /// CombatComponent.SetAttackLocked）。若这里无条件解锁，会出现两种破绽：
        ///  · 眩晕在前摇期间结束 → 把前摇的锁也解了 → 前摇剩下的时间可以被点走；
        ///  · 反之，SkillComponent 结束前摇时也会检查 IsStunned，两边的判断是对称的，
        ///    因此无论"谁先结束"，锁都只在【两个持有者都放开】之后才真正解除。
        /// </summary>
        private void ReleaseControlLocks()
        {
            SkillComponent resolvedSkills = Skills;
            if (resolvedSkills != null && resolvedSkills.IsCasting)
            {
                // 施法前摇仍持有锁，交由它在释放结束时自行解锁。
                return;
            }

            if (Movement != null)
            {
                Movement.SetMovementLocked(false);
            }

            if (Combat != null)
            {
                Combat.SetAttackLocked(false);
            }
        }

        /// <summary>
        /// 取原速快照（若尚未取过）。
        ///
        /// 【为什么必须"只在没有移速修饰时取一次"】减速与加速的语义都是"从原速按比例改"，
        /// 所以必须知道原速是多少。若每个来源各自快照，A 记下 6、B 记下 3.6，
        /// 到期恢复顺序不同会得到 6 或 3.6，是典型的静默错误。
        /// 阶段八加入加速后，两个修饰器仍然共用这一份快照，只是改成"两个系数相乘"。
        /// </summary>
        private void EnsureSpeedSnapshot()
        {
            if (hasSpeedSnapshot)
            {
                return;
            }

            baseSpeed = Movement != null ? Movement.Speed : 0f;
            hasSpeedSnapshot = true;
        }

        /// <summary>
        /// 两个修饰器都已解除时作废快照，让下一次减速/加速重新取原速。
        /// 不作废的后果：单位在减速期间被复活/换装（原速变化）后，下一次减速会恢复到一个过期的原速上。
        /// </summary>
        private void ReleaseSpeedSnapshotIfIdle()
        {
            if (slowed || hasted)
            {
                return;
            }

            hasSpeedSnapshot = false;
        }

        /// <summary>
        /// 按当前状态写入移速。
        /// 这是本组件【唯一】改移速的地方，保证"减速 / 加速 → 恢复"这条路径只有一份实现。
        ///
        /// 两个修饰器同时存在时系数【相乘】：移速 = 原速 × (1 - 减速比例) × (1 + 加速比例)。
        /// 相乘保证结果与施加顺序无关（可交换），相加则会因为顺序不同而得到不同的数。
        /// </summary>
        private void ApplySpeedModifier()
        {
            MovementComponent resolvedMovement = Movement;
            if (resolvedMovement == null)
            {
                // 建筑类单位没有移动能力，减速 / 加速对它们没有意义，静默跳过即可（不是错误）。
                return;
            }

            if (!hasSpeedSnapshot)
            {
                // 没有任何移速修饰（快照已作废）：不该写移速，否则会用一个陈旧的原速覆盖掉外部刚写入的值。
                return;
            }

            float factor = 1f;

            if (slowed)
            {
                factor *= 1f - Mathf.Clamp01(slowPercent);
            }

            if (hasted)
            {
                factor *= 1f + Mathf.Max(0f, hastePercent);
            }

            resolvedMovement.SetMoveSpeed(baseSpeed * factor);
        }

        /// <summary>是否已死亡（生命组件缺失时视为未死亡——没有生命就永远不会死）。</summary>
        private bool IsDead
        {
            get
            {
                HealthComponent resolved = Health;
                return resolved != null && resolved.IsDead;
            }
        }

        #endregion
    }
}
