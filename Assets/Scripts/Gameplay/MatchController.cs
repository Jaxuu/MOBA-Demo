using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using MOBA.AI;
using MOBA.Components;
using MOBA.Core;
using MOBA.Data;
using MOBA.Units;

namespace MOBA.Gameplay
{
    /// <summary>
    /// 对局控制器：管理"准备 → 运行 → 结束"三个对局阶段，完成胜负闭环。
    ///
    /// 职责边界（计划书阶段四）：
    /// 1. 开局：等待 matchConfig.StartDelay 秒后，统一启动所有出兵点（延迟放在这里而不是各个 Spawner 里，
    ///    保证双方出兵时机严格同步，也保证"开局延迟"只有一个配置入口）；
    /// 2. 结束：监听任意一方基地被摧毁，停止全部出兵，并输出获胜方；
    /// 3. 本类【不】直接读写单位状态、不停止场上已有单位的战斗——那是"冻结战场"能力，
    ///    详见类末的已知缺口说明。
    ///
    /// 事件订阅放在 OnEnable / 注销放在 OnDisable（而不是 Start / OnDestroy）：
    /// 这样"组件被禁用期间不会误收事件"，且与 BaseCoreController 的静态事件配合时不会出现
    /// "物体还活着但组件已禁用，却仍在处理对局结束"的错位状态。
    /// </summary>
    [DisallowMultipleComponent]
    public class MatchController : MonoBehaviour
    {
        [Header("配置")]
        [Tooltip("对局全局配置（开局出兵延迟等）。")]
        [SerializeField] private MatchConfigData matchConfig;

        [Tooltip("场上所有出兵点。开局延迟结束后会被统一启动，对局结束时被统一停止。")]
        [SerializeField] private MinionSpawner[] spawners;

        [Header("结束处理")]
        [Tooltip("对局结束时是否冻结战场：停用场上所有 AI 驱动单位与固定建筑的战斗逻辑，并让仍在移动的单位停下。\n" +
                 "对应计划书阶段四验收标准「任一基地生命归零后停止生成单位、停止战斗逻辑」。\n" +
                 "玩家英雄不挂 EntityAIController / TowerController，因此其移动能力不受影响（仍可自由走动）。")]
        [SerializeField] private bool freezeBattlefieldOnMatchEnd = true;

        [Header("英雄复活（阶段七 / 阶段八扩展）")]
        [Tooltip("玩家英雄。复活【倒计时】由本类统筹（配置与对局状态都在本类手上），" +
                 "复活【动作】由 HeroController.Revive 执行（NavMeshAgent / Collider / 输入都是它禁用的）。\n" +
                 "本引用同时是 HUD 与复活遮罩的数据源（IsHeroRespawning / RespawnRemaining 都只看它），" +
                 "因此即使阶段八把复活扩展到全部 10 个英雄，这个字段也必须保留。")]
        [SerializeField] private HeroController playerHero;

        [Tooltip("场上全部英雄（阶段八：蓝 5 + 红 5）。本类为每一个英雄独立编排复活倒计时。\n" +
                 "【为什么需要独立记账】10 个英雄可能同时处于不同的倒计时阶段，" +
                 "阶段七那个「同一时刻只允许一个倒计时」的全局标记会让第二个阵亡的英雄直接把第一个的倒计时吞掉。" +
                 "数组为空时自动退化为只有玩家英雄（兼容阶段七生成的旧场景）。")]
        [SerializeField] private HeroController[] heroes;

        [Tooltip("MatchConfigData 缺失时使用的兜底复活时长（秒）。正常应当配置在 MatchConfigData.RespawnTime。")]
        [Min(0f)]
        [SerializeField] private float respawnFallbackTime = 8f;

        [Header("调试")]
        [Tooltip("在 Console 输出对局阶段变化（开局准备、出兵启动）。对局结束的胜负日志不受此开关控制，始终输出。")]
        [SerializeField] private bool logMatchEvents = true;

        /// <summary>开局延迟协程句柄。对局在延迟期间就结束时需要掐断它，否则延迟结束仍会启动出兵。</summary>
        private Coroutine startRoutine;

