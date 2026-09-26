using System;
using System.Collections.Generic;
using UnityEngine;
using MOBA.Components;
using MOBA.Core;
using MOBA.Skills;

namespace MOBA.VFX
{
    /// <summary>
    /// 特效生成器（表现层，阶段九）：订阅逻辑层的战斗 / 技能事件，从对象池取特效预制体播放。
    ///
    /// ==================== 它订阅什么 ====================
    /// <code>
    ///  逻辑事件（只读订阅）                     通道            摆放位置
    ///  ──────────────────────────────────────────────────────────────────────
    ///  HealthComponent.OnDamaged            ─▶ 受击特效    ─▶ 受击者身上（固定）
    ///  SkillComponent.OnSpellReleased       ─▶ 施法特效    ─▶ 施法者身上（可跟随）
    ///  Projectile.OnHit（经生成器转发）      ─▶ 命中特效    ─▶ 弹道命中点（固定）
    /// </code>
    ///
    /// 【为什么施法订阅 OnSpellReleased 而不是 OnCastStarted】
    /// 这一条与 <c>AnimationComponent</c>（订阅 OnCastStarted 播施法**动作**）刻意相反，原因是两类表现的
    /// "正确时刻"本来就不同：动作要在前摇开始时就摆出来，而特效要在效果真正落地时才炸开。
    /// 而 OnSpellReleased 只在**真的生成了弹道 / 范围场**时才广播，因此"没打出去却播了特效"结构上不可能。
    ///
    /// 【为什么弹道命中要经 ProjectileSpawner 转发】
    /// 弹道是运行期动态创建的短命对象，既不在 EntityRegistry 里也无法被枚举。
    /// <c>ProjectileSpawner.OnProjectileSpawned</c> 是唯一能拿到"它诞生了"的接入点；
    /// 拿到之后挂一发 <c>OnHit</c>，就能在**真实命中点**播放命中特效 ——
    /// 这正是阶段九验收项「弹道命中位置与受击特效位置一致」所要求的。
    /// 订阅方无需退订：弹道命中 / 超时后自行 Destroy，委托链随对象一起消失。
    ///
    /// ==================== 严格遵守「表现与逻辑分离」铁律 ====================
    ///   · 只订阅事件、只读公开属性（transform / Team），不调用任何会改变伤害或状态的方法；
    ///   · 不持有任何战斗数值，不参与目标合法性判断；
    ///   · 池耗尽时**不 Instantiate**、不重试、不报错，只是"这一发没有画面"（README §3.4 约束 4）；
    ///   · 关掉本组件（或删掉 VfxSpawnerRoot）后对局结果与胜负完全一致。
    ///
    /// ==================== 与白盒占位特效的关系 ====================
    /// 阶段八的 <c>WhiteboxVfxManager</c>（VfxRoot）是"没有美术资源时也能看清技能"的权宜实现。
    /// 本组件是它的**正式替代**：三个特效槽位一旦由工具 / 美术填上预制体就会真的开始工作。
    /// 两者同时启用会让同一次施法播两套特效，因此美术资源接入后应关掉 VfxRoot。
    /// 在三个槽位都为空时本组件**完全惰性**（不建池、不生成任何对象），因此现在与白盒管理器并存是安全的。
    /// </summary>
    [DisallowMultipleComponent]
    public class VfxSpawner : MonoBehaviour
    {
        [Header("总开关")]
        [Tooltip("关掉它等于「整块关闭特效」——对局结果完全不变（README §3.4 约束 4 的判据）。")]
        [SerializeField] private bool enableVfx = true;

        [Header("特效预制体（留空 = 该通道不播放，不影响任何对局逻辑）")]
        [Tooltip("受击特效：订阅 HealthComponent.OnDamaged，播在受击者身上。留空则不播。")]
        [SerializeField] private GameObject hitVfxPrefab;

        [Tooltip("施法特效：订阅 SkillComponent.OnSpellReleased（效果真正落地的那一刻）。留空则不播。")]
        [SerializeField] private GameObject castVfxPrefab;

