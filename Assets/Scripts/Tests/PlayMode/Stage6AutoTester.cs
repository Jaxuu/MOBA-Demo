using System.Collections;
using System.Reflection;
using UnityEngine;
using UnityEngine.AI;
using MOBA.Components;
using MOBA.Core;
using MOBA.Data;
using MOBA.Skills;

namespace MOBA.Tests
{
    /// <summary>
    /// 阶段六自动化测试：不依赖任何场景、预制体或 Inspector 配置，纯代码在内存里搭出
    /// 「施法者 + 敌方 + 友方」三个单位，跑一遍技能系统的完整链路与全部拒绝路径。
    ///
    /// 覆盖的验收点（逐条对应 MOBA_Demo_Plan.md 阶段六的验收标准）：
    /// 1. 【拒绝路径零副作用】蓝量不足 / 超出施法距离 / 目标非法（友方、null）/ 冷却中
    ///    四种情况：TryCast 返回 false，且不扣蓝、不起 CD、不广播任何事件；
    /// 2. 【Q 弹道命中】弹道有真实飞行时间，命中时结算伤害，掉血量 == 配置伤害；
    /// 3. 【非穿透】命中后弹道立即回收（场景中不残留 Projectile）；
    /// 4. 【目标中途销毁】弹道安全回收，无空引用、无残留；
    /// 5. 【护盾】优先吸收伤害，耗尽后才扣血；
    /// 6. 【眩晕】移动与攻击同时被锁，到期自动解锁；
    /// 7. 【减速】生效期间移速符合配置，到期精确恢复原值；
    /// 8. 【W 非指向性 AOE】圈内敌方被减速、友方零影响。
    ///
    /// 运行方式（两种任选其一）：
    /// A. 免场景：把本文件的 enableAutoBootstrap 改回 true，进入播放模式即自动运行（见 AutoBootstrap）。
    /// B. 手动（推荐）：把本组件挂到场景任意空物体上，保持 autoRunOnStart = true 后播放。
    ///
    /// 【推荐在 MainScene 里运行】被减速的单位需要 NavMeshAgent，而 agent 若不在 NavMesh 上
    /// 会打印一条「not close enough to the NavMesh」——那是预期内的噪声，不影响任何断言
    /// （移速断言读的是 agent.speed，与是否在网格上无关）。详见 ResolveNavMeshPosition。
    ///
    /// 【默认已关闭自动运行】与 Stage2AutoTester 同一约定：避免每次进播放模式都生成测试单位，
    /// 干扰正常调试（测试单位是敌方阵营，会被真实小兵/塔当成目标）。
    /// </summary>
    [DisallowMultipleComponent]
    public class Stage6AutoTester : MonoBehaviour
    {
        [Header("测试参数（断言会跟着自动换算）")]
        [Tooltip("测试用技能：施法距离。")]
        [SerializeField] private float testCastRange = 8f;

        [Tooltip("测试用技能：前摇（秒）。")]
        [SerializeField] private float testCastTime = 0.25f;

        [Tooltip("测试用技能：冷却（秒）。刻意取得比配置值短，避免测试为了等冷却而空转太久。")]
        [SerializeField] private float testCooldown = 2f;

        [Tooltip("测试用技能：蓝耗。")]
        [SerializeField] private float testManaCost = 30f;

        [Tooltip("测试用技能：弹道速度（米/秒）。")]
        [SerializeField] private float testProjectileSpeed = 14f;

        [Tooltip("测试用技能：单次伤害。")]
        [SerializeField] private float testSkillDamage = 120f;

        [Tooltip("测试用技能：法力上限。")]
        [SerializeField] private float testMaxMana = 300f;

        [Tooltip("测试用单位：最大生命值。取得很大，避免被场景里真实的小兵/塔打死而污染断言。")]
        [SerializeField] private float testUnitMaxHealth = 100000f;

        [Tooltip("测试用单位：移动速度。减速断言以它为基准。")]
        [SerializeField] private float testUnitMoveSpeed = 6f;

        [Tooltip("测试单位所在区域（会被吸附到 NavMesh 上；刻意远离兵线以免被真实单位打扰）。")]
        [SerializeField] private Vector3 testAreaCenter = new Vector3(0f, 0f, 12f);

        [Header("运行开关")]
        [Tooltip("Start 时自动开始测试。")]
        [SerializeField] private bool autoRunOnStart = true;

        [Tooltip("测试结束后销毁动态创建的单位与配置资产。默认关闭，方便在 Hierarchy / Inspector 里检查结果。")]
        [SerializeField] private bool destroyUnitsAfterTest = false;

        /// <summary>
        /// 免场景自动引导开关（静态字段，无法在 Inspector 显示）。
        /// 默认 false：测试单位是敌方阵营，每次进播放模式都生成会干扰真实对局调试。
        /// </summary>
        private static bool enableAutoBootstrap = false;

        /// <summary>防止同一次播放会话里重复引导。</summary>
        private static bool hasBootstrapped;