        /// <summary>对局是否已结束。保证"只认第一次基地被摧毁"。</summary>
        private bool isMatchOver;

        /// <summary>
        /// 一个英雄的复活倒计时账目（阶段八新增）。
        /// 用"一个英雄一条记录"替代阶段七的单个布尔标记：10 个英雄可能同时处于不同的倒计时阶段，
        /// 共用一个标记会让第二个阵亡的英雄直接把第一个的倒计时吞掉（表现为"某个英雄永远不复活"）。
        /// </summary>
        private sealed class RespawnEntry
        {
            /// <summary>正在倒计时的英雄。</summary>
            public HeroController Hero;

            /// <summary>该英雄的倒计时协程句柄，用于对局结束时精确掐断。</summary>
            public Coroutine Routine;

            /// <summary>该英雄的复活时刻（Time.time 基准）。</summary>
            public float EndTime;
        }

        /// <summary>
        /// 正在倒计时的英雄清单。
        /// 每局最多 10 条，条目的对象分配发生在"某英雄阵亡"这一刻（一局至多 10 次 × 若干轮），
        /// 不在每帧路径上，因此不会影响稳态 GC 目标。
        /// </summary>
        private readonly List<RespawnEntry> respawnEntries = new List<RespawnEntry>();

        /// <summary>
        /// 参与复活编排的英雄数量。
        /// 优先用 heroes 数组（阶段八，工具注入 10 个）；数组为空时退化为只有玩家英雄，
        /// 这样阶段七生成的旧场景不需要重新组装也能继续工作。
        /// </summary>
        private int HeroCount
        {
            get
            {
                if (heroes != null && heroes.Length > 0)
                {
                    return heroes.Length;
                }

                return playerHero != null ? 1 : 0;
            }
        }

        /// <summary>对局是否已结束（只读），供对局结果 UI 与调试视图读取。</summary>
        public bool IsMatchOver => isMatchOver;

        /// <summary>玩家英雄（只读）。</summary>
        public HeroController PlayerHero => playerHero;

        /// <summary>
        /// 【玩家英雄】是否正在复活倒计时中（只读）。
        /// 刻意只反映玩家英雄：这个查询的消费方是复活遮罩 UI，
        /// 而 AI 英雄阵亡不该在玩家屏幕上弹出"已阵亡"遮罩。
        /// </summary>
        public bool IsHeroRespawning => FindRespawnEntry(playerHero) != null;

        /// <summary>
        /// 【玩家英雄】的复活倒计时剩余秒数（只读）。不在倒计时中时恒为 0。
        /// 【为什么由 UI 每帧读它，而不是每帧广播事件】倒计时是一个连续递减量，
        /// 每帧广播事件等于每帧产生一次委托调用与字符串分配；读一个 float 减法则是零成本。
        /// 事件只负责"开始 / 完成"两个跳变（与技能冷却遮罩同一套取舍）。
        /// </summary>
        public float RespawnRemaining
        {
            get
            {
                RespawnEntry entry = FindRespawnEntry(playerHero);
                return entry != null ? Mathf.Max(0f, entry.EndTime - Time.time) : 0f;
            }
        }

        /// <summary>复活倒计时开始事件：参数为（英雄, 倒计时总时长秒）。</summary>
        public event Action<HeroController, float> OnRespawnCountdownStarted;

        /// <summary>复活完成事件：参数为已复活的英雄。在 HeroController.Revive 成功之后广播。</summary>
        public event Action<HeroController> OnRespawnCompleted;

        /// <summary>
        /// 获胜阵营（只读）。
        /// 对局未结束时为 TeamType.Neutral（"尚未决出"与"中立获胜"共用该值，因为本 Demo 没有中立阵营）。
        /// </summary>
        public TeamType WinnerTeam { get; private set; } = TeamType.Neutral;

