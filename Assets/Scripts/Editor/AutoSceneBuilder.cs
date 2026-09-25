using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using MOBA.AI;
using MOBA.Components;
using MOBA.Core;
using MOBA.Data;
using MOBA.Gameplay;
using MOBA.Units;

namespace MOBA.Editor
{
    /// <summary>
    /// 一键组装测试战场：用代码把「兵线 + 双方基地 + 双方出兵点 + 对局控制器」搭出来，替代手工拖拽与填坐标。
    ///
    /// 职责边界：
    /// 1. 只存在于编辑器（本文件位于 Assets/Scripts/Editor/，Unity 会自动把它编入 Assembly-CSharp-Editor，
    ///    不会进入任何运行时程序集），不参与任何运行时逻辑；
    /// 2. 负责「创建对象 / 挂组件 / 注入引用」，并顺带补齐「会让整条兵线静默失效」的缺口：
    ///    多余的对局控制器（步骤 7）与小兵预制体缺失的必需组件（步骤 8）。
    ///    不烘焙 NavMesh、不做胜负判定、不改任何运行时代码；
    /// 3. 幂等：重复执行会先按固定名称清掉上一次由本工具生成的对象，不会堆出第二条兵线或第二套基地。
    ///
    /// 为什么写字段用 SerializedObject 而不是反射（对比 Tests/PlayMode/Stage2AutoTester 的做法）：
    /// 需要写入的都是 private [SerializeField] 字段，编辑器里唯一被 Unity 序列化系统承认的写入通道就是
    /// SerializedObject + SerializedProperty —— 改动会同步进 Inspector 与磁盘；反射改字段则未必被察觉，
    /// 尤其是对 ScriptableObject 资产。代价是字段名退化为字符串，因此每次写入找不到字段都会报错而非静默失败。
    /// </summary>
    public static class AutoSceneBuilder
    {
        /// <summary>菜单路径。层级中的「MOBA Demo」是本项目所有编辑器工具的归口。</summary>
        private const string MenuPath = "MOBA Demo/一键组装测试战场";

        /// <summary>撤销组名称：用户误点后一次 Ctrl+Z 即可完整回退整套战场。</summary>
        private const string UndoGroupName = "一键组装测试战场";

        /// <summary>配置资产根目录。与项目约定一致（资产放 Assets/ScriptableObjects/）。</summary>
        private const string ScriptableObjectsFolder = "Assets/ScriptableObjects";

        /// <summary>预制体根目录。</summary>
        private const string PrefabsFolder = "Assets/Prefabs";

        // ---- 固定对象名。清理与重建都以这些名字为准，改名即意味着本工具不再接管该对象。 ----
        private const string MidLaneName = "MidLane";
        private const string BlueBaseName = "BlueBase";
        private const string BlueSpawnerName = "BlueSpawner";
        private const string RedBaseName = "RedBase";
        private const string RedSpawnerName = "RedSpawner";
        private const string BattlefieldName = "Battlefield";

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

        /// <summary>基地占位碰撞体的尺寸与中心（底面贴地，向上抬 1 米）。</summary>
        private static readonly Vector3 BaseColliderCenter = new Vector3(0f, 1f, 0f);
        private static readonly Vector3 BaseColliderSize = new Vector3(2f, 2f, 2f);

        /// <summary>
        /// 本工具接管的根对象名清单。
        /// 刻意【只按名字匹配】而不做「全场景扫描所有 MinionSpawner/BaseCoreController 并删除」：
        /// 后者会把用户手工搭的、名字不同的对象一并抹掉，代价远大于收益。名字写死意味着删除范围完全可预期。
        /// </summary>
        private static readonly string[] ManagedRootNames =
        {
            MidLaneName, BlueBaseName, BlueSpawnerName, RedBaseName, RedSpawnerName, BattlefieldName
        };

        /// <summary>
        /// 菜单入口：一键组装测试战场。
        /// 流程：清理旧战场 → 查找配置资产 → 建兵线 → 建蓝方 → 建红方 → 建对局编排 → 补齐资产缺口。
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

            // ---------- 步骤 2：查找配置资产与预制体 ----------
            EntityStatsData baseStats = LoadPreferredAsset<EntityStatsData>(
                FindAssetPaths<EntityStatsData>(ScriptableObjectsFolder),
                new[] { "Base", "Tower" },
                "基地的 EntityStatsData");

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
            EntityBase blueBase = CreateBase(BlueBaseName, BlueBasePosition, TeamType.Player, baseStats);
            MinionSpawner blueSpawner = CreateSpawner(
                BlueSpawnerName, BlueSpawnerPosition, TeamType.Player, spawnData, midLane);

            // ---------- 步骤 5：红方阵地（阵营强制为 Enemy，与蓝方形成敌对关系） ----------
            EntityBase redBase = CreateBase(RedBaseName, RedBasePosition, TeamType.Enemy, baseStats);
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

            // ---------- 步骤 8：补齐会导致运行期直接失败的资产与预制体缺口 ----------
            List<string> assetNotes = PatchAssetGaps(spawnData, minionPrefab, attackData);
            List<string> prefabNotes = RepairPrefabComponents(minionPrefab);

            // ---------- 收尾 ----------
            // 标记场景已修改，避免用户在没保存的情况下直接进播放模式、丢失本次组装结果。
            EditorSceneManager.MarkSceneDirty(scene);
            Undo.CollapseUndoOperations(undoGroup);

