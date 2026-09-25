using System.Collections;
using System.Reflection;
using UnityEngine;
using MOBA.Components;
using MOBA.Core;
using MOBA.Data;

namespace MOBA.Tests
{
    /// <summary>
    /// 阶段二自动化测试：不依赖任何场景、预制体或 Inspector 配置，纯代码在内存里搭出两个单位并跑一次完整交战。
    ///
    /// 覆盖的验收点：
    /// 1. 射程外攻击被拒绝（攻击距离校验）；
    /// 2. 进入射程后按攻击间隔连续命中，命中次数 = 目标血量 / 单次伤害；
    /// 3. 每次受击都广播一次血量变化（含初始化时的一次）；
    /// 4. 目标死亡时死亡事件【只触发一次】；
    /// 5. 死亡后再次攻击不生效、血量不再变化（鞭尸检测）。
    ///
    /// 运行方式（两种任选其一）：
    /// A. 免场景：把本文件的 enableAutoBootstrap 改回 true，进入播放模式即自动运行（见 AutoBootstrap）。
    /// B. 手动（推荐）：把本组件挂到场景任意空物体上，保持 autoRunOnStart = true 后播放。
    ///
    /// 【默认已关闭自动运行】阶段二验收完成后，enableAutoBootstrap 已置为 false，
    /// 以免每次进播放模式都生成测试单位、干扰后续阶段的正常调试。
    /// </summary>
    [DisallowMultipleComponent]
    public class Stage2AutoTester : MonoBehaviour
    {
        [Header("被测参数（可自行调整，断言会跟着自动换算）")]
        [Tooltip("英雄单次攻击伤害。")]
        [SerializeField] private float heroAttackDamage = 10f;

        [Tooltip("英雄攻击距离。")]
        [SerializeField] private float heroAttackRange = 2.5f;

        [Tooltip("英雄攻击间隔（秒）。")]
        [SerializeField] private float heroAttackInterval = 0.5f;

        [Tooltip("敌方目标最大生命值。")]
        [SerializeField] private float enemyMaxHealth = 30f;

        [Tooltip("阶段一的测试距离：必须大于攻击距离，用于验证射程外攻击被拒绝。")]
        [SerializeField] private float outOfRangeDistance = 10f;

        [Tooltip("阶段二的测试距离：必须小于攻击距离，用于验证正常交战。")]
        [SerializeField] private float inRangeDistance = 1.5f;

        [Tooltip("整个交战阶段的超时保护（秒），防止逻辑异常时协程永久挂起。")]
        [SerializeField] private float timeoutSeconds = 15f;

        [Header("运行开关")]
        [Tooltip("Start 时自动开始测试。")]
        [SerializeField] private bool autoRunOnStart = true;

        [Tooltip("测试结束后销毁动态创建的单位与配置资产。默认关闭，方便在 Hierarchy / Inspector 里检查结果。")]
        [SerializeField] private bool destroyUnitsAfterTest = false;

        /// <summary>
        /// 免场景自动引导开关（静态字段，无法在 Inspector 显示）。
        ///
        /// 【已置为 false】阶段二验收已完成，阶段三起改用 Debug/FSMDebugView 观察 AI。
        /// 若继续开启，每次进入播放模式都会自动生成 TestHero / TestEnemy 两个单位并跑一遍阶段二流程，
        /// 既会往 Console 灌大量测试日志，也会干扰小兵 AI 的正常调试（例如被误当成敌对目标）。
        ///
        /// 需要重跑阶段二回归时的两种做法（任选其一）：
        ///   A. 临时把本字段改回 true；
        ///   B. 保持 false，把一个空物体挂上 Stage2AutoTester 组件（保持 autoRunOnStart = true）后播放。
        /// </summary>
        private static bool enableAutoBootstrap = false;

        /// <summary>防止同一次播放会话里重复引导（静态标记会在域重载时复位）。</summary>
        private static bool hasBootstrapped;

        private EntityBase hero;
        private EntityBase enemy;
        private HealthComponent enemyHealth;
        private CombatComponent heroCombat;

        // 动态创建的配置资产，测试结束后按需释放
        private AttackData heroAttackData;
        private AttackData enemyAttackData;
        private EntityStatsData heroStatsData;
        private EntityStatsData enemyStatsData;

        // ---- 统计与断言用数据 ----
        private int healthChangedCount;          // 血量变化事件总次数
        private int healthChangedCountAtDeath;   // 死亡时刻的血量变化事件次数
        private int deathEventCount;             // 死亡事件触发次数
        private int attackSuccessCount;          // 实际命中次数

