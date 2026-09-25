namespace MOBA.Skills
{
    /// <summary>
    /// 技能施法类型：决定"这次施法需要一个什么样的目标"。
    ///
    /// 这个枚举是技能系统的第一个分派点：SkillComponent 在释放阶段按它决定生成弹道还是生成范围场。
    /// 与 SkillEffectType（造成什么效果）刻意分开——两者是正交的两个维度，
    /// 例如「指向性 + 减速」与「非指向性 + 减速」应当是同一套效果代码、不同的施法路径。
    /// </summary>
    public enum SkillCastType
    {
        /// <summary>指向性：必须锁定一个合法的敌方单位，射程按"施法者 → 目标单位"计算。</summary>
        UnitTarget = 0,

        /// <summary>非指向性：只需要一个地面落点，射程按"施法者 → 落点"计算。</summary>
        GroundPoint = 1,
    }
}
