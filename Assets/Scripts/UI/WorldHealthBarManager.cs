using System.Collections.Generic;
using UnityEngine;
using MOBA.Components;
using MOBA.Core;

namespace MOBA.UI
{
    /// <summary>
    /// 头顶血条管理器（阶段七）：决定"谁该有血条"，并驱动全部血条的位置、朝向与缩放。
    ///
    /// 【为什么由中央管理器挂载，而不是让每个单位自己挂】
    /// 让单位自己挂（在 HeroPrefab / MinionPrefab 上加一个 HealthBarBinder 组件）有三个代价：
    /// 1. 逻辑层预制体上出现了"UI 挂点"这个概念，与「逻辑层禁止引用 UI」擦边；
    /// 2. 每新增一种单位（塔、基地、日后的野怪）都要记得给它加；
    /// 3. MinionSpawner 动态生成的小兵需要额外确认它确实挂了。
    /// 改成"管理器订阅 EntityRegistry 的登记/注销事件"之后：
    /// 单位预制体【零改动】，任何新单位自动获得血条，且禁用本管理器就等于"关掉全部血条"——
    /// 对局结果完全不变（表现层可整体关闭，铁律 4）。
    ///
    /// 【阶段八实机修复：三层挂载保障】「某单位没有血条」曾经是个纯静默失效 —— 登记事件只广播一次，
    /// 一旦那一次没挂上（池耗尽 / 相机未就绪 / 管理器的 OnEnable 与实体登记交错），该单位就永久没有血条。
    /// 现在三道防线叠加，任何一道生效都能补齐：
    ///   ① 事件订阅（实时路径，覆盖绝大多数情况）；
    ///   ② 首帧末的全量 Snapshot 补挂（覆盖「订阅晚于登记」的窗口，且打汇总日志留痕）；
    ///   ③ 可恢复原因的补挂队列 + 定时重试（覆盖池耗尽 / 相机未就绪）。
    /// 三者都走同一个 TryBind 入口，因此去重与告警口径只有一份实现。
    ///
    /// 【执行顺序】[DefaultExecutionOrder(100)] 保证本类的 LateUpdate 晚于 CameraController 的 LateUpdate：
    /// 相机在 LateUpdate 里才写最终位置，若血条先于它读相机，画面会慢一帧（平移时表现为血条轻微漂移）。
    ///
    /// 【每帧成本】一次 List 遍历 + 每条血条 1 次位置写入、1 次旋转复制、1 次缩放计算（含 1 次开方）。
    /// 不读任何战斗数值、不产生任何分配。
    /// </summary>
    [DefaultExecutionOrder(100)]
    [DisallowMultipleComponent]
    public class WorldHealthBarManager : MonoBehaviour
    {
        [Header("对象池")]
        [Tooltip("血条模板（场景中的非激活对象，由一键组装工具生成）。池按它预生成实例。")]
        [SerializeField] private WorldHealthBarView barTemplate;

        [Tooltip("池对象的父节点。归还时血条会挂回这里，保证层级干净。")]
        [SerializeField] private Transform poolHost;

        [Tooltip("预生成数量，同时也是池容量上限。同屏单位数超过它时，多出来的单位不显示血条（不影响逻辑）。")]
        [Min(0)]
        [SerializeField] private int prewarmCount = 24;

        [Header("补挂（阶段八实机修复）")]
        // 【为什么必须有这一段 —— 这是实机"英雄没有血条"的根因之一】
        // 血条池是【硬上限】：池空时 Get() 返回 null，而 HandleEntityRegistered 只在实体登记的那一刻被调用一次。
        // 若那一次没拿到血条，该单位就【永远不会再有血条】—— 因为登记事件不会重发。
        // 池里其它单位归还后空出来的位置，只会被"之后新登记的单位"用掉，先到先得的失败者被永久饿死。
        // 实机日志证据：`[PrefabPool] HealthBarTemplate 的池已耗尽（容量 48）` 由 MinionSpawner.SpawnOne
        // 的 Instantiate → EntityBase.OnEnable 路径打出 —— 说明当时确有单位被拒绝。
        //
        // 因此把"被拒绝"改成"排队等重试"：池腾出位置后按固定间隔补挂，直到成功或实体消失。
        // 这与项目既有的「自愈路径必须无声也留痕」一致：补挂结果会打一条汇总日志，便于核对到底挂上了几条。
        [Tooltip("补挂重试间隔（秒）。池是硬上限且被拒绝的单位不会收到第二次登记事件，\n" +
                 "没有重试它就会永久没有血条。本值决定池腾出位置后多久补挂一次。")]
        [Min(0.05f)]
        [SerializeField] private float retryInterval = 0.5f;