        /// <summary>
        /// 免场景引导：播放模式加载完场景后自动创建一个承载测试的物体。
        /// 用静态标记而不是 FindObjectOfType 判重，是为了兼容不同 Unity 版本（FindObjectOfType 在新版本已标记过时）。
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoBootstrap()
        {
            if (!enableAutoBootstrap || hasBootstrapped)
            {
                return;
            }

            hasBootstrapped = true;

            GameObject testerObject = new GameObject("[Stage2AutoTester]");
            testerObject.AddComponent<Stage2AutoTester>();
        }

        private void Start()
        {
            if (!autoRunOnStart)
            {
                Debug.Log("[Stage2AutoTester] autoRunOnStart 已关闭，跳过测试。", this);
                return;
            }

            if (!BuildTestUnits())
            {
                Debug.LogError("[Stage2AutoTester] 测试环境搭建失败，测试中止。", this);
                return;
            }

            StartCoroutine(RunTestRoutine());
        }

        #region 测试环境搭建

        /// <summary>
        /// 纯代码搭建测试环境：造配置资产 → 造两个单位 → 订阅事件 → 注入配置。
        /// </summary>
        /// <returns>搭建成功返回 true。</returns>
        private bool BuildTestUnits()
        {
            // ---- 1. 造配置资产（CreateInstance 生成的是内存中的资产，不会写入磁盘）----
            heroAttackData = CreateAttackData(heroAttackDamage, heroAttackRange, heroAttackInterval);

            // 敌方单位不需要攻击能力，但仍给它一份攻击配置：
            // 一是保持数据完整，二是避免 EntityBase 因缺少配置而输出告警，让 Console 保持干净。
            enemyAttackData = CreateAttackData(1f, 1f, 1f);

            heroStatsData = CreateStatsData(100f, 3.5f, 8f, heroAttackData);
            enemyStatsData = CreateStatsData(enemyMaxHealth, 0f, 8f, enemyAttackData);

            // ---- 2. 造两个单位 ----
            hero = CreateUnit("TestHero", new Vector3(0f, 0f, 0f));
            enemy = CreateUnit("TestEnemy", new Vector3(outOfRangeDistance, 0f, 0f));

            if (hero == null || enemy == null)
            {
                return false;
            }

            // 注意：这里【不能】用 hero.Combat / enemy.Health 读取组件。
            // EntityBase 的组件引用是在它自己的 Awake 里缓存的，而 AddComponent<EntityBase>() 会立刻触发 Awake，
            // 那一刻 HealthComponent / CombatComponent 还没挂上，缓存到的是 null；
            // 只有 Initialize() 内部再调一次 CacheComponents() 才会补全。
            // 但下面必须"先订阅事件、再 Initialize"（否则会漏掉初始化时广播的那次血量），
            // 也就是说读取组件的时刻一定早于 Initialize，因此这里直接从物体上取组件，绕开尚未补全的缓存。
            heroCombat = hero.GetComponent<CombatComponent>();
            enemyHealth = enemy.GetComponent<HealthComponent>();

            if (heroCombat == null || enemyHealth == null)
            {
                Debug.LogError("[Stage2AutoTester] 单位缺少 CombatComponent 或 HealthComponent，测试无法继续。", this);
                return false;
            }

            // ---- 3. 先订阅事件，再注入配置 ----
            // 顺序很重要：Initialize 会广播一次"初始血量"，订阅放后面就会漏掉这次事件，
            // 导致血量事件计数比预期少 1，断言会误判为失败。
            enemyHealth.OnHealthChanged += HandleEnemyHealthChanged;
            enemyHealth.OnDied += HandleEnemyDied;

            // ---- 4. 注入配置 ----
            // 用 EntityBase.Initialize 而非直接操作组件，目的是顺便验证"配置 → 组件"这条注入链是否正常。
            // 这里两个单位都用 EntityType.Tower：本测试只覆盖战斗与生命逻辑，不需要移动能力，
            // 而 Tower/Base 按设计就没有移动组件，可以避免 EntityBase 报"英雄/小兵必须有 MovementComponent"。
            hero.Initialize(heroStatsData, TeamType.Player, EntityType.Tower);
            enemy.Initialize(enemyStatsData, TeamType.Enemy, EntityType.Tower);

            return true;
        }

