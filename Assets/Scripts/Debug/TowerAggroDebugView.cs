using System.Collections.Generic;
using UnityEngine;
using MOBA.Components;
using MOBA.Core;
using MOBA.Units;

// 【命名空间说明 · 必读】与同目录的 FSMDebugView / MatchDebugView 一致，本文件必须使用
// MOBA.Debugging 而不是 MOBA.Debug —— 原因见 FSMDebugView 顶部的长注释：
// 一旦存在命名空间 MOBA.Debug，MOBA 下所有文件里的 Debug.Log(...) 都会被解析成该命名空间，
// 一次性污染同根命名空间下的全部文件（本项目实测波及 14 个文件、67 处调用）。
namespace MOBA.Debugging
{
    /// <summary>
    /// 防御塔仇恨调试视图（阶段八新增，落地计划书里从阶段六 6B 顺延至今的可视化项）：
    /// 在 Scene 视图里把"每座塔此刻在打谁、为什么打它"画出来。
    ///
    /// 职责边界（与 FSMDebugView 同一约定）：
    /// 1. 【只读不写】不切目标、不调攻击、不碰任何会改变塔行为的方法 ——
    ///    因此可以随时挂上或摘下而不影响对局结果（符合 README §3.4 铁律 4「表现层可整体关闭」）；
    /// 2. 全部信息都经 `TowerController` 的公开只读接口（`ForcedAggroTarget` / `EngagementRange`）
    ///    与 `TargetingComponent.CurrentTarget` 获取，不读任何私有字段；
    /// 3. 用 OnDrawGizmos 而不是 OnGUI：这是"空间关系"的可视化（半径 / 连线），
    ///    画在 Scene 视图里才能与地形、塔位、兵线一起看；而且不需要 Canvas / EventSystem / 预制体。
    ///
    /// 【本视图存在的必要性】塔仇恨有四条规则（默认小兵 &gt; 英雄同级取最近 / 英雄抗塔强制转移 /
    /// 目标失效后按优先级重选 / 同一时刻单目标），其中"英雄抗塔"与"重选"都是**时间维度**上的行为：
    /// 只看 Console 日志很难判断"塔到底有没有在 0.25 秒内换目标"，而画出来一眼就能看出来。
    /// 因此本视图把"目标类型"编成颜色：
    ///   蓝色 = 默认优先级选中的小兵；品红 = 默认优先级选中的英雄；红色 = 强制仇恨（英雄抗塔的产物）。
    /// 看到红色连线就说明"有敌方英雄在塔下攻击了己方英雄"，这是规则 2 生效的直接证据。
    ///
    /// 【为什么单独成类而不是塞进 FSMDebugView】FSMDebugView 带 `[RequireComponent(typeof(EntityAIController))]`，
    /// 它是**挂在每个单位身上**的观察者；而塔仇恨是"建筑级"的全局关系，必须由**场景级单例**统一绘制。
    /// 两者的生命周期与挂载位置都不同，合并会逼着其中一个放弃自己的前提。
    /// </summary>
    [DisallowMultipleComponent]
    public class TowerAggroDebugView : MonoBehaviour
    {
        [Header("日志")]
        [Tooltip("某座塔的仇恨目标发生变化时，在 Console 输出一行记录。\n" +
                 "默认关闭：一局里目标切换可能很频繁，而「哪座塔在打谁」在 Scene 视图里已经看得见；" +
                 "排查「仇恨为什么没转移」时再打开。")]
        [SerializeField] private bool logAggroChanges = false;

        [Header("Gizmos")]
        [Tooltip("在 Scene 视图绘制交战半径、当前目标与优先级判定结果。")]
        [SerializeField] private bool drawGizmos = true;

        [Tooltip("交战半径（= CombatComponent.AttackRange，README §2.1.3 规定塔不持有独立索敌半径）线框球的颜色。")]
        [SerializeField] private Color engagementRangeColor = new Color(1f, 0.55f, 0.1f, 0.55f);

        [Tooltip("当前目标是【小兵】时的连线颜色（默认优先级的第一档）。")]
        [SerializeField] private Color minionPriorityColor = new Color(0.35f, 0.7f, 1f, 1f);

        [Tooltip("当前目标是【英雄】时的连线颜色（默认优先级的第二档）。")]
        [SerializeField] private Color heroPriorityColor = new Color(1f, 0.45f, 0.9f, 1f);

