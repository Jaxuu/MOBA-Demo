using UnityEngine;
using MOBA.Components;
using MOBA.Core;

namespace MOBA.AI
{
    /// <summary>
    /// 追击状态：把与当前目标的距离压缩到攻击距离以内，进入后交给 AttackState。
    ///
    /// 职责边界（对应计划书 3.4「ChaseState：追踪有效目标；目标失效或超出追击范围时退出」）：
    /// 1. 只负责"靠近"，不负责"打"——攻击由 AttackState 触发；
    /// 2. 目标的有效性判断统一走 EntityAIController.TryGetValidTarget，避免本状态与 AttackState 口径不一致；
    /// 3. 本状态【不主动换目标】：只要当前目标仍有效就只读它，不因为"旁边有更近的敌人"而改目标。
    ///    只有当前目标彻底失效（死亡 / 被销毁 / 不可选中 / 被策反）时，才会经控制器【节流后】的感知
    ///    立刻接手一个新目标——这样就不必为了重新索敌白跑一趟推进状态。
    ///
    /// 【防震荡设计 · 退出阈值与进入阈值错开】
    ///   - 进入本状态：由 MoveState / IdleState 在索敌半径内发现敌人时发起；
    ///   - 退出本状态：距离超过「索敌半径 × chaseAbandonRangeFactor（默认 1.5）」才放弃。
    /// 由于 1.5 &gt; 1，一旦追起来就不会因为目标在索敌半径边缘轻微飘动而反复退出/进入。
    ///
    /// 【牵引极限 · 追击起点锚点】
    /// 仅限制"与目标的距离"是不够的：若目标始终贴着本单位跑（例如被玩家英雄牵引），
    /// 与目标的距离一直很小，本单位就会一路被牵着离开兵线、越跑越远。
    /// 为此在 Enter 时记录【追击起点锚点 chaseStartPos】（即脱离兵线时的坐标），
    /// 并在 Tick 中判断"与锚点的距离"是否超过牵引极限（索敌半径 × 2.0）。
    /// 超过则强制放弃目标、返回兵线——这就是"被引离路线超过限制"的兜底。
    ///
    /// 锚点的生命周期（这是该机制能否生效的关键，不可简化）：
    ///   - 首次进入追击时记录；
    ///   - 追击过程中【不刷新】——否则每次 Attack → Chase 的往返都会把锚点前移，牵引限制形同虚设；
    ///   - 只有在本单位真正回到兵线（MoveState 抵达路径点）时才由 MoveState 清除，允许开始下一轮追击。
    /// </summary>
    public class ChaseState : IState
    {
        /// <summary>状态上下文。</summary>
        private readonly EntityAIController context;

        /// <summary>上一次下达给 MovementComponent 的目的地，用于判断"目标是否移动得足够多、值得重新寻路"。</summary>
        private Vector3 lastIssuedDestination;

        /// <summary>是否已下达过移动指令。为 false 时强制重新寻路一次，避免代理带着上一段的旧路径继续走。</summary>
        private bool hasIssuedDestination;

        /// <summary>追击起点锚点：本轮追击开始时（脱离兵线那一刻）本单位所在的世界坐标。</summary>
        private Vector3 chaseStartPos;

        /// <summary>锚点是否已记录。为 false 时表示"尚未脱离兵线"，此时不做牵引限制。</summary>
        private bool hasChaseStartPos;

        /// <summary>
        /// 本轮追击已经「几乎没在动」的累计时长（秒）。
        /// 重新下达指令、单位重新动起来、或目标切换后清零——它衡量的是「连续僵持了多久」，
        /// 因此中途一旦动起来就必须归零，否则会把"走走停停"误判成卡死。
        /// </summary>
        private float stagnationTimer;

        /// <summary>是否已就「被卡住」报过警告，防止同一单位反复卡住时刷屏。</summary>
        private bool hasReportedStagnation;

        /// <summary>
        /// 构造状态。
        /// </summary>
        /// <param name="context">所属 AI 控制器，必须非空。</param>
        public ChaseState(EntityAIController context)
        {
            this.context = context;
        }

