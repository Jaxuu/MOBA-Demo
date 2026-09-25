namespace MOBA.Core
{
    /// <summary>
    /// 可受伤目标接口：把"受伤"这件事从具体单位类型中抽象出来。
    ///
    /// 设计意图（对应计划书 3.1）：攻击方（CombatComponent）只依赖本接口，
    /// 不需要知道对方是英雄、小兵还是防御塔，也不需要持有 HealthComponent 的具体类型，
    /// 这样后续新增单位类型时战斗逻辑一行都不用改。
    ///
    /// 实现方：HealthComponent（唯一的实现者，所有可受伤实体都通过它转发伤害）。
    /// 注意：伤害结算的细节（护甲、免伤、护盾）不在这里定义，本接口只规定"入口"和"存活状态"。
    /// </summary>
    public interface IDamageable
    {
        /// <summary>
        /// 受到一次伤害。
        /// 约定：实现方内部必须保证「生命归零只触发一次死亡事件」，
        /// 并且在已死亡状态下重复调用不得再扣血（否则会出现死亡后被鞭尸、重复播死亡动画的问题）。
        /// </summary>
        /// <param name="amount">伤害数值，应为正数；具体是否受抗性/护盾影响由实现方决定。</param>
        void TakeDamage(float amount);

        /// <summary>
        /// 是否已死亡。
        /// 供攻击方在出手前做目标合法性校验，也供 UI 与 AI 判断是否要停止输出。
        /// </summary>
        bool IsDead { get; }
    }
}