        /// <summary>订阅基地摧毁事件。放在 OnEnable 而非 Start：从物体被启用起就应具备判定能力。</summary>
        private void OnEnable()
        {
            BaseCoreController.OnBaseDestroyed += HandleBaseDestroyed;

            // 英雄死亡事件同样在这里订阅：复活倒计时的启停属于对局级编排，
            // 与"对局是否已结束"必须由同一个对象裁决。
            SubscribeHeroDeathEvents();
        }

        /// <summary>
        /// 注销基地摧毁事件。
        /// 必要性：OnBaseDestroyed 是【静态】事件，其委托链的生命周期属于类型而非实例，
        /// 不主动注销就会让已禁用/已销毁的 MatchController 一直被持有并继续接收回调（内存泄漏 + 重复响应）。
        /// 英雄的死亡事件是实例事件，同样必须退订（否则已禁用的本对象仍会启动复活倒计时）。
        /// </summary>
        private void OnDisable()
        {
            BaseCoreController.OnBaseDestroyed -= HandleBaseDestroyed;

            UnsubscribeHeroDeathEvents();

            // 被禁用时掐断全部倒计时：协程不会因为 MonoBehaviour.enabled = false 而自动停止，
            // 留着它们会让"已禁用的对局控制器"仍然把英雄复活。
            StopRespawnCountdown();
        }

        /// <summary>
        /// 订阅全部英雄的死亡事件。
        /// 采用"先退订再订阅"的写法，保证本方法重复调用（组件被反复启停）时
        /// 不会造成同一次死亡触发多遍处理。
        /// </summary>
        private void SubscribeHeroDeathEvents()
        {
            int count = HeroCount;

            for (int i = 0; i < count; i++)
            {
                HeroController hero = GetHero(i);
                if (hero == null)
                {
                    continue;
                }

                hero.OnHeroDied -= HandleHeroDied;
                hero.OnHeroDied += HandleHeroDied;
            }
        }

        /// <summary>退订全部英雄的死亡事件。与 SubscribeHeroDeathEvents 严格对称。</summary>
        private void UnsubscribeHeroDeathEvents()
        {
            int count = HeroCount;

            for (int i = 0; i < count; i++)
            {
                HeroController hero = GetHero(i);
                if (hero == null)
                {
                    continue;
                }

                hero.OnHeroDied -= HandleHeroDied;
            }
        }

        /// <summary>
        /// 按索引取英雄。
        /// 数组为空时退化为"只有玩家英雄"，与 HeroCount 的取值口径必须完全一致——
        /// 两处口径不一致会导致"订阅了 10 个、退订只退了 1 个"这类悬空订阅。
        /// </summary>
        /// <param name="index">英雄索引。</param>
        /// <returns>对应英雄；索引越界或引用为空时返回 null。</returns>
        private HeroController GetHero(int index)
        {
            if (heroes != null && heroes.Length > 0)
            {
                return index >= 0 && index < heroes.Length ? heroes[index] : null;
            }

            return index == 0 ? playerHero : null;
        }

        /// <summary>查找某个英雄的复活账目；不在倒计时中返回 null。</summary>
        /// <param name="hero">待查询的英雄，允许为 null。</param>
        private RespawnEntry FindRespawnEntry(HeroController hero)
        {
            if (hero == null)
            {
                return null;
            }

            for (int i = 0; i < respawnEntries.Count; i++)
            {
                if (ReferenceEquals(respawnEntries[i].Hero, hero))
                {
                    return respawnEntries[i];
                }
            }

            return null;
        }