        [Tooltip("弹道命中特效：订阅 Projectile.OnHit，播在弹道命中点。留空则不播。")]
        [SerializeField] private GameObject projectileHitVfxPrefab;

        [Tooltip("护盾特效（阶段九新增）：施法者的技能带护盾效果时，播在施法者身上并跟随。留空则不播。\n" +
                 "判据是 SkillData 的语义字段（EffectType / SecondaryEffectType == Shield），不是槽位 —— " +
                 "9 个 AI 英雄的 4 个技能各不相同，按 Q/W/E/R 写死会整体错位。")]
        [SerializeField] private GameObject shieldVfxPrefab;

        [Tooltip("范围场特效（阶段九新增）：技能生成范围场时，播在真实落点的地面上。留空则不播。\n" +
                 "判据是 SpawnZoneAtSelf（以自身为中心的持续 AOE）或 CastType == GroundPoint（地面 AOE）。")]
        [SerializeField] private GameObject zoneVfxPrefab;

        [Header("对象池")]
        [Tooltip("每个特效预制体预生成的实例数（即该通道的容量上限）。\n" +
                 "池耗尽时只是少播一个特效，绝不即时 Instantiate —— 请按战场同屏最大并发量给足。\n" +
                 "【为什么默认给到 40】实机实测：5v5 团战里 21+ 单位互殴时，受击通道（每次挨打都播一次）\n" +
                 "在 16 个容量下会被打空并静默丢特效。40 是「一场团战同时挨打的单位数 × 余量」的量级。")]
        [Min(0)]
        [SerializeField] private int prewarmPerPrefab = 40;

        [Header("摆放")]
        [Tooltip("受击特效相对受击者原点的偏移（米）。抬到身体中部，避免特效半个身子埋在地里。")]
        [SerializeField] private Vector3 hitOffset = new Vector3(0f, 1f, 0f);

        [Tooltip("施法特效相对施法者原点的偏移（米）。")]
        [SerializeField] private Vector3 castOffset = new Vector3(0f, 1f, 0f);

        [Tooltip("施法特效是否跟随施法者移动（例如持续施法的光环）。关掉则固定在施法瞬间的位置。")]
        [SerializeField] private bool followCasterForCastVfx = true;

        [Tooltip("范围场特效相对地面的抬高（米）。略微抬离地面，避免与地面 Z-Fighting 被吃掉。")]
        [SerializeField] private float zoneOffset = 0.12f;

        [Header("播放时长")]
        [Tooltip("受击特效的播放时长（秒）。0 = 按粒子系统的实际时长自动推算（推荐）。")]
        [Min(0f)]
        [SerializeField] private float hitVfxLifetime = 0f;

        [Tooltip("施法特效的播放时长（秒）。0 = 按粒子系统的实际时长自动推算。\n" +
                 "【循环特效必须显式填值】循环系统的 duration 只表示一个循环的长度，自动推算会明显偏短。")]
        [Min(0f)]
        [SerializeField] private float castVfxLifetime = 0f;

        [Tooltip("弹道命中特效的播放时长（秒）。0 = 按粒子系统的实际时长自动推算。")]
        [Min(0f)]
        [SerializeField] private float projectileHitVfxLifetime = 0f;

        [Tooltip("护盾特效的播放时长（秒）。0 = 按粒子系统的实际时长自动推算。\n" +
                 "【循环特效必须显式填值】护盾光环通常是循环的，自动推算会明显偏短。")]
        [Min(0f)]
        [SerializeField] private float shieldVfxLifetime = 0f;

        [Tooltip("范围场特效的播放时长（秒）。0 = 按粒子系统的实际时长自动推算。")]
        [Min(0f)]
        [SerializeField] private float zoneVfxLifetime = 0f;

        [Header("调试")]
        [Tooltip("在 Console 输出每次特效生成记录（排查「按了没特效」时打开）。")]
        [SerializeField] private bool logVfxSpawns = false;

        [Tooltip("在 Console 输出订阅 / 退订记录（排查「某些单位没有特效」时打开）。")]
        [SerializeField] private bool logSubscriptions = false;

