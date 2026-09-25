using System.Collections.Generic;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using MOBA.AI;
using MOBA.Components;
using MOBA.Controllers;
using MOBA.Core;
using MOBA.Data;
using MOBA.Gameplay;
using MOBA.Skills;
using MOBA.Units;

namespace MOBA.Editor
{
    /// <summary>
    /// 一键组装测试战场：用代码把「兵线 + 双方基地 + 双方出兵点 + 对局控制器」搭出来，替代手工拖拽与填坐标。
    ///
    /// 职责边界：
    /// 1. 只存在于编辑器（本文件位于 Assets/Scripts/Editor/，Unity 会自动把它编入 Assembly-CSharp-Editor，
    ///    不会进入任何运行时程序集），不参与任何运行时逻辑；
    /// 2. 负责「创建对象 / 挂组件 / 注入引用」，并顺带补齐「会让整条兵线或英雄静默失效」的缺口：
    ///    多余的对局控制器、小兵预制体缺失的必需组件、英雄预制体的生成与全套依赖注入。
    ///    不烘焙 NavMesh、不做胜负判定、不改任何运行时代码；
    /// 3. 幂等：重复执行会先按固定名称清掉上一次由本工具生成的对象，不会堆出第二条兵线或第二套基地；
    /// 4. 玩家英雄同样走「0 手动拖拽」：绿色胶囊体预制体、基础属性资产、组件挂载、
    ///    场景实例化、相机跟随绑定，全部由本工具生成并注入（见步骤 9~11）；
    /// 5. 顺带解决「一键组装完也跑不起来」的前置条件：地面太小 / 未标记 Navigation Static / 未烘焙 NavMesh
    ///    会让 NavMeshAgent 报「not close enough to the NavMesh」，英雄与小兵永久卡在出生点，
    ///    而 Console 里看不出到底是坐标还是地面的问题（见步骤 8）。
    ///
    /// 为什么写字段用 SerializedObject 而不是反射（对比 Tests/PlayMode/Stage2AutoTester 的做法）：
    /// 需要写入的都是 private [SerializeField] 字段，编辑器里唯一被 Unity 序列化系统承认的写入通道就是
    /// SerializedObject + SerializedProperty —— 改动会同步进 Inspector 与磁盘；反射改字段则未必被察觉，
    /// 尤其是对 ScriptableObject 资产。代价是字段名退化为字符串，因此每次写入找不到字段都会报错而非静默失败。
    /// </summary>
    public static partial class AutoSceneBuilder
    {
        /// <summary>菜单路径。层级中的「MOBA Demo」是本项目所有编辑器工具的归口。</summary>
        private const string MenuPath = "MOBA Demo/一键组装测试战场";

        /// <summary>撤销组名称：用户误点后一次 Ctrl+Z 即可完整回退整套战场。</summary>
        private const string UndoGroupName = "一键组装测试战场";

        /// <summary>配置资产根目录。与项目约定一致（资产放 Assets/ScriptableObjects/）。</summary>
        private const string ScriptableObjectsFolder = "Assets/ScriptableObjects";

        /// <summary>预制体根目录。</summary>
        private const string PrefabsFolder = "Assets/Prefabs";

        /// <summary>材质根目录（英雄的绿色胶囊体需要一个可见的材质资产）。</summary>
        private const string MaterialsFolder = "Assets/Art/Materials";

        /// <summary>技能配置资产目录（阶段六）。</summary>
        private const string SkillsFolder = ScriptableObjectsFolder + "/Skills";

        /// <summary>
        /// Q 技能配置资产路径（指向性非穿透弹道）。
        /// 文件名刻意以 Hero 开头：这样它会被 IsHeroDedicatedAsset 挡在通用资产查找之外，
        /// 不会污染「按类型找候选 → 名称命中关键词优先」这套选择逻辑。
        /// </summary>
        private const string HeroSkillQAssetPath = SkillsFolder + "/HeroSkillQ.asset";

        /// <summary>W 技能配置资产路径（非指向性 AOE 减速圈）。命名规则同 Q。</summary>
        private const string HeroSkillWAssetPath = SkillsFolder + "/HeroSkillW.asset";

        /// <summary>弹道预制体目录。</summary>
        private const string VfxPrefabsFolder = PrefabsFolder + "/VFX";

        /// <summary>弹道预制体资产名。</summary>
        private const string ProjectilePrefabName = "Projectile";

        /// <summary>弹道预制体资产路径。</summary>
        private const string ProjectilePrefabPath = VfxPrefabsFolder + "/" + ProjectilePrefabName + ".prefab";

        /// <summary>弹道材质路径。</summary>
        private const string ProjectileMaterialPath = MaterialsFolder + "/ProjectileOrange.mat";

        // ---- 固定对象名。清理与重建都以这些名字为准，改名即意味着本工具不再接管该对象。 ----
        private const string MidLaneName = "MidLane";
        private const string BlueBaseName = "BlueBase";
        private const string BlueSpawnerName = "BlueSpawner";
        private const string RedBaseName = "RedBase";
        private const string RedSpawnerName = "RedSpawner";
        private const string BattlefieldName = "Battlefield";

        /// <summary>英雄预制体资产名（Assets/Prefabs/HeroPrefab.prefab）。</summary>
        private const string HeroPrefabName = "HeroPrefab";

        /// <summary>
        /// 场景中英雄实例的名字。
        /// 刻意与预制体资产名分开：实例名要满足本工具「按名字清理」的约定（便于重复执行时先删旧再建），
        /// 而预制体资产名是资产标识，两者混用会让日志与层级面板都难以分辨。
        /// </summary>
        private const string HeroRootName = "PlayerHero";

        /// <summary>英雄胶囊体的子节点名（胶囊体是子节点，根节点只作为贴地坐标原点）。</summary>
        private const string HeroBodyName = "Body";

        private const string Waypoint1Name = "Waypoint1";
        private const string Waypoint2Name = "Waypoint2";

        // ---- 固定坐标。全部是场景世界坐标，集中在常量区便于后续调整阵型。 ----
        private static readonly Vector3 MidLanePosition = new Vector3(0f, 0f, 0f);
        private static readonly Vector3 Waypoint1Position = new Vector3(0f, 0f, 0f);
        private static readonly Vector3 Waypoint2Position = new Vector3(5f, 0f, 0f);
        private static readonly Vector3 BlueBasePosition = new Vector3(-10f, 0f, 0f);
        private static readonly Vector3 BlueSpawnerPosition = new Vector3(-8f, 0f, 0f);
        private static readonly Vector3 RedBasePosition = new Vector3(10f, 0f, 0f);
        private static readonly Vector3 RedSpawnerPosition = new Vector3(8f, 0f, 0f);

        /// <summary>玩家英雄出生点（蓝方基地后方）。实际落点会先吸附到 NavMesh 上，见 CreateHeroInstance。</summary>
        private static readonly Vector3 HeroSpawnPosition = new Vector3(-12f, 0f, 0f);

        /// <summary>出生点的 NavMesh 采样半径。采样不到时会退回原始坐标并告警。</summary>
        private const float HeroSpawnSampleRadius = 2f;

        // ---- 地面与导航（阶段五的实机验证前置：地面必须覆盖战场，否则 NavMeshAgent 无法移动） ----

        /// <summary>战场关键落点外扩的余量（米）。地面至少要外扩这么多，英雄才有走位空间、兵线才有展开空间。</summary>
        private const float GroundPadding = 8f;

        /// <summary>Unity 内置 Plane 原始网格的边长（米）。按比例放大地面时用它把「需要的米数」换算成 localScale。</summary>
        private const float PlaneMeshSize = 10f;

        /// <summary>自动放大后的地面最小边长（米）。防止战场范围很小时地面被算得比出厂 10×10 还小。</summary>
        private const float MinGroundSize = 10f;

        /// <summary>
        /// 内置 Plane 网格的名字。按【网格名】而不是对象名识别可放大的地面：
        /// 对象名用户可以随便改（Ground / Floor / Plane1），而网格名来自 Unity 内置资源，稳定可靠。
        /// </summary>
        private const string PlaneMeshName = "Plane";

        /// <summary>
        /// 上一次烘焙后注册进编辑器导航世界的临时 NavMeshData。
        /// 持有它有两个目的：① 下次执行时先回收，避免世界里堆出多份（三角形数会越报越多）；
        /// ② 它的内容已经 CopySerialized 进场景资产，自身不需要落盘。
        /// </summary>
        private static NavMeshData previewNavMeshData;

        /// <summary>上面那份临时数据的注册句柄，用于精确注销。</summary>
        private static NavMeshDataInstance previewNavMeshInstance;

        /// <summary>是否已注册过临时导航数据（NavMeshDataInstance 是结构体，不能用 null 判断）。</summary>
        private static bool hasPreviewNavMeshInstance;

        // ---- 英雄数值（需求指定：生命 500 / 移速 6 / 射程 2.5）。 ----
        private const float HeroMaxHealth = 500f;
        private const float HeroMoveSpeed = 6f;
        private const float HeroAttackRange = 2.5f;

        /// <summary>英雄普攻伤害。需求未指定，取「明显强于小兵（10）」的 25，让英雄手感有区别。</summary>
        private const float HeroAttackDamage = 25f;

        /// <summary>英雄普攻间隔。需求未指定，与小兵保持一致（1 秒）便于对照观察。</summary>
        private const float HeroAttackInterval = 1f;

        /// <summary>英雄索敌半径。英雄不跑 FSM，该值目前只作为 TargetingComponent 的兜底配置。</summary>
        private const float HeroDetectionRange = 8f;

        /// <summary>英雄胶囊体的颜色（绿色，用于与白色小兵区分）。</summary>
        private static readonly Color HeroColor = new Color(0.25f, 0.85f, 0.35f, 1f);

        /// <summary>英雄属性资产路径。与其它配置资产同目录，便于统一管理。</summary>
        private const string HeroStatsAssetPath = ScriptableObjectsFolder + "/HeroStats.asset";

        /// <summary>英雄普攻资产路径。</summary>
        private const string HeroAttackAssetPath = ScriptableObjectsFolder + "/HeroAttack.asset";

        /// <summary>英雄材质资产路径。</summary>
        private const string HeroMaterialPath = MaterialsFolder + "/HeroGreen.mat";

        // ---- 阶段六：技能数值（与 Stage6_Skill_System_Architecture.md §4 逐条对齐） ----
        // 这些数值只在「资产缺失、需要新建」时写入；已存在的资产一律沿用，避免覆盖策划调参。

        /// <summary>英雄最大法力值。300 点配合 Q 的 30 蓝耗 → 满蓝可放 10 次。</summary>
        private const float HeroMaxMana = 300f;

        /// <summary>英雄每秒法力回复量。</summary>
        private const float HeroManaRegenPerSecond = 10f;

        // Q：指向性非穿透火球。射程 8 / 前摇 0.25s / CD 6s / 蓝耗 30 / 弹速 14（8 米飞行约 0.57 秒）。
        private const float SkillQCastRange = 8f;
        private const float SkillQCastTime = 0.25f;
        private const float SkillQCooldown = 6f;
        private const float SkillQManaCost = 30f;
        private const float SkillQProjectileSpeed = 14f;
        private const float SkillQProjectileRadius = 0.5f;

        /// <summary>命中数上限：1 = 非穿透（命中即销毁），这是本次火球的明确需求。</summary>
        private const int SkillQMaxHitCount = 1;

        /// <summary>弹道最大飞行距离：射程 8 的 1.5 倍，用于兜住"目标不可达"时弹道永生。</summary>
        private const float SkillQMaxTravelDistance = 12f;

        private const float SkillQDamage = 120f;

        // W：非指向性 AOE 减速圈。射程 10 / 前摇 0.15s / CD 10s / 蓝耗 50 / 半径 3.5 / 持续 4s / 每 0.5s 结算。
        private const float SkillWCastRange = 10f;
        private const float SkillWCastTime = 0.15f;
        private const float SkillWCooldown = 10f;
        private const float SkillWManaCost = 50f;
        private const float SkillWEffectRadius = 3.5f;
        private const float SkillWAreaDuration = 4f;
        private const float SkillWTickInterval = 0.5f;
        private const float SkillWSlowPercent = 0.4f;

        /// <summary>
        /// 减速时长。刻意【大于】结算周期（0.5s）：让敌人离开圈后仍残留一小段减速，
        /// 手感上更"粘"，也避免"刚出圈就瞬间恢复"的割裂感。
        /// </summary>
        private const float SkillWSlowDuration = 0.6f;

        /// <summary>弹道材质颜色（火球橙）。</summary>
        private static readonly Color ProjectileColor = new Color(1f, 0.45f, 0.1f, 1f);

        /// <summary>
        /// 地面 Layer 名。PlayerCommandController 靠它做右键点地拾取，AutoSceneBuilder 靠它做落点覆盖校验，
        /// 因此两处共用同一个常量，避免改名后只改一处。
        /// </summary>
        private const string GroundLayerName = "Ground";

        /// <summary>
        /// 英雄专用资产的文件名前缀（HeroStats / HeroAttack / HeroPrefab）。
        /// 这些资产由本工具按固定路径直接创建与取用，因此必须排除在通用资产查找之外，见 IsHeroDedicatedAsset。
        /// </summary>
        private const string HeroAssetNamePrefix = "Hero";

        /// <summary>基地外观子节点名。与英雄的 Body 命名保持一致，便于在层级面板里一眼认出。</summary>
        private const string BaseBodyName = "Body";

        /// <summary>
        /// 基地外观（Cube）的尺寸（米）。取 2.5×2.5×2.5：
        /// 比小兵与英雄（胶囊高 2 米）明显大一圈，顶视角下一眼可辨，与"大型固定建筑"的定位相称；
        /// 同时刻意不超过 3 米——英雄出生点在 x = -12、蓝方基地在 x = -10，
        /// 边长 3 米时基地侧面会正好贴到英雄胶囊边缘（x = -11.5），视觉上像是"英雄卡在基地里"。
        /// 2.5 米留出 0.25 米间隙，既醒目又不打架。
        /// </summary>
        private static readonly Vector3 BaseBodySize = new Vector3(2.5f, 2.5f, 2.5f);

        /// <summary>
        /// 基地最大生命值。远高于小兵（100 量级）与英雄（500）：
        /// 基地是胜负判定载体，被一波兵推掉就没有对局可言。
        /// </summary>
        private const float BaseMaxHealth = 3000f;

        /// <summary>基地属性资产路径（确定性资产，由工具按固定路径创建与取用）。</summary>
        private const string BaseStatsAssetPath = ScriptableObjectsFolder + "/BaseStats.asset";

        /// <summary>蓝方（玩家方）基地材质路径。</summary>
        private const string BlueBaseMaterialPath = MaterialsFolder + "/BaseBlue.mat";

        /// <summary>红方（敌方）基地材质路径。</summary>
        private const string RedBaseMaterialPath = MaterialsFolder + "/BaseRed.mat";

        /// <summary>蓝方（玩家方）基地颜色。</summary>
        private static readonly Color BlueBaseColor = new Color(0.25f, 0.55f, 0.95f, 1f);

        /// <summary>红方（敌方）基地颜色。</summary>
        private static readonly Color RedBaseColor = new Color(0.9f, 0.25f, 0.25f, 1f);

        /// <summary>
        /// 本工具接管的根对象名清单。
        /// 刻意【只按名字匹配】而不做「全场景扫描所有 MinionSpawner/BaseCoreController 并删除」：
        /// 后者会把用户手工搭的、名字不同的对象一并抹掉，代价远大于收益。名字写死意味着删除范围完全可预期。
        /// </summary>
        private static readonly string[] ManagedRootNames =
        {
            MidLaneName, BlueBaseName, BlueSpawnerName, RedBaseName, RedSpawnerName, BattlefieldName, HeroRootName,
            // 阶段七新增：屏幕空间 HUD 与世界空间 UI（血条 / 飘字）的根对象。
            // 加入清单后，重复执行会自动先删后建，UI 与战场一样保持幂等。
            UIRootName, WorldUIRootName
        };

        /// <summary>
        /// 菜单入口：一键组装测试战场。
        /// 流程：清理旧战场 → 查找配置资产 → 建兵线 → 建蓝方 → 建红方 → 建对局编排
        ///      → 地面覆盖与 NavMesh 烘焙 → 生成英雄预制体 → 实例化玩家英雄 → 绑定相机跟随
        ///      → 补齐资产缺口 → 收尾校验。
        /// </summary>
        [MenuItem(MenuPath)]
        private static void BuildTestBattlefield()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded)
            {
                Debug.LogError("[AutoSceneBuilder] 当前没有已加载的有效场景，组装中止。请先打开或新建一个场景。");
                return;
            }

            // 整次组装归入同一个撤销组：中途失败或误点都能用一次 Ctrl+Z 完整回退，不会留下半套战场。
            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(UndoGroupName);

            // ---------- 步骤 1：清理旧战场 ----------
            List<string> removedNames = CleanupOldBattlefield();

            // 幽灵对象清理：名字命中接管清单、却不是场景根对象的游离副本（历史遗留），见方法注释。
            CleanupGhostEntities(removedNames);

            // ---------- 步骤 2：查找配置资产与预制体 ----------
            // 【基地属性改为"确定性资产"】原实现是 LoadPreferredAsset<EntityStatsData>(..., {"Base","Tower"}, ...)
            // 在 Assets/ScriptableObjects 下按"文件名含 Base/Tower"挑选——而仓库里只有 MinionStats 与 HeroStats，
            // 于是每次组装都会告警"没有名称含 Base/Tower 的 EntityStatsData"，并退回 MinionStats 当占位。
            // 后果不只是那条告警：基地会拿到小兵量级的生命值（几波兵就能推掉基地，对局失去意义）。
            // 现在改为缺失才创建、已存在沿用（见 EnsureBaseStatsAsset），与 HeroStats / HeroAttack 同一套路。
            List<string> baseNotes = new List<string>();
            EntityStatsData baseStats = EnsureBaseStatsAsset(baseNotes);

