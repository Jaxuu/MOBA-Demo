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
using MOBA.VFX;

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
    ///    场景实例化、相机跟随绑定，全部由本工具生成并注入（见步骤 9~11）。
    ///    阶段八把「1 个英雄」扩展为「10 英雄小队」（蓝 5 / 红 5）：玩家英雄是蓝方下标 0 的那一个，
    ///    其余 9 个是 AI 英雄（挂 EntityAIController + HeroAIController、禁用玩家输入控制器、
    ///    关闭尸体销毁）。6 座防御塔（含塔弹道与仇恨配置）的装配同样在这里完成 ——
    ///    塔与英雄的装配实现拆在 AutoSceneBuilder.Stage8Assembly.cs（partial 分册）；
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
        /// 文件名刻意以 Hero 开头：这样它会被 IsDedicatedAsset 挡在通用资产查找之外，
        /// 不会污染「按类型找候选 → 名称命中关键词优先」这套选择逻辑。
        /// </summary>
        private const string HeroSkillQAssetPath = SkillsFolder + "/HeroSkillQ.asset";

        /// <summary>W 技能配置资产路径（非指向性 AOE 减速圈）。命名规则同 Q。</summary>
        private const string HeroSkillWAssetPath = SkillsFolder + "/HeroSkillW.asset";

        /// <summary>E 技能配置资产路径（阶段八：自身施法 + 跟随自身的持续 AOE）。命名规则同 Q。</summary>
        private const string HeroSkillEAssetPath = SkillsFolder + "/HeroSkillE.asset";

        /// <summary>R 技能配置资产路径（阶段八：指向性斩杀）。命名规则同 Q。</summary>
        private const string HeroSkillRAssetPath = SkillsFolder + "/HeroSkillR.asset";

        /// <summary>
        /// 技能池目录（阶段八第二步）。池内是"与玩家 QWER 无关的一批基础技能"，
        /// 由本工具按固定 seed 分配给 9 个 AI 英雄。
        ///
        /// 【为什么单独一层目录】池资产的名字一律以 Pool 开头，与 Hero* 的专用资产在物理上分开，
        /// 一眼就能看出"这些是给 AI 用的通用技能"；日后要做"AI 难度分档"时也只需换一个目录。
        /// </summary>
        private const string SkillPoolFolder = SkillsFolder + "/Pool";

        /// <summary>
        /// 技能池分配的固定随机种子。
        ///
        /// 【为什么必须是常量】AutoSceneBuilder 的铁律是幂等：重复执行必须得到完全相同的结果。
        /// 用 UnityEngine.Random（全局状态、受调用顺序影响）会让"再点一次菜单"就换一套技能分配，
        /// 于是"AI 行为变了"到底是代码改了还是随机到了，永远无法判断。
        /// 用 System.Random + 固定种子，分配结果就变成了纯函数：输入（池子内容 + 英雄序号）→ 输出（4 个下标）。
        ///
        /// 取值 2026 由架构师指定（"固定随机种子，如 new Random(2026)"）。
        /// 每个 AI 英雄实际用的是 `new System.Random(2026 + 序号 × SkillPoolSeedStride)` ——
        /// 仍然是"固定种子 + 完全可复现"，但比 9 个英雄共用一个顺序 Random 更好：
        /// 共用时"谁抽到什么"取决于抽签顺序，加一个英雄就会让所有人的分配整体错位（见 RollAiSkillSlots 的说明）。
        /// </summary>
        private const int SkillPoolRandomSeed = 2026;

        /// <summary>
        /// 派生每个 AI 英雄种子时的步长。取一个与池大小（10）互质的大质数，
        /// 保证 9 个英雄的派生种子彼此远离，抽出的组合不会因为"种子只差 1"而高度雷同。
        /// </summary>
        private const int SkillPoolSeedStride = 7919;

        /// <summary>弹道预制体目录。</summary>
        private const string VfxPrefabsFolder = PrefabsFolder + "/VFX";

        /// <summary>弹道预制体资产名。</summary>
        private const string ProjectilePrefabName = "Projectile";

        /// <summary>弹道预制体资产路径。</summary>
        private const string ProjectilePrefabPath = VfxPrefabsFolder + "/" + ProjectilePrefabName + ".prefab";

        /// <summary>弹道材质路径。</summary>
        private const string ProjectileMaterialPath = MaterialsFolder + "/ProjectileOrange.mat";

        // ---- 固定对象名。清理与重建都以这些名字为准，改名即意味着本工具不再接管该对象。 ----
        // 【阶段八】兵线由「双方共用一条」改为「蓝 / 红各一条」：README 2.1.4 要求用节点顺序相反
        // 表达相反的推进方向。桥梁长 60 余米后，共用一条兵线会让红方小兵掉头往红方基地方向走
        // （节点顺序对红方而言是反的），旧场景那点距离掩盖了这个缺陷。
        private const string MidLaneName = "MidLane";
        private const string BlueLaneName = "BlueLane";
        private const string RedLaneName = "RedLane";

        /// <summary>
        /// 双方英雄的推进线（阶段八第二步新增）。与小兵兵线分处桥心两侧，避免 10 英雄与双兵线
        /// 挤在同一条宽度为 0 的直线上（详见 HeroLaneOffsetZ 的说明）。
        /// 它们是独立的场景根对象，因此同样进入 ManagedRootNames，随清理一起被重建。
        /// </summary>
        private const string BlueHeroLaneName = "BlueHeroLane";
        private const string RedHeroLaneName = "RedHeroLane";

        private const string BlueBaseName = "BlueBase";
        private const string BlueSpawnerName = "BlueSpawner";
        private const string RedBaseName = "RedBase";
        private const string RedSpawnerName = "RedSpawner";
        private const string BattlefieldName = "Battlefield";

        /// <summary>双方防御塔的父节点名（塔本身是它的子节点，随父节点一起被按名清理）。</summary>
        private const string BlueTowersRootName = "BlueTowers";
        private const string RedTowersRootName = "RedTowers";

        /// <summary>双方英雄小队的父节点名（5 个英雄实例是它的子节点）。</summary>
        private const string BlueHeroesRootName = "BlueHeroes";
        private const string RedHeroesRootName = "RedHeroes";

        /// <summary>防御塔的外观子节点名（与基地 / 英雄的 Body 命名保持一致）。</summary>
        private const string TowerBodyName = "Body";

        /// <summary>英雄预制体资产名（Assets/Prefabs/HeroPrefab.prefab）。</summary>
        private const string HeroPrefabName = "HeroPrefab";

        /// <summary>
        /// 阶段八之前玩家英雄实例的名字（一个独立的场景根对象）。
        /// 保留这个常量只为了让清理清单能删掉旧场景里的残留；新场景的英雄实例名见 HeroInstancePrefix。
        /// </summary>
        private const string HeroRootName = "PlayerHero";

        /// <summary>
        /// 英雄实例的命名前缀：蓝方 BlueHero_0~4、红方 RedHero_0~4（下标 0 恒为玩家英雄）。
        /// 用「前缀 + 下标」而不是逐个写死名字：实例名要满足本工具按名清理的约定，
        /// 而 10 个名字逐个维护既啰嗦又容易漏改一处。
        /// </summary>
        private const string BlueHeroInstancePrefix = "BlueHero_";
        private const string RedHeroInstancePrefix = "RedHero_";

        /// <summary>英雄胶囊体的子节点名（胶囊体是子节点，根节点只作为贴地坐标原点）。</summary>
        private const string HeroBodyName = "Body";

        /// <summary>
        /// 蓝方的英雄席位数（1 玩家 + 4 AI = 5）。
        ///
        /// 【阶段八 5v5 修复：这个数字不再是"每方 5 个"的同义词】
        /// 上一版用它同时表示蓝方与红方的人数，玩家英雄被塞在蓝方下标 0 里 ——
        /// 于是"蓝方有几个 AI"这件事只存在于 `i == 0` 这个细节中，一改动就可能变成 6v5。
        /// 现在三方人数由 Stage8Assembly 的 PlayerHeroCount / BlueAiHeroCount / RedAiHeroCount 分别表达，
        /// 本常量只作为"蓝方席位 = 玩家 + AI"的派生值保留（出生点数组与循环边界都用它）。
        /// 红方席位恒等于 RedAiHeroCount（红方没有玩家英雄）。
        /// </summary>
        private const int HeroSquadSize = PlayerHeroCount + BlueAiHeroCount;

        // ---- 固定坐标。全部是场景世界坐标，集中在常量区便于后续调整阵型。 ----
        //
        // 【阶段八 · 第三步：桥面加宽到 40 米 + 烘焙期挖洞】
        // 战场沿 X 轴 120 米（Plane 缩放 X=12）、沿 Z 轴 40 米（Plane 缩放 Z=4），长宽比 3 : 1。
        // 蓝方在 -X 侧、红方在 +X 侧，双方基地相距 110 米，一条兵线直通对方基地。
        //
        // 加宽 Z 轴（24 → 40 米）的直接收益：塔的底座在烘焙期被烘成障碍后，洞的半宽约 1.3 米
        // （塔身半宽 0.8 + Agent 半径 0.5），因此塔的两侧各有约 **18.7 米**的绕行车道 ——
        // 5v5 团战有足够的拉扯与包抄空间，不会再出现"所有单位挤在塔正面推挤"的场面。
        //
        // 坐标一览（每一项都在扩容时拉开过 ≥3 倍）：
        //   · 基地 x = ±55，出兵点 x = ±52，英雄出生 x = ±58（各自基地后方 3 米）
        //   · 防御塔 x = ±45（门牙）/ ±30（中路一塔）/ ±15（中路二塔），相邻间距 15 米
        //   · 两侧中路一塔之间留出 30 米中场交战区
        //
        // 相邻塔间距 15 米 > 塔攻击距离 6.5 米，因此两塔射程**不重叠**（中间留 2 米无火力缝隙）——
        // 这是刻意的：若射程重叠，进攻方在通道里会被两座塔同时覆盖，没有任何可绕行的空间。

        private static readonly Vector3 BlueBasePosition = new Vector3(-55f, 0f, 0f);
        private static readonly Vector3 BlueSpawnerPosition = new Vector3(-52f, 0f, 0f);
        private static readonly Vector3 RedBasePosition = new Vector3(55f, 0f, 0f);
        private static readonly Vector3 RedSpawnerPosition = new Vector3(52f, 0f, 0f);

        /// <summary>
        /// 兵线节点的 X 坐标（按蓝方推进顺序：从蓝方侧一路推到红方基地前）。
        ///
        /// 【阶段八第三步：节点改为"贯穿全图"，杜绝"走到一半没节点可走而聚团发呆"】
        /// 上一版只有 4 个节点（-38 / 0 / 38 / 52），最长一段要一次走 38 米。
        /// 大地图上这段"无节点真空"会放大两个问题：
        ///   ① 中途被追击打断、返回兵线时，单位会先朝一个很远的节点走，途中反复被牵引，表现为"来回晃"；
        ///   ② 一旦某个节点因地形原因永远到不了（见下条），MoveState 会卡在那一步，
        ///      停滞检测每 1 秒重下一次指令，整队就停在原地——就是实机看到的"聚团发呆"。
        /// 现在改为每 12.5~15 米一个节点、共 7 个，从己方出兵点前 2 米一直铺到敌方基地前 5 米。
        ///
        /// 【端点为什么从 ±52 内收到 ±50（阶段八修复）】
        /// 上一版的端点恰好等于【出兵点的坐标】（±52），于是每个小兵出生时的"第一个路径点"
        /// 就是它自己脚下的那个点 —— 一个退化的零距离目标。这一条同时踩中两个坑：
        ///   · `HasReachedDestination()` 要求 `remainingDistance ≤ stoppingDistance` **且**
        ///     `agent.velocity ≈ 0`；而同一波 3 个小兵与上一波残兵是**叠在同一个坐标**上生成的，
        ///     它们互相推挤时速度永远不为零，于是第一个节点长期判不到达；
        ///   · 停滞检测（1 秒）反复重新下令，指令指向的还是脚下那个点，什么也不会发生。
        /// 实机日志里"小兵出生 1 秒后立刻报 [MoveState] 连续 1.0 秒几乎未移动"就是这条。
        /// 把端点内收到 ±50（出生点前方 2 米）之后，第一个节点成为一个"真正要走过去"的目标，
        /// 退化情形消失。配套地，MinionSpawner 会把同一波小兵沿 Z 轴错开出生，消除同点堆叠。
        ///
        /// 【为什么终点是 ±50 而不是基地所在的 ±55】基地在烘焙期会被挖成障碍（洞半宽约 1.75 米），
        /// 节点若落在基地正中心，`HasReachedDestination` 将**永远为 false**（agent 到不了洞心），
        /// 于是 MoveState 永远走不完这一站、也就永远切不到 IdleState —— 这正是"死锁卡住"的成因之一。
        /// 终点放在 50（距基地 5 米、在可达区域内），而基地就在 5 米外、落在"追击发起半径"（7 米）之内，
        /// 于是单位抵达后会自然转为攻击基地 —— 这一步现在是靠 `chaseEngageRange` 而不是靠巨大的索敌半径保证的。
        ///
        /// 【为什么刻意避开 ±15 / ±30 / ±45】那三个 X 是防御塔的位置，同样会被挖成障碍。
        /// 节点取两塔中点（±22.5 / ±37.5）与桥心（0），保证每一站都落在可达区域内。
        /// </summary>
        private static readonly float[] LaneWaypointX = { -50f, -37.5f, -22.5f, 0f, 22.5f, 37.5f, 50f };

        /// <summary>
        /// 小兵兵线沿 Z 的固定偏移（米）。取 +2.5。
        ///
        /// 【为什么整条兵线必须偏离桥心 z = 0 —— 这是阶段八第二轮实机修复的核心】
        /// 防御塔位于 z = 0 且会在 NavMesh 上被挖成半径约 1.3 米的洞。而原来的兵线节点【全部在 z = 0】，
        /// 于是"节点 → 节点"的直线段恰好穿过塔洞中心，绕行方向【左右完全等价】：
        ///   · 从 (-37.5, 0) 走到 (-22.5, 0) 要绕过 (-30, 0) 的洞，向左绕与向右绕的距离一模一样；
        ///   · NavMeshAgent 每次 SetDestination 都会重算路径，而单位的实际位置会被邻居推挤而漂移，
        ///     于是"这次绕左、下次绕右"完全可能发生 —— 表现为单位在塔正面来回摆动、推进效率骤降，
        ///     并被停滞检测反复判定为"被卡住"（实机日志里 [MoveState] 被卡住的警告正来自这里）。
        /// 把整条线偏移到 z = 2.5 之后：
        ///   · 2.5 > 塔洞半径 1.3 → 节点连线【不再与洞相交】，路径退化为一条确定的直线，
        ///     不存在"向左还是向右"的二义；
        ///   · 仍然在塔的射程（7.5 米）内（横向偏距 2.5 米），"单线封锁"这条关卡约束不受影响；
        ///   · 距桥面可行走边缘（6.5 米）还有 4 米余量，不会贴边。
        /// </summary>
        private const float MinionLaneOffsetZ = 2.5f;

        /// <summary>
        /// 英雄推进线沿 Z 的固定偏移（米）。取 -2.5，与小兵兵线（+2.5）分处桥心两侧。
        ///
        /// 【为什么英雄需要一条自己的路线】阶段八第一步让 AI 英雄直接复用小兵兵线，
        /// 于是 10 个英雄与两条兵线（每波 3 个，持续补充）全部挤在同一条宽度为 0 的直线上。
        /// 桥面可行走半宽只有 6.5 米，塔洞两侧的通行带各 5.2 米，40 个单位挤在一条线上必然互相顶住。
        /// 分成两条平行通道（小兵 +2.5 / 英雄 -2.5）之后，两者相距 5 米，
        /// 各自仍处在塔的射程内，而互相干扰显著下降。
        /// </summary>
        private const float HeroLaneOffsetZ = -2.5f;

        /// <summary>蓝方小兵兵线的路径点（按行进顺序）。</summary>
        private static readonly Vector3[] BlueLaneWaypointPositions = BuildLaneWaypoints(false, MinionLaneOffsetZ);

        /// <summary>
        /// 红方小兵兵线的路径点：与蓝方【节点顺序相反】，用数据表达相反的行进方向
        /// （LanePath 刻意不引入"阵营方向"分支，见该类的说明）。
        /// </summary>
        private static readonly Vector3[] RedLaneWaypointPositions = BuildLaneWaypoints(true, MinionLaneOffsetZ);

        /// <summary>蓝方英雄推进线（阶段八第二步新增，与小兵兵线分处桥心两侧）。</summary>
        private static readonly Vector3[] BlueHeroLaneWaypointPositions = BuildLaneWaypoints(false, HeroLaneOffsetZ);

        /// <summary>红方英雄推进线（节点顺序与蓝方相反）。</summary>
        private static readonly Vector3[] RedHeroLaneWaypointPositions = BuildLaneWaypoints(true, HeroLaneOffsetZ);

        /// <summary>
        /// 按"是否反向 + Z 偏移"生成一条兵线的节点坐标。
        ///
        /// 【为什么抽成方法而不是写四份字面量】四条线只有两个维度不同（方向、横向偏移），
        /// 写四份等于把"节点 X 序列"复制四遍——日后调整节点密度就要改四处，
        /// 漏改一处的后果是"红方英雄的推进线和小兵线密度不一样"，而这类差异极难在实机里看出来。
        /// 抽成方法后，"节点序列"只有 <see cref="LaneWaypointX"/> 一个来源。
        /// </summary>
        /// <param name="reverse">true = 反向（从红方侧推向蓝方基地）。</param>
        /// <param name="offsetZ">整条线沿 Z 的固定偏移。</param>
        /// <returns>按行进顺序排列的节点坐标。</returns>
        private static Vector3[] BuildLaneWaypoints(bool reverse, float offsetZ)
        {
            Vector3[] waypoints = new Vector3[LaneWaypointX.Length];

            for (int i = 0; i < waypoints.Length; i++)
            {
                int sourceIndex = reverse ? waypoints.Length - 1 - i : i;
                waypoints[i] = new Vector3(LaneWaypointX[sourceIndex], 0f, offsetZ);
            }

            return waypoints;
        }

        /// <summary>蓝方 3 座防御塔的坐标（门牙塔 → 中路塔 A → 中路塔 B，逐级向桥心推进）。</summary>
        private static readonly Vector3[] BlueTowerPositions =
        {
            new Vector3(-45f, 0f, 0f),
            new Vector3(-30f, 0f, 0f),
            new Vector3(-15f, 0f, 0f)
        };

        /// <summary>红方 3 座防御塔的坐标（与蓝方沿桥心对称；中路一塔之间留出 30 米中场交战区）。</summary>
        private static readonly Vector3[] RedTowerPositions =
        {
            new Vector3(45f, 0f, 0f),
            new Vector3(30f, 0f, 0f),
            new Vector3(15f, 0f, 0f)
        };

        /// <summary>塔名后缀（与坐标数组同序），用于生成 BlueTower_Inner / BlueTower_MidA / BlueTower_MidB。</summary>
        private static readonly string[] TowerNameSuffixes = { "Inner", "MidA", "MidB" };

        /// <summary>塔对象名前缀（配合 TowerNameSuffixes 生成完整塔名）。</summary>
        private const string BlueTowerNamePrefix = "BlueTower_";
        private const string RedTowerNamePrefix = "RedTower_";

        /// <summary>双方英雄出生的 X 坐标（各自基地后方 3 米）。</summary>
        private const float BlueHeroSpawnX = -58f;
        private const float RedHeroSpawnX = 58f;

        /// <summary>
        /// 同一阵营 5 个英雄的 Z 轴站位偏移（横向铺开，避免 5 个胶囊体重叠在一起）。
        /// 下标 0 恒为玩家英雄（居中），AI 英雄依次向两侧展开——玩家永远在队伍中央，便于观察。
        ///
        /// 【间距为什么从 2 米收到 1.5 米】桥面宽度从 40 米收窄到 14 米后，
        /// 可行走半宽只有 6.5 米。原来的 ±4 虽然仍在界内，但会让最外侧英雄贴着桥沿出生，
        /// 出生后第一件事就是被 NavMesh 边界挤回中间；收到 ±3 之后整队离桥沿还有 3.5 米余量，
        /// 开局的"整队"动作不会退化成"贴边蠕动"。
        /// </summary>
        private static readonly float[] HeroSquadZOffsets = { 0f, -1.5f, 1.5f, -3f, 3f };

        /// <summary>出生点的 NavMesh 采样半径。采样不到时会退回原始坐标并告警。</summary>
        private const float HeroSpawnSampleRadius = 2f;

        // ---- 地面与导航（阶段五的实机验证前置：地面必须覆盖战场，否则 NavMeshAgent 无法移动） ----

        /// <summary>
        /// 桥梁地形的目标尺寸（米，XZ 平面）：120 × 14。
        ///
        /// 【为什么从"关键点反推"改成显式常量】原实现是「关键点包围盒 + 每边固定外扩 8 米」，
        /// 于是地图尺寸会随某个关键点挪动而漂移（出生点往后退 3 米，桥就长 6 米）。
        /// 关卡扩容的需求给的是**确定的地图尺寸**，因此尺寸必须是输入而不是输出；
        /// 关键点是否落在桥内改由收尾校验负责（见 ValidateGroundCoverage），
        /// 布局一旦超出桥面就会立刻报出具体是哪个落点，而不是悄悄把桥撑大。
        ///
        /// 【Z 为什么是 14 —— 这是"单线推塔"能否成立的决定性数字，不是审美问题】
        /// 阶段八第二步把 Z 放到了 40 米，理由是"给绕行留出车道"。实机验证证明这个理由站不住：
        /// 塔的攻击距离只有 6.5 米，而 40 米宽的桥可行走宽度（NavMesh 内缩一个 Agent 半径后）
        /// 是 z ∈ [-19.5, 19.5] —— **塔只封锁了其中 33% 的宽度**，剩下 26 米全是"免打绕行道"。
        /// 单位（尤其是玩家英雄）只要贴着 z = ±15 走，就能一路绕开全部 6 座塔直取基地，
        /// "必须一座一座推塔"的关卡意图被彻底破坏。
        ///
        /// 收窄到 14 米之后：
        ///   · 可行走半宽 = 14/2 - 0.5（NavMesh 边缘内缩 Agent 半径）= 6.5 米；
        ///   · 塔攻击距离同时提到 7.5 米（见 TowerAttackRange）；
        ///   · 6.5 &lt; 7.5 → **桥面上不存在任何一个"离塔足够远而不挨打"的横向位置**，
        ///     沿 Z 方向的绕行被彻底堵死。
        ///
        /// 【为什么不是更窄】塔底座在烘焙期会被抠成一个半径约 1.3 米的洞（0.8 塔半宽 + 0.5 Agent 半径），
        /// 单位必须从洞两侧绕过去。14 米桥面在塔位处仍留出左右各约 5.2 米的通行带，
        /// 足够 5v5 加上两条兵线的单位错身；再窄就会把"绕过塔"变成"堵在塔前"，
        /// 那正是问题 1 里"聚团发呆"的成因之一。
        ///
        /// 【X 保持 120】桥长由两座基地的间距（±55）与英雄出生点（±58）决定，与本次修复无关。
        /// </summary>
        private static readonly Vector2 BridgeGroundSize = new Vector2(120f, 14f);

        /// <summary>
        /// 关键落点距桥面边缘的最小余量（米），仅用于收尾校验时提示"贴边"。
        ///
        /// 取值 2 米而不是阶段五的 8 米：地图尺寸现在由 BridgeGroundSize 定死，
        /// 而基地（±55，立方体到 ±56.25）与英雄出生点（±58）本就在桥的两端，
        /// 若还按 8 米要求余量，桥就必须被撑到 132 米 —— 与需求给的地图尺寸直接冲突。
        /// 这里只做"提示"，不自动改尺寸：贴边的后果（出生点被 NavMesh 内缩裁掉）需要人来看一眼。
        /// </summary>
        private const float MinKeyPointMargin = 2f;

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

        /// <summary>
        /// 英雄索敌半径。阶段八第三步由 8 放大到 20（×2.5）。
        ///
        /// 【为什么必须放大】地图拉长到 120 米后，8 米的索敌半径只覆盖桥宽的 1/5，
        /// AI 英雄要走到几乎贴脸才"看见"敌人，表现为"走到一半就站着发呆"。
        ///
        /// 【阶段八修复：索敌半径不再驱动追击与牵引】
        /// 阶段八第二步曾让"放弃追击 / 牵引极限"都以索敌半径（20 / 24）为基准，
        /// 于是索敌半径放大到 24 米时，牵引极限同步涨到 48 米 —— 相当于地图长度的 40%。
        /// 后果是小兵与 AI 英雄"看见 24 米内任何敌人就脱离兵线去追"，被牵出去几十米再走回来，
        /// 全队反复进出兵线、在兵线节点上挤成一团（实机日志：42 条「已被牵引出界」）。
        ///
        /// 现在两者彻底解耦，各司其职：
        ///   · 索敌半径 = "看得见" —— 决定 AI 有没有反应、会不会转向，可以很大（地图长）；
        ///   · 追击发起半径（chaseEngageRange，见 AiMinionChaseEngageRange / AiHeroChaseEngageRange）
        ///     = "值不值得离开兵线去打" —— 决定会不会真的脱线，必须很小。
        /// 放弃追击距离与牵引极限距离**改以追击发起半径为准**（仍保留 ×1.5 / ×2.0 的滞回关系，
        /// 只是基准换成了"真正会脱线的那个距离"），因此不需要改任何系数。
        ///
        /// 该值同时是"资产缺失时新建 HeroStats 的写入值"与"TargetingComponent 的兜底配置"。
        /// 仓库里已存在的 HeroStats.asset 由工具按"已存在沿用"策略保留，
        /// 因此本轮已直接把它资产内的 detectionRange 同步改为 20（数据驱动，改资产不改代码）。
        /// </summary>
        private const float HeroDetectionRange = 20f;

        /// <summary>
        /// 小兵的追击发起半径（米）：只有敌人在这个距离内，小兵才会【脱离兵线】去追击。
        ///
        /// 取 7 米 = 小兵攻击距离（2 米）+ 5 米缓冲。缓冲给的是"转向 + 靠近"的余量：
        /// 3.5 米/秒的移速走 5 米约 1.4 秒，足够自然，不会出现"刚发现就贴脸"的突兀感。
        ///
        /// 【为什么必须远小于索敌半径（24 米）】见 HeroDetectionRange 的说明：
        /// 索敌半径决定"看不看得见"，本值决定"会不会离线"。桥面收窄到 14 米之后，
        /// 7 米的发起半径意味着小兵只在"兵线上正面遭遇"时交战，不会再被侧后方的单位勾走。
        /// </summary>
        private const float AiMinionChaseEngageRange = 7f;

        /// <summary>
        /// 英雄（含 AI 英雄）的追击发起半径（米）。取 8 米 = 英雄攻击距离（2.5 米）+ 5.5 米缓冲。
        ///
        /// 比小兵略大：英雄移速 6 米/秒，一次"转身—贴近—开打"只要 1.3 秒，
        /// 且英雄需要能主动接住"从兵线侧面切入的敌人"，因此给它多 1 米的反应窗口。
        /// </summary>
        private const float AiHeroChaseEngageRange = 8f;

        // ---- 阶段八第二步：HeroAIController 的决策参数（由工具按实例注入，理由同 chaseEngageRange）----
        // 【为什么这几个也要注入而不是靠字段默认值】它们是"AI 的行为契约"：
        //   · 评审 / 验收时要在 Inspector 里一眼核对"AI 到底是 0.5 秒评估一次还是 1 秒"；
        //   · 一旦有人在某个实例上手工改过，默认值就不再可靠，而症状是"这个 AI 的反应和别人不一样"，
        //     极难在实机里看出来。
        // 与 chaseEngageRange 的处理完全同构：**设计值写在工具常量里，工具显式写入**。

        /// <summary>AI 英雄的技能决策节流间隔（秒）。</summary>
        private const float AiHeroDecisionInterval = 0.5f;

        /// <summary>AI 英雄的技能起手距离宽松系数（目标可能在移动，按精确射程筛会漏掉"刚好能打到"的目标）。</summary>
        private const float AiHeroCastRangeSlack = 1.15f;

        /// <summary>AI 英雄「自身增益类」技能的起手交战距离（米）：只有当前目标在此距离内才会开增益。</summary>
        private const float AiHeroSelfCastEngageRange = 12f;

        /// <summary>AI 英雄友方治疗类技能的施放阈值（血量百分比）。</summary>
        private const float AiHeroAllyHealHealthThreshold = 0.9f;

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

        // ---- 阶段八第二步：玩家 QWER 四技能数值（盖伦式机制） ----
        // 与阶段六相同：这些数值只在「资产缺失需要新建」或「一次性的阶段六 → 阶段八迁移」时写入；
        // 迁移完成之后一律沿用，策划在 Inspector 里的调参不会被每轮组装抹掉。

        // Q 致命打击：自身施法 → 加速 + 强化下一次普攻（额外伤害 + 命中沉默）。
        // 前摇 0 是刻意的：盖伦的 Q 是"取消后摇"的核心手段，加前摇会毁掉这个手感。
        private const float SkillQCastTime = 0f;
        private const float SkillQCooldown = 8f;
        private const float SkillQManaCost = 25f;

        /// <summary>强化普攻的额外伤害（叠在下一次普攻的 25 点之上）。</summary>
        private const float SkillQEmpowerBonus = 60f;

        /// <summary>强化普攻的兜底超时（秒）。</summary>
        private const float SkillQEmpowerDuration = 5f;

        /// <summary>强化普攻命中后对目标施加的沉默时长（秒）。</summary>
        private const float SkillQSilenceDuration = 1.5f;

        /// <summary>Q 附带的加速比例（0.3 = 移速 130%）。</summary>
        private const float SkillQHastePercent = 0.3f;

        /// <summary>Q 附带的加速时长（秒）。</summary>
        private const float SkillQHasteDuration = 3f;

        // W 勇气：自身施法 → 立即获得护盾。
        // 前摇 0：它是"抗伤"技能，必须在被集火的瞬间生效，前摇会让它形同虚设。
        private const float SkillWCastTime = 0f;
        private const float SkillWCooldown = 14f;
        private const float SkillWManaCost = 40f;

        /// <summary>护盾值。取 150：约等于英雄最大生命（500）的 30%，能实打实扛住 6 下普攻。</summary>
        private const float SkillWShieldValue = 150f;

        /// <summary>护盾持续时长（秒）。</summary>
        private const float SkillWShieldDuration = 4f;

        // E 审判：自身施法 + 跟随自身的持续 AOE。
        private const float SkillECastTime = 0.15f;
        private const float SkillECooldown = 10f;
        private const float SkillEManaCost = 55f;

        /// <summary>审判的作用半径（米）。比 W 的 3.5 略大，符合"自身周围一圈"的观感。</summary>
        private const float SkillEEffectRadius = 3.6f;

        /// <summary>审判的持续时长（秒）。</summary>
        private const float SkillEAreaDuration = 3f;

        /// <summary>审判的周期结算间隔（秒）。取 0.25：旋转切割的手感需要更密的结算节奏。</summary>
        private const float SkillETickInterval = 0.25f;

        /// <summary>
        /// 审判每次结算的伤害。刻意远低于火球（120）：它是【高频低伤】，
        /// 总伤害靠 12 次结算累积（3s / 0.25s），而不是靠单次爆发。
        /// </summary>
        private const float SkillETickDamage = 18f;

        // R 德玛西亚正义：指向性斩杀（基础伤害 + 系数 × 目标已损失生命值）。
        private const float SkillRCastRange = 6f;
        private const float SkillRCastTime = 0.3f;
        private const float SkillRCooldown = 40f;
        private const float SkillRManaCost = 100f;

        /// <summary>斩杀的基础伤害（满血目标只吃这一部分）。</summary>
        private const float SkillRBaseDamage = 100f;

        /// <summary>
        /// 斩杀系数：伤害 = 基础伤害 + 系数 × 目标已损失生命值。
        /// 取 0.35：目标掉一半血（250）时额外承受 87.5，残血（400 已损失）时额外承受 140。
        /// </summary>
        private const float SkillRExecuteHealthRatio = 0.35f;

        /// <summary>弹道材质颜色（火球橙）。</summary>
        private static readonly Color ProjectileColor = new Color(1f, 0.45f, 0.1f, 1f);

        /// <summary>
        /// 弹道外观的半径（米）。取 0.5，与各技能资产里的 projectileRadius 保持一致。
        ///
        /// 【为什么它必须与技能配置对齐】弹道的命中判定是"与目标的距离 ≤ 配置的命中半径"（见 Projectile），
        /// 而外观只是那个半径的可视化。两者不一致就会出现"明明擦着飞过去却掉血"（外观小于判定）
        /// 或"看着打中了却没伤害"（外观大于判定）—— 这类问题玩家一定会报，但代码里查不出任何异常。
        /// 因此这里用一个显式常量承载它，改技能配置时同步改这一处。
        /// </summary>
        private const float ProjectileVisualRadius = 0.5f;

        /// <summary>
        /// 地面 Layer 名。PlayerCommandController 靠它做右键点地拾取，AutoSceneBuilder 靠它做落点覆盖校验，
        /// 因此两处共用同一个常量，避免改名后只改一处。
        /// </summary>
        private const string GroundLayerName = "Ground";

        /// <summary>
        /// 静态建筑所在的 Layer（阶段八第三步新增，索引 9，见 ProjectSettings/TagManager.asset）。
        ///
        /// 【为什么必须给建筑一个专属 Layer —— 这是"烘焙期挖洞"能成立的前提】
        /// 本工程需要「防御塔与基地在 NavMesh 上被天然挖空，单位划弧线绕行」。
        /// 旧版 Unity 的做法是给对象打 `StaticEditorFlags.NavigationStatic`，但该枚举成员在本引擎版本
        /// 已弃用（CS0618），官方替代路径是"由调用方显式收集几何"（见 BakeNavMeshModern）。
        /// 而显式收集是按【Layer 掩码】筛选的，于是：
        ///   · 若把建筑所在的 Default 层加进掩码 —— 场景里 10 个英雄实例（Default 层）的胶囊体
        ///     也会被烘成洞，NavMesh 上凭空多出 10 个坑，而且是"编辑器里烘出来、运行时才发现"；
        ///   · 因此必须给静态建筑一个专属 Layer，把它单独纳入烘焙范围。
        ///
        /// 【为什么不靠"创建顺序"来规避】有人会想"英雄在步骤 8 之后创建，所以烘焙时场景里没有英雄"。
        /// 这个论证是脆弱的：步骤顺序一旦调整（或有人手工在场景里放一个英雄做调试），
        /// 洞就会凭空出现且没有任何提示。用 Layer 表达"这是静态障碍"是**数据**层面的约束，
        /// 与步骤顺序解耦，也与旧版 NavigationStatic 的语义完全等价。
        ///
        /// 【对既有系统的零影响】该 Layer 不是 Ignore Raycast，因此
        /// `Physics.DefaultRaycastLayers`（右键拾取）与 `Physics.AllLayers`（索敌）都包含它，
        /// 建筑仍然可被点到、可被锁定；NavMeshAgent 也不参与物理碰撞，因此物理层与导航层共用无副作用。
        /// </summary>
        private const string BuildingLayerName = "Building";

        /// <summary>
        /// 专用资产的文件名前缀。
        /// 这些资产由本工具按固定路径直接创建与取用，因此必须排除在通用资产查找之外，见 IsDedicatedAsset。
        ///   · Hero  —— HeroStats / HeroAttack / HeroPrefab（阶段五起）；
        ///   · Tower —— TowerStats / TowerAttack（阶段八新增）。
        /// 阶段八必须把 Tower 也挡掉：通用查找的"名称命中关键词优先"里，小兵的 AttackData 靠关键词 Minion
        /// 胜出，而 TowerAttack 同样含 Attack —— 一旦 MinionAttack 缺失（或改名），
        /// 塔的攻速配置会被当成小兵的普攻配置注入，且不会有任何报错。
        /// </summary>
        private static readonly string[] DedicatedAssetNamePrefixes = { "Hero", "Tower" };

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
            // 阶段八起兵线拆为蓝 / 红两条；MidLane 是阶段八之前那条双方共用的单兵线，
            // 保留在清单里只为清掉旧场景的残留（清掉之后不会再被创建，规则自限）。
            MidLaneName, BlueLaneName, RedLaneName,

            // 阶段八第二步新增：双方英雄的推进线（与小兵兵线分处桥心两侧）。
            BlueHeroLaneName, RedHeroLaneName,

            BlueBaseName, BlueSpawnerName, RedBaseName, RedSpawnerName, BattlefieldName,

            // 阶段八新增：双方各 3 座防御塔的父节点、双方各 5 个英雄的父节点。
            // 塔与英雄都是这些父节点的子节点，随父节点一起被清理，因此不必逐个登记名字。
            BlueTowersRootName, RedTowersRootName, BlueHeroesRootName, RedHeroesRootName,

            // 阶段八之前玩家英雄是一个独立的场景根对象，保留在清单里只为清掉旧场景的残留。
            HeroRootName,

            // 阶段七新增：屏幕空间 HUD 与世界空间 UI（血条 / 飘字）的根对象。
            // 加入清单后，重复执行会自动先删后建，UI 与战场一样保持幂等。
            UIRootName, WorldUIRootName,

            // 阶段八自审新增：场景级调试视图的根对象（MatchDebugView + TowerAggroDebugView）。
            DebugViewsRootName,

            // 阶段八实机修复新增：白盒占位特效的根对象（WhiteboxVfxManager 与它生成的临时几何体）。
            VfxRootName,

            // 阶段九新增：正式特效层的根对象（VfxSpawner 与它的对象池实例）。
            // 与白盒 VfxRoot 刻意分开：白盒占位是"没有美术资源时的权宜实现"，资源接入后应整体删除；
            // 正式特效层要长期存在。合在一个根下会让"删掉白盒"变成一件需要小心区分对象的事。
            VfxSpawnerRootName,

            // 阶段四/五遗留：MatchDebugView 曾经手工挂在这个根对象上（工具从不清理它）。
            // 纳入清单只为清掉旧场景的残留 —— 该视图现已由本工具挂到 DebugViews 上（规则自限）。
            LegacyGameManagerName
        };

        /// <summary>
        /// 菜单入口：一键组装测试战场（阶段八：嚎哭深渊桥梁地形 + 6 塔 + 10 英雄 5v5）。
        /// 流程：清理旧战场 → 查找配置资产 → 弹道资产（塔要用） → 建双兵线 → 建蓝方 → 建红方
        ///      → 建对局编排 → 建 6 座防御塔 → 地面（矩形桥梁）与 NavMesh 烘焙 → 生成英雄预制体
        ///      → 实例化 10 英雄小队 → 绑定相机跟随 → 补齐资产缺口 → 收尾校验 → 装配 UI。
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

            // ---------- 步骤 0：前置硬校验（不通过就整体中止，不留下半套战场） ----------
            // 【为什么 Building 层的缺失必须"中止"而不是"降级告警"】
            // 防御塔与基地在导航网格上是【靠烘焙期挖洞】来表达"这里走不过去"的：
            // 它们的几何被烘进 NavMesh，于是原地形成空洞，单位自动划弧线绕行。
            // 而烘焙是按 Layer 掩码收集几何的 —— 层不存在，掩码里就没有建筑，
            // 烘焙本身仍然"成功"，只是网格上什么洞都没有。
            //
            // 后果是【静默的穿模】：NavMeshAgent 不参与物理碰撞（小兵/英雄预制体上既没有
            // Rigidbody 也没有 CharacterController），于是单位会直接从塔体与基地里穿过去。
            // 上一轮实机验证踩到的正是这条：烘焙记录写着"1 个几何源 / 10 个三角形"，
            // 只有地面被收进去，Console 里却只有一条容易被忽略的 Warning。
            // 因此这里把"层是否存在"提升为**前置硬约束**：没有层就不动手，避免又生成一个
            // "看起来正常、跑起来穿模"的战场。
            if (!EnsureBuildingLayerOrAbort())
            {
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

            // 英雄侧的组装说明收集器在这里就声明：阶段八把「弹道材质与预制体」上移到了步骤 2b
            // （防御塔需要它），而那一块仍然属于英雄侧资产的范畴，记录要写进同一份清单。
            List<string> heroNotes = new List<string>();

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

            // ---------- 步骤 2b：弹道材质与预制体（原步骤 14，阶段八上移） ----------
            // 【为什么上移到这个位置】阶段八的防御塔普攻要发射弹道，塔在步骤 7b 就要被创建，
            // 而塔必须拿到弹道预制体才能完成注入。原来的位置（步骤 14）在塔之后，
            // 那样要么塔拿不到预制体（降级为即时结算），要么得让塔装配自己去按路径取一次资产
            // （第二次取用的可能是上一次运行留下的旧预制体，与本次生成的不是同一个对象）。
            // 弹道资产与预制体只依赖材质，不依赖英雄或地面，因此上移没有任何副作用。
            Material projectileMaterial = EnsureProjectileMaterial(heroNotes);
            GameObject projectilePrefab = EnsureProjectilePrefab(projectileMaterial, heroNotes);

            // ---------- 步骤 3：推进线（小兵两条 + 英雄两条，阶段八第二步） ----------
            // 四条线的节点序列来自同一个 LaneWaypointX，只是"方向"与"Z 偏移"不同：
            // 小兵走 z = +2.5，英雄走 z = -2.5（详见 MinionLaneOffsetZ / HeroLaneOffsetZ）。
            CreateLanes(
                out LanePath blueLane,
                out LanePath redLane,
                out LanePath blueHeroLane,
                out LanePath redHeroLane);

            // ---------- 步骤 4：蓝方阵地 ----------
            // 材质：基地模型贴图材质优先、纯色兜底（阶段九，理由同防御塔）。
            Material blueBaseMaterial = ResolveEntityColorMaterial(
                NexusModelBlueMaterialPath, BlueBaseMaterialPath, BlueBaseColor, 0.1f, "蓝方基地材质（蓝）", baseNotes);

            EntityBase blueBase = CreateBase(
                BlueBaseName, BlueBasePosition, TeamType.Player, baseStats, blueBaseMaterial);
            MinionSpawner blueSpawner = CreateSpawner(
                BlueSpawnerName, BlueSpawnerPosition, TeamType.Player, spawnData, blueLane);

            // ---------- 步骤 5：红方阵地（阵营强制为 Enemy，与蓝方形成敌对关系） ----------
            Material redBaseMaterial = ResolveEntityColorMaterial(
                NexusModelRedMaterialPath, RedBaseMaterialPath, RedBaseColor, 0.1f, "红方基地材质（红）", baseNotes);

            EntityBase redBase = CreateBase(
                RedBaseName, RedBasePosition, TeamType.Enemy, baseStats, redBaseMaterial);
            MinionSpawner redSpawner = CreateSpawner(
                RedSpawnerName, RedSpawnerPosition, TeamType.Enemy, spawnData, redLane);

            // ---------- 步骤 5b：给双方基地挂真实水晶模型（阶段九） ----------
            // 放在这里而不是 CreateBase 内部：与防御塔走同一条通道（AttachBuildingModel），
            // "建筑换模型"只有一份实现。白盒 Body 只关渲染器，碰撞体保留 —— 基地要被索敌与右键拾取。
            if (blueBase != null)
            {
                AttachBuildingModel(blueBase.gameObject, NexusModelPrefabPath, blueBaseMaterial, BlueBaseName, baseNotes);
            }

            if (redBase != null)
            {
                AttachBuildingModel(redBase.gameObject, NexusModelPrefabPath, redBaseMaterial, RedBaseName, baseNotes);
            }

            // ---------- 步骤 6：对局编排 ----------
            // 必要性：MinionSpawner 刻意不在 Start 里出兵，出兵由 MatchController 统一控制。
            // 若不建这个对象，场景跑起来后一条兵都不会出，也无法判定胜负——「测试战场」就只是个静态摆设。
            MatchController matchController = CreateMatchController(matchConfig, blueSpawner, redSpawner);

            // ---------- 步骤 7：清理场景里多余的 MatchController ----------
            // 必要性：MatchController 是「每场景一个」的对局编排者。场景里若存在第二个
            // （例如更早手工搭的、其出兵点引用已被本次按名字清理置空的那个），它会各自跑一遍开局流程，
            // 日志里会出现两行「对局准备中」+ 两行「对局开始」，对局状态互相覆盖，排查成本极高。
            List<string> removedControllers = RemoveDuplicateMatchControllers(matchController);

            // ---------- 步骤 7b：6 座防御塔（阶段八） ----------
            // 放在这里而不是更靠后：塔是固定建筑，不依赖 NavMesh，也不依赖英雄；
            // 但它们的坐标会参与「地面覆盖范围」与「关键落点校验」，因此必须在步骤 8 之前建好，
            // 这样收尾校验读到的战场是完整的。
            // 资产与装配实现拆在 AutoSceneBuilder.Stage8Assembly.cs（partial 分册）。
            //
            // 【阶段八第三步：顺序现在是硬约束，不可再调换】
            // 建筑（塔 + 基地）的外观体位于 Building 层，NavMesh 烘焙会收集该层的几何并把它们抠成空洞。
            // 因此「建建筑」必须严格早于「烘焙 NavMesh」（步骤 8）—— 顺序反了的话，
            // 本次烘焙的导航网格里根本没有塔与基地，单位又会朝建筑中心推挤，
            // 而 Console 里不会有任何线索（烘焙本身是成功的）。基地在步骤 4 / 5 创建，同样满足该约束。
            List<string> towerNotes = new List<string>();
            CreateTowerLine(projectilePrefab, towerNotes);

            // ---------- 步骤 8：地面覆盖与 NavMesh 烘焙（实机验证前置） ----------
            // 必须放在「实例化玩家英雄」之前：英雄落点要做 NavMesh.SamplePosition 吸附，
            // 地面还没放大、网格还没烘焙时采样必然失败，英雄会被放到 NavMesh 之外并永久卡死。
            List<string> groundNotes = new List<string>();
            EnsureGroundAndNavMesh(groundNotes);

            // ---------- 步骤 8b：建筑白盒外观让位（阶段九，**必须在烘焙之后**） ----------
            // 烘焙按层掩码收集 MeshRenderer 来"挖洞"，先关掉渲染器 = 建筑不参与几何收集 = 没有洞 = 单位穿模。
            // 详见 HideBuildingWhiteboxVisuals 的说明。
            HideBuildingWhiteboxVisuals(groundNotes);

            // ---------- 步骤 9~11：玩家英雄（预制体 → 场景实例 → 相机跟随） ----------
            // 这三步都遵循同一条原则：所有依赖注入都由工具完成，用户不需要在 Inspector 里拖任何引用。
            AttackData heroAttack = EnsureHeroAttackAsset(heroNotes);
            EntityStatsData heroStats = EnsureHeroStatsAsset(heroAttack, heroNotes);
            Material heroMaterial = EnsureHeroMaterial(heroNotes);

            // ---------- 步骤 13：技能资产与属性资产补缺（阶段六 Q/W → 阶段八 QWER + 技能池） ----------
            // 顺序说明：必须先有技能资产，才能把它注入 EntityStatsData 的技能槽，
            // 也才能把它注入英雄预制体的 SkillComponent。
            SkillData heroSkillQ = EnsureHeroSkillQAsset(heroNotes);
            SkillData heroSkillW = EnsureHeroSkillWAsset(heroNotes);
            SkillData heroSkillE = EnsureHeroSkillEAsset(heroNotes);
            SkillData heroSkillR = EnsureHeroSkillRAsset(heroNotes);

            // 技能池（阶段八第二步，本轮扩到 10 个）：一批与玩家 QWER 无关的基础技能，
            // 供 9 个 AI 英雄按【固定 seed】各抽 4 个（无放回）挂入 Q/W/E/R。池资产是"派生工件"（数值由本工具定义），
            // 因此与 Q/W/E/R 同一策略：缺失才创建、已存在一律沿用（策划仍可在 Inspector 里调参）。
            List<SkillData> skillPool = EnsureSkillPoolAssets(heroNotes);

            // 只补空字段、绝不覆盖已有值 —— 见方法注释里那条"资产 guid 铁律"的踩坑记录。
            // 注意：它同时会把 HeroStats 上的四个技能槽【清空】（技能不再由属性资产承载，
            // 否则运行期会用共享模板把 AI 英雄按技能池注入的技能覆盖掉 —— 实机打回的根因之一）。
            PatchHeroStatsGaps(heroStats, heroNotes);

            // 实体四格配色材质（英雄蓝/红、小兵白/黑）。必须早于步骤 15：英雄预制体在创建时就要把
            // TeamColorView 与这四份材质一起注入（理由见 CreateHeroPrefab 里的说明）。
            EntityColorSet entityColors = EnsureEntityColorMaterials(heroNotes);

            // ---------- 步骤 15：英雄预制体（含技能系统全套组件与依赖注入） ----------
            GameObject heroPrefab = CreateHeroPrefab(
                heroStats, heroAttack, heroMaterial, entityColors,
                heroSkillQ, heroSkillW, heroSkillE, heroSkillR,
                projectilePrefab, heroNotes);

            // 小兵预制体的四格配色注入（与英雄预制体同一套材质、同一套判据）。
            // 【为什么必须紧跟步骤 15、而不能拖到步骤 19】上一版把它放在步骤 19，于是"同一件事"
            // 被拆在相隔很远的两个步骤里，读回校验无论放哪边都会漏掉另一边；更要命的是步骤 19 在
            // 步骤 12（小兵预制体补组件/写参数）之后 —— 一旦某次改动让两处顺序倒过来，
            // 就会出现"注入被后一步的 SaveAsPrefabAsset 盖掉"这类只在实机上才看得见的静默失效。
            ApplyEntityColorsToMinionPrefab(minionPrefab, entityColors, heroNotes);

            // ---------- 步骤 15a：阶段九表现层基建（动画控制器 + 预制体动画接线 + 模型槽位预留） ----------
            // 【为什么必须在这里、而不是放到步骤 20】步骤 15b 会立刻用英雄预制体实例化 10 个英雄。
            // 预制体必须在【实例化之前】就把 Animator 与 AnimationComponent 挂好 ——
            // 若放在实例化之后，就得依赖"改预制体资产后已有实例自动同步"这条引擎行为：
            // 能work，但一旦某次同步失败，症状是"10 个英雄全都没有动画组件"，排查时很难想到是装配顺序问题
            // （与 TeamColorView 那次"注入顺序导致颜色不对"的踩坑同源）。
            // 实现拆在 AutoSceneBuilder.Stage9Assembly.cs（partial 分册）。
            List<string> presentationNotes = new List<string>();
            BuildStage9Presentation(heroPrefab, minionPrefab, presentationNotes);

            // ---------- 步骤 15b：10 英雄小队实例化（阶段八 5v5） ----------
            // 蓝方 = 1 玩家 + 4 AI，红方 = 5 AI（编制由 PlayerHeroCount / BlueAiHeroCount /
            // RedAiHeroCount 三个常量给出，合计 10）；AI 英雄按固定 seed 从技能池各抽 4 个技能。
            HeroController hero = CreateHeroSquad(
                heroPrefab, blueLane, redLane, blueHeroLane, redHeroLane, skillPool, heroNotes,
                out HeroController[] allHeroes);
            CameraController cameraController = SetupCameraController(hero, heroNotes);

            // ---------- 步骤 16：收尾校验（只提示不改场景） ----------
            ValidateSkillSetup(hero, heroStats, projectilePrefab);

            // 编制校验：回场景里把"10 个英雄 / 蓝方 1 玩家 + 4 AI / 红方 5 AI"重新数一遍。
            // 这一步刻意不读上面那些局部变量（它们只是"我打算建什么"），而是读场景里真实存在的对象，
            // 因此"建少了 / 建多了 / 阵营写错了"都逃不掉。
            ValidateHeroRoster();

            // AI 技能分配校验：读回 9 个 AI 英雄实例的技能槽，确认 4 个槽位都来自技能池，
            // 且玩家专属的盖伦 QWER 没有泄漏给任何一个 AI。
            // 【注意它的能力边界】本校验只能证明"磁盘上的场景里写了什么"；运行期是否被别的写入者
            // 覆盖（例如 EntityBase.ApplyStats 拿共享模板改写技能槽）它看不出来 ——
            // 那条链路由 SkillComponent.Initialize 的"实例优先"规则 + HeroAIController 的运行期
            // 技能自证日志共同保证（见两者的说明）。
            ValidateAiSkillAssignment();

            // 英雄模型分配校验（阶段九）：回场景里逐个核对 10 个英雄是否各挂一份**不同**的模型。
            // 放在实例化之后是必然的 —— 模型是按实例挂的，预制体上根本没有。
            ValidateHeroModelAssignment();

            // 实体配色读回校验：英雄 / 小兵预制体上的四格材质是否真的注入了、有没有被改名清空。
            ValidateEntityColorInjection(heroPrefab, minionPrefab, entityColors);

            // Stats Data 注入校验必须放在最后：它是"所有 EntityBase 都拿到了属性资产"的确定性结论，
            // 用来替代 EntityBase.OnValidate 在 AddComponent 瞬间打出的那条瞬时假告警（详见该方法注释）。
            ValidateStatsInjection(blueBase, redBase, hero, minionPrefab);

            // ---------- 步骤 16b：对局控制器补齐英雄引用（阶段八） ----------
            // 必须在 BuildUIAssembly 之前：复活遮罩要读 MatchController.PlayerHero，
            // 而 BuildUIAssembly 会把它注入给 UI 视图。
            AssignObjectReference(matchController, "playerHero", hero);
            AssignObjectArray(matchController, "heroes", allHeroes);

            // ---------- 步骤 12：补齐会导致运行期直接失败的资产与预制体缺口 ----------
            List<string> assetNotes = PatchAssetGaps(spawnData, minionPrefab, attackData);
            List<string> prefabNotes = RepairPrefabComponents(minionPrefab);

            // 小兵预制体的 AI / 代理参数（追击发起半径、到达停靠距离、避让优先级）必须在"补齐组件"之后写：
            // 若预制体原本缺 EntityAIController 或 MovementComponent，上一步刚把它们补上，这一步才写得到值。
            PatchMinionPrefabTuning(minionPrefab, prefabNotes);

            // ---------- 步骤 17：UI 与可视化装配（阶段七） ----------
            // 放在最后：UI 需要英雄实例、对局控制器与主相机都已就位才能完成依赖注入。
            // 实现拆在 AutoSceneBuilder.UIAssembly.cs（partial 分册），菜单入口与撤销组仍是同一个。
            List<string> uiNotes = new List<string>();
            BuildUIAssembly(hero, matchController, uiNotes);

            // ---------- 步骤 18：场景级调试视图（阶段八自审补齐） ----------
            // 为什么排在 UI 之后：调试视图不参与任何逻辑，也不被其它步骤依赖，
            // 放在最后可以保证"战场 / UI 已经成型"时再挂观察者 —— 出问题时的排查顺序更直观。
            // 实现拆在 AutoSceneBuilder.DebugAssembly.cs（partial 分册）。
            // 两个视图都不需要注入任何引用（MatchDebugView 读静态事件 + 静态方法，
            // TowerAggroDebugView 自己经 EntityRegistry 找塔），因此不传 matchController。
            List<string> debugNotes = new List<string>();
            BuildDebugViews(debugNotes);

            // ---------- 步骤 19：白盒视觉反馈（阶段八实机修复） ----------
            // 只做一件事：场景级占位特效管理器（VfxRoot）。
            // 实体配色的注入与校验已经收在步骤 15 / 16（英雄与小兵预制体同批处理，理由见
            // ApplyEntityColorsToMinionPrefab 的说明）。
            // 排在最后的原因与调试视图相同：它不参与任何逻辑、也不被其它步骤依赖，
            // 放在"战场 / UI / 调试视图都成型之后"可以让排查顺序更直观。
            // 实现拆在 AutoSceneBuilder.VfxAssembly.cs（partial 分册）。
            List<string> vfxNotes = new List<string>();
            BuildWhiteboxVfx(vfxNotes);

            // ---------- 步骤 20：阶段九正式特效层（VfxSpawnerRoot） ----------
            // 与白盒 VfxRoot 并列的另一个根对象：三个特效槽位为空时 VfxSpawner 完全惰性
            // （不建池、不生成任何对象），因此现在与白盒管理器并存不会播两套特效。
            // 实现同样在 AutoSceneBuilder.Stage9Assembly.cs 里。
            BuildVfxSpawner(presentationNotes);

            // ---------- 收尾 ----------
            // 标记场景已修改，避免用户在没保存的情况下直接进播放模式、丢失本次组装结果。
            EditorSceneManager.MarkSceneDirty(scene);
            Undo.CollapseUndoOperations(undoGroup);

            // 自动保存（阶段八第二步新增，见 SaveSceneAfterAssembly 的说明）。
            // 必须在 LogSummary 之前调用：那条日志要如实报告"到底存没存"。
            SaveSceneAfterAssembly(scene);

            LogSummary(
                removedNames, removedControllers, blueBase, redBase, blueSpawner, redSpawner, assetNotes, prefabNotes,
                hero, cameraController, heroNotes, groundNotes, baseNotes, towerNotes, uiNotes, debugNotes, vfxNotes,
                presentationNotes);
            ValidateNavMeshCoverage();
        }

        /// <summary>
        /// 组装结束后自动保存场景。
        ///
        /// 【为什么必须由工具来保存 —— 这是实机踩出来的一个静默不一致】
        /// 上一轮实机验证时，NavMesh 资产（22:47）已经被烘焙工具原地更新成"120×14 + 建筑挖洞"，
        /// 而 MainScene.scene（21:51）却还停在阶段七的样子（38×38 的地面、没有塔、没有英雄）。
        /// 根因是：工具只调了 MarkSceneDirty，进播放模式时用的是【内存里的场景】（所以实机表现是对的），
        /// 但用户没有按 Ctrl+S —— 于是磁盘上留下一对互相矛盾的资产：
        /// 场景里的地面是 38×38，而它按 guid 引用的 NavMesh 却是 120×14 的。
        /// 下一次打开工程，单位会站在一张比可见地面大得多的导航网格上，且塔与英雄全部消失。
        ///
        /// 这类"编辑器里看着对、重启后全变样"的问题几乎无法靠使用者自查发现，
        /// 而它又直接违背了本工具的承诺（§6 铁律：一键组装 → 直接 Play，拒绝人工步骤）。
        /// 因此这里把"保存"也纳入自动化：工具既然改了场景，就由工具负责把它落盘。
        ///
        /// 【幂等与安全边界】
        ///   · 只保存当前活动场景，不碰任何其它已打开的场景（不调 SaveOpenScenes）；
        ///   · 场景从未保存过（没有磁盘路径，例如 Untitled）时只告警不猜测路径 —— 工具不该替用户决定存到哪；
        ///   · 保存失败（磁盘只读、路径被占用等）只告警：本次组装的内存结果仍然可用，用户可手动 Ctrl+S。
        /// </summary>
        /// <param name="scene">当前活动场景。</param>
        private static void SaveSceneAfterAssembly(Scene scene)
        {
            if (string.IsNullOrEmpty(scene.path))
            {
                Debug.LogWarning(
                    "[AutoSceneBuilder] 当前场景尚未保存过（没有磁盘路径），已跳过自动保存。" +
                    "本次组装结果只存在于内存中，重启编辑器后会丢失 —— 请手动另存为场景文件。");
                return;
            }

            if (!EditorSceneManager.SaveScene(scene))
            {
                Debug.LogWarning(
                    $"[AutoSceneBuilder] 场景 {scene.path} 自动保存失败（磁盘只读 / 路径被占用？）。" +
                    "本次组装结果仍在内存中可用，请手动按 Ctrl+S 保存，否则重启后会丢失。");
                return;
            }

            Debug.Log(
                $"[AutoSceneBuilder] 场景 {scene.path} 已自动保存。\n" +
                "  · 为什么要自动保存：工具会同时改动【场景】与【场景级 NavMeshData 资产】两处，" +
                "若只落盘后者，重启编辑器后就会看到一张比地面大得多的导航网格（且塔与英雄全部消失）——" +
                "这正是上一轮实机踩到的静默不一致。工具改了场景，就由工具负责把它存下来。\n" +
                "  · 撤销提示：Ctrl+Z 的撤销栈在保存后仍然有效（本工具全部改动都在同一个撤销组内），" +
                "误点本菜单时依旧可以一次性回退整套战场。");
        }

        #region 步骤 0：前置硬校验（Building 层：解析 / 自愈）

        /// <summary>ProjectSettings 里 TagManager 资产的标准路径（Layer / Tag 都存在这里）。</summary>
        private const string TagManagerAssetPath = "ProjectSettings/TagManager.asset";

        /// <summary>
        /// 用户层的第一个下标。0~7 是引擎内置层（Default / TransparentFX / Ignore Raycast / …），不可占用；
        /// 用户层是 8~31，共 24 个。
        /// </summary>
        private const int FirstUserLayerIndex = 8;

        /// <summary>用户层的最后一个下标（含）。层数组总长 32。</summary>
        private const int LastUserLayerIndex = 31;

        /// <summary>
        /// 本工程约定的 Building 层槽位（第 9 个用户层 = 下标 9，紧跟在 Ground 之后）。
        /// 只是"优先占用"的偏好：该槽位已被别的名字占用时会自动退到第一个空槽。
        /// </summary>
        private const int PreferredBuildingLayerIndex = 9;

        /// <summary>
        /// 解析 <see cref="BuildingLayerName"/> 的层号；层不存在时**尝试把它写进 TagManager 自愈**。
        ///
        /// 【为什么必须自愈，而不是"报错让人去手工加"】这一条是实机踩出来的：
        /// 上一轮通过直接编辑 `ProjectSettings/TagManager.asset` 加了这个层，**文件里确实有**，
        /// 但编辑器只在启动时加载一次 ProjectSettings 的层表 —— 运行中的会话不会因为文件被外部改动
        /// 就重读它。于是 `LayerMask.NameToLayer("Building")` 返回 -1，工具报"层不存在"并中止，
        /// 而用户去 Tags and Layers 里看却是有的，排查方向完全被带偏。
        /// 本工具的一切产出都必须是"点一次菜单就成立"的（§6 铁律：装配自动化、禁止手工步骤），
        /// 因此这里改为自己把层写进去。
        ///
        /// 【自愈走编辑器 API 而不是改文件】用 `SerializedObject` 打开 TagManager 资产再写，
        /// 这样编辑器自己的序列化路径与内存状态是同步的，也不会与编辑器后续保存产生冲突。
        ///
        /// 【三层返回语义】
        ///   1. 名字表里已有 → 直接用它的层号（正常路径，零副作用）；
        ///   2. 名字表没有、但成功写进了 TagManager → 返回写入的槽位号。
        ///      注意：此时名字表可能仍是旧快照（`NameToLayer` 依旧返回 -1），**这不影响使用** ——
        ///      `gameObject.layer` 与烘焙掩码 `1 &lt;&lt; index` 认的都是层号，层号才是引擎级事实，
        ///      名字表只是"名字 → 层号"的查询表。因此本工具全程用返回的层号，不依赖名字查询。
        ///   3. 连写都失败（TagManager 资产取不到 / 用户层已满）→ 返回 -1，调用方中止组装。
        /// </summary>
        /// <returns>可用的层号（0~31）；无法解析且无法创建时返回 -1。</returns>
        private static int ResolveBuildingLayerIndex()
        {
            int existing = LayerMask.NameToLayer(BuildingLayerName);
            if (existing >= 0)
            {
                return existing;
            }

            return TryCreateBuildingLayer();
        }

        /// <summary>
        /// 把 <see cref="BuildingLayerName"/> 写进 TagManager 的用户层空槽。
        ///
        /// 【幂等性】只有在"名字查不到"时才会被调用；而一旦层名真的存在，
        /// `ResolveBuildingLayerIndex` 的第一步就会短路返回，因此不可能写出重复的层。
        /// 若内存里的层表与文件不一致（本方法存在的直接原因），内存层表里那个槽位也是空的，
        /// 写入的结果与文件里的既有内容一致，不会覆盖别的层名。
        /// </summary>
        /// <returns>写入成功返回槽位下标；失败返回 -1。</returns>
        private static int TryCreateBuildingLayer()
        {
            UnityEngine.Object[] tagManagerAssets = AssetDatabase.LoadAllAssetsAtPath(TagManagerAssetPath);

            if (tagManagerAssets == null || tagManagerAssets.Length == 0 || tagManagerAssets[0] == null)
            {
                Debug.LogError(
                    $"[AutoSceneBuilder] 取不到 {TagManagerAssetPath}，无法自动创建 \"{BuildingLayerName}\" 层。" +
                    "请手工在 Edit > Project Settings > Tags and Layers 里新增该层后重新执行本菜单。");
                return -1;
            }

            SerializedObject tagManager = new SerializedObject(tagManagerAssets[0]);
            SerializedProperty layers = tagManager.FindProperty("layers");

            if (layers == null || !layers.isArray)
            {
                Debug.LogError(
                    $"[AutoSceneBuilder] {TagManagerAssetPath} 里找不到 layers 数组，" +
                    $"无法自动创建 \"{BuildingLayerName}\" 层。请手工在 Tags and Layers 里新增该层。");
                return -1;
            }

            int targetIndex = FindEmptyUserLayerSlot(layers);

            if (targetIndex < 0)
            {
                Debug.LogError(
                    $"[AutoSceneBuilder] 24 个用户层已全部被占用，无法自动创建 \"{BuildingLayerName}\" 层。" +
                    "请先腾出一个空层（本工程约定把 Building 放在第 9 个用户层）后重新执行本菜单。");
                return -1;
            }

            SerializedProperty slot = layers.GetArrayElementAtIndex(targetIndex);
            slot.stringValue = BuildingLayerName;

            tagManager.ApplyModifiedProperties();
            AssetDatabase.SaveAssets();

            Debug.Log(
                $"[AutoSceneBuilder] 项目里没有 \"{BuildingLayerName}\" 层，已自动写入 {TagManagerAssetPath} " +
                $"的用户层下标 {targetIndex}（本工程的约定槽位）。\n" +
                "  · 为什么工具要自己加：防御塔与基地靠【烘焙期挖洞】在 NavMesh 上形成障碍，" +
                "烘焙按 Layer 掩码收集几何；层缺失时烘焙仍然\"成功\"，但网格上不会有任何洞，" +
                "而 NavMeshAgent 不参与物理碰撞，单位会直接从塔体里穿过去（实机表现为穿模）。\n" +
                "  · 本次组装会直接使用层号继续（层号是引擎级事实，不依赖名字查询），无需重启编辑器。");

            return targetIndex;
        }

        /// <summary>
        /// 在 layers 数组里找一个可写的用户层空槽：优先本工程约定的下标 9，被占用则退到第一个空槽。
        ///
        /// 【为什么优先固定槽位而不是"第一个空槽"】层号一旦被场景里的对象引用就会写进场景文件，
        /// 固定槽位能让"Building 层"在任何人机器上都是同一个层号，diff 与文档对得上。
        /// 只有当约定槽位被别的层名占用时才退让 —— 那种情况下保持确定性已无意义，
        /// "能装上"优先。
        /// </summary>
        /// <param name="layers">TagManager 的 layers 序列化数组。</param>
        /// <returns>可写的槽位下标；没有空槽返回 -1。</returns>
        private static int FindEmptyUserLayerSlot(SerializedProperty layers)
        {
            if (IsUserLayerSlotEmpty(layers, PreferredBuildingLayerIndex))
            {
                return PreferredBuildingLayerIndex;
            }

            for (int i = FirstUserLayerIndex; i <= LastUserLayerIndex; i++)
            {
                if (IsUserLayerSlotEmpty(layers, i))
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// 判断用户层的某个槽位是否为空（数组越界视为"不可用"）。
        ///
        /// 【为什么不做 propertyType 校验】`TagManager.layers` 在引擎里就是 `string[]`，
        /// 元素必然是字符串；反过来，若哪天它真的换了类型，读取 `stringValue` 会被引擎直接报错，
        /// 不会静默通过 —— 因此再加一层类型判断只会增加"类型名对不上就永远找不到空槽"的风险，
        /// 而那会把排查方向带偏到"用户层满了"这条完全错误的路上。
        /// </summary>
        /// <param name="layers">TagManager 的 layers 序列化数组。</param>
        /// <param name="index">待检查的层号。</param>
        /// <returns>可写返回 true。</returns>
        private static bool IsUserLayerSlotEmpty(SerializedProperty layers, int index)
        {
            if (index < FirstUserLayerIndex || index >= layers.arraySize)
            {
                return false;
            }

            // 空槽在 TagManager 里存的是空字符串（YAML 里显示为一个空的列表项）。
            return string.IsNullOrEmpty(layers.GetArrayElementAtIndex(index).stringValue);
        }

        /// <summary>
        /// 步骤 0 的入口：确保 <see cref="BuildingLayerName"/> 层可用，否则中止组装。
        ///
        /// 【为什么这条校验值得"中止整次组装"】见调用点的长注释：层不可用不会让烘焙报错，
        /// 只会让导航网格里没有洞，最终表现为"单位与防御塔穿模"这种**跑起来才发现**的症状。
        /// 一次组装要花几十秒，让它带着一个必然穿模的结果跑完，比直接停下更贵。
        ///
        /// 【为什么放在最早的入口】`AssignBuildingLayer` 要到步骤 4/5/7b 才被调用，
        /// 那时场景已经被清理过一轮（旧战场已经删了）。在这里一次拦住，
        /// 用户的场景仍是上一次的完整状态，修好之后直接重跑即可。
        /// </summary>
        /// <returns>层可用返回 true；确实无法创建返回 false（调用方应直接 return）。</returns>
        private static bool EnsureBuildingLayerOrAbort()
        {
            if (ResolveBuildingLayerIndex() >= 0)
            {
                return true;
            }

            Debug.LogError(
                $"[AutoSceneBuilder] \"{BuildingLayerName}\" 层不可用且无法自动创建，本次组装已中止。\n" +
                "  · 为什么必须有它：防御塔与基地靠【烘焙期挖洞】在 NavMesh 上形成障碍，" +
                "烘焙按 Layer 掩码收集几何；层不存在时烘焙仍然\"成功\"，但网格上不会有任何洞，" +
                "而 NavMeshAgent 不参与物理碰撞，单位会直接从塔体里穿过去（实机表现为穿模）。\n" +
                "  · 修法：Edit > Project Settings > Tags and Layers，在 User Layer 里新增一个名为 " +
                $"\"{BuildingLayerName}\" 的层（本工程的约定位置是第 9 个用户层），然后重新执行本菜单。\n" +
                "  · 层名必须一字不差（区分大小写）。");

            return false;
        }

        #endregion

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

                // 专用资产（Hero* / Tower*）不参与通用查找，理由见 IsDedicatedAsset。
                if (IsDedicatedAsset(path))
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
                if (IsDedicatedAsset(path))
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
        /// 判断某个资产路径是否属于「专用资产」（文件名以 Hero / Tower 开头）。
        ///
        /// 为什么必须排除（这是一个不会报错、但会让组装结果与预期不符的静默错误）：
        /// 通用查找是「按类型找候选 → 名称命中关键词的优先 → 都没有就用第一个占位」。
        /// 专用资产一旦混进去就会污染这个选择：
        ///   · HeroAttack 的名称含 Attack，会被当成「小兵的 AttackData」；
        ///   · TowerAttack 同理（阶段八新增）；
        ///   · HeroStats 按字母序排在 MinionStats 之前，会顶掉「基地的 EntityStatsData」占位；
        ///   · HeroPrefab 会成为「小兵预制体」的候选。
        /// 三者都不会抛异常，只会让日志与实际注入的资产对不上，因此在这里一次性挡掉。
        /// </summary>
        /// <param name="assetPath">资产路径。</param>
        /// <returns>属于专用资产返回 true（应从通用查找中排除）。</returns>
        private static bool IsDedicatedAsset(string assetPath)
        {
            string fileName = System.IO.Path.GetFileNameWithoutExtension(assetPath);

            for (int i = 0; i < DedicatedAssetNamePrefixes.Length; i++)
            {
                if (fileName.StartsWith(DedicatedAssetNamePrefixes[i], System.StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
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
        /// 创建双方的推进线：小兵两条（阶段八：蓝 / 红各一条）+ 英雄两条（阶段八第二步新增）。
        ///
        /// 【为什么从"双方共用一条"改成"各一条"】README 2.1.4 要求蓝红双方用【节点顺序相反】
        /// 表达推进方向。旧场景里双方共用 MidLane，靠"小兵在中路打起来"掩盖了方向问题；
        /// 桥梁拉长到 60 余米后，红方小兵会先走到中点、再掉头走向红方自己那一侧的节点，一路推回自家基地。
        /// 拆成两条独立的 LanePath 后，"推进方向"变成纯数据，MinionSpawner 与 FSM 一行都不用改。
        ///
        /// 【英雄线为什么单独一条】见 HeroLaneOffsetZ：10 个英雄与两条兵线共用同一条直线时，
        /// 桥面 14 米的通行带会被挤满，互相顶住（实机表现为"整队挤在兵线节点上不动"）。
        /// 分成两条相距 5 米的平行通道后，两者仍各自处在塔的射程内，但互相干扰显著下降。
        /// </summary>
        /// <param name="blueLane">蓝方小兵兵线（从蓝方侧推向红方基地）。</param>
        /// <param name="redLane">红方小兵兵线（节点顺序与蓝方相反）。</param>
        /// <param name="blueHeroLane">蓝方英雄推进线。</param>
        /// <param name="redHeroLane">红方英雄推进线。</param>
        private static void CreateLanes(
            out LanePath blueLane,
            out LanePath redLane,
            out LanePath blueHeroLane,
            out LanePath redHeroLane)
        {
            blueLane = CreateLane(BlueLaneName, BlueLaneWaypointPositions);
            redLane = CreateLane(RedLaneName, RedLaneWaypointPositions);
            blueHeroLane = CreateLane(BlueHeroLaneName, BlueHeroLaneWaypointPositions);
            redHeroLane = CreateLane(RedHeroLaneName, RedHeroLaneWaypointPositions);
        }

        /// <summary>
        /// 创建一条兵线：根对象挂 LanePath，下挂按行进顺序排列的路径点（节点顺序即推进方向）。
        /// </summary>
        /// <param name="laneName">兵线根对象名（进入本工具的接管清单）。</param>
        /// <param name="waypointPositions">路径点的世界坐标，按行进顺序排列。</param>
        /// <returns>刚创建的兵线。</returns>
        private static LanePath CreateLane(string laneName, Vector3[] waypointPositions)
        {
            GameObject laneObject = CreateRootObject(laneName, Vector3.zero);
            LanePath lanePath = Undo.AddComponent<LanePath>(laneObject);

            // Waypoints 是 LanePath 公开暴露的字段（设计上就供外部与 Inspector 装配节点列表），
            // 因此这里直接写列表，不必绕 SerializedObject 的字符串字段名，避免字段改名时静默失效。
            lanePath.Waypoints.Clear();

            for (int i = 0; i < waypointPositions.Length; i++)
            {
                // 子节点名固定为 Waypoint1..N：它们是本工具产物的子节点，随父节点一起被清理，
                // 因此不需要进接管清单，也不需要全局唯一。
                GameObject waypoint = CreateChildObject(
                    laneObject.transform, "Waypoint" + (i + 1), waypointPositions[i]);
                lanePath.Waypoints.Add(waypoint.transform);
            }

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

            // 烘焙期挖洞：把基地设为 Building 层，NavMesh 烘焙时会在这里天然形成一个洞，
            // 单位（小兵 / AI 英雄）会自动划出弧线绕过基地（详见 BuildingLayerName 的说明）。
            // 根节点与外观体都设：真正被烘焙收集的是有网格的 Body，根节点设上只是让层归属一眼可见。
            AssignBuildingLayer(baseObject);
            AssignBuildingLayer(body);

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

        /// <summary>
        /// 把静态建筑对象设为 <see cref="BuildingLayerName"/> 层 —— 这是"烘焙期挖洞"的开关，
        /// 等价于旧版 Unity 里给对象打 `StaticEditorFlags.NavigationStatic` 标记。
        ///
        /// 【根节点与外观体都要调用，但真正起作用的是外观体】
        /// NavMeshBuilder.CollectSources 按【被收集对象自身】的 layer 过滤，而可烘焙的几何来自
        /// MeshRenderer —— 建筑的 Cube 挂在 Body 子节点上，根节点是空物体、没有网格。
        /// 因此"洞"是由 Body 的层决定的。根节点同样设上是为了让"整个建筑属于 Building 层"这件事
        /// 在 Inspector 里一眼可见、不留下"为什么只有子节点在这一层"的疑问。
        ///
        /// 【对既有系统的零影响】该层不是 Ignore Raycast，因此右键拾取
        /// （`Physics.DefaultRaycastLayers`）与索敌（`Physics.AllLayers`）都包含它，
        /// 建筑仍然可被点到、可被锁定；NavMeshAgent 也不参与物理碰撞，物理层与导航层共用无副作用。
        ///
        /// 【层缺失时的行为】用 LogError 报出并保持对象在 Default 层。
        /// 正常流程下走不到这里 —— 步骤 0 的 EnsureBuildingLayerOrAbort 已经把"层缺失"挡在组装之前。
        /// 保留这条分支是为了兜住"组装进行到一半时有人手工删掉了层"这种极端情况：
        /// 宁可留下一条明确的错误日志，也不要静默生成一个"塔不挡路"的战场。
        /// </summary>
        /// <param name="buildingObject">建筑对象（根节点或外观体）。允许为 null。</param>
        private static void AssignBuildingLayer(GameObject buildingObject)
        {
            if (buildingObject == null)
            {
                return;
            }

            int buildingLayer = ResolveBuildingLayerIndex();
            if (buildingLayer < 0)
            {
                Debug.LogError(
                    $"[AutoSceneBuilder] 设置建筑层失败：找不到 \"{BuildingLayerName}\" 层，" +
                    $"{buildingObject.name} 留在 Default 层，NavMesh 烘焙不会在它这里挖洞（单位会穿模）。" +
                    "请在 Tags and Layers 里补上该层后重新执行本菜单。");
                return;
            }

            // 登记撤销：层变更属于场景资产改动，必须能用一次 Ctrl+Z 回退。
            Undo.RecordObject(buildingObject, "设置建筑层");
            buildingObject.layer = buildingLayer;
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
        /// 1. 【尺寸以目标值为准】：地面水平尺寸与中心被写成"战场需要的矩形"（容差 0.1 米内视为已达标，
        ///    因此重复执行完全幂等）；
        /// 2. 【只动内置 Plane 网格】：按 MeshFilter 的网格名识别，Terrain / 自定义网格只告警不缩放
        ///    （那些网格的尺寸语义各不相同，按比例缩放很可能把地面拉变形）；
        /// 3. 【阶段八：允许矩形】原第 3 条是「保持正方形」（统一取 x/z 里的较大值），
        ///    那对一块正方形地面是安全的；但嚎哭深渊要求的是【桥梁】——必须把 Z 轴从阶段七遗留的
        ///    38 米收窄到 24 米。"覆盖即跳过 + 只放大不缩小"会让地面永远停在 38 米宽，桥梁无从谈起。
        ///    因此改为按轴独立设置尺寸，尺寸取自 <see cref="BridgeGroundSize"/>（120×40），
        ///    并只对【本工具产出的那块内置 Plane】生效（约束 2 已把范围收窄）；
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

            // ---- 5. 尺寸比对与调整（阶段八：矩形桥梁） ----
            Bounds requiredBounds = ComputeRequiredGroundBounds();

            if (resizableGround != null)
            {
                // 只比对【这块地面自己】的包围盒，而不是全部 Ground 层碰撞体的并集：
                // 并集里若混入别的 Ground 对象，尺寸永远对不上，每次执行都会重复缩放（破坏幂等）。
                Bounds ownBounds = ResolveGroundObjectBounds(resizableGround, groundBounds);

                if (MatchesRequiredBounds(ownBounds, requiredBounds))
                {
                    notes.Add(
                        $"地面已是目标桥梁尺寸（{ownBounds.size.x:F0}×{ownBounds.size.z:F0}，" +
                        $"中心 ({ownBounds.center.x:F1}, {ownBounds.center.z:F1})），未改动");
                }
                else
                {
                    ResizePlaneGround(resizableGround, requiredBounds, notes);

                    // 改完 Transform 立刻同步一次物理：Collider.bounds 与导航烘焙都读的是「物理世界里」的变换，
                    // 而编辑器默认不会每帧自动同步（Physics.autoSyncTransforms 为 false）。
                    // 不同步的话，收尾的 ValidateGroundCoverage 可能读到缩放前的旧包围盒，
                    // 反过来报一条「落点超出地面范围」的假告警——刚修好就报警，比不报还难排查。
                    Physics.SyncTransforms();
                }
            }
            else if (CoversHorizontally(groundBounds, requiredBounds))
            {
                // 找不到可自动缩放的内置 Plane（Terrain / 自定义网格）：退回"覆盖判断"，只提示不动它。
                notes.Add(
                    $"地面已覆盖战场范围（现有 x∈[{groundBounds.min.x:F1}, {groundBounds.max.x:F1}]，" +
                    $"z∈[{groundBounds.min.z:F1}, {groundBounds.max.z:F1}]），未改动" +
                    "（未找到可自动缩放的内置 Plane 网格，因此无法调整为桥梁比例）");
            }
            else
            {
                Debug.LogWarning(
                    "[AutoSceneBuilder] 地面未覆盖战场范围，但 Ground 层上找不到可自动放大的内置 Plane 网格" +
                    "（可能是 Terrain 或自定义网格）。请手动放大地面，否则出生点会落在 NavMesh 之外。" +
                    $"需要的水平范围：x∈[{requiredBounds.min.x:F1}, {requiredBounds.max.x:F1}]，" +
                    $"z∈[{requiredBounds.min.z:F1}, {requiredBounds.max.z:F1}]。");
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

            // 【收集范围 = Ground 层 + Building 层】
            //   · Ground：地面本体（没有它就没有可行走区域）；
            //   · Building：防御塔与基地 —— 它们被烘进去之后，NavMesh 在这些位置天然形成空洞，
            //     单位会自动划出弧线绕行。这正是需求要的"烘焙期挖洞"，
            //     也是旧版 StaticEditorFlags.NavigationStatic 的现代等价实现
            //     （该枚举成员在本引擎版本已弃用，官方替代路径就是"由调用方显式收集几何"）。
            //
            // 【为什么必须是"专属 Layer"，而不是"顺手把 Default 层也收进来"】
            // 建筑曾经在 Default 层。若直接收集 Default 层，场景里 10 个英雄实例的胶囊体
            // （同样在 Default 层）也会被烘成洞 —— NavMesh 上凭空多出 10 个坑，
            // 而且是"编辑器里烘出来、运行时才发现"的静默失效。
            // 因此阶段八第三步新增了 Building 层（见 BuildingLayerName），把"这是静态障碍"表达成数据，
            // 而不是依赖"英雄恰好在本步骤之后才创建"这种脆弱的步骤顺序论证。
            //
            // 【Layer 缺失的防线是两道，且都是"响亮失败"而不是静默退化】
            // 第一道在步骤 0（EnsureBuildingLayerOrAbort）：层不存在就直接中止整次组装，
            // 那时场景还没被清理，用户修好层就能原样重跑。
            // 第二道就是下面这个 else 分支与紧随其后的几何源数量断言：兜住"组装进行到一半时层被删掉"
            // 这种极端情况，以及"层在、但建筑没被收进来"（外观体被关了 Renderer 等）。
            // 为什么不允许静默退化：层缺失时烘焙仍然"成功"，只是网格上什么洞都没有，
            // 而 NavMeshAgent 不参与物理碰撞 —— 单位会直接穿塔而过，Console 里却只有一条容易被忽略的 Warning。
            int buildingLayer = ResolveBuildingLayerIndex();
            int layerMask = 1 << groundLayer;

            if (buildingLayer >= 0)
            {
                layerMask |= 1 << buildingLayer;
            }
            else
            {
                // 正常流程走不到这里（步骤 0 已中止组装）。保留一条 Error 是为了兜住
                // "组装进行到一半时层被人删掉"这种极端情况 —— 宁可报错，也不要静默生成穿模的战场。
                Debug.LogError(
                    $"[AutoSceneBuilder] 项目里没有名为 \"{BuildingLayerName}\" 的 Layer，" +
                    "本次 NavMesh 烘焙将【不包含】防御塔与基地 —— 它们不会在导航网格上形成空洞，" +
                    "而 NavMeshAgent 不参与物理碰撞，单位会直接从塔体里穿过去（穿模）。" +
                    "请到 Edit > Project Settings > Tags and Layers 中新增该 Layer 后重新组装。");
            }

            List<NavMeshBuildMarkup> markups = new List<NavMeshBuildMarkup>();
            List<NavMeshBuildSource> sources = new List<NavMeshBuildSource>();

            // markups 传空列表 = "不按 markup 过滤，把掩码命中的几何全部收进来"（这是官方文档定义的行为，
            // 本工程从阶段五起一直这么用）。因此真正决定"收哪些对象"的只有 layerMask。
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

            // 【几何源数量断言：把"塔没被烘进去"从静默失效变成响亮失败】
            // 期望值 = 地面 1 个 + 每座建筑的外观 Cube 各 1 个（塔 6 + 基地 2 = 8）。
            // 少于期望值就说明掩码没把建筑收进来 —— 上一轮实机验证踩到的正是这条：
            // 烘焙记录写着"1 个几何源"，只有地面，塔与基地全都没进网格，于是单位穿塔而过。
            // 烘焙本身不会因此报错（它只是忠实地把收到的几何烘出来），所以这条断言是唯一的早期信号。
            int expectedBuildingSourceCount = CountBuildingPositions();
            int minimumExpectedSourceCount = 1 + expectedBuildingSourceCount;

            if (buildingLayer >= 0 && sources.Count < minimumExpectedSourceCount)
            {
                Debug.LogError(
                    $"[AutoSceneBuilder] NavMesh 几何源数量异常：只收集到 {sources.Count} 个，至少应为 " +
                    $"{minimumExpectedSourceCount} 个（地面 1 + 建筑 {expectedBuildingSourceCount}）。\n" +
                    "  · 后果：防御塔与基地不会在导航网格上形成空洞，而 NavMeshAgent 不参与物理碰撞，" +
                    "单位会直接从建筑里穿过去（穿模）。\n" +
                    "  · 常见原因：建筑外观体（Body 子节点）不在 Building 层、或被关了 Renderer、" +
                    "或落在地面包围盒之外导致未被 CollectSources 收进来。");
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

            // 【为什么用文件名而不是 target.name —— 原实现在这里有一个"修复不了已损坏资产"的缺陷】
            // CopySerialized 会把源对象的 name 一并拷过来，而 NavMeshBuilder.BuildNavMeshData
            // 生成的对象 name 为空 —— 结果是场景级 NavMeshData 资产的 m_Name 变成空字符串，
            // 引擎随即输出「Main Object Name '' does not match filename 'NavMesh'」。
            //
            // 原实现写的是 baked.name = target.name，本意是"对齐到目标名"。但资产一旦已经被写成空名字，
            // target.name 本身就是空的 —— 于是"修复"退化成"把空名字再拷一遍"，
            // 引擎每次烘焙都继续报同一条告警（实机已连续复现三次：161290 / 177194 / 180662 行）。
            // 资产名必须有一个与【当前目标状态】无关的确定来源，就是它自己的文件名。
            string expectedAssetName = System.IO.Path.GetFileNameWithoutExtension(assetPath);

            baked.name = expectedAssetName;

            EditorUtility.CopySerialized(baked, target);

            // 【双保险】CopySerialized 对原生类型（NavMeshData 是二进制资产）是否拷贝 m_Name，
            // 在引擎各版本上并不一致 —— 因此目标对象自己也显式写一次。
            // 这也是唯一能自愈"已被写成空名字"的历史资产的地方：只改源对象的话，坏名字会被一直继承下去。
            target.name = expectedAssetName;

            EditorUtility.SetDirty(target);
            AssetDatabase.SaveAssets();

            // 刷新编辑器导航世界，让收尾校验读到的是本次结果（详见方法注释）。
            RefreshEditorNavMeshWorld(baked);

            // 立刻验证：烘焙失败不会抛异常，只会留下一张空网格，因此必须主动读三角形数。
            // 【三角形数用 indices.Length / 3 而不是 vertices.Length / 3】vertices 是整个顶点表，
            // 一个顶点会被多个三角形共用，除以 3 得到的数字没有几何意义（会显著偏小），
            // 而这条日志正是"烘焙到底成没成"的第一手依据，数字必须是准的。
            NavMeshTriangulation triangulation = NavMesh.CalculateTriangulation();
            int triangleCount = triangulation.indices != null ? triangulation.indices.Length / 3 : 0;

            if (triangleCount == 0)
            {
                Debug.LogWarning(
                    "[AutoSceneBuilder] 导航数据已写入资产，但编辑器导航世界里读不到任何可导航区域。" +
                    "请检查 NavMeshSettings 的 Agent 参数（半径 / 高度 / 坡度）是否与地面尺寸匹配。");
            }

            // 【烘焙后语义校验：塔与基地的中心必须"不可行走"】
            // 几何源计数只能证明"建筑被收进来了"，不能证明"洞真的形成了"（例如建筑高度不足、
            // 或 Agent 高度参数异常时，收集成功但烘焙不出障碍）。这里直接对烘焙结果做语义断言：
            // 逐个采样建筑中心，若采样点在水平面上几乎与建筑重合，说明洞没挖出来。
            ValidateBuildingCarve(notes);

            notes.Add(
                $"NavMesh 已烘焙（{sources.Count} 个几何源，其中建筑 {expectedBuildingSourceCount} 个 / " +
                $"{triangleCount} 个三角形）并原地更新 {assetPath}");
        }

        /// <summary>
        /// 建筑落点清单（双方基地 + 双方各 3 座防御塔），坐标与名字成对追加。
        ///
        /// 用途有两个，都围绕"烘焙期挖洞"这一机制：
        ///   ① 烘焙几何源数量断言 —— 期望值 = 地面 1 + 本清单长度；
        ///   ② 烘焙后语义校验 —— 逐个采样建筑中心，确认它真的不可行走。
        /// 与 CollectKeyPoints 同一约定：两份清单在同一次遍历里成对追加，不可能错位。
        /// </summary>
        /// <param name="positions">输出：建筑中心坐标。</param>
        /// <param name="labels">输出：与坐标同序的对象名（用于日志定位）。</param>
        private static void CollectBuildingPositions(out Vector3[] positions, out string[] labels)
        {
            List<Vector3> positionList = new List<Vector3>();
            List<string> labelList = new List<string>();

            AppendKeyPoint(positionList, labelList, BlueBasePosition, BlueBaseName);
            AppendKeyPoint(positionList, labelList, RedBasePosition, RedBaseName);

            for (int i = 0; i < BlueTowerPositions.Length; i++)
            {
                AppendKeyPoint(positionList, labelList, BlueTowerPositions[i],
                    $"{BlueTowersRootName}/{BuildTowerObjectName(BlueTowerNamePrefix, i)}");
            }

            for (int i = 0; i < RedTowerPositions.Length; i++)
            {
                AppendKeyPoint(positionList, labelList, RedTowerPositions[i],
                    $"{RedTowersRootName}/{BuildTowerObjectName(RedTowerNamePrefix, i)}");
            }

            positions = positionList.ToArray();
            labels = labelList.ToArray();
        }

        /// <summary>本工具会创建的建筑总数（由坐标数组推导，加一座塔断言会自动跟上）。</summary>
        private static int CountBuildingPositions()
        {
            CollectBuildingPositions(out Vector3[] positions, out string[] _);
            return positions.Length;
        }

        /// <summary>
        /// 判断某个落点是否是建筑落点（防御塔 / 基地的中心）。
        ///
        /// 用途：把建筑从"关键落点是否落在 NavMesh 上"的校验里排除掉。
        /// 建筑是【故意】被烘成障碍的（烘焙期挖洞），它们的中心本来就不该可行走，
        /// 拿"采样不到就是错"的规则去套它们必然产生假告警。
        /// 只比较 XZ：建筑与关键落点都是贴地坐标，水平位置才是身份，y 不参与身份判定。
        /// </summary>
        /// <param name="point">待判断的落点。</param>
        /// <returns>该落点与某个建筑中心重合（水平误差 &lt; 1 厘米）返回 true。</returns>
        private static bool IsBuildingPosition(Vector3 point)
        {
            CollectBuildingPositions(out Vector3[] buildings, out string[] _);

            for (int i = 0; i < buildings.Length; i++)
            {
                if (Mathf.Abs(buildings[i].x - point.x) < 0.01f && Mathf.Abs(buildings[i].z - point.z) < 0.01f)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 烘焙后语义校验：每个建筑中心都必须是【不可行走】的。
        ///
        /// 【为什么几何源计数还不够】计数只能证明"建筑几何被收进来了"，不能证明"洞真的挖出来了" ——
        /// 例如建筑高度不足 Agent 高度、或 NavMeshSettings 的 Agent 参数被改坏时，
        /// 几何被成功收集、烘焙也成功，但网格上没有任何障碍。这里直接对结果做语义断言：
        /// 采样建筑中心，若最近的可行走点几乎就在它脚下，说明洞没挖出来。
        ///
        /// 【只比较水平距离】导航网格的高度与建筑根节点（贴地原点）本就不在同一个平面上
        /// （地面 Plane 自身带 y 偏移，NavMesh 还会按体素高度量化），比较三维距离会误报。
        /// 因此把 y 剔掉，只看 XZ 平面上的最近可行走点离建筑中心多远。
        ///
        /// 【必须排除"建筑顶面"这块孤立区域】NavMesh 烘焙会把建筑立方体的顶面也烘出来
        /// （1.6×1.6 = 2.56 平方米，大于 minRegionArea 的 2，因此不会被自动剔除）。
        /// 它是一块与地面不连通的孤立区域，任何单位都到不了，不构成穿模；
        /// 但它的正下方就是建筑中心，若不加区分，采样会命中它并报出一条假失败。
        /// 判据：采样点明显高于【地面导航网格的高度】就是顶面 —— 地面高度先在桥心实测一次，
        /// 不写死数值，这样地面 Plane 的 y 偏移日后被调整也不会让这条校验失效。
        ///
        /// 【水平阈值为什么是 1 米】塔半宽 0.8 + Agent 半径 0.5 ≈ 1.3 米；基地半宽 1.25 + 0.5 = 1.75 米。
        /// 取 1 米作为下界：洞正常时实测约 1.3~1.75 米，通过；洞没挖出来时实测约 0 米，失败。
        /// 这个区间足够宽，不会因为体素量化产生的零点几米误差而误判。
        /// </summary>
        /// <param name="notes">组装说明收集器（通过时写一条结论，便于在日志里核对）。</param>
        private static void ValidateBuildingCarve(List<string> notes)
        {
            if (ResolveBuildingLayerIndex() < 0)
            {
                // 层缺失已经在烘焙阶段报过 Error，这里不重复。
                return;
            }

            CollectBuildingPositions(out Vector3[] positions, out string[] labels);

            const float sampleRadius = 2f;
            const float fallbackSampleRadius = 8f;
            const float minimumClearance = 1f;
            const float roofHeightThreshold = 1f;

            // 先在桥心（世界原点，恒为可行走的空地）实测一次地面导航网格的高度。
            bool hasGroundLevel = NavMesh.SamplePosition(
                Vector3.zero, out NavMeshHit groundHit, fallbackSampleRadius, NavMesh.AllAreas);

            float groundLevelY = hasGroundLevel ? groundHit.position.y : 0f;

            List<string> problems = new List<string>();

            for (int i = 0; i < positions.Length; i++)
            {
                Vector3 center = positions[i];

                // 采样半径取建筑半宽的量级：洞正常时，最近的可行走点就在 (半宽 + Agent 半径) 之外。
                if (!NavMesh.SamplePosition(center, out NavMeshHit hit, sampleRadius, NavMesh.AllAreas) &&
                    !NavMesh.SamplePosition(center, out hit, fallbackSampleRadius, NavMesh.AllAreas))
                {
                    // 连 8 米内都没有任何导航网格：既可能是洞挖得干净，也可能是这一片根本没烘出来。
                    // 两种情况的排查动作完全不同，因此单列一条，不当成"通过"。
                    problems.Add($"{labels[i]}{FormatPosition(center)}（周边 {fallbackSampleRadius:F0} 米内没有任何导航网格）");
                    continue;
                }

                // 命中"建筑顶面"（孤立区域，不可达）不算穿模，跳过。
                if (hasGroundLevel && hit.position.y > groundLevelY + roofHeightThreshold)
                {
                    continue;
                }

                float clearance = new Vector2(hit.position.x - center.x, hit.position.z - center.z).magnitude;

                if (clearance < minimumClearance)
                {
                    problems.Add($"{labels[i]}{FormatPosition(center)}（最近可行走点仅 {clearance:F2} 米）");
                }
            }

            if (problems.Count > 0)
            {
                Debug.LogError(
                    "[AutoSceneBuilder] 建筑挖洞校验失败：以下建筑中心在导航网格上【仍然可行走】，" +
                    "说明 NavMesh 烘焙没有把它们抠成空洞：\n  · " + string.Join("\n  · ", problems) + "\n" +
                    "  · 后果：NavMeshAgent 不参与物理碰撞，小兵与英雄会直接从这些建筑里穿过去（穿模）。\n" +
                    "  · 修法：确认建筑外观体（Body 子节点）位于 Building 层、带启用的 MeshRenderer、" +
                    "且落在 NavMeshSettings 的 Agent 参数（半径 0.5 / 高度 2）能够识别为障碍的尺寸内，" +
                    "然后重新执行本菜单。");
                return;
            }

            notes.Add(
                $"建筑挖洞校验通过：{positions.Length} 个建筑中心均不可行走（最近可行走点 ≥ {minimumClearance:F1} 米）");
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
        /// 计算战场需要的水平范围（= 桥梁地形的目标尺寸与位置）。
        ///
        /// 【尺寸】取 <see cref="BridgeGroundSize"/>（120×40 米），不再是"关键点包围盒 + 外扩"：
        /// 关卡扩容的需求给的是**确定的地图尺寸**，尺寸必须是输入而不是输出。
        /// 关键点若超出桥面，由 ValidateGroundCoverage 在收尾时点名报出具体是哪个落点
        /// （而不是悄悄把桥撑大到 132 米，让"120 米的桥"变成一个会漂移的数字）。
        /// 【中心】仍由关键点包围盒决定：把整套阵型沿 X 平移时地图会跟着走，不必同步改尺寸常量。
        /// 【y】不参与：地面高度是关卡决策，工具不该动它。
        /// </summary>
        private static Bounds ComputeRequiredGroundBounds()
        {
            Vector3[] points = GetKeyPoints();

            Bounds keyBounds = new Bounds(points[0], Vector3.zero);

            for (int i = 1; i < points.Length; i++)
            {
                keyBounds.Encapsulate(points[i]);
            }

            return new Bounds(
                keyBounds.center,
                new Vector3(BridgeGroundSize.x, keyBounds.size.y, BridgeGroundSize.y));
        }

        /// <summary>判断地面在水平面上是否已经完整覆盖需求范围（只比 x/z，y 不参与）。</summary>
        private static bool CoversHorizontally(Bounds groundBounds, Bounds requiredBounds)
        {
            return groundBounds.min.x <= requiredBounds.min.x && groundBounds.max.x >= requiredBounds.max.x &&
                   groundBounds.min.z <= requiredBounds.min.z && groundBounds.max.z >= requiredBounds.max.z;
        }

        /// <summary>
        /// 把一块内置 Plane 地面调整为目标矩形并居中到需求范围（阶段八：桥梁地形）。
        ///
        /// Plane 原始网格是 10×10 米，因此 localScale = 需要的边长 / 10，【按轴独立换算】——
        /// 这正是"桥梁"与"正方形"的区别所在（原实现统一取较大值，只会得到正方形）。
        /// y 方向的缩放与位置一律保持原值（地面高度是关卡决策，工具只动水平面）。
        /// </summary>
        /// <param name="groundObject">目标地面对象（Ground 层 + 内置 Plane 网格）。</param>
        /// <param name="requiredBounds">战场需要的水平范围。</param>
        /// <param name="notes">本次写入的描述，供日志核对。</param>
        private static void ResizePlaneGround(GameObject groundObject, Bounds requiredBounds, List<string> notes)
        {
            Transform groundTransform = groundObject.transform;

            // 先登记撤销：变换属于场景资产改动，必须能用一次 Ctrl+Z 回退。
            Undo.RecordObject(groundTransform, "调整地面为桥梁矩形");

            Vector3 originalScale = groundTransform.localScale;

            // 两个轴各自取 max(需求, 出厂下限)：MinGroundSize 保证地面不会被算得比出厂 10×10 还小。
            float targetSizeX = Mathf.Max(requiredBounds.size.x, MinGroundSize);
            float targetSizeZ = Mathf.Max(requiredBounds.size.z, MinGroundSize);

            groundTransform.localScale = new Vector3(
                targetSizeX / PlaneMeshSize, originalScale.y, targetSizeZ / PlaneMeshSize);

            // 水平居中到需求范围中心，y 保持原值。
            Vector3 position = groundTransform.position;
            position.x = requiredBounds.center.x;
            position.z = requiredBounds.center.z;
            groundTransform.position = position;

            float aspect = targetSizeZ > 0f ? targetSizeX / targetSizeZ : 0f;

            notes.Add(
                $"{groundObject.name} 调整为桥梁矩形 {targetSizeX:F0}×{targetSizeZ:F0}" +
                $"（长宽比 {aspect:F1}:1），水平中心 ({position.x:F1}, {position.z:F1})");
        }

        /// <summary>
        /// 取某块地面对象【自身】的水平包围盒；它没有碰撞体时退回传入的兜底包围盒。
        ///
        /// 【为什么不用"全部 Ground 层碰撞体的并集"来判定尺寸】并集把场景里其它 Ground 对象也算进来，
        /// 只要多出一块地面，尺寸就永远与需求对不上，每次执行都会重复缩放（幂等被破坏）。
        /// 并集仍然用于"报告地面实际覆盖范围"与"烘焙范围"这两件真正需要全局视野的事。
        /// </summary>
        /// <param name="groundObject">地面对象。</param>
        /// <param name="fallback">该对象没有碰撞体时使用的兜底包围盒。</param>
        private static Bounds ResolveGroundObjectBounds(GameObject groundObject, Bounds fallback)
        {
            Collider collider = groundObject.GetComponent<Collider>();
            return collider != null ? collider.bounds : fallback;
        }

        /// <summary>
        /// 判断地面是否已经是目标尺寸与目标中心（容差 0.1 米）。
        ///
        /// 【为什么不再用"覆盖判断"】阶段五的策略是「只放大不缩小 + 只要覆盖就跳过」，
        /// 那对一块正方形地面是安全的；但桥梁地形要求把一个轴【收窄】（Z 从 38 米收到 24 米），
        /// "覆盖即跳过"会让地面永远停在 38 米宽。改成"尺寸与中心是否已等于目标值"之后，
        /// 重复执行仍然完全幂等：第二次执行时尺寸已经相等，不做任何改动。
        /// </summary>
        /// <param name="groundBounds">地面自身当前的水平包围盒。</param>
        /// <param name="requiredBounds">战场需要的水平范围。</param>
        /// <returns>已达标返回 true。</returns>
        private static bool MatchesRequiredBounds(Bounds groundBounds, Bounds requiredBounds)
        {
            const float tolerance = 0.1f;

            return Mathf.Abs(groundBounds.size.x - requiredBounds.size.x) <= tolerance &&
                   Mathf.Abs(groundBounds.size.z - requiredBounds.size.z) <= tolerance &&
                   Mathf.Abs(groundBounds.center.x - requiredBounds.center.x) <= tolerance &&
                   Mathf.Abs(groundBounds.center.z - requiredBounds.center.z) <= tolerance;
        }

        /// <summary>
        /// 战场关键落点：双方出兵点、双方基地、两个路径点、英雄出生点。
        /// 抽成方法的原因：地面覆盖校验、NavMesh 覆盖校验、地面自动放大三处都要用同一份清单，
        /// 各自维护一份迟早出现「改了坐标只改一处」的漏网。
        /// </summary>
        private static Vector3[] GetKeyPoints()
        {
            CollectKeyPoints(out Vector3[] positions, out string[] _);
            return positions;
        }

        /// <summary>与 <see cref="GetKeyPoints"/> 一一对应的名字，仅用于日志定位。</summary>
        private static string[] GetKeyPointLabels()
        {
            CollectKeyPoints(out Vector3[] _, out string[] labels);
            return labels;
        }

        /// <summary>
        /// 构建战场关键落点及其名字。两份清单在【同一次遍历】里成对追加，因此不可能错位——
        /// 这是"改了坐标却忘了改标签"这类问题的结构性防线（阶段八的落点从 7 个涨到 30 个，
        /// 再靠两处手写数组对齐迟早出错）。
        ///
        /// 覆盖范围：双方出兵点 / 基地 / 各 3 座防御塔 / 各一条兵线的全部路径点 / 各 5 个英雄出生点。
        /// 这些点同时被三处消费：地面尺寸计算、地面覆盖校验、NavMesh 覆盖校验。
        /// </summary>
        /// <param name="positions">输出：关键落点坐标。</param>
        /// <param name="labels">输出：与坐标同序的名字（用于日志定位）。</param>
        private static void CollectKeyPoints(out Vector3[] positions, out string[] labels)
        {
            List<Vector3> positionList = new List<Vector3>();
            List<string> labelList = new List<string>();

            AppendKeyPoint(positionList, labelList, BlueSpawnerPosition, BlueSpawnerName);
            AppendKeyPoint(positionList, labelList, BlueBasePosition, BlueBaseName);
            AppendKeyPoint(positionList, labelList, RedSpawnerPosition, RedSpawnerName);
            AppendKeyPoint(positionList, labelList, RedBasePosition, RedBaseName);

            for (int i = 0; i < BlueTowerPositions.Length; i++)
            {
                AppendKeyPoint(positionList, labelList, BlueTowerPositions[i],
                    $"{BlueTowersRootName}/{BuildTowerObjectName(BlueTowerNamePrefix, i)}");
            }

            for (int i = 0; i < RedTowerPositions.Length; i++)
            {
                AppendKeyPoint(positionList, labelList, RedTowerPositions[i],
                    $"{RedTowersRootName}/{BuildTowerObjectName(RedTowerNamePrefix, i)}");
            }

            for (int i = 0; i < BlueLaneWaypointPositions.Length; i++)
            {
                AppendKeyPoint(positionList, labelList, BlueLaneWaypointPositions[i],
                    $"{BlueLaneName}/Waypoint{i + 1}");
            }

            for (int i = 0; i < RedLaneWaypointPositions.Length; i++)
            {
                AppendKeyPoint(positionList, labelList, RedLaneWaypointPositions[i],
                    $"{RedLaneName}/Waypoint{i + 1}");
            }

            // 阶段八第二步新增的英雄推进线同样纳入关键落点清单：
            // 它们也是"必须落在 NavMesh 上"的坐标，漏掉的话"英雄线被烘到网格外"不会有任何提示，
            // 而症状（AI 英雄站在原地不动）看起来完全不像坐标问题。
            for (int i = 0; i < BlueHeroLaneWaypointPositions.Length; i++)
            {
                AppendKeyPoint(positionList, labelList, BlueHeroLaneWaypointPositions[i],
                    $"{BlueHeroLaneName}/Waypoint{i + 1}");
            }

            for (int i = 0; i < RedHeroLaneWaypointPositions.Length; i++)
            {
                AppendKeyPoint(positionList, labelList, RedHeroLaneWaypointPositions[i],
                    $"{RedHeroLaneName}/Waypoint{i + 1}");
            }

            // 出生点数量与各方的实际编制一致（蓝方 = 玩家 + AI，红方 = AI），
            // 否则"某个英雄没有出生点"这件事不会被任何校验发现（理由见 GetHeroSpawnPositions）。
            Vector3[] blueHeroSpawns = GetHeroSpawnPositions(TeamType.Player, HeroSquadSize);
            for (int i = 0; i < blueHeroSpawns.Length; i++)
            {
                AppendKeyPoint(positionList, labelList, blueHeroSpawns[i],
                    $"{BlueHeroesRootName}/{BlueHeroInstancePrefix}{i}");
            }

            Vector3[] redHeroSpawns = GetHeroSpawnPositions(TeamType.Enemy, RedAiHeroCount);
            for (int i = 0; i < redHeroSpawns.Length; i++)
            {
                AppendKeyPoint(positionList, labelList, redHeroSpawns[i],
                    $"{RedHeroesRootName}/{RedHeroInstancePrefix}{i}");
            }

            positions = positionList.ToArray();
            labels = labelList.ToArray();
        }

        /// <summary>把一组坐标与名字成对追加进两份清单。抽出来是为了让"新增一个落点"只写一行。</summary>
        /// <param name="positions">坐标清单。</param>
        /// <param name="labels">名字清单。</param>
        /// <param name="position">落点坐标。</param>
        /// <param name="label">该落点的可读名字。</param>
        private static void AppendKeyPoint(
            List<Vector3> positions, List<string> labels, Vector3 position, string label)
        {
            positions.Add(position);
            labels.Add(label);
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
        /// （HeroStats 又被 IsDedicatedAsset 排除），因此：
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

        #region 步骤 13：技能资产与属性资产补缺（阶段六 Q/W → 阶段八 QWER）

        /// <summary>
        /// 确保 Q 技能资产存在（阶段八：盖伦式「致命打击」= 自身加速 + 强化下一次普攻）。
        ///
        /// 【阶段八第二步的一次性迁移，这是本方法唯一复杂的地方】
        /// 阶段六的 Q 是一发指向性火球（castType = UnitTarget），而阶段八的 Q 是自身施法
        /// （castType = Self）。工具对已存在资产的既有策略是"一律沿用、绝不覆盖"，
        /// 若照搬这条，仓库里那份旧火球配置会被原样保留 —— 玩家按 Q 打出来的仍然是火球，
        /// 与"准确还原盖伦机制"的要求直接冲突，而且不会报任何错。
        ///
        /// 因此这里加一次【结构化的迁移】：以 castType 为分界标记（阶段八的 Q/W 都是 Self 施法，
        /// 阶段六的 Q/W 分别是 UnitTarget / GroundPoint），
        ///   · 旧资产（castType != Self）→ 整体改写为阶段八配置，并在日志里明确报出"这是一次迁移"；
        ///   · 新资产（castType == Self）→ 走原来的"已存在一律沿用"，策划的调参不会被抹掉。
        /// 用结构字段而不是技能名做标记：名字是人会改的，castType 是机制的一部分。
        /// 迁移只可能发生一次（改写后 castType 就是 Self 了），因此幂等性不受影响。
        /// </summary>
        private static SkillData EnsureHeroSkillQAsset(List<string> notes)
        {
            SkillData existing = AssetDatabase.LoadAssetAtPath<SkillData>(HeroSkillQAssetPath);

            if (existing != null)
            {
                if (existing.CastType != SkillCastType.Self)
                {
                    ApplyHeroSkillQConfig(existing);

                    EditorUtility.SetDirty(existing);
                    AssetDatabase.SaveAssets();

                    notes.Add(
                        $"{HeroSkillQAssetPath} 从阶段六的指向性火球【迁移】为阶段八的自身强化" +
                        "（castType 由 UnitTarget 改为 Self，数值整体改写；此后重复组装不再覆盖）");
                    return existing;
                }

                notes.Add($"{HeroSkillQAssetPath} 已存在，沿用现有数值（不覆盖）");

                // Q 是"作用于自身"的技能，范围字段对它完全惰性 —— 顺手归一化掉，
                // 免得 Inspector 上显示"致命打击：持续 4 秒、半径 3.5 米"（见方法注释）。
                NormalizeSelfSkillRangeFields(existing, HeroSkillQAssetPath, notes);
                return existing;
            }

            SkillData created = CreateSkillAsset(HeroSkillQAssetPath);
            if (created == null)
            {
                return null;
            }

            ApplyHeroSkillQConfig(created);

            EditorUtility.SetDirty(created);
            AssetDatabase.SaveAssets();

            notes.Add(
                $"新建 {HeroSkillQAssetPath}（自身施法：加速 {SkillQHastePercent * 100f:F0}% / " +
                $"{SkillQHasteDuration}s + 强化下次普攻 +{SkillQEmpowerBonus:F0}（命中沉默 " +
                $"{SkillQSilenceDuration:F1}s）/ CD {SkillQCooldown}s / 蓝耗 {SkillQManaCost}）");
            return created;
        }

        /// <summary>
        /// 把阶段八「致命打击」的完整配置写进资产。
        /// 抽成方法是为了让"新建"与"迁移"两条路径共用同一份字段清单 ——
        /// 两处各写一遍必然会漏掉某个字段，而漏掉的那个不会有任何提示。
        /// </summary>
        /// <param name="target">目标技能资产。</param>
        private static void ApplyHeroSkillQConfig(SkillData target)
        {
            AssignEnum(target, "slot", (int)SkillSlot.Q);
            AssignString(target, "displayName", "致命打击");
            AssignString(target, "description",
                "立刻提升自身移动速度，并强化下一次普攻：命中时造成额外伤害并沉默目标，命中即消耗。");
            AssignEnum(target, "castType", (int)SkillCastType.Self);

            // Self 施法没有目标也没有落点，射程字段不参与校验；显式写 0 让"这个技能不需要距离"一眼可见。
            AssignFloat(target, "castRange", 0f);
            AssignFloat(target, "castTime", SkillQCastTime);
            AssignFloat(target, "cooldown", SkillQCooldown);
            AssignFloat(target, "manaCost", SkillQManaCost);

            // 阶段六遗留的弹道字段必须显式清零：castType 已经是 Self，HasProjectile 恒为 false，
            // 但资产里留着 14 的弹速会让日后有人把它改回 UnitTarget 时"顺手"得到一发火球。
            AssignFloat(target, "projectileSpeed", 0f);

            // ---- Self 施法的两条路径必须由显式开关指定，且范围字段要清零 ----
            // 【这不是"顺手清理"，而是修一个真 Bug】分派原先读的是 areaDuration > 0，
            // 而它的默认值是 4 —— 于是 Q 会被判成"生成范围场"，按 Q 不给自己加 buff，
            // 反而在脚下生成一个对敌人施加"强化普攻"的怪圈（不报任何错）。
            // 现在改用 spawnZoneAtSelf；同时把 area/tick/radius 清零，
            // 免得 Inspector 里留下"这个自身增益技能有 4 秒持续、3.5 米半径"的误导性数字。
            AssignBool(target, "spawnZoneAtSelf", false);
            AssignBool(target, "followCaster", false);
            AssignFloat(target, "areaDuration", 0f);
            AssignFloat(target, "tickInterval", 0f);
            AssignFloat(target, "effectRadius", 0f);

            AssignEnum(target, "effectType", (int)SkillEffectType.EmpowerNextAttack);
            AssignFloat(target, "empowerBonusDamage", SkillQEmpowerBonus);
            AssignFloat(target, "empowerDuration", SkillQEmpowerDuration);
            AssignFloat(target, "silenceDuration", SkillQSilenceDuration);

            // 附带效果 = 加速。这正是"主效果 + 附带效果"这条通道存在的理由：
            // 盖伦的 Q 本身就是"加速 + 强化普攻"两件事，单主效果模型无法表达。
            AssignEnum(target, "secondaryEffectType", (int)SkillEffectType.Haste);
            AssignFloat(target, "secondaryValue", SkillQHastePercent);
            AssignFloat(target, "secondaryDuration", SkillQHasteDuration);

            AssignString(target, "castVfxId", "SkillQ_Cast");
            AssignString(target, "hitVfxId", "SkillQ_Empower");
            AssignString(target, "releaseVfxId", "SkillQ_Ready");
        }

        /// <summary>
        /// 确保 W 技能资产存在（阶段八：盖伦式「勇气」= 立即获得护盾）。
        /// 迁移策略与 Q 完全一致（分界标记同为 castType：阶段六的 W 是 GroundPoint 减速圈）。
        /// </summary>
        private static SkillData EnsureHeroSkillWAsset(List<string> notes)
        {
            SkillData existing = AssetDatabase.LoadAssetAtPath<SkillData>(HeroSkillWAssetPath);

            if (existing != null)
            {
                if (existing.CastType != SkillCastType.Self)
                {
                    ApplyHeroSkillWConfig(existing);

                    EditorUtility.SetDirty(existing);
                    AssetDatabase.SaveAssets();

                    notes.Add(
                        $"{HeroSkillWAssetPath} 从阶段六的非指向性减速圈【迁移】为阶段八的自身护盾" +
                        "（castType 由 GroundPoint 改为 Self，数值整体改写；此后重复组装不再覆盖）");
                    return existing;
                }

                notes.Add($"{HeroSkillWAssetPath} 已存在，沿用现有数值（不覆盖）");

                // 与 Q 同一处理（见方法注释）。
                NormalizeSelfSkillRangeFields(existing, HeroSkillWAssetPath, notes);
                return existing;
            }

            SkillData created = CreateSkillAsset(HeroSkillWAssetPath);
            if (created == null)
            {
                return null;
            }

            ApplyHeroSkillWConfig(created);

            EditorUtility.SetDirty(created);
            AssetDatabase.SaveAssets();

            notes.Add(
                $"新建 {HeroSkillWAssetPath}（自身施法：护盾 {SkillWShieldValue:F0} / {SkillWShieldDuration}s / " +
                $"CD {SkillWCooldown}s / 蓝耗 {SkillWManaCost}）");
            return created;
        }

        /// <summary>把阶段八「勇气」的完整配置写进资产。理由同 ApplyHeroSkillQConfig。</summary>
        /// <param name="target">目标技能资产。</param>
        private static void ApplyHeroSkillWConfig(SkillData target)
        {
            AssignEnum(target, "slot", (int)SkillSlot.W);
            AssignString(target, "displayName", "勇气");
            AssignString(target, "description", "立刻获得一层护盾，优先吸收伤害，耗尽或到期后移除。");
            AssignEnum(target, "castType", (int)SkillCastType.Self);
            AssignFloat(target, "castRange", 0f);
            AssignFloat(target, "castTime", SkillWCastTime);
            AssignFloat(target, "cooldown", SkillWCooldown);
            AssignFloat(target, "manaCost", SkillWManaCost);

            // 与 Q 同一处理：W 是"直接把护盾作用于自身"，不是范围场。
            AssignBool(target, "spawnZoneAtSelf", false);
            AssignBool(target, "followCaster", false);
            AssignFloat(target, "areaDuration", 0f);
            AssignFloat(target, "tickInterval", 0f);
            AssignFloat(target, "effectRadius", 0f);

            AssignEnum(target, "effectType", (int)SkillEffectType.Shield);
            AssignFloat(target, "shieldValue", SkillWShieldValue);
            AssignFloat(target, "shieldDuration", SkillWShieldDuration);
            AssignString(target, "castVfxId", "SkillW_Cast");
            AssignString(target, "hitVfxId", "SkillW_Shield");
            AssignString(target, "releaseVfxId", "SkillW_Barrier");
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

        #endregion

        #region 步骤 13b：玩家 QWER 后两个槽位 + AI 技能池（阶段八第二步）

        /// <summary>
        /// 确保 E 技能资产存在（盖伦式「审判」：自身施法 + 跟随自身的持续 AOE）。
        ///
        /// 【两条落地路径由 spawnZoneAtSelf 分派，不是 areaDuration】见 SkillData.SpawnZoneAtSelf 的说明：
        /// 用 areaDuration 反推会让"没显式清零"的 Self 技能全被误判成范围场（实机已踩）。
        /// </summary>
        private static SkillData EnsureHeroSkillEAsset(List<string> notes)
        {
            SkillData existing = AssetDatabase.LoadAssetAtPath<SkillData>(HeroSkillEAssetPath);
            if (existing != null)
            {
                notes.Add($"{HeroSkillEAssetPath} 已存在，沿用现有数值（不覆盖）");

                // 一次性自愈：E 的资产可能是在 spawnZoneAtSelf 这个字段出现之前落盘的
                // （它由本工具生成、且工具的既有策略是"已存在一律沿用"）。
                // 不补这一步，E 会从"跟随自身的持续 AOE"退化成"对自己结算一次伤害"，且不报任何错。
                EnsureSelfZoneFlag(existing, HeroSkillEAssetPath, notes);
                return existing;
            }

            SkillData created = CreateSkillAsset(HeroSkillEAssetPath);
            if (created == null)
            {
                return null;
            }

            AssignEnum(created, "slot", (int)SkillSlot.E);
            AssignString(created, "displayName", "审判");
            AssignString(created, "description",
                "以自身为中心掀起持续的范围伤害，并跟随自己移动，周期性对范围内敌方单位结算伤害。");
            AssignEnum(created, "castType", (int)SkillCastType.Self);
            AssignFloat(created, "castRange", 0f);
            AssignFloat(created, "castTime", SkillECastTime);
            AssignFloat(created, "cooldown", SkillECooldown);
            AssignFloat(created, "manaCost", SkillEManaCost);
            AssignFloat(created, "effectRadius", SkillEEffectRadius);
            AssignFloat(created, "areaDuration", SkillEAreaDuration);
            AssignFloat(created, "tickInterval", SkillETickInterval);

            // 这两行就是"E 与 Q/W 的全部区别"：生成范围场 + 场跟随施法者。
            AssignBool(created, "spawnZoneAtSelf", true);
            AssignBool(created, "followCaster", true);

            AssignEnum(created, "effectType", (int)SkillEffectType.Damage);
            AssignFloat(created, "damage", SkillETickDamage);
            AssignString(created, "castVfxId", "SkillE_Cast");
            AssignString(created, "hitVfxId", "SkillE_Tick");
            AssignString(created, "releaseVfxId", "SkillE_Zone");

            EditorUtility.SetDirty(created);
            AssetDatabase.SaveAssets();

            notes.Add(
                $"新建 {HeroSkillEAssetPath}（自身施法 + 跟随 AOE：前摇 {SkillECastTime}s / CD {SkillECooldown}s / " +
                $"蓝耗 {SkillEManaCost} / 半径 {SkillEEffectRadius} / 持续 {SkillEAreaDuration}s / " +
                $"每 {SkillETickInterval}s 结算 {SkillETickDamage} 点 / spawnZoneAtSelf + followCaster = true）");
            return created;
        }

        /// <summary>
        /// 一次性自愈：给"跟随自身的范围场"技能补上 <c>spawnZoneAtSelf = true</c>（必要时一并修复范围设计值）。
        ///
        /// 【为什么需要它】这个字段是本轮为修一个真 Bug 才引入的显式意图开关
        /// （原先用 areaDuration &gt; 0 反推，而 areaDuration 的默认值是 4 —— 于是 Q / W / 护盾术 / 疾行术
        /// 全被误判成范围场，按下去不给自己加 buff，反而在脚下生成一个对敌人生效的怪圈）。
        /// 但 E 的资产在该字段出现【之前】就已经落盘，而工具对已存在资产的策略是"一律沿用" ——
        /// 不补这一步，E 会退化成"对自己结算一次伤害"，而且不报任何错。
        ///
        /// 【判据为什么不能再依赖 followCaster（实机踩到）】
        /// 这里原本的判据是「followCaster = true 且 spawnZoneAtSelf = false」，理由是
        /// followCaster 只在"生成范围场"这条路径上有意义。但 `SkillData.OnValidate` 曾经会在
        /// 【脚本加载时】把"Self 且 spawnZoneAtSelf 为假"的资产范围字段就地清零（含 followCaster）——
        /// 于是判据在自己的旁证被抹掉之后永远不成立，这段自愈一次都没执行过，
        /// E 一直退化成"对自己结算一次伤害"（实机症状：按 E 自己掉 18 点血，Console 全静音）。
        /// 现在改成【显式身份判据】：本方法只服务 E 资产，而 E 的定义就是"以自身为中心、跟随自己的
        /// 持续 AOE"（README §2.1.6），因此只要开关是假的就必须补上 —— 不依赖任何会被别处改写的旁证。
        /// （OnValidate 那一段清零也已删除，见 SkillData.OnValidate 的说明。）
        ///
        /// 【为什么还要顺手修复范围设计值】上面那次清零发生在【内存】里，而本方法补完开关后会
        /// `SaveAssets` —— 保存会把内存里的当前值一并落盘。若不修复，被清零的
        /// effectRadius / areaDuration / tickInterval 会被真正写进资产，E 就变成一个半径 0（被兜到 1）、
        /// 不结算的废场。因此这里对"小于等于 0"的项按设计常量补回，已有正常值的项保持不动。
        ///
        /// 迁移只可能发生一次（补完后 spawnZoneAtSelf 即为 true），幂等性不受影响。
        /// </summary>
        /// <param name="asset">目标技能资产。</param>
        /// <param name="assetPath">资产路径（仅用于日志）。</param>
        /// <param name="notes">组装说明收集器。</param>
        /// <returns>确实补写了返回 true。</returns>
        private static bool EnsureSelfZoneFlag(SkillData asset, string assetPath, List<string> notes)
        {
            if (asset == null)
            {
                return false;
            }

            if (asset.SpawnZoneAtSelf)
            {
                // 【无声也留痕】与 NormalizeSelfSkillRangeFields 同一理由：这条自愈路径必须可被外部证实
                // （"开关已经是对的"与"这段代码压根没跑"在日志上必须能区分开）。
                notes.Add($"{assetPath} 自身范围场开关已检查（spawnZoneAtSelf = true，无需补写）");
                return false;
            }

            // 结构性字段：E 的身份就是"以自身为中心、跟随自己移动的持续 AOE"，两个开关都由工具强控。
            AssignBool(asset, "spawnZoneAtSelf", true);
            AssignBool(asset, "followCaster", true);

            List<string> repairs = new List<string>();

            if (asset.EffectRadius <= 0f)
            {
                AssignFloat(asset, "effectRadius", SkillEEffectRadius);
                repairs.Add($"effectRadius → {SkillEEffectRadius:F1}");
            }

            if (asset.AreaDuration <= 0f)
            {
                AssignFloat(asset, "areaDuration", SkillEAreaDuration);
                repairs.Add($"areaDuration → {SkillEAreaDuration:F1}");
            }

            if (asset.TickInterval <= 0f)
            {
                AssignFloat(asset, "tickInterval", SkillETickInterval);
                repairs.Add($"tickInterval → {SkillETickInterval:F2}");
            }

            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();

            notes.Add(
                $"{assetPath} 补写 spawnZoneAtSelf = true + followCaster = true" +
                $"（该资产在引入此字段之前就已存在：缺开关会被判成「把主效果作用于自身」而不是生成跟随范围场，" +
                $"实机症状是按 E 自己掉血）" +
                (repairs.Count > 0 ? $"；并修复了被清零的范围设计值：{string.Join("、", repairs)}" : string.Empty));
            return true;
        }

        /// <summary>
        /// 确保 R 技能资产存在（盖伦式「德玛西亚正义」：指向性斩杀）。
        ///
        /// 【为什么 projectileSpeed 必须显式写 0】它是"是否有弹道"的唯一开关
        /// （SkillData.HasProjectile = UnitTarget &amp;&amp; projectileSpeed &gt; 0）。
        /// 而字段默认值是 14 —— 不写 0 的话，新建出来的 R 会变成一发飞行火球，
        /// 与"即时的指向性爆发（斩杀）"的定位完全不符，且不会有任何报错。
        /// </summary>
        private static SkillData EnsureHeroSkillRAsset(List<string> notes)
        {
            SkillData existing = AssetDatabase.LoadAssetAtPath<SkillData>(HeroSkillRAssetPath);
            if (existing != null)
            {
                notes.Add($"{HeroSkillRAssetPath} 已存在，沿用现有数值（不覆盖）");
                return existing;
            }

            SkillData created = CreateSkillAsset(HeroSkillRAssetPath);
            if (created == null)
            {
                return null;
            }

            AssignEnum(created, "slot", (int)SkillSlot.R);
            AssignString(created, "displayName", "德玛西亚正义");
            AssignString(created, "description",
                "锁定一名敌方单位并立刻结算：伤害 = 基础伤害 + 系数 × 目标已损失生命值，目标越残越致命。");
            AssignEnum(created, "castType", (int)SkillCastType.UnitTarget);
            AssignFloat(created, "castRange", SkillRCastRange);
            AssignFloat(created, "castTime", SkillRCastTime);
            AssignFloat(created, "cooldown", SkillRCooldown);
            AssignFloat(created, "manaCost", SkillRManaCost);
            AssignFloat(created, "projectileSpeed", 0f);
            AssignEnum(created, "effectType", (int)SkillEffectType.ExecuteDamage);
            AssignFloat(created, "damage", SkillRBaseDamage);
            AssignFloat(created, "executeHealthRatio", SkillRExecuteHealthRatio);
            AssignString(created, "castVfxId", "SkillR_Cast");
            AssignString(created, "hitVfxId", "SkillR_Hit");
            AssignString(created, "releaseVfxId", "SkillR_Execute");

            EditorUtility.SetDirty(created);
            AssetDatabase.SaveAssets();

            notes.Add(
                $"新建 {HeroSkillRAssetPath}（指向性斩杀：射程 {SkillRCastRange} / 前摇 {SkillRCastTime}s / " +
                $"CD {SkillRCooldown}s / 蓝耗 {SkillRManaCost} / 基础伤害 {SkillRBaseDamage} + " +
                $"系数 {SkillRExecuteHealthRatio:F2} × 已损失生命值 / 无弹道即时结算）");
            return created;
        }

        /// <summary>
        /// 确保技能池资产齐备，并返回按固定顺序排列的池子（本轮：10 个截然不同的基础技能）。
        ///
        /// 【池子与玩家 QWER 的关系】两者刻意没有任何引用关系：玩家四技能是"盖伦的机制"，
        /// 池内技能是"验证技能架构泛用性的一批通用技能"，**只有玩家操控的 PlayerHero 使用前者**。
        /// 9 个 AI 英雄从池内各抽 4 个挂到 Q/W/E/R，因此"改池内某个技能的参数 → AI 行为随之变化，
        /// 无需改代码"这条验收项才成立。
        ///
        /// 【为什么是 10 个】池子要覆盖现有 SkillEffectType × SkillCastType 的主要组合，
        /// 让 4 槽抽取的组合足够有辨识度（ARAM 的乐趣来自"这个英雄居然会治疗"这种错配）。
        /// 10 个技能按机制分列：
        /// <list type="number">
        ///   <item>火球 —— 指向性弹道伤害（最基础的一档）；</item>
        ///   <item>寒霜领域 —— 落点持续减速圈；</item>
        ///   <item>治疗术 —— 指向性友方治疗；</item>
        ///   <item>眩晕弹 —— 指向性弹道控制 + 附带伤害（主效果 + 附带效果通道）；</item>
        ///   <item>冲击波 —— 落点一次性爆发伤害；</item>
        ///   <item>护盾术 —— 自身施法护盾（直接作用于自身）；</item>
        ///   <item>疾行术 —— 自身施法加速；</item>
        ///   <item>处决 —— 指向性斩杀（按已损失生命值追加）；</item>
        ///   <item>自爆冲击 —— 自身为中心的即时范围伤害（spawnZoneAtSelf 的伤害型）；</item>
        ///   <item>远程狙击 —— 14 米超远射程弹道（远程消耗）。</item>
        /// </list>
        /// 架构师示例里的"前方扇形顺劈 / 群体治疗"在 V1 的效果模型下无法表达
        /// （范围场是圆形且只结算敌方，没有扇形与友方 AOE 这两种形状），
        /// 因此用机制等价且可验证的条目替代 —— 见 §阶段八文档里的说明。
        ///
        /// 【为什么已存在的资产一律沿用】池资产同样是策划数据：他们可以在 Inspector 里
        /// 调伤害 / CD / 半径，而工具每次组装都覆盖会把这些调参抹掉（与 HeroSkillQ 同一策略）。
        /// 数值只在"资产缺失、需要新建"的那一次写入。
        /// </summary>
        /// <param name="notes">组装说明收集器。</param>
        /// <returns>池内技能（顺序固定，即分配算法使用下标空间的依据）。</returns>
        private static List<SkillData> EnsureSkillPoolAssets(List<string> notes)
        {
            List<SkillData> pool = new List<SkillData>();

            if (!AssetDatabase.IsValidFolder(SkillsFolder))
            {
                Debug.LogError(
                    $"[AutoSceneBuilder] 目录 {SkillsFolder} 不存在，无法创建技能池，" +
                    "9 个 AI 英雄将没有技能可放。");
                return pool;
            }

            if (!AssetDatabase.IsValidFolder(SkillPoolFolder))
            {
                AssetDatabase.CreateFolder(SkillsFolder, "Pool");
            }

            // ---- 1. 火球：指向性弹道伤害（最基础的一档） ----
            SkillData fireball = EnsurePoolSkill(
                "PoolFireball", "火球", "锁定一名敌方单位发射火球，命中时造成伤害。", notes);
            if (fireball != null)
            {
                AssignEnum(fireball, "castType", (int)SkillCastType.UnitTarget);
                AssignFloat(fireball, "castRange", 8f);
                AssignFloat(fireball, "castTime", 0.25f);
                AssignFloat(fireball, "cooldown", 6f);
                AssignFloat(fireball, "manaCost", 25f);
                AssignFloat(fireball, "projectileSpeed", 14f);
                AssignFloat(fireball, "projectileRadius", 0.5f);
                AssignInt(fireball, "maxHitCount", 1);
                AssignFloat(fireball, "maxTravelDistance", 12f);
                AssignEnum(fireball, "effectType", (int)SkillEffectType.Damage);
                AssignFloat(fireball, "damage", 90f);
                pool.Add(fireball);
            }

            // ---- 2. 减速圈：非指向性持续 AOE（与玩家 W 同范式，验证"同一套效果可被多个技能复用"） ----
            SkillData slowZone = EnsurePoolSkill(
                "PoolSlowZone", "寒霜领域", "在指定落点生成持续范围效果，周期性减速范围内敌方单位。", notes);
            if (slowZone != null)
            {
                AssignEnum(slowZone, "castType", (int)SkillCastType.GroundPoint);
                AssignFloat(slowZone, "castRange", 9f);
                AssignFloat(slowZone, "castTime", 0.2f);
                AssignFloat(slowZone, "cooldown", 10f);
                AssignFloat(slowZone, "manaCost", 30f);
                AssignFloat(slowZone, "effectRadius", 3.5f);
                AssignFloat(slowZone, "areaDuration", 4f);
                AssignFloat(slowZone, "tickInterval", 0.5f);
                AssignEnum(slowZone, "effectType", (int)SkillEffectType.Slow);
                AssignFloat(slowZone, "slowPercent", 0.4f);
                AssignFloat(slowZone, "slowDuration", 0.6f);
                pool.Add(slowZone);
            }

            // ---- 3. 治疗术：指向性【友方】（HealthComponent.Heal 的首个消费方） ----
            SkillData heal = EnsurePoolSkill(
                "PoolHeal", "治疗术", "为一名友方单位恢复生命（血量不会超过上限）。", notes);
            if (heal != null)
            {
                AssignEnum(heal, "castType", (int)SkillCastType.UnitTarget);
                AssignBool(heal, "targetsAlly", true);
                AssignFloat(heal, "castRange", 9f);
                AssignFloat(heal, "castTime", 0.3f);
                AssignFloat(heal, "cooldown", 12f);
                AssignFloat(heal, "manaCost", 40f);
                AssignEnum(heal, "effectType", (int)SkillEffectType.Heal);
                AssignFloat(heal, "healAmount", 120f);
                pool.Add(heal);
            }

            // ---- 4. 眩晕弹：指向性弹道 + 眩晕（顺带验证"主效果 + 附带效果"这条通道） ----
            SkillData stunBolt = EnsurePoolSkill(
                "PoolStunBolt", "眩晕弹", "锁定一名敌方单位发射弹丸，命中时造成少量伤害并眩晕。", notes);
            if (stunBolt != null)
            {
                AssignEnum(stunBolt, "castType", (int)SkillCastType.UnitTarget);
                AssignFloat(stunBolt, "castRange", 8f);
                AssignFloat(stunBolt, "castTime", 0.2f);
                AssignFloat(stunBolt, "cooldown", 12f);
                AssignFloat(stunBolt, "manaCost", 45f);
                AssignFloat(stunBolt, "projectileSpeed", 16f);
                AssignFloat(stunBolt, "projectileRadius", 0.5f);
                AssignInt(stunBolt, "maxHitCount", 1);
                AssignFloat(stunBolt, "maxTravelDistance", 12f);
                AssignEnum(stunBolt, "effectType", (int)SkillEffectType.Stun);
                AssignFloat(stunBolt, "stunDuration", 1.2f);

                // 主效果是眩晕，但"只控不打"的弹道会显得很弱，因此用附带效果补一份伤害 ——
                // 这条通道本身也是阶段八新增的能力（见 SkillData.SecondaryEffectType）。
                AssignEnum(stunBolt, "secondaryEffectType", (int)SkillEffectType.Damage);
                AssignFloat(stunBolt, "secondaryValue", 40f);
                pool.Add(stunBolt);
            }

            // ---- 5. 冲击波：非指向性一次性爆发（areaDuration = 0 → 生成即结算并回收） ----
            SkillData shockwave = EnsurePoolSkill(
                "PoolShockwave", "冲击波", "在指定落点引爆一次范围伤害。", notes);
            if (shockwave != null)
            {
                AssignEnum(shockwave, "castType", (int)SkillCastType.GroundPoint);
                AssignFloat(shockwave, "castRange", 7f);
                AssignFloat(shockwave, "castTime", 0.3f);
                AssignFloat(shockwave, "cooldown", 9f);
                AssignFloat(shockwave, "manaCost", 35f);
                AssignFloat(shockwave, "effectRadius", 2.8f);
                AssignFloat(shockwave, "areaDuration", 0f);
                AssignFloat(shockwave, "tickInterval", 0f);
                AssignEnum(shockwave, "effectType", (int)SkillEffectType.Damage);
                AssignFloat(shockwave, "damage", 110f);
                pool.Add(shockwave);
            }

            // ---- 6. 护盾术：自身施法（无需目标与落点） ----
            SkillData shield = EnsurePoolSkill(
                "PoolShield", "护盾术", "立即为自身套上一层护盾，吸收伤害直至耗尽或到期。", notes);
            if (shield != null)
            {
                AssignEnum(shield, "castType", (int)SkillCastType.Self);
                AssignFloat(shield, "castRange", 0f);
                AssignFloat(shield, "castTime", 0f);
                AssignFloat(shield, "cooldown", 16f);
                AssignFloat(shield, "manaCost", 40f);
                AssignEnum(shield, "effectType", (int)SkillEffectType.Shield);
                AssignFloat(shield, "shieldValue", 120f);
                AssignFloat(shield, "shieldDuration", 5f);

                // 护盾是"作用于自身"，不是范围场 —— 必须把范围字段清零（见 NormalizeSelfSkillRangeFields）。
                NormalizeSelfSkillRangeFields(shield, SkillPoolFolder + "/PoolShield.asset", notes);
                pool.Add(shield);
            }

            // ---- 7. 疾行术：自身施法 + 加速（验证 BuffType.Haste 这条独立通道） ----
            SkillData haste = EnsurePoolSkill(
                "PoolHaste", "疾行术", "立即提升自身移动速度，持续一段时间。", notes);
            if (haste != null)
            {
                AssignEnum(haste, "castType", (int)SkillCastType.Self);
                AssignFloat(haste, "castRange", 0f);
                AssignFloat(haste, "castTime", 0f);
                AssignFloat(haste, "cooldown", 14f);
                AssignFloat(haste, "manaCost", 25f);
                AssignEnum(haste, "effectType", (int)SkillEffectType.Haste);
                AssignFloat(haste, "hastePercent", 0.35f);
                AssignFloat(haste, "hasteDuration", 4f);

                NormalizeSelfSkillRangeFields(haste, SkillPoolFolder + "/PoolHaste.asset", notes);
                pool.Add(haste);
            }

            // ---- 8. 处决：指向性斩杀（与玩家 R 同效果类型，验证效果层与技能层解耦） ----
            SkillData execute = EnsurePoolSkill(
                "PoolExecute", "处决", "锁定一名敌方单位，按目标已损失生命值追加伤害。", notes);
            if (execute != null)
            {
                AssignEnum(execute, "castType", (int)SkillCastType.UnitTarget);
                AssignFloat(execute, "castRange", 6f);
                AssignFloat(execute, "castTime", 0.3f);
                AssignFloat(execute, "cooldown", 20f);
                AssignFloat(execute, "manaCost", 60f);
                AssignFloat(execute, "projectileSpeed", 0f);
                AssignEnum(execute, "effectType", (int)SkillEffectType.ExecuteDamage);
                AssignFloat(execute, "damage", 60f);
                AssignFloat(execute, "executeHealthRatio", 0.25f);
                pool.Add(execute);
            }

            // ---- 9. 自爆冲击：自身施法 + 在【自己脚下】生成一次性范围场（本轮新增） ----
            // 与第 2 项（寒霜领域）的差别是"场在哪、活多久"：那个是"选一个落点、持续 4 秒、周期减速"，
            // 这个是"以自己为中心、生成即爆发、打完就散"。也是 spawnZoneAtSelf 这条路径上
            // 【伤害型】的首个消费方（此前的 E 审判是持续型）。
            SkillData selfNova = EnsurePoolSkill(
                "PoolSelfNova", "自爆冲击", "以自身为中心引爆一次范围伤害，适合被近身围攻时反打。", notes);
            if (selfNova != null)
            {
                AssignEnum(selfNova, "castType", (int)SkillCastType.Self);
                AssignFloat(selfNova, "castRange", 0f);
                AssignFloat(selfNova, "castTime", 0.35f);
                AssignFloat(selfNova, "cooldown", 11f);
                AssignFloat(selfNova, "manaCost", 40f);
                AssignFloat(selfNova, "effectRadius", 3.2f);

                // 【这两个字段必须显式写 0】areaDuration = 0 → 生成即结算一次并立刻回收（一次性爆发）；
                // tickInterval = 0 只是让语义一致（一次性爆发不看它）。若不写，
                // areaDuration 会取字段默认值 4 → 变成"持续 4 秒、每 0.5 秒炸一次"的伤害圈，
                // 强度与设计意图完全不同，而且不会有任何报错。
                AssignFloat(selfNova, "areaDuration", 0f);
                AssignFloat(selfNova, "tickInterval", 0f);

                // spawnZoneAtSelf 是 Self 施法走"生成范围场"还是"作用于自身"的唯一开关（显式字段，
                // 绝不能从 areaDuration 反推 —— 见 SkillData.SpawnZoneAtSelf 的踩坑记录）。
                AssignBool(selfNova, "spawnZoneAtSelf", true);

                // 一次性爆发不需要跟随：场在生成的同一帧就结算并销毁，跟随没有意义。
                AssignBool(selfNova, "followCaster", false);

                AssignEnum(selfNova, "effectType", (int)SkillEffectType.Damage);
                AssignFloat(selfNova, "damage", 85f);
                pool.Add(selfNova);
            }

            // ---- 10. 远程狙击：指向性弹道 + 超远射程（本轮新增） ----
            // 与第 1 项（火球）同为"指向性弹道伤害"，但**射程剖面完全不同**：
            // 火球 8 米 / 6 秒 CD，是贴脸对拼的主力技能；狙击 14 米 / 14 秒 CD，
            // 是"站在塔外点人"的消耗手段。V1 的效果模型是单主效果，伤害类技能之间
            // 只能靠射程 / 速度 / 数值 / CD 拉开差异 —— 这两个的差异正好覆盖"近战爆发 vs 远程消耗"两端。
            SkillData snipe = EnsurePoolSkill(
                "PoolSnipe", "远程狙击", "锁定一名远处敌方单位射出高速弹丸，射程远但冷却较长。", notes);
            if (snipe != null)
            {
                AssignEnum(snipe, "castType", (int)SkillCastType.UnitTarget);
                AssignFloat(snipe, "castRange", 14f);
                AssignFloat(snipe, "castTime", 0.4f);
                AssignFloat(snipe, "cooldown", 14f);
                AssignFloat(snipe, "manaCost", 45f);
                AssignFloat(snipe, "projectileSpeed", 22f);
                AssignFloat(snipe, "projectileRadius", 0.4f);
                AssignInt(snipe, "maxHitCount", 1);

                // 弹道最大飞行距离必须 ≥ 射程：它只在"目标不可达 / 被障碍挡住"时兜底，
                // 若小于射程，正常命中会在半路被静默回收（症状是"技能放了、飞了一半没了、目标不掉血"）。
                AssignFloat(snipe, "maxTravelDistance", 18f);

                AssignEnum(snipe, "effectType", (int)SkillEffectType.Damage);
                AssignFloat(snipe, "damage", 140f);
                pool.Add(snipe);
            }

            notes.Add($"技能池已就绪（{pool.Count} 个：{DescribeSkillPool(pool)}）");
            return pool;
        }

        /// <summary>
        /// 归一化：把"自身施法 + 不生成范围场"的技能的范围字段清零。
        ///
        /// 【为什么这不算"覆盖策划数值"】对这类技能，areaDuration / tickInterval / effectRadius
        /// 三个字段【完全惰性】—— SkillComponent.Release 走的是"把主效果作用于自身"分支，
        /// 范围场根本不会被创建，因此清零不可能改变任何行为。
        /// 它们留在资产里的唯一效果是误导：Inspector 上会显示"护盾术：持续 4 秒、半径 3.5 米"，
        /// 而这两个数字与护盾术毫无关系。
        ///
        /// 【为什么必须做这件事】SkillData 的 areaDuration 默认值是 4（地面 AOE 需要它），
        /// 于是"新建一个 Self 技能但忘了清零"会得到一个看起来一切正常、实际语义完全错乱的资产
        /// （这正是本轮修掉的那个真 Bug：Q 不给自己加 buff，反而在脚下生成对敌人生效的怪圈）。
        /// 把惰性字段抹掉，是让"这类技能没有范围概念"在数据上也一眼可见。
        /// </summary>
        /// <param name="asset">目标技能资产。</param>
        /// <param name="assetPath">资产路径（仅用于日志）。</param>
        /// <param name="notes">组装说明收集器。</param>
        /// <returns>确实发生了归一化返回 true。</returns>
        private static bool NormalizeSelfSkillRangeFields(SkillData asset, string assetPath, List<string> notes)
        {
            if (asset == null || asset.CastType != SkillCastType.Self || asset.SpawnZoneAtSelf)
            {
                return false;
            }

            // 三项都为 0 才是"已经干净"，否则写一次并把改动记进组装说明（保持幂等：第二次执行不会再有改动）。
            if (Mathf.Approximately(asset.AreaDuration, 0f) &&
                Mathf.Approximately(asset.TickInterval, 0f) &&
                Mathf.Approximately(asset.EffectRadius, 0f))
            {
                // 【无声也留痕】不写任何东西时也记一条"已检查"：
                // 这条路径此前是静默的，于是"它到底执行了没有"在实机日志里无法判断 ——
                // 上一轮就因此产生过"代码明明写了、日志里却查不到"的歧义（见 Plan 阶段八 §（11））。
                // 留痕的代价是每个自身增益技能多一句说明，换来的是"组装结果可被外部证实"。
                notes.Add($"{assetPath} 自身增益范围字段已检查（均为 0，无需归一化）");
                return false;
            }

            AssignFloat(asset, "areaDuration", 0f);
            AssignFloat(asset, "tickInterval", 0f);
            AssignFloat(asset, "effectRadius", 0f);

            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();

            notes.Add($"{assetPath} 归一化：自身增益类技能的范围字段已清零（对这类技能它们完全惰性，只是误导）");
            return true;
        }

        /// <summary>
        /// 取（必要时创建）一个技能池资产，并写入通用的标识字段。
        /// 已存在时不覆盖任何数值 —— 池内数值是策划可调的（见 EnsureSkillPoolAssets 的说明）。
        /// </summary>
        /// <param name="fileName">资产文件名（不含扩展名，落在 SkillPoolFolder 下）。</param>
        /// <param name="displayName">技能显示名。</param>
        /// <param name="description">技能描述。</param>
        /// <param name="notes">组装说明收集器。</param>
        /// <returns>可用的技能资产；创建失败返回 null。</returns>
        private static SkillData EnsurePoolSkill(
            string fileName, string displayName, string description, List<string> notes)
        {
            string assetPath = SkillPoolFolder + "/" + fileName + ".asset";

            SkillData existing = AssetDatabase.LoadAssetAtPath<SkillData>(assetPath);
            if (existing != null)
            {
                return existing;
            }

            SkillData created = CreateSkillAsset(assetPath);
            if (created == null)
            {
                return null;
            }

            AssignString(created, "displayName", displayName);
            AssignString(created, "description", description);

            EditorUtility.SetDirty(created);
            AssetDatabase.SaveAssets();

            return created;
        }

        /// <summary>把技能池拼成一行可读文本（只列名字），仅用于日志。</summary>
        private static string DescribeSkillPool(List<SkillData> pool)
        {
            if (pool == null || pool.Count == 0)
            {
                return "空";
            }

            System.Text.StringBuilder builder = new System.Text.StringBuilder();

            for (int i = 0; i < pool.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append("、");
                }

                builder.Append(pool[i] != null ? pool[i].DisplayName : "缺失");
            }

            return builder.ToString();
        }

        /// <summary>
        /// 补齐英雄属性资产里新增的【数值】字段（法力上限与回蓝），并**清空**它不该承载的技能槽。
        ///
        /// 【为什么必须有这个方法】EnsureHeroStatsAsset 的策略是"已存在就沿用、不覆盖"——
        /// 而仓库里的 HeroStats.asset 是阶段五生成的，它没有 maxMana / manaRegenPerSecond 这些字段。
        /// 若不做补缺，一键组装之后英雄会处于"没蓝条、按技能毫无反应"的状态，而且【不报任何错】——
        /// 这是最难排查的一类问题（表现为"整个技能系统都没生效"）。
        ///
        /// 【为什么是"只补空、不覆盖"而不是整体重写】
        /// 1. 策划可能已经在 Inspector 里调过法力上限（例如为了手感调到 500），覆盖会抹掉调参；
        /// 2. HeroStats.asset 被 HeroPrefab 按 guid 引用，任何"删除重建"都会让引用断链
        ///    （这是本项目在 NavMeshData 上踩过的真实事故）。本方法只改字段值、不碰资产本体，guid 恒定不变。
        ///
        /// 【为什么技能槽反过来要"清空" —— 这是实机打回后修掉的那条覆盖链】
        /// 症状：9 个 AI 英雄在实机里全在放盖伦的 QWER（大风车 / 大宝剑），而编辑器里看它们的技能槽
        /// 明明是技能池抽出来的技能。
        /// 根因：10 个英雄共用同一份 HeroStats，而它配着盖伦四件套；运行期 `EntityBase.ApplyStats`
        /// 会拿这份共享模板调 `SkillComponent.Initialize`，把【AI 实例上按技能池注入的技能槽全部改写】。
        /// 编辑期的注入是真的，被覆盖发生在 Start 之后，因此工具侧任何"读回校验"都查不出来。
        ///
        /// 因此这里把"模板侧"的技能彻底摘掉：技能的唯一承载者是 SkillComponent 本身
        /// （玩家 = 预制体直挂的盖伦四件套，由 CreateHeroPrefab 写入；AI = 按实例注入的技能池抽签）。
        /// 清空后运行期那条注入链拿到的是 4 个 null，什么也写不了 —— 覆盖链从数据层被切断。
        /// 与之配套的两道保险：`SkillComponent.Initialize` 改成"实例整体接管"（代码层，
        /// 以及 AI 英雄在 Start 时把最终生效的四个技能打进 Console（运行期证据）。
        /// </summary>
        /// <param name="heroStats">英雄属性资产。</param>
        /// <param name="notes">组装说明收集器。</param>
        private static void PatchHeroStatsGaps(EntityStatsData heroStats, List<string> notes)
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

            if (patched.Count > 0)
            {
                EditorUtility.SetDirty(heroStats);
                AssetDatabase.SaveAssets();
                notes.Add($"{HeroStatsAssetPath} 补写数值字段（只补空、不覆盖已有值）：{string.Join("；", patched)}");
            }

            ClearHeroStatsSkillFields(heroStats, notes);
        }

        /// <summary>
        /// 清空属性资产上的四个技能槽（幂等：已经是空的不写盘、不报日志）。
        ///
        /// 这四个字段在阶段六曾是"技能配置的主数据流"，阶段八实机打回后改为"**不由本资产承载**"：
        /// 只要它们里还留着盖伦四件套，运行期就会把 AI 英雄按技能池注入的技能覆盖掉（见 PatchHeroStatsGaps 的说明）。
        /// 字段本身保留（避免旧资产的序列化数据出现无谓噪音），但每次组装都会确保它们是空的 ——
        /// 若有人手工填了回来，下一次组装会清掉并留下一条说明，因此不会重新变成静默失效。
        /// </summary>
        /// <param name="heroStats">英雄属性资产。</param>
        /// <param name="notes">组装说明收集器。</param>
        private static void ClearHeroStatsSkillFields(EntityStatsData heroStats, List<string> notes)
        {
            string[] skillFields = { "skillQ", "skillW", "skillE", "skillR" };
            List<string> cleared = new List<string>();

            for (int i = 0; i < skillFields.Length; i++)
            {
                if (ClearObjectReferenceIfSet(heroStats, skillFields[i]))
                {
                    cleared.Add(skillFields[i]);
                }
            }

            if (cleared.Count == 0)
            {
                // 【无声也留痕】与 NormalizeSelfSkillRangeFields 同一约定：不写盘时也记一条"已检查"，
                // 否则"这条归一化到底跑没跑"在日志上无法证实。
                notes.Add($"{HeroStatsAssetPath} 技能槽已检查（均为空，无需清空 —— 技能由 SkillComponent 承载）");
                return;
            }

            EditorUtility.SetDirty(heroStats);
            AssetDatabase.SaveAssets();

            notes.Add(
                $"{HeroStatsAssetPath} 已清空技能字段（{string.Join("、", cleared)}）：" +
                "技能不再由 EntityStatsData 承载 —— 运行期拿共享模板注入会覆盖 AI 英雄按技能池注入的技能槽，" +
                "实机症状是「9 个 AI 全在放玩家的盖伦 QWER」。玩家技能走预制体直挂，AI 技能走按实例注入。");
        }

        /// <summary>
        /// 把对象引用字段清空（置 null）；本来就是空的时候返回 false（不产生任何修改）。
        /// 与 PatchObjectReferenceIfUnset 语义相反，专供"这个字段不该有值"的归一化使用。
        /// </summary>
        /// <param name="target">目标资产。</param>
        /// <param name="fieldName">字段名。</param>
        /// <returns>确实清空了返回 true。</returns>
        private static bool ClearObjectReferenceIfSet(UnityEngine.Object target, string fieldName)
        {
            SerializedObject serialized = new SerializedObject(target);
            SerializedProperty property = FindProperty(serialized, fieldName);

            if (property == null || property.propertyType != SerializedPropertyType.ObjectReference)
            {
                // FindProperty 已经报过"字段不存在"；类型不对属于脚本与工具不一致，同样不该静默。
                return false;
            }

            if (property.objectReferenceValue == null)
            {
                return false;
            }

            property.objectReferenceValue = null;
            serialized.ApplyModifiedProperties();
            return true;
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
            float diameter = Mathf.Max(0.1f, ProjectileVisualRadius * 2f);
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
                problems.Add("缺少 BuffComponent（减速 / 眩晕 / 护盾 / 加速 / 沉默 / 强化普攻都无处承载）");
            }

            if (heroObject.GetComponent<ProjectileSpawner>() == null)
            {
                problems.Add("缺少 ProjectileSpawner（指向性弹道无法生成）");
            }

            if (heroObject.GetComponent<PlayerSkillController>() == null)
            {
                problems.Add("缺少 PlayerSkillController（Q / W / E / R 按键无人接收）");
            }

            if (skills != null)
            {
                // 四个槽位逐个点名：漏配一个槽位的症状是"某个键按下去毫无反应"，
                // 而运行期只会打一条很容易被忽略的告警，因此这里必须把槽位号报出来。
                CheckSkillSlotInjected(problems, skills, SkillSlot.Q);
                CheckSkillSlotInjected(problems, skills, SkillSlot.W);
                CheckSkillSlotInjected(problems, skills, SkillSlot.E);
                CheckSkillSlotInjected(problems, skills, SkillSlot.R);
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
                    "[AutoSceneBuilder] 技能装配校验通过：Q / W / E / R 四槽配置、法力、状态效果容器、" +
                    "弹道生成器与技能输入层均已就位。");
                return;
            }

            Debug.LogWarning(
                "[AutoSceneBuilder] 技能装配存在以下问题（本次只提示，不改场景）：\n  · " + string.Join("\n  · ", problems));
        }

        /// <summary>
        /// 检查单个技能槽是否已注入配置，未注入时把槽位号写进问题清单。
        /// 抽出来的原因：四个槽位的检查逻辑完全一致，逐个写四遍容易在复制时把槽位号写错
        /// （那样报出来的问题会指向错误的按键，反而增加排查成本）。
        /// </summary>
        /// <param name="problems">问题清单。</param>
        /// <param name="skills">技能组件。</param>
        /// <param name="slot">待检查的槽位。</param>
        private static void CheckSkillSlotInjected(List<string> problems, SkillComponent skills, SkillSlot slot)
        {
            if (skills.GetSkillData(slot) == null)
            {
                problems.Add($"SkillComponent 的 {slot} 槽未注入技能配置（该键按下不会有任何反应）");
            }
        }

        #region 步骤 16b：编制与技能分配校验（本轮修复新增）

        /// <summary>
        /// 编制校验：**场上必须恰好 10 个英雄 —— 蓝方 1 玩家 + 4 AI、红方 5 AI**。
        ///
        /// 【为什么要在"工具刚建完"之后再数一遍】这是本轮修复的 6v5 溢出问题的验收手段。
        /// 工具自己的循环边界当然是对的（否则也建不出场景），但"循环建了 5 个"与"场景里真的只有 5 个"
        /// 是两件事：历史遗留的游离副本、上一次组装残留、手工拖进去的实例，都会让场上多出英雄，
        /// 而这类多出来的单位会正常索敌、正常推塔、正常进计分板 —— 表现为"蓝方莫名其妙 6 打 5"，
        /// Hierarchy 里却看不出是谁多出来的（与 CleanupGhostEntities 记录的那次"凭空多一个基地"同源）。
        ///
        /// 因此这里【读场景】而不是读局部变量：
        ///   ① 两个英雄根节点下各数一遍（数量 + 阵营 + 类型 + 玩家/AI 归属）；
        ///   ② 全场景再数一遍 EntityType.Hero（能抓到"挂在别处的游离英雄"）。
        /// 两条都通过，才敢说"5v5 成立"。
        /// </summary>
        private static void ValidateHeroRoster()
        {
            List<string> problems = new List<string>();

            int blueTotal = CollectHeroRoster(
                problems, BlueHeroesRootName, TeamType.Player, out int bluePlayers, out int blueAi);

            int redTotal = CollectHeroRoster(
                problems, RedHeroesRootName, TeamType.Enemy, out int redPlayers, out int redAi);

            // ---- ① 蓝方：1 玩家 + 4 AI（= HeroSquadSize）----
            if (blueTotal != HeroSquadSize)
            {
                problems.Add(
                    $"{BlueHeroesRootName} 下有 {blueTotal} 个英雄，应为 {HeroSquadSize} 个" +
                    $"（{PlayerHeroCount} 玩家 + {BlueAiHeroCount} AI）");
            }

            if (bluePlayers != PlayerHeroCount)
            {
                problems.Add(
                    $"{BlueHeroesRootName} 下有 {bluePlayers} 个玩家英雄（无 EntityAIController 的那些），" +
                    $"应为 {PlayerHeroCount} 个");
            }

            if (blueAi != BlueAiHeroCount)
            {
                problems.Add($"{BlueHeroesRootName} 下有 {blueAi} 个 AI 英雄，应为 {BlueAiHeroCount} 个");
            }

            // ---- ② 红方：5 AI，且不能有玩家英雄 ----
            if (redTotal != RedAiHeroCount)
            {
                problems.Add($"{RedHeroesRootName} 下有 {redTotal} 个英雄，应为 {RedAiHeroCount} 个（全为 AI）");
            }

            if (redAi != RedAiHeroCount)
            {
                problems.Add($"{RedHeroesRootName} 下有 {redAi} 个 AI 英雄，应为 {RedAiHeroCount} 个");
            }

            if (redPlayers > 0)
            {
                problems.Add($"{RedHeroesRootName} 下有 {redPlayers} 个没有 EntityAIController 的英雄（红方不应有玩家英雄）");
            }

            // ---- ③ 全场景总数：多出来的游离英雄在这里暴露 ----
            int sceneHeroTotal = CountSceneHeroEntities();

            if (sceneHeroTotal != TotalHeroCount)
            {
                problems.Add(
                    $"场景中 EntityType.Hero 的实体共 {sceneHeroTotal} 个，应为 {TotalHeroCount} 个" +
                    "（多出来的英雄通常来自历史遗留的游离副本，可用 CleanupGhostEntities 的清理规则排查）");
            }

            if (problems.Count == 0)
            {
                Debug.Log(
                    "[AutoSceneBuilder] 英雄编制校验通过：场上共 " +
                    $"{TotalHeroCount} 个英雄 —— 蓝方 {blueTotal} 个（{PlayerHeroCount} 玩家 + {blueAi} AI）、" +
                    $"红方 {redTotal} 个（全 AI）；无游离英雄。");
                return;
            }

            Debug.LogError(
                "[AutoSceneBuilder] 英雄编制校验失败（会直接破坏 5v5 平衡，请务必处理）：\n  · " +
                string.Join("\n  · ", problems));
        }

        /// <summary>
        /// 统计一个英雄根节点下的编制，并把发现的问题追加进清单。
        /// </summary>
        /// <param name="problems">问题清单。</param>
        /// <param name="rootName">英雄根节点名（BlueHeroes / RedHeroes）。</param>
        /// <param name="expectedTeam">该根节点下所有英雄应有的阵营。</param>
        /// <param name="playerCount">输出：玩家英雄数量（无 EntityAIController 的那些）。</param>
        /// <param name="aiCount">输出：AI 英雄数量（挂了 EntityAIController 的那些）。</param>
        /// <returns>该根节点下的英雄总数；根节点不存在时返回 0（并记一条问题）。</returns>
        private static int CollectHeroRoster(
            List<string> problems, string rootName, TeamType expectedTeam, out int playerCount, out int aiCount)
        {
            playerCount = 0;
            aiCount = 0;

            GameObject root = FindRootObjectByName(rootName);
            if (root == null)
            {
                problems.Add($"{rootName} 根节点不存在（英雄小队未生成）");
                return 0;
            }

            int total = root.transform.childCount;

            for (int i = 0; i < total; i++)
            {
                Transform child = root.transform.GetChild(i);

                // 用 GetComponent 读【场景里真实的组件】，而不是读工具传进来的参数 ——
                // 这一步的全部意义就在于"不信任本地变量"。
                EntityBase entity = child.GetComponent<EntityBase>();
                HeroController hero = child.GetComponent<HeroController>();

                if (entity == null || hero == null)
                {
                    problems.Add(
                        $"{rootName}/{child.name} 缺少 {(entity == null ? "EntityBase" : "HeroController")}，" +
                        "它不会被任何对局逻辑当成英雄处理");
                    continue;
                }

                if (entity.EntityType != EntityType.Hero)
                {
                    problems.Add(
                        $"{rootName}/{child.name} 的 EntityType = {entity.EntityType}，应为 Hero" +
                        "（类型错了会走小兵/建筑的行为分支）");
                }

                if (entity.Team != expectedTeam)
                {
                    problems.Add(
                        $"{rootName}/{child.name} 的阵营是 {entity.Team}，应为 {expectedTeam}" +
                        "（阵营决定敌我，错了会导致同队互殴或对敌人视而不见）");
                }

                // 玩家 / AI 的结构判据 = 有没有 EntityAIController：
                // 它是"这个英雄由 FSM 自己驱动"的唯一标志（玩家英雄靠指令层，刻意不挂 FSM）。
                if (child.GetComponent<EntityAIController>() != null)
                {
                    aiCount++;
                }
                else
                {
                    playerCount++;
                }
            }

            return total;
        }

        /// <summary>
        /// 数出全场景 EntityType.Hero 的实体数量（含未激活对象）。
        /// 含未激活对象（true）是必须的：游离副本常被顺手关掉，漏掉就永远发现不了。
        /// </summary>
        /// <returns>英雄实体数量。</returns>
        private static int CountSceneHeroEntities()
        {
            EntityBase[] entities = UnityEngine.Object.FindObjectsOfType<EntityBase>(true);

            int count = 0;
            for (int i = 0; i < entities.Length; i++)
            {
                EntityBase entity = entities[i];

                // 快照里的元素可能在取快照之后被销毁（Unity 重载的 == 能识别这种情况）。
                if (entity == null)
                {
                    continue;
                }

                if (entity.EntityType == EntityType.Hero)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>
        /// AI 技能分配校验：**9 个 AI 英雄 × 4 槽，必须全部来自技能池；玩家专属的盖伦 QWER 不得外泄**。
        ///
        /// 【为什么必须有这一步】本轮修掉的那个偷懒行为（"AI 英雄因为池子取不到就静默沿用预制体 QWER"）
        /// 在实机上的表现是"10 个英雄全在用同一套技能"，而 Console 里【一条日志都没有】。
        /// 判据不能靠"注入代码写对了"，只能靠事后读回场景里的技能槽去核对：
        ///   ① 四个槽位都不能为空（空 = 那个键按下去没反应）；
        ///   ② 每个槽位的资产路径必须落在技能池目录下（否则就是玩家技能或别的东西泄漏进来了）；
        ///   ③ 同一个英雄的四个技能互不相同（无放回抽取的验收项）；
        ///   ④ 玩家英雄那边反向核对：四槽必须恰好是盖伦那四份资产。
        /// 四条都通过，才能说"玩家专属 QWER + AI 专属技能池"这条分界成立。
        /// </summary>
        private static void ValidateAiSkillAssignment()
        {
            List<string> problems = new List<string>();

            int aiHeroCount = 0;
            int aiSlotCount = 0;

            CollectAiSkillProblems(problems, BlueHeroesRootName, ref aiHeroCount, ref aiSlotCount);
            CollectAiSkillProblems(problems, RedHeroesRootName, ref aiHeroCount, ref aiSlotCount);

            // ---- 玩家英雄反向核对：盖伦四件套必须且只能挂在它身上 ----
            GameObject blueRoot = FindRootObjectByName(BlueHeroesRootName);
            SkillComponent playerSkills = null;

            if (blueRoot != null && blueRoot.transform.childCount > 0)
            {
                playerSkills = FindPlayerHeroSkillComponent(blueRoot.transform);
            }

            if (playerSkills == null)
            {
                problems.Add($"在 {BlueHeroesRootName} 下找不到玩家英雄的 SkillComponent，无法核对玩家专属 QWER");
            }
            else
            {
                CheckPlayerSkillSlot(problems, playerSkills, SkillSlot.Q, HeroSkillQAssetPath);
                CheckPlayerSkillSlot(problems, playerSkills, SkillSlot.W, HeroSkillWAssetPath);
                CheckPlayerSkillSlot(problems, playerSkills, SkillSlot.E, HeroSkillEAssetPath);
                CheckPlayerSkillSlot(problems, playerSkills, SkillSlot.R, HeroSkillRAssetPath);
            }

            if (problems.Count == 0)
            {
                Debug.Log(
                    $"[AutoSceneBuilder] AI 技能分配校验通过：{aiHeroCount} 个 AI 英雄 × {AiHeroSkillSlotCount} 槽 " +
                    $"= {aiSlotCount} 个技能全部来自技能池（{SkillPoolFolder}），同英雄内无重复；" +
                    "玩家专属的盖伦 Q/W/E/R 仅挂在玩家英雄身上。");
                return;
            }

            Debug.LogError(
                "[AutoSceneBuilder] AI 技能分配校验失败（AI 会与玩家用同一套技能，或部分技能槽是空的）：\n  · " +
                string.Join("\n  · ", problems));
        }

        /// <summary>
        /// 逐个 AI 英雄核对四个技能槽，并把问题追加进清单。
        /// </summary>
        /// <param name="problems">问题清单。</param>
        /// <param name="rootName">英雄根节点名。</param>
        /// <param name="aiHeroCount">累计：已核对的 AI 英雄数。</param>
        /// <param name="aiSlotCount">累计：已核对的有效技能槽数。</param>
        private static void CollectAiSkillProblems(
            List<string> problems, string rootName, ref int aiHeroCount, ref int aiSlotCount)
        {
            GameObject root = FindRootObjectByName(rootName);
            if (root == null)
            {
                // 根节点缺失已由 ValidateHeroRoster 报出，这里不重复刷屏。
                return;
            }

            for (int i = 0; i < root.transform.childCount; i++)
            {
                Transform child = root.transform.GetChild(i);

                // 只核对 AI 英雄：玩家英雄的四槽是另一套规则（见 ValidateAiSkillAssignment）。
                if (child.GetComponent<EntityAIController>() == null)
                {
                    continue;
                }

                aiHeroCount++;

                SkillComponent skills = child.GetComponent<SkillComponent>();
                if (skills == null)
                {
                    problems.Add($"{rootName}/{child.name} 上没有 SkillComponent，无法核对技能分配");
                    continue;
                }

                // 用长度 4 的局部数组收集本英雄的技能，用于"同英雄内无重复"的判据。
                SkillData[] resolved = new SkillData[AiHeroSkillSlotCount];
                bool hasDuplicate = false;

                for (int slotIndex = 0; slotIndex < AiHeroSkillSlotCount; slotIndex++)
                {
                    SkillSlot slot = (SkillSlot)slotIndex;
                    SkillData data = skills.GetSkillData(slot);

                    if (data == null)
                    {
                        problems.Add($"{rootName}/{child.name} 的 {slot} 槽是空的（该 AI 会少一个技能）");
                        continue;
                    }

                    aiSlotCount++;

                    string assetPath = AssetDatabase.GetAssetPath(data);
                    if (string.IsNullOrEmpty(assetPath) ||
                        !assetPath.StartsWith(SkillPoolFolder, System.StringComparison.Ordinal))
                    {
                        problems.Add(
                            $"{rootName}/{child.name} 的 {slot} 槽是 {data.DisplayName}" +
                            $"（{(string.IsNullOrEmpty(assetPath) ? "非资产引用" : assetPath)}），" +
                            $"不在技能池 {SkillPoolFolder} 下 —— 玩家专属技能泄漏给了 AI");
                    }

                    for (int k = 0; k < slotIndex; k++)
                    {
                        if (ReferenceEquals(resolved[k], data))
                        {
                            hasDuplicate = true;
                        }
                    }

                    resolved[slotIndex] = data;
                }

                if (hasDuplicate)
                {
                    problems.Add(
                        $"{rootName}/{child.name} 的四个技能里有重复项（抽取应当是无放回的，" +
                        "重复意味着同一个英雄身上挂了两遍同一个技能）");
                }
            }
        }

        /// <summary>
        /// 在蓝方英雄里找出玩家英雄的 SkillComponent。
        /// 判据与 ValidateHeroRoster 完全一致（没有 EntityAIController 的那个就是玩家英雄）——
        /// 两处口径若不一致，会出现"编制校验认为玩家英雄是 A、技能校验认为没有玩家英雄"的错位结论。
        /// </summary>
        /// <param name="blueRoot">蓝方英雄根节点的 Transform。</param>
        /// <returns>玩家英雄的技能组件；找不到返回 null。</returns>
        private static SkillComponent FindPlayerHeroSkillComponent(Transform blueRoot)
        {
            for (int i = 0; i < blueRoot.childCount; i++)
            {
                Transform child = blueRoot.GetChild(i);

                if (child.GetComponent<EntityAIController>() != null)
                {
                    continue;
                }

                return child.GetComponent<SkillComponent>();
            }

            return null;
        }

        /// <summary>
        /// 核对玩家英雄的某个槽位是否指向预期的那份盖伦技能资产。
        /// </summary>
        /// <param name="problems">问题清单。</param>
        /// <param name="skills">玩家英雄的技能组件。</param>
        /// <param name="slot">槽位。</param>
        /// <param name="expectedAssetPath">期望的资产路径。</param>
        private static void CheckPlayerSkillSlot(
            List<string> problems, SkillComponent skills, SkillSlot slot, string expectedAssetPath)
        {
            SkillData data = skills.GetSkillData(slot);

            if (data == null)
            {
                problems.Add($"玩家英雄的 {slot} 槽是空的（应指向 {expectedAssetPath}）");
                return;
            }

            string assetPath = AssetDatabase.GetAssetPath(data);
            if (!string.Equals(assetPath, expectedAssetPath, System.StringComparison.Ordinal))
            {
                problems.Add(
                    $"玩家英雄的 {slot} 槽指向 {(string.IsNullOrEmpty(assetPath) ? "非资产引用" : assetPath)}，" +
                    $"应为 {expectedAssetPath}");
            }
        }

        /// <summary>
        /// 按名字在场景根对象里找一个对象。
        ///
        /// 用 GetRootGameObjects 而不是 GameObject.Find：后者只找得到【激活】的对象，
        /// 而校验恰恰需要看到"被关掉的游离副本"（与 CleanupOldBattlefield 同一取舍）。
        /// </summary>
        /// <param name="objectName">对象名。</param>
        /// <returns>命中的根对象；找不到返回 null。</returns>
        private static GameObject FindRootObjectByName(string objectName)
        {
            GameObject[] roots = SceneManager.GetActiveScene().GetRootGameObjects();

            for (int i = 0; i < roots.Length; i++)
            {
                GameObject root = roots[i];

                // 快照里的元素可能在本次调用之前已被销毁，先判 null 再读 name。
                if (root == null || !string.Equals(root.name, objectName, System.StringComparison.Ordinal))
                {
                    continue;
                }

                return root;
            }

            return null;
        }

        #endregion

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
                problems.Add($"玩家英雄（{BlueHeroInstancePrefix}0）：英雄实例不存在");
            }
            else
            {
                // HeroController.Entity 读的是序列化字段（工具注入过），因此编辑模式下可用；
                // 不能读 entity.Health / entity.Combat —— 那些是 Awake 里缓存的引用，编辑模式恒为 null。
                AppendStatsProblem(problems, $"玩家英雄（{hero.name}）", hero.Entity);
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
        ///   + HeroController + PlayerCommandController + PlayerSkillController
        ///   + 阶段八实机修复：TeamColorView（运行期按 阵营×单位类型 上色，见 CreateHeroPrefab 内的说明）。
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
            EntityColorSet entityColors,
            SkillData heroSkillQ,
            SkillData heroSkillW,
            SkillData heroSkillE,
            SkillData heroSkillR,
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

            // ---- 实体配色（阶段八实机修复：英雄 / 小兵用同一套四格配色逻辑）----
            // TeamColorView 在运行期按 (EntityBase.Team, EntityBase.EntityType) 选材质，
            // 因此【10 个英雄共用同一个预制体也能长出蓝红两色】—— 与工具在实例上逐个写渲染器覆写相比，
            // 这条通道的好处是"英雄与小兵只有一处上色实现"，坏处是颜色要等运行时才可见
            // （编辑器 Scene 视图里看到的仍是预制体的绿色胶囊体，属于预期行为）。
            //
            // 【为什么挂在这里而不是步骤 19 的 BuildWhiteboxVfx】
            // 步骤 15b 会立刻用这个预制体实例化 10 个英雄。若把组件留到步骤 19 才加，
            // 就要依赖"改预制体资产后已有实例自动同步"这条引擎行为 —— 能work，但顺序上不直观，
            // 而且一旦哪次同步失败，症状是"10 个英雄全是绿的"，排查时很难想到是装配顺序问题。
            TeamColorView colorView = temp.AddComponent<TeamColorView>();
            AssignObjectReference(colorView, "allyHeroMaterial", entityColors.AllyHero);
            AssignObjectReference(colorView, "enemyHeroMaterial", entityColors.EnemyHero);
            AssignObjectReference(colorView, "allyMinionMaterial", entityColors.AllyMinion);
            AssignObjectReference(colorView, "enemyMinionMaterial", entityColors.EnemyMinion);

            // ---- 依赖注入：全部由工具完成，用户不需要拖任何引用 ----
            // 身份与配置：运行时由 EntityBase.ApplyStats 转交给各组件（生命/移速/索敌/攻击/法力/技能各一条数据流）。
            AssignEnum(entity, "team", (int)TeamType.Player);
            AssignEnum(entity, "entityType", (int)EntityType.Hero);
            AssignObjectReference(entity, "statsData", heroStats);

            // 组件级兜底：EntityStatsData.Attack 为空时 CombatComponent 会沿用这里直挂的资产，
            // 两处都写上，任何一条数据流被破坏都不会导致「英雄打不出伤害」。
            AssignObjectReference(combat, "attackData", heroAttack);

            // 技能配置的双保险，与普攻的 attackData 完全同一套路：
            // EntityStatsData.Skill* 是主数据流，这里直挂是兜底。
            //
            // 【为什么写整个数组而不是逐个槽位】skillSlots 是一个数组字段（下标 = (int)SkillSlot），
            // 逐个槽位写入需要"先保证数组长度再写第 i 个元素"，多一层容易漏的步骤；
            // 整体写入则一次性把"长度 = 4"与"每个槽位是什么"都确定下来。
            AssignObjectArray(skills, "skillSlots", new UnityEngine.Object[]
            {
                heroSkillQ, heroSkillW, heroSkillE, heroSkillR
            });

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
            notes.Add(
                $"{prefabPath} 已生成（绿色胶囊体 + 全套组件 + 依赖注入 + TeamColorView 四格配色；" +
                "运行期按 阵营×单位类型 上色：蓝英雄 / 红英雄 / 白小兵 / 黑小兵）");
            return saved;
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
        /// 写入布尔型序列化字段（阶段八：AI 英雄的 playerControlled / destroyCorpseOnDeath 用这个注入）。
        ///
        /// 【为什么不复用 AssignInt】SerializedProperty 的类型不匹配时写入会失败且不报错，
        /// 因此每种类型都必须有各自的入口（与 AssignFloat / AssignInt / AssignString 同一约定）。
        /// </summary>
        private static bool AssignBool(UnityEngine.Object target, string fieldName, bool value)
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

            if (property.propertyType != SerializedPropertyType.Boolean)
            {
                Debug.LogError(
                    $"[AutoSceneBuilder] {target.GetType().Name}.{fieldName} 不是布尔字段，注入失败。");
                return false;
            }

            property.boolValue = value;
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
        /// 把字段**精确改写**为给定值；值已经相同则不写、不返回 true（避免每轮组装都产生无意义的落盘）。
        ///
        /// 与 <see cref="ClampFloatAboveCap"/>（只压高、不抬低）的区别：
        ///   · ClampFloatAboveCap —— 单向阀门，用于"攻击力这类不希望每轮覆盖的策划数值"；
        ///   · ForceFloatValue     —— 双向强控，用于"架构师明确要求锁死的数值"。
        /// 两者都是**白盒测试期的临时手段**，阶段九正式数值平衡时应一并移除调用点。
        /// </summary>
        /// <param name="target">目标资产。</param>
        /// <param name="fieldName">字段名。</param>
        /// <param name="value">要写入的精确值。</param>
        /// <param name="previousValue">输出：修改前的值（未发生修改时等于当前值，便于日志写出"原 X"）。</param>
        /// <returns>确实发生了修改返回 true。</returns>
        private static bool ForceFloatValue(
            UnityEngine.Object target, string fieldName, float value, out float previousValue)
        {
            previousValue = 0f;

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
                    $"[AutoSceneBuilder] {target.GetType().Name}.{fieldName} 不是浮点字段，强控写入失败。");
                return false;
            }

            previousValue = property.floatValue;

            // 用 Mathf.Approximately 而不是 ==：两者都是同一份 float 序列化值，实际必然精确相等，
            // 但用近似比较可以避免"浮点表示差异导致每轮都重写一次"的意外。
            if (Mathf.Approximately(previousValue, value))
            {
                return false;
            }

            property.floatValue = value;
            serialized.ApplyModifiedProperties();
            return true;
        }

        /// <summary>
        /// 只在字段值【高于上限】时把它下调到上限；等于或低于上限时一个字节都不改。
        ///
        /// 与 PatchFloatIfUnset（只补空、不覆盖）语义相反：这是"只压高、不抬低"。
        ///
        /// 【为什么需要这样一种写入】白盒测试期发现塔的血量（上一轮写进资产的 1500）把推塔流程
        /// 拖得极其痛苦，而工具的既有策略是"资产已存在就一律沿用、绝不覆盖"—— 于是工具**改不动**它，
        /// 只能靠人手工去 Inspector 里调，而"一键组装"的承诺就出现了缺口。
        /// 折中方案是加一道单向阀门：只压高、不抬低。策划若主动把塔调到 400 血，工具不会把它抬回 600。
        ///
        /// 【生命周期】这是**白盒测试期的临时阀门**，不是长期平衡机制。
        /// 阶段九进入正式数值平衡时应删除全部调用点（EnsureTowerStatsAsset / EnsureTowerAttackAsset），
        /// 回到"已存在一律沿用"的单一策略。
        /// </summary>
        /// <param name="target">目标资产。</param>
        /// <param name="fieldName">字段名。</param>
        /// <param name="cap">上限值。</param>
        /// <param name="previousValue">输出：修改前的值（未发生修改时等于当前值，便于日志写出"从多少降到多少"）。</param>
        /// <returns>确实发生了下调返回 true。</returns>
        private static bool ClampFloatAboveCap(
            UnityEngine.Object target, string fieldName, float cap, out float previousValue)
        {
            previousValue = 0f;

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
                    $"[AutoSceneBuilder] {target.GetType().Name}.{fieldName} 不是浮点字段，上限钳制失败。");
                return false;
            }

            previousValue = property.floatValue;

            if (previousValue <= cap)
            {
                // 已经在上限之内（含相等）→ 不动。这就是"不抬低"。
                return false;
            }

            property.floatValue = cap;
            serialized.ApplyModifiedProperties();
            return true;
        }

        /// <summary>
        /// 只在引用字段为空时写入对象引用，已有引用一律不动。语义与 PatchFloatIfUnset 完全对称。
        /// 调用方：塔的攻击资产补缺（Stage8Assembly）、技能图标补缺（UIAssembly）。
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
            List<string> towerNotes,
            List<string> uiNotes,
            List<string> debugNotes,
            List<string> vfxNotes,
            List<string> presentationNotes)
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
            string towerText = towerNotes.Count > 0 ? string.Join("；", towerNotes) : "（未生成）";
            string uiText = uiNotes.Count > 0 ? string.Join("；", uiNotes) : "（无动作）";
            string debugText = debugNotes.Count > 0 ? string.Join("；", debugNotes) : "（无动作）";
            string vfxText = vfxNotes.Count > 0 ? string.Join("；", vfxNotes) : "（无动作）";
            string presentationText = presentationNotes.Count > 0 ? string.Join("；", presentationNotes) : "（无动作）";
            string cameraText = DescribeCamera(cameraController, hero);

            Debug.Log(
                "[AutoSceneBuilder] 测试战场组装完成。\n" +
                $"  · 清理旧对象：{removedText}\n" +
                $"  · 清理多余 MatchController：{controllerText}\n" +
                $"  · 小兵兵线：{BlueLaneName}（{DescribeLane(BlueLaneWaypointPositions)}，从蓝方推向红方基地）；" +
                $"{RedLaneName}（{DescribeLane(RedLaneWaypointPositions)}，节点顺序与蓝方相反）\n" +
                $"  · 英雄推进线：{BlueHeroLaneName}（{DescribeLane(BlueHeroLaneWaypointPositions)}）；" +
                $"{RedHeroLaneName}（{DescribeLane(RedHeroLaneWaypointPositions)}）" +
                $"—— 与小兵兵线分处桥心两侧（小兵 z = {MinionLaneOffsetZ:F1} / 英雄 z = {HeroLaneOffsetZ:F1}）\n" +
                $"  · 蓝方：{DescribeBase(BlueBaseName, blueBase)}{FormatPosition(BlueBasePosition)}；" +
                $"{BlueSpawnerName}{FormatPosition(BlueSpawnerPosition)}（{DescribeSpawner(blueSpawner)}）\n" +
                $"  · 红方：{DescribeBase(RedBaseName, redBase)}{FormatPosition(RedBasePosition)}；" +
                $"{RedSpawnerName}{FormatPosition(RedSpawnerPosition)}（{DescribeSpawner(redSpawner)}）\n" +
                $"  · 对局编排：{BattlefieldName} 挂 MatchController，已登记 2 个出兵点与 10 个英雄\n" +
                $"  · 基地外观与属性：{baseText}\n" +
                $"  · 防御塔（阶段八 6 座）：{towerText}\n" +
                $"  · 地面与导航：{groundText}\n" +
                $"  · 玩家英雄：{DescribeHero(hero)}\n" +
                $"  · 英雄装配：{heroText}\n" +
                $"  · 相机跟随：{cameraText}\n" +
                $"  · UI 与可视化（阶段七）：{uiText}\n" +
                $"  · 调试视图（阶段八自审补齐）：{debugText}\n" +
                $"  · 白盒视觉反馈（阶段八实机修复）：{vfxText}\n" +
                $"  · 表现层基建（阶段九）：{presentationText}\n" +
                $"  · 资产补写：{assetText}\n" +
                $"  · 小兵预制体：{prefabText}\n" +
                $"  · 地图：单线桥梁地形 {BridgeGroundSize.x:F0}×{BridgeGroundSize.y:F0} 米（沿 X 轴拉长，" +
                $"长宽比 {BridgeGroundSize.x / BridgeGroundSize.y:F1}:1），双方基地相距 110 米，一条兵线直通对方基地。\n" +
                "  · 防御塔：双方各 3 座沿中线对称分布（门牙塔 ±45 / 中路塔 ±30 / ±15；相邻间距 15 米，" +
                "两侧中路一塔之间留出 30 米中场交战区）；仇恨优先级为「小兵 > 英雄 > 其它」，" +
                "且敌方英雄在塔下攻击己方英雄时会立刻转移仇恨；塔的普攻是弹道（有飞行时间、命中时结算）。\n" +
                "  · 交通疏导（烘焙期挖洞）：防御塔与基地的外观体位于 Building 层，NavMesh 烘焙时" +
                $"直接把它们抠成空洞。桥面可行走半宽 = {BridgeGroundSize.y / 2f - 0.5f:F1} 米 < 塔攻击距离 " +
                $"{TowerAttackRange:F1} 米，因此沿 Z 方向不存在绕开塔的通道；塔洞两侧各留约 " +
                $"{BridgeGroundSize.y / 2f - 0.5f - (TowerBodySize.x / 2f + 0.5f):F1} 米通行带。\n" +
                "  · 视野与追击：索敌半径（小兵 24 米 / 英雄 20 米）只决定「看得见多远」；" +
                $"真正决定「值不值得脱离兵线」的是追击发起半径（小兵 {AiMinionChaseEngageRange:F0} / " +
                $"英雄 {AiHeroChaseEngageRange:F0} 米），放弃追击与牵引极限都以它为基准（×1.5 / ×2.0）。\n" +
                "  · 塔数值（白盒测试期强控）：生命 1000（精确写入）/ 伤害 45 / " +
                $"射程 {TowerAttackRange:F1}（精确写入，与桥宽绑定）/ 间隔 1.2 秒。" +
                "阶段九正式平衡时应把强控改回「已存在一律沿用」。\n" +
                "  · 英雄：战场共 10 个 —— 蓝方 5 个（1 玩家 + 4 AI）/ 红方 5 个（全 AI）。" +
                "玩家操控蓝方 BlueHero_0（挂玩家指令层 + 技能输入层，相机跟随，盖伦 Q/W/E/R 四技能，" +
                "**全场只有它使用这套技能**）；其余 9 个是 AI 英雄（挂 EntityAIController 走小兵同源 FSM，" +
                "并挂 HeroAIController 做技能决策）。\n" +
                $"  · AI 技能分配：9 个 AI 英雄按固定 seed（{SkillPoolRandomSeed} + 序号 × {SkillPoolSeedStride}）" +
                $"从 10 技能池中【无放回】各抽 4 个不同技能，挂入 Q / W / E / R 四槽" +
                "（按实例注入，不改共享资产）；池内技能参数在 Inspector 里可调，改完重新组装即生效。\n" +
                "  · 实体配色（阶段九更新）：由 TeamColorView 在运行期按「阵营 × 单位类型」四格查表上色 —— " +
                "蓝方英雄蓝 / 红方英雄红 / 蓝方小兵蓝 / 红方小兵红；建筑（塔 / 基地）沿用各自材质，" +
                "不在四格表内。英雄预制体与小兵预制体都注入了同一套材质，上色逻辑全项目只有一处。\n" +
                "      材质来源（阶段九）：若 Assets/Art/Materials 下存在 HeroModelBlue/Red.mat 与 " +
                "MinionModelBlue/Red.mat（带模型贴图的材质），优先使用它们；否则退回四份纯色材质。" +
                "**导入模型贴图后必须让配色材质带贴图** —— TeamColorView 写的是整个 sharedMaterial，" +
                "纯色材质盖到模型上会把贴图整个抹掉。\n" +
                "  · 避让优先级：英雄 30 段 / 小兵 60 段（数值越小优先级越高），" +
                "打破局部避让的对称性，避免单位在窄道里互相顶死。\n" +
                "  · 到达判定：NavMeshAgent.stoppingDistance = 0.5 米（远小于最小攻击距离 2.0 米），" +
                "让「到达路径点」在拥堵下也能成立；同一路径点被卡住 3 次后自动跳过，不会永久停滞。\n" +
                "  · AI 英雄不销毁尸体：EntityAIController.destroyCorpseOnDeath 已置 false，" +
                "否则阵亡即销毁，复活链路无从谈起。\n" +
                "  · 提醒：相机操作为「右键=指令，中键拖拽=平移，中键双击=回中，滚轮=缩放」，" +
                "与英雄的右键指令互不冲突。\n" +
                "  · 技能操作（盖伦式 QWER，全部为智能施法：按下即释放，无二次确认）：\n" +
                "      Q 致命打击 —— 自身加速 + 强化下一次普攻（额外伤害 + 命中沉默，命中即消耗）；\n" +
                "      W 勇气 —— 立即获得护盾；\n" +
                "      E 审判 —— 以自身为中心的持续 AOE，跟随自己移动；\n" +
                "      R 德玛西亚正义 —— 指向性斩杀（伤害 = 基础值 + 系数 × 目标已损失生命值）。\n" +
                "  · 技能机制：前摇期间英雄被锁住移动与攻击（逻辑计时，与动画长度无关）；" +
                "眩晕与施法前摇共用同一把锁，两个持有者都放开才会真正解锁；沉默只封施法，不影响移动与普攻。\n" +
                "  · UI 与可视化：头顶血条与伤害飘字由 WorldUIRoot 下的管理器统一挂载（单位预制体零改动）；" +
                "左下角是玩家 HUD（头像/血条/蓝条/Q-W-E-R 四个技能槽 + CD 径向遮罩），顶部是计分板与击杀播报，" +
                "玩家英雄阵亡后会出现复活倒计时遮罩（时长取自 MatchConfigData.RespawnTime）。\n" +
                "  · 血条显示策略：默认【满血也显示】（hideWhenFull = false，由工具显式写入）。" +
                "开启满血隐藏会让刚复活/刚回满血的英雄完全没有血条，实机上会被误读成「英雄没挂血条」。\n" +
                "  · 特效层（阶段九：已接通）：VfxSpawnerRoot 下的 VfxSpawner 订阅受击 / 施法 / 弹道命中，" +
                "并按 SkillData 的语义字段分派护盾与范围场通道，从对象池取粒子预制体播放，粒子播完自动回收；" +
                "池耗尽只少播一个特效，绝不即时 Instantiate。\n" +
                "  · 表现层基建（阶段九）：AnimationComponent 把逻辑状态单向映射为 Animator 参数 —— " +
                "移动速度（MovementComponent.CurrentVelocity 归一化）驱动 Speed(float)（Idle ↔ Run 平滑过渡），" +
                "CombatComponent.OnAttackPerformed / SkillComponent.OnCastStarted / HealthComponent.OnDied " +
                "分别触发 Attack / Spell / Die；**动画绝不反向回调伤害**（不订阅任何 Animation Event）。\n" +
                "      控制器：Assets/Art/Animations/UnitAnimator.controller（Idle / Run / Attack / Spell / Die 五状态 + " +
                "Speed / Attack / Spell / Die 四参数 + 六条过渡），**五个状态均已接入动画片段** —— " +
                "当前是工具程序化生成的占位片段（驱动 Model 子节点的 Transform：呼吸起伏 / 奔跑弹跳前倾 / " +
                "攻击前冲 / 施法上浮 / 死亡前扑），用于消除 T-pose；导入真美术片段后拖到对应状态即可覆盖" +
                "（工具的规则是「状态的 motion 为空才写」，不会冲掉美术的连线）。\n" +
                "      特效层：VfxSpawnerRoot 下的 VfxSpawner 订阅受击（HealthComponent.OnDamaged）/ 施法" +
                "（SkillComponent.OnSpellReleased）/ 弹道命中（Projectile.OnHit，经 ProjectileSpawner 转发），" +
                "从对象池取特效预制体播放，粒子播完自动回收；池耗尽只少播一个特效，绝不即时 Instantiate。\n" +
                "      预留槽位（导入后重新执行本菜单即自动接入，无需手工拖引用）：" +
                "模型 Assets/Art/Models/Heroes/HeroModel.prefab 与 Models/Minions/MinionModel.prefab；" +
                "特效 Assets/Prefabs/VFX/ 下的 HitVfx.prefab / CastVfx.prefab / ProjectileHitVfx.prefab。\n" +
                "      白盒残留清理（本轮完成）：尸体隐藏（EntityVisuals.SetRenderersEnabled 的三处调用点）已全部删除，" +
                "EntityVisuals 类随之删除（无消费方 = 死代码）；WhiteboxVfxManager 已由工具置为禁用状态 —— " +
                "**注意：在正式特效预制体导入之前，技能将没有任何画面反馈**（这是刻意的：白盒占位让位给正式层）。\n" +
                "  · 提醒：步骤 8 已确保地面覆盖战场并烘焙 NavMesh；若仍有落点告警，" +
                "说明地面是 Terrain/自定义网格或 NavMeshSettings 被人工调整过，需手动处理。\n" +
                "  · 场景已自动保存（见上方 SaveSceneAfterAssembly 的日志）。" +
                "之所以由工具来存：工具会同时改动【场景】与【场景级 NavMeshData 资产】两处，" +
                "只落盘后者会让重启后的场景与导航网格对不上。");
        }

        /// <summary>
        /// 把一条兵线拼成可读的描述，仅用于日志。
        ///
        /// 只打首尾两个节点 + 节点总数：节点从 4 个增加到 7 个之后，逐点打印会让日志行变得极长，
        /// 而"这条线从哪里走到哪里、有几个节点"才是核对时真正要读的信息
        /// （每个节点的完整坐标仍可在 Hierarchy 里选中 BlueLane / RedLane 查看）。
        /// </summary>
        /// <param name="waypoints">路径点坐标数组。</param>
        private static string DescribeLane(Vector3[] waypoints)
        {
            if (waypoints == null || waypoints.Length == 0)
            {
                return "无节点";
            }

            if (waypoints.Length == 1)
            {
                return FormatPosition(waypoints[0]);
            }

            return $"{waypoints.Length} 节点：{FormatPosition(waypoints[0])} → … → " +
                   $"{FormatPosition(waypoints[waypoints.Length - 1])}";
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
                // 【建筑落点必须排除在外】防御塔与基地是【故意】被烘成障碍的（烘焙期挖洞），
                // 它们的中心本来就不该有可行走区域 —— 采样不到才是正确结果。
                // 若不排除，每次组装都会为这 8 个点刷出"落点不在 NavMesh 覆盖范围内"的假告警，
                // 而这 8 条假告警会立刻淹没真正的问题（真有一个出生点没烘进去时反而看不见了）。
                if (IsBuildingPosition(points[i]))
                {
                    continue;
                }

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
            List<string> tooClose = new List<string>();

            for (int i = 0; i < points.Length; i++)
            {
                Vector3 point = points[i];

                if (point.x < groundBounds.min.x || point.x > groundBounds.max.x ||
                    point.z < groundBounds.min.z || point.z > groundBounds.max.z)
                {
                    outside.Add($"{labels[i]}{FormatPosition(point)}");
                    continue;
                }

                // 在范围内但"贴边"：余量不足 MinKeyPointMargin。
                // 后果不是报错，而是**被 NavMesh 内缩裁掉**——NavMesh 会在可行走区域边缘内缩一个
                // Agent 半径（本项目 0.5 米），出生点若离边缘太近，SamplePosition 会失败并退回原始坐标，
                // 运行期表现为"某个单位一出生就动不了"，而 Console 里只有一条采样告警。
                // 关卡扩容后基地/英雄出生点本就在桥的两端（±55 / ±58 对 ±60 边缘），因此这条提示是常驻的，
                // 只在余量真的过小时才报，避免变成噪声。
                float margin = Mathf.Min(
                    Mathf.Min(point.x - groundBounds.min.x, groundBounds.max.x - point.x),
                    Mathf.Min(point.z - groundBounds.min.z, groundBounds.max.z - point.z));

                if (margin < MinKeyPointMargin)
                {
                    tooClose.Add($"{labels[i]}{FormatPosition(point)}（余量 {margin:F2} 米）");
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

            if (tooClose.Count > 0)
            {
                Debug.LogWarning(
                    $"[AutoSceneBuilder] 以下落点距地面边缘不足 {MinKeyPointMargin:F1} 米（贴边）：" +
                    string.Join("、", tooClose) + "。\n" +
                    $"  · 地面水平范围：x ∈ [{groundBounds.min.x:F1}, {groundBounds.max.x:F1}]，" +
                    $"z ∈ [{groundBounds.min.z:F1}, {groundBounds.max.z:F1}]\n" +
                    "  · 后果：NavMesh 会在可行走区域边缘内缩一个 Agent 半径，贴边的出生点可能采样失败，" +
                    "运行期表现为「该单位一出生就无法移动」。\n" +
                    "  · 修法：把该落点向桥心方向内收，或按需求调整 BridgeGroundSize。");
            }
        }

        #endregion
    }
}