        /// <summary>
        /// 一个实体的全部订阅记录。
        /// 之所以要保存委托本身：C# 事件用 <c>-=</c> 退订时必须传入"同一个委托实例"，
        /// 而 lambda 每次求值都会生成新的实例 —— 不保存就永远退订不掉（表现为场景重载后旧生成器仍在响应）。
        /// </summary>
        private sealed class EntitySubscription
        {
            public EntityBase Entity;
            public HealthComponent Health;
            public SkillComponent Skills;
            public ProjectileSpawner Spawner;

            /// <summary>受击处理器。事件带 sender，因此所有实体可共用同一个方法（无闭包分配）。</summary>
            public Action<HealthComponent, float, float, EntityBase> DamageHandler;

            /// <summary>施法处理器。事件不带 sender，因此必须按实体各绑一个闭包。</summary>
            public Action<SkillSlot, SkillData> SpellHandler;

            /// <summary>弹道生成处理器。事件带 Projectile（其 Source 即发射者），可共用同一个方法。</summary>
            public Action<Projectile> ProjectileHandler;
        }

        /// <summary>当前已订阅的实体清单。</summary>
        private readonly List<EntitySubscription> subscriptions = new List<EntitySubscription>();

        /// <summary>特效预制体 → 对象池。键是预制体资产本身，因此同一种特效全局只有一个池。</summary>
        private readonly Dictionary<GameObject, SimpleObjectPool> pools =
            new Dictionary<GameObject, SimpleObjectPool>();

        /// <summary>是否已建池。建池会 Instantiate 实例，必须只发生一次（重复建池 = 实例泄漏）。</summary>
        private bool hasBuiltPools;

        /// <summary>是否已就"三个槽位全空"提示过一次。</summary>
        private bool hasReportedInactive;

        /// <summary>是否已就"取不到池"告警过一次。</summary>
        private bool hasReportedMissingPool;

        /// <summary>当前活跃特效总数（只读），供调试视图与 Profiler 对照使用。</summary>
        public int ActiveVfxCount
        {
            get
            {
                int count = 0;

                foreach (SimpleObjectPool pool in pools.Values)
                {
                    if (pool != null)
                    {
                        count += pool.ActiveCount;
                    }
                }

                return count;
            }
        }

        /// <summary>已订阅的实体数量（只读），供调试视图使用。</summary>
        public int SubscriptionCount => subscriptions.Count;

        /// <summary>已建立的对象池数量（只读）。三个槽位都为空时为 0。</summary>
        public int PoolCount => pools.Count;

        private void OnEnable()
        {
            BuildPoolsOnce();

            // 【顺序不可颠倒】先订阅事件、再读 Snapshot（与 WorldHealthBarManager / WhiteboxVfxManager 同一理由）：
            // 反过来的话，在两次调用之间登记的单位既不在快照里、也没收到事件，会被彻底漏掉。
            EntityRegistry.OnEntityRegistered += HandleEntityRegistered;
            EntityRegistry.OnEntityUnregistered += HandleEntityUnregistered;

            EntityBase[] snapshot = EntityRegistry.Snapshot();

            for (int i = 0; i < snapshot.Length; i++)
            {
                AttachTo(snapshot[i]);
            }
        }

        private void OnDisable()
        {
            EntityRegistry.OnEntityRegistered -= HandleEntityRegistered;
            EntityRegistry.OnEntityUnregistered -= HandleEntityUnregistered;

            for (int i = subscriptions.Count - 1; i >= 0; i--)
            {
                DetachAt(i);
            }

            // 归还全部活跃特效：否则禁用后战场上会留下"不归任何人管"的粒子（它们还在各自的倒计时里）。
            foreach (SimpleObjectPool pool in pools.Values)
            {
                if (pool != null)
                {
                    pool.ReleaseAll();
                }
            }
        }

        /// <summary>
        /// 立刻归还全部活跃特效（公开入口）。用于对局结束冻结战场这类需要"清场"的时刻。
        /// </summary>
        public void ClearAllVfx()
        {
            foreach (SimpleObjectPool pool in pools.Values)
            {
                if (pool != null)
                {
                    pool.ReleaseAll();
                }
            }
        }

