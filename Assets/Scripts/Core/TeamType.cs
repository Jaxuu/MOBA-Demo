namespace MOBA.Core
{
    /// <summary>
    /// 阵营类型。目标合法性判断（ITargetable / TargetingComponent）以此为依据：
    /// 只有阵营不同的单位才互为敌人；Neutral（野怪、无归属单位）【不会】被任何一方视为敌人
    /// （README 2.2 规定不可攻击中立单位，判定入口在 TargetingComponent.IsEnemy）。
    /// 显式指定枚举值，避免后续插入成员时打乱已有配置资产的序列化数据。
    /// </summary>
    public enum TeamType
    {
        /// <summary>玩家方阵营。</summary>
        Player = 0,

        /// <summary>敌方阵营。</summary>
        Enemy = 1,

        /// <summary>中立阵营（野怪、无归属单位）。</summary>
        Neutral = 2
    }
}