        /// <summary>
        /// 当前是否已被牵引出允许范围（与追击起点锚点的距离超过牵引极限）。
        ///
        /// 对外暴露给 EntityAIController 使用：当本单位已被牵引出界时，
        /// 即便附近仍有敌人也不允许重新进入追击——否则会出现
        /// 「Chase 判定出界 → Move → Move 立刻又发现敌人 → Chase → 再次判定出界」的每帧互切。
        /// 必须先把兵线走回来（MoveState 抵达路径点、清除锚点）才重新获得追击资格。
        /// </summary>
        public bool IsBeyondChaseLeash
        {
            get
            {
                if (!hasChaseStartPos)
                {
                    // 尚未脱离兵线，不设限制。
                    return false;
                }

                float leashDistance = context != null ? context.ChaseLeashDistance : 0f;
                if (leashDistance <= 0f)
                {
                    // 索敌半径未配置（为 0）时无法计算极限，按"不限制"处理，避免误判成永久出界。
                    return false;
                }

                return Vector3.Distance(context.transform.position, chaseStartPos) > leashDistance;
            }
        }

        /// <summary>
        /// 进入追击。
        ///
        /// 做两件事：
        /// 1. 清掉寻路记录，强制本状态第一帧就下达一次移动指令——上一段推进留下的目的地可能是
        ///    路径点而非目标位置，若沿用旧路径，代理会先跑错方向；
        /// 2. 【仅在尚未记录时】记下追击起点锚点。之所以用"尚未记录"而不是每次 Enter 都覆盖：
        ///    Attack → Chase 的往返（目标在射程边缘进出）会反复触发 Enter，
        ///    若每次都刷新锚点，锚点就会跟着单位一路前移，牵引极限永远触发不了。
        /// </summary>
        public void Enter()
        {
            hasIssuedDestination = false;
            stagnationTimer = 0f;

            if (hasChaseStartPos || context == null)
            {
                return;
            }

            // 控制器的 GameObject 就是 EntityBase 所在的物体（AI 与实体身份必须挂在同一物体上），
            // 因此 context.transform.position 与 EntityBase.transform.position 是同一个点。
            chaseStartPos = context.transform.position;
            hasChaseStartPos = true;
        }

        /// <summary>
        /// 清除追击起点锚点，允许开始新一轮追击。
        /// 由 MoveState 在"真正回到兵线"（抵达当前路径点）时调用——
        /// 这是锚点唯一的清除时机，不能改到 MoveState.Enter：
        /// 那样在返回兵线的途中就会重置锚点，单位会立刻再次被牵引出界。
        /// </summary>
        public void ClearChaseStartPos()
        {
            hasChaseStartPos = false;
        }

        /// <summary>
        /// 每帧驱动：校验目标 → 判断是否被牵引出界 → 判断是否放弃 → 判断是否进入攻击距离 → 否则继续追踪。
        /// 判断顺序不可颠倒：先做两个"放弃"判定（牵引极限、目标距离），再判攻击距离，
        /// 可以保证"目标已跑远"时不会误判成"进入攻击距离"。
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

            // ---------- 1. 目标校验：失效（死亡 / 被销毁 / 不可选中 / 阵营变化）即换目标或放弃追击 ----------
            if (!context.TryGetValidTarget(out ITargetable target))
            {
                // 当前目标没了。先用控制器【节流后】的感知尝试立刻接手一个新目标，
                // 避免为了重新索敌白跑一趟 MoveState（那会让单位在原地多停一帧）。
                // 拿不到新目标才回推进状态继续推线。
                if (!context.TryDetectEnemy(out ITargetable newTarget))
                {
                    // TryGetValidTarget 内部已顺手清空失效目标，这里只需回推进状态。
                    context.TryEnterMoveState();
                    return;
                }

                owner.Targeting.SetTarget(newTarget);
                target = newTarget;
            }

            Transform targetTransform = target.TargetTransform;
            if (targetTransform == null)
            {
                context.TryEnterMoveState();
                return;
            }

            // ---------- 2. 被牵引出界 → 强制放弃目标，返回兵线 ----------
            // 触发条件是"本单位离追击起点锚点太远"，而不是"离目标太远"，
            // 因此它拦住的正是"目标一直贴着本单位跑、把本单位牵着离开兵线"这种情形。
            // 放弃后 MoveState 会立刻重新感知到该敌人，但 EntityAIController.TryEnterChaseState
            // 会因为 IsBeyondChaseLeash 仍为真而拒绝再次追击，直到单位走回兵线。
            if (IsBeyondChaseLeash)
            {
                owner.Targeting.ClearTarget();
                context.TryEnterMoveState();
                return;
            }

