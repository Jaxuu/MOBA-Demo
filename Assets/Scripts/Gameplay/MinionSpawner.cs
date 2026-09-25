using System.Collections;
using UnityEngine;
using MOBA.AI;
using MOBA.Core;
using MOBA.Data;

namespace MOBA.Gameplay
{
    /// <summary>
    /// 小兵生成器：按配置的波次节奏持续生成小兵，并把路线与阵营注入到新生成的小兵上。
    ///
    /// 职责边界（计划书阶段四）：
    /// 1. 只负责"什么时候生成、生成在哪、生成后配置什么"，不参与小兵的行为逻辑（那是 FSM 的事）；
    /// 2. 一个 Spawner 对应"一方的一条兵线"：阵营 + 出生点 + 推进路线三者绑定，
    ///    因此蓝红双方各挂一个，靠 Inspector 配置区分，代码里没有阵营分支；
    /// 3. 生成节奏由 MatchController 统一控制（开局延迟后调 StartSpawning，对局结束调 StopSpawning），
    ///    本类不自作主张在 Start 里出兵——否则开局延迟就形同虚设。
    ///
    /// 预制体要求：小兵预制体必须挂载 EntityBase + EntityAIController + HealthComponent
    /// + TargetingComponent + CombatComponent + MovementComponent（含 NavMeshAgent）。
    /// </summary>
    [DisallowMultipleComponent]
    public class MinionSpawner : MonoBehaviour
    {
        [Header("配置")]
        [Tooltip("波次生成配置（每波数量 / 波内间隔 / 波次间隔 / 小兵预制体）。")]
        [SerializeField] private MinionSpawnData spawnData;

        [Tooltip("本出生点生成的小兵所属阵营。蓝红双方各挂一个 Spawner，靠这里区分。")]
        [SerializeField] private TeamType team = TeamType.Player;

        [Tooltip("出生点。小兵的位置与初始朝向都取自该 Transform，因此策划可用空物体的旋转控制初始面向。")]
        [SerializeField] private Transform spawnPoint;

        [Tooltip("注入给小兵的推进路线。蓝红双方使用【节点顺序相反】的两条 LanePath 来表达相反的行进方向。")]
        [SerializeField] private LanePath targetLane;

        [Header("调试")]
        [Tooltip("在 Console 输出每次生成小兵的记录。小兵数量多时比较吵，默认关闭。")]
        [SerializeField] private bool logSpawnEvents = false;

        /// <summary>当前正在运行的生成协程句柄。null 表示未在出兵。</summary>
        private Coroutine spawnRoutine;

        /// <summary>累计生成数量，供调试视图与验收核对（"连续生成至少 10 个小兵"）。</summary>
        private int totalSpawnedCount;

        /// <summary>是否已就"波次节奏配置异常"告过警，防止每波重复打印。</summary>
        private bool hasReportedRhythmIssue;

        /// <summary>是否正在出兵（只读）。</summary>
        public bool IsSpawning => spawnRoutine != null;

        /// <summary>累计生成的小兵数量（只读）。</summary>
        public int TotalSpawnedCount => totalSpawnedCount;

        /// <summary>本出生点的阵营（只读）。</summary>
        public TeamType Team => team;

        /// <summary>
        /// 启动出兵。
        /// 允许被重复调用（第二次起会被忽略并告警），这样 MatchController 即使在 OnEnable 重入
        /// 或场景里已有 Spawner 自行启动的情况下也不会生成出两套兵线。
        /// </summary>
        /// <returns>本次调用确实启动了出兵协程返回 true；已在进行中、配置不合法或组件未激活时返回 false。</returns>
        public bool StartSpawning()
        {
            if (spawnRoutine != null)
            {
                Debug.LogWarning($"[MinionSpawner] {name} 已在出兵中，本次 StartSpawning 被忽略。", this);
                return false;
            }

            // 【协程生命周期】协程只能由"已激活且已启用"的组件启动。
            // 若出生点在层级里被关掉（调试时非常常见的操作），StartCoroutine 会直接抛 Unity 内部错误
            // 并且【不返回协程句柄】——那样 spawnRoutine 仍是 null，外部会误以为"已经在出兵"，
            // 兵线却一条都不出。这里提前拦下并给出可操作的提示。
            if (!isActiveAndEnabled)
            {
                Debug.LogError(
                    $"[MinionSpawner] {name} 当前处于未激活或已禁用状态，无法启动出兵协程。" +
                    "请在 Hierarchy 中启用该对象后再开始对局。", this);
                return false;
            }

            if (!ValidateConfiguration())
            {
                return false;
            }

            ValidateSpawnRhythm();

            spawnRoutine = StartCoroutine(SpawnLoop());

            // 理论上 StartCoroutine 不会返回 null（上面的激活状态已校验），这里只做防御性兜底：
            // 万一返回 null，说明协程没起来，不能留下"看起来在出兵"的假象。
            return spawnRoutine != null;
        }

