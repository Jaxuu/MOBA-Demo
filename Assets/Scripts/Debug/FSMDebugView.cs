using UnityEngine;
using MOBA.AI;
using MOBA.Components;
using MOBA.Core;

// 【命名空间说明 · 必读】本文件位于 Assets/Scripts/Debug/，但命名空间刻意【不】取 MOBA.Debug，
// 而是 MOBA.Debugging —— 这是本项目"命名空间 = MOBA.<目录名>"约定的一处必要例外。
//
// 原因：C# 的名字查找中，外层命名空间的成员【优先于】using 导入。
// 一旦存在命名空间 MOBA.Debug，那么 MOBA 下所有文件里的 `Debug.Log(...)`
// 都会把 `Debug` 解析成这个命名空间，从而报 CS0234（命名空间 MOBA.Debug 中不存在 Log）——
// 也就是说，这个名字会一次性污染同根命名空间下的全部文件（本项目实测波及 14 个文件、67 处调用）。
// 因此：目录名保持 Debug/（与计划书第 2 节的目录结构一致），命名空间改为 MOBA.Debugging。
//
// 本目录后续新增的文件（计划书里的 EntityDebugView / NavMeshDebugView）必须沿用 MOBA.Debugging，
// 不要"顺手改回" MOBA.Debug，否则整个项目会立刻编译失败。
namespace MOBA.Debugging
{
    /// <summary>
    /// FSM 调试视图：把 AI 的内部状态翻译成人可读的日志与 Scene 视图图形，
    /// 用于在 Unity 中直观验证阶段三的行为是否符合计划（寻路 → 索敌 → 追击 → 攻击 → 返回兵线）。
    ///
    /// 职责边界：
    /// 1. 本类【只读不写】——不调用任何会改变 AI 行为的方法（不切状态、不设目标、不移动），
    ///    因此可以随时挂上或摘下而不影响逻辑，是纯粹的观察者；
    /// 2. 本类不参与任何战斗或寻路决策，全部信息都经 EntityAIController 的公开只读接口获取；
    /// 3. 所有可视化与日志能力都暴露 [SerializeField] 开关（项目约定），
    ///    单位数量多时可在 Inspector 逐个关掉，避免 Scene 视图与 Console 被刷满。
    ///
    /// 与其它组件自带 Gizmos 的分工（避免重复绘制）：
    /// TargetingComponent 画"锁定的目标连线"、CombatComponent 画"攻击距离"。
    /// 本类补的是 AI 视角独有的三个量：
    ///   1. 索敌半径（黄）——多大范围内会发现敌人；
    ///   2. 最大追击半径（红）——离目标多远就放弃；
    ///   3. 牵引极限（青）——被牵着离开追击起点多远就强制返回兵线。
    /// 黄色与红色之间的环带是【滞回余量】，是验证"目标在半径边缘飘动不会引起状态震荡"的依据；
    /// 青色球则是验证"被引离路线超过限制"这一条的依据。
    /// </summary>
    [RequireComponent(typeof(EntityAIController))]
    [DisallowMultipleComponent]
    public class FSMDebugView : MonoBehaviour
    {
        [Header("日志")]
        [Tooltip("状态或目标发生变化时，在 Console 输出一行快照（单位名 + 当前状态 + 当前目标）。")]
        [SerializeField] private bool logStateChanges = true;

        [Header("Gizmos")]
        [Tooltip("在 Scene 视图绘制索敌半径、最大追击半径与目标连线。")]
        [SerializeField] private bool drawGizmos = true;

        [Tooltip("索敌半径（Detection Range）线框球的颜色。")]
        [SerializeField] private Color detectionRangeColor = Color.yellow;

        [Tooltip("最大追击半径（索敌半径 × 追击系数）线框球的颜色。")]
        [SerializeField] private Color chaseRangeColor = Color.red;

        [Tooltip("绘制牵引极限球（与追击起点锚点的最大允许距离）。\n" +
                 "用途：验证「被引离路线超过限制」这一条——红色球管的是「离目标太远」，\n" +
                 "青色球管的是「被牵着离开兵线太远」，两个闸门互相补充。")]
        [SerializeField] private bool drawLeashGizmo = true;

        [Tooltip("牵引极限线框球的颜色。")]
        [SerializeField] private Color leashRangeColor = Color.cyan;

        [Tooltip("指向当前目标的连线颜色。")]
        [SerializeField] private Color targetLineColor = Color.red;

