using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using MOBA.Components;
using MOBA.Core;

namespace MOBA.UI
{
    /// <summary>
    /// 小地图（阶段七，D6 批准纳入）：把战场俯视图映射到屏幕角落的一块正方形区域，
    /// 显示双方单位、建筑与英雄位置。
    ///
    /// 职责边界：
    /// 1. 只做"世界坐标 → 小地图坐标"的映射与标记显隐，不参与任何战斗逻辑；
    /// 2. 标记的挂载与回收沿用阶段七既定的模式：订阅 <see cref="EntityRegistry"/> 的登记 / 注销事件，
    ///    因此**单位预制体零改动**，新单位类型自动出现在小地图上；
    /// 3. 不自己判断"谁该显示"：凡是登记进注册表且带生命组件的实体都显示，死亡即隐藏。
    ///
    /// 【为什么标记显隐用"每帧读 IsDead"而不是订阅 OnDied】
    /// 英雄死亡后并不注销（它会复活），若用"死亡事件 → 隐藏"就必须再补一个"复活事件 → 显示"，
    /// 两个事件要成对维护；而每帧读一次 <c>Health.IsDead</c> 只是一个 bool 字段读取（零分配），
    /// 并且天然覆盖"死亡隐藏 → 复活重现"这一整条链路。
    ///
    /// 【验收口径】"单位死亡后标记 1 帧内移除"——本类在 LateUpdate 中读取 IsDead，
    /// 死亡发生在同一帧的伤害结算里，因此标记最多在下一帧就被隐藏，满足要求。
    /// 实体被销毁时（小兵尸体 2 秒后销毁）走注销事件回收标记，不留残留。
    ///
    /// 【执行顺序】[DefaultExecutionOrder(100)] 与血条 / 飘字保持一致：
    /// 晚于相机与单位位移的 Update，读到的是本帧的最终位置。
    /// </summary>
    [DefaultExecutionOrder(100)]
    [DisallowMultipleComponent]
    public class MinimapView : MonoBehaviour
    {
        [Header("对象池（由一键组装工具注入）")]
        [Tooltip("标记模板（场景中的非激活 Image）。池按它预生成实例。")]
        [SerializeField] private Image markerTemplate;

        [Tooltip("标记的父节点。它必须是一个轴心在中心的正方形区域，本类写入的 anchoredPosition 即相对该中心的偏移。")]
        [SerializeField] private Transform markerHost;

        [Tooltip("映射区（取它的尺寸换算像素偏移）。")]
        [SerializeField] private RectTransform mapRect;

        [Tooltip("预生成数量，同时也是池容量上限。")]
        [Min(0)]
        [SerializeField] private int prewarmCount = 32;

        [Header("世界映射")]
        [Tooltip("战场中心（世界 XZ，取 X 与 Z 两个分量）。由工具按地面覆盖范围注入。")]
        [SerializeField] private Vector2 worldCenter = Vector2.zero;

        [Tooltip("战场尺寸（世界 XZ）。地面是 38×38，工具会按同一份包围盒注入，保证小地图与可行走区域一致。")]
        [SerializeField] private Vector2 worldSize = new Vector2(38f, 38f);

        [Tooltip("映射区边缘留白（像素）：避免贴着边界的单位标记被压出边框。")]
        [Min(0f)]
        [SerializeField] private float mapPadding = 8f;

        [Header("阵营配色")]
        [Tooltip("「我方」阵营，用于标记配色。由工具按英雄阵营注入。")]
        [SerializeField] private TeamType localTeam = TeamType.Player;

        [SerializeField] private Color allyColor = new Color(0.35f, 0.75f, 1f, 1f);
        [SerializeField] private Color enemyColor = new Color(1f, 0.4f, 0.35f, 1f);
        [SerializeField] private Color neutralColor = new Color(0.8f, 0.8f, 0.8f, 1f);

        [Tooltip("英雄标记的颜色。刻意与阵营色区分：README 要求「英雄用更醒目的标记」。")]
        [SerializeField] private Color heroColor = new Color(1f, 0.92f, 0.45f, 1f);

