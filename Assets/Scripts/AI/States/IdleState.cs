using UnityEngine;
using MOBA.Components;
using MOBA.Core;

namespace MOBA.AI
{
    /// <summary>
    /// 待机状态：无目标时观察周围环境，并按结果分流到"推进"或"追击"。
    ///
    /// 职责边界（对应计划书 3.4「IdleState：无目标时等待或检查下一路径点」）：
    /// 本状态【只做感知与分流】，不自己移动、不自己攻击——它把决定权交给
    /// EntityAIController.TryEnterMoveState / TryEnterChaseState，由控制器完成状态切换。
    /// 这样"谁能切状态"只有控制器一个出口，状态之间不会互相直接引用。
    ///
    /// 为什么 IdleState 在 FSM 中并非可有可无：
    /// 它是"没有目标"时的统一收敛点。追击目标死亡、超出追击范围、攻击结束等
    /// 各种路径最终都回到这里重新判断该推进还是该打架，避免每个状态各自重复写一套分流逻辑。
    /// </summary>
    public class IdleState : IState
    {
        /// <summary>状态上下文。所有组件访问都经它拿到，状态本身不缓存任何组件引用。</summary>
        private readonly EntityAIController context;

        /// <summary>是否已就"缺少索敌组件"报过警告，防止每帧刷屏。</summary>
        private bool hasReportedMissingTargeting;

        /// <summary>
        /// 构造状态。
        /// 用构造函数注入上下文（而不是让状态自己去 FindObjectOfType）：
        /// 保证状态与实体严格一对一绑定，也让它可以在不启动 Unity 的场景下被构造与测试。
        /// </summary>
        /// <param name="context">所属 AI 控制器，必须非空。</param>
        public IdleState(EntityAIController context)
        {
            this.context = context;
        }

        /// <summary>
        /// 进入待机。
        /// 刻意留空：待机状态不申请任何资源（不订阅事件、不下达移动指令），因此没有"进入时"的动作要做。
        /// 保留方法实现而不是省略，是为了让 IState 的三段式生命周期在阅读时保持完整。
        /// </summary>
        public void Enter()
        {
        }

        /// <summary>
        /// 每帧驱动：感知敌人 → 有敌人则请求追击，无敌人则请求推进。
        /// </summary>
        public void Tick()
        {
            if (context == null)
            {
                return;
            }

            // Owner 为 EntityBase（UnityEngine.Object），这里用 == null 判断：
            // Unity 重载的 == 能正确识别"已被销毁"的对象，而 C# 的 ?. / is null 不能。
            EntityBase owner = context.Owner;
            if (owner == null)
            {
                return;
            }

            TargetingComponent targeting = owner.Targeting;
            if (targeting == null)
            {
                // 没有索敌组件就无法感知敌人，只能一直待机。
                ReportMissingTargetingOnce();
                return;
            }

            // 感知：统一走控制器【节流后】的入口（内部按 detectionInterval 限制物理查询频率），
            // 而不是自己调 FindNearestEnemy —— 否则每个单位每帧都会做一次 OverlapSphere（GC Alloc），
            // 与计划书第 6 条「AI 感知采用固定周期检测」冲突。
            // targeting 变量仍然保留：感知只是"找"，把结果写进索敌组件才是"锁"，后者必须由本状态显式完成。
            if (!context.TryDetectEnemy(out ITargetable target))
            {
                // 没有敌人：只有在推进状态确实还有未走完的路径点时才切回去。
                // 必要性：MoveState 走完路线后会切到本状态，若这里无条件切回 MoveState，
                // 两者就会每帧互切（Move → Idle → Move → …），切换日志刷屏、CPU 空转，
                // 而且会让"路线走完"这个事实永远无法稳定地停留在待机状态上。
                if (context.HasRemainingWaypoints)
                {
                    context.TryEnterMoveState();
                }

                return;
            }

            // 顺序不能颠倒：必须先把目标写入索敌组件，再请求切换状态。
            // 因为 ChaseState 的 Tick 会直接读取 TargetingComponent.CurrentTarget，
            // 若先切状态再设目标，追击的第一帧会因为读不到目标而立刻判定失败并退回。
            targeting.SetTarget(target);

            if (context.TryEnterChaseState())
            {
                // 切换成功 → 本帧职责结束。
                return;
            }

            // 切换被拒绝（本单位不具备追击能力，见 EntityAIController.TryEnterChaseState）
            // → 敌人不构成威胁，继续往下判断是否需要回到推进状态。
            if (context.HasRemainingWaypoints)
            {
                context.TryEnterMoveState();
            }
        }

        /// <summary>
        /// 离开待机。
        /// 无需清理：本状态在 Enter 中未申请任何资源，也没有持续生效的副作用。
        /// </summary>
        public void Exit()
        {
        }

        /// <summary>
        /// 报告一次"缺少索敌组件"的警告。
        /// 用一次性标记的原因：Tick 每帧执行，不加标记会在一秒内刷出几十条同样的日志。
        /// </summary>
        private void ReportMissingTargetingOnce()
        {
            if (hasReportedMissingTargeting)
            {
                return;
            }

            hasReportedMissingTargeting = true;

            Debug.LogError(
                $"[IdleState] {context.name} 上找不到 TargetingComponent，无法感知敌人，" +
                "AI 将一直停留在待机状态。请为需要 AI 的单位补齐索敌组件。", context);
        }
    }
}