        #region 对象池

        /// <summary>
        /// 建池（只执行一次）。
        /// 【为什么必须在 OnEnable 一次性建好】池的构造会 Instantiate 实例；
        /// 若改成"第一次生成特效时才建池"，那一次 Instantiate 就发生在战斗中途 ——
        /// 正是阶段九性能验收要消灭的分配抖动。因此这里预先把三个通道的池全部建好，
        /// 战斗期间只剩 Get / Release（零分配）。
        /// </summary>
        private void BuildPoolsOnce()
        {
            if (hasBuiltPools)
            {
                return;
            }

            hasBuiltPools = true;

            EnsurePool(hitVfxPrefab);
            EnsurePool(castVfxPrefab);
            EnsurePool(projectileHitVfxPrefab);
            EnsurePool(shieldVfxPrefab);
            EnsurePool(zoneVfxPrefab);

            if (pools.Count == 0 && !hasReportedInactive)
            {
                hasReportedInactive = true;

                Debug.Log(
                    "[VfxSpawner] 五个特效槽位都为空，正式特效层处于惰性状态（不建池、不生成任何对象）。" +
                    "这不影响任何对局逻辑。\n" +
                    "  · 接入方式：把特效预制体放到 Assets/Prefabs/VFX/ 下并执行「MOBA Demo/一键组装测试战场」，" +
                    "工具会自动注入；或直接在 Inspector 上把预制体拖进三个槽位。\n" +
                    "  · 接入后请关掉 VfxRoot（白盒占位），避免同一次施法播两套特效。", this);
            }
        }

        /// <summary>为一个非空预制体建池（幂等：同一预制体只建一次）。</summary>
        /// <param name="prefab">特效预制体，允许为 null（该通道不建池）。</param>
        private void EnsurePool(GameObject prefab)
        {
            if (prefab == null || pools.ContainsKey(prefab))
            {
                return;
            }

            pools[prefab] = new SimpleObjectPool(prefab, transform, prewarmPerPrefab);
        }

        /// <summary>
        /// 取某个预制体对应的池。
        /// 池在 <see cref="BuildPoolsOnce"/> 里已经建好，因此正常流程下这里必然命中；
        /// 命中不了只有一种可能：运行期有人替换了字段里的预制体引用（工具不会这么做）。
        /// </summary>
        /// <param name="prefab">特效预制体。</param>
        /// <returns>对象池；未建池时返回 null 并告警一次。</returns>
        private SimpleObjectPool ResolvePool(GameObject prefab)
        {
            if (prefab != null && pools.TryGetValue(prefab, out SimpleObjectPool pool) && pool != null)
            {
                return pool;
            }

            if (!hasReportedMissingPool)
            {
                hasReportedMissingPool = true;

                Debug.LogWarning(
                    "[VfxSpawner] 找不到特效预制体对应的对象池（该预制体可能是在运行期才被赋值的）。" +
                    "本次特效已跳过 —— 这不会影响任何对局逻辑。若需要它生效，请在编辑期就把预制体配置好。", this);
            }

            return null;
        }

        #endregion

        #region 订阅管理

        private void HandleEntityRegistered(EntityBase entity)
        {
            AttachTo(entity);
        }

        private void HandleEntityUnregistered(EntityBase entity)
        {
            for (int i = subscriptions.Count - 1; i >= 0; i--)
            {
                if (subscriptions[i].Entity == entity)
                {
                    DetachAt(i);
                }
            }
        }

