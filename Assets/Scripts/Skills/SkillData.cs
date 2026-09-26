using UnityEngine;

namespace MOBA.Skills
{
    /// <summary>
    /// 技能静态配置模板（只读）。
    ///
    /// 约定（与 EntityStatsData / AttackData 完全一致）：本资产只存放"模板值"，
    /// 任何运行时状态（冷却剩余、是否正在前摇、弹道飞行进度）必须保存在 SkillComponent / Projectile 实例中，
    /// 严禁写回 ScriptableObject，否则会在编辑器与打包环境下产生数据污染。
    ///
    /// 【效果模型：主效果 + 一个附带效果（阶段八扩充）】
    /// 阶段六定的是"V1 单主效果模型"（Q = Damage、W = Slow 各占一种）。
    /// 阶段八的「Q 致命打击」需要同时表达"自身加速"与"强化下一次普攻"两件事，
    /// 单主效果无法覆盖，因此扩成"主效果 + 一个附带效果"（见 SecondaryEffectType）。
    /// 仍然【不做效果数组】：那是完整的属性修饰器聚合体系，README §3.5 明确排除；
    /// 而"两个效果"已经能覆盖本阶段全部需求，且 Inspector 里只是多一组字段，策划不需要理解组合语义。
    /// 演进路径已留好：日后要支持更多效果，把这两个槽位升级为 SkillEffectData[] 即可，
    /// SkillEffectResolver.Apply 的调用点（Projectile / AreaEffectZone / SkillComponent）一行都不用改。
    ///
    /// 【为什么不持有特效预制体引用】README §3.4 硬性约束第 1 条要求逻辑层禁止引用
    /// Animator / ParticleSystem / AudioSource / UI 组件。若这里直接放 GameObject / ParticleSystem 引用，
    /// SkillComponent（逻辑层）就会间接持有表现资源，等于绕开这条铁律。
    /// 因此表现槽位统一存字符串 ID，由表现层（阶段八的 VfxSpawner）查表换成实际资源——
    /// 顺带让阶段八接 Addressables 时零改动。
    ///
    /// 【前摇 castTime 的语义】它是"逻辑前摇"（秒），由 SkillComponent 用 Time.time 计时，
    /// 与动画片段长度【无关】。README §3.4 第 3 条明确"逻辑层不等待动画"：
    /// 动画只做事后视觉匹配（必要时调播放速度），动画被裁剪不得影响技能释放时机。
    /// </summary>
    [CreateAssetMenu(
        fileName = "SkillData_New",
        menuName = "MOBA/Skill Data",
        order = 2)]
    public class SkillData : ScriptableObject
    {
        [Header("标识与槽位")]
        [Tooltip("技能槽位（Q/W/E/R）。必须与配置它的槽位一致，SkillComponent 不读本字段做分派，仅用于日志与 UI。")]
        [SerializeField] private SkillSlot slot = SkillSlot.Q;

        [Tooltip("技能显示名，供 UI / 日志展示。")]
        [SerializeField] private string displayName = "新技能";

        [Tooltip("技能描述，供 UI 展示。")]
        [TextArea(2, 4)]
        [SerializeField] private string description = "";

        [Tooltip("技能图标，供阶段七 HUD 使用。资源引用不参与任何战斗逻辑。")]
        [SerializeField] private Sprite icon;

        [Header("施法类型")]
        [Tooltip("UnitTarget = 指向性（需锁定一个单位）；GroundPoint = 非指向性（需一个地面落点）；" +
                 "Self = 自身施法（无需目标与落点，按键即生效）。")]
        [SerializeField] private SkillCastType castType = SkillCastType.UnitTarget;

        [Tooltip("目标是否为【友方】（阶段八新增，仅 castType = UnitTarget 时有意义）。\n" +
                 "false = 只能锁定敌方单位（伤害 / 控制类，默认）；\n" +
                 "true  = 只能锁定友方单位（治疗 / 护盾类）。\n" +
                 "为什么用显式开关而不是从效果类型反推：治疗与伤害的目标阵营是两套不同的规则，" +
                 "写在数据里，SkillComponent 的校验链与 AI 的选目标逻辑才能各读同一个来源，不会分叉。")]
        [SerializeField] private bool targetsAlly = false;

        [Header("施法约束")]
        [Tooltip("施法距离（单位）。指向性量「施法者 → 目标单位」，非指向性量「施法者 → 落点」。超出即拒绝释放。")]
        [Min(0f)]
        [SerializeField] private float castRange = 8f;