        /// <summary>
        /// 生命周期入口：校验配置并启动"开局延迟 → 出兵"流程。
        /// 放在 Start 而不是 Awake：本类依赖的其他对象（Spawner、基地）此时都已完成自身的 Awake，
        /// 且延迟等待本身就应该从"场景真正跑起来"之后才开始计时。
        /// </summary>
        private void Start()
        {
            if (playerHero == null)
            {
                Debug.LogWarning(
                    $"[MatchController] {name} 未指定玩家英雄，玩家英雄阵亡后将不会复活，" +
                    "HUD 与复活遮罩也拿不到数据源。" +
                    "请执行 MOBA Demo/一键组装测试战场 重新生成场景（该引用由工具注入）。", this);
            }

            if (heroes == null || heroes.Length == 0)
            {
                // 不报错：数组为空时会退化为"只复活玩家英雄"，阶段七生成的旧场景仍可运行。
                Debug.LogWarning(
                    $"[MatchController] {name} 未配置英雄数组（Heroes），复活编排将只覆盖玩家英雄。" +
                    "阶段八的 5v5 场景请执行 MOBA Demo/一键组装测试战场 重新生成（工具会注入全部 10 个英雄）。", this);
            }

            if (matchConfig == null)
            {
                Debug.LogError(
                    $"[MatchController] {name} 未配置 MatchConfigData，无法确定开局延迟，对局不会开始。" +
                    "请在 Inspector 的 Match Config 字段指定配置资产。", this);
                return;
            }

            if (spawners == null || spawners.Length == 0)
            {
                Debug.LogError(
                    $"[MatchController] {name} 未配置任何 MinionSpawner，双方都不会出兵，" +
                    "对局无法推进。请在 Inspector 的 Spawners 数组中加入双方的出兵点。", this);
                return;
            }

            startRoutine = StartCoroutine(StartMatchAfterDelay());
        }

        /// <summary>
        /// 开局流程：等待配置的开局延迟，然后统一启动所有出兵点。
        /// </summary>
        private IEnumerator StartMatchAfterDelay()
        {
            float delay = Mathf.Max(0f, matchConfig.StartDelay);

            if (logMatchEvents)
            {
                Debug.Log($"[MatchController] 对局准备中，{delay:F1} 秒后开始出兵。", this);
            }

            // WaitForSeconds(0) 也会让出一帧，因此这里不必为 delay == 0 单独分支。
            yield return new WaitForSeconds(delay);

            startRoutine = null;

            // 延迟期间对局可能已经结束（例如测试场景里基地是初始就死的）：
            // 此时不能再出兵，否则会出现"对局已结束、兵线还在生产"的矛盾状态。
            //
            // 【必须用 yield break 而不是 return】本方法是迭代器（方法体内有 yield return），
            // C# 不允许在迭代器块里写普通的 return;（会报 CS1622），结束迭代只有 yield break 一种写法。
            if (isMatchOver)
            {
                yield break;
            }

            StartAllSpawners();
        }

        /// <summary>统一启动所有出兵点。数组中的空槽位（Inspector 留空）会被跳过。</summary>
        private void StartAllSpawners()
        {
            int startedCount = 0;
            int validCount = 0;

            for (int i = 0; i < spawners.Length; i++)
            {
                MinionSpawner spawner = spawners[i];

                // 空槽位与"已被销毁的对象"都会命中 Unity 重载过的 == null，一并跳过。
                if (spawner == null)
                {
                    continue;
                }

                validCount++;

                // 按实际启动结果计数：StartSpawning 可能因为配置缺失或对象未激活而拒绝启动，
                // 那种情况下兵线不会出，日志必须如实反映，否则会掩盖问题。
                if (spawner.StartSpawning())
                {
                    startedCount++;
                }
            }

            if (startedCount < validCount)
            {
                Debug.LogError(
                    $"[MatchController] 共有 {validCount} 个出兵点，但只有 {startedCount} 个成功启动，" +
                    "对局无法正常推进。请检查上方 MinionSpawner 报出的具体原因。", this);
            }

            if (logMatchEvents)
            {
                Debug.Log($"[MatchController] 对局开始，已启动 {startedCount} / {validCount} 个出兵点。", this);
            }
        }

        /// <summary>统一停止所有出兵点。对局结束时调用，幂等（重复调用无副作用）。</summary>
        private void StopAllSpawners()
        {
            if (spawners == null)
            {
                return;
            }

            for (int i = 0; i < spawners.Length; i++)
            {
                MinionSpawner spawner = spawners[i];
                if (spawner == null)
                {
                    continue;
                }

                spawner.StopSpawning();
            }
        }

