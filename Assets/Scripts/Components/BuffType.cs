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

        /// <summary>
        /// 加速（阶段八新增）：提高移动速度，到期精确恢复原值。
        /// 与 Slow 共用同一个原速快照，两者的系数相乘（见 BuffComponent.ApplySpeedModifier）。
        /// </summary>
        Haste = 3,

        /// <summary>
        /// 沉默（阶段八新增）：禁止施法，到期自动解除。
        ///
        /// 【为什么沉默不像眩晕那样上锁】沉默只封"施法"这一条通道，移动与普攻照常。
        /// 若也去锁 Movement / Combat，它就会与眩晕、施法前摇抢同一把锁，
        /// 三种来源互相解锁的破绽会成倍增加。因此它只在 SkillComponent.TryCast 的
        /// 自身状态校验链上拦一道 —— 那里本来就是"能不能施法"的唯一判定点。
        /// </summary>
        Silence = 4,

        /// <summary>
        /// 强化下一次普攻（阶段八新增）：一次性状态，在下一次普攻命中时被取走。
        /// 与 BuffType 的其它成员不同，它的终点不是"到期"而是"被消耗"，到期只是兜底超时。
        /// </summary>
        EmpowerNextAttack = 5,
    }
}
