using UnityEngine;

namespace MOBA.Data
{
    /// <summary>
    /// 普通攻击静态配置模板（只读）。
    /// 约定：与 EntityStatsData 一样，本资产只存放"初始值 / 模板值"，
    /// 任何运行时状态（下次可攻击时间 nextAttackTime、当前目标）必须保存在 CombatComponent 实例中，
    /// 严禁写回 ScriptableObject，否则会在编辑器与打包环境下产生数据污染。
    /// </summary>
    [CreateAssetMenu(
        fileName = "AttackData_New",
        menuName = "MOBA/Attack Data",
        order = 1)]
    public class AttackData : ScriptableObject
    {
        [Header("普攻参数")]
        [Tooltip("单次攻击造成的基础伤害值（每次命中结算一次）。")]
        [Min(0f)]
        [SerializeField] private float damage = 10f;

        [Tooltip("攻击距离（单位），单位中心到目标中心的距离判定阈值。")]
        [Min(0f)]
        [SerializeField] private float attackRange = 2f;

        [Tooltip("攻击间隔（秒），两次攻击之间的最小时间差；CombatComponent 用它与 Time.time 比较得出冷却。")]
        [Min(0.01f)]
        [SerializeField] private float attackInterval = 1f;

        public float Damage => damage;

        public float AttackRange => attackRange;

        public float AttackInterval => attackInterval;
    }
}