        // ---- 被测单位 ----
        private EntityBase caster;      // 施法者（EntityType.Tower：不需要移动，可自由摆放）
        private EntityBase enemy;       // 敌方目标（EntityType.Hero：需要 MovementComponent 验证减速）
        private EntityBase ally;        // 友方单位（用于验证"不能对友方施法"与"AOE 不影响友方"）
        private EntityBase doomedEnemy; // 专用于"弹道飞行中目标被销毁"的临时敌方单位

        // ---- 组件缓存 ----
        private HealthComponent enemyHealth;
        private HealthComponent allyHealth;
        private HealthComponent casterHealth;
        private ManaComponent casterMana;
        private SkillComponent casterSkills;
        private CombatComponent enemyCombat;
        private BuffComponent enemyBuff;
        private BuffComponent allyBuff;
        private MovementComponent enemyMovement;

        // ---- 动态创建的配置资产 ----
        private EntityStatsData casterStats;
        private EntityStatsData enemyStats;
        private EntityStatsData allyStats;
        private EntityStatsData doomedStats;
        private AttackData casterAttack;
        private AttackData dummyAttack;
        private SkillData fireballSkill;
        private SkillData slowZoneSkill;

        // ---- 断言统计 ----
        private int passCount;
        private int failCount;
        private int castStartedCount;
        private int spellReleasedCount;

        /// <summary>
        /// 免场景引导：播放模式加载完场景后自动创建一个承载测试的物体。
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoBootstrap()
        {
            if (!enableAutoBootstrap || hasBootstrapped)
            {
                return;
            }

            hasBootstrapped = true;

            GameObject testerObject = new GameObject("[Stage6AutoTester]");
            testerObject.AddComponent<Stage6AutoTester>();
        }

        private void Start()
        {
            if (!autoRunOnStart)
            {
                Debug.Log("[Stage6AutoTester] autoRunOnStart 已关闭，跳过测试。", this);
                return;
            }

            if (!BuildTestUnits())
            {
                Debug.LogError("[Stage6AutoTester] 测试环境搭建失败，测试中止。", this);
                return;
            }

            StartCoroutine(RunTestRoutine());
        }

        #region 测试环境搭建

        /// <summary>
        /// 纯代码搭建测试环境：造配置资产 → 造三个单位 → 订阅事件 → 注入配置。
        /// </summary>
        /// <returns>搭建成功返回 true。</returns>
        private bool BuildTestUnits()
        {
            // ---- 1. 造配置资产（CreateInstance 生成的是内存中的资产，不会写入磁盘）----
            casterAttack = CreateAttackData(testSkillDamage, testCastRange, 1f);
            dummyAttack = CreateAttackData(1f, 1f, 1f);

            fireballSkill = CreateFireballSkill();
            slowZoneSkill = CreateSlowZoneSkill();

            // caster 与 ally 用 EntityType.Tower：本测试不需要它们移动，
            // 而 Tower/Base 按设计就没有 MovementComponent，可避免 EntityBase 报
            // 「英雄/小兵必须有 MovementComponent」的告警，也让它们可以被自由摆放（没有 NavMeshAgent 约束）。
            casterStats = CreateStatsData(testUnitMaxHealth, 0f, testCastRange, casterAttack, fireballSkill, slowZoneSkill);
            allyStats = CreateStatsData(testUnitMaxHealth, 0f, testCastRange, dummyAttack, null, null);
            doomedStats = CreateStatsData(testUnitMaxHealth, 0f, testCastRange, dummyAttack, null, null);

            // enemy 必须是 Hero：减速验证需要 MovementComponent，而只有 Hero/Minion 才被允许挂载它。
            enemyStats = CreateStatsData(testUnitMaxHealth, testUnitMoveSpeed, testCastRange, dummyAttack, null, null);

            // ---- 2. 造单位 ----
            Vector3 anchor = ResolveNavMeshPosition(testAreaCenter);

            caster = CreateUnit("TestCaster", anchor + new Vector3(0f, 0f, -3f), EntityType.Tower, false);
            ally = CreateUnit("TestAlly", anchor + new Vector3(2f, 0f, 0f), EntityType.Tower, false);
            enemy = CreateUnit("TestEnemy", anchor, EntityType.Hero, true);

            if (caster == null || ally == null || enemy == null)
            {
                return false;
            }

            // ---- 3. 取组件（不能读 entity.Skills 之类的属性：那是 Awake 里缓存的，
            //         而 AddComponent<EntityBase> 触发 Awake 时其它组件还没挂上）----
            casterSkills = caster.GetComponent<SkillComponent>();
            casterMana = caster.GetComponent<ManaComponent>();
            casterHealth = caster.GetComponent<HealthComponent>();

            enemyHealth = enemy.GetComponent<HealthComponent>();
            enemyBuff = enemy.GetComponent<BuffComponent>();
            enemyMovement = enemy.GetComponent<MovementComponent>();
            enemyCombat = enemy.GetComponent<CombatComponent>();

            allyHealth = ally.GetComponent<HealthComponent>();
            allyBuff = ally.GetComponent<BuffComponent>();

            if (casterSkills == null || casterMana == null || enemyHealth == null ||
                enemyBuff == null || enemyMovement == null || allyHealth == null || allyBuff == null)
            {
                Debug.LogError(
                    "[Stage6AutoTester] 单位缺少技能系统必需组件（SkillComponent / ManaComponent / " +
                    "BuffComponent / MovementComponent / HealthComponent），测试无法继续。", this);
                return false;
            }

            // ---- 4. 先订阅事件，再注入配置（顺序与 Stage2AutoTester 一致：Initialize 会广播初始值）----
            casterSkills.OnCastStarted += HandleCastStarted;
            casterSkills.OnSpellReleased += HandleSpellReleased;

            // ---- 5. 注入配置 ----
            caster.Initialize(casterStats, TeamType.Player, EntityType.Tower);
            ally.Initialize(allyStats, TeamType.Player, EntityType.Tower);
            enemy.Initialize(enemyStats, TeamType.Enemy, EntityType.Hero);

            return true;
        }

