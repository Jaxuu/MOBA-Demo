using UnityEngine;
using MOBA.Skills;

namespace MOBA.Data
{
    /// <summary>
    /// 单位静态数值配置模板（只读）。
    /// 约定：本资产只存放"初始值 / 模板值"，任何运行时状态（当前生命、攻击冷却、当前目标）
    /// 必须保存在组件实例中，严禁写回 ScriptableObject，否则会在编辑器与打包环境下产生数据污染。
    /// </summary>
    [CreateAssetMenu(
        fileName = "EntityStats_New",
        menuName = "MOBA/Entity Stats",
        order = 0)]
    public class EntityStatsData : ScriptableObject
    {
        [Header("基础属性")]
        [Tooltip("最大生命值，同时作为 HealthComponent 的初始生命。")]
        [Min(1f)]
        [SerializeField] private float maxHealth = 100f;

        [Tooltip("移动速度（单位/秒），写入 NavMeshAgent.speed，用于让策划免改代码调整手感。")]
        [Min(0f)]
        [SerializeField] private float moveSpeed = 3.5f;

        [Tooltip("索敌距离（单位），TargetingComponent 在此范围内搜索敌方目标。")]
        [Min(0f)]
        [SerializeField] private float detectionRange = 8f;

        [Header("攻击配置")]
        [Tooltip("普通攻击参数资产，由 EntityBase 注入 CombatComponent。" +
                 "留空时 CombatComponent 会沿用其 Inspector 上直挂的 AttackData。")]
        [SerializeField] private AttackData attack;

        [Header("法力与技能（阶段六）")]
        [Tooltip("最大法力值。填 0 表示该单位没有法力（小兵/建筑保持 0 即可），ManaComponent 不会启用回复。")]
        [Min(0f)]
        [SerializeField] private float maxMana = 0f;

        [Tooltip("每秒法力回复量。填 0 表示不自动回复。")]
        [Min(0f)]
        [SerializeField] private float manaRegenPerSecond = 0f;

        // ---- 技能槽（阶段八实机打回后：不由本资产承载）----
        //
        // 【为什么留着字段却要求它必须为空】它们在阶段六/七曾是"技能配置的主数据流"，
        // 但 10 个英雄共用同一份 HeroStats，而运行期 `EntityBase.ApplyStats` 会拿它调
        // `SkillComponent.Initialize` —— 于是 AI 英雄【按技能池注入】的技能槽会被这份共享模板
        // 整体改写成玩家的盖伦四件套（实机症状：9 个 AI 全在转大风车 / 劈大宝剑，而编辑器里看
        // 它们的技能槽完全正确，因为覆盖发生在运行期 Start）。这类"两个写入者"的失效在工具侧查不出来。
        //
        // 现在技能的唯一承载者是 SkillComponent 自己：
        //   · 玩家英雄 = 英雄预制体上直挂的盖伦四件套（由 CreateHeroPrefab 注入）；
        //   · AI 英雄   = 按实例注入的技能池抽签结果（由 CreateHeroInstance 注入）。
        // 一键组装会**清空**这四个字段并给出说明（见 ClearHeroStatsSkillFields），
        // 同时 SkillComponent.Initialize 遵守"实例整体接管"（任意一槽非空即视为实例提供技能，
        // 模板一个槽都不写），因此即使有人把值填回来，也只会被忽略并报一条警告，不会再静默覆盖实例配置。
        //
        // 字段保留（而不删除）的理由：旧资产的序列化数据里本来就有它们，删字段会在加载时产生
        // 无谓的差异噪音；保留 + 由工具保证为空，语义上等价且改动面最小。

        [Tooltip("【已废弃：请勿填写】Q 槽位技能配置。技能槽由 SkillComponent 承载" +
                 "（玩家 = 预制体直挂、AI = 按实例注入）；填了也不会生效，一键组装会清空它。")]
        [SerializeField] private SkillData skillQ;

        [Tooltip("【已废弃：请勿填写】W 槽位技能配置，说明同 Q。")]
        [SerializeField] private SkillData skillW;

        [Tooltip("【已废弃：请勿填写】E 槽位技能配置，说明同 Q。")]
        [SerializeField] private SkillData skillE;

        [Tooltip("【已废弃：请勿填写】R 槽位技能配置，说明同 Q。")]
        [SerializeField] private SkillData skillR;

        public float MaxHealth => maxHealth;

        public float MoveSpeed => moveSpeed;

        public float DetectionRange => detectionRange;

        /// <summary>普通攻击配置引用。可能为 null，使用方需判空。</summary>
        public AttackData Attack => attack;

        /// <summary>最大法力值；0 表示该单位没有法力。</summary>
        public float MaxMana => maxMana;

        /// <summary>每秒法力回复量；0 表示不自动回复。</summary>
        public float ManaRegenPerSecond => manaRegenPerSecond;

        /// <summary>Q 槽位技能配置。**已废弃**：技能槽由 SkillComponent 承载，本字段恒为空（见上方说明）。</summary>
        public SkillData SkillQ => skillQ;

        /// <summary>W 槽位技能配置。**已废弃**，说明同 Q。</summary>
        public SkillData SkillW => skillW;

        /// <summary>E 槽位技能配置。**已废弃**，说明同 Q。</summary>
        public SkillData SkillE => skillE;

        /// <summary>R 槽位技能配置。**已废弃**，说明同 Q。</summary>
        public SkillData SkillR => skillR;
    }
}
