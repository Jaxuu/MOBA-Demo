using UnityEngine;
using MOBA.Components;
using MOBA.Core;

namespace MOBA.AI
{
    /// <summary>
    /// 死亡状态：AI 的终态，负责把单位从"活跃"收敛为"静止"。
    ///
    /// 职责边界（对应计划书 3.4「DeadState：停止寻路和战斗，禁用选中与碰撞，播放死亡表现」）：
    /// 1. 本状态【只做收尾】，不参与任何战斗或移动决策。收尾动作共四步，顺序见 Enter 内的说明：
    ///    停止寻路 → 清空目标 → 禁用碰撞与寻路代理 → 标记不可选中；
    ///    （"播放死亡表现"不在本状态职责内——计划书第 4 节明确要求逻辑代码不得强耦合特效/动画，
    ///      表现层订阅 HealthComponent.OnDied 自行处理即可。）
    /// 2. 由 EntityAIController 监听 HealthComponent.OnDied 后强制切入，
    ///    而不是靠状态自己每帧检查"我死了吗"——死亡是事件，不是需要轮询的条件；
    /// 3. 本状态是【单向终态】：一旦进入就不会再离开，因此 Exit 不需要做任何事。
    ///
    /// 为什么收尾动作放在 Enter 而不是 Tick：
    /// Enter 只会执行一次，天然满足"停止寻路只做一次"的语义；
    /// 若写在 Tick 里，就会变成每帧重复调用 Stop()，既浪费也会干扰后续可能的死亡表现。
    /// </summary>
    public class DeadState : IState
    {
        /// <summary>状态上下文。所有组件访问都经它拿到。</summary>
        private readonly EntityAIController context;

        /// <summary>
        /// 构造状态。
        /// </summary>
        /// <param name="context">所属 AI 控制器，必须非空。</param>
        public DeadState(EntityAIController context)
        {
            this.context = context;
        }

        /// <summary>
        /// 进入死亡状态：停止寻路 → 清空目标 → 输出日志。
        /// 三步顺序有讲究：先停止移动（不再产生新的位移意图），再清空目标（不再被任何状态读取），
        /// 最后打日志作为"收尾已完成"的锚点——阶段三验收要求"目标死亡后攻击与寻路立即停止"。
        /// </summary>
        public void Enter()
        {
            if (context == null)
            {
                return;
            }

            EntityBase owner = context.Owner;

            // EntityBase 是 UnityEngine.Object，用 == null 判断可正确识别"已被销毁"的情况。
            if (owner == null)
            {
                Debug.LogWarning("[DeadState] 上下文中的 EntityBase 已被销毁，跳过死亡收尾。");
                return;
            }

            // 1. 停止寻路：清空路径并冻结 NavMeshAgent。
            //    防御塔与基地按设计没有 MovementComponent（计划书 3.2），因此必须判空。
            MovementComponent movement = owner.Movement;
            if (movement != null)
            {
                movement.Stop();
            }

            // 2. 清空目标：让本单位不再持有敌人引用，也避免后续状态残留的追击逻辑继续生效。
            //    注意：清空的是"本单位锁定的目标"，而不是"别人锁定本单位"——
            //    后者由 EntityBase.IsValidTarget 中的 Health.IsDead 判断自动失效，无需在此处理。
            TargetingComponent targeting = owner.Targeting;
            if (targeting != null)
            {
                targeting.ClearTarget();
            }

            // 3. 禁用碰撞：计划书 3.4「禁用选中与碰撞」的"碰撞"一半。
            //    为什么在这里直接操作 Collider，而不是让 MovementComponent 代劳：
            //    MovementComponent 的职责是「怎么移动」，没有「把自己彻底关掉」的语义；
            //    而且防御塔、基地按设计没有 MovementComponent，它们的碰撞体同样需要被处理，
            //    因此这里按组件能力统一处理，天然覆盖所有单位类型。
            //    直接收益：尸体不再被 Physics.OverlapSphere 命中，从源头退出其他单位的索敌候选列表。
            //    （IsValidTarget 虽然已能过滤尸体，但那是"查出来再丢掉"，禁用碰撞体是"根本查不出来"。）
            Collider[] colliders = owner.GetComponentsInChildren<Collider>();
            for (int i = 0; i < colliders.Length; i++)
            {
                if (colliders[i] != null)
                {
                    colliders[i].enabled = false;
                }
            }

            // 4. 禁用寻路代理：让尸体退出导航网格的局部避让计算，
            //    否则活着的小兵会绕着一具尸体走（NavMeshAgent 之间会互相推挤）。
            //    必须先于"停止寻路"之外的任何代理操作：Stop() 已在上面第 1 步执行完毕，
            //    此后 MovementComponent 内部的 IsAgentUsable 会因 agent.enabled == false 而全部返回安全默认值。
            //
            //    【阶段八自审修正】此前这里是 `owner.GetComponent<NavMeshAgent>()` 再写 enabled = false ——
            //    那是绕过 MovementComponent 直接操作代理，与它"本项目里唯一负责驱动 NavMeshAgent 的模块"
            //    这条职责边界直接冲突（同一件事在 DeadState 与 HeroController 各有一份实现）。
            //    现在统一走 movement.SetAgentEnabled(false)，与上面第 1 步的 Stop() 是同一个组件实例。
            if (movement != null)
            {
                movement.SetAgentEnabled(false);
            }

            // 5. 标记为不可选中：计划书 3.4「禁用选中与碰撞」的"选中"一半。
            //    与第 3 步配套——即使有人绕开 IsValidTarget 直接按 Collider 选目标，也选不到尸体。
            owner.SetSelectable(false);

            // 6. 日志：作为人工验证的锚点。用 owner 作为日志上下文，便于在 Console 中点击定位到该单位。
            Debug.Log(
                $"[DeadState] {owner.name} 已死亡，AI 停止（已停止寻路、清空目标、禁用碰撞与选中）。", owner);
        }

        /// <summary>
        /// 每帧驱动。
        /// 刻意留空：死亡后不再感知、不再移动、不再攻击，没有任何需要持续处理的事情。
        /// 保留空实现而不是让 StateMachine 特判"死亡后停止 Tick"，
        /// 是为了保持 StateMachine 对所有状态一视同仁（它不认识任何具体状态类型）。
        /// </summary>
        public void Tick()
        {
        }

        /// <summary>
        /// 离开死亡状态。
        /// 无需清理：死亡是单向终态，不存在"复活后继续用同一套状态实例"的合法路径。
        /// 若后续要实现英雄复活，应新建独立的状态与流程，而不是让 DeadState 被退出。
        /// </summary>
        public void Exit()
        {
        }
    }
}
