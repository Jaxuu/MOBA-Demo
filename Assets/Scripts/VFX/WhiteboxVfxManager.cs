using System;
using System.Collections.Generic;
using UnityEngine;
using MOBA.Components;
using MOBA.Core;
using MOBA.Skills;

namespace MOBA.VFX
{
    /// <summary>
    /// 白盒占位特效管理器（阶段八，表现层）：把"技能放出去了"这件事变成画面上看得见的东西。
    ///
    /// 【为什么需要它】阶段八的 5v5 团战里，技能释放除了"进 CD"之外没有任何画面反馈 ——
    /// 玩家与评审都无从判断技能到底放出去了没有、打到了谁、范围多大。而正式美术资源要等到阶段九，
    /// 因此这里用引擎自带的几何体（<see cref="GameObject.CreatePrimitive"/>）做占位反馈。
    ///
    /// 【严格遵守「表现与逻辑分离」铁律】
    ///   · 本类只【订阅】SkillComponent 的事件、只【读】SkillData 与 EntityBase 的公开属性；
    ///   · 不调用任何会改变伤害/状态的方法，不写任何战斗字段，不参与目标合法性判断；
    ///   · 生成的占位物【没有 Collider】（见 CreateVfxObject 的说明），因此不会挡射线、不会被索敌扫到；
    ///   · 关掉本组件（或删掉 VfxRoot）对局结果完全不变 —— 这是"表现层可整体关闭"的判据。
    ///
    /// 【为什么按"效果语义"分派而不是按 Q/W/E/R 槽位分派】
    /// 槽位是给玩家英雄用的（盖伦式 QWER），而 9 个 AI 英雄的技能是【从技能池按固定 seed 抽出来的】，
    /// 同一个槽位在不同英雄身上是完全不同的技能。按槽位写死特效，AI 英雄的表现会整体错位。
    /// 因此判据全部取自 SkillData 的语义字段（castType / effectType / spawnZoneAtSelf / followCaster），
    /// 于是"任何英雄放的任何技能"都能得到与它机制相符的占位反馈。
    ///
    /// 【订阅的是哪个事件】SkillComponent 只有 OnCastStarted（校验通过、扣蓝起 CD 后）与
    /// OnSpellReleased（真的生成了弹道 / 范围场 / 已结算自身效果后）两个事件，没有 OnSpellCast。
    /// 本类订阅 OnSpellReleased —— 它的语义正是"效果已经落地"，与"特效要在效果生效时出现"完全对齐，
    /// 而且它只在真的生成了效果载体时才广播，因此"没打出去却播了特效"不会发生。
    ///
    /// 【层级约定】所有占位物都挂在本组件所在对象的子树下（VfxRoot/WhiteboxVfxManager）。
    /// 好处是"层级即账本"：回收不需要任何登记表，禁用管理器时遍历子节点全部销毁即可。
    /// VfxRoot 由一键组装工具创建在原点、缩放为 1，因此挂载不会改变占位物的世界尺寸。
    /// </summary>
    [DisallowMultipleComponent]
    public class WhiteboxVfxManager : MonoBehaviour
    {
        /// <summary>占位物名字前缀。用于 OnDisable 时精确识别"哪些子对象是自己生成的"。</summary>
        private const string VfxNamePrefix = "WBVfx_";

        private const string ShieldOrbName = VfxNamePrefix + "ShieldOrb";
        private const string ZoneDiscName = VfxNamePrefix + "ZoneDisc";
        private const string SmitePillarName = VfxNamePrefix + "SmitePillar";
        private const string AllyPulseName = VfxNamePrefix + "AllyPulse";
        private const string FallbackPulseName = VfxNamePrefix + "Pulse";

        [Header("总开关")]
        [Tooltip("关掉它等于「整块关闭白盒特效」——对局结果完全不变（表现层可整体关闭）。")]
        [SerializeField] private bool enableVfx = true;