            AttackData attackData = LoadPreferredAsset<AttackData>(
                FindAssetPaths<AttackData>(ScriptableObjectsFolder),
                new[] { "Minion", "Attack" },
                "小兵的 AttackData");

            MinionSpawnData spawnData = LoadPreferredAsset<MinionSpawnData>(
                FindAssetPaths<MinionSpawnData>(ScriptableObjectsFolder),
                new[] { "Spawn" },
                "出兵点的 MinionSpawnData");

            MatchConfigData matchConfig = LoadPreferredAsset<MatchConfigData>(
                FindAssetPaths<MatchConfigData>(ScriptableObjectsFolder),
                new[] { "Match" },
                "MatchController 的 MatchConfigData");

            GameObject minionPrefab = LoadPreferredAsset<GameObject>(
                FindPrefabPaths(PrefabsFolder),
                new[] { "Minion" },
                "小兵预制体");

            // 真正会被生成出来的是 MinionSpawnData 上配置的那个预制体，只有它为空时才用刚找到的这个。
            // 后续的「完整性修复」必须针对"实际会被生成的"那一个，否则可能修了一个根本没被使用的预制体。
            if (spawnData != null && spawnData.MinionPrefab != null)
            {
                minionPrefab = spawnData.MinionPrefab;
            }

            // ---------- 步骤 3：兵线 ----------
            LanePath midLane = CreateMidLane();

            // ---------- 步骤 4：蓝方阵地 ----------
            Material blueBaseMaterial = EnsureColorMaterial(
                BlueBaseMaterialPath, BlueBaseColor, 0.1f, "蓝方基地材质（蓝）", baseNotes);

            EntityBase blueBase = CreateBase(
                BlueBaseName, BlueBasePosition, TeamType.Player, baseStats, blueBaseMaterial);
            MinionSpawner blueSpawner = CreateSpawner(
                BlueSpawnerName, BlueSpawnerPosition, TeamType.Player, spawnData, midLane);

            // ---------- 步骤 5：红方阵地（阵营强制为 Enemy，与蓝方形成敌对关系） ----------
            Material redBaseMaterial = EnsureColorMaterial(
                RedBaseMaterialPath, RedBaseColor, 0.1f, "红方基地材质（红）", baseNotes);

            EntityBase redBase = CreateBase(
                RedBaseName, RedBasePosition, TeamType.Enemy, baseStats, redBaseMaterial);
            MinionSpawner redSpawner = CreateSpawner(
                RedSpawnerName, RedSpawnerPosition, TeamType.Enemy, spawnData, midLane);

            // ---------- 步骤 6：对局编排 ----------
            // 必要性：MinionSpawner 刻意不在 Start 里出兵，出兵由 MatchController 统一控制。
            // 若不建这个对象，场景跑起来后一条兵都不会出，也无法判定胜负——「测试战场」就只是个静态摆设。
            MatchController matchController = CreateMatchController(matchConfig, blueSpawner, redSpawner);

            // ---------- 步骤 7：清理场景里多余的 MatchController ----------
            // 必要性：MatchController 是「每场景一个」的对局编排者。场景里若存在第二个
            // （例如更早手工搭的、其出兵点引用已被本次按名字清理置空的那个），它会各自跑一遍开局流程，
            // 日志里会出现两行「对局准备中」+ 两行「对局开始」，对局状态互相覆盖，排查成本极高。
            List<string> removedControllers = RemoveDuplicateMatchControllers(matchController);

            // ---------- 步骤 8：地面覆盖与 NavMesh 烘焙（实机验证前置） ----------
            // 必须放在「实例化玩家英雄」之前：英雄落点要做 NavMesh.SamplePosition 吸附，
            // 地面还没放大、网格还没烘焙时采样必然失败，英雄会被放到 NavMesh 之外并永久卡死。
            List<string> groundNotes = new List<string>();
            EnsureGroundAndNavMesh(groundNotes);

            // ---------- 步骤 9~11：玩家英雄（预制体 → 场景实例 → 相机跟随） ----------
            // 这三步都遵循同一条原则：所有依赖注入都由工具完成，用户不需要在 Inspector 里拖任何引用。
            List<string> heroNotes = new List<string>();

            AttackData heroAttack = EnsureHeroAttackAsset(heroNotes);
            EntityStatsData heroStats = EnsureHeroStatsAsset(heroAttack, heroNotes);
            Material heroMaterial = EnsureHeroMaterial(heroNotes);

            // ---------- 步骤 13：技能资产与属性资产补缺（阶段六） ----------
            // 顺序说明：必须先有技能资产，才能把它注入 EntityStatsData 的技能槽，
            // 也才能把它注入英雄预制体的 SkillComponent。
            SkillData heroSkillQ = EnsureHeroSkillQAsset(heroNotes);
            SkillData heroSkillW = EnsureHeroSkillWAsset(heroNotes);

            // 只补空字段、绝不覆盖已有值 —— 见方法注释里那条"资产 guid 铁律"的踩坑记录。
            PatchHeroStatsGaps(heroStats, heroSkillQ, heroSkillW, heroNotes);

            // ---------- 步骤 14：弹道材质与预制体（阶段六） ----------
            Material projectileMaterial = EnsureProjectileMaterial(heroNotes);
            GameObject projectilePrefab = EnsureProjectilePrefab(projectileMaterial, heroNotes);

            // ---------- 步骤 15：英雄预制体（含技能系统全套组件与依赖注入） ----------
            GameObject heroPrefab = CreateHeroPrefab(
                heroStats, heroAttack, heroMaterial, heroSkillQ, heroSkillW, projectilePrefab, heroNotes);

            HeroController hero = CreateHeroInstance(heroPrefab, heroNotes);
            CameraController cameraController = SetupCameraController(hero, heroNotes);

            // ---------- 步骤 16：收尾校验（只提示不改场景） ----------
            ValidateSkillSetup(hero, heroStats, projectilePrefab);

            // Stats Data 注入校验必须放在最后：它是"所有 EntityBase 都拿到了属性资产"的确定性结论，
            // 用来替代 EntityBase.OnValidate 在 AddComponent 瞬间打出的那条瞬时假告警（详见该方法注释）。
            ValidateStatsInjection(blueBase, redBase, hero, minionPrefab);

            // ---------- 步骤 12：补齐会导致运行期直接失败的资产与预制体缺口 ----------
            List<string> assetNotes = PatchAssetGaps(spawnData, minionPrefab, attackData);
            List<string> prefabNotes = RepairPrefabComponents(minionPrefab);

            // ---------- 步骤 17：UI 与可视化装配（阶段七） ----------
            // 放在最后：UI 需要英雄实例、对局控制器与主相机都已就位才能完成依赖注入。
            // 实现拆在 AutoSceneBuilder.UIAssembly.cs（partial 分册），菜单入口与撤销组仍是同一个。
            List<string> uiNotes = new List<string>();
            BuildUIAssembly(hero, matchController, uiNotes);

            // ---------- 收尾 ----------
            // 标记场景已修改，避免用户在没保存的情况下直接进播放模式、丢失本次组装结果。
            EditorSceneManager.MarkSceneDirty(scene);
            Undo.CollapseUndoOperations(undoGroup);