        /// <summary>
        /// 为一个实体挂上它拥有的全部特效通道。
        /// 三条通道各自独立：小兵没有 SkillComponent / ProjectileSpawner 属正常配置，
        /// 它仍然要能播受击特效，因此这里逐条判空、不做"要么全有要么全无"的整体判断。
        /// </summary>
        /// <param name="entity">目标实体。</param>
        private void AttachTo(EntityBase entity)
        {
            if (entity == null)
            {
                return;
            }

            // 用 GetComponent 而不是读 entity.Health / entity.Skills 属性：动态创建的单位在 Awake 时
            // 组件可能还没挂齐，属性里缓存的引用会是 null（与 WhiteboxVfxManager 取组件同一口径）。
            HealthComponent health = entity.GetComponent<HealthComponent>();
            SkillComponent skills = entity.GetComponent<SkillComponent>();
            ProjectileSpawner spawner = entity.GetComponent<ProjectileSpawner>();

            if (health == null && skills == null && spawner == null)
            {
                // 三条通道都没有（纯装饰物 / 建筑缺组件）：不必占一条订阅记录。
                return;
            }

            for (int i = 0; i < subscriptions.Count; i++)
            {
                if (subscriptions[i].Entity == entity)
                {
                    // 已订阅（快照与事件可能都命中同一实体）：去重，否则同一次受击会生成两套特效。
                    return;
                }
            }

            EntitySubscription subscription = new EntitySubscription
            {
                Entity = entity,
                Health = health,
                Skills = skills,
                Spawner = spawner
            };

            if (health != null)
            {
                subscription.DamageHandler = HandleDamaged;
                health.OnDamaged += subscription.DamageHandler;
            }

            if (skills != null)
            {
                // 事件签名里没有发送者，因此必须为每个实体各绑一个闭包。
                // 这一次分配发生在"单位登记时"（一局十几次），不在战斗路径上，可以接受。
                subscription.SpellHandler = (slot, data) => HandleSpellReleased(entity, slot, data);
                skills.OnSpellReleased += subscription.SpellHandler;
            }

            if (spawner != null)
            {
                subscription.ProjectileHandler = HandleProjectileSpawned;
                spawner.OnProjectileSpawned += subscription.ProjectileHandler;
            }

            subscriptions.Add(subscription);

            if (logSubscriptions)
            {
                Debug.Log(
                    $"[VfxSpawner] 已订阅 {entity.name}（受击 {(health != null ? "有" : "无")} / " +
                    $"施法 {(skills != null ? "有" : "无")} / 弹道 {(spawner != null ? "有" : "无")}）。", this);
            }
        }

        /// <summary>退订指定下标并移除记录（调用方负责倒序遍历）。</summary>
        /// <param name="index">订阅清单下标。</param>
        private void DetachAt(int index)
        {
            EntitySubscription subscription = subscriptions[index];
            subscriptions.RemoveAt(index);

            // 组件已被销毁时，事件委托链也随之失去意义，但 -= 本身仍然安全：
            // 它操作的是托管对象的字段，不触发 Unity 的已销毁检查。
            if (subscription.Health != null && subscription.DamageHandler != null)
            {
                subscription.Health.OnDamaged -= subscription.DamageHandler;
            }

            if (subscription.Skills != null && subscription.SpellHandler != null)
            {
                subscription.Skills.OnSpellReleased -= subscription.SpellHandler;
            }

            if (subscription.Spawner != null && subscription.ProjectileHandler != null)
            {
                subscription.Spawner.OnProjectileSpawned -= subscription.ProjectileHandler;
            }

            if (logSubscriptions)
            {
                string entityName = subscription.Entity != null ? subscription.Entity.name : "已销毁实体";
                Debug.Log($"[VfxSpawner] 已退订 {entityName}。", this);
            }
        }

        #endregion

        #region 逻辑事件 → 特效

        /// <summary>
        /// 受击回调：在受击者身上播放受击特效。
        ///
        /// 【为什么忽略伤害数值】表现层不需要知道这一下打了多少 —— 数字由伤害飘字负责，
        /// 特效只需要"被打到了"这一个事实。这样受击特效对"被护盾完全吸收（扣血为 0）"的情形
        /// 同样会播放（OnDamaged 在那种情况下也会广播），观感上"挡住了"与"没打中"得以区分。
        /// </summary>
        /// <param name="sender">受击者的生命组件（事件发送者）。</param>
        /// <param name="healthDamage">本次实际扣除的生命值（未使用）。</param>
        /// <param name="shieldAbsorbed">本次被护盾吸收的伤害（未使用）。</param>
        /// <param name="source">伤害来源（未使用）。</param>
        private void HandleDamaged(
            HealthComponent sender, float healthDamage, float shieldAbsorbed, EntityBase source)
        {
            if (!enableVfx || sender == null)
            {
                return;
            }

            SpawnVfx(
                hitVfxPrefab, sender.transform, sender.transform.position + hitOffset, null, hitVfxLifetime, "受击");
        }