            float distance = Vector3.Distance(owner.transform.position, targetTransform.position);

            // ---------- 3. 超出追击极限 → 放弃目标，返回路线 ----------
            if (distance > context.ChaseAbandonDistance)
            {
                // 主动清空目标：若不清理，下一帧 MoveState 的 FindNearestEnemy 仍可能命中它，
                // 于是立刻又切回追击，形成 Chase ↔ Move 每帧震荡。
                owner.Targeting.ClearTarget();
                context.TryEnterMoveState();
                return;
            }

            // ---------- 4. 已进入攻击距离 → 转攻击 ----------
            if (distance <= context.AttackEnterDistance)
            {
                context.TryEnterAttackState();
                return;
            }

            // ---------- 5. 继续追踪 ----------
            MovementComponent movement = owner.Movement;
            if (movement == null)
            {
                // 防御性检查：EntityAIController.CanChase 已保证进入本状态时移动组件存在，
                // 正常情况下走不到这里。真发生了（例如组件被运行时移除）则退回推进状态，避免空转。
                context.TryEnterMoveState();
                return;
            }

            // 只在目标相对上次寻路位置移动超过阈值时才重新下达指令。
            // 必要性：本状态每帧都在跑，而 NavMeshAgent.SetDestination 每次调用都会重新算路；
            // 目标几乎静止时每帧重算纯属浪费，还会让代理在路径上轻微抖动。
            float repathDistance = context.ChaseRepathDistance;
            if (!hasIssuedDestination ||
                (targetTransform.position - lastIssuedDestination).sqrMagnitude > repathDistance * repathDistance)
            {
                // 记录"这次是否真的下达成功"：失败（目标点落在 NavMesh 外、或代理不在网格上）时保持 false，
                // 下一帧重新尝试。若失败也置 true，当目标静止不动时本单位会永久停在原地——
                // 与 MoveState 同一处理原则（阶段三验收要求"无明显状态卡死"）。
                hasIssuedDestination = movement.MoveTo(targetTransform.position);
                lastIssuedDestination = targetTransform.position;
                stagnationTimer = 0f;
                return;
            }

            // ---------- 6. 停滞检测：被堵住时强制重新下达移动指令 ----------
            // 覆盖的场景：目标静止、本单位却被其他单位挡在路径上，NavMeshAgent 的局部避让绕不开，
            // 于是"已下令但永远靠不过去"。这类僵持没有任何超时机制就会永久停住。
            if (context.IsStagnant(movement))
            {
                stagnationTimer += Time.deltaTime;

                if (stagnationTimer >= context.StagnationDuration)
                {
                    // 清掉"已下令"标记 → 下一帧重新下达一次 MoveTo（代理会重算路径）。
                    stagnationTimer = 0f;
                    hasIssuedDestination = false;
                    ReportStagnationOnce();
                }

                return;
            }

            // 动起来了：清空僵持计时，避免"走走停停"被累计成卡死。
            stagnationTimer = 0f;
        }

        /// <summary>
        /// 离开追击状态。
        /// 无需清理：本状态没有订阅事件；寻路记录会在下次 Enter 时重置。
        /// 注意这里【不】调用 MovementComponent.Stop()——追击通常紧接攻击或推进，
        /// 由目标状态决定是否停下（AttackState.Enter 会 Stop），避免在退出时多做一次无意义的冻结。
        /// 也【不】清除追击起点锚点——锚点必须跨越 Chase → Attack → Chase 的往返持续有效，
        /// 否则牵引极限会被反复重置而失效。
        /// </summary>
        public void Exit()
        {
        }

        /// <summary>
        /// 报告一次"被卡住"。
        /// 用一次性标记的原因：单位可能被长期堵住，每 1 秒重下一次指令就会每 1 秒打一条日志。
        /// 出现这条警告通常意味着：目标点不可达，或被其他单位挡在路径上无法靠近。
        /// </summary>
        private void ReportStagnationOnce()
        {
            if (hasReportedStagnation)
            {
                return;
            }

            hasReportedStagnation = true;

            Debug.LogWarning(
                $"[ChaseState] {context.name} 连续 {context.StagnationDuration:F1} 秒几乎未移动，判定为被卡住，" +
                "已强制重新下达移动指令。若该警告反复出现，请检查目标点是否可达、" +
                "或本单位是否被其他单位长期堵在路径上。", context);
        }
    }
}