        /// <summary>
        /// 用代码创建一个单位：挂 EntityBase + HealthComponent + CombatComponent。
        /// 注意不要在这里调用 Initialize——调用方需要先订阅事件再注入配置。
        /// </summary>
        private EntityBase CreateUnit(string unitName, Vector3 position)
        {
            GameObject unitObject = new GameObject(unitName);
            unitObject.transform.position = position;

            // 先挂 EntityBase 再挂其他组件：EntityBase 的 Initialize 会重新缓存组件引用，
            // 因此顺序不是硬性要求，但"身份组件在最前"更符合直觉，也便于排查问题。
            EntityBase entity = unitObject.AddComponent<EntityBase>();
            unitObject.AddComponent<HealthComponent>();
            unitObject.AddComponent<CombatComponent>();

            return entity;
        }

        /// <summary>
        /// 造一份攻击配置。
        /// 字段是 private 的，只能用反射写入——这是刻意的：生产类不提供公开 setter，
        /// 是为了守住"ScriptableObject 是只读模板、运行时状态不得写回"的约定，
        /// 测试代码不应该为了自己好写就污染生产类的封装。
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
        private EntityStatsData CreateStatsData(float maxHealth, float moveSpeed, float detectionRange, AttackData attack)
        {
            EntityStatsData data = ScriptableObject.CreateInstance<EntityStatsData>();
            SetPrivateField(data, "maxHealth", maxHealth);
            SetPrivateField(data, "moveSpeed", moveSpeed);
            SetPrivateField(data, "detectionRange", detectionRange);
            SetPrivateField(data, "attack", attack);
            return data;
        }

        /// <summary>
        /// 反射写入私有序列化字段。
        /// 字段被改名时这里会立刻报错（而不是静默失效），属于可接受的测试耦合。
        /// </summary>
        private static void SetPrivateField(object target, string fieldName, object value)
        {
            if (target == null)
            {
                Debug.LogError($"[Stage2AutoTester] SetPrivateField 目标为空，字段 {fieldName} 未写入。");
                return;
            }

            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null)
            {
                Debug.LogError(
                    $"[Stage2AutoTester] 在 {target.GetType().Name} 上找不到私有字段 \"{fieldName}\"（可能已被改名），该配置未生效。");
                return;
            }

            field.SetValue(target, value);
        }

        #endregion

        #region 事件监听

        /// <summary>
        /// 监听敌方血量变化，把每次受击后的"当前血量"打印出来。
        /// </summary>
        private void HandleEnemyHealthChanged(float currentHealth, float maxHealth)
        {
            healthChangedCount++;
            Debug.Log($"[监听 · 血量变化 #{healthChangedCount}] {enemy.name} 当前血量 {currentHealth:F1} / {maxHealth:F1}");
        }

        /// <summary>
        /// 监听死亡事件。死亡事件只应触发一次，因此这里同时记录触发次数用于断言。
        /// </summary>
        private void HandleEnemyDied()
        {
            deathEventCount++;
            healthChangedCountAtDeath = healthChangedCount;
            Debug.Log($"[监听 · 死亡宣告] {enemy.name} 已死亡（死亡事件第 {deathEventCount} 次触发，期望仅 1 次）");
        }

        #endregion

        #region 测试主流程

