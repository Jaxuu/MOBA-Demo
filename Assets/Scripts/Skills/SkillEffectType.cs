namespace MOBA.Skills
{
    /// <summary>
    /// 技能主效果类型（V1 单主效果模型）。
    ///
    /// 【为什么是"单主效果"而不是效果数组】依据 README §3.5 的设计边界：
    /// 「Buff/Debuff 只做基础状态效果，不做完整属性修饰器聚合体系」。
    /// V1 的两个技能正好各自对应一种效果（Q = Damage、W = Slow），单主效果已完全覆盖需求，
    /// 且策划在 Inspector 里看到的是一个下拉框 + 一组数值，而不是一个需要理解"原子效果组合"的数组。
    ///
    /// 【演进路径已留好】若日后要做「伤害 + 减速」复合技能，只需把 SkillData.EffectType 升级为
    /// SkillEffectData[]，SkillEffectResolver.Apply 的调用点（Projectile / AreaEffectZone）无需改动——
    /// 它们只调一个统一入口，不关心效果有几种。
    /// </summary>
    public enum SkillEffectType
    {
        /// <summary>伤害：走 HealthComponent.TakeDamage（护盾优先吸收）。</summary>
        Damage = 0,

        /// <summary>护盾：走 HealthComponent.SetShield（吸收伤害，耗尽或到期移除）。</summary>
        Shield = 1,

        /// <summary>减速：走 BuffComponent.ApplySlow（只经 MovementComponent.SetMoveSpeed 改移速）。</summary>
        Slow = 2,

        /// <summary>眩晕：走 BuffComponent.ApplyStun（移动锁 + 攻击锁，到期自动解锁）。</summary>
        Stun = 3,
    }
}
