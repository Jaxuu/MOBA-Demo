namespace MOBA.Core
{
    /// <summary>
    /// 单位类型。用于区分不同实体的行为策略：
    /// Hero / Minion 可移动并携带 FSM，Tower / Base 为固定建筑，不挂载 MovementComponent。
    /// 显式指定枚举值，保证已序列化到场景与预制体中的数据稳定。
    /// </summary>
    public enum EntityType
    {
        /// <summary>英雄单位，由玩家或 AI 直接操控。</summary>
        Hero = 0,

        /// <summary>小兵单位，沿兵线推进并自动交战。</summary>
        Minion = 1,

        /// <summary>防御塔，固定位置索敌与攻击。</summary>
        Tower = 2,

        /// <summary>基地，对局胜负判定的核心目标。</summary>
        Base = 3
    }
}
