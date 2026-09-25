namespace MOBA.Components
{
    /// <summary>
    /// 状态效果类型（Buff/Debuff）。
    ///
    /// 刻意只有三种：依据 README §3.5 的设计边界「Buff/Debuff 只做基础状态效果（减速、眩晕 + 护盾），
    /// 不做完整属性修饰器聚合体系」。枚举每多一项，就意味着 BuffComponent 里要多一套
    /// 刷新策略 + 到期恢复 + 与其他效果互斥的规则，而 V1 没有任何技能会用到第四种。
    ///
    /// 与 SkillEffectType 的区别：SkillEffectType 是"技能想造成什么效果"（数据层视角），
    /// 本枚举是"单位身上挂着什么状态"（组件层视角）。两者当前一一对应，但刻意不合并——
    /// 日后装备/光环等非技能来源的 buff 只会增加本枚举，不会污染技能配置表。
    /// </summary>
    public enum BuffType
    {
        /// <summary>减速：修改移动速度，到期精确恢复原值。</summary>
        Slow = 0,

        /// <summary>眩晕：禁止移动与攻击，到期自动解除。</summary>
        Stun = 1,

        /// <summary>护盾：吸收伤害，耗尽或到期移除（数值在 HealthComponent，时长在 BuffComponent）。</summary>
        Shield = 2,
    }
}
