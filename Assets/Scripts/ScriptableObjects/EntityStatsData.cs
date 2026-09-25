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

        [Tooltip("Q 槽位技能配置，由 EntityBase 注入 SkillComponent。" +
                 "留空时 SkillComponent 会沿用其 Inspector 上直挂的 SkillData。")]
        [SerializeField] private SkillData skillQ;

        [Tooltip("W 槽位技能配置，注入规则同 Q。")]
        [SerializeField] private SkillData skillW;

        public float MaxHealth => maxHealth;

        public float MoveSpeed => moveSpeed;

        public float DetectionRange => detectionRange;

        /// <summary>普通攻击配置引用。可能为 null，使用方需判空。</summary>
        public AttackData Attack => attack;

        /// <summary>最大法力值；0 表示该单位没有法力。</summary>
        public float MaxMana => maxMana;

        /// <summary>每秒法力回复量；0 表示不自动回复。</summary>
        public float ManaRegenPerSecond => manaRegenPerSecond;

        /// <summary>Q 槽位技能配置，可能为 null。</summary>
        public SkillData SkillQ => skillQ;

        /// <summary>W 槽位技能配置，可能为 null。</summary>
        public SkillData SkillW => skillW;
    }
}
