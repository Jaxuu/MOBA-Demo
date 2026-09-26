using UnityEngine;
using MOBA.Core;

namespace MOBA.VFX
{
    /// <summary>
    /// 实体配色视图（表现层）：把单位的身体材质换成「阵营 × 单位类型」对应的颜色。
    ///
    /// 【四格配色表 —— 这是本轮修复的核心】
    /// <code>
    ///                  | 英雄 EntityType.Hero | 小兵 EntityType.Minion
    /// 蓝方 TeamType.Player | 蓝色（含玩家英雄）    | 白色
    /// 红方 TeamType.Enemy  | 红色                 | 黑色
    /// </code>
    ///
    /// 【为什么英雄与小兵必须用两套颜色，而不是统一按阵营上色 —— 实机踩出来的】
    /// 上一版"按阵营一色到底"的结果是：蓝方英雄与蓝方小兵同为蓝色、红方同理。
    /// 顶视角团战里，5v5 的 10 个英雄 + 两条兵线挤在 14 米宽的桥面上，
    /// 玩家（与评审）**无法区分"那一坨蓝色是 4 个小兵还是 1 个英雄"** ——
    /// 分不清谁是谁，5v5 就没法测。
    /// 因此改成：**英雄拿高饱和阵营色（主角），小兵拿无色（白 / 黑，背景兵线）**。
    /// 这样一眼看过去"有颜色的 = 英雄、黑白小点 = 兵"，再叠加阵营色，敌我与我方兵线同时可读。
    ///
    /// 【为什么必须做成"运行期组件"而不是工具直接改预制体】
    /// 小兵只有一个 MinionPrefab，而两方的阵营是 <c>MinionSpawner</c> 在【运行期】注入的
    /// （`entity.Initialize(stats, team, EntityType.Minion)`）。同一个预制体要同时长出蓝兵和红兵，
    /// 唯一的办法就是在运行期按 (Team, EntityType) 选材质 —— 在编辑期给预制体写死一个颜色是做不到的。
    /// 英雄虽然由工具在编辑期逐个实例化，但它【共用】同一个 HeroPrefab，且阵营同样要按实例区分，
    /// 因此走同一条运行期通道最省心，也保证"英雄与小兵只有一处上色实现"。
    ///
    /// 【为什么英雄实例上不再写渲染器覆写（本轮同步删掉了 ApplyHeroBodyMaterial）】
    /// 同一件事只能有一个写入者。运行期本组件一旦生效，它会把工具写的实例级 sharedMaterial 覆写【盖掉】——
    /// 两个写入者并存的结果是"看起来生效了、其实谁赢取决于执行顺序"，而且不报任何错。
    /// 因此本轮把英雄上色也统一收进本组件：工具只负责"把组件与四份材质挂上去"。
    ///
    /// 【为什么在 Start 而不是 Awake / OnEnable 应用】
    /// 小兵是 Instantiate 出来的，Unity 会在 Instantiate 的同一调用栈里立刻执行 Awake 与 OnEnable，
    /// 而阵营与单位类型是在那之后才由 MinionSpawner 注入的（`Instantiate` → `entity.Initialize(...)` 是两行）。
    /// Start 晚于"本帧的 Instantiate 调用栈"，因此那时读到的 Team / EntityType 一定是最终值。
    /// 反过来若写在 Awake 里，读到的会是预制体上写死的 Player / Hero，红方小兵会全部变成白的 —— 且不报任何错。
    ///
    /// 【写入的是 sharedMaterial 而不是 material】后者会在运行期克隆出材质实例（每个单位一份，
    /// 破坏合批并持续占用内存）；而这里赋的是工具生成的材质【资产】引用，几十个单位共用四份材质。
    ///
    /// 【边界】本组件只读 EntityBase.Team / EntityBase.EntityType、只写 Renderer.sharedMaterial，
    /// 不参与任何伤害/胜负判定，关掉它（或删掉这个组件）对局结果完全不变。
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(EntityBase))]
    public class TeamColorView : MonoBehaviour
    {
        [Header("英雄配色（EntityType.Hero）")]
        [Tooltip("蓝方英雄（TeamType.Player，含玩家操控的那一个）的身体材质。留空则保持预制体自带材质。")]
        [SerializeField] private Material allyHeroMaterial;

        [Tooltip("红方英雄（TeamType.Enemy）的身体材质。留空则保持预制体自带材质。")]
        [SerializeField] private Material enemyHeroMaterial;

        [Header("小兵配色（EntityType.Minion）")]
        [Tooltip("蓝方小兵（TeamType.Player）的身体材质 —— 白色。留空则保持预制体自带材质。")]
        [SerializeField] private Material allyMinionMaterial;

        [Tooltip("红方小兵（TeamType.Enemy）的身体材质 —— 黑色。留空则保持预制体自带材质。")]
        [SerializeField] private Material enemyMinionMaterial;

        [Header("行为")]
        [Tooltip("是否连同子节点上的渲染器一起上色（白盒期单位只有 Body 一个渲染器，默认开启以防日后加了部件）。")]
        [SerializeField] private bool includeChildren = true;

        [Tooltip("是否在 Start 自动应用一次。关闭后需要外部显式调用 ApplyTeamColor()（例如换了阵营之后）。")]
        [SerializeField] private bool applyOnStart = true;

        [Header("调试")]
        [Tooltip("在 Console 输出上色结果（含读到的是哪个阵营 / 哪种单位类型），排查「颜色没变」时打开。")]
        [SerializeField] private bool logApply = false;

        /// <summary>实体身份入口。由 [RequireComponent] 保证存在，仍判空以给出明确报错。</summary>
        private EntityBase entity;

        /// <summary>是否已就"缺少 EntityBase"报错一次，防止刷屏。</summary>
        private bool hasReportedMissingEntity;

        /// <summary>
        /// 是否已就"英雄 / 小兵没有配色材质"报错一次。
        /// 刻意用 static：同一批小兵会生成几十个，实例标记等于每个都报一条（刷屏）。
        /// 该条件本身是全局的（预制体没注入），因此全局只报一次是正确粒度。
        /// </summary>
        private static bool hasWarnedMissingMaterial;

        /// <summary>上次应用的材质（只读），供调试与测试断言使用。</summary>
        public Material AppliedMaterial { get; private set; }

        private void Awake()
        {
            // 只取引用，不在这里读 Team / EntityType（原因见类注释：此刻身份可能还没注入）。
            entity = GetComponent<EntityBase>();
        }

        private void Start()
        {
            if (applyOnStart)
            {
                ApplyTeamColor();
            }
        }

        /// <summary>
        /// 按当前「阵营 + 单位类型」应用材质。可重复调用（幂等：写入的是同一个共享材质引用）。
        /// </summary>
        /// <returns>确实写入过材质返回 true；无对应材质（或引用缺失）时返回 false（保持原材质）。</returns>
        public bool ApplyTeamColor()
        {
            if (entity == null)
            {
                entity = GetComponent<EntityBase>();
            }

            if (entity == null)
            {
                if (!hasReportedMissingEntity)
                {
                    hasReportedMissingEntity = true;
                    Debug.LogError(
                        $"[TeamColorView] {name} 上找不到 EntityBase，无法读取阵营与单位类型，实体配色不生效。" +
                        "请确保 EntityBase 与 TeamColorView 挂在同一个 GameObject 上。", this);
                }

                return false;
            }

            Material material = ResolveMaterial(entity.Team, entity.EntityType);

            if (material == null)
            {
                // 情况一：建筑（Tower / Base）没有配色表项 —— 它们有自己的一套材质，本就该保持原样；
                // 情况二：中立单位没有专用材质；
                // 情况三（**本轮实机打回的真凶**）：工具没注入 / 注入的字段已被改名清空。
                // 前两种是正常情况，第三种是装配缺陷，必须【响】——见 ReportMissingMaterialOnce。
                if (entity.EntityType == EntityType.Hero || entity.EntityType == EntityType.Minion)
                {
                    ReportMissingMaterialOnce(entity.Team, entity.EntityType);
                }
                else if (logApply)
                {
                    Debug.Log(
                        $"[TeamColorView] {name} 的（{entity.Team}, {entity.EntityType}）没有配置材质，保持原材质。", this);
                }

                return false;
            }

            int appliedCount = ApplyToRenderers(material);
            AppliedMaterial = material;

            if (logApply)
            {
                Debug.Log(
                    $"[TeamColorView] {name}（{entity.Team} / {entity.EntityType}）已为 {appliedCount} 个渲染器换上 " +
                    $"{material.name}。", this);
            }

            return appliedCount > 0;
        }

        /// <summary>
        /// 按「阵营 × 单位类型」取材质；没有对应配置时返回 null（调用方保持原样）。
        ///
        /// 【为什么是二维查表而不是"先按阵营、再按类型改色"】四格各自是一份独立材质资产：
        /// 颜色一旦要微调（例如黑兵在黑背景上看不清要提亮一点），改的是资产而不是代码里的乘法系数。
        /// 公开成方法是为了让编辑器工具与测试能断言"这一格到底指向哪份材质"，
        /// 而不必真的进播放模式看画面。
        /// </summary>
        /// <param name="team">阵营。</param>
        /// <param name="type">单位类型。</param>
        /// <returns>对应材质；该格无配置返回 null。</returns>
        public Material ResolveMaterial(TeamType team, EntityType type)
        {
            switch (type)
            {
                case EntityType.Hero:
                    switch (team)
                    {
                        case TeamType.Player:
                            return allyHeroMaterial;

                        case TeamType.Enemy:
                            return enemyHeroMaterial;

                        default:
                            return null;
                    }

                case EntityType.Minion:
                    switch (team)
                    {
                        case TeamType.Player:
                            return allyMinionMaterial;

                        case TeamType.Enemy:
                            return enemyMinionMaterial;

                        default:
                            return null;
                    }

                default:
                    // 防御塔 / 基地：它们的材质由工具在实例化时写死（TowerBlue / TowerRed / BaseBlue / BaseRed），
                    // 本组件刻意不接管 —— 建筑不需要区分"英雄色还是小兵色"，多一套配色只会多一处不一致。
                    return null;
            }
        }

        /// <summary>
        /// 就"英雄 / 小兵没有配色材质"报错一次（全局一次性，防刷屏）。
        ///
        /// 【为什么这条必须升级成 LogError —— 实机打回的真凶】
        /// 上一轮把字段从 `allyMaterial / enemyMaterial` 改名为四格表（`allyHeroMaterial` …）之后，
        /// `MinionPrefab.prefab` 里存的仍是【旧字段名】的值。C# 侧字段已不存在 → Unity 反序列化时
        /// **静默丢弃**这些值，四个新字段全是 null → 本组件判定"没有配色" → 保持原材质。
        /// 而当时这里只是一条 `logApply = false` 的可选日志，于是整件事**一点声音都没有**：
        /// 实机表现就是"红方小兵不是黑的（双方小兵同色）"，而 Console 一片安静。
        ///
        /// 因此这里的判据从"这不是错误"改成"**英雄与小兵必须有配色**"：缺了就报错，并直接给出修法
        /// （重新执行一键组装 —— 注入由工具的步骤 15 完成）。
        /// 用静态标记而不是实例标记：同一批小兵会生成几十个，每个报一条等于刷屏。
        /// </summary>
        /// <param name="team">读到的阵营。</param>
        /// <param name="type">读到的单位类型。</param>
        private static void ReportMissingMaterialOnce(TeamType team, EntityType type)
        {
            if (hasWarnedMissingMaterial)
            {
                return;
            }

            hasWarnedMissingMaterial = true;

            Debug.LogError(
                $"[TeamColorView] （{team} / {type}）没有配色材质，该单位将保持预制体自带材质 —— " +
                "症状是「双方小兵 / 英雄同色，团战分不清敌我」。\n" +
                "  · 根因：预制体上 TeamColorView 的四个材质字段为空（常见于字段改名后预制体里存的还是旧字段名，" +
                "Unity 会静默丢弃旧值；或组装步骤没有跑到）。\n" +
                "  · 修法：执行「MOBA Demo/一键组装测试战场」，工具会把四格配色材质重新注入英雄 / 小兵预制体" +
                "（组装日志里会给出「已写入四格配色」与读回校验的结论）。");
        }

        /// <summary>
        /// 把材质写到渲染器上。
        /// includeInactive = true：单位的渲染器可能挂在暂时未激活的子节点上（备用的部件 / 模型），
        /// 漏掉它们会出现"换了一半颜色"的诡异外观。
        /// </summary>
        /// <param name="material">目标材质（资产引用）。</param>
        /// <returns>实际被写入的渲染器数量。</returns>
        private int ApplyToRenderers(Material material)
        {
            Renderer[] renderers = includeChildren
                ? GetComponentsInChildren<Renderer>(true)
                : GetComponents<Renderer>();

            int appliedCount = 0;

            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null)
                {
                    continue;
                }

                // sharedMaterial：写入资产引用，不克隆材质实例（理由见类注释）。
                renderer.sharedMaterial = material;
                appliedCount++;
            }

            return appliedCount;
        }
    }
}