        /// <summary>
        /// 基地被摧毁回调：结束对局。
        ///
        /// 只认第一次触发：两个基地有可能在同一帧内相继归零（例如同一次伤害结算波及双方），
        /// 若不做这个判断，胜负会被后一次事件覆盖，甚至出现"双方都赢"的日志。
        /// </summary>
        /// <param name="destroyedTeam">被摧毁基地的阵营，由 BaseCoreController 广播。</param>
        private void HandleBaseDestroyed(TeamType destroyedTeam)
        {
            if (isMatchOver)
            {
                return;
            }

            isMatchOver = true;

            // 掐断还没走完的开局延迟：否则延迟结束时仍会把兵线放出来。
            if (startRoutine != null)
            {
                StopCoroutine(startRoutine);
                startRoutine = null;
            }

            StopAllSpawners();

            // 复活倒计时必须一并掐断：否则"对局已结束"之后倒计时走完，英雄会自己站起来，
            // 与 README「对局结束时不再复活」直接冲突。
            StopRespawnCountdown();

            // 停止战斗逻辑（阶段四验收：「停止生成单位、停止战斗逻辑」）。
            // 只停生成是不够的——场上已有单位仍会继续互相攻击，那与「对局已结束」自相矛盾。
            if (freezeBattlefieldOnMatchEnd)
            {
                FreezeBattlefield();
            }

            WinnerTeam = GetOpponentTeam(destroyedTeam);

            // 这条日志【不受 logMatchEvents 开关控制】：它是本类唯一的对外结果输出，
            // 也是对局结束时最需要留痕的一条信息（每局仅一条，不存在刷屏风险）。
            // 阵营用中文描述而不是直接打印枚举名：验收时是人在看 Console，
            // "Player 方基地被摧毁"读起来不如"玩家方基地被摧毁"直观。
            Debug.Log(
                $"[MatchController] 对局结束：{DescribeTeam(destroyedTeam)}基地被摧毁，" +
                $"{DescribeTeam(WinnerTeam)}获胜。", this);
        }

        /// <summary>把阵营枚举转成便于阅读的中文描述，仅用于日志。</summary>
        private static string DescribeTeam(TeamType team)
        {
            switch (team)
            {
                case TeamType.Player:
                    return "玩家方";
                case TeamType.Enemy:
                    return "敌方";
                default:
                    return "中立方";
            }
        }

        /// <summary>
        /// 取对立阵营，即获胜方。
        /// 基地被摧毁 = 另一方获胜，因此这里是"被摧毁方"的对立面。
        /// Neutral 是防御性兜底：本 Demo 没有中立基地，正常不会走到。
        ///
        /// 公开为 static：调试视图（MatchDebugView）也要把"被摧毁方"换算成"获胜方"来显示状态。
        /// 这条换算规则只能有一处实现，否则两处口径迟早不一致（例如日后加入三方阵营时漏改一处）。
        /// </summary>
        public static TeamType GetOpponentTeam(TeamType team)
        {
            if (team == TeamType.Player)
            {
                return TeamType.Enemy;
            }

            if (team == TeamType.Enemy)
            {
                return TeamType.Player;
            }

            return TeamType.Neutral;
        }

