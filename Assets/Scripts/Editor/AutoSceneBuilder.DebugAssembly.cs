using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using MOBA.Debugging;
using MOBA.Gameplay;

namespace MOBA.Editor
{
    /// <summary>
    /// AutoSceneBuilder 的「调试视图装配」分册（阶段八自审补齐）。
    ///
    /// 【为什么需要这个分册】阶段八自审时发现两个调试视图此前都不在工具的管理范围内：
    ///   · `MatchDebugView` 挂在一个名为 `GameManager` 的场景根对象上 —— 那是阶段四/五时代
    ///     手工装配留下的宿主，既不在 `ManagedRootNames` 里（工具从不清理它），也没有任何代码引用它；
    ///   · `TowerAggroDebugView` 是计划书明确要求"随 6B 一并落地"的可视化项，但**从未被创建**。
    /// 两者都直接违背 README §6.1 铁律 1「所有场景装配必须通过 Editor 脚本完成，严禁手动拖拽依赖」——
    /// 而且症状是"场景重建后调试能力悄悄消失"，没有任何报错。
    ///
    /// 因此这里把**场景级**调试视图统一收进一个工具管理的根对象 `DebugViews`：
    /// 它进了 `ManagedRootNames`，重复执行会自动先删后建，调试视图与战场、UI 一样保持幂等。
    ///
    /// 【为什么 FSMDebugView 不在这里】它带 `[RequireComponent(typeof(EntityAIController))]`，
    /// 设计上就是**挂在每个单位身上**的观察者（观察同一个 GameObject 上的 FSM），
    /// 无法做成场景级单例。它的装配因此分散在两处：
    ///   · 小兵：挂在 MinionPrefab 上（阶段三起的既有做法，工具不动它）；
    ///   · AI 英雄：由 CreateHeroInstance 按实例挂载（阶段八自审新增，见该方法的第 8 步）。
    /// </summary>
    public static partial class AutoSceneBuilder
    {
        /// <summary>
        /// 场景级调试视图的根对象名（进入本工具的接管清单）。
        /// 用独立根节点而不是挂在 Battlefield 上：调试视图可以整体关掉或删掉而不影响对局，
        /// 与"逻辑对象"分开更符合"表现/调试层可整体关闭"这条铁律的意图。
        /// </summary>
        private const string DebugViewsRootName = "DebugViews";

        /// <summary>
        /// 阶段四/五遗留的调试视图宿主对象名（`MatchDebugView` 曾经挂在它身上）。
        /// 保留在接管清单里只为**清掉旧场景的残留**：新的 `MatchDebugView` 由本工具挂到 DebugViews 上，
        /// 规则自限（清掉之后不会再被创建）。已确认全项目没有任何代码引用 `GameManager`。
        /// </summary>
        private const string LegacyGameManagerName = "GameManager";

        /// <summary>
        /// 装配场景级调试视图：`MatchDebugView`（对局状态浮层 + 胜负快捷按钮）与
        /// `TowerAggroDebugView`（塔仇恨可视化，阶段八新增）。
        ///
        /// 【为什么它们是"表现/调试层"而不是"逻辑层"】两个视图都只读不写：
        /// 不切目标、不调攻击、不改任何战斗数值。关掉它们，对局逻辑与胜负结果完全一致
        /// （README §3.4 铁律 4）——这正是它们可以"整体关闭"的依据。
        /// `MatchDebugView` 上的两个"摧毁基地"按钮是**调试用**的胜负快捷入口，
        /// 它调用的是 MatchController 的公开方法，仍然没有绕过任何规则。
        ///
        /// 【依赖注入：两个视图都不需要注入任何引用】
        /// `MatchDebugView` 读的是静态事件 `BaseCoreController.OnBaseDestroyed` 与静态方法
        /// `MatchController.GetOpponentTeam`，并自己用 `FindObjectsOfType<BaseCoreController>()` 找基地；
        /// `TowerAggroDebugView` 运行时经 `EntityRegistry` 自己找塔。两者都不持有 `MatchController` 引用。
        ///
        /// 【本步骤曾注入一个不存在的字段 —— 实机踩到】这里此前写着
        /// `AssignObjectReference(matchView, "matchController", matchController)`，
        /// 但 `MatchDebugView` 从来没有声明过 `matchController` 字段（它的注释里写着"必须注入"，
        /// 属于设计意图没落到代码上）。后果是**每次组装都刷一条红色的假错误**
        /// 「在 MatchDebugView 上找不到序列化字段「matchController」」——
        /// 这类假错误比没有校验更糟：它会训练人忽略 Console，而本项目整套流程都依赖 Console 暴露真问题。
        /// 现在直接删掉这次注入（视图确实不需要它），并把参数一并去掉，避免留一个"看着像要用"的死参数。
        /// </summary>
        /// <param name="notes">组装说明收集器。</param>
        private static void BuildDebugViews(List<string> notes)
        {
            GameObject root = CreateRootObject(DebugViewsRootName, Vector3.zero);

            // 对局状态浮层：用 OnGUI 实现（调试工具不需要 Canvas / EventSystem / 预制体）。
            // 无引用依赖，因此不做任何注入。
            Undo.AddComponent<MatchDebugView>(root);

            // 塔仇恨可视化：6 座塔"此刻在打谁、走的是哪条优先级通道"，在 Scene 视图里一眼可见。
            Undo.AddComponent<TowerAggroDebugView>(root);

            notes.Add(
                $"{DebugViewsRootName} 已装配（MatchDebugView 对局状态浮层 + TowerAggroDebugView 塔仇恨可视化）；" +
                $"并把阶段四遗留的 {LegacyGameManagerName} 纳入接管清单（其上的 MatchDebugView 已由本工具重建）");
        }
    }
}