        [Header("占位配色")]
        [Tooltip("Q 类自身增益（强化普攻 / 加速）时施法者被临时染成的颜色。")]
        [SerializeField] private Color empowerTintColor = new Color(1f, 0.9f, 0.2f, 1f);

        [Tooltip("W 类护盾：施法者身上的半透明球。")]
        [SerializeField] private Color shieldOrbColor = new Color(0.4f, 0.75f, 1f, 0.35f);

        [Tooltip("E 类以自身为中心的持续 AOE：脚下的压扁圆柱（模拟范围圈）。")]
        [SerializeField] private Color zoneDiscColor = new Color(1f, 0.15f, 0.1f, 0.3f);

        [Tooltip("R 类指向性斩杀：目标头顶落下的金色巨柱。")]
        [SerializeField] private Color smitePillarColor = new Color(1f, 0.82f, 0.2f, 0.45f);

        [Tooltip("指向性友方技能（治疗 / 护盾）：目标身上的淡绿脉冲。")]
        [SerializeField] private Color allyPulseColor = new Color(0.35f, 1f, 0.5f, 0.4f);

        [Tooltip("兜底脉冲：某个技能不匹配任何已知语义时，至少在施法者身上给一次可见反馈。")]
        [SerializeField] private Color fallbackPulseColor = new Color(1f, 1f, 1f, 0.3f);

        [Header("占位尺寸")]
        [Tooltip("护盾球半径（米）。取 1.15 略大于英雄胶囊体半径（0.5），能整个包住身体。")]
        [SerializeField] private float shieldOrbRadius = 1.15f;

        [Tooltip("护盾球的兜底最长存在时间（秒）。正常回收时机是「护盾降到 0」，这是防止极端配置下特效永生。")]
        [SerializeField] private float shieldOrbMaxLifetime = 10f;

        [Tooltip("范围圈圆柱的厚度（米）。压扁才有「地上的圈」的观感。")]
        [SerializeField] private float zoneDiscHeight = 0.14f;

        [Tooltip("范围圈半径读不到配置（EffectRadius <= 0）时使用的兜底半径（米）。")]
        [SerializeField] private float zoneDiscFallbackRadius = 3.5f;

        [Tooltip("范围圈读不到持续时长（AreaDuration <= 0）时使用的兜底时长（秒）。")]
        [SerializeField] private float zoneDiscFallbackDuration = 3f;

        [Tooltip("斩杀巨柱半径（米）。")]
        [SerializeField] private float smitePillarRadius = 1.1f;

        [Tooltip("斩杀巨柱高度（米）。取 9 米 = 明显高过所有单位，顶视角下一眼可见。")]
        [SerializeField] private float smitePillarHeight = 9f;

        [Tooltip("斩杀巨柱的下落耗时（秒）。")]
        [SerializeField] private float smiteFallDuration = 0.2f;

        [Tooltip("斩杀巨柱落地后的停留时长（秒）。")]
        [SerializeField] private float smiteHoldDuration = 0.6f;

        [Tooltip("友方脉冲半径（米）。")]
        [SerializeField] private float allyPulseRadius = 0.9f;

        [Tooltip("友方脉冲存在时长（秒）。")]
        [SerializeField] private float allyPulseLifetime = 1f;

        [Tooltip("兜底脉冲半径（米）。")]
        [SerializeField] private float fallbackPulseRadius = 0.8f;

        [Tooltip("兜底脉冲存在时长（秒）。")]
        [SerializeField] private float fallbackPulseLifetime = 0.6f;

        [Header("调试")]
        [Tooltip("在 Console 输出每次技能释放生成了哪些占位特效（排查「按了没反馈」时打开）。")]
        [SerializeField] private bool logVfxSpawns = false;

        [Tooltip("在 Console 输出订阅/退订记录（排查「AI 英雄的技能没有特效」时打开）。")]
        [SerializeField] private bool logSubscriptions = false;

