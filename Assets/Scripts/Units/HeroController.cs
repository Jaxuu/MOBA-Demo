using System;
using UnityEngine;
using MOBA.Components;
using MOBA.Controllers;
using MOBA.Core;

namespace MOBA.Units
{
    /// <summary>
    /// 英雄控制器：玩家英雄的「身份 + 核心数据」封装，以及英雄专属的生命周期收尾。
    ///
    /// 职责边界（README 3.1「玩家英雄」）：
    /// 1. 数据封装：把英雄的身份与运行状态（显示名、阵营、存活与否、当前/最大生命、攻击距离、移速）
    ///    以只读属性对外暴露。相机、UI、调试视图直接读这里即可，不必各自 GetComponent 拼装同一份数据；
    /// 2. 死亡收尾：英雄【不挂 EntityAIController】，因此没有 FSM 的 DeadState 那条路径，
    ///    「死亡后原地静止、不再响应玩家输入」必须由本类承担（见 HandleDied）；
    /// 3. 本类【不】采集输入（那是 PlayerCommandController 的职责）、【不】驱动移动或攻击
    ///    （那是 MovementComponent / CombatComponent 的职责），也不重复实现目标合法性判断
    ///    （唯一入口是 TargetingComponent）。
    ///
    /// 为什么英雄不挂 EntityAIController（这是本类的核心设计前提）：
    /// FSM 会自主索敌、追击、攻击，并直接驱动 MovementComponent 与 CombatComponent；
    /// 而玩家指令层驱动的是同一对组件。两者同时存在会互相覆盖指令，表现为
    /// 「刚下达的移动命令被 AI 立刻改写」或「英雄自己跑回兵线」。
    /// 因此本项目的可移动单位分成两条互斥路线：AI 单位走 FSM（EntityAIController），
    /// 玩家英雄走玩家指令层（PlayerCommandController + 本类）。
    ///
    /// 【阶段八扩展：AI 英雄复用本类】10 个英雄共用同一个英雄预制体，因此 AI 英雄同样挂本类
    /// （死亡收尾 / 复活链路完全一致）。两条路线的划分不再由"挂不挂本类"表达，而是由
    /// <see cref="playerControlled"/> 表达：AI 英雄在实例上把 PlayerCommandController /
    /// PlayerSkillController 禁用，并由 EntityAIController 驱动移动与平A。
    /// 本类在死亡与复活两条路径上都用该开关守住输入控制器，绝不误开 AI 英雄的输入通道。
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(EntityBase))]
    public class HeroController : MonoBehaviour
    {
        [Header("英雄身份")]
        [Tooltip("英雄显示名，供 UI / 结算界面展示。与 GameObject 名字分开：物体名要满足工具按名清理的约定，显示名要能改。")]
        [SerializeField] private string heroDisplayName = "英雄";

        [Tooltip("是否为【玩家操控】的英雄（阶段八新增）。\n" +
                 "true  = 死亡时掐断 PlayerCommandController、复活时恢复（阶段五以来的行为）。\n" +
                 "false = AI 英雄：绝不触碰输入控制器。\n" +
                 "【为什么必须有这个开关】AI 英雄与玩家英雄共用同一个英雄预制体，预制体上必然挂着\n" +
                 "PlayerCommandController；工具在实例上把它禁用即可，但 Revive() 若无条件把它 enabled = true，\n" +
                 "一个 AI 英雄复活后就会开始响应玩家的右键 —— 10 个英雄里会有 9 个跟着鼠标跑。\n" +
                 "把'谁是被玩家操控的那一个'写成显式数据，比在复活路径上猜要可靠得多。")]
        [SerializeField] private bool playerControlled = true;

        [Header("死亡处理")]
        [Tooltip("死亡后是否禁用 PlayerCommandController。\n" +
                 "默认开启：README 3.1 要求英雄死亡后原地静止，而 PlayerCommandController 是唯一会驱动它的输入源，\n" +
                 "掐断输入源比在每个指令分支里判死亡更彻底（指令层内部仍保留一道 IsDead 校验作为第二道闸门）。")]
        [SerializeField] private bool disablePlayerControlOnDeath = true;

        [Tooltip("死亡后是否禁用自身碰撞体。\n" +
                 "默认开启，与 DeadState 对移动单位的处理保持一致：尸体不应继续占据敌人的索敌候选列表\n" +
                 "（索敌走 Physics.OverlapSphere，碰撞体被禁用后该单位不再出现在任何一次查询结果里）。")]
        [SerializeField] private bool disableCollidersOnDeath = true;

        [Header("复活")]
        [Tooltip("复活点（可选）。留空时使用英雄在场景中的初始落点 —— 也就是一键组装工具放它的位置（蓝方出生点）。\n" +
                 "刻意【不】新建 SpawnPoint 组件：这里只需要一个 Transform，多一个没有消费方的标记组件就是死代码\n" +
                 "（沿用阶段五「不做 SpawnPoint.cs」的裁决）。")]
        [SerializeField] private Transform respawnPoint;

        [Header("调试")]
        [Tooltip("在 Console 输出英雄初始化与死亡记录（每个英雄仅两条）。")]
        [SerializeField] private bool logHeroEvents = true;

        /// <summary>
        /// 实体身份入口。由 [RequireComponent] 保证存在，仍做判空以给出明确报错。
        /// 必须带 [SerializeField]：一键组装工具是通过 SerializedObject 注入这个引用的，
        /// 未序列化的私有字段对序列化系统不可见，注入会直接失败。
        /// </summary>
        [SerializeField] private EntityBase entity;

        /// <summary>生命组件。死亡事件订阅与状态读取的来源，缺失则本类无法工作。</summary>
        private HealthComponent health;

        /// <summary>法力组件。复活时补满法力用，允许为 null（未配置法力的英雄）。</summary>
        private ManaComponent mana;

        /// <summary>战斗组件。对外暴露攻击距离用，允许为 null（未配置攻击能力的英雄）。</summary>
        private CombatComponent combat;

        /// <summary>移动组件。对外暴露移速与位移状态用，英雄必须有它（EntityBase.RequiresMovement 会校验）。</summary>
        private MovementComponent movement;

        /// <summary>玩家指令控制器。死亡时需要掐断它，因此在这里解析并持有。</summary>
        private PlayerCommandController commandController;

        /// <summary>死亡收尾是否已执行，保证 OnDied 回调与「订阅时已死亡」的补偿路径不会重复执行。</summary>
        private bool hasHandledDeath;

        /// <summary>
        /// 英雄的初始落点（Awake 时记录），作为未注入 respawnPoint 时的复活点。
        /// 在 Awake 记录而不是 Start：Awake 早于一切移动逻辑，此刻的 transform.position
        /// 必然是场景里保存的位置（即工具放置英雄的位置），不会被任何一帧位移污染。
        /// </summary>
        private Vector3 respawnAnchor;

        /// <summary>英雄死亡事件。参数为英雄自身，供 UI / 结算 / 调试视图订阅。</summary>
        public event Action<HeroController> OnHeroDied;

        /// <summary>
        /// 英雄复活完成事件（阶段七新增）。参数为英雄自身。
        /// 在 Revive() 的收尾动作【全部完成之后】广播，订阅方（HUD 解除置灰、复活遮罩隐藏）读到的是最终状态。
        /// 注意倒计时本身不在这里——它由 MatchController 统筹（见 MatchController.RespawnRoutine）。
        /// </summary>
        public event Action<HeroController> OnHeroRespawned;

        /// <summary>实体身份入口（只读）。</summary>
        public EntityBase Entity => entity;

        /// <summary>英雄显示名（只读）。</summary>
        public string HeroDisplayName => heroDisplayName;

        /// <summary>所属阵营（只读）。身份未就绪时返回 Neutral（表示「未知」）。</summary>
        public TeamType Team => entity != null ? entity.Team : TeamType.Neutral;

        /// <summary>是否已死亡（只读）。生命组件缺失时返回 false——没有生命组件就永远不会死亡。</summary>
        public bool IsDead => health != null && health.IsDead;

        /// <summary>当前生命值（只读）。生命组件缺失时为 0。</summary>
        public float CurrentHealth => health != null ? health.CurrentHealth : 0f;

        /// <summary>最大生命值（只读）。</summary>
        public float MaxHealth => health != null ? health.MaxHealth : 0f;

        /// <summary>生命百分比（0~1，只读），血条 UI 可直接使用。</summary>
        public float HealthPercent => health != null ? health.HealthPercent : 0f;

        /// <summary>攻击距离（只读）。无战斗组件或未配置攻击数据时为 0，表示无法攻击。</summary>
        public float AttackRange => combat != null ? combat.AttackRange : 0f;

        /// <summary>当前移动速度（单位/秒，只读）。</summary>
        public float MoveSpeed => movement != null ? movement.Speed : 0f;

        /// <summary>
        /// 当前是否正在位移（只读）。
        /// 阈值与 MovementComponent.HasReachedDestination 的速度判定取同一个数量级（0.01 平方），
        /// 避免出现「相机认为在动、移动组件认为已到达」这类口径分叉。
        /// </summary>
        public bool IsMoving => movement != null && movement.CurrentVelocity.sqrMagnitude > 0.01f;

        /// <summary>玩家指令控制器（只读），供调试视图确认输入通道是否仍然开启。</summary>
        public PlayerCommandController CommandController => commandController;

        /// <summary>
        /// 复活点世界坐标（只读）。
        /// 优先用 Inspector 注入的 respawnPoint；未指定时回落到英雄的初始落点
        /// （= 一键组装工具实例化它的位置，即蓝方出生点）。
        /// </summary>
        public Vector3 RespawnPosition => respawnPoint != null ? respawnPoint.position : respawnAnchor;

        /// <summary>
        /// 是否已执行过死亡收尾（只读）。
        /// 供复活编排方判断"这个英雄现在是不是一具尸体"，避免对活着的英雄误调 Revive。
        /// </summary>
        public bool HasHandledDeath => hasHandledDeath;

        /// <summary>
        /// 只取实体身份。
        /// 不在这里读 EntityBase 的组件属性：同一 GameObject 上多个组件的 Awake 顺序不确定，
        /// 而且用代码动态创建单位时其他组件可能尚未 AddComponent，此时那些属性会是 null。
        /// 组件解析统一放到 Start（见 ResolveComponents），与项目既定的「Awake 取引用、Start 做注入」约定一致。
        /// </summary>
        private void Awake()
        {
            entity = GetComponent<EntityBase>();

            // 记录初始落点作为复活点兜底（理由见 respawnAnchor 的注释）。
            respawnAnchor = transform.position;
        }

        /// <summary>
        /// 解析组件依赖并订阅死亡事件。
        /// 放在 Start 的原因同上：Unity 保证所有 Awake 先于任何 Start 执行，此时所有组件必定已挂载完成。
        /// </summary>
        private void Start()
        {
            ResolveComponents();
        }

        /// <summary>
        /// 组件解析 + 依赖校验 + 事件订阅。
        /// 必需依赖（EntityBase / HealthComponent）缺失时用 LogError 明确报出并禁用自身，
        /// 而不是等运行中抛空引用异常——那种情况下 Console 会刷出成片的 NullReferenceException，反而更难定位。
        /// </summary>
        private void ResolveComponents()
        {
            if (entity == null)
            {
                Debug.LogError(
                    $"[HeroController] {name} 上找不到 EntityBase，无法读取阵营与组件，英雄控制器不可用。" +
                    "请确保 EntityBase 与 HeroController 挂在同一个 GameObject 上。", this);
                enabled = false;
                return;
            }

            // 刻意用 GetComponent 直接取，而不是读 entity.Health / entity.Combat 属性：
            // 那些属性返回的是 EntityBase 在【自己的 Awake】里缓存的引用，而同一物体上组件的 Start 顺序同样不确定，
            // EntityBase.Start 可能晚于本组件的 Start，届时属性里还可能是 null（见项目记忆中「动态创建单位的硬性顺序」）。
            health = entity.GetComponent<HealthComponent>();
            combat = entity.GetComponent<CombatComponent>();
            movement = entity.GetComponent<MovementComponent>();
            commandController = entity.GetComponent<PlayerCommandController>();

            // 法力组件允许缺失（没配法力的英雄），因此不参与下面的必需依赖校验。
            mana = entity.GetComponent<ManaComponent>();

            if (health == null)
            {
                Debug.LogError(
                    $"[HeroController] {name} 未挂载 HealthComponent，英雄既不会受伤也不会触发死亡处理。" +
                    "请检查预制体配置。", this);
                enabled = false;
                return;
            }

            health.OnDied += HandleDied;

            if (logHeroEvents)
            {
                Debug.Log(
                    $"[HeroController] {heroDisplayName} 初始化完成（阵营 {entity.Team}，类型 {entity.EntityType}，" +
                    $"生命 {health.MaxHealth:F0}，移速 {(movement != null ? movement.Speed : 0f):F1}，" +
                    $"攻击距离 {(combat != null ? combat.AttackRange : 0f):F1}）。", this);
            }

            // 订阅时可能已经死亡（用代码创建后立刻被击杀、或场景里预置了「已阵亡的英雄」）：
            // 那种情况下 OnDied 早已广播过，事件订阅不会补发，必须主动补一次死亡收尾，
            // 否则英雄会以「已死亡但仍响应输入」的矛盾状态存在。与血条 UI「订阅后必须主动读一次当前血量」同一思路。
            if (health.IsDead)
            {
                Debug.LogWarning(
                    $"[HeroController] {name} 在控制器初始化时已处于死亡状态，直接执行死亡收尾。", this);
                HandleDied();
            }
        }

        /// <summary>
        /// 死亡收尾（单向终态，只执行一次）。
        /// 顺序：停寻路 → 清目标 → 掐断输入 → 禁用代理与碰撞体 → 取消可选中 → 退订 → 广播事件。
        ///
        /// 为什么代理与碰撞体都要禁用：
        /// - NavMeshAgent 只是「停止寻路」仍可能被别的代码重新下达目的地，禁用后物理上不可能再位移，
        ///   这才是 README 3.1「死亡后原地静止」的硬保证；
        /// - 碰撞体与 DeadState 的处理保持一致：尸体不应继续出现在敌人的索敌候选列表里。
        /// 注意 MovementComponent.Stop() 内部对「代理已被禁用」有早退分支，因此这里的顺序（先 Stop 再禁用）是安全的。
        /// </summary>
        private void HandleDied()
        {
            if (hasHandledDeath)
            {
                // OnDied 本身已保证只广播一次，这里是给「订阅时已死亡」的补偿路径兜底，防止两处都触发。
                return;
            }

            hasHandledDeath = true;

            // 1. 停止寻路并清空路径：先做这一步，避免禁用代理时留下半条路径。
            if (movement != null)
            {
                movement.Stop();
            }

            // 2. 清空当前目标。攻击指令存放在 TargetingComponent 中，清空即等价于「撤销未完成的攻击指令」。
            if (entity.Targeting != null)
            {
                entity.Targeting.ClearTarget();
            }

            // 3. 掐断玩家输入。指令层内部另有一道 IsDead 校验，这里是第一道闸门。
            //    AI 英雄（playerControlled == false）跳过：它的输入控制器本来就被工具禁用着，
            //    这里去"禁用"它是无意义操作，而且一旦有人把它当成"AI 英雄也该关的东西"来理解，
            //    就会掩盖"AI 英雄的输入通道是构造上就不存在"这一事实。
            if (playerControlled && disablePlayerControlOnDeath && commandController != null)
            {
                commandController.enabled = false;
            }

            // 4. 物理上锁死位移与索敌参与资格。
            //    【阶段八自审修正】代理的启停统一走 MovementComponent（它才是"唯一驱动 NavMeshAgent 的模块"），
            //    不再在这里 GetComponent<NavMeshAgent>() 直接写 enabled —— 那是绕过封装的分叉实现。
            //    注意 movement 允许为 null（异常配置的英雄没有移动组件），此时静默跳过：
            //    没有代理也就没有"锁死代理"这回事。
            if (movement != null)
            {
                movement.SetAgentEnabled(false);
            }

            if (disableCollidersOnDeath)
            {
                Collider[] colliders = entity.GetComponentsInChildren<Collider>();
                for (int i = 0; i < colliders.Length; i++)
                {
                    colliders[i].enabled = false;
                }
            }

            // 5.（阶段九：白盒隐身已删除）
            //    这里原先调用 EntityVisuals.SetRenderersEnabled(gameObject, false) 把肉身直接隐藏。
            //    那是"没有死亡动画"时期的权宜手段，代价是**尸体会在死亡瞬间凭空消失** ——
            //    接入真实模型与 Die 动画后，死亡表现必须交给表现层（AnimationComponent 的 Die 触发），
            //    由动画自己把单位放倒；逻辑层再抢着隐藏渲染器，只会让死亡动画一帧都播不出来。
            //
            //    与之对称的恢复调用（Revive 里的 SetRenderersEnabled(true)）也已一并删除 ——
            //    同一件事不能留半个写入者，否则"谁在什么时候改渲染器"又要靠推理。

            // 6. 取消可选中。IsValidTarget 已用 Health.IsDead 兜住「死人不是合法目标」，
            //    这里显式再置一次是为了与 DeadState 的处理口径完全一致（避免日后有人只读 IsSelectable 做判断）。
            entity.SetSelectable(false);

            // 7. 退订：英雄只死亡一次，留着订阅没有意义，还会让 HealthComponent 的委托链白占引用。
            health.OnDied -= HandleDied;

            if (logHeroEvents)
            {
                Debug.Log(
                    $"[HeroController] {heroDisplayName} 已阵亡，已停止寻路、退出索敌并关闭玩家输入" +
                    "（外观交给表现层的死亡动画，逻辑层不再隐藏渲染器）。", this);
            }

            // 8. 广播英雄死亡。放在收尾动作之后：订阅方（UI / 结算）读到的已经是最终状态。
            OnHeroDied?.Invoke(this);
        }

        /// <summary>
        /// 复活（阶段七新增）：把 <see cref="HandleDied"/> 做过的一切逆操作按对称顺序撤销，并把英雄搬回复活点。
        ///
        /// 【为什么逆操作必须写在本类里】Collider 与 PlayerCommandController 都是 HandleDied
        /// 亲手禁用的。谁禁用、谁恢复，是唯一能让"死亡与复活"这两条路径保持可推理的方式；
        /// 让外部代码去解锁这些组件，迟早会出现"某处新增了一个禁用动作、但没有对应的恢复动作"的静默失效
        /// （典型症状：复活后英雄动不了，且 Console 一片安静）。
        /// 唯一例外是 NavMeshAgent —— 它的启停与搬运统一由 MovementComponent 负责
        /// （见 MovementComponent.SetAgentEnabled / WarpTo），本类只调用组件方法，不直接持有代理。
        ///
        /// 【调用者】由 MatchController 在复活倒计时结束时调用（配置与对局状态都在它手上），
        /// 本类只负责"执行"，不负责"计时"，也不读任何对局配置。
        ///
        /// 【为什么必须先启用代理再 Warp】NavMeshAgent 在禁用状态下 Warp 不生效——
        /// 顺序反了的症状是"复活了，但人还在原地躺着"，而且不报任何错。
        /// 该顺序现已封装进 MovementComponent.WarpTo（阶段八自审修正），本类不再需要记得它。
        ///
        /// 【为什么 Warp 之后还要校验 isOnNavMesh】复活点若落在 NavMesh 之外（地图改动、出生点被挪），
        /// 英雄会永久卡死且无法移动。该校验同样在 MovementComponent.WarpTo 内部完成（LogError）。
        /// </summary>
        /// <returns>确实执行了复活返回 true；英雄并未处于"已收尾的死亡状态"时返回 false（不做任何修改）。</returns>
        public bool Revive()
        {
            // 只允许复活"已经走完死亡收尾"的英雄。这道闸门同时挡住了两类误调用：
            // 重复调用（第二次进来 hasHandledDeath 已是 false）与对活着的英雄调用。
            if (!hasHandledDeath)
            {
                return false;
            }

            if (entity == null || health == null)
            {
                Debug.LogError(
                    $"[HeroController] {name} 缺少 EntityBase 或 HealthComponent，无法复活。" +
                    "请检查预制体配置。", this);
                return false;
            }

            // 1. 先清收尾标记：之后再死亡时 HandleDied 才会重新执行。
            hasHandledDeath = false;

            // 2. 补回死亡事件订阅。这一步最容易被漏掉：HandleDied 里退订了，
            //    不补订阅的话【第二次死亡不会再触发任何收尾】——英雄会以"已死亡但仍在响应输入"的矛盾状态存在。
            health.OnDied -= HandleDied;
            health.OnDied += HandleDied;

            // 3. 生命与法力复位（内部会主动广播，血条/法力条自动刷新）。
            health.Revive();

            if (mana != null)
            {
                mana.RestoreFull();
            }

            // 4. 先启用代理，再搬运。顺序不可颠倒 —— 该顺序现在由 MovementComponent.WarpTo 内部保证，
            //    调用方不可能弄反（阶段八自审修正：此前这段"先启用再 Warp + 失败降级"的逻辑写在本类里，
            //    而 NavMeshAgent 的启停与搬运都属于 MovementComponent 的职责范围）。
            Vector3 respawnPosition = RespawnPosition;

            if (movement != null)
            {
                if (!movement.WarpTo(respawnPosition))
                {
                    // 失败原因由 WarpTo 内部以 LogError 报出（该点不在 NavMesh 上 / 代理缺失）。
                    // 这里只补一句"是谁在复活"，让 Console 里的两条日志能对上人。
                    Debug.LogWarning(
                        $"[HeroController] {heroDisplayName} 的复活搬运未成功，已按降级路径处理（详见上一条错误日志）。", this);
                }
            }
            else
            {
                // 没有移动组件（异常配置）：至少把坐标摆正，行为与"无法寻路的英雄"一致。
                transform.position = respawnPosition;
            }

            // 5. 恢复碰撞体：与 HandleDied 用同一批（含子节点），否则复活后永久索敌不到、也点不中。
            if (disableCollidersOnDeath)
            {
                Collider[] colliders = entity.GetComponentsInChildren<Collider>(true);
                for (int i = 0; i < colliders.Length; i++)
                {
                    colliders[i].enabled = true;
                }
            }

            // 6.（阶段九：外观恢复已删除）
            //    这里原先调用 EntityVisuals.SetRenderersEnabled(gameObject, true) 与 HandleDied 的隐藏对称。
            //    既然隐藏已删（见 HandleDied 第 5 步的说明），恢复也必须一并删除 ——
            //    留一个"只恢复、不隐藏"的写入者，等于给渲染器留了一个没人知道何时触发的开关。

            // 7. 恢复可选中与玩家输入。
            //    恢复输入同样只在"这是玩家英雄"时才做，理由与 HandleDied 第 3 步完全对称：
            //    对 AI 英雄无条件 enabled = true，会让它复活后开始抢玩家的右键指令。
            entity.SetSelectable(true);

            if (playerControlled && commandController != null)
            {
                commandController.enabled = true;
            }

            // 8. 清掉死亡前残留的路径与攻击指令（否则复活瞬间会顺着旧路径自己跑走）。
            if (movement != null)
            {
                movement.Stop();
            }

            if (entity.Targeting != null)
            {
                entity.Targeting.ClearTarget();
            }

            if (logHeroEvents)
            {
                Debug.Log(
                    $"[HeroController] {heroDisplayName} 已在 {respawnPosition} 复活（满血满蓝，肉身已重现，" +
                    "已恢复寻路、碰撞、选中与玩家输入）。", this);
            }

            // 9. 广播。放在全部逆操作之后：订阅方读到的已经是最终状态。
            OnHeroRespawned?.Invoke(this);
            return true;
        }

        /// <summary>
        /// 兜底退订。物体在存活状态下被销毁（换场景、手动删除）时同样需要解绑，
        /// 否则 HealthComponent 会持有已销毁组件的引用，之后广播 OnDied 时回调到「已销毁的对象」。
        /// </summary>
        private void OnDestroy()
        {
            if (health != null)
            {
                health.OnDied -= HandleDied;
            }
        }
    }
}