            LogSummary(
                removedNames, removedControllers, blueBase, redBase, blueSpawner, redSpawner, assetNotes, prefabNotes,
                hero, cameraController, heroNotes, groundNotes, baseNotes, uiNotes);
            ValidateNavMeshCoverage();
        }

        #region 步骤 1：清理

        /// <summary>
        /// 删除上一次由本工具生成的根对象，保证重复执行不会堆叠。
        /// </summary>
        /// <returns>被删除的对象名（按层级顺序），供日志核对。</returns>
        private static List<string> CleanupOldBattlefield()
        {
            List<string> removedNames = new List<string>();

            // 用 GetRootGameObjects 而不是 GameObject.Find：后者只找得到「激活」的对象，
            // 若上一次的战场被用户在 Hierarchy 里关掉了，就会漏删并留下重复兵线。
            GameObject[] roots = SceneManager.GetActiveScene().GetRootGameObjects();

            // 倒序遍历：删除会改变层级顺序，但 roots 是快照，倒序只是让删除顺序更直观。
            for (int i = roots.Length - 1; i >= 0; i--)
            {
                GameObject root = roots[i];

                // 必须先判 null 再读 name：快照里的元素可能在本次循环中已被销毁，
                // 对已销毁对象访问 name 会抛 MissingReferenceException。
                if (root == null || !IsManagedRootName(root.name))
                {
                    continue;
                }

                removedNames.Add(root.name);

                // 用 DestroyObjectImmediate 而不是 DestroyImmediate：前者会把删除登记进撤销栈，
                // 与本次组装的其余操作同属一个撤销组。
                Undo.DestroyObjectImmediate(root);
            }

            removedNames.Reverse();
            return removedNames;
        }

        /// <summary>判断某个根对象名是否属于本工具接管的范围。</summary>
        private static bool IsManagedRootName(string objectName)
        {
            for (int i = 0; i < ManagedRootNames.Length; i++)
            {
                if (string.Equals(objectName, ManagedRootNames[i], System.StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 清理「幽灵对象」：名字命中本工具接管清单、但【不是场景根对象】的游离副本。
        ///
        /// 为什么必须清：场景里曾存在历史遗留 —— 两个 Unity 默认名的空对象（`GameObject (1)` / `GameObject (2)`）
        /// 下面挂着名为 `BlueBase` / `RedBase` 的副本，它们带着 `EntityBase` / `BaseCoreController` 等残余组件。
        /// 这类对象不会让任何工具报错，却会在 `OnEnable` 时进 `EntityRegistry`、参与索敌与胜负判定 ——
        /// 属于隐蔽性最高的污染（表现为「凭空多一个基地」，而 Hierarchy 里一眼看不出是谁）。
        ///
        /// 判定规则刻意收得很窄：**名字命中 + 有父节点**。工具的产物永远是场景根对象，所以这条规则
        /// 不会误伤工具自己的东西，也不需要做全场景组件扫描，删除范围完全可预期；
        /// 清完之后场景里再没有同名游离副本可命中，规则自限（不会长期「霸占」某个名字）。
        /// </summary>
        /// <param name="removedNames">删除记录，追加进已有的清理清单供日志核对。</param>
        private static void CleanupGhostEntities(List<string> removedNames)
        {
            List<Transform> emptiedParents = new List<Transform>();

            // 必须含未激活对象（true）：幽灵对象常被顺手关掉，漏掉就永远清不掉。
            Transform[] allTransforms = UnityEngine.Object.FindObjectsOfType<Transform>(true);

            for (int i = 0; i < allTransforms.Length; i++)
            {
                Transform candidate = allTransforms[i];

                // parent == null 的是根对象，归 CleanupOldBattlefield 管，这里不碰。
                if (candidate == null || candidate.parent == null || !IsManagedRootName(candidate.name))
                {
                    continue;
                }

                Transform parent = candidate.parent;

                removedNames.Add($"{parent.name}/{candidate.name}（幽灵副本）");
                Undo.DestroyObjectImmediate(candidate.gameObject);

                if (!emptiedParents.Contains(parent))
                {
                    emptiedParents.Add(parent);
                }
            }

            // 幽灵副本清掉后，若宿主只剩一个空壳、且名字是 Unity 默认名（GameObject / GameObject (1) …），
            // 顺手收掉：它们没有任何组件，纯粹是层级垃圾。
            // 这一步是「自限」的关键——空壳清掉后，下一次执行既没有幽灵副本、也没有空壳可命中。
            for (int i = 0; i < emptiedParents.Count; i++)
            {
                Transform parent = emptiedParents[i];

                if (parent == null || parent.childCount > 0 || !IsDefaultGameObjectName(parent.name))
                {
                    continue;
                }

                removedNames.Add($"{parent.name}（空壳）");
                Undo.DestroyObjectImmediate(parent.gameObject);
            }
        }

        /// <summary>
        /// 判断名字是否为 Unity 默认新建对象名（`GameObject`、`GameObject (1)` …）。
        /// 只在「该对象刚刚因为幽灵副本被清而变成空壳」时才用它做二次判定，因此不会误删用户自建的默认名对象。
        /// </summary>
        private static bool IsDefaultGameObjectName(string objectName)
        {
            if (string.Equals(objectName, "GameObject", System.StringComparison.Ordinal))
            {
                return true;
            }

            return objectName.StartsWith("GameObject (", System.StringComparison.Ordinal) &&
                   objectName.EndsWith(")", System.StringComparison.Ordinal);
        }

        #endregion

        #region 步骤 2：资产查找

        /// <summary>
        /// 按类型在指定目录下查找全部资产路径。
        /// 结果排序，保证多次执行选中的是同一个资产（顺序稳定，行为可复现）。
        /// </summary>
        private static List<string> FindAssetPaths<T>(string folder) where T : UnityEngine.Object
        {
            List<string> paths = new List<string>();

            if (!AssetDatabase.IsValidFolder(folder))
            {
                Debug.LogWarning($"[AutoSceneBuilder] 目录 {folder} 不存在，跳过 {typeof(T).Name} 的查找。");
                return paths;
            }

            foreach (string guid in AssetDatabase.FindAssets("t:" + typeof(T).Name, new[] { folder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);

                // 英雄专用资产不参与通用查找，理由见 IsHeroDedicatedAsset。
                if (IsHeroDedicatedAsset(path))
                {
                    continue;
                }

                // 用 LoadAssetAtPath 再做一次类型确认：FindAssets 的 t: 过滤基于类型名，
                // 二次确认可以避免把同名/同基类的非目标资产注入槽位。
                if (AssetDatabase.LoadAssetAtPath<T>(path) != null)
                {
                    paths.Add(path);
                }
            }

            paths.Sort();
            return paths;
        }

        /// <summary>
        /// 查找目录下的预制体路径。
        /// 单独写一个方法的原因：t:GameObject 也会命中 .fbx/.obj 等模型文件，必须限定扩展名，
        /// 否则可能把模型资源当成小兵预制体注入 MinionSpawnData。
        /// </summary>
        private static List<string> FindPrefabPaths(string folder)
        {
            List<string> paths = new List<string>();

            if (!AssetDatabase.IsValidFolder(folder))
            {
                Debug.LogWarning($"[AutoSceneBuilder] 目录 {folder} 不存在，跳过预制体查找。");
                return paths;
            }

            foreach (string guid in AssetDatabase.FindAssets("t:GameObject", new[] { folder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);

                // 英雄预制体同样不参与通用查找：它由 CreateHeroPrefab 按固定路径生成并直接取用。
                if (IsHeroDedicatedAsset(path))
                {
                    continue;
                }

                if (path.EndsWith(".prefab", System.StringComparison.OrdinalIgnoreCase))
                {
                    paths.Add(path);
                }
            }

            paths.Sort();
            return paths;
        }

        /// <summary>
        /// 判断某个资产路径是否属于「英雄专用资产」（文件名以 Hero 开头）。
        ///
        /// 为什么必须排除（这是一个不会报错、但会让组装结果与预期不符的静默错误）：
        /// 通用查找是「按类型找候选 → 名称命中关键词的优先 → 都没有就用第一个占位」。
        /// 英雄资产一旦混进去就会污染这个选择：
        ///   · HeroAttack 的名称含 Attack，会被当成「小兵的 AttackData」；
        ///   · HeroStats 按字母序排在 MinionStats 之前，会顶掉「基地的 EntityStatsData」占位；
        ///   · HeroPrefab 会成为「小兵预制体」的候选。
        /// 三者都不会抛异常，只会让日志与实际注入的资产对不上，因此在这里一次性挡掉。
        /// </summary>
        private static bool IsHeroDedicatedAsset(string assetPath)
        {
            string fileName = System.IO.Path.GetFileNameWithoutExtension(assetPath);
            return fileName.StartsWith(HeroAssetNamePrefix, System.StringComparison.Ordinal);
        }

        /// <summary>
        /// 从候选路径中挑一个最合适的资产：文件名命中关键词的优先。
        /// 找不到命中项时退而取第一个候选并显式告警 —— 这是「宁可给个占位、也不让槽位空着导致运行期报错」的取舍，
        /// 但占位资产未必符合该槽位的预期（例如把 MinionStats 当基地属性用），必须让用户知道。
        /// </summary>
        private static T LoadPreferredAsset<T>(
            List<string> candidatePaths, string[] nameHints, string slotDescription) where T : UnityEngine.Object
        {
            if (candidatePaths == null || candidatePaths.Count == 0)
            {
                Debug.LogWarning(
                    $"[AutoSceneBuilder] 未找到任何 {typeof(T).Name} 资产，{slotDescription} 将留空，请在 Inspector 中手动指定。");
                return null;
            }

            string preferredPath = null;

            foreach (string path in candidatePaths)
            {
                string fileName = System.IO.Path.GetFileNameWithoutExtension(path);

                for (int i = 0; i < nameHints.Length; i++)
                {
                    if (fileName.IndexOf(nameHints[i], System.StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        preferredPath = path;
                        break;
                    }
                }

                if (preferredPath != null)
                {
                    break;
                }
            }

            if (preferredPath != null)
            {
                Debug.Log($"[AutoSceneBuilder] {slotDescription} ← {preferredPath}");
                return AssetDatabase.LoadAssetAtPath<T>(preferredPath);
            }

            string fallbackPath = candidatePaths[0];
            Debug.LogWarning(
                $"[AutoSceneBuilder] 没有名称含「{string.Join("/", nameHints)}」的 {typeof(T).Name}，" +
                $"{slotDescription} 已暂用 {fallbackPath} 作为占位资产，请确认是否符合预期。");

            return AssetDatabase.LoadAssetAtPath<T>(fallbackPath);
        }

        #endregion

        #region 步骤 3~6：创建对象

        /// <summary>
        /// 创建兵线：MidLane 挂 LanePath，下挂两个按行进顺序排列的路径点。
        /// </summary>
        private static LanePath CreateMidLane()
        {
            GameObject midLaneObject = CreateRootObject(MidLaneName, MidLanePosition);
            LanePath lanePath = Undo.AddComponent<LanePath>(midLaneObject);

            GameObject waypoint1 = CreateChildObject(midLaneObject.transform, Waypoint1Name, Waypoint1Position);
            GameObject waypoint2 = CreateChildObject(midLaneObject.transform, Waypoint2Name, Waypoint2Position);

            // Waypoints 是 LanePath 公开暴露的字段（设计上就供外部与 Inspector 装配节点列表），
            // 因此这里直接写列表，不必绕 SerializedObject 的字符串字段名，避免字段改名时静默失效。
            lanePath.Waypoints.Clear();
            lanePath.Waypoints.Add(waypoint1.transform);
            lanePath.Waypoints.Add(waypoint2.transform);
            EditorUtility.SetDirty(lanePath);

            return lanePath;
        }

        /// <summary>
        /// 创建一个阵营基地：EntityBase + HealthComponent + BaseCoreController + 可见的立方体外观。
        ///
        /// 【为什么必须给外观】原实现只建了一个空物体，在 Scene / Game 视图里完全不可见——
        /// 用户无法判断基地到底建在哪、尺寸对不对、有没有和地面重叠，属于白盒阶段欠下的账。
        /// 现在用 Cube 让基地肉眼可见，并按阵营着色（蓝方蓝、红方红），
        /// 与"小兵白色 / 英雄绿色"形成一眼可辨的配色体系。
        ///
        /// 【层级为什么是「空根节点 + Body 子节点」而不是把 Cube 直接当根】
        /// 与英雄预制体完全同一套结构，理由有两条：
        /// 1. 根节点保持【贴地坐标原点】，基地的 position 语义与其它所有关键点（出生点、路径点）一致，
        ///    GetKeyPoints / 地面覆盖校验 / 兵线距离判定都不需要额外考虑高度偏移；
        /// 2. 视觉体上抬半格高度后，立方体是「立在地面上」而不是有一半埋进地里。
        /// 碰撞体在子节点上不影响任何逻辑——索敌与拾取统一用 GetComponentInParent 向上查找。
        ///
        /// 【碰撞体从哪来】直接复用 Cube 自带的 BoxCollider，不再手工 AddComponent&lt;BoxCollider&gt;：
        /// 手工加一个会与 Cube 自带的那个重叠，让同一个基地在 OverlapSphere 结果里出现两次
        /// （索敌有去重能兜住，但没必要制造这种重复）。
        ///
        /// 刻意【不挂】CombatComponent / TargetingComponent / MovementComponent：
        /// README 3.3 规定基地「不可移动、不可攻击实体，仅作为胜负判定的载体」，
        /// 多挂反而会被 EntityBase.ValidateDependencies 按类型报出误配告警。
        /// </summary>
        /// <param name="objectName">基地对象名（进入本工具的接管清单）。</param>
        /// <param name="position">基地位置（贴地）。</param>
        /// <param name="team">阵营。</param>
        /// <param name="stats">属性资产。</param>
        /// <param name="bodyMaterial">外观材质，允许为 null（退回引擎默认材质）。</param>
        private static EntityBase CreateBase(
            string objectName, Vector3 position, TeamType team, EntityStatsData stats, Material bodyMaterial)
        {
            GameObject baseObject = CreateRootObject(objectName, position);

            GameObject body = GameObject.CreatePrimitive(PrimitiveType.Cube);
            body.name = BaseBodyName;
            body.transform.SetParent(baseObject.transform, false);

            // 立方体底面贴地：中心抬到高度的一半，否则会有一半埋在地面下。
            body.transform.localPosition = new Vector3(0f, BaseBodySize.y * 0.5f, 0f);
            body.transform.localScale = BaseBodySize;

            if (bodyMaterial != null)
            {
                // sharedMaterial 而不是 material：写入资产引用，避免生成材质实例。
                body.GetComponent<MeshRenderer>().sharedMaterial = bodyMaterial;
            }

            // 外观子节点同样登记撤销：本工具既有的约定是"每个新建对象都登记"（见 CreateChildObject），
            // 这样 Ctrl+Z 能一次回退掉整套战场，不会留下一个孤零零的立方体。
            Undo.RegisterCreatedObjectUndo(body, "创建 " + objectName + " 外观");

            EntityBase entity = Undo.AddComponent<EntityBase>(baseObject);
            Undo.AddComponent<HealthComponent>(baseObject);
            Undo.AddComponent<BaseCoreController>(baseObject);

            AssignEnum(entity, "team", (int)team);
            AssignEnum(entity, "entityType", (int)EntityType.Base);
            AssignObjectReference(entity, "statsData", stats);

            return entity;
        }

        /// <summary>
        /// 创建一个出兵点：MinionSpawner + 波次配置 + 阵营 + 出生点 + 推进路线。
        /// 出生点用对象自身的 Transform，因此出生位置就是上面传入的坐标，
        /// 初始朝向可以直接在 Scene 视图里旋转该对象来调整（MinionSpawner 会取 spawnPoint 的 position 与 rotation）。
        /// </summary>
        private static MinionSpawner CreateSpawner(
            string objectName, Vector3 position, TeamType team, MinionSpawnData spawnData, LanePath lanePath)
        {
            GameObject spawnerObject = CreateRootObject(objectName, position);
            MinionSpawner spawner = Undo.AddComponent<MinionSpawner>(spawnerObject);

            AssignObjectReference(spawner, "spawnData", spawnData);
            AssignEnum(spawner, "team", (int)team);
            AssignObjectReference(spawner, "spawnPoint", spawnerObject.transform);
            AssignObjectReference(spawner, "targetLane", lanePath);

            return spawner;
        }

        /// <summary>
        /// 创建对局编排对象：MatchController + 对局配置 + 双方出兵点。
        /// </summary>
        /// <returns>刚创建的控制器，供"清理多余控制器"与日志使用。</returns>
        private static MatchController CreateMatchController(
            MatchConfigData matchConfig, MinionSpawner blueSpawner, MinionSpawner redSpawner)
        {
            GameObject battlefieldObject = CreateRootObject(BattlefieldName, Vector3.zero);
            MatchController controller = Undo.AddComponent<MatchController>(battlefieldObject);

            AssignObjectReference(controller, "matchConfig", matchConfig);
            AssignObjectArray(controller, "spawners", new UnityEngine.Object[] { blueSpawner, redSpawner });

            return controller;
        }

        /// <summary>在场景根下创建一个空物体并登记撤销。</summary>
        private static GameObject CreateRootObject(string objectName, Vector3 position)
        {
            GameObject created = new GameObject(objectName);
            created.transform.position = position;
            Undo.RegisterCreatedObjectUndo(created, "创建 " + objectName);
            return created;
        }

        /// <summary>创建子物体。写入的是【世界坐标】，因此 MidLane 日后被移动也不会改变路径点的实际落点。</summary>
        private static GameObject CreateChildObject(Transform parent, string objectName, Vector3 worldPosition)
        {
            GameObject created = new GameObject(objectName);

            // worldPositionStays = true 只是让本次挂载不改变世界坐标；紧接着用 position 显式写入世界坐标，
            // 保证语义唯一（局部坐标还是世界坐标不会含糊）。
            created.transform.SetParent(parent, true);
            created.transform.position = worldPosition;

            Undo.RegisterCreatedObjectUndo(created, "创建 " + objectName);
            return created;
        }

        #endregion

        #region 步骤 8：地面覆盖与 NavMesh 烘焙

        /// <summary>
        /// 让地面覆盖整个战场并烘焙 NavMesh —— 这是「一键组装完就能直接 Play」的前置条件。
        ///
        /// 为什么必须由工具来做（而不是像收尾校验那样只提示）：
        /// 阶段五的验收标准是「全新克隆 → 打开 MainScene → 执行一次菜单 → 直接 Play → 完整对局」。
        /// 而出厂的 MainScene 只有一块 10×10 的 Plane（x∈[-7.8, 2.2]，z∈[-2.7, 7.3]），
        /// 英雄出生点 (-12,0,0)、蓝方基地 (-10,0,0)、蓝方出兵点 (-8,0,0) 全在范围外；
        /// 场景也从未烘焙过 NavMesh（NavMeshSettings.m_NavMeshData 为空）。
        /// 这种配置下 NavMeshAgent 会报「not close enough to the NavMesh」，英雄与小兵永久卡在出生点，
        /// 而 Console 里看不出到底是坐标错了还是地面小了 —— 不动地面，怎么点菜单都测不出右键控制。
        ///
        /// 四条自我约束（避免工具越权改关卡）：
        /// 1. 【只放大不缩小】：地面已经覆盖战场范围时一个字节都不改，因此连续执行完全幂等；
        /// 2. 【只动内置 Plane 网格】：按 MeshFilter 的网格名识别，Terrain / 自定义网格只告警不缩放
        ///    （那些网格的尺寸语义各不相同，按比例缩放很可能把地面拉变形）；
        /// 3. 【保持正方形】：统一取 x/z 需求里的较大值，避免非均匀缩放把 NavMesh 的 cell 拉成矩形；
        /// 4. 【只清手工残留、不动工具自己的产物】：只删 Ground 对象上的 NavMeshSurface 组件
        ///    （手工排障遗留，见 RemoveManualNavMeshSurfaces），不碰任何其它用户组件。
        /// </summary>
        /// <param name="notes">本次实际写入的描述列表，供日志核对。</param>
        private static void EnsureGroundAndNavMesh(List<string> notes)
        {
            int groundLayer = LayerMask.NameToLayer(GroundLayerName);
            if (groundLayer < 0)
            {
                Debug.LogError(
                    $"[AutoSceneBuilder] 项目里没有名为 \"{GroundLayerName}\" 的 Layer，无法定位地面，跳过地面放大与 NavMesh 烘焙。");
                return;
            }

            // ---- 1. 收集 Ground 层对象，并算出地面当前的水平包围盒 ----
            // 用 Collider 而不是 Renderer：碰撞体才是射线拾取与 NavMesh 烘焙真正依赖的东西，
            // 且 Terrain 这类地面没有 MeshRenderer（用 Renderer 会漏判）。只比较 x/z，理由见 ValidateGroundCoverage。
            List<GameObject> groundObjects = new List<GameObject>();
            Bounds groundBounds = new Bounds(Vector3.zero, Vector3.zero);
            bool hasGroundBounds = false;

            Collider[] colliders = UnityEngine.Object.FindObjectsOfType<Collider>(true);

            for (int i = 0; i < colliders.Length; i++)
            {
                Collider collider = colliders[i];

                if (collider == null || collider.gameObject.layer != groundLayer)
                {
                    continue;
                }

                GameObject groundObject = collider.gameObject;

                if (!groundObjects.Contains(groundObject))
                {
                    groundObjects.Add(groundObject);
                }

                if (hasGroundBounds)
                {
                    groundBounds.Encapsulate(collider.bounds);
                }
                else
                {
                    groundBounds = collider.bounds;
                    hasGroundBounds = true;
                }
            }

            if (!hasGroundBounds)
            {
                Debug.LogError(
                    $"[AutoSceneBuilder] {GroundLayerName} 层上没有任何碰撞体，既无法确定地面范围，也无法烘焙 NavMesh。" +
                    "请给地面对象补上碰撞体并确认它位于该 Layer。");
                return;
            }

            // ---- 2. 找出「可安全按比例放大」的地面：Ground 层 + 内置 Plane 网格 ----
            GameObject resizableGround = null;
            float resizableArea = 0f;

            MeshFilter[] meshFilters = UnityEngine.Object.FindObjectsOfType<MeshFilter>(true);

            for (int i = 0; i < meshFilters.Length; i++)
            {
                MeshFilter meshFilter = meshFilters[i];

                if (meshFilter == null || meshFilter.gameObject.layer != groundLayer)
                {
                    continue;
                }

                Mesh mesh = meshFilter.sharedMesh;
                if (mesh == null || !string.Equals(mesh.name, PlaneMeshName, System.StringComparison.Ordinal))
                {
                    continue;
                }

                // 只挑最大的那一块放大：地面通常只有一块，若场景里有多块 Plane，
                // 把每一块都放大到覆盖整个战场会让它们彼此重叠，反而更乱。
                // 用「还没选中任何一块」作为兜底条件：万一地面没挂碰撞体，包围盒面积为 0，
                // 只比较面积会一块都选不中，白白放弃自动放大。
                Collider planeCollider = meshFilter.GetComponent<Collider>();
                Bounds bounds = planeCollider != null
                    ? planeCollider.bounds
                    : new Bounds(meshFilter.transform.position, Vector3.zero);
                float area = Mathf.Abs(bounds.size.x * bounds.size.z);

                if (resizableGround == null || area > resizableArea)
                {
                    resizableArea = area;
                    resizableGround = meshFilter.gameObject;
                }
            }

            // ---- 3. 算出战场需要的水平范围，并与当前地面比较 ----
            // 兜底：可放大的地面也纳入地面对象清单 —— 自愈清理要覆盖到它，
            // 否则会出现「手工组件挂在一块没被清到的地面上」。
            if (resizableGround != null && !groundObjects.Contains(resizableGround))
            {
                groundObjects.Add(resizableGround);
            }

            // ---- 4. 自愈：清理 Ground 上手工残留的 NavMeshSurface ----
            RemoveManualNavMeshSurfaces(groundObjects, notes);

            // ---- 5. 覆盖判断与放大 ----
            Bounds requiredBounds = ComputeRequiredGroundBounds();

            if (CoversHorizontally(groundBounds, requiredBounds))
            {
                notes.Add(
                    $"地面已覆盖战场范围（现有 x∈[{groundBounds.min.x:F1}, {groundBounds.max.x:F1}]，" +
                    $"z∈[{groundBounds.min.z:F1}, {groundBounds.max.z:F1}]），未改动");
            }
            else if (resizableGround == null)
            {
                Debug.LogWarning(
                    "[AutoSceneBuilder] 地面未覆盖战场范围，但 Ground 层上找不到可自动放大的内置 Plane 网格" +
                    "（可能是 Terrain 或自定义网格）。请手动放大地面，否则出生点会落在 NavMesh 之外。" +
                    $"需要的水平范围：x∈[{requiredBounds.min.x:F1}, {requiredBounds.max.x:F1}]，" +
                    $"z∈[{requiredBounds.min.z:F1}, {requiredBounds.max.z:F1}]。");
            }
            else
            {
                ResizePlaneGround(resizableGround, requiredBounds, notes);

                // 改完 Transform 立刻同步一次物理：Collider.bounds 与导航烘焙都读的是「物理世界里」的变换，
                // 而编辑器默认不会每帧自动同步（Physics.autoSyncTransforms 为 false）。
                // 不同步的话，收尾的 ValidateGroundCoverage 可能读到放大前的旧包围盒，
                // 反过来报一条「落点超出地面范围」的假告警——刚修好就报警，比不报还难排查。
                Physics.SyncTransforms();
            }

            // ---- 6. 烘焙 ----
            // 用放大【之后】重新测得的实际地面范围去收集几何：上面那份 groundBounds 是改动前的快照。
            if (!TryCollectGroundBounds(groundLayer, out Bounds bakedBounds))
            {
                Debug.LogError("[AutoSceneBuilder] 放大地面后重新扫描不到 Ground 层碰撞体，跳过导航烘焙。");
                return;
            }

            BakeNavMeshModern(groundLayer, bakedBounds, notes);
        }

        /// <summary>
        /// 重新扫描 Ground 层碰撞体，返回它们的水平包围盒（放大地面之后调用，拿到实际生效的范围）。
        /// 与 EnsureGroundAndNavMesh 开头那次扫描口径一致：只比 x/z，y 不参与比较。
        /// </summary>
        private static bool TryCollectGroundBounds(int groundLayer, out Bounds bounds)
        {
            Collider[] colliders = UnityEngine.Object.FindObjectsOfType<Collider>(true);

            bounds = new Bounds(Vector3.zero, Vector3.zero);
            bool hasBounds = false;

            for (int i = 0; i < colliders.Length; i++)
            {
                Collider collider = colliders[i];

                if (collider == null || collider.gameObject.layer != groundLayer)
                {
                    continue;
                }

                if (hasBounds)
                {
                    bounds.Encapsulate(collider.bounds);
                }
                else
                {
                    bounds = collider.bounds;
                    hasBounds = true;
                }
            }

            return hasBounds;
        }

        /// <summary>
        /// 自愈：移除 Ground 对象上手工残留的 NavMeshSurface 组件。
        ///
        /// 为什么必须清掉：本工程的导航数据由本工具用 NavMeshBuilder 全自动烘焙、并原地更新场景级 NavMeshData 资产；
        /// 而手工挂上的 NavMeshSurface（AI Navigation 包，带 `[ExecuteAlways]`）会在编辑器里**再注册一份** navmesh 数据，
        /// 于是场景里同时存在两份导航数据 —— 既重复，又会让它引用的资产看起来「没人用」，
        /// 极易被当成孤儿资产误删（本项目差点踩到）。这同样违反 §6 铁律「场景装配必须自动化，严禁手工拖拽依赖」。
        ///
        /// 幂等性：清完之后没有组件可清，重复执行不会产生任何改动。
        /// </summary>
        /// <param name="groundObjects">Ground 层对象清单。</param>
        /// <param name="notes">本次写入的描述，供日志核对。</param>
        private static void RemoveManualNavMeshSurfaces(List<GameObject> groundObjects, List<string> notes)
        {
            List<string> removed = new List<string>();

            for (int i = 0; i < groundObjects.Count; i++)
            {
                GameObject groundObject = groundObjects[i];

                if (groundObject == null)
                {
                    continue;
                }

                // 用复数版一次清干净：手工排障时很容易反复添加同一个组件。
                NavMeshSurface[] surfaces = groundObject.GetComponents<NavMeshSurface>();

                for (int j = 0; j < surfaces.Length; j++)
                {
                    if (surfaces[j] == null)
                    {
                        continue;
                    }

                    removed.Add($"{groundObject.name}.NavMeshSurface");
                    Undo.DestroyObjectImmediate(surfaces[j]);
                }
            }

            if (removed.Count > 0)
            {
                notes.Add($"已清理手工残留组件：{string.Join("、", removed)}");
            }
        }

        /// <summary>
        /// 用现代导航烘焙 API 生成导航网格，并**原地更新**场景已有的 NavMeshData 资产。
        ///
        /// 【为什么不再用 StaticEditorFlags.NavigationStatic】
        /// 该枚举成员在本引擎版本已弃用（CS0618），官方给出的替代路径正是
        /// `NavMeshBuilder.CollectSources` + `NavMeshBuildMarkup` —— 由调用方显式收集几何，
        /// 不再依赖「对象上打了静态标记」这种隐式选择，因此这里彻底不再触碰该标记。
        ///
        /// 【为什么是「原地更新已有资产」而不是「新建资产」】
        /// 场景级 NavMeshSettings 对导航数据的引用是**按 guid** 存的，而 `AssetDatabase.CreateAsset`
        /// 覆盖同路径资产时会先删除旧资产（guid 随之改变）→ 场景引用直接断链。
        /// 因此这里把烘焙结果用 `EditorUtility.CopySerialized` 灌进已有资产对象：**内容更新、身份不变，引用不断**。
        ///
        /// 【为什么烘焙后要 RemoveAllNavMeshData + AddNavMeshData】
        /// 编辑器的导航世界是在场景加载时按「当时的资产内容」建立的，改资产不会自动刷新它。
        /// 不刷新的话，收尾校验（`NavMesh.CalculateTriangulation` / `SamplePosition`）读到的还是上一次的数据，
        /// 会报出「落点不在 NavMesh 覆盖范围内」这类假告警。先清空再加，保证重复执行不会累加出多份数据。
        /// 这一步只影响编辑器会话、不写盘；进播放模式时场景会按更新后的资产重新加载。
        /// </summary>
        private static void BakeNavMeshModern(int groundLayer, Bounds groundBounds, List<string> notes)
        {
            string assetPath = FindSceneNavMeshAssetPath();

            if (string.IsNullOrEmpty(assetPath))
            {
                Debug.LogError(
                    "[AutoSceneBuilder] 场景文件夹里找不到 NavMeshData 资产，无法写入导航数据。" +
                    "请在 Navigation 窗口执行一次 Bake（它会创建资产并挂到场景的 NavMeshSettings 上），然后重新运行本工具。");
                return;
            }

            // 收集范围 = 地面水平范围 + 上下各留 5 米：CollectSources 按「包围盒相交」筛选对象，
            // 而地面是一张很薄的网格，垂直方向不留余量就可能与范围擦肩而过。
            Bounds collectBounds = groundBounds;
            collectBounds.Expand(new Vector3(0f, 10f, 0f));

            // 只收集 Ground 层：单位与建筑都不在这一层，因此不会把动态对象烘进导航网格。
            int layerMask = 1 << groundLayer;

            List<NavMeshBuildMarkup> markups = new List<NavMeshBuildMarkup>();
            List<NavMeshBuildSource> sources = new List<NavMeshBuildSource>();

            NavMeshBuilder.CollectSources(
                collectBounds, layerMask, NavMeshCollectGeometry.RenderMeshes, 0, markups, sources);

            if (sources.Count == 0)
            {
                Debug.LogError(
                    "[AutoSceneBuilder] 在 Ground 层上没有收集到任何可烘焙几何" +
                    $"（范围 x∈[{collectBounds.min.x:F1}, {collectBounds.max.x:F1}]，" +
                    $"z∈[{collectBounds.min.z:F1}, {collectBounds.max.z:F1}]）。" +
                    "请确认地面带 MeshRenderer 且位于 Ground 层。");
                return;
            }

            // agentTypeID 0 = Navigation 窗口里的第一个 Agent（本项目地面用的就是它：半径 0.5 / 高度 2）。
            NavMeshBuildSettings settings = NavMesh.GetSettingsByID(0);

            NavMeshData baked = NavMeshBuilder.BuildNavMeshData(
                settings, sources, collectBounds, Vector3.zero, Quaternion.identity);

            if (baked == null)
            {
                Debug.LogError("[AutoSceneBuilder] NavMeshBuilder.BuildNavMeshData 返回 null，导航烘焙失败。");
                return;
            }

            NavMeshData target = AssetDatabase.LoadAssetAtPath<NavMeshData>(assetPath);

            if (target == null)
            {
                Debug.LogError($"[AutoSceneBuilder] {assetPath} 不是 NavMeshData 资产，烘焙结果无法写入。");
                UnityEngine.Object.DestroyImmediate(baked);
                return;
            }

            EditorUtility.CopySerialized(baked, target);
            EditorUtility.SetDirty(target);
            AssetDatabase.SaveAssets();

            // 刷新编辑器导航世界，让收尾校验读到的是本次结果（详见方法注释）。
            RefreshEditorNavMeshWorld(baked);

            // 立刻验证：烘焙失败不会抛异常，只会留下一张空网格，因此必须主动读三角形数。
            NavMeshTriangulation triangulation = NavMesh.CalculateTriangulation();
            int triangleCount = triangulation.vertices != null ? triangulation.vertices.Length / 3 : 0;

            if (triangleCount == 0)
            {
                Debug.LogWarning(
                    "[AutoSceneBuilder] 导航数据已写入资产，但编辑器导航世界里读不到任何可导航区域。" +
                    "请检查 NavMeshSettings 的 Agent 参数（半径 / 高度 / 坡度）是否与地面尺寸匹配。");
            }

            notes.Add($"NavMesh 已烘焙（{sources.Count} 个几何源 / {triangleCount} 个三角形）并原地更新 {assetPath}");
        }

        /// <summary>
        /// 找到场景文件夹里的 NavMeshData 资产（场景级导航数据就存在这里，由 NavMeshSettings 按 guid 引用）。
        ///
        /// 用「列出文件夹内全部资产、再逐个按类型加载」而不是 `AssetDatabase.FindAssets("t:NavMeshData")`：
        /// 后者依赖类型过滤器对原生类型的支持，前者不依赖任何过滤器，行为完全确定。
        /// 同名候选里优先取 `NavMesh*`：场景文件夹可能残留多个历史 navmesh 资产，约定名更可能是在用的那个。
        /// </summary>
        private static string FindSceneNavMeshAssetPath()
        {
            string scenePath = SceneManager.GetActiveScene().path;

            if (string.IsNullOrEmpty(scenePath))
            {
                return null;
            }

            string sceneFolder = System.IO.Path.GetDirectoryName(scenePath);

            if (string.IsNullOrEmpty(sceneFolder))
            {
                return null;
            }

            sceneFolder = sceneFolder.Replace('\\', '/');

            string[] guids = AssetDatabase.FindAssets(string.Empty, new[] { sceneFolder });
            string fallback = null;

            for (int i = 0; i < guids.Length; i++)
            {
                string candidatePath = AssetDatabase.GUIDToAssetPath(guids[i]);

                if (AssetDatabase.LoadAssetAtPath<NavMeshData>(candidatePath) == null)
                {
                    continue;
                }

                if (System.IO.Path.GetFileNameWithoutExtension(candidatePath)
                    .StartsWith("NavMesh", System.StringComparison.Ordinal))
                {
                    return candidatePath;
                }

                if (fallback == null)
                {
                    fallback = candidatePath;
                }
            }

            return fallback;
        }

        /// <summary>
        /// 把本次烘焙结果注册进编辑器的导航世界，让收尾校验读到的是最新数据。
        /// 只影响编辑器会话，不写盘；进播放模式时场景会按更新后的资产重新加载。
        /// </summary>
        /// <param name="baked">本次烘焙出的临时 NavMeshData（注册后由 previewNavMeshData 持有，下次执行时回收）。</param>
        private static void RefreshEditorNavMeshWorld(NavMeshData baked)
        {
            // 先回收上一次的临时数据：否则反复执行会在世界里堆出多份，三角形数越报越多。
            ReleasePreviewNavMesh();

            // 再清空：场景在加载时已把它当时的导航数据注册进世界，不清掉就会与新数据叠加。
            NavMesh.RemoveAllNavMeshData();

            previewNavMeshData = baked;
            previewNavMeshInstance = NavMesh.AddNavMeshData(baked);
            hasPreviewNavMeshInstance = true;
        }

        /// <summary>回收上一次注册的临时导航数据（只作用于编辑器会话内的对象）。</summary>
        private static void ReleasePreviewNavMesh()
        {
            if (hasPreviewNavMeshInstance)
            {
                NavMesh.RemoveNavMeshData(previewNavMeshInstance);
                hasPreviewNavMeshInstance = false;
            }

            if (previewNavMeshData != null)
            {
                UnityEngine.Object.DestroyImmediate(previewNavMeshData);
                previewNavMeshData = null;
            }
        }

        /// <summary>
        /// 计算战场需要的水平范围：所有关键落点取包围盒，再向四周外扩 <see cref="GroundPadding"/> 米。
        /// 外扩的目的：让英雄有走位空间、兵线有展开空间，而不是贴着地面边缘交战。
        /// </summary>
        private static Bounds ComputeRequiredGroundBounds()
        {
            Vector3[] points = GetKeyPoints();

            Bounds bounds = new Bounds(points[0], Vector3.zero);

            for (int i = 1; i < points.Length; i++)
            {
                bounds.Encapsulate(points[i]);
            }

            // Expand 的参数是「总共增加多少」，因此外扩量要乘 2 才能做到每边外扩 GroundPadding。
            // y 不参与：地面高度是关卡决策，工具不该动它。
            bounds.Expand(new Vector3(GroundPadding * 2f, 0f, GroundPadding * 2f));
            return bounds;
        }

        /// <summary>判断地面在水平面上是否已经完整覆盖需求范围（只比 x/z，y 不参与）。</summary>
        private static bool CoversHorizontally(Bounds groundBounds, Bounds requiredBounds)
        {
            return groundBounds.min.x <= requiredBounds.min.x && groundBounds.max.x >= requiredBounds.max.x &&
                   groundBounds.min.z <= requiredBounds.min.z && groundBounds.max.z >= requiredBounds.max.z;
        }

        /// <summary>
        /// 把一块内置 Plane 地面放大并居中到需求范围。
        /// Plane 原始网格是 10×10 米，因此 localScale = 需要的边长 / 10；
        /// y 方向的缩放与位置一律保持原值（地面高度是关卡决策，工具只动水平面）。
        /// </summary>
        private static void ResizePlaneGround(GameObject groundObject, Bounds requiredBounds, List<string> notes)
        {
            Transform groundTransform = groundObject.transform;

            // 先登记撤销：变换与静态标记都属于场景资产改动，必须能用一次 Ctrl+Z 回退。
            Undo.RecordObject(groundTransform, "放大地面");

            Vector3 originalScale = groundTransform.localScale;
            float currentSize = Mathf.Max(Mathf.Abs(originalScale.x), Mathf.Abs(originalScale.z)) * PlaneMeshSize;

            float requiredSize = Mathf.Max(requiredBounds.size.x, requiredBounds.size.z);
            float targetSize = Mathf.Max(Mathf.Max(requiredSize, MinGroundSize), currentSize);
            float targetScale = targetSize / PlaneMeshSize;

            groundTransform.localScale = new Vector3(targetScale, originalScale.y, targetScale);

            // 水平居中到需求范围中心，y 保持原值。
            Vector3 position = groundTransform.position;
            position.x = requiredBounds.center.x;
            position.z = requiredBounds.center.z;
            groundTransform.position = position;

            notes.Add(
                $"{groundObject.name} 放大到 {targetSize:F0}×{targetSize:F0}，" +
                $"水平中心 ({position.x:F1}, {position.z:F1})");
        }

        /// <summary>
        /// 战场关键落点：双方出兵点、双方基地、两个路径点、英雄出生点。
        /// 抽成方法的原因：地面覆盖校验、NavMesh 覆盖校验、地面自动放大三处都要用同一份清单，
        /// 各自维护一份迟早出现「改了坐标只改一处」的漏网。
        /// </summary>
        private static Vector3[] GetKeyPoints()
        {
            return new[]
            {
                BlueSpawnerPosition, BlueBasePosition, RedSpawnerPosition, RedBasePosition,
                Waypoint1Position, Waypoint2Position, HeroSpawnPosition
            };
        }

        /// <summary>与 <see cref="GetKeyPoints"/> 一一对应的名字，仅用于日志定位。</summary>
        private static string[] GetKeyPointLabels()
        {
            return new[]
            {
                BlueSpawnerName, BlueBaseName, RedSpawnerName, RedBaseName,
                Waypoint1Name, Waypoint2Name, HeroRootName
            };
        }

        #endregion

        #region 步骤 9~11：玩家英雄（预制体 / 场景实例 / 相机跟随）

        /// <summary>
        /// 确保英雄普攻配置资产存在（射程 2.5 就配在这里）。
        /// 只在资产缺失时创建、绝不覆盖已有配置 —— 与 PatchAssetGaps 的取舍一致：
        /// 这些是策划数值，工具不该在每次一键组装时把人工调过的值抹掉。
        /// </summary>
        /// <returns>可用的普攻资产；目录缺失导致无法创建时返回 null。</returns>
        private static AttackData EnsureHeroAttackAsset(List<string> notes)
        {
            AttackData existing = AssetDatabase.LoadAssetAtPath<AttackData>(HeroAttackAssetPath);
            if (existing != null)
            {
                notes.Add($"{HeroAttackAssetPath} 已存在，沿用现有数值（不覆盖）");
                return existing;
            }

            if (!AssetDatabase.IsValidFolder(ScriptableObjectsFolder))
            {
                Debug.LogError(
                    $"[AutoSceneBuilder] 目录 {ScriptableObjectsFolder} 不存在，无法创建英雄普攻资产，" +
                    "英雄将没有攻击配置（CombatComponent 会报错且无法攻击）。");
                return null;
            }

            AttackData created = ScriptableObject.CreateInstance<AttackData>();
            AssetDatabase.CreateAsset(created, HeroAttackAssetPath);

            AssignFloat(created, "damage", HeroAttackDamage);
            AssignFloat(created, "attackRange", HeroAttackRange);
            AssignFloat(created, "attackInterval", HeroAttackInterval);
            EditorUtility.SetDirty(created);
            AssetDatabase.SaveAssets();

            notes.Add(
                $"新建 {HeroAttackAssetPath}（伤害 {HeroAttackDamage} / 射程 {HeroAttackRange} / 间隔 {HeroAttackInterval}）");
            return created;
        }

        /// <summary>
        /// 确保基地属性资产存在（确定性资产：缺失才创建，已存在沿用、不覆盖）。
        ///
        /// 【为什么不再用通用查找】原实现用 LoadPreferredAsset&lt;EntityStatsData&gt;(..., {"Base","Tower"}, ...)
        /// 在 Assets/ScriptableObjects 下按"文件名含 Base / Tower"挑选。而仓库里只有 MinionStats 与 HeroStats
        /// （HeroStats 又被 IsHeroDedicatedAsset 排除），因此：
        ///   ① 每次组装都会输出一条"没有名称含 Base/Tower 的 EntityStatsData，已暂用 MinionStats 作为占位资产"的告警；
        ///   ② 更严重的是基地真的拿到了小兵量级的生命值——几波兵就能推掉基地，胜负闭环失去意义。
        /// 现在改为按固定路径直接创建/取用，查找结果完全可预期，也不再需要"占位"这个中间态。
        ///
        /// 与 HeroStats / HeroAttack 同一约定：只写一次数值，之后一律沿用（策划可能在 Inspector 里调过）。
        /// </summary>
        private static EntityStatsData EnsureBaseStatsAsset(List<string> notes)
        {
            EntityStatsData existing = AssetDatabase.LoadAssetAtPath<EntityStatsData>(BaseStatsAssetPath);
            if (existing != null)
            {
                notes.Add($"{BaseStatsAssetPath} 已存在，沿用现有数值（不覆盖）");
                return existing;
            }

            if (!AssetDatabase.IsValidFolder(ScriptableObjectsFolder))
            {
                Debug.LogError(
                    $"[AutoSceneBuilder] 目录 {ScriptableObjectsFolder} 不存在，无法创建基地属性资产，" +
                    "基地将没有生命值配置（运行时 EntityBase 会报错，且基地不会受伤）。");
                return null;
            }

            EntityStatsData created = ScriptableObject.CreateInstance<EntityStatsData>();
            AssetDatabase.CreateAsset(created, BaseStatsAssetPath);

            AssignFloat(created, "maxHealth", BaseMaxHealth);

            // 基地不可移动、不索敌、不攻击：这三个字段刻意留 0 / 空，
            // 与其"不可移动、不可攻击"的设计定位一致（填了反而会被误读为它具备这些能力）。
            AssignFloat(created, "moveSpeed", 0f);
            AssignFloat(created, "detectionRange", 0f);

            EditorUtility.SetDirty(created);
            AssetDatabase.SaveAssets();

            notes.Add($"新建 {BaseStatsAssetPath}（基地生命 {BaseMaxHealth}，移速 / 索敌 / 攻击留空）");
            return created;
        }

        /// <summary>
        /// 确保英雄属性资产存在（生命 500 / 移速 5 就配在这里）。
        /// 攻击配置一并注入，保证「配置 → 组件」这条数据流与运行时完全一致
        /// （EntityBase.ApplyStats 会把它转交给 CombatComponent）。
        /// </summary>
        private static EntityStatsData EnsureHeroStatsAsset(AttackData heroAttack, List<string> notes)
        {
            EntityStatsData existing = AssetDatabase.LoadAssetAtPath<EntityStatsData>(HeroStatsAssetPath);
            if (existing != null)
            {
                notes.Add($"{HeroStatsAssetPath} 已存在，沿用现有数值（不覆盖）");
                return existing;
            }

            if (!AssetDatabase.IsValidFolder(ScriptableObjectsFolder))
            {
                Debug.LogError(
                    $"[AutoSceneBuilder] 目录 {ScriptableObjectsFolder} 不存在，无法创建英雄属性资产，" +
                    "英雄将使用组件默认值（100 生命 / 3.5 移速）。");
                return null;
            }

            EntityStatsData created = ScriptableObject.CreateInstance<EntityStatsData>();
            AssetDatabase.CreateAsset(created, HeroStatsAssetPath);

            AssignFloat(created, "maxHealth", HeroMaxHealth);
            AssignFloat(created, "moveSpeed", HeroMoveSpeed);
            AssignFloat(created, "detectionRange", HeroDetectionRange);
            AssignObjectReference(created, "attack", heroAttack);
            EditorUtility.SetDirty(created);
            AssetDatabase.SaveAssets();

            notes.Add(
                $"新建 {HeroStatsAssetPath}（生命 {HeroMaxHealth} / 移速 {HeroMoveSpeed} / 索敌 {HeroDetectionRange}）");
            return created;
        }

        /// <summary>
        /// 确保英雄材质资产存在，返回可用于胶囊体的绿色材质。
        ///
        /// 为什么用 Material 资产而不是给渲染器设 material.color：
        /// 后者会在运行期克隆出材质实例（破坏合批、也污染内存），而资产引用是共享的。
        /// 只在缺失时创建；找不到着色器时返回 null 并告警——胶囊体将退回引擎默认材质（灰白），
        /// 不影响任何逻辑，因此不阻断整次组装。
        /// </summary>
        private static Material EnsureHeroMaterial(List<string> notes)
        {
            // 复用通用材质工厂：英雄材质没有独有的创建流程，只有"路径 + 颜色 + 描述"不同。
            return EnsureColorMaterial(HeroMaterialPath, HeroColor, 0.1f, "英雄胶囊体材质（绿色）", notes);
        }

        /// <summary>
        /// 确保弹道材质存在（火球橙）。
        /// 用材质资产而不是给渲染器设 material.color：后者会在运行期克隆出材质实例（破坏合批、也污染内存），
        /// 而资产引用是共享的。
        /// </summary>
        private static Material EnsureProjectileMaterial(List<string> notes)
        {
            return EnsureColorMaterial(ProjectileMaterialPath, ProjectileColor, 0.2f, "弹道材质（火球橙）", notes);
        }

        /// <summary>
        /// 通用纯色材质工厂：缺失才创建，已存在直接返回（不覆盖）。
        ///
        /// 抽成通用方法的理由：英雄材质与弹道材质只有路径与颜色不同，
        /// 各写一份意味着"日后切到 URP 要改两个地方的着色器名"——
        /// 漏改一处就会出现"英雄是彩色、弹道是品红"的诡异现象。
        /// </summary>
        /// <param name="assetPath">材质资产路径。</param>
        /// <param name="color">基础颜色。</param>
        /// <param name="glossiness">高光度（越小越"哑光"，顶视角下更容易分辨颜色）。</param>
        /// <param name="description">日志用的描述。</param>
        /// <param name="notes">组装说明收集器。</param>
        /// <returns>材质资产；着色器缺失时返回 null（调用方需判空）。</returns>
        private static Material EnsureColorMaterial(
            string assetPath, Color color, float glossiness, string description, List<string> notes)
        {
            Material existing = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
            if (existing != null)
            {
                return existing;
            }

            // 逐级建目录：AssetDatabase.CreateFolder 不会自动创建父级。
            if (!AssetDatabase.IsValidFolder("Assets/Art"))
            {
                AssetDatabase.CreateFolder("Assets", "Art");
            }

            if (!AssetDatabase.IsValidFolder(MaterialsFolder))
            {
                AssetDatabase.CreateFolder("Assets/Art", "Materials");
            }

            // 本项目使用内置渲染管线（Graphics Settings 未指定 SRP），因此 Standard 着色器必然存在。
            // 万一取不到（例如日后切到 URP），退回默认材质即可，不报 Error。
            Shader shader = Shader.Find("Standard");
            if (shader == null)
            {
                Debug.LogWarning(
                    $"[AutoSceneBuilder] 未找到 Standard 着色器，{description} 无法生成，" +
                    "对应对象将使用引擎默认材质。若项目已切换到 URP/HDRP，请把本方法里的着色器名改成对应的 Lit 着色器。");
                return null;
            }

            Material material = new Material(shader);
            material.color = color;

            // 去掉默认高光，让物体在顶视角下颜色更实、更容易与其它单位区分。
            material.SetFloat("_Glossiness", glossiness);
            material.SetFloat("_Metallic", 0f);

            AssetDatabase.CreateAsset(material, assetPath);
            AssetDatabase.SaveAssets();

            notes.Add($"新建 {assetPath}（{description}）");
            return material;
        }

        #region 步骤 13：技能资产与属性资产补缺（阶段六）

        /// <summary>
        /// 确保 Q 技能资产存在（指向性非穿透火球）。
        ///
        /// 已存在时【不覆盖数值】：它是策划数据（策划会在 Inspector 里调 CD / 伤害 / 弹速），
        /// 而且被 HeroPrefab 按 guid 引用——"删除重建"会直接断链。
        /// 数值只在"资产缺失、需要新建"的那一次写入。
        /// </summary>
        private static SkillData EnsureHeroSkillQAsset(List<string> notes)
        {
            SkillData existing = AssetDatabase.LoadAssetAtPath<SkillData>(HeroSkillQAssetPath);
            if (existing != null)
            {
                notes.Add($"{HeroSkillQAssetPath} 已存在，沿用现有数值（不覆盖）");
                return existing;
            }

            SkillData created = CreateSkillAsset(HeroSkillQAssetPath);
            if (created == null)
            {
                return null;
            }

            AssignEnum(created, "slot", (int)SkillSlot.Q);
            AssignString(created, "displayName", "烈焰火球");
            AssignString(created, "description", "锁定一名敌方单位，发射非穿透火球，命中时造成伤害并销毁。");
            AssignEnum(created, "castType", (int)SkillCastType.UnitTarget);
            AssignFloat(created, "castRange", SkillQCastRange);
            AssignFloat(created, "castTime", SkillQCastTime);
            AssignFloat(created, "cooldown", SkillQCooldown);
            AssignFloat(created, "manaCost", SkillQManaCost);
            AssignFloat(created, "projectileSpeed", SkillQProjectileSpeed);
            AssignFloat(created, "projectileRadius", SkillQProjectileRadius);
            AssignInt(created, "maxHitCount", SkillQMaxHitCount);
            AssignFloat(created, "maxTravelDistance", SkillQMaxTravelDistance);
            AssignEnum(created, "effectType", (int)SkillEffectType.Damage);
            AssignFloat(created, "damage", SkillQDamage);
            AssignString(created, "castVfxId", "SkillQ_Cast");
            AssignString(created, "hitVfxId", "SkillQ_Hit");
            AssignString(created, "releaseVfxId", "SkillQ_Projectile");

            EditorUtility.SetDirty(created);
            AssetDatabase.SaveAssets();

            notes.Add(
                $"新建 {HeroSkillQAssetPath}（指向性弹道：射程 {SkillQCastRange} / 前摇 {SkillQCastTime}s / CD {SkillQCooldown}s / " +
                $"蓝耗 {SkillQManaCost} / 弹速 {SkillQProjectileSpeed} / 伤害 {SkillQDamage} / 命中上限 {SkillQMaxHitCount} 即非穿透）");
            return created;
        }

        /// <summary>
        /// 确保 W 技能资产存在（非指向性 AOE 减速圈）。已存在时不覆盖，规则同 Q。
        /// </summary>
        private static SkillData EnsureHeroSkillWAsset(List<string> notes)
        {
            SkillData existing = AssetDatabase.LoadAssetAtPath<SkillData>(HeroSkillWAssetPath);
            if (existing != null)
            {
                notes.Add($"{HeroSkillWAssetPath} 已存在，沿用现有数值（不覆盖）");
                return existing;
            }

            SkillData created = CreateSkillAsset(HeroSkillWAssetPath);
            if (created == null)
            {
                return null;
            }

            AssignEnum(created, "slot", (int)SkillSlot.W);
            AssignString(created, "displayName", "寒霜领域");
            AssignString(created, "description", "在指定落点生成持续范围效果，周期性对范围内敌方单位施加减速。");
            AssignEnum(created, "castType", (int)SkillCastType.GroundPoint);
            AssignFloat(created, "castRange", SkillWCastRange);
            AssignFloat(created, "castTime", SkillWCastTime);
            AssignFloat(created, "cooldown", SkillWCooldown);
            AssignFloat(created, "manaCost", SkillWManaCost);
            AssignFloat(created, "effectRadius", SkillWEffectRadius);
            AssignFloat(created, "areaDuration", SkillWAreaDuration);
            AssignFloat(created, "tickInterval", SkillWTickInterval);
            AssignEnum(created, "effectType", (int)SkillEffectType.Slow);
            AssignFloat(created, "slowPercent", SkillWSlowPercent);
            AssignFloat(created, "slowDuration", SkillWSlowDuration);
            AssignString(created, "castVfxId", "SkillW_Cast");
            AssignString(created, "hitVfxId", "SkillW_Tick");
            AssignString(created, "releaseVfxId", "SkillW_Zone");

            EditorUtility.SetDirty(created);
            AssetDatabase.SaveAssets();

            notes.Add(
                $"新建 {HeroSkillWAssetPath}（非指向性 AOE：射程 {SkillWCastRange} / 前摇 {SkillWCastTime}s / CD {SkillWCooldown}s / " +
                $"蓝耗 {SkillWManaCost} / 半径 {SkillWEffectRadius} / 持续 {SkillWAreaDuration}s / 每 {SkillWTickInterval}s 结算 / " +
                $"减速 {SkillWSlowPercent * 100f:F0}% 持续 {SkillWSlowDuration}s）");
            return created;
        }

        /// <summary>
        /// 创建一份空的技能资产（含目录创建）。数值由调用方逐个字段写入。
        /// </summary>
        private static SkillData CreateSkillAsset(string assetPath)
        {
            if (!AssetDatabase.IsValidFolder(ScriptableObjectsFolder))
            {
                Debug.LogError(
                    $"[AutoSceneBuilder] 目录 {ScriptableObjectsFolder} 不存在，无法创建技能资产，" +
                    "英雄将没有任何技能可用（SkillComponent 会就「槽位未配置」告警）。");
                return null;
            }

            if (!AssetDatabase.IsValidFolder(SkillsFolder))
            {
                AssetDatabase.CreateFolder(ScriptableObjectsFolder, "Skills");
            }

            SkillData created = ScriptableObject.CreateInstance<SkillData>();
            AssetDatabase.CreateAsset(created, assetPath);
            return created;
        }

        /// <summary>
        /// 补齐英雄属性资产里阶段六新增的字段（法力 + 技能槽）。
        ///
        /// 【为什么必须有这个方法】EnsureHeroStatsAsset 的策略是"已存在就沿用、不覆盖"——
        /// 而仓库里的 HeroStats.asset 是阶段五生成的，它没有 maxMana / manaRegenPerSecond / skillQ / skillW 这四个字段。
        /// 若不做补缺，一键组装之后英雄会处于"没蓝条、按 Q/W 毫无反应"的状态，而且【不报任何错】——
        /// 这是最难排查的一类问题（表现为"整个技能系统都没生效"）。
        ///
        /// 【为什么是"只补空、不覆盖"而不是整体重写】
        /// 1. 策划可能已经在 Inspector 里调过法力上限（例如为了手感调到 500），覆盖会抹掉调参；
        /// 2. HeroStats.asset 被 HeroPrefab 按 guid 引用，任何"删除重建"都会让引用断链
        ///    （这是本项目在 NavMeshData 上踩过的真实事故）。本方法只改字段值、不碰资产本体，guid 恒定不变。
        /// </summary>
        private static void PatchHeroStatsGaps(
            EntityStatsData heroStats, SkillData skillQ, SkillData skillW, List<string> notes)
        {
            if (heroStats == null)
            {
                return;
            }

            List<string> patched = new List<string>();

            // maxMana <= 0 视为"尚未配置"：0 法力对英雄没有意义（放不出任何技能），判空是安全的。
            if (PatchFloatIfUnset(heroStats, "maxMana", HeroMaxMana))
            {
                patched.Add($"maxMana = {HeroMaxMana}");
            }

            if (PatchFloatIfUnset(heroStats, "manaRegenPerSecond", HeroManaRegenPerSecond))
            {
                patched.Add($"manaRegenPerSecond = {HeroManaRegenPerSecond}");
            }

            if (PatchObjectReferenceIfUnset(heroStats, "skillQ", skillQ))
            {
                patched.Add($"skillQ ← {HeroSkillQAssetPath}");
            }

            if (PatchObjectReferenceIfUnset(heroStats, "skillW", skillW))
            {
                patched.Add($"skillW ← {HeroSkillWAssetPath}");
            }

            if (patched.Count > 0)
            {
                EditorUtility.SetDirty(heroStats);
                AssetDatabase.SaveAssets();
                notes.Add($"{HeroStatsAssetPath} 补写阶段六字段（只补空、不覆盖已有值）：{string.Join("；", patched)}");
            }
        }

        #endregion

        #region 步骤 14：弹道材质与预制体（阶段六）

        /// <summary>
        /// 生成弹道预制体：球体 + Projectile 组件，**不带碰撞体**。
        ///
        /// 每次执行都重新生成（覆盖同名资产）：预制体是派生工件而非策划数据，允许被重建；
        /// SaveAsPrefabAsset 会保留 guid，因此已有引用不会断链（与 HeroPrefab 同一模式）。
        /// </summary>
        /// <returns>弹道预制体；生成失败返回 null（弹道会退回 ProjectileSpawner 的代码兜底球体）。</returns>
        private static GameObject EnsureProjectilePrefab(Material projectileMaterial, List<string> notes)
        {
            if (!AssetDatabase.IsValidFolder(PrefabsFolder))
            {
                AssetDatabase.CreateFolder("Assets", "Prefabs");
            }

            if (!AssetDatabase.IsValidFolder(VfxPrefabsFolder))
            {
                AssetDatabase.CreateFolder(PrefabsFolder, "VFX");
            }

            // 临时对象：只用于产出预制体资产，保存后立刻销毁，因此全程【不登记 Undo】。
            GameObject temp = new GameObject(ProjectilePrefabName);

            GameObject body = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            body.name = "Body";
            body.transform.SetParent(temp.transform, false);

            // 【必须删掉碰撞体】CreatePrimitive 会附带一个 SphereCollider，而本项目的索敌（Physics.OverlapSphere）
            // 与右键拾取（Physics.RaycastNonAlloc）都会命中它：弹道会变成"能被右键点到的候选单位"，
            // 并占用索敌结果里的候选位。弹道的命中判定走自研距离检测（见 Projectile），根本不需要碰撞体。
            SphereCollider bodyCollider = body.GetComponent<SphereCollider>();
            if (bodyCollider != null)
            {
                UnityEngine.Object.DestroyImmediate(bodyCollider);
            }

            // 缩放对齐命中半径（半径 0.5 → 直径 1 米）：让"看起来打到了"和"判定打到了"是同一个尺寸，
            // 避免出现"明明擦着飞过去却掉血"的观感问题。
            float diameter = Mathf.Max(0.1f, SkillQProjectileRadius * 2f);
            body.transform.localScale = new Vector3(diameter, diameter, diameter);

            if (projectileMaterial != null)
            {
                // sharedMaterial 而不是 material：写入资产引用，避免生成材质实例。
                body.GetComponent<MeshRenderer>().sharedMaterial = projectileMaterial;
            }

            temp.AddComponent<Projectile>();

            GameObject saved = PrefabUtility.SaveAsPrefabAsset(temp, ProjectilePrefabPath);

            // 必须销毁临时对象：它从未进入本工具的接管清单，留着会在场景里堆出一个游离的球体。
            UnityEngine.Object.DestroyImmediate(temp);

            if (saved == null)
            {
                Debug.LogError(
                    $"[AutoSceneBuilder] 生成弹道预制体失败（路径 {ProjectilePrefabPath}），" +
                    "指向性弹道技能将退回代码生成的白盒球体。");
                return null;
            }

            AssetDatabase.SaveAssets();
            notes.Add($"{ProjectilePrefabPath} 已生成（球体 + Projectile 组件，已移除碰撞体）");
            return saved;
        }

        #endregion

        #region 步骤 16：收尾校验（技能装配 + Stats Data 注入）

        /// <summary>
        /// 校验英雄实例上的技能系统是否完整。**只提示、不改场景**（与 ValidateNavMeshCoverage 同一约定）。
        ///
        /// 【为什么只能读 GetComponent，不能读 entity.Skills / entity.Mana】
        /// 那些属性是 EntityBase 在 Awake 里缓存的引用，而编辑模式下 Awake 不执行，恒为 null。
        /// 编辑器工具能安全读取的只有"序列化字段"：组件是否挂载、SerializeField 是否已注入、配置资产的公开属性。
        /// </summary>
        private static void ValidateSkillSetup(
            HeroController hero, EntityStatsData heroStats, GameObject projectilePrefab)
        {
            if (hero == null)
            {
                Debug.LogWarning("[AutoSceneBuilder] 玩家英雄不可用，跳过技能装配校验。");
                return;
            }

            GameObject heroObject = hero.gameObject;
            List<string> problems = new List<string>();

            SkillComponent skills = heroObject.GetComponent<SkillComponent>();
            if (skills == null)
            {
                problems.Add("缺少 SkillComponent（英雄无法释放任何技能）");
            }

            if (heroObject.GetComponent<ManaComponent>() == null)
            {
                problems.Add("缺少 ManaComponent（技能无法消耗法力）");
            }

            if (heroObject.GetComponent<BuffComponent>() == null)
            {
                problems.Add("缺少 BuffComponent（减速 / 眩晕 / 护盾无处承载）");
            }

            if (heroObject.GetComponent<ProjectileSpawner>() == null)
            {
                problems.Add("缺少 ProjectileSpawner（指向性弹道无法生成）");
            }

            if (heroObject.GetComponent<PlayerSkillController>() == null)
            {
                problems.Add("缺少 PlayerSkillController（Q / W 按键无人接收）");
            }

            if (skills != null)
            {
                if (skills.GetSkillData(SkillSlot.Q) == null)
                {
                    problems.Add("SkillComponent.skillQ 未注入");
                }

                if (skills.GetSkillData(SkillSlot.W) == null)
                {
                    problems.Add("SkillComponent.skillW 未注入");
                }
            }

            if (projectilePrefab == null)
            {
                problems.Add("弹道预制体缺失（弹道会退回代码生成的白盒球体）");
            }

            // 0 法力意味着所有技能都放不出来，必须显式报出——它不会在运行期报任何错。
            if (heroStats == null)
            {
                problems.Add("英雄属性资产缺失");
            }
            else if (heroStats.MaxMana <= 0f)
            {
                problems.Add("EntityStatsData.maxMana 为 0（英雄没有法力，所有技能都放不出来）");
            }

            if (problems.Count == 0)
            {
                Debug.Log(
                    "[AutoSceneBuilder] 技能装配校验通过：Q / W 配置、法力、状态效果容器、弹道生成器与技能输入层均已就位。");
                return;
            }

            Debug.LogWarning(
                "[AutoSceneBuilder] 技能装配存在以下问题（本次只提示，不改场景）：\n  · " + string.Join("\n  · ", problems));
        }

        /// <summary>
        /// 校验本次组装涉及的所有 EntityBase 是否都拿到了 Stats Data，并给出确定性结论。
        ///
        /// 【为什么必须由工具自己再做一次】EntityBase.OnValidate 里那条"Stats Data 未赋值"的告警，
        /// 会在 AddComponent 的【同一瞬间】被 Unity 同步调用——而工具的顺序必然是"先挂组件、后注入"，
        /// 那一刻 statsData 必然是 null。注入随后确实完成了（apply 之后 OnValidate 会再跑一次，那时不再报），
        /// 但先前那条告警已经打进 Console 了，使用者看到的就是"注入失败"。
        /// 这条假告警比"没有校验"更糟：它会训练人忽略 Console，而本项目整套流程都依赖 Console 暴露配置错误。
        ///
        /// 因此这里改为在收尾阶段【读回序列化字段】做一次确定性校验：
        /// 有问题就点名到具体对象、指出该查哪个资产；没问题就明确说"全部已注入"。
        /// （EntityBase.OnValidate 侧也已改为延迟校验，两条一起才能让 Console 结论无歧义。）
        /// </summary>
        /// <param name="blueBase">蓝方基地。</param>
        /// <param name="redBase">红方基地。</param>
        /// <param name="hero">玩家英雄实例。</param>
        /// <param name="minionPrefab">实际会被生成的小兵预制体（它的 statsData 必须配在【资产】上，运行时才生效）。</param>
        private static void ValidateStatsInjection(
            EntityBase blueBase, EntityBase redBase, HeroController hero, GameObject minionPrefab)
        {
            List<string> problems = new List<string>();

            AppendStatsProblem(problems, BlueBaseName, blueBase);
            AppendStatsProblem(problems, RedBaseName, redBase);

            if (hero == null)
            {
                problems.Add($"{HeroRootName}：英雄实例不存在");
            }
            else
            {
                // HeroController.Entity 读的是序列化字段（工具注入过），因此编辑模式下可用；
                // 不能读 entity.Health / entity.Combat —— 那些是 Awake 里缓存的引用，编辑模式恒为 null。
                AppendStatsProblem(problems, HeroRootName, hero.Entity);
            }

            if (minionPrefab == null)
            {
                problems.Add("小兵预制体不存在（无法校验其 Stats Data）");
            }
            else
            {
                // 小兵是运行时由 MinionSpawner 实例化的，它的 statsData 必须已经配在预制体资产上，
                // 否则每个小兵出生时都会报一条"未配置 EntityStatsData"的错误。
                AppendStatsProblem(problems, minionPrefab.name + "（预制体）", minionPrefab.GetComponent<EntityBase>());
            }

            if (problems.Count == 0)
            {
                Debug.Log(
                    "[AutoSceneBuilder] Stats Data 注入校验通过：本次组装涉及的全部 EntityBase" +
                    "（双方基地 / 英雄 / 小兵预制体）均已正确注入属性资产。");
                return;
            }

            Debug.LogError(
                "[AutoSceneBuilder] Stats Data 注入校验失败（这会导致单位没有生命值，属致命问题）：\n  · " +
                string.Join("\n  · ", problems));
        }

        /// <summary>
        /// 检查单个 EntityBase 的 statsData 是否已注入。用 SerializedObject 读【序列化字段】而不是读属性：
        /// 编辑模式下 Awake 不执行，组件缓存属性一律为 null，只有序列化字段是可信的。
        /// </summary>
        private static void AppendStatsProblem(List<string> problems, string label, EntityBase entity)
        {
            if (entity == null)
            {
                problems.Add($"{label}：对象不存在（可能未创建成功）");
                return;
            }

            SerializedObject serialized = new SerializedObject(entity);
            SerializedProperty property = serialized.FindProperty("statsData");

            // 这里刻意不用 FindProperty 辅助方法：它会直接打一条 LogError，
            // 而本方法要的是"汇总成一条可读的结论"，重复报错只会让 Console 更乱。
            if (property == null)
            {
                problems.Add($"{label}：EntityBase 上找不到 statsData 字段（字段可能已改名）");
                return;
            }

            if (property.objectReferenceValue == null)
            {
                problems.Add($"{label}：Stats Data 未注入");
            }
        }

        #endregion

        /// <summary>
        /// 生成英雄预制体：绿色胶囊体 + 全套运行时组件 + 全部依赖注入。
        ///
        /// 组件清单（缺任何一项都会造成静默失效，清单依据与修复小兵预制体的方法一致）：
        ///   NavMeshAgent（必须先于 MovementComponent，后者带 RequireComponent）
        ///   + EntityBase + HealthComponent + TargetingComponent + CombatComponent + MovementComponent
        ///   + CapsuleCollider（随 CreatePrimitive 一并生成）
        ///   + 阶段六新增：ManaComponent + BuffComponent + SkillComponent + ProjectileSpawner
        ///   + HeroController + PlayerCommandController + PlayerSkillController。
        ///
        /// 【刻意不挂 EntityAIController】：英雄由玩家操控，FSM 会与玩家指令争夺 MovementComponent
        /// 与 CombatComponent 的写入权（详见 HeroController 的类注释）。
        /// 【刻意不挂 TowerController】：英雄是移动单位，塔的直线索敌逻辑与它无关。
        ///
        /// 层级结构：根节点是贴地坐标原点（NavMeshAgent 驱动的就是它），胶囊体作为子节点上抬 1 米，
        /// 这样胶囊体是「站在地面上」而不是像小兵那样被地面切掉一半。
        /// 碰撞体在子节点上不影响任何逻辑——索敌与拾取统一用 GetComponentInParent 向上查找。
        ///
        /// 每次执行都重新生成（覆盖同名资产）：本工具追求「一键组装的结果完全可预期」，
        /// 预制体是派生工件而非策划数据，允许被重建。GUID 会被保留，因此场景里已有的实例不会断链。
        /// </summary>
        private static GameObject CreateHeroPrefab(
            EntityStatsData heroStats,
            AttackData heroAttack,
            Material heroMaterial,
            SkillData heroSkillQ,
            SkillData heroSkillW,
            GameObject projectilePrefab,
            List<string> notes)
        {
            if (!AssetDatabase.IsValidFolder(PrefabsFolder))
            {
                AssetDatabase.CreateFolder("Assets", "Prefabs");
            }

            // 临时对象：只用于产出预制体资产，保存后立刻销毁。
            // 因此全程【不登记 Undo】——登记会让 Ctrl+Z 去操作一个已经销毁的对象。
            GameObject temp = new GameObject(HeroPrefabName);

            GameObject body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.name = HeroBodyName;
            body.transform.SetParent(temp.transform, false);
            body.transform.localPosition = new Vector3(0f, 1f, 0f);

            if (heroMaterial != null)
            {
                // sharedMaterial 而不是 material：写入资产引用，避免生成材质实例。
                body.GetComponent<MeshRenderer>().sharedMaterial = heroMaterial;
            }

            temp.AddComponent<NavMeshAgent>();
            EntityBase entity = temp.AddComponent<EntityBase>();
            temp.AddComponent<HealthComponent>();
            temp.AddComponent<TargetingComponent>();
            CombatComponent combat = temp.AddComponent<CombatComponent>();
            temp.AddComponent<MovementComponent>();

            // ---- 阶段六：技能系统组件 ----
            // ManaComponent：技能蓝耗的唯一数据源。
            temp.AddComponent<ManaComponent>();
            // BuffComponent：减速 / 眩晕 / 护盾的时长与刷新策略（无引用需要注入，运行时自解析同物体组件）。
            temp.AddComponent<BuffComponent>();
            SkillComponent skills = temp.AddComponent<SkillComponent>();
            // ProjectileSpawner：唯一持有弹道预制体的组件（逻辑层不碰表现资源，见该类的说明）。
            ProjectileSpawner projectileSpawner = temp.AddComponent<ProjectileSpawner>();

            HeroController hero = temp.AddComponent<HeroController>();
            PlayerCommandController commandController = temp.AddComponent<PlayerCommandController>();
            // 技能输入层与右键指令层刻意分成两个控制器：各管一条输入通道，互不干扰。
            PlayerSkillController skillController = temp.AddComponent<PlayerSkillController>();

            // ---- 依赖注入：全部由工具完成，用户不需要拖任何引用 ----
            // 身份与配置：运行时由 EntityBase.ApplyStats 转交给各组件（生命/移速/索敌/攻击/法力/技能各一条数据流）。
            AssignEnum(entity, "team", (int)TeamType.Player);
            AssignEnum(entity, "entityType", (int)EntityType.Hero);
            AssignObjectReference(entity, "statsData", heroStats);

            // 组件级兜底：EntityStatsData.Attack 为空时 CombatComponent 会沿用这里直挂的资产，
            // 两处都写上，任何一条数据流被破坏都不会导致「英雄打不出伤害」。
            AssignObjectReference(combat, "attackData", heroAttack);

            // 技能配置的双保险，与普攻的 attackData 完全同一套路：
            // EntityStatsData.SkillQ/SkillW 是主数据流，这里直挂是兜底。
            AssignObjectReference(skills, "skillQ", heroSkillQ);
            AssignObjectReference(skills, "skillW", heroSkillW);

            // 弹道预制体只注入给 ProjectileSpawner —— SkillComponent 拿不到它，
            // 这样"逻辑层不引用表现资源"就是结构上成立的，而不是靠约定。
            AssignObjectReference(projectileSpawner, "projectilePrefab", projectilePrefab);

            // 控制器引用：三个控制器都支持「同物体自解析」，这里显式注入是为了让预制体在 Inspector 里自解释。
            AssignObjectReference(hero, "entity", entity);
            AssignObjectReference(commandController, "controlledEntity", entity);
            AssignObjectReference(skillController, "controlledEntity", entity);
            AssignObjectReference(skillController, "skillComponent", skills);

            string prefabPath = PrefabsFolder + "/" + HeroPrefabName + ".prefab";
            GameObject saved = PrefabUtility.SaveAsPrefabAsset(temp, prefabPath);

            // 必须销毁临时对象：它从未进入本工具的接管清单，留着会在场景里堆出一个游离的重复英雄。
            UnityEngine.Object.DestroyImmediate(temp);

            if (saved == null)
            {
                Debug.LogError($"[AutoSceneBuilder] 生成英雄预制体失败（路径 {prefabPath}），场景中不会出现玩家英雄。");
                return null;
            }

            AssetDatabase.SaveAssets();
            notes.Add($"{prefabPath} 已生成（绿色胶囊体 + 全套组件 + 依赖注入）");
            return saved;
        }

        /// <summary>
        /// 在场景中实例化玩家英雄。
        ///
        /// 用 PrefabUtility.InstantiatePrefab 而不是 Object.Instantiate：场景里留下的必须是「预制体实例」，
        /// 这样日后 HeroPrefab 被重新生成时，场景实例会同步更新，而不是变成一份游离的拷贝。
        ///
        /// 落点会先做 NavMesh 采样：NavMeshAgent 若不在网格上会报「not close enough to the NavMesh」
        /// 并永久无法移动，而这属于「跑起来才发现」的静默失效。这里直接从源头把英雄放到网格表面，
        /// 采样失败（场景未烘焙 NavMesh）时才退回配置坐标，并由 ValidateNavMeshCoverage 统一告警。
        /// </summary>
        /// <returns>实例上的英雄控制器；预制体为空或实例化失败时返回 null。</returns>
        private static HeroController CreateHeroInstance(GameObject heroPrefab, List<string> notes)
        {
            if (heroPrefab == null)
            {
                Debug.LogWarning("[AutoSceneBuilder] 英雄预制体不可用，跳过玩家英雄的实例化。");
                return null;
            }

            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(heroPrefab, SceneManager.GetActiveScene());
            if (instance == null)
            {
                Debug.LogError("[AutoSceneBuilder] 玩家英雄实例化失败。");
                return null;
            }

            // 改名后再登记撤销：物体名要满足本工具「按名字清理」的约定。
            instance.name = HeroRootName;
            Undo.RegisterCreatedObjectUndo(instance, "创建 " + HeroRootName);

            Vector3 spawnPosition = HeroSpawnPosition;
            if (NavMesh.SamplePosition(HeroSpawnPosition, out NavMeshHit navHit, HeroSpawnSampleRadius, NavMesh.AllAreas))
            {
                spawnPosition = navHit.position;
            }
            else
            {
                Debug.LogWarning(
                    $"[AutoSceneBuilder] 英雄出生点{FormatPosition(HeroSpawnPosition)}附近 {HeroSpawnSampleRadius} 米内" +
                    "没有 NavMesh，英雄将按原始坐标放置，运行时会因「不在 NavMesh 上」而无法移动。" +
                    "请先烘焙 NavMesh 后重新组装。");
            }

            instance.transform.position = spawnPosition;

            HeroController hero = instance.GetComponent<HeroController>();
            if (hero == null)
            {
                // 理论上不可能（预制体由本工具生成），只做防御性兜底。
                Debug.LogError($"[AutoSceneBuilder] 玩家英雄实例上找不到 HeroController，英雄数据封装与死亡收尾将不可用。");
            }

            notes.Add($"{HeroRootName}{FormatPosition(spawnPosition)}（英雄实例，已登记进清理清单）");
            return hero;
        }

        /// <summary>
        /// 把主相机接上跟随逻辑并锁定英雄。
        ///
        /// 顺带做一次「立即吸附」：编辑器里不会执行 Start，若不主动吸附，
        /// 场景视图会一直停在相机的旧位置，用户无法当场确认视角是否合理。
        /// </summary>
        /// <returns>配置好的相机控制器；场景里没有相机时返回 null。</returns>
        private static CameraController SetupCameraController(HeroController hero, List<string> notes)
        {
            if (hero == null)
            {
                Debug.LogWarning("[AutoSceneBuilder] 玩家英雄不可用，跳过相机跟随的绑定。");
                return null;
            }

            Camera mainCamera = FindMainCamera();
            if (mainCamera == null)
            {
                Debug.LogWarning("[AutoSceneBuilder] 场景中找不到任何相机，已跳过相机跟随配置。请先创建主相机后重新组装。");
                return null;
            }

            // 已有控制器就复用（幂等），不重复挂载。
            CameraController controller = mainCamera.GetComponent<CameraController>();
            if (controller == null)
            {
                controller = Undo.AddComponent<CameraController>(mainCamera.gameObject);
            }

            AssignObjectReference(controller, "target", hero.transform);

            // 变换写入必须先进撤销栈：Undo.RecordObject 同时会把该对象标记为已修改，
            // 配合收尾的 MarkSceneDirty 保证「组装结果」能被 Ctrl+S 保存下来。
            Undo.RecordObject(mainCamera.transform, "吸附相机到英雄");
            controller.SnapToTarget();

            notes.Add($"{DescribeHierarchyPath(mainCamera.transform)} 已挂 CameraController，跟随 {hero.name}");

            // 拾取与相机是两条独立的依赖链：相机没有 MainCamera 标签时 PlayerCommandController 会直接禁用输入，
            // 因此这里单独提醒（FindMainCamera 取到 fallback 时已经告警过一次，此处只做补充说明）。
            if (!mainCamera.CompareTag("MainCamera"))
            {
                Debug.LogWarning(
                    $"[AutoSceneBuilder] 相机 {mainCamera.name} 没有 MainCamera 标签，PlayerCommandController 会因 " +
                    "Camera.main 为空而禁用右键输入。请在 Inspector 里把 Tag 设为 MainCamera。");
            }

            return controller;
        }

        /// <summary>
        /// 查找场景主相机：优先取带 MainCamera 标签的，取不到则退而用第一个启用的相机并告警。
        /// 不用 Camera.main：它是运行期接口，在编辑器工具里行为不直观，也拿不到「找不到」的具体原因。
        /// </summary>
        private static Camera FindMainCamera()
        {
            // includeInactive = true：被临时关掉的相机同样需要接上跟随逻辑，否则用户一启用就发现相机不动。
            Camera[] cameras = UnityEngine.Object.FindObjectsOfType<Camera>(true);

            Camera fallback = null;

            for (int i = 0; i < cameras.Length; i++)
            {
                Camera candidate = cameras[i];
                if (candidate == null)
                {
                    continue;
                }

                if (candidate.CompareTag("MainCamera"))
                {
                    return candidate;
                }

                if (fallback == null && candidate.enabled)
                {
                    fallback = candidate;
                }
            }

            if (fallback != null)
            {
                Debug.LogWarning(
                    $"[AutoSceneBuilder] 场景中没有带 MainCamera 标签的相机，已改用 {fallback.name}。" +
                    "注意 PlayerCommandController 依赖 Camera.main（即 MainCamera 标签），请为该相机补上标签。");
            }

            return fallback;
        }

        #endregion

        #region 步骤 7 / 12：重复控制器清理与缺口修补

        /// <summary>
        /// 补齐资产上会导致运行期直接失败的缺口（只在缺口存在时写，绝不覆盖已有配置）。
        ///
        /// 目前只有两处：
        /// 1. MinionSpawnData.minionPrefab 为空 —— MinionSpawner.ValidateConfiguration 会 LogError 并拒绝出兵，
        ///    结果是一条兵都出不来；
        /// 2. EntityStatsData.Attack 为空 —— 这不是错误（CombatComponent 会退回使用自己 Inspector 上直挂的
        ///    AttackData），但既然查到了 AttackData 资产，补上能让「配置 → 组件」这条数据流更自洽。
        /// </summary>
        /// <returns>本次实际写入的描述列表，供日志核对。</returns>
        private static List<string> PatchAssetGaps(
            MinionSpawnData spawnData, GameObject minionPrefab, AttackData attackData)
        {
            List<string> notes = new List<string>();

            if (spawnData != null && spawnData.MinionPrefab == null && minionPrefab != null)
            {
                AssignObjectReference(spawnData, "minionPrefab", minionPrefab);
                EditorUtility.SetDirty(spawnData);
                notes.Add($"{spawnData.name}.minionPrefab ← {minionPrefab.name}");
            }

            // 小兵实际使用的是「预制体上 EntityBase 指向的那份」属性资产，因此直接读预制体，
            // 而不是猜某个名字像 Minion 的资产 —— 数据来源与运行时完全一致。
            EntityStatsData prefabStats = null;
            if (minionPrefab != null)
            {
                EntityBase prefabEntity = minionPrefab.GetComponent<EntityBase>();
                prefabStats = prefabEntity != null ? prefabEntity.StatsData : null;
            }

            if (prefabStats != null && prefabStats.Attack == null && attackData != null)
            {
                AssignObjectReference(prefabStats, "attack", attackData);
                EditorUtility.SetDirty(prefabStats);
                notes.Add($"{prefabStats.name}.attack ← {attackData.name}");
            }

            if (notes.Count > 0)
            {
                AssetDatabase.SaveAssets();
            }

            return notes;
        }

        /// <summary>
        /// 移除场景里除刚创建的那个之外的全部 MatchController。
        ///
        /// 必要性（真实事故）：场景里同时存在两个 MatchController 时，两个都会各自跑一遍
        /// 「等待开局延迟 → 统一启动出兵点」的流程，日志里会出现两行「对局准备中」与两行「对局开始」，
        /// 其中一个的出兵点数组还可能是全 null 的陈旧引用（它引用的对象已被本工具的按名字清理删掉）。
        /// 对局状态（isMatchOver / winnerTeam）被两份实例各自持有、互相覆盖，表现极难排查。
        ///
        /// 为什么只删组件、不删整个 GameObject：承载它的对象上可能还挂着用户自己配的其它东西，
        /// 而"双份对局流程"这个症状只需要组件级删除即可消除。整个操作可撤销。
        /// </summary>
        /// <param name="keep">本次刚创建的控制器，必须保留。</param>
        /// <returns>被移除的控制器所在对象的层级路径，供日志核对。</returns>
        private static List<string> RemoveDuplicateMatchControllers(MatchController keep)
        {
            List<string> removedPaths = new List<string>();

            // includeInactive = true：被关掉的重复对象同样会在启用时开始跑对局流程，不能漏。
            // FindObjectsOfType 只搜已加载的场景，不会命中预制体资产，因此不必担心误伤。
            MatchController[] all = UnityEngine.Object.FindObjectsOfType<MatchController>(true);

            for (int i = 0; i < all.Length; i++)
            {
                MatchController candidate = all[i];

                if (candidate == null || candidate == keep)
                {
                    continue;
                }

                removedPaths.Add(DescribeHierarchyPath(candidate.transform));
                Undo.DestroyObjectImmediate(candidate);
            }

            return removedPaths;
        }

        /// <summary>生成对象的层级路径（如 "Root/Child"），仅用于日志定位。</summary>
        private static string DescribeHierarchyPath(Transform target)
        {
            string path = target.name;
            Transform parent = target.parent;

            while (parent != null)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }

            return path;
        }

        /// <summary>
        /// 小兵预制体完整性修复：缺哪个必需组件就补哪个。
        ///
        /// 必要性（真实事故）：MinionPrefab 上挂着 NavMeshAgent，却没有封装它的 MovementComponent，
        /// 也没有 TargetingComponent —— 两者都不会让编译失败，症状分别是：
        /// 1. 缺 MovementComponent：MoveState 每帧报「没有 MovementComponent，无法沿路线推进」，
        ///    小兵原地不动（EntityBase.ValidateDependencies 也会报错）；
        /// 2. 缺 TargetingComponent：**完全不报任何错**（ValidateDependencies 不检查它），
        ///    但 TryDetectEnemy 会因取不到索敌组件而永远返回 false，小兵全程"看不见"敌人。
        ///    这类静默失效只能靠主动校验发现，因此这里按清单逐项检查。
        /// 3. 阶段六补记：缺 BuffComponent 时，减速 / 眩晕 / 护盾全都无处落地。
        ///    SkillEffectResolver 会打一条「…上找不到 BuffComponent，减速效果未生效」后放弃，
        ///    表现为"技能放出去了、圈也画了、小兵却一点没慢"——同样是静默失效的一类。
        ///    **注意：BuffComponent 不是英雄专属能力**，任何需要被控制的单位都得挂它。
        ///
        /// 为什么走 PrefabUtility.LoadPrefabContents 而不是直接改文件：
        /// 预制体资产的脚本引用由引擎自己维护，手写 YAML 需要正确编码 guid（本项目的 meta guid 并非
        /// 标准 32 位十六进制），极易写坏；而 PrefabUtility 是官方支持的资产编辑通道。
        /// 用 SaveAsPrefabAsset 写回同一路径会保留 guid，因此 MinionSpawnData 对预制体的引用不会断链。
        ///
        /// 刻意先做只读检查再决定是否重写：预制体完整时不碰文件，避免产生无意义的改动。
        /// </summary>
        /// <returns>本次实际补齐的描述列表，供日志核对。</returns>
        private static List<string> RepairPrefabComponents(GameObject prefab)
        {
            List<string> notes = new List<string>();

            if (prefab == null)
            {
                return notes;
            }

            string prefabPath = AssetDatabase.GetAssetPath(prefab);
            if (string.IsNullOrEmpty(prefabPath) ||
                !prefabPath.EndsWith(".prefab", System.StringComparison.OrdinalIgnoreCase))
            {
                Debug.LogWarning(
                    $"[AutoSceneBuilder] {prefab.name} 不是预制体资产（路径「{prefabPath}」），" +
                    "跳过组件完整性修复。");
                return notes;
            }

            // 只读检查：列出缺失项。清单依据项目记忆「小兵预制体需挂 EntityBase + EntityAIController
            // + Health + Targeting + Combat + Movement + Collider」，阶段六起追加 BuffComponent。
            List<string> missing = new List<string>();

            if (prefab.GetComponent<EntityBase>() == null) missing.Add("EntityBase");
            if (prefab.GetComponent<HealthComponent>() == null) missing.Add("HealthComponent");
            if (prefab.GetComponent<CombatComponent>() == null) missing.Add("CombatComponent");
            if (prefab.GetComponent<TargetingComponent>() == null) missing.Add("TargetingComponent");
            if (prefab.GetComponent<NavMeshAgent>() == null) missing.Add("NavMeshAgent");
            if (prefab.GetComponent<MovementComponent>() == null) missing.Add("MovementComponent");
            if (prefab.GetComponent<EntityAIController>() == null) missing.Add("EntityAIController");

            // 阶段六新增：BuffComponent 是【状态效果的承载者】，不是"英雄专属能力"。
            // 缺了它，减速 / 眩晕 / 护盾全都无处落地——SkillEffectResolver 会打一条
            // 「MinionPrefab(Clone) 上找不到 BuffComponent，减速效果未生效」然后静默放弃，
            // 表现为"技能放出去了、圈也画了、小兵却一点没慢"，且不报任何错误。
            if (prefab.GetComponent<BuffComponent>() == null) missing.Add("BuffComponent");

            if (prefab.GetComponentInChildren<Collider>() == null) missing.Add("Collider");

            if (missing.Count == 0)
            {
                return notes;
            }

            Debug.LogWarning(
                $"[AutoSceneBuilder] 小兵预制体 {prefabPath} 缺少必需组件：{string.Join("、", missing)}，正在自动补齐。");

            GameObject contents = PrefabUtility.LoadPrefabContents(prefabPath);

            try
            {
                // NavMeshAgent 先于 MovementComponent：后者带 [RequireComponent(typeof(NavMeshAgent))]，
                // 顺序反了引擎会自动补一个，虽然结果相同，但显式写出来意图更清楚。
                if (contents.GetComponent<NavMeshAgent>() == null) contents.AddComponent<NavMeshAgent>();
                if (contents.GetComponent<EntityBase>() == null) contents.AddComponent<EntityBase>();
                if (contents.GetComponent<HealthComponent>() == null) contents.AddComponent<HealthComponent>();
                if (contents.GetComponent<CombatComponent>() == null) contents.AddComponent<CombatComponent>();
                if (contents.GetComponent<TargetingComponent>() == null) contents.AddComponent<TargetingComponent>();
                if (contents.GetComponent<MovementComponent>() == null) contents.AddComponent<MovementComponent>();
                if (contents.GetComponent<EntityAIController>() == null) contents.AddComponent<EntityAIController>();

                // 阶段六：小兵必须能吃控制。BuffComponent 无引用需要注入（运行时自解析同物体的
                // Movement / Combat / Health），因此挂上即可生效。
                if (contents.GetComponent<BuffComponent>() == null) contents.AddComponent<BuffComponent>();

                // 碰撞体：索敌走 Physics.OverlapSphere + GetComponentInParent<ITargetable>，
                // 没有碰撞体的单位永远不会出现在索敌结果里，且不报任何异常。
                if (contents.GetComponentInChildren<Collider>() == null) contents.AddComponent<CapsuleCollider>();

                PrefabUtility.SaveAsPrefabAsset(contents, prefabPath);
                AssetDatabase.SaveAssets();

                notes.Add($"{prefab.name} 补齐组件：{string.Join("、", missing)}");
            }
            catch (System.Exception exception)
            {
                // 补组件失败不能让整次组装中止：其它对象已经建好了，先如实报出问题即可。
                Debug.LogError(
                    $"[AutoSceneBuilder] 修复小兵预制体 {prefabPath} 时出错，该预制体可能仍不完整：" +
                    $"{exception.Message}");
            }
            finally
            {
                // 必须卸载：LoadPrefabContents 会在一个临时场景里实例化预制体内容，不卸载会残留。
                PrefabUtility.UnloadPrefabContents(contents);
            }

            return notes;
        }

        #endregion

        #region 字段写入与收尾

        /// <summary>写入对象引用型序列化字段。</summary>
        private static bool AssignObjectReference(
            UnityEngine.Object target, string fieldName, UnityEngine.Object value)
        {
            if (target == null)
            {
                return false;
            }

            SerializedObject serialized = new SerializedObject(target);
            SerializedProperty property = FindProperty(serialized, fieldName);
            if (property == null)
            {
                return false;
            }

            if (property.propertyType != SerializedPropertyType.ObjectReference)
            {
                Debug.LogError(
                    $"[AutoSceneBuilder] {target.GetType().Name}.{fieldName} 不是对象引用字段，注入失败。");
                return false;
            }

            property.objectReferenceValue = value;
            serialized.ApplyModifiedProperties();
            return true;
        }

        /// <summary>
        /// 写入浮点型序列化字段（英雄的属性资产就是用这个注入数值的）。
        /// 与 AssignObjectReference 一样，字段名取不到时立刻报错而不是静默失败。
        /// </summary>
        private static bool AssignFloat(UnityEngine.Object target, string fieldName, float value)
        {
            if (target == null)
            {
                return false;
            }

            SerializedObject serialized = new SerializedObject(target);
            SerializedProperty property = FindProperty(serialized, fieldName);
            if (property == null)
            {
                return false;
            }

            if (property.propertyType != SerializedPropertyType.Float)
            {
                Debug.LogError(
                    $"[AutoSceneBuilder] {target.GetType().Name}.{fieldName} 不是浮点字段，注入失败。");
                return false;
            }

            property.floatValue = value;
            serialized.ApplyModifiedProperties();
            return true;
        }

        /// <summary>
        /// 写入枚举型序列化字段。
        /// 写 intValue 而不是 enumValueIndex：枚举在序列化层面就是一个 int，intValue 直接对应枚举值本身；
        /// 而 enumValueIndex 是「枚举下拉列表里的序号」，只有在枚举值从 0 连续编号时才恰好相等，
        /// 一旦日后插入带显式值的成员就会写错。
        /// </summary>
        private static bool AssignEnum(UnityEngine.Object target, string fieldName, int enumValue)
        {
            if (target == null)
            {
                return false;
            }

            SerializedObject serialized = new SerializedObject(target);
            SerializedProperty property = FindProperty(serialized, fieldName);
            if (property == null)
            {
                return false;
            }

            property.intValue = enumValue;
            serialized.ApplyModifiedProperties();
            return true;
        }

        /// <summary>
        /// 写入字符串型序列化字段（技能的表现 ID 槽位用这个注入）。
        /// 与 AssignFloat 同一约定：字段名取不到时立刻报错，而不是静默失败。
        /// </summary>
        private static bool AssignString(UnityEngine.Object target, string fieldName, string value)
        {
            if (target == null)
            {
                return false;
            }

            SerializedObject serialized = new SerializedObject(target);
            SerializedProperty property = FindProperty(serialized, fieldName);
            if (property == null)
            {
                return false;
            }

            if (property.propertyType != SerializedPropertyType.String)
            {
                Debug.LogError(
                    $"[AutoSceneBuilder] {target.GetType().Name}.{fieldName} 不是字符串字段，注入失败。");
                return false;
            }

            property.stringValue = value;
            serialized.ApplyModifiedProperties();
            return true;
        }

        /// <summary>
        /// 写入整型序列化字段（技能的命中数上限等用这个注入）。
        /// 不能用 AssignFloat 代替：SerializedProperty 的类型不匹配时会写入失败且不报错。
        /// </summary>
        private static bool AssignInt(UnityEngine.Object target, string fieldName, int value)
        {
            if (target == null)
            {
                return false;
            }

            SerializedObject serialized = new SerializedObject(target);
            SerializedProperty property = FindProperty(serialized, fieldName);
            if (property == null)
            {
                return false;
            }

            if (property.propertyType != SerializedPropertyType.Integer)
            {
                Debug.LogError(
                    $"[AutoSceneBuilder] {target.GetType().Name}.{fieldName} 不是整型字段，注入失败。");
                return false;
            }

            property.intValue = value;
            serialized.ApplyModifiedProperties();
            return true;
        }

        /// <summary>
        /// 只在字段"尚未配置"（值 &lt;= 0）时写入浮点数，已有值一律不动。
        ///
        /// 这是"只补缺不覆盖"原则的实现体：阶段六给 EntityStatsData 新增了 maxMana / manaRegenPerSecond，
        /// 而仓库里已有的 HeroStats.asset 没有这两个字段（反序列化后为 0）。
        /// 若直接覆盖，策划调过的数值会被每次一键组装抹掉。
        /// </summary>
        private static bool PatchFloatIfUnset(UnityEngine.Object target, string fieldName, float value)
        {
            if (target == null)
            {
                return false;
            }

            SerializedObject serialized = new SerializedObject(target);
            SerializedProperty property = FindProperty(serialized, fieldName);
            if (property == null)
            {
                return false;
            }

            if (property.propertyType != SerializedPropertyType.Float)
            {
                Debug.LogError(
                    $"[AutoSceneBuilder] {target.GetType().Name}.{fieldName} 不是浮点字段，补缺失败。");
                return false;
            }

            if (property.floatValue > 0f)
            {
                // 已有值 → 不动。这就是"只补缺不覆盖"。
                return false;
            }

            property.floatValue = value;
            serialized.ApplyModifiedProperties();
            return true;
        }

        /// <summary>
        /// 只在引用字段为空时写入对象引用，已有引用一律不动。语义与 PatchFloatIfUnset 完全对称。
        /// </summary>
        private static bool PatchObjectReferenceIfUnset(
            UnityEngine.Object target, string fieldName, UnityEngine.Object value)
        {
            if (target == null || value == null)
            {
                return false;
            }

            SerializedObject serialized = new SerializedObject(target);
            SerializedProperty property = FindProperty(serialized, fieldName);
            if (property == null)
            {
                return false;
            }

            if (property.propertyType != SerializedPropertyType.ObjectReference)
            {
                Debug.LogError(
                    $"[AutoSceneBuilder] {target.GetType().Name}.{fieldName} 不是对象引用字段，补缺失败。");
                return false;
            }

            if (property.objectReferenceValue != null)
            {
                return false;
            }

            property.objectReferenceValue = value;
            serialized.ApplyModifiedProperties();
            return true;
        }

        /// <summary>写入对象引用数组（或 List）字段，元素按顺序覆盖，多余槽位会被裁掉。</summary>
        private static bool AssignObjectArray(
            UnityEngine.Object target, string fieldName, UnityEngine.Object[] values)
        {
            if (target == null)
            {
                return false;
            }

            SerializedObject serialized = new SerializedObject(target);
            SerializedProperty property = FindProperty(serialized, fieldName);
            if (property == null)
            {
                return false;
            }

            if (!property.isArray)
            {
                Debug.LogError(
                    $"[AutoSceneBuilder] {target.GetType().Name}.{fieldName} 不是数组或 List 字段，注入失败。");
                return false;
            }

            property.arraySize = values != null ? values.Length : 0;
            for (int i = 0; i < property.arraySize; i++)
            {
                property.GetArrayElementAtIndex(i).objectReferenceValue = values[i];
            }

            serialized.ApplyModifiedProperties();
            return true;
        }

        /// <summary>
        /// 取序列化字段；取不到就报错。
        /// 单独抽出来的原因：字段名是字符串，改名后编译期毫无提示，这里是唯一能拦住「静默失效」的地方。
        /// </summary>
        private static SerializedProperty FindProperty(SerializedObject serialized, string fieldName)
        {
            SerializedProperty property = serialized.FindProperty(fieldName);
            if (property == null)
            {
                Debug.LogError(
                    $"[AutoSceneBuilder] 在 {serialized.targetObject.GetType().Name} 上找不到序列化字段「{fieldName}」，" +
                    "注入失败（字段可能已被改名），请同步修改本工具的字段名常量。");
            }

            return property;
        }

        /// <summary>
        /// 输出组装结果。
        /// 阵营 / 类型 / 属性资产三项是从刚创建的对象上【读回】的（而不是照抄上面传入的参数），
        /// 因此这段日志同时充当「注入是否真的生效」的自检：字段被改名或写入失败时会直接显示为异常值。
        /// 注意只读 Team / EntityType / StatsData 这类序列化字段，
        /// 不读 Health / Combat 等属性 —— 那些是 EntityBase 在 Awake 里缓存的引用，编辑模式下尚未缓存，必然为 null。
        /// </summary>
        private static void LogSummary(
            List<string> removedNames,
            List<string> removedControllers,
            EntityBase blueBase,
            EntityBase redBase,
            MinionSpawner blueSpawner,
            MinionSpawner redSpawner,
            List<string> assetNotes,
            List<string> prefabNotes,
            HeroController hero,
            CameraController cameraController,
            List<string> heroNotes,
            List<string> groundNotes,
            List<string> baseNotes,
            List<string> uiNotes)
        {
            string removedText = removedNames.Count > 0 ? string.Join("、", removedNames) : "（无）";
            string assetText = assetNotes.Count > 0 ? string.Join("；", assetNotes) : "（无缺口）";
            string prefabText = prefabNotes.Count > 0 ? string.Join("；", prefabNotes) : "（组件完整，未改动）";
            string controllerText = removedControllers.Count > 0
                ? string.Join("、", removedControllers)
                : "（无重复）";
            string heroText = heroNotes.Count > 0 ? string.Join("；", heroNotes) : "（未生成）";
            string groundText = groundNotes.Count > 0 ? string.Join("；", groundNotes) : "（无动作）";
            string baseText = baseNotes.Count > 0 ? string.Join("；", baseNotes) : "（无动作）";
            string uiText = uiNotes.Count > 0 ? string.Join("；", uiNotes) : "（无动作）";
            string cameraText = DescribeCamera(cameraController, hero);

            Debug.Log(
                "[AutoSceneBuilder] 测试战场组装完成。\n" +
                $"  · 清理旧对象：{removedText}\n" +
                $"  · 清理多余 MatchController：{controllerText}\n" +
                $"  · 兵线：{MidLaneName} → {Waypoint1Name}{FormatPosition(Waypoint1Position)}" +
                $" → {Waypoint2Name}{FormatPosition(Waypoint2Position)}\n" +
                $"  · 蓝方：{DescribeBase(BlueBaseName, blueBase)}{FormatPosition(BlueBasePosition)}；" +
                $"{BlueSpawnerName}{FormatPosition(BlueSpawnerPosition)}（{DescribeSpawner(blueSpawner)}）\n" +
                $"  · 红方：{DescribeBase(RedBaseName, redBase)}{FormatPosition(RedBasePosition)}；" +
                $"{RedSpawnerName}{FormatPosition(RedSpawnerPosition)}（{DescribeSpawner(redSpawner)}）\n" +
                $"  · 对局编排：{BattlefieldName} 挂 MatchController，已登记 2 个出兵点\n" +
                $"  · 基地外观与属性：{baseText}\n" +
                $"  · 地面与导航：{groundText}\n" +
                $"  · 玩家英雄：{DescribeHero(hero)}\n" +
                $"  · 英雄装配：{heroText}\n" +
                $"  · 相机跟随：{cameraText}\n" +
                $"  · UI 与可视化（阶段七）：{uiText}\n" +
                $"  · 资产补写：{assetText}\n" +
                $"  · 小兵预制体：{prefabText}\n" +
                "  · 提醒：双方 Spawner 共用同一条 MidLane，红方会沿与蓝方相同的节点顺序推进" +
                "（先走向 Waypoint1，再折返 Waypoint2）。若要红方严格反向推进，需为红方另建一条节点顺序相反的 LanePath。\n" +
                "  · 提醒：英雄【不挂】EntityAIController——FSM 会与玩家指令争夺移动与攻击的写入权，" +
                "英雄的行为完全由右键指令驱动（点地面=移动，点敌方单位=攻击）。\n" +
                "  · 提醒：相机操作为「右键=指令，中键拖拽=平移，中键双击=回中，滚轮=缩放」，" +
                "与英雄的右键指令互不冲突。\n" +
                "  · 技能操作：Q = 指向性火球（优先锁定鼠标下的敌方单位，其次用已锁定的普攻目标）；" +
                "W = 非指向性减速圈（落在鼠标位置）。V1 采用智能施法：按下即释放，无二次确认。\n" +
                "  · 技能机制：前摇期间英雄被锁住移动与攻击（逻辑计时，与动画长度无关）；" +
                "眩晕与施法前摇共用同一把锁，两个持有者都放开才会真正解锁。\n" +
                "  · UI 与可视化：头顶血条与伤害飘字由 WorldUIRoot 下的管理器统一挂载（单位预制体零改动）；" +
                "左下角是玩家 HUD（头像/血条/蓝条/Q-W 技能槽 + CD 径向遮罩），顶部是计分板与击杀播报，" +
                "英雄阵亡后会出现复活倒计时遮罩（时长取自 MatchConfigData.RespawnTime）。\n" +
                "  · 提醒：步骤 8 已确保地面覆盖战场并烘焙 NavMesh；若仍有落点告警，" +
                "说明地面是 Terrain/自定义网格或 NavMeshSettings 被人工调整过，需手动处理。\n" +
                "  · 场景已标记为已修改，请按 Ctrl+S 保存后再进入播放模式。");
        }

        /// <summary>拼装相机的读回描述。</summary>
        private static string DescribeCamera(CameraController controller, HeroController hero)
        {
            if (controller == null)
            {
                return "未绑定（场景中缺少可用相机）";
            }

            string targetName = hero != null ? hero.name : "（目标为空）";
            return $"{controller.name} 跟随 {targetName}，偏移 {controller.Offset}";
        }

        /// <summary>
        /// 拼装英雄的读回描述。
        /// 与 DescribeBase 同一取舍：只读序列化字段（Team / EntityType / StatsData），
        /// 不读 Health / Combat 等属性——那些是 EntityBase 在 Awake 里缓存的引用，编辑模式下尚未缓存，必然为 null。
        /// </summary>
        private static string DescribeHero(HeroController hero)
        {
            if (hero == null)
            {
                return "（创建失败）";
            }

            EntityBase entity = hero.Entity;
            if (entity == null)
            {
                return $"{hero.name}（缺少 EntityBase）";
            }

            string statsName = entity.StatsData != null ? entity.StatsData.name : "未指定";
            return $"{hero.name}（阵营 {entity.Team}，类型 {entity.EntityType}，属性资产 {statsName}，" +
                   $"位置 {FormatPosition(hero.transform.position)}）";
        }

        /// <summary>拼装基地的读回描述。</summary>
        private static string DescribeBase(string objectName, EntityBase entity)
        {
            if (entity == null)
            {
                return $"{objectName}（创建失败）";
            }

            string statsName = entity.StatsData != null ? entity.StatsData.name : "未指定";
            return $"{objectName}（阵营 {entity.Team}，类型 {entity.EntityType}，属性资产 {statsName}）";
        }

        /// <summary>拼装出兵点的读回描述。MinionSpawner 只公开了 Team，其余槽位不暴露读取接口。</summary>
        private static string DescribeSpawner(MinionSpawner spawner)
        {
            return spawner != null ? $"阵营 {spawner.Team}" : "创建失败";
        }

        /// <summary>把坐标格式化成 (x, y, z)，仅用于日志。</summary>
        private static string FormatPosition(Vector3 position)
        {
            return $"({position.x}, {position.y}, {position.z})";
        }

        /// <summary>
        /// 场景依赖检查（两级）：
        /// 1. 场景有没有烘焙 NavMesh；
        /// 2. 关键落点（双方出生点、路径点）是否真的落在 NavMesh 覆盖范围内。
        ///
        /// 为什么第 2 级必须单独做：出生点写对了坐标、NavMesh 也确实烘焙过，两者仍可能不重叠
        /// ——只要地面网格比战场坐标范围小（出厂 MainScene 的 Plane 只有 10×10 且中心偏离原点，
        /// 而出生点在 x = -8 ~ -12）。这种配置下 NavMeshAgent 会报「离 NavMesh 太远」，
        /// 小兵永久卡在出生点，而 Console 里看不出是"坐标"还是"地面"的问题。
        ///
        /// 这里只做【校验】：实际的地面放大与 NavMesh 烘焙已经在步骤 8 完成，
        /// 因此走到这里还报「落点不在 NavMesh 上」，说明地面形态超出了步骤 8 能自动处理的范围
        /// （Terrain / 自定义网格 / 手工调过的 NavMeshSettings），需要人工介入。
        /// </summary>
        private static void ValidateNavMeshCoverage()
        {
            // 关键落点清单由 GetKeyPoints 统一提供（地面放大、本校验、地面覆盖校验共用同一份，
            // 避免「改了阵型坐标只改一处」导致校验与生成结果对不上）。
            Vector3[] points = GetKeyPoints();
            string[] labels = GetKeyPointLabels();

            // 地面覆盖检查放在最前面：它解释的是「为什么这些点不在 NavMesh 上」，
            // 先给出因，再给出果，排查时不会只看到一串落点坐标而无从下手。
            ValidateGroundCoverage(points, labels);

            NavMeshTriangulation triangulation = NavMesh.CalculateTriangulation();

            if (triangulation.vertices == null || triangulation.vertices.Length == 0)
            {
                Debug.LogWarning(
                    "[AutoSceneBuilder] 当前场景未检测到已烘焙的 NavMesh，小兵与英雄都将无法移动。" +
                    "步骤 8 已用 NavMeshBuilder 自动烘焙，仍未成功时请检查：地面是否带 MeshRenderer 且在 Ground 层、" +
                    "NavMeshSettings 的 Agent 参数（半径 / 高度 / 坡度）是否与地面尺寸匹配。");
                return;
            }

            List<string> offMesh = new List<string>();

            for (int i = 0; i < points.Length; i++)
            {
                // 采样半径取 1 米，比 NavMeshAgent 自身的吸附范围宽松：采样不到就一定离网格很远，
                // 因此这条告警几乎不会有假阳性。
                if (!NavMesh.SamplePosition(points[i], out NavMeshHit _, 1f, NavMesh.AllAreas))
                {
                    offMesh.Add($"{labels[i]}{FormatPosition(points[i])}");
                }
            }

            if (offMesh.Count > 0)
            {
                Debug.LogWarning(
                    "[AutoSceneBuilder] 以下落点不在已烘焙的 NavMesh 覆盖范围内：" + string.Join("、", offMesh) + "。" +
                    "对应阵营的小兵会报「离 NavMesh 太远」并永久卡在出生点，英雄同理。" +
                    "请放大地面网格或调整阵型坐标，然后重新烘焙 NavMesh。");
            }
        }

        /// <summary>
        /// 地面覆盖检查（两级）：
        /// 1. Ground 层上到底有没有碰撞体——没有的话右键点地面不会有任何反应（射线靠碰撞体命中），
        ///    而且这个问题在 Console 里完全没有报错，只能靠主动校验发现；
        /// 2. 关键落点是否落在这些碰撞体的水平覆盖范围内——它直接解释了「为什么 NavMesh 采样不到」。
        ///
        /// 为什么用 Collider.bounds 而不是 Renderer.bounds：
        /// 碰撞体才是射线拾取与 NavMesh 烘焙真正依赖的东西，且 Terrain 这类地面没有 MeshRenderer，
        /// 用 Renderer 会漏判。只比较 x/z 水平范围：地面碰撞体往往很薄或很厚，y 区间不具备可比性。
        ///
        /// 与 ValidateNavMeshCoverage 一样：只提示、不自动改地面尺寸、不自动烘焙。
        /// 地面尺寸与阵型坐标属于关卡决策，工具不该替用户拍板。
        /// </summary>
        private static void ValidateGroundCoverage(Vector3[] points, string[] labels)
        {
            int groundLayer = LayerMask.NameToLayer(GroundLayerName);

            if (groundLayer < 0)
            {
                Debug.LogError(
                    $"[AutoSceneBuilder] 项目里没有名为 \"{GroundLayerName}\" 的 Layer。" +
                    "请到 Edit > Project Settings > Tags and Layers 中新增该 Layer 并分配给地面对象，" +
                    "否则玩家右键点地面不会有任何反应（PlayerCommandController 会直接禁用输入）。");
                return;
            }

            Collider[] colliders = UnityEngine.Object.FindObjectsOfType<Collider>(true);

            bool hasGroundBounds = false;
            Bounds groundBounds = new Bounds(Vector3.zero, Vector3.zero);

            for (int i = 0; i < colliders.Length; i++)
            {
                Collider collider = colliders[i];

                if (collider == null || collider.gameObject.layer != groundLayer)
                {
                    continue;
                }

                if (!hasGroundBounds)
                {
                    groundBounds = collider.bounds;
                    hasGroundBounds = true;
                }
                else
                {
                    groundBounds.Encapsulate(collider.bounds);
                }
            }

            if (!hasGroundBounds)
            {
                Debug.LogError(
                    $"[AutoSceneBuilder] {GroundLayerName} 层上没有任何碰撞体，玩家右键点地面不会有任何反应" +
                    "（射线拾取依赖碰撞体，而 NavMesh 也无法从没有碰撞体的地面烘焙出来）。" +
                    "请给地面对象补上碰撞体并确认它位于该 Layer。");
                return;
            }

            List<string> outside = new List<string>();

            for (int i = 0; i < points.Length; i++)
            {
                Vector3 point = points[i];

                if (point.x < groundBounds.min.x || point.x > groundBounds.max.x ||
                    point.z < groundBounds.min.z || point.z > groundBounds.max.z)
                {
                    outside.Add($"{labels[i]}{FormatPosition(point)}");
                }
            }

            if (outside.Count > 0)
            {
                Debug.LogWarning(
                    "[AutoSceneBuilder] 以下落点超出了 Ground 层碰撞体的水平覆盖范围：" +
                    string.Join("、", outside) + "。\n" +
                    $"  · 地面水平范围：x ∈ [{groundBounds.min.x:F1}, {groundBounds.max.x:F1}]，" +
                    $"z ∈ [{groundBounds.min.z:F1}, {groundBounds.max.z:F1}]\n" +
                    "  · 后果：这些坐标既点不到地面（右键无效），也无法成为 NavMesh 的可达区域" +
                    "（NavMeshAgent 会报「not close enough to the NavMesh」并永久无法移动）。\n" +
                    "  · 修法：放大地面网格（Plane 默认只有 10×10 单位）或调整阵型坐标，然后重新烘焙 NavMesh。");
            }
        }

        #endregion
    }
}