        /// <summary>
        /// 用代码创建一个单位。是否挂 MovementComponent 由参数决定
        /// （挂了会自动带上 NavMeshAgent，因为 MovementComponent 声明了 RequireComponent）。
        ///
        /// 【刻意不挂 Collider】EntityBase.ValidateDependencies 会因此报 4 条
        /// 「没有 Collider，该单位无法被索敌锁定」的告警——那是预期内的噪声。
        /// 不挂碰撞体的原因是测试隔离：一旦带上碰撞体，场景里真实的小兵与防御塔
        /// 就会通过 Physics.OverlapSphere 把测试单位当成可攻击目标，测试期间掉的血
        /// 会污染"掉血量 == 配置伤害"这类精确断言。
        /// </summary>
        /// <param name="unitName">对象名。</param>
        /// <param name="position">初始位置。</param>
        /// <param name="entityType">单位类型（决定 EntityBase 的依赖校验口径）。</param>
        /// <param name="withMovement">是否挂载移动组件。</param>
        private EntityBase CreateUnit(string unitName, Vector3 position, EntityType entityType, bool withMovement)
        {
            GameObject unitObject = new GameObject(unitName);
            unitObject.transform.position = position;

            EntityBase entity = unitObject.AddComponent<EntityBase>();
            unitObject.AddComponent<HealthComponent>();
            unitObject.AddComponent<TargetingComponent>();
            unitObject.AddComponent<CombatComponent>();
            unitObject.AddComponent<ManaComponent>();
            unitObject.AddComponent<BuffComponent>();
            unitObject.AddComponent<SkillComponent>();

            if (withMovement)
            {
                unitObject.AddComponent<MovementComponent>();
            }

            return entity;
        }

        /// <summary>
        /// 把测试区域吸附到 NavMesh 上。
        /// 必要性：被减速的单位挂有 NavMeshAgent，而 agent 若不在 NavMesh 上会打印
        /// 「Failed to create agent because it is not close enough to the NavMesh」。
        /// 这条噪声不影响任何断言（移速读的是 agent.speed），但会让人误以为测试失败，因此这里先尝试规避。
        /// </summary>
        private Vector3 ResolveNavMeshPosition(Vector3 preferred)
        {
            if (NavMesh.SamplePosition(preferred, out NavMeshHit navHit, 6f, NavMesh.AllAreas))
            {
                return navHit.position;
            }

            Debug.LogWarning(
                $"[Stage6AutoTester] 场景中 {preferred} 附近没有 NavMesh，测试单位将按原始坐标放置。" +
                "接下来被减速单位的 NavMeshAgent 会报「not close enough to the NavMesh」——" +
                "这是预期内的噪声，不影响任何断言（移速断言读的是 agent.speed）。" +
                "若想消除该噪声，请在已烘焙 NavMesh 的 MainScene 中运行本测试。", this);

            return preferred;
        }

        /// <summary>
        /// 造一份攻击配置。字段是 private 的，只能用反射写入——
        /// 这是刻意的：生产类不提供公开 setter，是为了守住「ScriptableObject 是只读模板」的约定。
        /// </summary>
        private AttackData CreateAttackData(float damage, float range, float interval)
        {
            AttackData data = ScriptableObject.CreateInstance<AttackData>();
            SetPrivateField(data, "damage", damage);
            SetPrivateField(data, "attackRange", range);
            SetPrivateField(data, "attackInterval", interval);
            return data;
        }

        /// <summary>造一份单位属性配置。</summary>
        private EntityStatsData CreateStatsData(
            float maxHealth, float moveSpeed, float detectionRange, AttackData attack, SkillData q, SkillData w)
        {
            EntityStatsData data = ScriptableObject.CreateInstance<EntityStatsData>();
            SetPrivateField(data, "maxHealth", maxHealth);
            SetPrivateField(data, "moveSpeed", moveSpeed);
            SetPrivateField(data, "detectionRange", detectionRange);
            SetPrivateField(data, "attack", attack);
            SetPrivateField(data, "maxMana", testMaxMana);
            SetPrivateField(data, "manaRegenPerSecond", 0f);
            SetPrivateField(data, "skillQ", q);
            SetPrivateField(data, "skillW", w);
            return data;
        }