        /// <summary>
        /// 停止出兵。对局结束时由 MatchController 调用。
        /// 已生成的小兵不受影响（它们的生死由各自的 FSM 与生命组件决定），本方法只掐断"继续生产"。
        /// </summary>
        public void StopSpawning()
        {
            if (spawnRoutine == null)
            {
                return;
            }

            StopCoroutine(spawnRoutine);
            spawnRoutine = null;
        }

        /// <summary>
        /// 组件被禁用或物体被销毁时收尾。
        /// 必要性：Unity 会在组件禁用时自动停掉它启动的协程，但【协程句柄不会自动清空】。
        /// 若不清空，重新启用后 StartSpawning 会误判"已在出兵"而拒绝启动，兵线再也出不来。
        /// </summary>
        private void OnDisable()
        {
            StopSpawning();
        }

        /// <summary>
        /// 配置校验。任何一项缺失都会让"出兵"这件事整体失效，因此用 LogError 明确报出并放弃启动，
        /// 而不是启动一个每波都抛空引用的协程。
        /// </summary>
        /// <returns>配置完整返回 true。</returns>
        private bool ValidateConfiguration()
        {
            if (spawnData == null)
            {
                Debug.LogError(
                    $"[MinionSpawner] {name} 未配置 MinionSpawnData，无法出兵。" +
                    "请在 Inspector 的 Spawn Data 字段指定波次配置资产。", this);
                return false;
            }

            if (spawnData.MinionPrefab == null)
            {
                Debug.LogError(
                    $"[MinionSpawner] {name} 的 MinionSpawnData（{spawnData.name}）未指定小兵预制体，无法出兵。", this);
                return false;
            }

            if (spawnPoint == null)
            {
                Debug.LogError(
                    $"[MinionSpawner] {name} 未配置出生点 Transform，无法确定小兵生成位置。", this);
                return false;
            }

            if (targetLane == null)
            {
                // 不报 Error 的原因：小兵预制体上可能已经直挂了 LanePath，InitializeAI(null) 会沿用那个引用。
                // 真正的"无路可走"会在 EntityAIController 的初始化日志里以"未配置 LanePath"明确暴露出来。
                Debug.LogWarning(
                    $"[MinionSpawner] {name} 未配置 Target Lane，将沿用预制体上已直挂的路线；" +
                    "若预制体上也没有配置，小兵将无法推进。", this);
            }

            return true;
        }

        /// <summary>
        /// 波次节奏合理性检查（一次性告警）。
        /// 目的：把"配置写错导致行为诡异"提前说出来，而不是让策划对着满屏小兵猜原因。
        /// </summary>
        private void ValidateSpawnRhythm()
        {
            if (hasReportedRhythmIssue)
            {
                return;
            }

            int waveCount = Mathf.Max(1, spawnData.WaveCount);

            if (spawnData.WaveInterval <= 0f)
            {
                hasReportedRhythmIssue = true;
                Debug.LogWarning(
                    $"[MinionSpawner] {name} 的 Wave Interval 为 0，下一波会在上一波出完后立刻开始，" +
                    "出兵速度将不受控，请确认是否为配置错误。", this);
                return;
            }

            // 波内总耗时 = (数量 - 1) × 波内间隔；它一旦不小于波次间隔，两波就会重叠。
            // 重叠本身不会报错（代码按"上一波首个小兵出生时刻"为基准计时），但通常不是策划的本意。
            float waveDuration = (waveCount - 1) * Mathf.Max(0f, spawnData.SpawnInterval);
            if (waveDuration >= spawnData.WaveInterval)
            {
                hasReportedRhythmIssue = true;
                Debug.LogWarning(
                    $"[MinionSpawner] {name} 的波内生成耗时（{waveDuration:F1} 秒）已不小于波次间隔" +
                    $"（{spawnData.WaveInterval:F1} 秒），下一波会在上一波尚未出完时就开始，请确认配置。", this);
            }
        }