        [Tooltip("前摇（秒，逻辑计时）。0 = 瞬发。\n" +
                 "前摇期间施法者被锁住移动与攻击；死亡会中断前摇（V1 不做主动打断）。\n" +
                 "【前摇为 0 时不上控制锁】没有硬直就没有需要保护的时间窗，" +
                 "上锁反而会清掉施法者当前路径且不恢复（表现为「边走边放瞬发技能会立刻停在原地」）。")]
        [Min(0f)]
        [SerializeField] private float castTime = 0.25f;

        [Tooltip("冷却时间（秒）。校验通过后立刻开始计时（MOBA 惯例：施法瞬间即进入 CD）。")]
        [Min(0f)]
        [SerializeField] private float cooldown = 6f;

        [Tooltip("法力消耗。法力不足时拒绝释放，且不扣蓝、不起 CD。")]
        [Min(0f)]
        [SerializeField] private float manaCost = 30f;

        [Header("弹道（仅 castType = UnitTarget 且 projectileSpeed > 0 时使用）")]
        [Tooltip("弹道速度（单位/秒）。它决定「可见飞行时间」：射程 8 / 速度 14 ≈ 0.57 秒。填 0 表示瞬发指向性技能。")]
        [Min(0f)]
        [SerializeField] private float projectileSpeed = 14f;

        [Tooltip("命中判定半径（单位）。弹道与目标的距离小于它即判定命中，不使用物理碰撞体。")]
        [Min(0.01f)]
        [SerializeField] private float projectileRadius = 0.5f;

        [Tooltip("命中数上限：1 = 非穿透（命中即销毁，V1 火球）；大于 1 为穿透，属阶段六不做范围（运行时会告警并仍按非穿透处理）。")]
        [Min(1)]
        [SerializeField] private int maxHitCount = 1;

        [Tooltip("弹道最大飞行距离（单位）。超出即静默回收，用于兜住「目标不可达 / 被障碍挡住」时弹道永生的极端情况。")]
        [Min(0.1f)]
        [SerializeField] private float maxTravelDistance = 12f;

        [Header("范围（仅 castType = GroundPoint 时使用）")]
        [Tooltip("作用半径（单位）。范围内的敌方单位才会被结算，友方与中立零影响。")]
        [Min(0f)]
        [SerializeField] private float effectRadius = 3.5f;

        [Tooltip("范围持续时长（秒）。0 = 一次性爆发（生成即结算一次并立刻销毁）。")]
        [Min(0f)]
        [SerializeField] private float areaDuration = 4f;

        [Tooltip("周期结算间隔（秒）。0 = 只结算一次（持续时长仅用于表现留场）。建议不要小于 0.1，否则等于每帧结算。")]
        [Min(0f)]
        [SerializeField] private float tickInterval = 0.5f;

        [Tooltip("范围场是否【跟随施法者移动】（阶段八新增，仅 spawnZoneAtSelf = true 时有意义）。\n" +
                 "true  = 场以施法者为中心每帧同步位置（以自身为中心的持续 AOE，如 E 审判）；\n" +
                 "false = 场固定在释放时的落点。")]
        [SerializeField] private bool followCaster = false;

        [Tooltip("是否在【施法者脚下】生成一个持续范围场（阶段八新增，仅 castType = Self 时有意义）。\n" +
                 "true  = 走 AreaEffectZone（E 审判：以自身为中心的持续 AOE）；\n" +
                 "false = 直接把主效果作用于自身（Q 强化普攻 / W 护盾 / 疾行术）。\n" +
                 "【为什么必须有这个显式开关 —— 这是实机踩出来的】最初用「areaDuration > 0」来分派两条路径，\n" +
                 "但 areaDuration 的默认值是 4（地面 AOE 需要它），于是任何【没有显式清零】的 Self 技能\n" +
                 "都会被误判成范围场：按 Q 不会给自己加 buff，反而在脚下生成一个对敌人施加「强化普攻」的怪圈，\n" +
                 "而且不报任何错。教训：施法意图必须由显式字段表达，不能从另一个字段的数值默认值反推。")]
        [SerializeField] private bool spawnZoneAtSelf = false;

        [Header("效果（V1 单主效果）")]
        [Tooltip("主效果类型。决定下面哪几个数值字段会被用到，其余字段填 0 即可。")]
        [SerializeField] private SkillEffectType effectType = SkillEffectType.Damage;