        /// <summary>
        /// 造 Q 技能配置：指向性非穿透弹道。数值刻意与一键组装工具生成的 HeroSkillQ 保持一致，
        /// 只有 maxTravelDistance 放宽（测试里目标距离可能更远）。
        /// </summary>
        private SkillData CreateFireballSkill()
        {
            SkillData data = ScriptableObject.CreateInstance<SkillData>();
            SetPrivateField(data, "slot", SkillSlot.Q);
            SetPrivateField(data, "displayName", "测试火球");
            SetPrivateField(data, "castType", SkillCastType.UnitTarget);
            SetPrivateField(data, "castRange", testCastRange);
            SetPrivateField(data, "castTime", testCastTime);
            SetPrivateField(data, "cooldown", testCooldown);
            SetPrivateField(data, "manaCost", testManaCost);
            SetPrivateField(data, "projectileSpeed", testProjectileSpeed);
            SetPrivateField(data, "projectileRadius", 0.5f);
            SetPrivateField(data, "maxHitCount", 1);
            SetPrivateField(data, "maxTravelDistance", 30f);
            SetPrivateField(data, "effectType", SkillEffectType.Damage);
            SetPrivateField(data, "damage", testSkillDamage);
            return data;
        }

        /// <summary>
        /// 造 W 技能配置：非指向性 AOE 减速圈。数值与一键组装工具生成的 HeroSkillW 一致。
        /// </summary>
        private SkillData CreateSlowZoneSkill()
        {
            SkillData data = ScriptableObject.CreateInstance<SkillData>();
            SetPrivateField(data, "slot", SkillSlot.W);
            SetPrivateField(data, "displayName", "测试减速圈");
            SetPrivateField(data, "castType", SkillCastType.GroundPoint);
            SetPrivateField(data, "castRange", 10f);
            SetPrivateField(data, "castTime", 0.15f);
            SetPrivateField(data, "cooldown", 3f);
            SetPrivateField(data, "manaCost", 50f);
            SetPrivateField(data, "effectRadius", 3.5f);
            SetPrivateField(data, "areaDuration", 4f);
            SetPrivateField(data, "tickInterval", 0.5f);
            SetPrivateField(data, "effectType", SkillEffectType.Slow);
            SetPrivateField(data, "slowPercent", 0.4f);
            SetPrivateField(data, "slowDuration", 0.6f);
            return data;
        }

        /// <summary>
        /// 反射写入私有序列化字段。字段被改名时这里会立刻报错（而不是静默失效），属于可接受的测试耦合。
        /// </summary>
        private static void SetPrivateField(object target, string fieldName, object value)
        {
            if (target == null)
            {
                Debug.LogError($"[Stage6AutoTester] SetPrivateField 目标为空，字段 {fieldName} 未写入。");
                return;
            }

            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null)
            {
                Debug.LogError(
                    $"[Stage6AutoTester] 在 {target.GetType().Name} 上找不到私有字段 \"{fieldName}\"（可能已被改名），该配置未生效。");
                return;
            }

            field.SetValue(target, value);
        }

        #endregion

        #region 事件监听与断言

        private void HandleCastStarted(SkillSlot slot, SkillData data)
        {
            castStartedCount++;
        }

        private void HandleSpellReleased(SkillSlot slot, SkillData data)
        {
            spellReleasedCount++;
        }

        /// <summary>
        /// 单条断言。成功/失败都打印，失败时额外计数，最后统一汇总。
        /// </summary>
        private void Check(string description, bool condition)
        {
            if (condition)
            {
                passCount++;
                Debug.Log($"[Stage6 · 断言通过] {description}");
            }
            else
            {
                failCount++;
                Debug.LogError($"[Stage6 · 断言失败] {description}");
            }
        }

        /// <summary>
        /// 断言"拒绝原因里包含某个关键词"。
        ///
        /// 用【包含】而不是全等：原因文本里带可量化信息（例如「距离太远（目标 33.0m > 射程 8.0m）」），
        /// 断言写死全文会在改一次文案或改一个测试数值后立刻失效。
        /// 关键词只取结论部分（"法力不足" / "冷却中" / "距离太远" / "目标非法" / "缺少目标" / "前摇硬直中"），
        /// 既能验证"拒绝得对"，又不会把测试绑死在文案上。
        /// </summary>
        private static bool ReasonMatches(string failReason, string keyword)
        {
            return !string.IsNullOrEmpty(failReason) && failReason.Contains(keyword);
        }

        #endregion

        #region 测试主流程