        /// <summary>被观察的 AI 控制器。RequireComponent 已保证其存在。</summary>
        private EntityAIController controller;

        /// <summary>上一次输出的状态名，用于"仅在变化时打印"的去重。null 表示尚未输出过。</summary>
        private string lastStateName;

        /// <summary>上一次输出的目标名，用于"仅在变化时打印"的去重。</summary>
        private string lastTargetName;

        /// <summary>
        /// 缓存被观察的控制器。
        /// 这里只做一次尝试性缓存：编辑模式下 Awake 不会执行，Gizmos 需要走 <see cref="ResolveController"/> 兜底。
        /// </summary>
        private void Awake()
        {
            controller = GetComponent<EntityAIController>();

            if (controller == null)
            {
                // 正常由 RequireComponent 保证存在，这里仅作防御性检查（例如组件被运行时移除）。
                Debug.LogError(
                    $"[FSMDebugView] {name} 上找不到 EntityAIController，调试视图已禁用。", this);
                enabled = false;
            }
        }

        /// <summary>
        /// 每帧比对状态与目标，仅在发生变化时输出一行日志。
        /// 为什么不每帧打印：10 个小兵 × 60 帧 = 每秒 600 行，Console 会完全不可读；
        /// 而"变化时打印"恰好覆盖了状态机调试最需要的信息——迁移路径与迁移时刻。
        /// </summary>
        private void Update()
        {
            EntityAIController ai = ResolveController();
            if (ai == null)
            {
                return;
            }

            string stateName = DescribeCurrentState(ai);
            string targetName = DescribeCurrentTarget(ai);

            if (!logStateChanges)
            {
                // 关闭日志时仍同步缓存：否则重新打开开关的那一帧会把"没变"误报成一次状态跳变。
                lastStateName = stateName;
                lastTargetName = targetName;
                return;
            }

            bool stateChanged = stateName != lastStateName;
            bool targetChanged = targetName != lastTargetName;

            if (!stateChanged && !targetChanged)
            {
                return;
            }

            string previousStateName = lastStateName;
            lastStateName = stateName;
            lastTargetName = targetName;

            if (stateChanged)
            {
                // 状态迁移是最重要的信息，用箭头把"从哪来、到哪去"一次说清。
                string fromName = previousStateName ?? "未初始化";
                Debug.Log(
                    $"[FSMDebugView] {name} 状态：{fromName} → {stateName} ｜ 目标：{targetName}", this);
            }
            else
            {
                Debug.Log(
                    $"[FSMDebugView] {name} 状态：{stateName} ｜ 目标变化 → {targetName}", this);
            }
        }

        /// <summary>
        /// Scene 视图可视化：索敌半径（黄）、最大追击半径（红）、指向当前目标的连线（红）。
        /// 用 OnDrawGizmos 而不是 OnDrawGizmosSelected：本组件的用途就是"一眼看到所有单位的 AI 覆盖范围"，
        /// 若必须逐个选中才显示，就无法直观判断兵线交锋时双方索敌范围是否重叠。
        /// </summary>
        private void OnDrawGizmos()
        {
            if (!drawGizmos)
            {
                return;
            }

            EntityAIController ai = ResolveController();
            if (ai == null)
            {
                return;
            }

            float detectionRadius = ResolveDetectionRadius(ai);
            float chaseRadius = detectionRadius * ai.ChaseAbandonRangeFactor;
            float leashRadius = detectionRadius * ai.ChaseLeashRangeFactor;

            Vector3 origin = transform.position;

            // 黄色线框球 = 索敌半径：进入该范围才会"发现敌人"（MoveState / IdleState 的 FindNearestEnemy）。
            if (detectionRadius > 0f)
            {
                Gizmos.color = detectionRangeColor;
                Gizmos.DrawWireSphere(origin, detectionRadius);
            }

            // 红色线框球 = 最大追击半径：与目标的距离超过它就会放弃追击、返回兵线。
            // 它与黄色球之间的环带即【滞回余量】——目标在该环带内飘动不会触发任何状态切换，
            // 这正是"Move ↔ Chase 不会每帧互切"的可视化依据（见 README 3.2 行为优先级第 3 条）。
            if (chaseRadius > 0f)
            {
                Gizmos.color = chaseRangeColor;
                Gizmos.DrawWireSphere(origin, chaseRadius);
            }

            // 青色线框球 = 牵引极限：与【追击起点锚点】的距离超过它就会强制放弃目标。
            // 注意它是画在本单位当前位置上的：锚点本身的位置随追击开始时刻变化，
            // 因此这个球表达的是"当前允许的活动半径"，而不是锚点的绝对位置。
            if (drawLeashGizmo && leashRadius > 0f)
            {
                Gizmos.color = leashRangeColor;
                Gizmos.DrawWireSphere(origin, leashRadius);
            }

            // 红色连线 = 指向当前有效目标的连线。
            ITargetable target = ResolveCurrentTarget(ai);
            if (target == null)
            {
                return;
            }

            Transform targetTransform = target.TargetTransform;
            if (targetTransform == null)
            {
                return;
            }

            // 抬升 0.5 米避免连线被地面 Z-Fighting 吃掉。
            Gizmos.color = targetLineColor;
            Gizmos.DrawLine(origin + Vector3.up * 0.5f, targetTransform.position + Vector3.up * 0.5f);
        }