        /// <summary>
        /// 施法回调：在施法者身上播放施法特效（可跟随）。
        /// 事件参数（槽位 / 技能配置）刻意不参与选型：本组件按**通道**播放，
        /// 而"某个技能该长什么样"属于特效预制体自身的表现（多形态特效可由 SkillData 进一步扩展）。
        /// </summary>
        /// <param name="caster">施法者。</param>
        /// <param name="slot">技能槽位（仅日志用）。</param>
        /// <param name="data">技能配置（仅日志用）。</param>
        private void HandleSpellReleased(EntityBase caster, SkillSlot slot, SkillData data)
        {
            if (!enableVfx || caster == null || data == null)
            {
                return;
            }

            Transform follow = followCasterForCastVfx ? caster.transform : null;

            // ① 施法通道：任何技能释放都在施法者身上播一次。
            SpawnVfx(
                castVfxPrefab, caster.transform, caster.transform.position + castOffset, follow, castVfxLifetime, "施法");

            // ② 护盾通道（阶段九新增）：技能带护盾效果时，在施法者身上播一个跟随的光环。
            //    判据取自 SkillData 的**语义字段**而不是 Q/W/E/R 槽位 —— 9 个 AI 英雄的 4 个技能各不相同，
            //    按槽位写死会让特效整体错位（这是阶段八白盒特效踩过并写进 README 的教训）。
            if (HasShieldEffect(data))
            {
                SpawnVfx(
                    shieldVfxPrefab, caster.transform, caster.transform.position + castOffset,
                    caster.transform, shieldVfxLifetime, "护盾");
            }

            // ③ 范围场通道（阶段九新增）：播在**真实落点**的地面上。
            //    落点从 SkillComponent.LastCastGroundPoint 读（事件参数里没有它，而逻辑层才知道鼠标点在哪）。
            if (data.CastType == SkillCastType.Self && data.SpawnZoneAtSelf)
            {
                // 以自身为中心的持续 AOE（如 E 审判）：落在脚下，followCaster 时随人移动。
                SpawnVfx(
                    zoneVfxPrefab, caster.transform, caster.transform.position + Vector3.up * zoneOffset,
                    data.FollowCaster ? caster.transform : null, zoneVfxLifetime, "范围场");
            }
            else if (data.CastType == SkillCastType.GroundPoint)
            {
                // 非指向性地面 AOE：落在输入层指定的鼠标点（不是施法者脚下）。
                Vector3 point = ResolveLastCastGroundPoint(caster);

                SpawnVfx(zoneVfxPrefab, null, point + Vector3.up * zoneOffset, null, zoneVfxLifetime, "范围场");
            }
        }

        /// <summary>技能是否带有护盾效果（主效果或附带效果之一）。</summary>
        /// <param name="data">技能配置。</param>
        /// <returns>带护盾返回 true。</returns>
        private static bool HasShieldEffect(SkillData data)
        {
            return data.EffectType == SkillEffectType.Shield
                || data.SecondaryEffectType == SkillEffectType.Shield;
        }

        /// <summary>
        /// 取最近一次施法的地面落点。读不到（没有 SkillComponent）时退化为施法者自身位置 ——
        /// 特效位置略偏总好过整个特效消失。
        /// </summary>
        /// <param name="caster">施法者。</param>
        /// <returns>落点（世界坐标）。</returns>
        private static Vector3 ResolveLastCastGroundPoint(EntityBase caster)
        {
            SkillComponent skills = caster.Skills;
            return skills != null ? skills.LastCastGroundPoint : caster.transform.position;
        }