        private IEnumerator RunTestRoutine()
        {
            Debug.Log("========== 阶段六自动测试开始（Stage6AutoTester）==========");

            // 等一帧，让新建对象的生命周期回调走完，确保读到的是初始化之后的状态。
            yield return null;

            Debug.Log(
                $"[初始状态] 施法者 {caster.name}：生命 {casterHealth.CurrentHealth:F0}，法力 {casterMana.CurrentMana:F0}/{casterMana.MaxMana:F0}，" +
                $"Q = {(casterSkills.GetSkillData(SkillSlot.Q) != null ? "已配置" : "缺失")}，" +
                $"W = {(casterSkills.GetSkillData(SkillSlot.W) != null ? "已配置" : "缺失")} | " +
                $"敌方 {enemy.name}：生命 {enemyHealth.CurrentHealth:F0}，移速 {enemyMovement.Speed:F2}");

            Check("SkillComponent 已拿到 Q / W 配置（验证 EntityStatsData → SkillComponent 的注入链）",
                casterSkills.GetSkillData(SkillSlot.Q) != null && casterSkills.GetSkillData(SkillSlot.W) != null);

            Check("ManaComponent 已拿到法力上限（验证 EntityStatsData → ManaComponent 的注入链）",
                Mathf.Approximately(casterMana.MaxMana, testMaxMana));

            yield return RunRejectionTests();
            yield return RunProjectileTests();
            yield return RunProjectileTargetLostTest();
            yield return RunShieldTests();
            yield return RunControlLockAndSlowTests();
            yield return RunAreaZoneTests();

            ReportSummary();
        }

        /// <summary>
        /// 阶段 1：四条拒绝路径必须"零副作用"（不扣蓝、不起 CD、不广播事件）。
        /// </summary>
        private IEnumerator RunRejectionTests()
        {
            Debug.Log("[阶段1] 拒绝路径 —— 四种非法施法都必须被拒绝且不产生任何副作用");

            // ---- 1a 蓝量不足 ----
            float manaBefore = casterMana.CurrentMana;
            casterMana.TrySpend(casterMana.CurrentMana); // 把蓝扣光
            int castStartedBefore = castStartedCount;

            bool castWithoutMana = casterSkills.TryCast(SkillSlot.Q, Vector3.zero, enemy, out string reasonNoMana);

            Check("1a 蓝量不足时 TryCast 返回 false", !castWithoutMana);
            Check("1a 拒绝原因是「法力不足」（验证 failReason 出参）", ReasonMatches(reasonNoMana, "法力不足"));
            Check("1a 蓝量不足时法力不再变化（不产生负数扣减）",
                Mathf.Approximately(casterMana.CurrentMana, 0f));
            Check("1a 蓝量不足时未起冷却", Mathf.Approximately(casterSkills.GetCooldownRemaining(SkillSlot.Q), 0f));
            Check("1a 蓝量不足时未广播 OnCastStarted（表现层收不到事件 = 不会播动画）",
                castStartedCount == castStartedBefore);

            // 恢复法力（Initialize 会把法力填满）
            casterMana.Initialize(testMaxMana, 0f);

            // ---- 1b 超出施法距离 ----
            Vector3 originalCasterPosition = caster.transform.position;
            caster.transform.position = originalCasterPosition + new Vector3(0f, 0f, -30f); // 远离目标
            castStartedBefore = castStartedCount;

            bool castOutOfRange = casterSkills.TryCast(SkillSlot.Q, Vector3.zero, enemy, out string reasonOutOfRange);

            Check("1b 超出施法距离时 TryCast 返回 false", !castOutOfRange);
            Check("1b 拒绝原因是「距离太远」（验证 failReason 出参）", ReasonMatches(reasonOutOfRange, "距离太远"));
            Check("1b 超距时法力未扣减", Mathf.Approximately(casterMana.CurrentMana, testMaxMana));
            Check("1b 超距时未起冷却", Mathf.Approximately(casterSkills.GetCooldownRemaining(SkillSlot.Q), 0f));
            Check("1b 超距时未广播 OnCastStarted", castStartedCount == castStartedBefore);

            caster.transform.position = originalCasterPosition;

            // ---- 1c 目标非法（友方） ----
            castStartedBefore = castStartedCount;
            bool castOnAlly = casterSkills.TryCast(SkillSlot.Q, Vector3.zero, ally, out string reasonOnAlly);

            Check("1c 对友方施放指向性技能被拒绝", !castOnAlly);
            Check("1c 拒绝原因是「目标非法」（验证 failReason 出参）", ReasonMatches(reasonOnAlly, "目标非法"));
            Check("1c 目标非法时法力未扣减", Mathf.Approximately(casterMana.CurrentMana, testMaxMana));
            Check("1c 目标非法时未广播 OnCastStarted", castStartedCount == castStartedBefore);

            // ---- 1d 目标为 null ----
            bool castWithoutTarget = casterSkills.TryCast(SkillSlot.Q, Vector3.zero, null, out string reasonNoTarget);
            Check("1d 指向性技能缺少目标时被拒绝", !castWithoutTarget);
            Check("1d 拒绝原因是「缺少目标」（验证 failReason 出参）", ReasonMatches(reasonNoTarget, "缺少目标"));
            Check("1d 缺少目标时法力未扣减", Mathf.Approximately(casterMana.CurrentMana, testMaxMana));

            yield return null;
        }