        [Tooltip("是否在首帧末做一次全量补挂。事件订阅已能覆盖绝大多数情况，\n" +
                 "这一趟是为了兜住「管理器的 OnEnable 早于实体登记」与「实体在两次调用之间登记」两种窗口。")]
        [SerializeField] private bool reconcileOnFirstFrame = true;

        [Header("相机与阵营")]
        [Tooltip("主相机。留空时自动取 Camera.main。")]
        [SerializeField] private Camera targetCamera;

        [Tooltip("「我方」阵营，用于血条配色（友方绿 / 敌方红）。一键组装工具会按英雄阵营注入。")]
        [SerializeField] private TeamType localTeam = TeamType.Player;

        [Header("调试")]
        [Tooltip("在 Console 输出血条挂载/回收记录（排查「某单位没有血条」时打开）。")]
        [SerializeField] private bool logLifecycle = false;

        /// <summary>实体 → 血条。用于在注销事件里 O(1) 找到对应的血条并归还。</summary>
        private readonly Dictionary<EntityBase, WorldHealthBarView> bars =
            new Dictionary<EntityBase, WorldHealthBarView>();

        /// <summary>
        /// 待补挂队列：登记时没能拿到血条、但【原因可恢复】的实体。
        /// 只有"池耗尽"与"相机尚未就绪"两种原因会进队列 —— "没有 HealthComponent" 属于永久性原因，
        /// 入队只会让每 0.5 秒做一次注定失败的尝试，并掩盖真正的问题（那条一次性告警已经说明了原因）。
        /// </summary>
        private readonly List<EntityBase> pendingBars = new List<EntityBase>();

        /// <summary>下一次补挂的时刻（Time.time 基准）。</summary>
        private float nextRetryTime;

        /// <summary>是否已完成首帧末的全量补挂。</summary>
        private bool hasReconciledOnce;

        /// <summary>累计成功挂载过的血条数（含已回收的），仅用于汇总日志。</summary>
        private int totalBoundCount;

        /// <summary>累计被拒绝（进入补挂队列）的次数，仅用于汇总日志。</summary>
        private int totalDeferredCount;

        /// <summary>对象池。Awake 里创建，之后只增删其中的实例。</summary>
        private PrefabPool<WorldHealthBarView> pool;

        /// <summary>是否已就"单位没有 HealthComponent"告警过一次（防止刷屏）。</summary>
        private bool hasReportedMissingHealth;

        /// <summary>是否已就"没有相机"告警过一次。</summary>
        private bool hasReportedMissingCamera;

        /// <summary>当前正在显示的血条数量（只读），供调试视图使用。</summary>
        public int ActiveBarCount => bars.Count;

        /// <summary>
        /// 建立对象池。放在 Awake（早于 OnEnable 的订阅），因此 OnEnable 里补挂时池必定已就绪。
        /// </summary>
        private void Awake()
        {
            if (barTemplate == null || poolHost == null)
            {
                Debug.LogError(
                    $"[WorldHealthBarManager] {name} 未配置血条模板或池宿主，头顶血条将完全不可用。" +
                    "请执行 MOBA Demo/一键组装测试战场 重新生成 UI（模板与宿主都由工具生成并注入）。", this);
                enabled = false;
                return;
            }

            if (targetCamera == null)
            {
                targetCamera = Camera.main;
            }

            pool = new PrefabPool<WorldHealthBarView>(barTemplate, poolHost, prewarmCount);
        }

        /// <summary>
        /// 订阅注册表事件并补挂已存在的实体。
        /// 【顺序不可颠倒】必须先订阅、再 Snapshot：
        /// 反过来的话，在两次调用之间完成登记的单位会被彻底漏掉（既不在快照里，也没收到事件）。
        /// 两者都做时由 HandleEntityRegistered 内部的去重保证不会重复挂载。
        /// </summary>
        private void OnEnable()
        {
            EntityRegistry.OnEntityRegistered += HandleEntityRegistered;
            EntityRegistry.OnEntityUnregistered += HandleEntityUnregistered;

            EntityBase[] snapshot = EntityRegistry.Snapshot();
            for (int i = 0; i < snapshot.Length; i++)
            {
                HandleEntityRegistered(snapshot[i]);
            }
        }