            LogSummary(
                removedNames, removedControllers, blueBase, redBase, blueSpawner, redSpawner, assetNotes, prefabNotes);
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
                if (path.EndsWith(".prefab", System.StringComparison.OrdinalIgnoreCase))
                {
                    paths.Add(path);
                }
            }

            paths.Sort();
            return paths;
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
        /// 创建一个阵营基地：EntityBase + HealthComponent + BaseCoreController + 占位碰撞体。
        ///
        /// 刻意【不挂】CombatComponent / TargetingComponent / MovementComponent：
        /// README 3.3 规定基地「不可移动、不可攻击实体，仅作为胜负判定的载体」，
        /// 多挂反而会被 EntityBase.ValidateDependencies 按类型报出误配告警。
        /// </summary>
        private static EntityBase CreateBase(
            string objectName, Vector3 position, TeamType team, EntityStatsData stats)
        {
            GameObject baseObject = CreateRootObject(objectName, position);

            EntityBase entity = Undo.AddComponent<EntityBase>(baseObject);
            Undo.AddComponent<HealthComponent>(baseObject);
            Undo.AddComponent<BaseCoreController>(baseObject);

            // 碰撞体是必需的，不是装饰：
            // 索敌走 Physics.OverlapSphere + GetComponentInParent<ITargetable>，
            // 没有碰撞体的单位永远不会出现在任何一次索敌结果里，而且不抛任何异常——
            // 表现为「小兵从基地旁边走过却永远打不掉它」，是本项目排查成本最高的一类配置错误。
            BoxCollider boxCollider = Undo.AddComponent<BoxCollider>(baseObject);
            boxCollider.center = BaseColliderCenter;
            boxCollider.size = BaseColliderSize;

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

        #region 步骤 7~8：场景与预制体缺口修补

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
        ///
        /// 为什么走 PrefabUtility.LoadPrefabContents 而不是直接改文件：
        /// 预制体资产的脚本引用由引擎自己维护，手写 YAML 需要正确编码 guid（本项目的 meta guid 并非
        /// 标准 32 位十六进制），极易写坏；而 PrefabUtility 是官方支持的资产编辑通道。
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
            // + Health + Targeting + Combat + Movement + Collider」。
            List<string> missing = new List<string>();

            if (prefab.GetComponent<EntityBase>() == null) missing.Add("EntityBase");
            if (prefab.GetComponent<HealthComponent>() == null) missing.Add("HealthComponent");
            if (prefab.GetComponent<CombatComponent>() == null) missing.Add("CombatComponent");
            if (prefab.GetComponent<TargetingComponent>() == null) missing.Add("TargetingComponent");
            if (prefab.GetComponent<NavMeshAgent>() == null) missing.Add("NavMeshAgent");
            if (prefab.GetComponent<MovementComponent>() == null) missing.Add("MovementComponent");
            if (prefab.GetComponent<EntityAIController>() == null) missing.Add("EntityAIController");
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
            List<string> prefabNotes)
        {
            string removedText = removedNames.Count > 0 ? string.Join("、", removedNames) : "（无）";
            string assetText = assetNotes.Count > 0 ? string.Join("；", assetNotes) : "（无缺口）";
            string prefabText = prefabNotes.Count > 0 ? string.Join("；", prefabNotes) : "（组件完整，未改动）";
            string controllerText = removedControllers.Count > 0
                ? string.Join("、", removedControllers)
                : "（无重复）";

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
                $"  · 资产补写：{assetText}\n" +
                $"  · 小兵预制体：{prefabText}\n" +
                "  · 提醒：双方 Spawner 共用同一条 MidLane，红方会沿与蓝方相同的节点顺序推进" +
                "（先走向 Waypoint1，再折返 Waypoint2）。若要红方严格反向推进，需为红方另建一条节点顺序相反的 LanePath。\n" +
                "  · 提醒：出生点与路径点必须落在已烘焙的 NavMesh 上，否则小兵会永久卡在出生点。\n" +
                "  · 场景已标记为已修改，请按 Ctrl+S 保存后再进入播放模式。");
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
        /// ——只要地面网格比战场坐标范围小（本项目现状：Plane 只有 10×10 且中心偏离原点，
        /// 而出生点在 x = ±8）。这种配置下 NavMeshAgent 会报「离 NavMesh 太远」，
        /// 小兵永久卡在出生点，而 Console 里看不出是"坐标"还是"地面"的问题。
        ///
        /// 这里只提示、不自动烘焙、也不改坐标：烘焙范围、地面尺寸与阵型坐标都属于关卡决策，工具不该替用户拍板。
        /// </summary>
        private static void ValidateNavMeshCoverage()
        {
            NavMeshTriangulation triangulation = NavMesh.CalculateTriangulation();

            if (triangulation.vertices == null || triangulation.vertices.Length == 0)
            {
                Debug.LogWarning(
                    "[AutoSceneBuilder] 当前场景未检测到已烘焙的 NavMesh，小兵与英雄都将无法移动。" +
                    "请先在 Navigation 窗口烘焙 NavMesh（地面需标记为 Walkable）后再开始对局。");
                return;
            }

            Vector3[] points =
            {
                BlueSpawnerPosition, RedSpawnerPosition, Waypoint1Position, Waypoint2Position
            };

            string[] labels = { BlueSpawnerName, RedSpawnerName, Waypoint1Name, Waypoint2Name };

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
                    "对应阵营的小兵会报「离 NavMesh 太远」并永久卡在出生点。" +
                    "请放大地面网格（Plane 默认只有 10×10 单位）或调整阵型坐标，然后重新烘焙 NavMesh。");
            }
        }

        #endregion
    }
}
