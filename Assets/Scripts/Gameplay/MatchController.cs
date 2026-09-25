using System.Collections;
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

        [Header("调试")]
        [Tooltip("在 Console 输出对局阶段变化（开局准备、出兵启动）。对局结束的胜负日志不受此开关控制，始终输出。")]
        [SerializeField] private bool logMatchEvents = true;

        /// <summary>开局延迟协程句柄。对局在延迟期间就结束时需要掐断它，否则延迟结束仍会启动出兵。</summary>
        private Coroutine startRoutine;

        /// <summary>对局是否已结束。保证"只认第一次基地被摧毁"。</summary>
        private bool isMatchOver;

        /// <summary>对局是否已结束（只读），供对局结果 UI 与调试视图读取。</summary>
        public bool IsMatchOver => isMatchOver;

        /// <summary>
        /// 获胜阵营（只读）。
        /// 对局未结束时为 TeamType.Neutral（"尚未决出"与"中立获胜"共用该值，因为本 Demo 没有中立阵营）。
        /// </summary>
        public TeamType WinnerTeam { get; private set; } = TeamType.Neutral;

        /// <summary>订阅基地摧毁事件。放在 OnEnable 而非 Start：从物体被启用起就应具备判定能力。</summary>
        private void OnEnable()
        {
            BaseCoreController.OnBaseDestroyed += HandleBaseDestroyed;
        }

        /// <summary>
        /// 注销基地摧毁事件。
        /// 必要性：OnBaseDestroyed 是【静态】事件，其委托链的生命周期属于类型而非实例，
        /// 不主动注销就会让已禁用/已销毁的 MatchController 一直被持有并继续接收回调（内存泄漏 + 重复响应）。
        /// </summary>
        private void OnDisable()
        {
            BaseCoreController.OnBaseDestroyed -= HandleBaseDestroyed;
        }

        /// <summary>
        /// 生命周期入口：校验配置并启动"开局延迟 → 出兵"流程。
        /// 放在 Start 而不是 Awake：本类依赖的其他对象（Spawner、基地）此时都已完成自身的 Awake，
        /// 且延迟等待本身就应该从"场景真正跑起来"之后才开始计时。
        /// </summary>
        private void Start()
        {
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