        private IEnumerator RunTestRoutine()
        {
            Debug.Log("========== 阶段二自动测试开始（Stage2AutoTester）==========");

            // 等一帧，让新建对象的生命周期回调走完，确保读到的是初始化之后的状态。
            yield return null;

            Debug.Log(
                $"[初始状态] {hero.name}: 阵营={hero.Team}, 生命={hero.Health.CurrentHealth:F1}/{hero.Health.MaxHealth:F1}, " +
                $"攻击力={heroAttackData.Damage:F1}, 射程={heroCombat.AttackRange:F2}, 间隔={heroAttackData.AttackInterval:F2}s | " +
                $"{enemy.name}: 阵营={enemy.Team}, 生命={enemyHealth.CurrentHealth:F1}/{enemyHealth.MaxHealth:F1}");

            // ---------- 阶段 1：射程外攻击必须被拒绝 ----------
            enemy.transform.position = new Vector3(outOfRangeDistance, 0f, 0f);
            bool outOfRangeAttack = heroCombat.TryAttack(enemy);
            Debug.Log(
                $"[阶段1 · 射程校验] 距离 {outOfRangeDistance:F2} > 射程 {heroCombat.AttackRange:F2}，" +
                $"TryAttack 返回 {outOfRangeAttack}（期望 False）{(outOfRangeAttack ? " → ❌ 失败" : " → ✅ 通过")}");

            // ---------- 阶段 2：进入射程，按攻击间隔循环攻击 ----------
            enemy.transform.position = new Vector3(inRangeDistance, 0f, 0f);
            Debug.Log($"[阶段2 · 交战] 进入射程（距离 {inRangeDistance:F2}），开始按 {heroAttackData.AttackInterval:F2}s 间隔攻击……");

            float deadline = Time.time + timeoutSeconds;
            while (!enemyHealth.IsDead && Time.time < deadline)
            {
                if (heroCombat.TryAttack(enemy))
                {
                    attackSuccessCount++;
                }

                // 用攻击间隔作为节奏：这正是 FSM AttackState 未来要做的事，这里先手动模拟。
                yield return new WaitForSeconds(heroAttackData.AttackInterval);
            }

            if (!enemyHealth.IsDead)
            {
                Debug.LogError($"[阶段2 · 交战] 超时：{timeoutSeconds:F1}s 内未能击杀目标，测试中止。", this);
                yield break;
            }

            // ---------- 阶段 3：死亡后不得再造成伤害（鞭尸检测） ----------
            bool attackAfterDeath = heroCombat.TryAttack(enemy);
            bool healthStayedZero = Mathf.Approximately(enemyHealth.CurrentHealth, 0f);
            bool noMoreHealthEvents = healthChangedCount == healthChangedCountAtDeath;
            bool corpseCheckPassed = !attackAfterDeath && healthStayedZero && noMoreHealthEvents;

            Debug.Log(
                $"[阶段3 · 鞭尸检测] 死亡后再次 TryAttack 返回 {attackAfterDeath}（期望 False）；" +
                $"血量仍为 {enemyHealth.CurrentHealth:F1}（期望 0）；死亡后新增血量事件 {healthChangedCount - healthChangedCountAtDeath} 次（期望 0）" +
                $"{(corpseCheckPassed ? " → ✅ 通过" : " → ❌ 失败")}");

            // ---------- 阶段 4：汇总断言 ----------
            // 命中次数 = 目标血量 / 单次伤害（向上取整）；血量事件 = 命中次数 + 初始化时的一次广播。
            int expectedHits = Mathf.CeilToInt(enemyMaxHealth / Mathf.Max(0.0001f, heroAttackDamage));
            int expectedHealthEvents = expectedHits + 1;

            bool pass = !outOfRangeAttack
                        && attackSuccessCount == expectedHits
                        && deathEventCount == 1
                        && healthStayedZero
                        && corpseCheckPassed
                        && healthChangedCount == expectedHealthEvents;

            Debug.Log(
                $"[测试结论] {(pass ? "✅ 全部通过" : "❌ 存在失败项")}\n" +
                $"  · 射程外攻击被拒绝：{(!outOfRangeAttack ? "是 ✅" : "否 ❌")}\n" +
                $"  · 实际命中次数：{attackSuccessCount}（期望 {expectedHits}）\n" +
                $"  · 死亡事件触发次数：{deathEventCount}（期望 1）\n" +
                $"  · 最终血量：{enemyHealth.CurrentHealth:F1}（期望 0）\n" +
                $"  · 死亡后攻击无效：{(!attackAfterDeath ? "是 ✅" : "否 ❌")}\n" +
                $"  · 死亡后无新增血量事件：{(noMoreHealthEvents ? "是 ✅" : "否 ❌")}\n" +
                $"  · 血量事件总数：{healthChangedCount}（期望 {expectedHealthEvents}）",
                this);

            if (destroyUnitsAfterTest)
            {
                Cleanup();
            }
        }

        /// <summary>
        /// 释放本次测试动态创建的对象与配置资产。
        /// 内存中 CreateInstance 出来的 ScriptableObject 不销毁会一直留到退出播放模式，
        /// 多次运行测试时容易造成"资产越来越多"的错觉。
        /// </summary>
        private void Cleanup()
        {
            if (hero != null)
            {
                Destroy(hero.gameObject);
            }

            if (enemy != null)
            {
                Destroy(enemy.gameObject);
            }

            Destroy(heroStatsData);
            Destroy(enemyStatsData);
            Destroy(heroAttackData);
            Destroy(enemyAttackData);

            // 最后销毁承载测试自身的物体（若本组件挂在用户自己的场景物体上，请把 destroyUnitsAfterTest 保持关闭）。
            Destroy(gameObject);
        }

        #endregion
    }
}