        /// <summary>
        /// 退订并把全部血条归还池中。退订的必要性与其它静态事件订阅方一致：
        /// 不注销就会让已禁用的管理器一直被持有并继续接收回调。
        /// </summary>
        private void OnDisable()
        {
            EntityRegistry.OnEntityRegistered -= HandleEntityRegistered;
            EntityRegistry.OnEntityUnregistered -= HandleEntityUnregistered;

            if (pool != null)
            {
                pool.ReleaseAll();
            }

            bars.Clear();
            pendingBars.Clear();
        }

        /// <summary>
        /// 每帧驱动全部血条。
        /// 放在 LateUpdate 且晚于相机控制器执行（见类的 [DefaultExecutionOrder]）。
        /// </summary>
        private void LateUpdate()
        {
            if (pool == null)
            {
                return;
            }

            // 首帧末的全量补挂：必须在所有场景对象的 OnEnable 都跑完之后做，
            // 因此不能放在自己的 OnEnable 里（那时场景里可能还有实体尚未登记）。
            // 每局只跑一次，代价是 1 次数组分配 + 一次全表遍历。
            if (reconcileOnFirstFrame && !hasReconciledOnce)
            {
                hasReconciledOnce = true;
                ReconcileSnapshot();
            }

            // 补挂队列：只有"池耗尽 / 相机未就绪"这两种可恢复的拒绝才会入队（见 pendingBars 的注释）。
            if (pendingBars.Count > 0 && Time.time >= nextRetryTime)
            {
                nextRetryTime = Time.time + Mathf.Max(0.05f, retryInterval);
                RetryPendingBars();
            }

            // 遍历池的活跃清单而不是 bars 字典：血条的位置更新只依赖"是否活跃"，
            // 而字典遍历在 .NET 上会产生枚举器（结构体，但仍不如索引遍历直接）。
            List<WorldHealthBarView> activeBars = pool.ActiveItems;

            for (int i = 0; i < activeBars.Count; i++)
            {
                WorldHealthBarView bar = activeBars[i];
                if (bar == null)
                {
                    continue;
                }

                bar.UpdateVisual(targetCamera);
            }
        }

        /// <summary>
        /// 全量补挂：遍历注册表快照，把"还没有血条"的实体补上，并打一条汇总日志。
        ///
        /// 为什么要有这一趟（事件订阅不是已经覆盖了吗）：
        ///   · 管理器的 OnEnable 与实体的 OnEnable 之间没有确定的先后，两边的兜底（事件 + 快照）必须同时存在；
        ///   · 补挂队列只覆盖"登记过但被拒绝"的实体，覆盖不了"登记事件压根没被收到"的窗口。
        /// 汇总日志是刻意的：没有它时，"补挂逻辑跑了但无需补挂"与"补挂逻辑根本没跑"在 Console 上无法区分，
        /// 而这正是排查"某单位没有血条"时最需要立刻知道的事。
        /// </summary>
        private void ReconcileSnapshot()
        {
            EntityBase[] snapshot = EntityRegistry.Snapshot();
            int newlyBound = 0;

            for (int i = 0; i < snapshot.Length; i++)
            {
                EntityBase entity = snapshot[i];

                if (entity == null || bars.ContainsKey(entity))
                {
                    continue;
                }

                if (TryBind(entity))
                {
                    newlyBound++;
                }
            }

            Debug.Log(
                $"[WorldHealthBarManager] 血条就绪：当前显示 {bars.Count} 条（本次补挂 {newlyBound} 条），" +
                $"待补挂 {pendingBars.Count} 个｜池占用 {pool.ActiveCount}/{pool.TotalCount}" +
                $"（累计挂载 {totalBoundCount} 次，累计排队 {totalDeferredCount} 次）。" +
                "若「待补挂」长期大于 0，说明池容量不足，请调大 prewarmCount。", this);
        }

        /// <summary>
        /// 重试补挂队列。倒序遍历：成功项会被移出列表，倒序可保证索引不失效。
        /// 已销毁的实体直接出队（Unity 重载的 == 运算符能识别已销毁对象）。
        /// </summary>
        private void RetryPendingBars()
        {
            for (int i = pendingBars.Count - 1; i >= 0; i--)
            {
                EntityBase entity = pendingBars[i];

                if (entity == null || TryBind(entity))
                {
                    pendingBars.RemoveAt(i);
                }
            }
        }

