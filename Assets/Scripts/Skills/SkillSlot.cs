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
    /// 【阶段八起四个槽位全部投入使用】阶段六只实现 Q / W 两个槽位；
    /// 阶段八把玩家英雄扩展为 Q / W / E / R 四槽（盖伦式机制，见 README §2.1.6），
    /// 9 个 AI 英雄则按固定 seed 从技能池抽取 2 个挂到 Q / W 槽。
    /// 未配置的槽位在 SkillComponent.TryCast 里会被拒绝并告警一次（不是静默失败）。
    /// </summary>
    public enum SkillSlot
    {
        /// <summary>槽位 1：玩家 = 致命打击（自身加速 + 强化下次普攻）；AI = 技能池随机技能。</summary>
        Q = 0,

        /// <summary>槽位 2：玩家 = 勇气（立即获得护盾）；AI = 技能池随机技能。</summary>
        W = 1,

        /// <summary>槽位 3：玩家 = 审判（以自身为中心并跟随的持续 AOE）。</summary>
        E = 2,

        /// <summary>槽位 4：玩家 = 德玛西亚正义（指向性斩杀）。</summary>
        R = 3,
    }
}