        /// <summary>
        /// 阶段 2：Q 弹道 —— 命中结算、伤害一致、非穿透回收、冷却中再放被拒绝。
        /// </summary>
        private IEnumerator RunProjectileTests()
        {
            Debug.Log("[阶段2] Q 指向性弹道 —— 飞行 → 命中结算 → 非穿透回收");

            float healthBefore = enemyHealth.CurrentHealth;
            float manaBefore = casterMana.CurrentMana;
            int startedBefore = castStartedCount;

            bool casted = casterSkills.TryCast(SkillSlot.Q, Vector3.zero, enemy, out string castReason);

            Check($"2a 合法目标 + 射程内 + 蓝量充足时施法成功（failReason = {castReason ?? "null"}）", casted);
            Check("2a 施法成功后扣除法力（蓝耗与配置一致）",
                Mathf.Approximately(manaBefore - casterMana.CurrentMana, testManaCost));
            Check("2a 施法成功后立即进入冷却（施法瞬间起 CD）",
                casterSkills.GetCooldownRemaining(SkillSlot.Q) > 0f);
            Check("2a 施法成功后广播了一次 OnCastStarted",
                castStartedCount == startedBefore + 1);

            // ---- 2a-2 前摇期间再次施法：命中的是"前摇硬直"而不是"冷却中" ----
            // 这一步是刻意插在"立刻再按"的位置：TryCast 的校验链里 isCasting 排在冷却【之前】，
            // 因此前摇未结束时再按，得到的必然是"前摇硬直中"。先验证这一条，
            // 后面的"冷却中"断言才有意义（否则两者会被混为一谈）。
            bool castDuringWindup = casterSkills.TryCast(SkillSlot.Q, Vector3.zero, enemy, out string reasonWindup);
            Check("2a 前摇期间再次施法被拒绝", !castDuringWindup);
            Check("2a 拒绝原因是「前摇硬直中」（验证 failReason 出参）", ReasonMatches(reasonWindup, "前摇硬直中"));

            // 等前摇结束（0.25s）、但冷却（2s）还没好 —— 此时再按才是真正的"冷却中"。
            yield return new WaitForSeconds(testCastTime + 0.1f);

            float manaAfterFirstCast = casterMana.CurrentMana;
            bool castWhileCooling = casterSkills.TryCast(SkillSlot.Q, Vector3.zero, enemy, out string reasonCooling);

            Check("2b 冷却中再次施法被拒绝", !castWhileCooling);
            Check("2b 拒绝原因是「冷却中」（验证 failReason 出参）", ReasonMatches(reasonCooling, "冷却中"));
            Check("2b 冷却中被拒绝时不扣蓝", Mathf.Approximately(casterMana.CurrentMana, manaAfterFirstCast));

            // 等弹道命中（距离 3 米 / 速度 14 → 约 0.21 秒；给足余量）
            float deadline = Time.time + 3f;
            while (enemyHealth.CurrentHealth >= healthBefore && Time.time < deadline)
            {
                yield return null;
            }

            Check("2c 弹道命中并结算伤害（掉血量 == 配置伤害）",
                Mathf.Approximately(healthBefore - enemyHealth.CurrentHealth, testSkillDamage));

            // Destroy 是延迟到帧末执行的，因此再等一帧才能确认弹道已被回收
            yield return null;
            yield return null;

            Projectile[] remaining = FindObjectsByType<Projectile>(FindObjectsSortMode.None);
            Check("2d 非穿透：命中后弹道已被回收（场景中不残留 Projectile）", remaining.Length == 0);
        }

        /// <summary>
        /// 阶段 3：弹道飞行途中目标被销毁 —— 弹道必须安全回收，不抛空引用、不残留。
        /// </summary>
        private IEnumerator RunProjectileTargetLostTest()
        {
            Debug.Log("[阶段3] 弹道飞行中目标被销毁 —— 必须安全回收且无空引用");

            // 等冷却结束
            yield return WaitForCooldown(SkillSlot.Q);

            // 造一个距离较远的临时敌方单位，让弹道有足够的飞行时间（7 米 / 14 ≈ 0.5 秒）
            doomedEnemy = CreateUnit("TestDoomedEnemy", caster.transform.position + new Vector3(0f, 0f, 7f),
                EntityType.Tower, false);
            doomedEnemy.Initialize(doomedStats, TeamType.Enemy, EntityType.Tower);
            yield return null;

            bool casted = casterSkills.TryCast(SkillSlot.Q, Vector3.zero, doomedEnemy, out string doomedReason);
            Check($"3a 对临时敌方单位施法成功（failReason = {doomedReason ?? "null"}）", casted);

            // 弹道飞行途中销毁目标（0.2 秒时，弹道还在半路）
            yield return new WaitForSeconds(0.2f);
            Destroy(doomedEnemy.gameObject);

            // 等弹道飞完剩下的路程 + 回收
            yield return new WaitForSeconds(1.5f);

            Projectile[] remaining = FindObjectsByType<Projectile>(FindObjectsSortMode.None);
            Check("3b 目标中途销毁后弹道已安全回收（无残留、无空引用异常）", remaining.Length == 0);
            Check("3c 目标销毁后本次施法未再结算伤害（弹道未命中任何目标）",
                Mathf.Approximately(enemyHealth.CurrentHealth, enemyHealth.MaxHealth - testSkillDamage));
        }