        [Tooltip("伤害值（effectType = Damage 时使用）。")]
        [Min(0f)]
        [SerializeField] private float damage = 120f;

        [Tooltip("护盾值（effectType = Shield 时使用）。护盾优先吸收伤害，耗尽或到期移除。")]
        [Min(0f)]
        [SerializeField] private float shieldValue = 0f;

        [Tooltip("护盾持续时长（秒，effectType = Shield 时使用）。0 = 永久护盾（不自动移除）。")]
        [Min(0f)]
        [SerializeField] private float shieldDuration = 0f;

        [Tooltip("减速比例 0~1（effectType = Slow 时使用）。0.4 表示移速降到原来的 60%。")]
        [Range(0f, 1f)]
        [SerializeField] private float slowPercent = 0.4f;

        [Tooltip("减速时长（秒）。刻意建议大于 tickInterval：让敌人离开范围后仍残留一小段减速，手感更「粘」。")]
        [Min(0f)]
        [SerializeField] private float slowDuration = 0.6f;

        [Tooltip("眩晕时长（秒，effectType = Stun 时使用）。期间禁止移动与攻击，到期自动解除。")]
        [Min(0f)]
        [SerializeField] private float stunDuration = 0f;

        [Tooltip("斩杀系数（effectType = ExecuteDamage 时使用）：伤害 = damage + 本系数 × 目标已损失生命值。\n" +
                 "0.35 表示残血目标额外承受其已损失生命值 35% 的伤害。")]
        [Min(0f)]
        [SerializeField] private float executeHealthRatio = 0f;

        [Tooltip("治疗量（effectType = Heal 时使用）。血量不会超过上限，死亡目标不生效。")]
        [Min(0f)]
        [SerializeField] private float healAmount = 0f;

        [Tooltip("强化普攻的额外伤害（effectType = EmpowerNextAttack 时使用），在下一次普攻命中时叠加。")]
        [Min(0f)]
        [SerializeField] private float empowerBonusDamage = 0f;

        [Tooltip("强化普攻的持续时间（秒，effectType = EmpowerNextAttack 时使用）。\n" +
                 "它是兜底超时：正常消耗时机是下一次普攻【命中】，到期未消耗则自动作废，避免长期攒着。")]
        [Min(0f)]
        [SerializeField] private float empowerDuration = 5f;

        [Tooltip("沉默时长（秒）。effectType = EmpowerNextAttack 时表示命中后对目标施加的沉默；\n" +
                 "填 0 表示本次强化只加伤害、不附带沉默。")]
        [Min(0f)]
        [SerializeField] private float silenceDuration = 0f;

        [Tooltip("加速比例（effectType = Haste 或作为附带效果时使用）。0.3 表示移速提升到 130%。")]
        [Min(0f)]
        [SerializeField] private float hastePercent = 0f;

        [Tooltip("加速时长（秒）。")]
        [Min(0f)]
        [SerializeField] private float hasteDuration = 0f;

        [Header("附带效果（V1 上限一个；目前只服务于「Q = 加速 + 强化普攻」这类组合）")]
        [Tooltip("附带效果类型。None 表示没有附带效果。\n" +
                 "主效果与附带效果由 SkillEffectResolver 在同一入口里依次结算，调用点无需感知。")]
        [SerializeField] private SkillEffectType secondaryEffectType = SkillEffectType.None;

        [Tooltip("附带效果的数值，按类型解释：\n" +
                 "Haste  → 加速比例（0.3 = 移速 130%）；\n" +
                 "Heal   → 治疗量；Shield → 护盾值；Damage → 伤害值；Slow → 减速比例。")]
        [Min(0f)]
        [SerializeField] private float secondaryValue = 0f;

        [Tooltip("附带效果的时长（秒），按类型解释（Haste 的加速时长 / Slow 的减速时长 / Shield 的护盾时长）。")]
        [Min(0f)]
        [SerializeField] private float secondaryDuration = 0f;

        [Header("表现槽位（阶段八接入，逻辑层只读字符串 ID，不持有任何资源引用）")]
        [Tooltip("施法瞬间的表现 ID（施法动画 / 前摇特效 / 施法音效）。")]
        [SerializeField] private string castVfxId = "";

        [Tooltip("命中瞬间的表现 ID（命中特效 / 命中音效）。")]
        [SerializeField] private string hitVfxId = "";