        [Header("标记尺寸（像素）")]
        [SerializeField] private float minionMarkerSize = 7f;
        [SerializeField] private float heroMarkerSize = 13f;
        [SerializeField] private float buildingMarkerSize = 16f;

        [Header("调试")]
        [Tooltip("在 Console 输出标记挂载 / 回收记录（排查「某单位没有出现在小地图上」时打开）。")]
        [SerializeField] private bool logLifecycle = false;

        /// <summary>实体 → 它的标记。注销事件里 O(1) 找到并归还。</summary>
        private readonly Dictionary<EntityBase, Image> markers = new Dictionary<EntityBase, Image>();

        /// <summary>对象池。Awake 创建，早于 OnEnable 的订阅。</summary>
        private PrefabPool<Image> pool;

        /// <summary>是否已就"单位没有生命组件"告警过一次。</summary>
        private bool hasReportedMissingHealth;

        /// <summary>当前显示的标记数量（只读），供调试使用。</summary>
        public int ActiveMarkerCount => markers.Count;

        /// <summary>建立对象池并校验配置。</summary>
        private void Awake()
        {
            if (markerTemplate == null || markerHost == null || mapRect == null)
            {
                Debug.LogError(
                    $"[MinimapView] {name} 未配置标记模板 / 标记父节点 / 映射区，小地图将完全不可用。" +
                    "请执行 MOBA Demo/一键组装测试战场 重新生成 UI。", this);
                enabled = false;
                return;
            }

            pool = new PrefabPool<Image>(markerTemplate, markerHost, prewarmCount);
        }

        /// <summary>
        /// 订阅注册表事件并补挂已存在的实体。
        /// 顺序不可颠倒：必须先订阅、再 Snapshot（详见 EntityRegistry 的注释）。
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

        /// <summary>退订并把全部标记归还池中。</summary>
        private void OnDisable()
        {
            EntityRegistry.OnEntityRegistered -= HandleEntityRegistered;
            EntityRegistry.OnEntityUnregistered -= HandleEntityUnregistered;

            if (pool != null)
            {
                pool.ReleaseAll();
            }

            markers.Clear();
        }

        /// <summary>
        /// 每帧刷新全部标记的位置与显隐。
        /// 【关于遍历 Dictionary】它的枚举器是结构体，foreach 不会产生堆分配；
        /// 每次迭代只做两次除法 + 两次乘法 + 一次 anchoredPosition 写入。
        /// </summary>
        private void LateUpdate()
        {
            if (pool == null || mapRect == null)
            {
                return;
            }

            // 可用区域：整块映射区减去边缘留白。每帧取一次 rect，屏幕尺寸变化时自动跟上。
            float usableWidth = Mathf.Max(1f, mapRect.rect.width - mapPadding * 2f);
            float usableHeight = Mathf.Max(1f, mapRect.rect.height - mapPadding * 2f);

            float sizeX = Mathf.Max(0.01f, worldSize.x);
            float sizeY = Mathf.Max(0.01f, worldSize.y);

            foreach (KeyValuePair<EntityBase, Image> pair in markers)
            {
                EntityBase entity = pair.Key;
                Image marker = pair.Value;

                if (marker == null)
                {
                    continue;
                }

                // 实体已被销毁：先隐藏（注销事件随后会把它归还池中）。
                if (entity == null)
                {
                    SetMarkerVisible(marker, false);
                    continue;
                }

                // 死亡即隐藏（含英雄阵亡；英雄复活后 IsDead 变回 false，标记自动重现）。
                HealthComponent health = entity.Health;
                if (health != null && health.IsDead)
                {
                    SetMarkerVisible(marker, false);
                    continue;
                }

                Vector3 world = entity.transform.position;

                // 归一化到 [-0.5, 0.5]：世界 X → 小地图横向，世界 Z → 小地图纵向（+Z 在小地图上朝上，
                // 与顶视角相机下玩家的直觉一致）。
                float u = Mathf.Clamp01((world.x - worldCenter.x) / sizeX + 0.5f) - 0.5f;
                float v = Mathf.Clamp01((world.z - worldCenter.y) / sizeY + 0.5f) - 0.5f;

                marker.rectTransform.anchoredPosition = new Vector2(u * usableWidth, v * usableHeight);

                SetMarkerVisible(marker, true);
            }
        }

