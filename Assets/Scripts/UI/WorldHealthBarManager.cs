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
        /// 实体登记回调：为它取一条血条并绑定。
        /// 去重是必要的——本方法既被事件调用、也被 OnEnable 的快照补挂调用。
        /// </summary>
        /// <param name="entity">刚登记的实体。</param>
        private void HandleEntityRegistered(EntityBase entity)
        {
            if (entity == null || pool == null)
            {
                return;
            }

            if (bars.ContainsKey(entity))
            {
                return;
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

                return;
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

            WorldHealthBarView bar = pool.Get();

            // 池耗尽 → 本次不显示。表现缺失不影响逻辑（池内部已告警一次）。
            if (bar == null)
            {
                return;
            }

            if (!bar.Bind(entity, targetCamera, localTeam))
            {
                pool.Release(bar);
                return;
            }

            bars[entity] = bar;

            if (logLifecycle)
            {
                Debug.Log($"[WorldHealthBarManager] 已为 {entity.name} 挂载血条（当前 {bars.Count} 条）。", this);
            }
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