        /// <summary>
        /// 阶段 4：护盾 —— 优先吸收伤害，耗尽后才扣血。
        /// </summary>
        private IEnumerator RunShieldTests()
        {
            Debug.Log("[阶段4] 护盾 —— 优先吸收伤害，耗尽后才扣血");

            float healthBefore = allyHealth.CurrentHealth;

            bool shieldApplied = SkillEffectResolver.ApplyShield(ally, 200f, 0f);
            Check("4a 施加护盾成功", shieldApplied);
            Check("4a 护盾值写入 HealthComponent", Mathf.Approximately(allyHealth.CurrentShield, 200f));

            // 伤害小于护盾：应全部被吸收，血量不变
            allyHealth.TakeDamage(150f, caster);
            Check("4b 伤害小于护盾时血量不变（全部被吸收）",
                Mathf.Approximately(allyHealth.CurrentHealth, healthBefore));
            Check("4b 护盾被扣减至剩余 50", Mathf.Approximately(allyHealth.CurrentShield, 50f));

            // 伤害大于剩余护盾：溢出部分才扣血
            allyHealth.TakeDamage(100f, caster);
            Check("4c 伤害大于剩余护盾时，溢出部分扣血（50 点）",
                Mathf.Approximately(healthBefore - allyHealth.CurrentHealth, 50f));
            Check("4c 护盾已耗尽", Mathf.Approximately(allyHealth.CurrentShield, 0f));
            Check("4c 伤害来源被记录（供阶段七做击杀归属）", ReferenceEquals(allyHealth.LastDamageSource, caster));

            yield return null;
        }

        /// <summary>
        /// 阶段 5：眩晕锁与减速 —— 移动/攻击同时被锁、到期解锁；减速到期精确恢复原速。
        /// </summary>
        private IEnumerator RunControlLockAndSlowTests()
        {
            Debug.Log("[阶段5] 眩晕锁与减速 —— 到期必须精确恢复");

            // ---- 5a 眩晕：移动与攻击同时被锁 ----
            enemyBuff.ApplyStun(0.5f);

            Check("5a 眩晕后移动被锁", enemyMovement.IsMovementLocked);
            Check("5a 眩晕后攻击被锁", enemyCombat.IsAttackLocked);
            Check("5a 眩晕后 BuffComponent 状态为眩晕中", enemyBuff.IsStunned);

            // 眩晕期间移动指令必须被拒绝（"眩晕真的能阻止位移"的直接证据）。
            // 但只有在单位确实位于 NavMesh 上时这个断言才有意义——否则 MoveTo 会因寻路失败而返回 false，
            // 与"被锁拒绝"无法区分，那种情况下断言会"因为错误的原因通过"，比不测更危险。
            NavMeshAgent enemyAgent = enemy.GetComponent<NavMeshAgent>();
            bool agentReady = enemyAgent != null && enemyAgent.enabled && enemyAgent.isOnNavMesh;

            if (agentReady)
            {
                bool moveWhileStunned = enemyMovement.MoveTo(enemy.transform.position + new Vector3(3f, 0f, 0f));
                Check("5a 眩晕期间移动指令被拒绝", !moveWhileStunned);
            }
            else
            {
                Debug.Log(
                    "[Stage6 · 跳过] 5a 眩晕期间移动指令被拒绝 —— 本单位不在 NavMesh 上，" +
                    "无法区分「被锁拒绝」与「寻路失败」，本项跳过（移速与锁状态断言不受影响）。");
            }

            // 等眩晕结束
            yield return new WaitForSeconds(0.8f);

            Check("5b 眩晕到期后移动锁已解除", !enemyMovement.IsMovementLocked);
            Check("5b 眩晕到期后攻击锁已解除", !enemyCombat.IsAttackLocked);
            Check("5b 眩晕到期后状态已清除", !enemyBuff.IsStunned);

            // ---- 5c 减速：生效期间移速符合配置，到期精确恢复 ----
            float baseSpeed = enemyMovement.Speed;
            Check("5c 减速前的移速等于配置值", Mathf.Approximately(baseSpeed, testUnitMoveSpeed));

            enemyBuff.ApplySlow(0.4f, 0.3f);

            Check("5c 减速生效期间移速 = 原速 × (1 - 0.4)",
                Mathf.Approximately(enemyMovement.Speed, baseSpeed * 0.6f));

            yield return new WaitForSeconds(0.6f);

            Check("5c 减速到期后移速精确恢复原值",
                Mathf.Approximately(enemyMovement.Speed, baseSpeed));
            Check("5c 减速到期后状态已清除", !enemyBuff.IsSlowed);
        }

