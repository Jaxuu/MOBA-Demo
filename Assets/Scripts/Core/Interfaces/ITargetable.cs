using UnityEngine;

namespace MOBA.Core
{
    /// <summary>
    /// 可被选为目标接口：把"能否被选为攻击目标"从具体单位类型中抽象出来。
    ///
    /// 设计意图（对应计划书 3.1）：TargetingComponent 只依赖本接口做搜索与校验，
    /// CombatComponent 只依赖本接口取位置算距离，二者都不需要知道目标的真实类型。
    /// 后续加新单位（野怪、召唤物、眼位）时，只要实现本接口即可被自动纳入索敌范围。
    ///
    /// 实现方：EntityBase（作为实体身份的聚合入口统一实现，转发给内部组件）。
    /// 注意：本接口刻意不包含 Transform 之外的任何表现信息，避免逻辑层被表现层污染。
    /// </summary>
    public interface ITargetable
    {
        /// <summary>
        /// 目标的位置载体。
        /// 用途：计算攻击距离、让攻击方转向目标、以及让攻击方追到"攻击距离内"的位置。
        /// 约定：应返回实体自身的 Transform（通常是模型根节点），不返回子节点或临时对象。
        /// </summary>
        Transform TargetTransform { get; }

        /// <summary>
        /// 目标所属阵营。
        /// 用途：目标合法性判断——只有阵营不同的单位才互为敌人（Neutral 的具体规则由 TargetingComponent 决定）。
        /// </summary>
        TeamType Team { get; }

        /// <summary>
        /// 当前是否仍是合法目标。
        /// 用途：一次集中校验「存活状态 + 是否被销毁 + 是否可被选中（如隐身、无敌、不可选中）」，
        /// 让调用方不必分别检查多个条件，避免遗漏。
        /// 约定：目标已死亡、GameObject 已被销毁、或处于不可选中状态时，都应返回 false。
        /// </summary>
        bool IsValidTarget { get; }
    }
}