        /// <summary>
        /// 出兵主循环。
        ///
        /// 计时基准：波次间隔以【上一波首个小兵出生的时刻】为准，即"每 N 秒必定开始一波"，
        /// 而不是"上一波出完后再等 N 秒"。后者会让实际波次周期随每波数量漂移，
        /// 与计划书验收标准"按固定间隔自动生成小兵"不符。
        /// </summary>
        private IEnumerator SpawnLoop()
        {
            int waveCount = Mathf.Max(1, spawnData.WaveCount);

            while (true)
            {
                float waveStartTime = Time.time;

                for (int i = 0; i < waveCount; i++)
                {
                    SpawnOne();

                    // 波内最后一个不等待：等待时间统一由下面的波次间隔承担。
                    // Mathf.Max 兜住 0 值，避免"间隔填 0"时协程在一帧内把整波出完（会瞬间刷出大量单位）。
                    if (i < waveCount - 1)
                    {
                        yield return new WaitForSeconds(Mathf.Max(0.02f, spawnData.SpawnInterval));
                    }
                }

                // 用"已消耗时间"倒推剩余等待，保证波次周期固定。
                // remaining <= 0 时说明这一波出得太慢（波内耗时超过了波次间隔），直接进入下一波，
                // 不做补偿性追赶——补偿会让后续几波在同一帧连续爆发。
                float remaining = spawnData.WaveInterval - (Time.time - waveStartTime);
                if (remaining > 0f)
                {
                    yield return new WaitForSeconds(remaining);
                }
            }
        }

        /// <summary>
        /// 生成一个小兵并完成配置注入。
        /// 生成失败（预制体缺关键组件）时会立刻销毁实例：一个没有 EntityBase/AI 的"半成品"
        /// 留在场景里只会变成无法交互的垃圾对象，还会干扰后续排查。
        /// </summary>
        private void SpawnOne()
        {
            GameObject instance = Instantiate(spawnData.MinionPrefab, spawnPoint.position, spawnPoint.rotation);

            EntityBase entity = instance.GetComponent<EntityBase>();
            EntityAIController ai = instance.GetComponent<EntityAIController>();

            if (entity == null || ai == null)
            {
                Debug.LogError(
                    $"[MinionSpawner] {name} 生成的小兵预制体缺少 {(entity == null ? "EntityBase" : "EntityAIController")}，" +
                    "该小兵已被销毁。请检查预制体配置。", this);
                Destroy(instance);
                return;
            }

            // 【顺序不可颠倒】必须先 EntityBase.Initialize，再 EntityAIController.InitializeAI。
            // 原因：Initialize 负责注入阵营与静态配置（生命 / 移速 / 索敌半径 / 攻击数据），
            // 而 InitializeAI 会创建状态并启动状态机；状态在 Tick 中读取的攻击距离、索敌半径
            // 全部来自刚注入的配置，顺序反了就会读到组件默认值，状态跳转判断直接失准。
            //
            // 第一个参数显式回传预制体上已配置的 statsData：Initialize 的约定是"传 null 表示沿用 Inspector 值"，
            // 这里不依赖该约定，写法更直白。
            entity.Initialize(entity.StatsData, team, EntityType.Minion);

            // 注入推进路线。传 null 时 InitializeAI 会沿用预制体上直挂的 LanePath。
            ai.InitializeAI(targetLane);

            totalSpawnedCount++;

            if (logSpawnEvents)
            {
                Debug.Log(
                    $"[MinionSpawner] {name} 生成第 {totalSpawnedCount} 个小兵：{instance.name}" +
                    $"（阵营 {team}，出生点 {spawnPoint.name}）。", this);
            }
        }
    }
}
