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
        [Tooltip("UnitTarget = 指向性（需锁定敌方单位）；GroundPoint = 非指向性（需一个地面落点）。")]
        [SerializeField] private SkillCastType castType = SkillCastType.UnitTarget;

        [Header("施法约束")]
        [Tooltip("施法距离（单位）。指向性量「施法者 → 目标单位」，非指向性量「施法者 → 落点」。超出即拒绝释放。")]
        [Min(0f)]
        [SerializeField] private float castRange = 8f;

        [Tooltip("前摇（秒，逻辑计时）。0 = 瞬发。前摇期间施法者被锁住移动与攻击；死亡会中断前摇（V1 不做主动打断）。")]
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
        /// </summary>
        private void OnValidate()
        {
            // 非指向性技能没有目标单位可锁定，maxHitCount / 弹道速度等字段无意义但不强制清零，
            // 以免策划在两种类型之间来回切换时把已调好的数值抹掉。
            if (castType == SkillCastType.GroundPoint && effectRadius <= 0f)
            {
                effectRadius = 1f;
            }

            if (castType == SkillCastType.UnitTarget && projectileSpeed > 0f && projectileRadius <= 0f)
            {
                projectileRadius = 0.5f;
            }
        }
    }
}