        /// <summary>
        /// 弹道生成回调：挂上命中监听。
        /// 【为什么在这里才挂】<c>Projectile.OnHit</c> 只在弹道存在期间有效，
        /// 而弹道是"生成 → 飞行 → 命中即销毁"的短命对象，除了这个回调没有第二个接入点。
        /// </summary>
        /// <param name="projectile">刚生成的弹道。</param>
        private void HandleProjectileSpawned(Projectile projectile)
        {
            if (!enableVfx || projectile == null)
            {
                return;
            }

            projectile.OnHit += HandleProjectileHit;
        }

        /// <summary>
        /// 弹道命中回调：在真实命中点播放命中特效。
        /// 事件自带命中位置，因此特效位置与伤害结算位置天然一致
        /// （这正是"弹道命中位置与受击特效位置一致"这条验收项的落实方式）。
        /// </summary>
        /// <param name="projectile">命中的弹道。</param>
        /// <param name="target">被命中的目标。</param>
        /// <param name="hitPosition">命中位置（世界坐标）。</param>
        private void HandleProjectileHit(Projectile projectile, ITargetable target, Vector3 hitPosition)
        {
            if (!enableVfx)
            {
                return;
            }

            SpawnVfx(projectileHitVfxPrefab, null, hitPosition, null, projectileHitVfxLifetime, "弹道命中");
        }

        /// <summary>
        /// 从池中取一个实例并播放。
        ///
        /// 【摆放语义】位置一律由调用方算好并传世界坐标（受击 / 施法传"锚点 + 偏移"，命中传真实命中点）；
        /// 把"锚点 + 偏移"的加法留在调用处而不是这里，是为了让本方法只有一条职责：
        /// "取池 → 定位 → 播放"。两种摆放方式收在同一处，流程就只有一份实现。
        /// </summary>
        /// <param name="prefab">特效预制体；为 null 表示该通道未配置（直接跳过）。</param>
        /// <param name="anchor">锚点（受击者 / 施法者）；仅用于推算跟随偏移，允许为 null。</param>
        /// <param name="worldPosition">播放位置（世界坐标）。</param>
        /// <param name="follow">跟随目标；null = 固定在生成点。</param>
        /// <param name="lifetimeOverride">显式播放时长（秒），0 = 按粒子时长自动推算。</param>
        /// <param name="channelLabel">通道名（仅用于日志）。</param>
        /// <returns>确实播放了一个特效返回 true。</returns>
        private bool SpawnVfx(
            GameObject prefab,
            Transform anchor,
            Vector3 worldPosition,
            Transform follow,
            float lifetimeOverride,
            string channelLabel)
        {
            if (!enableVfx || prefab == null)
            {
                return false;
            }

            SimpleObjectPool pool = ResolvePool(prefab);

            if (pool == null)
            {
                return false;
            }

            GameObject instance = pool.Get();

            if (instance == null)
            {
                // 池耗尽：少播一个特效，不影响任何逻辑（SimpleObjectPool 已告警一次）。
                return false;
            }

            PooledVfxInstance vfx = instance.GetComponent<PooledVfxInstance>();

            if (vfx == null)
            {
                // 首次借出这个实例：补上自生命周期组件。只发生一次，之后每次都命中缓存。
                // 之所以不要求美术在预制体上预先挂好：特效预制体是外部资源，
                // 让工具 / 运行期兜住比"每次导入美术资源都记得挂组件"可靠得多。
                vfx = instance.AddComponent<PooledVfxInstance>();
            }

            instance.transform.position = worldPosition;
            instance.transform.rotation = Quaternion.identity;

            // 跟随偏移 = 目标位置与锚点的相对关系，因此跟随期间特效会稳定地贴在锚点的同一部位上。
            Vector3 followOffset = follow != null && anchor != null
                ? worldPosition - anchor.position
                : Vector3.zero;

            vfx.Play(pool, lifetimeOverride, follow, followOffset);

            if (logVfxSpawns)
            {
                Debug.Log(
                    $"[VfxSpawner] {channelLabel}特效已播放（{prefab.name}）于 {worldPosition.ToString("F2")}，" +
                    $"当前活跃特效 {ActiveVfxCount} 个。", this);
            }

            return true;
        }

        #endregion
    }
}