        /// <summary>实体登记回调：取一个标记并按单位类型设定尺寸与颜色。</summary>
        /// <param name="entity">刚登记的实体。</param>
        private void HandleEntityRegistered(EntityBase entity)
        {
            if (entity == null || pool == null || markers.ContainsKey(entity))
            {
                return;
            }

            // 没有生命组件的实体不上小地图（本项目的可参战单位都挂了 HealthComponent）。
            if (entity.GetComponent<HealthComponent>() == null)
            {
                if (!hasReportedMissingHealth)
                {
                    hasReportedMissingHealth = true;
                    Debug.LogWarning(
                        $"[MinimapView] {entity.name} 上没有 HealthComponent，不会出现在小地图上。", entity);
                }

                return;
            }

            Image marker = pool.Get();

            // 池耗尽 → 该单位本次不上图（池内部已告警一次，表现缺失不影响逻辑）。
            if (marker == null)
            {
                return;
            }

            float size = ResolveMarkerSize(entity.EntityType);
            marker.rectTransform.sizeDelta = new Vector2(size, size);
            marker.color = ResolveMarkerColor(entity);
            marker.enabled = true;

            markers[entity] = marker;

            if (logLifecycle)
            {
                Debug.Log($"[MinimapView] {entity.name} 已上小地图（当前 {markers.Count} 个标记）。", this);
            }
        }

        /// <summary>实体注销回调：归还它的标记。</summary>
        /// <param name="entity">刚注销的实体。</param>
        private void HandleEntityUnregistered(EntityBase entity)
        {
            if (entity == null || pool == null)
            {
                return;
            }

            Image marker;
            if (!markers.TryGetValue(entity, out marker))
            {
                return;
            }

            markers.Remove(entity);
            pool.Release(marker);

            if (logLifecycle)
            {
                Debug.Log($"[MinimapView] {entity.name} 的标记已回收（当前 {markers.Count} 个标记）。", this);
            }
        }

        /// <summary>按单位类型取标记尺寸：英雄最醒目，建筑次之，小兵最小。</summary>
        /// <param name="entityType">单位类型。</param>
        /// <returns>标记边长（像素）。</returns>
        private float ResolveMarkerSize(EntityType entityType)
        {
            switch (entityType)
            {
                case EntityType.Hero:
                    return heroMarkerSize;
                case EntityType.Tower:
                case EntityType.Base:
                    return buildingMarkerSize;
                default:
                    return minionMarkerSize;
            }
        }

        /// <summary>
        /// 按阵营与类型取标记颜色。
        /// 英雄用统一的高亮色（不论敌我）——README 要求"英雄用更醒目的标记"，
        /// 而"谁是我方"在小地图上已经由其余标记的配色表达清楚了。
        /// </summary>
        /// <param name="entity">目标实体。</param>
        /// <returns>标记颜色。</returns>
        private Color ResolveMarkerColor(EntityBase entity)
        {
            if (entity.EntityType == EntityType.Hero)
            {
                return heroColor;
            }

            TeamType team = entity.Team;

            if (team == TeamType.Neutral)
            {
                return neutralColor;
            }

            return team == localTeam ? allyColor : enemyColor;
        }

        /// <summary>设置标记显隐（只在状态变化时写，避免每帧触发无意义的 Graphic 更新）。</summary>
        /// <param name="marker">目标标记。</param>
        /// <param name="visible">是否显示。</param>
        private static void SetMarkerVisible(Image marker, bool visible)
        {
            if (marker.enabled != visible)
            {
                marker.enabled = visible;
            }
        }
    }
}