        /// <summary>
        /// 冻结战场：把场上所有「会自己动手」的单位停掉，让对局真正结束。
        ///
        /// 具体停哪些（按组件能力而非按单位类型判断，因此不依赖任何枚举分支）：
        /// 1. EntityAIController —— 可移动单位（小兵）的行为大脑，停用后不再 Tick 状态机，
        ///    既不会再索敌/追击/攻击，也不会再下达移动指令；
        /// 2. TowerController —— 固定建筑（防御塔）的直线战斗逻辑，停用后不再索敌与攻击；
        /// 3. MovementComponent.Stop() —— 让仍在滑行/寻路的单位立刻停下并清空路径，
        ///    否则被冻结的单位会带着旧路径继续滑一段，看起来像"对局结束后还在跑"。
        ///
        /// 为什么用注册表而不是 FindObjectsOfType：
        /// 全场景扫描表达不出「谁算参战单位」，且会一次性分配一个包含场景全部对象的数组。
        /// EntityRegistry 由 EntityBase 自行登记，天然只含参战单位，新增单位类型无需改本方法。
        ///
        /// 刻意【不】停用 HealthComponent / 不销毁物体：冻结只针对「战斗与移动行为」，
        /// 保留生命组件与物体便于验收时观察战场终态（各单位的剩余血量）。
        /// 也刻意【不】触碰玩家输入：玩家英雄不挂上述两个控制器，因此冻结后仍可自由走动，
        /// 这是最保守的选择——"对局结束后是否允许玩家继续操作"属产品决策，本类不代为决定。
        ///
        /// 单向操作：本 Demo 没有"重开一局"的流程，因此不提供解冻。
        /// </summary>
        private void FreezeBattlefield()
        {
            // 取快照而不是直接遍历注册表：本方法会停用组件，期间若有实体被启用/禁用，
            // 注册表会被增删（OnEnable/OnDisable），直接遍历内部集合会抛 InvalidOperationException。
            EntityBase[] snapshot = EntityRegistry.Snapshot();

            int frozenCount = 0;

            for (int i = 0; i < snapshot.Length; i++)
            {
                EntityBase entity = snapshot[i];

                // 快照里的对象可能在取快照之后被销毁（Unity 重载的 == 能识别这种情况）。
                if (entity == null)
                {
                    continue;
                }

                bool frozen = false;

                EntityAIController ai = entity.GetComponent<EntityAIController>();
                if (ai != null && ai.enabled)
                {
                    ai.enabled = false;
                    frozen = true;
                }

                TowerController tower = entity.GetComponent<TowerController>();
                if (tower != null && tower.enabled)
                {
                    tower.enabled = false;
                    frozen = true;
                }

                // 防御塔、基地没有移动组件，Stop 前必须判空（与项目既定的建筑约定一致）。
                MovementComponent movement = entity.Movement;
                if (movement != null)
                {
                    movement.Stop();
                    frozen = true;
                }

                if (frozen)
                {
                    frozenCount++;
                }
            }

            Debug.Log(
                $"[MatchController] 战场已冻结：{frozenCount} / {snapshot.Length} 个单位的 AI 与移动逻辑已停止" +
                "（本局不再产生任何攻击与位移）。", this);
        }

        /// <summary>
        /// 英雄死亡回调：为该英雄启动一条独立的复活倒计时（阶段八：10 个英雄各自记账）。
        ///
        /// 本类只负责"什么时候复活"，不负责"怎么复活"——后者是 <see cref="HeroController.Revive"/> 的事。
        /// 这样划分的依据：NavMeshAgent / Collider / PlayerCommandController 都是 HeroController.HandleDied
        /// 亲手禁用的，谁禁用谁恢复才不会出现"解锁动作漏了一个"的静默失效；
        /// 而复活时长（MatchConfigData）与"对局是否已结束"（IsMatchOver）都天然属于本类。
        /// </summary>
        /// <param name="hero">阵亡的英雄。</param>
        private void HandleHeroDied(HeroController hero)
        {
            if (hero == null)
            {
                return;
            }

            // 对局已结束 → 不再复活（README 明确要求）。
            if (isMatchOver)
            {
                if (logMatchEvents)
                {
                    Debug.Log($"[MatchController] 对局已结束，{hero.HeroDisplayName} 不再复活。", this);
                }

                return;
            }

            // 防重入：同一英雄同时只允许一条倒计时。
            // 与阶段七的差别：闸门从"全局唯一"收紧为"该英雄唯一"——
            // 10 个英雄可能同时阵亡，全局唯一会让后阵亡者的倒计时被直接吞掉（症状是它永远不复活）。
            if (FindRespawnEntry(hero) != null)
            {
                return;
            }

            float delay = matchConfig != null
                ? Mathf.Max(0f, matchConfig.RespawnTime)
                : Mathf.Max(0f, respawnFallbackTime);

            RespawnEntry entry = new RespawnEntry
            {
                Hero = hero,
                EndTime = Time.time + delay
            };

            // 先入账、再启动协程：StartCoroutine 会【同步】执行协程体到第一个 yield，
            // 也就是 OnRespawnCountdownStarted 会在赋值语句之前就广播出去；
            // 若那时才入账，订阅方（复活遮罩）读到的会是"没在复活"。
            respawnEntries.Add(entry);
            entry.Routine = StartCoroutine(RespawnRoutine(entry, delay));
        }

