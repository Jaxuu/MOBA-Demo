using UnityEngine;
using MOBA.Components;
using MOBA.Core;

namespace MOBA.AI
{
    /// <summary>
    /// 攻击状态：站定输出，按攻击冷却对当前目标发起普通攻击。
    ///
    /// 职责边界（对应计划书 3.4「AttackState：进入攻击距离后停止移动，并按冷却触发攻击」）：
    /// 1. 本状态【不维护攻击冷却】——冷却由 CombatComponent 用 nextAttackTime 管理，
    ///    本状态只需每帧尝试一次 TryAttack，由组件决定这一下能不能打出去；
    /// 2. 本状态【不结算伤害】——伤害走 IDamageable，由 CombatComponent 内部完成；
    /// 3. 目标有效性统一走 EntityAIController.TryGetValidTarget，与 ChaseState 口径一致。
    ///
    /// 【防震荡设计 · 滞回死区】本状态与 ChaseState 的切换阈值刻意错开：
    ///   - 进入攻击（ChaseState 判断）：距离 ≤ 攻击距离（AttackEnterDistance）
    ///   - 退出攻击（本状态判断）：距离 &gt; 攻击距离 × attackExitRangeFactor（AttackExitDistance，默认 1.15 倍）
    /// 两个阈值之间存在一个"死区"。若两者取同一个值，目标在攻击距离边缘的任意微小位移
    /// （包括 NavMeshAgent 停下时的惯性滑动、目标自身的走位）都会让状态每帧在 Chase 与 Attack 之间翻面，
    /// 表现为：每帧 Stop/寻路来回调用、Console 被切换日志刷屏、单位在边缘原地抖动。
    /// 死区内本状态【既不切状态、也不追击、也不攻击】——这是消除该震荡的核心。
    /// </summary>
    public class AttackState : IState
    {
        /// <summary>状态上下文。</summary>
        private readonly EntityAIController context;

        /// <summary>
        /// 构造状态。
        /// </summary>
        /// <param name="context">所属 AI 控制器，必须非空。</param>
        public AttackState(EntityAIController context)
        {
            this.context = context;
        }

        /// <summary>
        /// 进入攻击状态：立刻停止移动。
        /// 攻击必须站定，否则会出现"边打边走"——单位会一边结算伤害一边从目标身上穿过去。
        /// 停止会清空路径并冻结代理；若之后需要重新追击，ChaseState 的 MoveTo 会自动恢复
        /// （MovementComponent.MoveTo 内部会把 isStopped 置回 false）。
        /// </summary>
        public void Enter()
        {
            if (context == null)
            {
                return;
            }

            EntityBase owner = context.Owner;
            if (owner == null)
            {
                return;
            }

            // 固定建筑（防御塔、基地）没有移动组件，必须判空。
            MovementComponent movement = owner.Movement;
            if (movement != null)
            {
                movement.Stop();
            }
        }

        /// <summary>
        /// 每帧驱动：校验目标 → 判断是否退出攻击 → 在攻击距离内则尝试攻击。
        /// </summary>
        public void Tick()
        {
            if (context == null)
            {
                return;
            }

            EntityBase owner = context.Owner;
            if (owner == null)
            {
                return;
            }

            // ---------- 1. 目标失效 / 死亡 → 回待机，由 IdleState 重新分流（找新敌人或继续推进） ----------
            if (!context.TryGetValidTarget(out ITargetable target))
            {
                // TryGetValidTarget 内部已顺手清空失效目标，因此 IdleState 下一帧不会重复读到它。
                context.TryEnterIdleState();
                return;
            }

            Transform targetTransform = target.TargetTransform;
            if (targetTransform == null)
            {
                context.TryEnterIdleState();
                return;
            }

            float distance = Vector3.Distance(owner.transform.position, targetTransform.position);

            // ---------- 2. 目标拉开到滞回上界之外 → 重新追击 ----------
            if (distance > context.AttackExitDistance)
            {
                context.TryEnterChaseState();
                return;
            }

            // ---------- 3. 滞回死区：攻击距离 < 距离 ≤ 滞回上界 ----------
            // 刻意什么都不做：既不切状态，也不追击，也不攻击（距离不足，打了也打不到）。
            // 这是本状态防止"追击-攻击-追击"高频震荡的关键，详见类头的说明。
            // 死区宽度 = 攻击距离 × (attackExitRangeFactor - 1)，默认 15%，代价是目标刚出射程时
            // 本单位会短暂停顿；一旦目标继续拉开到滞回上界之外，第 2 步会立刻重新追击。
            if (distance > context.AttackEnterDistance)
            {
                return;
            }

            // ---------- 4. 在攻击距离内 → 尝试攻击 ----------
            CombatComponent combat = owner.Combat;
            if (combat == null)
            {
                // 没有战斗组件却进入了攻击状态，说明配置有误（例如攻击距离来源被移除）。
                // 回待机避免在此状态空转卡死。
                context.TryEnterIdleState();
                return;
            }

            // 返回 false 属于正常情况：攻击冷却未就绪时 TryAttack 会拒绝本次攻击。
            // 攻击节奏由 CombatComponent 按 AttackData.AttackInterval 控制，
            // 本状态因此不需要（也不应该）自己维护冷却计时器，否则会出现两套节奏互相打架。
            combat.TryAttack(target);
        }

        /// <summary>
        /// 离开攻击状态。
        /// 无需清理：本状态没有订阅事件，也没有需要释放的资源（停止移动只是把代理冻结，不是资源占用）。
        /// </summary>
        public void Exit()
        {
        }
    }
}