        /// <summary>把一个实体加入补挂队列（已入队则忽略，避免队列无限增长）。</summary>
        private void EnqueuePendingBar(EntityBase entity)
        {
            if (entity == null || pendingBars.Contains(entity))
            {
                return;
            }

            pendingBars.Add(entity);
            totalDeferredCount++;
        }

        /// <summary>
        /// 实体登记回调：为它取一条血条并绑定。
        /// 去重是必要的——本方法既被事件调用、也被 OnEnable 的快照补挂调用。
        /// </summary>
        /// <param name="entity">刚登记的实体。</param>
        private void HandleEntityRegistered(EntityBase entity)
        {
            TryBind(entity);
        }

        /// <summary>
        /// 尝试为实体挂一条血条。三条失败路径的处理方式刻意不同：
        ///   · 没有 HealthComponent —— 永久性原因，只告警一次、不入队（入队只会做注定失败的轮询）；
        ///   · 没有相机 / 池耗尽 —— 可恢复原因，进入补挂队列，等条件满足后重试；
        /// 这正是"登记事件只发一次、被拒绝的单位会永久没有血条"这一静默失效的解药。
        /// </summary>
        /// <param name="entity">目标实体。</param>
        /// <returns>本次确实挂上了血条（或它本来就有）返回 true。</returns>
        private bool TryBind(EntityBase entity)
        {
            if (entity == null || pool == null)
            {
                return false;
            }

            // 去重是必要的：本方法既被事件调用、也被快照补挂与重试队列调用。
            if (bars.ContainsKey(entity))
            {
                return true;
            }

            // 没有生命组件的实体不需要血条（例如纯装饰对象）。这不是错误，但要留一条线索：
            // 正常单位（英雄/小兵/塔/基地）都挂了 HealthComponent。
            if (entity.GetComponent<HealthComponent>() == null)
            {
                if (!hasReportedMissingHealth)
                {
                    hasReportedMissingHealth = true;
                    Debug.LogWarning(
                        $"[WorldHealthBarManager] {entity.name} 上没有 HealthComponent，不会为它显示血条。", entity);
                }

                return false;
            }

            if (targetCamera == null)
            {
                targetCamera = Camera.main;

                if (targetCamera == null && !hasReportedMissingCamera)
                {
                    hasReportedMissingCamera = true;
                    Debug.LogWarning(
                        "[WorldHealthBarManager] 场景里找不到主相机，血条无法朝向相机与换算像素高度。" +
                        "请确认 MainCamera 的 Tag 是 MainCamera。", this);
                }
            }

            if (targetCamera == null)
            {
                // 相机还没就绪（场景加载中途）：入队等重试，而不是永久放弃这个单位。
                EnqueuePendingBar(entity);
                return false;
            }

            WorldHealthBarView bar = pool.Get();

            // 池耗尽 → 本次不显示，入队等池腾出位置后补挂（池内部已告警一次）。
            if (bar == null)
            {
                EnqueuePendingBar(entity);
                return false;
            }

            if (!bar.Bind(entity, targetCamera, localTeam))
            {
                // Bind 只在"实体没有 HealthComponent"时失败，而上面已经拦过，这里属于竞态兜底。
                pool.Release(bar);
                EnqueuePendingBar(entity);
                return false;
            }

            bars[entity] = bar;
            totalBoundCount++;

            if (logLifecycle)
            {
                Debug.Log($"[WorldHealthBarManager] 已为 {entity.name} 挂载血条（当前 {bars.Count} 条）。", this);
            }

            return true;
        }

        /// <summary>
        /// 实体注销回调：把它的血条归还池中。
        /// 归还即 SetActive(false)，血条视图在自己的 OnDisable 里退订事件，因此不会残留订阅。
        /// </summary>
        /// <param name="entity">刚注销的实体。</param>
        private void HandleEntityUnregistered(EntityBase entity)
        {
            if (entity == null || pool == null)
            {
                return;
            }

            // 先把可能存在的补挂申请撤掉：实体已经退出对局，再补挂它没有意义，
            // 而且留着会让"待补挂"这个数字永远不归零，掩盖真正的容量不足。
            pendingBars.Remove(entity);

            WorldHealthBarView bar;
            if (!bars.TryGetValue(entity, out bar))
            {
                return;
            }

            bars.Remove(entity);
            pool.Release(bar);

            if (logLifecycle)
            {
                Debug.Log($"[WorldHealthBarManager] 已回收 {entity.name} 的血条（当前 {bars.Count} 条）。", this);
            }
        }
    }
}
