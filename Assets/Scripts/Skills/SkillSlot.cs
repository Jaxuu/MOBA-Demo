namespace MOBA.Skills
{
    /// <summary>
    /// 技能槽位。
    ///
    /// 【为什么必须显式写出 0/1/2/3】SkillComponent 用 (int)slot 直接做 nextCastTime 数组的下标，
    /// 也就是"枚举值"与"数组下标"强绑定。若不写显式值，日后有人在中间插入一个成员，
    /// 所有已有槽位的冷却记录会整体错位——这种错误不报异常，只表现为"技能冷却串了"。
    /// 显式编号让这种改动必须由人手改数字，无法"顺手"发生。
    ///
    /// V1 只实现 Q / W 两个槽位（对应 README 首批技能规划），E / R 为槽位机制预留，
    /// 配置为空时 SkillComponent.TryCast 会拒绝并告警一次。
    /// </summary>
    public enum SkillSlot
    {
        /// <summary>槽位 1：指向性非穿透弹道（火球）。</summary>
        Q = 0,

        /// <summary>槽位 2：非指向性 AOE 减速圈。</summary>
        W = 1,

        /// <summary>槽位 3：预留，V1 无配置。</summary>
        E = 2,

        /// <summary>槽位 4：预留，V1 无配置。</summary>
        R = 3,
    }
}