        [Tooltip("释放瞬间的表现 ID（弹道外观 / AOE 圈表现）。")]
        [SerializeField] private string releaseVfxId = "";

        // ---- 只读访问口。运行时不提供任何 setter：配置只读，写入只走编辑器工具。 ----

        /// <summary>技能槽位（仅供日志与 UI 参考，SkillComponent 的分派依据是"被调用的槽位参数"）。</summary>
        public SkillSlot Slot => slot;

        /// <summary>技能显示名。</summary>
        public string DisplayName => displayName;

        /// <summary>技能描述。</summary>
        public string Description => description;

        /// <summary>技能图标（可能为 null）。</summary>
        public Sprite Icon => icon;

        /// <summary>施法类型。</summary>
        public SkillCastType CastType => castType;

        /// <summary>目标是否为友方（仅 UnitTarget 有意义）。</summary>
        public bool TargetsAlly => targetsAlly;

        /// <summary>施法距离。</summary>
        public float CastRange => castRange;

        /// <summary>前摇时长（秒，逻辑计时）。</summary>
        public float CastTime => castTime;

        /// <summary>冷却时间（秒）。</summary>
        public float Cooldown => cooldown;

        /// <summary>法力消耗。</summary>
        public float ManaCost => manaCost;

        /// <summary>弹道速度（单位/秒）；0 表示瞬发指向性技能。</summary>
        public float ProjectileSpeed => projectileSpeed;

        /// <summary>弹道命中判定半径。</summary>
        public float ProjectileRadius => projectileRadius;

        /// <summary>命中数上限（1 = 非穿透）。</summary>
        public int MaxHitCount => maxHitCount;

        /// <summary>弹道最大飞行距离。</summary>
        public float MaxTravelDistance => maxTravelDistance;

        /// <summary>范围作用半径。</summary>
        public float EffectRadius => effectRadius;

        /// <summary>范围持续时长（秒）；0 = 一次性爆发。</summary>
        public float AreaDuration => areaDuration;

        /// <summary>周期结算间隔（秒）；0 = 只结算一次。</summary>
        public float TickInterval => tickInterval;

        /// <summary>范围场是否跟随施法者移动（仅 spawnZoneAtSelf = true 时有意义）。</summary>
        public bool FollowCaster => followCaster;

        /// <summary>是否在施法者脚下生成持续范围场（仅 Self 施法有意义）。</summary>
        public bool SpawnZoneAtSelf => spawnZoneAtSelf;

        /// <summary>主效果类型。</summary>
        public SkillEffectType EffectType => effectType;

        /// <summary>伤害值。</summary>
        public float Damage => damage;

        /// <summary>护盾值。</summary>
        public float ShieldValue => shieldValue;

        /// <summary>护盾持续时长（秒）；0 = 永久护盾。</summary>
        public float ShieldDuration => shieldDuration;

        /// <summary>减速比例 0~1。</summary>
        public float SlowPercent => slowPercent;

        /// <summary>减速时长（秒）。</summary>
        public float SlowDuration => slowDuration;

        /// <summary>眩晕时长（秒）。</summary>
        public float StunDuration => stunDuration;

        /// <summary>斩杀系数：伤害 = Damage + 本系数 × 目标已损失生命值。</summary>
        public float ExecuteHealthRatio => executeHealthRatio;

        /// <summary>治疗量（effectType = Heal 时使用）。</summary>
        public float HealAmount => healAmount;

        /// <summary>强化普攻的额外伤害（effectType = EmpowerNextAttack 时使用）。</summary>
        public float EmpowerBonusDamage => empowerBonusDamage;

        /// <summary>强化普攻的兜底超时（秒）。</summary>
        public float EmpowerDuration => empowerDuration;

        /// <summary>沉默时长（秒）；0 = 不附带沉默。</summary>
        public float SilenceDuration => silenceDuration;

        /// <summary>加速比例（0.3 = 移速 130%）。</summary>
        public float HastePercent => hastePercent;

        /// <summary>加速时长（秒）。</summary>
        public float HasteDuration => hasteDuration;

        /// <summary>附带效果类型；None 表示没有附带效果。</summary>
        public SkillEffectType SecondaryEffectType => secondaryEffectType;

        /// <summary>附带效果的数值（按类型解释）。</summary>
        public float SecondaryValue => secondaryValue;