        [Tooltip("当前目标是【其它类型】时的连线颜色（默认优先级的兜底档）。")]
        [SerializeField] private Color otherPriorityColor = new Color(0.8f, 0.8f, 0.8f, 1f);

        [Tooltip("强制仇恨（英雄抗塔）生效时的连线颜色。看到这个颜色 = 规则 2 正在生效。")]
        [SerializeField] private Color forcedAggroColor = new Color(1f, 0.15f, 0.15f, 1f);

        [Header("刷新")]
        [Tooltip("重新扫描「场景里有哪些塔」的间隔（秒）。\n" +
                 "为什么要缓存而不是每帧扫：EntityRegistry.Snapshot() 会分配一个新数组，" +
                 "放在 OnDrawGizmos 里等于每帧产生垃圾，与「战斗稳态 GC ≈ 0」的验收目标冲突。\n" +
                 "塔在整局里不会被动态创建，1 秒一次的刷新已经远超需要。")]
        [Min(0.1f)]
        [SerializeField] private float towerRefreshInterval = 1f;

        /// <summary>当前跟踪的塔（缓存，按 towerRefreshInterval 周期刷新）。</summary>
        private readonly List<TowerController> towers = new List<TowerController>();

        /// <summary>上一轮各塔的仇恨目标，用于"仅变化时打印"的去重。下标与 towers 对齐。</summary>
        private readonly List<ITargetable> lastTargets = new List<ITargetable>();

        /// <summary>距离下次刷新塔列表的剩余时间（秒）。</summary>
        private float refreshTimer;

        /// <summary>当前跟踪的塔数量（只读），供调试与验收核对。</summary>
        public int TrackedTowerCount => towers.Count;

        /// <summary>刷新间隔（只读）。</summary>
        public float TowerRefreshInterval => Mathf.Max(0.1f, towerRefreshInterval);

        private void Update()
        {
            refreshTimer -= Time.deltaTime;
            if (refreshTimer > 0f)
            {
                return;
            }

            refreshTimer = TowerRefreshInterval;

            RefreshTowerList();

            if (logAggroChanges)
            {
                ReportAggroChanges();
            }
        }

        /// <summary>
        /// 重新扫描场景里的防御塔。
        /// 走 EntityRegistry（全项目"当前存活实体"的唯一清单）而不是 FindObjectsOfType：
        /// 后者会在每次刷新时遍历整个场景的所有对象，而注册表已经在 OnEnable/OnDisable 里维护好了。
        /// </summary>
        private void RefreshTowerList()
        {
            towers.Clear();

            EntityBase[] all = EntityRegistry.Snapshot();

            for (int i = 0; i < all.Length; i++)
            {
                EntityBase entity = all[i];

                if (entity == null || entity.EntityType != MOBA.Core.EntityType.Tower)
                {
                    continue;
                }

                TowerController tower = entity.GetComponent<TowerController>();
                if (tower != null)
                {
                    towers.Add(tower);
                }
            }

            // 目标缓存与塔列表对齐：列表长度变了就整体重建，
            // 否则"上一轮的目标"会错位到另一座塔上，日志会指错人。
            if (lastTargets.Count != towers.Count)
            {
                lastTargets.Clear();
                for (int i = 0; i < towers.Count; i++)
                {
                    lastTargets.Add(null);
                }
            }
        }

        /// <summary>
        /// 对比本轮与上一轮的仇恨目标，变化时输出一条记录。
        /// 只报【变化】而不是每轮都报：塔的目标在一次交战里可能稳定几十秒，
        /// 每 1 秒打一条等于把 Console 刷满无信息量的重复行。
        /// </summary>
        private void ReportAggroChanges()
        {
            for (int i = 0; i < towers.Count; i++)
            {
                TowerController tower = towers[i];
                if (tower == null)
                {
                    continue;
                }

                ITargetable current = ResolveCurrentTarget(tower);

                if (ReferenceEquals(current, lastTargets[i]))
                {
                    continue;
                }

                lastTargets[i] = current;

                string channel = tower.ForcedAggroTarget != null ? "强制仇恨（英雄抗塔）" : "默认优先级";
                Debug.Log(
                    $"[TowerAggroDebugView] {tower.name} 仇恨目标 → {DescribeTarget(current)}｜判定通道 {channel}" +
                    $"｜交战半径 {tower.EngagementRange:F1} 米", tower);
            }
        }