        /// <summary>
        /// 复活流程：广播倒计时开始 → 等待 → 调 HeroController.Revive → 复位 AI → 广播完成。
        ///
        /// 【为什么 delay 为 0 也走协程】WaitForSeconds(0) 仍会让出至少一帧，
        /// 从而避免"在 HealthComponent.OnDied 的广播链里同步复活"——
        /// 那会让排在后面的订阅者收到"一个已经活过来的英雄的死亡事件"，状态自相矛盾。
        /// </summary>
        /// <param name="entry">本次复活的账目（承载英雄与复活时刻）。</param>
        /// <param name="delay">倒计时时长（秒）。</param>
        private IEnumerator RespawnRoutine(RespawnEntry entry, float delay)
        {
            HeroController hero = entry.Hero;

            if (logMatchEvents)
            {
                Debug.Log($"[MatchController] {hero.HeroDisplayName} 已阵亡，{delay:F1} 秒后在出生点复活。", this);
            }

            OnRespawnCountdownStarted?.Invoke(hero, delay);

            yield return new WaitForSeconds(delay);

            // 先出账：此后任何"是否正在复活"的查询都不应再看到这条记录。
            respawnEntries.Remove(entry);

            // 倒计时期间对局可能已经结束：此时不能再把英雄放出来。
            // 【必须用 yield break 而不是 return】本方法是迭代器，C# 不允许在迭代器块里写普通的 return;（CS1622）。
            if (isMatchOver)
            {
                yield break;
            }

            // 英雄可能在倒计时期间被销毁（换场景 / 手工删除）。
            if (hero == null)
            {
                yield break;
            }

            if (hero.Revive())
            {
                // 阶段八：AI 英雄的 FSM 停在 DeadState（单向终态，无出口），必须显式复位，
                // 否则症状是"复活了、看得见、也能被点中，但永远站着不动、不还手"，且 Console 一片安静。
                // 玩家英雄刻意不挂 EntityAIController，GetComponent 返回 null，天然跳过这一步。
                EntityAIController ai = hero.GetComponent<EntityAIController>();
                if (ai != null)
                {
                    ai.ReviveAI();
                }

                OnRespawnCompleted?.Invoke(hero);
            }
        }

        /// <summary>
        /// 掐断全部复活倒计时（对局结束、组件被禁用时调用）。幂等。
        /// 只清状态与协程句柄，不广播任何事件 —— 调用方（对局结束）自己会给出更强的视觉结论（结算界面）。
        /// </summary>
        private void StopRespawnCountdown()
        {
            for (int i = 0; i < respawnEntries.Count; i++)
            {
                RespawnEntry entry = respawnEntries[i];

                if (entry.Routine != null)
                {
                    StopCoroutine(entry.Routine);
                    entry.Routine = null;
                }
            }

            respawnEntries.Clear();
        }

        // ------------------------------------------------------------------
        // 已知缺口（刻意未实现，等确认后再补）
        //
        // 1. 「显示胜负结果」的正式 UI（计划书 2 节列出的 UI/MatchResultView.cs）尚未实现。
        //    目前胜负结果只有两处出口：Console 日志（本类 HandleBaseDestroyed）与
        //    Debug/MatchDebugView 的 OnGUI 调试面板。调试面板不是验收 UI——
        //    它挂在场景里就显示，不满足「结算界面」的产品形态，做正式 UI 时再补。
        //
        // 2. 冻结战场是单向的。若后续要做"再来一局"，需要把冻结改成可逆
        //    （记录被停用的组件并逐个恢复），或者干脆走场景重载。
        // ------------------------------------------------------------------
    }
}