        /// <summary>附带效果的时长（秒，按类型解释）。</summary>
        public float SecondaryDuration => secondaryDuration;

        /// <summary>是否存在附带效果。</summary>
        public bool HasSecondaryEffect => secondaryEffectType != SkillEffectType.None;

        /// <summary>施法表现 ID（可能为空字符串）。</summary>
        public string CastVfxId => castVfxId;

        /// <summary>命中表现 ID（可能为空字符串）。</summary>
        public string HitVfxId => hitVfxId;

        /// <summary>释放表现 ID（可能为空字符串）。</summary>
        public string ReleaseVfxId => releaseVfxId;

        /// <summary>
        /// 是否需要生成弹道：指向性且配置了正速度。
        /// 单独抽成属性是为了让 SkillComponent 的分派条件只有一处，
        /// 避免"释放时判断一次、日志里又判断一次"导致口径分叉。
        /// </summary>
        public bool HasProjectile => castType == SkillCastType.UnitTarget && projectileSpeed > 0f;

        /// <summary>
        /// 编辑器内数值合法性纠正。只做夹取与互斥修正，不改变运行时的任何语义。
        ///
        /// 【为什么这里【不】做"自身增益类技能的范围字段清零" —— 这是实机踩出来的】
        /// 本方法此前有一段「castType == Self 且 !spawnZoneAtSelf → areaDuration / tickInterval /
        /// effectRadius / followCaster 一律清零」的归一化。它的意图是好的（这类技能的范围字段完全惰性，
        /// 留在 Inspector 里会误导），但 `OnValidate` 会在【脚本加载时】对所有已加载资产各跑一次 ——
        /// 而 `spawnZoneAtSelf` 是后加的字段，旧资产的序列化数据里【根本没有它】，
        /// 反序列化后必然取 C# 默认值 false。于是：
        ///   · 旧资产每次被加载都会在内存里被"顺手"清零一次（磁盘上还是旧值，两边长期不一致）；
        ///   · 更致命的是它抹掉了别的代码赖以判断的旁证 —— E 的自愈判据原本是
        ///     「followCaster = true 且 spawnZoneAtSelf = false」，而 followCaster 正好被这一段清零，
        ///     判据在自己的证据被抹掉后永远不成立 → 自愈一次都没跑过 → E 退化成"对自己结算一次伤害"
        ///     （实机症状：按 E 自己掉 18 点血，且 Console 一片安静）。
        ///   · 一旦有人在这之后触发一次保存，被清零的值还会被真正写进资产（设计值就此丢失）。
        ///
        /// 因此把这件事交给**工具在组装时显式做**（`NormalizeSelfSkillRangeFields`）：
        /// 那里是有意的、带日志的、可复核的一次性归一化；而 `OnValidate` 只保留"把非法值改成合法值"
        /// 这类必须即时生效的夹取。**通用教训：OnValidate 会因为"新字段尚未序列化"而误判，
        /// 因此它只能修正"单独看就非法"的值，不能基于跨字段推断去改写别的字段。**
        /// </summary>
        private void OnValidate()
        {
            // 非指向性技能没有目标单位可锁定，maxHitCount / 弹道速度等字段无意义但不强制清零，
            // 以免策划在两种类型之间来回切换时把已调好的数值抹掉。
            if (castType == SkillCastType.GroundPoint && effectRadius <= 0f)
            {
                effectRadius = 1f;
            }

            // 自身施法 + 生成范围场 = 以自身为中心的 AOE（E 审判走这条）。
            // 半径填 0 会让场"命中不到任何人"，而且不会报任何错，因此兜一个最小半径。
            // 注意判据是 spawnZoneAtSelf 而不是 areaDuration：后者有非零默认值，反推意图会误判。
            if (castType == SkillCastType.Self && spawnZoneAtSelf && effectRadius <= 0f)
            {
                effectRadius = 1f;
            }

            if (castType == SkillCastType.UnitTarget && projectileSpeed > 0f && projectileRadius <= 0f)
            {
                projectileRadius = 0.5f;
            }

            // 附带效果为空时把它的两个数值清零：否则资产里会留下"改了类型却还带着旧数值"的迷惑状态，
            // 排查"为什么这个技能没有任何附带效果"时要先去 Inspector 里读三个字段才能确认。
            if (secondaryEffectType == SkillEffectType.None)
            {
                secondaryValue = 0f;
                secondaryDuration = 0f;
            }
        }
    }
}