        /// <summary>
        /// 取某座塔当前的仇恨目标。
        /// 用 TowerController.ForcedAggroTarget 优先、否则读 TargetingComponent.CurrentTarget ——
        /// 两者合起来正好覆盖"强制仇恨"与"默认优先级"两条通道，与 TowerController 内部的实际选择一致
        /// （强制仇恨一旦置位就跳过默认优先级选择）。
        /// </summary>
        /// <param name="tower">目标塔。</param>
        /// <returns>当前仇恨目标；没有则返回 null。</returns>
        private static ITargetable ResolveCurrentTarget(TowerController tower)
        {
            if (tower == null)
            {
                return null;
            }

            ITargetable forced = tower.ForcedAggroTarget;
            if (forced != null)
            {
                return forced;
            }

            EntityBase owner = tower.GetComponent<EntityBase>();
            TargetingComponent targeting = owner != null ? owner.Targeting : null;
            return targeting != null ? targeting.CurrentTarget : null;
        }

        /// <summary>
        /// Scene 视图可视化：对每座塔画交战半径 + 到当前目标的连线。
        /// 用 OnDrawGizmos（而非 Selected）：README 要求"作用范围需白盒可视化便于验证与调试"，
        /// 验证塔仇恨时不该先选中某座塔才看得到另外五座在打谁。
        /// 编辑模式下 towers 为空（Update 不执行），因此不会画出任何东西。
        /// </summary>
        private void OnDrawGizmos()
        {
            if (!drawGizmos || towers.Count == 0)
            {
                return;
            }

            // 抬升 0.5 米：塔的根节点是贴地原点，抬一点避免球与连线被地面 Z-Fighting 吃掉。
            Vector3 lift = Vector3.up * 0.5f;

            for (int i = 0; i < towers.Count; i++)
            {
                TowerController tower = towers[i];
                if (tower == null)
                {
                    continue;
                }

                Vector3 origin = tower.transform.position + lift;

                // 橙色线框球 = 交战半径（= 攻击距离）。README §2.1.3：
                // 塔不持有独立索敌半径，"能锁定的范围"必须等于"能打到的范围"。
                Gizmos.color = engagementRangeColor;
                Gizmos.DrawWireSphere(origin, tower.EngagementRange);

                ITargetable current = ResolveCurrentTarget(tower);
                Transform targetTransform = current != null ? current.TargetTransform : null;

                if (targetTransform == null)
                {
                    // 没目标时画一个实心小点，表示"这座塔还活着、只是当前无仇恨对象"。
                    // 与"塔被摧毁"（画不出任何东西）在视觉上区分开。
                    Gizmos.color = Color.gray;
                    Gizmos.DrawSphere(origin, 0.25f);
                    continue;
                }

                Vector3 targetPosition = targetTransform.position + lift;

                // 连线颜色 = 优先级判定结果（见类注释的颜色约定）。
                Gizmos.color = ResolveLinkColor(tower, current);
                Gizmos.DrawLine(origin, targetPosition);

                // 目标头顶的小球：加粗"当前被这座塔锁定的是谁"，避免多塔重叠时看不清。
                Gizmos.DrawWireSphere(targetPosition, 0.45f);
            }
        }

        /// <summary>
        /// 决定连线的颜色：强制仇恨优先，其余按目标的单位类型分档。
        /// 颜色语义与类注释一致，是"规则 1 / 规则 2 哪一条在生效"的视觉答案。
        /// </summary>
        /// <param name="tower">绘制中的塔。</param>
        /// <param name="current">该塔当前的仇恨目标。</param>
        /// <returns>连线颜色。</returns>
        private Color ResolveLinkColor(TowerController tower, ITargetable current)
        {
            if (tower.ForcedAggroTarget != null)
            {
                return forcedAggroColor;
            }

            if (current is EntityBase entity)
            {
                switch (entity.EntityType)
                {
                    case MOBA.Core.EntityType.Minion:
                        return minionPriorityColor;

                    case MOBA.Core.EntityType.Hero:
                        return heroPriorityColor;

                    default:
                        return otherPriorityColor;
                }
            }

            return otherPriorityColor;
        }

        /// <summary>生成目标的简短描述，仅用于日志。</summary>
        private static string DescribeTarget(ITargetable target)
        {
            if (target == null)
            {
                return "无";
            }

            if (target is Component component)
            {
                return $"{component.name}({target.Team})";
            }

            return $"ITargetable({target.Team})";
        }
    }
}
