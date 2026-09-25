using UnityEngine;

namespace MOBA.AI
{
    /// <summary>
    /// 有限状态机核心引擎：只负责「保存当前状态、保证切换时序、驱动当前状态」。
    ///
    /// 设计取舍：
    /// 1. 本类是【纯 C# 类】而非 MonoBehaviour。状态机的生命周期完全由持有者
    ///    （EntityAIController）控制，不需要 Unity 的消息回调，因此不挂在 GameObject 上；
    ///    这同时也让它可以脱离场景在 EditMode 单元测试中被直接实例化与驱动
    ///    （计划书要求测试覆盖「FSM 状态切换」）。
    /// 2. 本类【不认识任何具体状态类型】，只面向 IState 接口编程。新增状态（例如后续的
    ///    ReturnState）无需改动本文件，符合「对扩展开放、对修改关闭」。
    /// 3. 本类不驱动自身 Tick —— 由 EntityAIController 在固定的 AI 更新周期中调用 Tick()，
    ///    使「谁来决定何时思考」与「状态如何切换」两件事解耦。
    /// </summary>
    public class StateMachine
    {
        /// <summary>
        /// 当前处于激活状态的状态对象；尚未 Initialize 时为 null。
        /// 用途：调试显示（计划书阶段三要求「增加 FSM 当前状态与目标的调试显示」）
        /// 以及状态实现内部读取上下文。只读——切换状态必须走 ChangeState()，
        /// 否则会绕过 Exit/Enter 时序，导致旧状态资源未释放。
        /// </summary>
        public IState CurrentState { get; private set; }

        /// <summary>
        /// 是否已经完成初始化（即 CurrentState 非空）。
        /// 用途：让调用方（EntityAIController / 调试视图）在 Tick 前判断状态机是否可用，
        /// 避免在未初始化时依赖 CurrentState 而抛出空引用。
        /// </summary>
        public bool IsInitialized => CurrentState != null;

        /// <summary>
        /// 初始化状态机：把起始状态设为当前状态并立即调用其 Enter()。
        /// 与 ChangeState 的区别：初始化时不存在「旧状态」，因此【不调用 Exit】。
        /// </summary>
        /// <param name="startingState">起始状态，必须非空。</param>
        public void Initialize(IState startingState)
        {
            if (startingState == null)
            {
                // 起始状态为空属于调用方配置错误，状态机将无法工作，必须立刻暴露而不是静默吞掉。
                Debug.LogError("[StateMachine] Initialize 收到空的起始状态，状态机保持未初始化状态。");
                return;
            }

            // 重复初始化时，先退出旧状态，保证 Exit/Enter 依旧成对，不会残留上一个状态的副作用。
            CurrentState?.Exit();

            CurrentState = startingState;
            CurrentState.Enter();
        }

        /// <summary>
        /// 切换到新状态。
        /// 执行时序严格为：旧状态 Exit() → 更新 CurrentState → 新状态 Enter()。
        /// 这个顺序不可颠倒：新状态的 Enter 中往往会读取由旧状态释放掉的资源状态（例如寻路已停止），
        /// 若先 Enter 再 Exit，旧状态的清理动作会把新状态刚建立的条件冲掉。
        /// </summary>
        /// <param name="newState">目标状态，必须非空。</param>
        public void ChangeState(IState newState)
        {
            if (newState == null)
            {
                Debug.LogError("[StateMachine] ChangeState 收到空状态，本次切换被忽略。");
                return;
            }

            // 使用引用比较而非 Equals：状态实例通常"一个实体一份"，用引用判断才能准确识别
            // "就是当前这一个"。同时避免实现方重写 Equals 后，把两个不同实例误判为同一状态。
            if (ReferenceEquals(newState, CurrentState))
            {
                // 重复切入同一状态时直接忽略：若在此处重新执行 Enter，
                // 会把状态内部的计时器与一次性移动指令全部重置，导致状态永远无法推进。
                return;
            }

            // 未初始化时 CurrentState 为 null，此处的空条件调用使 ChangeState 可以安全地
            // 承担"首次进入"的职责，不会因为漏调 Initialize 而崩溃。
            CurrentState?.Exit();

            CurrentState = newState;
            CurrentState.Enter();
        }

        /// <summary>
        /// 驱动当前状态执行一帧逻辑。
        /// 未初始化时静默返回（不报错）：EntityAIController 可能在拿到 EntityBase 引用之前
        /// 就已经开始接收更新调用，这属于合法的启动时序，不应视为错误。
        /// </summary>
        public void Tick()
        {
            CurrentState?.Tick();
        }
    }
}