        /// <summary>
        /// 阶段 6：W 非指向性 AOE —— 圈内敌方被减速，友方零影响。
        /// </summary>
        private IEnumerator RunAreaZoneTests()
        {
            Debug.Log("[阶段6] W 非指向性减速圈 —— 圈内敌方受伤，友方零影响");

            // 把友方单位挪到落点附近，验证"AOE 不会误伤/误减速友方"
            ally.transform.position = enemy.transform.position + new Vector3(1.5f, 0f, 0f);

            // 记录友方当前血量作为基线：前面护盾阶段已经对它造成过伤害，
            // 因此不能拿 MaxHealth 作比较（那会让断言因为"记错基线"而失败）。
            float allyHealthBefore = allyHealth.CurrentHealth;

            // 等 W 冷却（本阶段之前没放过 W，理论上无需等待，这里只为稳妥）
            yield return WaitForCooldown(SkillSlot.W);

            float enemyBaseSpeed = enemyMovement.Speed;
            int releasedBefore = spellReleasedCount;

            bool casted = casterSkills.TryCast(SkillSlot.W, enemy.transform.position, null, out string zoneReason);

            Check($"6a 非指向性技能（只需落点、无需目标）施法成功（failReason = {zoneReason ?? "null"}）", casted);
            Check("6a 施法成功后立即进入冷却", casterSkills.GetCooldownRemaining(SkillSlot.W) > 0f);

            // 等前摇结束（0.15s）+ 第一个周期结算（tickInterval = 0.5s）。
            // 注意：OnSpellReleased 是在前摇结束、范围场真正生成时才广播的，
            // 因此这个断言必须放在等待之后——放在 TryCast 之后会失败（那时还没释放）。
            yield return new WaitForSeconds(0.8f);

            Check("6a 前摇结束后广播了一次 OnSpellReleased（表现层据此播释放特效）",
                spellReleasedCount == releasedBefore + 1);

            Check("6b 圈内敌方被减速", enemyBuff.IsSlowed);
            Check("6b 减速比例与配置一致（0.4）",
                Mathf.Approximately(enemyBuff.CurrentSlowPercent, 0.4f));
            Check("6b 敌方移速已按减速比例下降",
                enemyMovement.Speed < enemyBaseSpeed - 0.01f);

            Check("6c 圈内友方零影响（既不掉血也不被减速）",
                !allyBuff.IsSlowed && Mathf.Approximately(allyHealth.CurrentHealth, allyHealthBefore));

            // 等范围场结束（4 秒）+ 最后一次减速残留（0.6 秒）
            yield return new WaitForSeconds(4.8f);

            AreaEffectZone[] zones = FindObjectsByType<AreaEffectZone>(FindObjectsSortMode.None);
            Check("6d 范围场到期后已回收", zones.Length == 0);
        }

        /// <summary>
        /// 轮询等待某个槽位的冷却结束。用轮询而不是固定等待：
        /// 冷却时长由配置决定，硬编码等待时间会在改配置后失效。
        /// </summary>
        private IEnumerator WaitForCooldown(SkillSlot slot)
        {
            float deadline = Time.time + 10f;
            while (casterSkills.GetCooldownRemaining(slot) > 0f && Time.time < deadline)
            {
                yield return null;
            }
        }

        /// <summary>输出测试结论。</summary>
        private void ReportSummary()
        {
            bool allPassed = failCount == 0;

            Debug.Log(
                $"[阶段六测试结论] {(allPassed ? "全部通过" : "存在失败项")}\n" +
                $"  · 通过断言：{passCount}\n" +
                $"  · 失败断言：{failCount}\n" +
                $"  · OnCastStarted 事件总数：{castStartedCount}\n" +
                $"  · OnSpellReleased 事件总数：{spellReleasedCount}\n" +
                $"  · 敌方最终生命：{enemyHealth.CurrentHealth:F0} / {enemyHealth.MaxHealth:F0}\n" +
                $"  · 敌方最终移速：{enemyMovement.Speed:F2}", this);

            if (destroyUnitsAfterTest)
            {
                Cleanup();
            }
        }

        /// <summary>
        /// 释放本次测试动态创建的对象与配置资产。
        /// 内存中 CreateInstance 出来的 ScriptableObject 不销毁会一直留到退出播放模式。
        /// </summary>
        private void Cleanup()
        {
            if (caster != null)
            {
                Destroy(caster.gameObject);
            }

            if (enemy != null)
            {
                Destroy(enemy.gameObject);
            }

            if (ally != null)
            {
                Destroy(ally.gameObject);
            }

            if (doomedEnemy != null)
            {
                Destroy(doomedEnemy.gameObject);
            }

            Destroy(casterStats);
            Destroy(enemyStats);
            Destroy(allyStats);
            Destroy(doomedStats);
            Destroy(casterAttack);
            Destroy(dummyAttack);
            Destroy(fireballSkill);
            Destroy(slowZoneSkill);

            // 最后销毁承载测试自身的物体（若本组件挂在用户自己的场景物体上，请把 destroyUnitsAfterTest 保持关闭）。
            Destroy(gameObject);
        }

        #endregion
    }
}