        /// <summary>
        /// 一条技能订阅记录。
        /// 之所以要保存委托本身：C# 事件用 `-=` 退订时必须传入"同一个委托实例"，
        /// 而 lambda 每次求值都会生成新的实例 —— 不保存就永远退订不掉（表现为场景重载后旧管理器仍在响应）。
        /// </summary>
        private sealed class SkillSubscription
        {
            public EntityBase Entity;
            public SkillComponent Skills;
            public Action<SkillSlot, SkillData> Handler;
        }

        /// <summary>当前已订阅的技能组件清单。</summary>
        private readonly List<SkillSubscription> subscriptions = new List<SkillSubscription>();

        /// <summary>
        /// Ignore Raycast 层的层号。
        ///
        /// 【为什么是实例字段 + 在 Awake 里解析，而不是 static readonly 就地初始化】
        /// `LayerMask.NameToLayer` 属于"必须等引擎就绪才能调"的 API —— 在 MonoBehaviour 的
        /// 构造函数 / 字段初始化器里调用会让 Unity 直接抛
        /// 「NameToLayer is not allowed to be called from a MonoBehaviour constructor (or instance field initializer)」，
        /// 而且静态字段初始化器在【类型加载】时执行，异常会变成 TypeInitializationException，
        /// 连 Undo.AddComponent 都会一起失败（实机踩到：VfxRoot 上压根没挂上本组件）。
        /// 因此改成实例字段，默认值直接写引擎内置约定值 2（Unity 的 0~7 层是固定的），
        /// 再在 Awake 里按层名校正一次（编辑器里 AddComponent 不会跑 Awake，此时默认值 2 依然是正确的）。
        /// </summary>
        private int ignoreRaycastLayer = IgnoreRaycastLayerFallback;

        /// <summary>Ignore Raycast 的引擎内置层号（Unity 0~7 层固定，不随项目配置变化）。</summary>
        private const int IgnoreRaycastLayerFallback = 2;

        /// <summary>是否已就"读不到施法目标"告警过一次。</summary>
        private bool hasReportedMissingCastTarget;