        /// <summary>
        /// 取被观察的控制器。
        /// 编辑模式下 Awake 不执行、<see cref="controller"/> 仍为 null，此时退化为直接查找，
        /// 保证未进入播放模式时也能看到 Gizmos（便于摆预制体与核对半径配置）。
        /// </summary>
        private EntityAIController ResolveController()
        {
            if (controller != null)
            {
                return controller;
            }

            return GetComponent<EntityAIController>();
        }

        /// <summary>
        /// 取本单位的实体身份入口。
        /// 运行期优先走控制器的 Owner；编辑模式下控制器尚未 Awake，退化为直接查找同物体上的实体组件。
        /// </summary>
        private static EntityBase ResolveOwner(EntityAIController ai)
        {
            EntityBase owner = ai.Owner;
            if (owner != null)
            {
                return owner;
            }

            return ai.GetComponent<EntityBase>();
        }

        /// <summary>
        /// 取索敌半径。
        /// 优先走控制器（读到的是 EntityStatsData 注入后的真实值）；
        /// 编辑模式下取不到时退化为直接读同物体上的索敌组件（此时读到的是预制体上的序列化默认值）。
        /// </summary>
        private static float ResolveDetectionRadius(EntityAIController ai)
        {
            float radius = ai.DetectionRadius;
            if (radius > 0f)
            {
                return radius;
            }

            TargetingComponent targeting = ai.GetComponent<TargetingComponent>();
            return targeting != null ? targeting.DetectionRadius : 0f;
        }

        /// <summary>
        /// 取当前锁定的有效目标；不存在或已失效时返回 null。
        /// </summary>
        private static ITargetable ResolveCurrentTarget(EntityAIController ai)
        {
            EntityBase owner = ResolveOwner(ai);
            if (owner == null)
            {
                return null;
            }

            TargetingComponent targeting = owner.Targeting;
            if (targeting == null)
            {
                return null;
            }

            ITargetable current = targeting.CurrentTarget;
            if (current == null)
            {
                return null;
            }

            // 接口引用在 GameObject 被销毁后不会变成 null，必须先做存活检查再访问其属性，
            // 否则日志与 Gizmos 会抛 MissingReferenceException，把真正的调试信息淹没。
            if (current is UnityEngine.Object unityTarget && unityTarget == null)
            {
                return null;
            }

            return current;
        }

        /// <summary>生成当前状态名；状态机尚未启动时返回"未初始化"而不是 null。</summary>
        private static string DescribeCurrentState(EntityAIController ai)
        {
            StateMachine machine = ai.StateMachine;

            // 状态机在 Awake 中创建，但在 InitializeAI 之前 CurrentState 仍为 null。
            if (machine == null || machine.CurrentState == null)
            {
                return "未初始化";
            }

            return machine.CurrentState.GetType().Name;
        }

        /// <summary>生成当前目标的简短描述（名字 + 阵营），供日志输出。</summary>
        private static string DescribeCurrentTarget(EntityAIController ai)
        {
            ITargetable target = ResolveCurrentTarget(ai);

            if (target == null)
            {
                return "无";
            }

            // 目标可能是任意实现 ITargetable 的类型；只取"名字 + 阵营"这两个对调试最有用的信息，
            // 不打印整个对象（默认 ToString 只会输出类型名，信息量低）。
            if (target is Component component)
            {
                return $"{component.name}({target.Team})";
            }

            return $"ITargetable({target.Team})";
        }
    }
}
