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

        /// <summary>
        /// 强化下一次普攻（阶段八新增）：给【自身】挂一层一次性状态，
        /// 下一次普攻结算时附加额外伤害，并在命中后对目标施加沉默；命中即消耗。
        ///
        /// 【为什么它是一个"效果"而不是一个 Buff 类型】它的生命周期终点不是"到期"，
        /// 而是"下一次普攻命中"——由 CombatComponent 在结算伤害时主动取走（TryConsume）。
        /// Buff 容器只负责保管与超时兜底（防止攒着不放）。
        /// </summary>
        EmpowerNextAttack = 4,

        /// <summary>
        /// 斩杀伤害（阶段八新增）：伤害 = 基础值 + 系数 × 目标【已损失生命值】。
        /// 满血目标只吃基础值，残血目标吃满加成 —— 这正是"斩杀"的手感来源。
        /// </summary>
        ExecuteDamage = 5,

        /// <summary>治疗：走 HealthComponent.Heal（血量不会超过上限，死亡目标不生效）。</summary>
        Heal = 6,

        /// <summary>加速：走 BuffComponent.ApplyHaste（只经 MovementComponent.SetMoveSpeed 改移速）。</summary>
        Haste = 7,

        /// <summary>
        /// 空效果：仅用于 SkillData 的【附带效果】槽位表示"没有附带效果"。
        ///
        /// 【为什么值必须追加在末尾（= 8）而不是放到开头当 0】枚举值会被工具按 intValue 落盘进技能资产。
        /// 把 None 插到 0 会让所有旧资产里的 Damage（0）被重新解释成"空效果"——技能照放、伤害为零、
        /// 且不报任何错，是最难排查的一类回归。因此新成员一律追加在末尾。
        /// </summary>
        None = 8,
    }
}