        /// <summary>当前由本管理器生成的占位物数量（只读），供调试视图使用。</summary>
        public int ActiveVfxCount
        {
            get
            {
                int count = 0;

                for (int i = 0; i < transform.childCount; i++)
                {
                    Transform child = transform.GetChild(i);

                    if (child != null && child.name.StartsWith(VfxNamePrefix, StringComparison.Ordinal))
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        /// <summary>已订阅的技能组件数量（只读），供调试视图使用。</summary>
        public int SubscriptionCount => subscriptions.Count;

        /// <summary>
        /// 解析 Ignore Raycast 层号。
        /// 必须在 Awake（而不是字段初始化器）里做 —— 理由见 ignoreRaycastLayer 的注释。
        /// </summary>
        private void Awake()
        {
            ignoreRaycastLayer = ResolveIgnoreRaycastLayer();
        }

        /// <summary>
        /// 订阅实体注册表，并为已存在的实体挂上技能监听。
        /// 【顺序不可颠倒】先订阅事件、再读 Snapshot（与 WorldHealthBarManager 同一理由）：
        /// 反过来的话，在两次调用之间登记的单位既不在快照里、也没收到事件，会被彻底漏掉。
        /// </summary>
        private void OnEnable()
        {
            EntityRegistry.OnEntityRegistered += HandleEntityRegistered;
            EntityRegistry.OnEntityUnregistered += HandleEntityUnregistered;

            EntityBase[] snapshot = EntityRegistry.Snapshot();

            for (int i = 0; i < snapshot.Length; i++)
            {
                AttachTo(snapshot[i]);
            }
        }

        /// <summary>
        /// 退订全部技能事件并清掉自己生成的所有占位物。
        /// 不清的话，禁用管理器后战场上会留下一地几何体（它们自己不归任何人管，只按各自的倒计时消失）。
        /// </summary>
        private void OnDisable()
        {
            EntityRegistry.OnEntityRegistered -= HandleEntityRegistered;
            EntityRegistry.OnEntityUnregistered -= HandleEntityUnregistered;

            for (int i = subscriptions.Count - 1; i >= 0; i--)
            {
                DetachAt(i);
            }

            ClearSpawnedVfx();
        }

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
        /// 为一个实体挂上技能监听。没有 SkillComponent 的实体（小兵 / 塔 / 基地）直接跳过 ——
        /// 这不是错误：本阶段只有英雄拥有技能，而给小兵挂监听只会白占一次订阅。
        /// </summary>
        /// <param name="entity">目标实体。</param>
        private void AttachTo(EntityBase entity)
        {
            if (entity == null)
            {
                return;
            }

            // 用 GetComponent 而不是读 entity.Skills 属性：动态创建的单位在 Awake 时组件可能还没挂齐，
            // 属性里缓存的引用会是 null（与 WorldHealthBarManager 取 HealthComponent 同一口径）。
            SkillComponent skills = entity.GetComponent<SkillComponent>();

            if (skills == null)
            {
                return;
            }

            for (int i = 0; i < subscriptions.Count; i++)
            {
                if (subscriptions[i].Skills == skills)
                {
                    // 已订阅（快照与事件可能都命中同一实体）：去重，否则同一次施法会生成两套特效。
                    return;
                }
            }

            SkillSubscription subscription = new SkillSubscription
            {
                Entity = entity,
                Skills = skills,

                // 事件签名里没有发送者，因此必须为每个实体各绑一个委托。
                // 这一次闭包分配发生在"单位登记时"（一局十几次），不在施法路径上，可以接受。
                Handler = (slot, data) => HandleSpellReleased(entity, slot, data)
            };

            skills.OnSpellReleased += subscription.Handler;
            subscriptions.Add(subscription);

            if (logSubscriptions)
            {
                Debug.Log($"[WhiteboxVfxManager] 已订阅 {entity.name} 的技能释放事件。", this);
            }
        }

        /// <summary>退订指定下标并移除记录（调用方负责倒序遍历）。</summary>
        private void DetachAt(int index)
        {
            SkillSubscription subscription = subscriptions[index];
            subscriptions.RemoveAt(index);

            if (subscription.Skills == null)
            {
                return;
            }

            // Skills 已被销毁时，事件委托链也随之失去意义（对象已不可达），但 -= 本身仍然安全：
            // 它操作的是托管对象的字段，不触发 Unity 的已销毁检查。
            subscription.Skills.OnSpellReleased -= subscription.Handler;

            if (logSubscriptions)
            {
                Debug.Log(
                    $"[WhiteboxVfxManager] 已退订 {(subscription.Entity != null ? subscription.Entity.name : "已销毁实体")} 的技能释放事件。",
                    this);
            }
        }

        #endregion

        #region 技能释放 → 占位特效

        /// <summary>
        /// 技能释放回调：按技能【语义】挑选占位特效。
        ///
        /// 一次施法可能同时命中多条规则（例如「护盾 + 附带加速」），因此这里刻意不做 else-if 互斥，
        /// 而是各自独立判断 —— 多生成一个几何体不影响任何逻辑，漏掉反馈却会让技能看起来"没生效"。
        /// </summary>
        /// <param name="caster">施法者。</param>
        /// <param name="slot">技能槽位（仅用于日志）。</param>
        /// <param name="data">技能配置。</param>
        private void HandleSpellReleased(EntityBase caster, SkillSlot slot, SkillData data)
        {
            if (!enableVfx || caster == null || data == null)
            {
                return;
            }

            // 施法者可能在前摇期间阵亡（OnSpellReleased 不会因此广播，但防御性判一次）。
            HealthComponent casterHealth = caster.Health;

            if (casterHealth != null && casterHealth.IsDead)
            {
                return;
            }

            bool spawnedAny = false;

            // ① 护盾 → 半透明淡蓝球，挂在施法者身上（跟随），护盾消失即回收。
            if (HasShieldEffect(data))
            {
                spawnedAny |= SpawnShieldOrb(caster);
            }

            // ② 以自身为中心的持续 AOE（E 审判）→ 脚下压扁的红色半透明圆柱，随施法者移动。
            if (data.CastType == SkillCastType.Self && data.SpawnZoneAtSelf)
            {
                spawnedAny |= SpawnZoneDisc(caster, data);
            }

            // ③ 指向性伤害 / 斩杀（R 德玛西亚正义）→ 目标头顶落下的金色巨柱。
            if (data.CastType == SkillCastType.UnitTarget
                && !data.TargetsAlly
                && (data.EffectType == SkillEffectType.ExecuteDamage || data.EffectType == SkillEffectType.Damage))
            {
                spawnedAny |= SpawnSmitePillar(caster, data);
            }

            // ④ 指向性友方（治疗 / 护盾）→ 目标身上的淡绿脉冲。
            if (data.CastType == SkillCastType.UnitTarget && data.TargetsAlly)
            {
                spawnedAny |= SpawnAllyPulse(caster);
            }

            // ⑤ 自身增益（Q 致命打击 / 疾行术）→ 施法者临时变黄，时长取该技能读得到的最大时长。
            if (data.CastType == SkillCastType.Self && !data.SpawnZoneAtSelf && !HasShieldEffect(data))
            {
                WhiteboxTint.Apply(caster.gameObject, empowerTintColor, ResolveTintDuration(data));
                spawnedAny = true;
            }

            // ⑥ 兜底：不匹配任何已知语义的技能，至少在施法者身上给一次可见脉冲。
            //    没有它的话，新增一种技能形态时会静默地"按了没反应"，而这正是本次要解决的问题。
            if (!spawnedAny)
            {
                spawnedAny = SpawnFallbackPulse(caster);
            }

            if (logVfxSpawns && spawnedAny)
            {
                Debug.Log(
                    $"[WhiteboxVfxManager] {caster.name} 释放 {data.DisplayName}（{slot}）→ 已生成占位特效" +
                    $"（类型 {data.CastType} / 效果 {data.EffectType}），当前场上 {ActiveVfxCount} 个。", this);
            }
        }

        /// <summary>技能是否带有护盾效果（主效果或附带效果之一）。</summary>
        private static bool HasShieldEffect(SkillData data)
        {
            return data.EffectType == SkillEffectType.Shield
                || data.SecondaryEffectType == SkillEffectType.Shield;
        }

        /// <summary>
        /// 变黄持续多久：取该技能里所有"有明确时长"的自身增益效果中的最大值。
        /// 读不到任何时长时给 1.5 秒兜底 —— 短脉冲也远好过"按了没反应"。
        /// </summary>
        private static float ResolveTintDuration(SkillData data)
        {
            float duration = 0f;

            if (data.EffectType == SkillEffectType.EmpowerNextAttack)
            {
                duration = Mathf.Max(duration, data.EmpowerDuration);
            }
            else if (data.EffectType == SkillEffectType.Haste)
            {
                duration = Mathf.Max(duration, data.HasteDuration);
            }

            if (data.SecondaryEffectType == SkillEffectType.EmpowerNextAttack
                || data.SecondaryEffectType == SkillEffectType.Haste)
            {
                duration = Mathf.Max(duration, data.SecondaryDuration);
            }

            return duration > 0f ? duration : 1.5f;
        }

        /// <summary>W：施法者身上的半透明淡蓝球，跟随施法者，护盾降到 0 即回收。</summary>
        private bool SpawnShieldOrb(EntityBase caster)
        {
            Material material = WhiteboxVfxMaterials.GetTransparent(shieldOrbColor);

            if (material == null)
            {
                return false;
            }

            Vector3 followOffset = Vector3.up * (shieldOrbRadius * 0.8f);

            // 球体图元（PrimitiveType.Sphere）的直径是 1，因此缩放 = 半径 × 2。
            GameObject orb = CreateVfxObject(
                ShieldOrbName, PrimitiveType.Sphere,
                caster.transform.position + followOffset,
                Vector3.one * (shieldOrbRadius * 2f),
                material);

            WhiteboxVfxLifetime lifetime = orb.GetComponent<WhiteboxVfxLifetime>();

            lifetime.Configure(
                shieldOrbMaxLifetime,
                caster.transform,
                followOffset,
                caster.Health,     // 护盾来源：降到 0 就回收
                caster.transform,  // 宿主：施法者被销毁也回收
                0f,
                0f);

            return true;
        }

        /// <summary>E：施法者脚下压扁的红色半透明圆柱（范围圈），followCaster 时随施法者移动。</summary>
        private bool SpawnZoneDisc(EntityBase caster, SkillData data)
        {
            Material material = WhiteboxVfxMaterials.GetTransparent(zoneDiscColor);

            if (material == null)
            {
                return false;
            }

            float radius = data.EffectRadius > 0f ? data.EffectRadius : zoneDiscFallbackRadius;
            float duration = data.AreaDuration > 0f ? data.AreaDuration : zoneDiscFallbackDuration;
            float halfHeight = zoneDiscHeight * 0.5f;

            // 圆柱图元（PrimitiveType.Cylinder）高 2、直径 1，因此 scale.y = 目标高度 / 2；
            // 位置抬高半个厚度，让底面正好贴地（而不是一半埋进地面）。
            GameObject disc = CreateVfxObject(
                ZoneDiscName, PrimitiveType.Cylinder,
                caster.transform.position + Vector3.up * halfHeight,
                new Vector3(radius * 2f, halfHeight, radius * 2f),
                material);

            WhiteboxVfxLifetime lifetime = disc.GetComponent<WhiteboxVfxLifetime>();

            // 刻意【不】把施法者作为"宿主回收"条件：范围场是独立于施法者存亡的持续效果
            // （AreaEffectZone 也是这个语义：施法者阵亡后场停在原地走完剩余时长）。
            // 因此只在 followCaster = true 时跟随，施法者消失后自然停在最后位置。
            lifetime.Configure(
                duration,
                data.FollowCaster ? caster.transform : null,
                Vector3.up * halfHeight,
                null,
                null,
                0f,
                0f);

            return true;
        }

        /// <summary>R：目标头顶落下的金色巨柱。</summary>
        private bool SpawnSmitePillar(EntityBase caster, SkillData data)
        {
            Material material = WhiteboxVfxMaterials.GetTransparent(smitePillarColor);

            if (material == null)
            {
                return false;
            }

            Transform targetTransform = ResolveCastTargetTransform(caster);
            Vector3 ground;

            if (targetTransform != null)
            {
                ground = targetTransform.position;
            }
            else
            {
                // 读不到目标（理论上不该发生，见 SkillComponent.LastCastTarget）：
                // 退化成"施法者正前方若干米"，至少让玩家看到一道光柱砸下来，而不是什么都没有。
                ground = caster.transform.position
                    + caster.transform.forward * Mathf.Min(data.CastRange, 5f);

                ReportMissingCastTargetOnce();
            }

            float halfHeight = smitePillarHeight * 0.5f;

            GameObject pillar = CreateVfxObject(
                SmitePillarName, PrimitiveType.Cylinder,
                ground + Vector3.up * halfHeight,
                new Vector3(smitePillarRadius * 2f, halfHeight, smitePillarRadius * 2f),
                material);

            WhiteboxVfxLifetime lifetime = pillar.GetComponent<WhiteboxVfxLifetime>();

            // 延迟出现：R 是弹道技能，OnSpellReleased 在【弹道生成】时广播，而伤害要等弹道飞到才结算。
            // 若巨柱立刻砸下来，会看到"柱子先落地、半秒后伤害数字才跳"的错位 —— 观感上技能像打空了。
            // 因此按「距离 / 弹道速度」估算飞行时间，让巨柱恰好在命中那一刻落下。
            float travelDelay = ResolveProjectileTravelTime(caster, data, ground);

            lifetime.Configure(
                travelDelay + smiteFallDuration + smiteHoldDuration,
                null,
                Vector3.zero,
                null,
                null,
                smitePillarHeight,
                smiteFallDuration);

            lifetime.SetStartDelay(travelDelay);

            return true;
        }

        /// <summary>
        /// 估算弹道从施法者飞到落点所需的时间（秒），用于把"命中瞬间"的表现对齐到真实结算时刻。
        /// 非弹道技能（瞬发指向性）返回 0。
        /// </summary>
        /// <param name="caster">施法者。</param>
        /// <param name="data">技能配置。</param>
        /// <param name="ground">落点。</param>
        /// <returns>飞行时间（秒），无法估算时为 0。</returns>
        private static float ResolveProjectileTravelTime(EntityBase caster, SkillData data, Vector3 ground)
        {
            if (!data.HasProjectile || data.ProjectileSpeed <= 0f)
            {
                return 0f;
            }

            float distance = Vector3.Distance(caster.transform.position, ground);
            return distance / data.ProjectileSpeed;
        }

        /// <summary>指向性友方技能：目标身上的淡绿脉冲（治疗 / 护盾落到谁身上，一眼可见）。</summary>
        private bool SpawnAllyPulse(EntityBase caster)
        {
            Material material = WhiteboxVfxMaterials.GetTransparent(allyPulseColor);

            if (material == null)
            {
                return false;
            }

            Transform targetTransform = ResolveCastTargetTransform(caster);
            Transform follow = targetTransform != null ? targetTransform : caster.transform;
            Vector3 followOffset = Vector3.up * 0.9f;

            GameObject pulse = CreateVfxObject(
                AllyPulseName, PrimitiveType.Sphere,
                follow.position + followOffset,
                Vector3.one * (allyPulseRadius * 2f),
                material);

            pulse.GetComponent<WhiteboxVfxLifetime>().Configure(
                allyPulseLifetime, follow, followOffset, null, follow, 0f, 0f);

            return true;
        }

        /// <summary>兜底脉冲：不匹配任何已知语义的技能，至少在施法者身上给一次可见反馈。</summary>
        private bool SpawnFallbackPulse(EntityBase caster)
        {
            Material material = WhiteboxVfxMaterials.GetTransparent(fallbackPulseColor);

            if (material == null)
            {
                return false;
            }

            Vector3 followOffset = Vector3.up * 0.9f;

            GameObject pulse = CreateVfxObject(
                FallbackPulseName, PrimitiveType.Sphere,
                caster.transform.position + followOffset,
                Vector3.one * (fallbackPulseRadius * 2f),
                material);

            pulse.GetComponent<WhiteboxVfxLifetime>().Configure(
                fallbackPulseLifetime, caster.transform, followOffset, null, caster.transform, 0f, 0f);

            return true;
        }

        /// <summary>
        /// 取本次施法锁定的目标 Transform。
        /// 数据来源是 <see cref="SkillComponent.LastCastTarget"/>（只读属性，与 HealthComponent.LastDamageSource 同一模式）——
        /// 之所以不能读 TargetingComponent.CurrentTarget：那是【普攻】的目标，
        /// 与"这次技能锁的是谁"是两件事（玩家可以右键锁定 A 而把技能丢给鼠标下的 B）。
        /// </summary>
        /// <param name="caster">施法者。</param>
        /// <returns>目标 Transform；非指向性技能、目标已销毁或目标就是施法者自身时返回 null。</returns>
        private Transform ResolveCastTargetTransform(EntityBase caster)
        {
            SkillComponent skills = caster.Skills;

            if (skills == null)
            {
                return null;
            }

            ITargetable target = skills.LastCastTarget;

            if (target == null)
            {
                return null;
            }

            // 自身施法时目标是施法者自己（不该在"头顶"再落一根柱子）。
            if (ReferenceEquals(target, caster))
            {
                return null;
            }

            // 接口引用在对象被销毁后不会变成 null，必须用 Unity 的 == 判断。
            if (target is UnityEngine.Object unityObject && unityObject == null)
            {
                return null;
            }

            return target.TargetTransform;
        }

        private void ReportMissingCastTargetOnce()
        {
            if (hasReportedMissingCastTarget)
            {
                return;
            }

            hasReportedMissingCastTarget = true;

            Debug.LogWarning(
                "[WhiteboxVfxManager] 指向性技能释放后读不到施法目标（SkillComponent.LastCastTarget 为空），" +
                "斩杀巨柱将落在施法者正前方。这不影响任何对局逻辑，但说明技能目标没有按预期传递到表现层。", this);
        }

        #endregion

        #region 占位物生成与清理

        /// <summary>
        /// 生成一个占位几何体。
        ///
        /// 【必须移除图元自带的 Collider —— 这是"表现反向影响逻辑"最直接的一条路径】
        /// <see cref="GameObject.CreatePrimitive"/> 会连 Collider 一起生成。留着它的后果：
        ///   · 玩家右键点地面时，射线会先打在特效上 → 拾取不到地面 Layer → 移动指令静默失效；
        ///   · Physics.OverlapSphere 会把特效扫进候选（索敌与范围结算的候选表被无谓撑大）。
        /// 因此这里做两件事：同步把 Collider 关掉（Destroy 要等帧末，本帧仍会被查询命中），
        /// 再把对象放到 Ignore Raycast 层（层号 2，Unity 内置恒定存在，且不在 Physics.DefaultRaycastLayers 里）。
        /// </summary>
        /// <param name="objectName">对象名（带 WBVfx_ 前缀，便于整体清理）。</param>
        /// <param name="primitive">图元类型。</param>
        /// <param name="position">世界坐标。</param>
        /// <param name="scale">世界缩放（VfxRoot 缩放为 1，因此直接写 localScale）。</param>
        /// <param name="material">材质（共享资产 / 缓存材质，不克隆）。</param>
        /// <returns>生成的对象（已挂好 WhiteboxVfxLifetime，调用方负责 Configure）。</returns>
        private GameObject CreateVfxObject(
            string objectName, PrimitiveType primitive, Vector3 position, Vector3 scale, Material material)
        {
            GameObject created = GameObject.CreatePrimitive(primitive);
            created.name = objectName;

            Collider primitiveCollider = created.GetComponent<Collider>();

            if (primitiveCollider != null)
            {
                primitiveCollider.enabled = false;
                Destroy(primitiveCollider);
            }

            created.layer = ignoreRaycastLayer;

            // 挂到本管理器下（VfxRoot → WhiteboxVfxManager），层级即账本，禁用时整棵子树清掉。
            created.transform.SetParent(transform, true);
            created.transform.position = position;
            created.transform.localScale = scale;

            Renderer renderer = created.GetComponent<Renderer>();

            if (renderer != null)
            {
                // sharedMaterial：写共享材质，避免每个特效克隆一份材质实例。
                renderer.sharedMaterial = material;
            }

            // AddComponent 会同步触发 Awake，因此必须在 AddComponent 之后再 Configure。
            created.AddComponent<WhiteboxVfxLifetime>();

            return created;
        }

        /// <summary>销毁本管理器生成的全部占位物（按名字前缀识别，不动别人的子对象）。</summary>
        private void ClearSpawnedVfx()
        {
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                Transform child = transform.GetChild(i);

                if (child == null || !child.name.StartsWith(VfxNamePrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                Destroy(child.gameObject);
            }
        }

        /// <summary>
        /// 解析 Ignore Raycast 层的层号（只在 Awake 里调用）。
        /// 拿不到时退回引擎约定值 2（该层是 Unity 内置层，正常不可能拿不到）。
        /// </summary>
        private static int ResolveIgnoreRaycastLayer()
        {
            int layer = LayerMask.NameToLayer("Ignore Raycast");
            return layer >= 0 ? layer : IgnoreRaycastLayerFallback;
        }

        #endregion
    }
}
