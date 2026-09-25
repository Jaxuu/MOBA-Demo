# MOBA Demo 项目记忆

## 硬约束
- `README.md`（需求）> `MOBA_Demo_Plan.md`（实施）；先对齐再动手，不自行发明架构。
- 单机 Unity3D 顶视角 MOBA Demo；MonoBehaviour 组件化 + 基础 FSM + NavMeshAgent，不做网络/ECS。
- ScriptableObject = **只读模板**，运行时状态（生命/冷却/目标）绝不回写；组件间走公开方法 + C# 事件，禁止跨类改私有字段。

## 环境与工具
- 团结引擎 Tuanjie 2022.3.61t14；场景扩展名 **`.scene`**；主场景 `Assets/Scenes/MainScene.scene`。
- Git `origin`=`github.com/Jaxuu/MOBA-Demo.git`，分支 **main**；推送必须走 `gh` 凭据通道（默认 GCM 静默挂起且报错误导）。
- 无 .NET SDK → 只能静态检查 `check_unity_cs.py [--fix-bom]`（改过脚本须跑 `test_check_unity_cs.py`）与 `check_missing_usings.py`（缺 using → CS0246）；替代不了编译，类型成员须人工核对。
- `.cs` 必须 **UTF-8 with BOM**（Write 不写 BOM，写完必跑 `--fix-bom`）；协程内只能 `yield break;`（`return;` 报 CS1622 且查不出）。
- 真实编译看 `C:\Users\14041\AppData\Local\Tuanjie\Editor\Editor.log`（Tuanjie 不是 Unity）；`error CS` 累积，按最后一次 `CompileScripts` 切分；编辑器失焦不刷新。
- **校验引擎 API 是否真实存在（本项目唯一可靠手段）**：`grep -o 'M:UnityEditor\.[A-Za-z.]*' "C:/Program Files/Tuanjie 2022.3.61t14/Editor/Data/Managed/UnityEditor.xml"`（`UnityEngine.xml` 同理）。猜 API 名会让整个编辑器程序集编译失败。
- **反查 guid → 资产路径**（`.meta` 里的 guid 是 base64、与场景内的 32 位十六进制 guid 对不上，无法直接映射）：查 `Editor.log` 里的 `Start importing <Assets/...> using Guid(<hex>)`，这是唯一可靠的映射来源。用它可判定某资产到底有没有被场景引用。

## 命名空间铁律
- `MOBA.<一级目录名>`；例外 `ScriptableObjects`→`MOBA.Data`、`Debug`→`MOBA.Debugging`、`Editor`→`MOBA.Editor`。
- `MOBA.<X>` 的 X 不能与 UnityEngine 类型同名（`MOBA.Debug` 曾致 54 条 CS0234 + Safe Mode）；解法是改命名空间，别名救不了。
- 引用其它命名空间的类型必须补 `using`（C# 只自动搜索当前命名空间及父级前缀）。

## 生命周期
- 组件引用 `Awake` 缓存，配置注入 `Start`；「配置→组件」只在 `EntityBase.ApplyStats` 一处（**Combat 先于 Targeting**）。
- 动态创建：**所有 AddComponent 完成后**才 `Initialize(stats[,team,type])`；此前 `entity.Health/Combat/...` 全 null，必须 `GetComponent<T>()`；`hasInitialized` 防 Start 重复注入（否则回满血）。
- 编辑模式 Awake 不执行 → `entity.Health/Combat` 恒 null；编辑器工具只能读 `Team/EntityType/StatsData`。

## 接口与组件
- `IDamageable`=`HealthComponent`；`ITargetable`=`EntityBase`（不同组件，Combat 用 `GetComponentInParent<IDamageable>()` 桥接）；接口引用销毁后不变 null，先做 `is UnityEngine.Object` 存活检查。
- `IsValidTarget` 是唯一目标合法性入口（未销毁 + activeInHierarchy + 有 Health + 未死 + isSelectable）。
- `MovementComponent` 唯一驱动 NavMeshAgent，速度只走 `SetMoveSpeed()`；建筑无 Movement → `EntityBase.Movement` 可能 null。
- **`MoveTo` 返回 bool**，FSM 必须用返回值作"已下令"标记，否则失败后永不重试、永久卡死。
- `TryAttack` 顺序：解析 IDamageable → 判 IsDead → 写 `nextAttackTime` → `TakeDamage`。
- 地面拾取用 Layer `"Ground"` + `NavMesh.SamplePosition`；旧输入 `Input.GetMouseButtonDown(1)`；日志分级 LogError/LogWarning/Log，重复告警用一次性 `hasReportedXxx`。
- `ValidateDependencies`：RequiresMovement=Hero/Minion，RequiresCombat=非 Base。**无 Collider 的单位永远索敌不到且不报异常**。

## 感知 / FSM（`MOBA.AI`）
- 感知唯一入口 `EntityAIController.TryDetectEnemy(out ITargetable)`（0.25s 节流）；Idle/Move/Chase 禁止直接调 `FindNearestEnemy`（GC Alloc）；缓存校验必须含「仍在索敌半径内」，否则 Chase↔Move 每帧互切；卡死检测：停滞 1s → 清"已下令"标记 → 重下 MoveTo。
- `StateMachine` 纯 C# 类；`ChangeState`：旧 `Exit()` → 赋值 → 新 `Enter()`；同状态 `ReferenceEquals` 忽略；状态名用字符串字面量（禁 `nameof`）；`TryEnter*State` 返回 bool 必须判。`EntityAIController` Awake 只 `new StateMachine()` + 取 EntityBase（**禁读 entity.Health**），保持纯逻辑（不 MoveTo/Stop/TryAttack、不打日志、无 Gizmos）。
- `IdleState` 先 `SetTarget` 再 `TryEnterChaseState()`；`DeadState` 收尾在 `Enter`：停寻路 → 清目标 → 禁用 Collider+NavMeshAgent → `SetSelectable(false)`。
- `LanePath.GetWaypointPosition` 越界返回 `transform.position`；蓝红各一条 LanePath，用节点顺序相反表达方向。

## 防震荡不变量（改阈值前必读）
| 通道 | 进入 | 退出 |
|---|---|---|
| Chase↔Attack | d ≤ AttackRange | d > AttackRange × 1.15 |
| Move/Idle↔Chase | 索敌命中 | d > 索敌半径 × 1.5 |
| Move↔Idle | 还有路径点 | 路线走完 |
| 无追击能力 | `CanChase` 拦下 | — |
| 已被牵出界 | `IsBeyondChaseLeash` 拦下 | — |
- 死区内 `AttackState` **刻意什么都不做**（架构师裁定），不要"顺手优化"；系数用 `Mathf.Max(1f, factor)` 夹住。
- 牵引极限：`ChaseAbandonDistance`（索敌×1.5，量**与目标距离**）、`ChaseLeashDistance`（索敌×2.0，量**锚点距离**）；锚点在 `ChaseState.Enter` 仅 `!hasChaseStartPos` 时记录，**唯一**清除时机 = `MoveState` 抵达路径点 → `ClearChaseStartPos()`。

## 建筑（`MOBA.Units`）
- 塔/基地**不挂 EntityAIController**，用 `TowerController` 直线逻辑：Awake 只取 EntityBase；Start 用 `GetComponent<T>()` 解析并订阅 `OnDied`；Update = ClearInvalidTarget → 超交战半径 ClearTarget → 节流索敌 → TryAttack。
- **交战半径 = `CombatComponent.AttackRange`**，规则只在 `ApplyStats` 对 `EntityType.Tower` 注入（不能放 TowerController.Start，Start 顺序不确定）。
- 死亡收尾统一 `hasHandledDeath` + `enabled=false`；`destroyOnDeath` 默认 false；`BaseCoreController` 只广播 `static event Action<TeamType> OnBaseDestroyed`，不判胜负；静态事件配 `[RuntimeInitializeOnLoadMethod(SubsystemRegistration)]` 重置。

## 对局编排（`MOBA.Gameplay`）
- `MinionSpawner`：一"方"一条兵线（阵营+出生点+LanePath 绑定），无阵营分支；**不在 Start 出兵**；注入顺序不可颠倒：`entity.Initialize(stats, team, Minion)` → 然后 `ai.InitializeAI(lane)`。
- `OnDisable` 必须 `StopSpawning()`（Unity 停协程但不清句柄）；`StartSpawning()` 返回 bool，调用方按返回值计数。
- `MatchController`：延迟 `StartDelay` 统一出兵；只认第一次基地摧毁；`GetOpponentTeam` 是 public static；`FreezeBattlefield()` 遍历 `EntityRegistry.Snapshot()` 停用 AI + TowerController 并 `Movement.Stop()`，单向无解冻。
- 中立阵营：`IsEnemy` 退化路径加 `Team == Neutral → false`。

## 玩家英雄（`HeroController` + `MOBA.Controllers`）
- **英雄刻意不挂 EntityAIController**（FSM 会与玩家指令争夺 Movement/Combat）；英雄**没有 DeadState**，死亡收尾由 `HeroController.HandleDied` 承担。
- `PlayerCommandController`：右键射线 → 比较最近敌方单位与最近地面点命中距离决定攻击/移动；点自己与友方忽略；**攻击指令存在 `TargetingComponent.CurrentTarget`**（唯一存放处）；执行器每帧 ClearInvalidTarget → 射程内 Stop+TryAttack / 射程外按 `chaseRepathDistance` 追击；**不设牵引极限**。
- `CameraController`：固定世界偏移 + `SmoothDamp`，`LateUpdate` 取位置，不跟随旋转；`SnapToTarget()` 编辑模式可用。偏移默认 **`(0,10,-10)`**（45°）；**平移/缩放是显示层增量**（`panOffset` / `zoomScale`，都不写进 `offset` 字段）：期望位置 = `目标 + offset*zoomScale + panOffset`，**注视点必须跟着 panOffset 平移**（否则退化为绕英雄转圈）；中键拖拽平移、中键双击回中、滚轮缩放。

## 编辑器工具（`MOBA.Editor`，`Assets/Scripts/Editor/`）
- `AutoSceneBuilder`（菜单 `MOBA Demo/一键组装测试战场`）：清理旧战场 → 兵线 → 蓝红基地+Spawner → MatchController → **地面覆盖+NavMesh 烘焙** → 英雄（资产/材质/预制体/实例/相机）→ 校验；**只按固定名字**清理自己生成的对象（`ManagedRootNames`），不做全场景扫描删除。英雄数值：生命 **500** / 移速 **6** / 射程 **2.5**（伤害 25、间隔 1s、索敌 8 为自选）。
- **步骤 8（地面与导航）**：只放大不缩小 / 只动内置 Plane 网格（按 `sharedMesh.name=="Plane"`）/ 保持正方形；需求范围 = `GetKeyPoints()` 外扩 8m（本战场得 38×38 中心 (-1,0)）；**自愈清理 Ground 上手工 `NavMeshSurface`** → 现代 API `NavMeshBuilder.CollectSources`(配 `NavMeshBuildMarkup`) + `BuildNavMeshData` 烘焙 → `EditorUtility.CopySerialized` **原地更新**场景的 NavMeshData 资产（**禁用 `StaticEditorFlags.NavigationStatic`，CS0618 已消除**）→ `NavMesh.CalculateTriangulation()` 验证。**改完 Transform 必须 `Physics.SyncTransforms()`**，否则 `Collider.bounds` 读到旧值、收尾校验报假告警。
- **资产 guid 铁律（踩坑教训）**：任何**按 guid 被场景引用**的资产（NavMeshData、材质、预制体…）都**不能**用 `AssetDatabase.CreateAsset` 覆盖重建 —— 它会先删旧资产、guid 随之改变、场景引用直接断链。必须**原地更新内容**（`EditorUtility.CopySerialized` + `SetDirty` + `SaveAssets`）。
- **编辑器导航世界不会自动刷新**：改 NavMeshData 资产后，`NavMesh.CalculateTriangulation` / `SamplePosition` 读到的仍是场景加载时的旧数据 → 收尾校验会报假告警。修法：`NavMesh.RemoveAllNavMeshData()` → `NavMesh.AddNavMeshData(baked)`（先清再加，避免重复执行累加；静态字段持有句柄，下次执行先回收）。
- **步骤 1 附加「幽灵对象清理」**：名字命中 `ManagedRootNames`、但**有父节点**的游离副本（历史遗留：`GameObject (1)/(2)` 下挂着带残余 `EntityBase` 的 `BlueBase`/`RedBase`）→ `Undo.DestroyObjectImmediate`；宿主变空壳且名为 Unity 默认名（`GameObject`/`GameObject (n)`）时一并收掉。判定收窄为「名字命中 + 有父节点」，清完即自限，不做全场景组件扫描。
- 写 private `[SerializeField]` 必须用 `SerializedObject`+`SerializedProperty`；枚举写 `intValue`；**要被工具注入的字段必须 `[SerializeField]`**。
- 资产查找 `AssetDatabase.FindAssets("t:<Type>", folders)`；`t:GameObject` 会命中 .fbx，取预制体限定 `.prefab`；**专用资产必须排除出通用查找**（`IsHeroDedicatedAsset()` 按前缀 `Hero` 过滤）。
- 创建/删除走 `Undo.*`（同一撤销组）；临时对象不登记 Undo，用完 `DestroyImmediate`；收尾只提示不改场景：`ValidateGroundCoverage()` + `ValidateNavMeshCoverage()`。

## 铁律（2026-09-25 定稿）
- **表现与逻辑分离**：逻辑层单向广播事件，表现层只读订阅；**动画事件/特效严禁反向影响伤害判定**（旧"动画事件驱动伤害"方案已废弃）。伤害判定唯一权威 = `CombatComponent` / `SkillComponent`。
- **场景装配必须走 Editor 脚本（`AutoSceneBuilder`），严禁手动拖拽依赖**；手工拖的引用必须回写工具，否则场景重建即丢失。

## 状态 / 待办
- **阶段五已封版**（`MOBA_Demo_Plan.md` 标 ✅，commit `2fb7e10`）：英雄指令层 + 相机（跟随/中键平移/滚轮缩放）+ `AutoSceneBuilder` 步骤 1~12 全自动。实机已确认右键移动控制。
- 两条封版裁决：**不做 `SpawnPoint.cs`**（`MinionSpawner` 用 Transform 足够，无消费方的标记组件 = 死代码）；**相机平移只做中键拖拽**（不做屏幕边缘平移），中键双击回中。
- ⚠️ **`MainScene.scene` 曾长期未保存**：工具的资产创建立刻落盘（HeroPrefab/HeroStats/HeroAttack/HeroGreen.mat/NavMesh.asset 均已生成），但**场景改动只在编辑器内存里**（地面尺寸、PlayerHero 实例、相机绑定、NavMesh 引用）。测出的实机效果来自内存态 → **Ctrl+S 后需补一次提交**。
- ⚠️ 待补测：Profiler 白盒稳态 GC Alloc ≤ 1KB/帧、单帧逻辑 < 2ms（风险点：`TargetingComponent.FindNearestEnemy` 用 `OverlapSphere`，每次分配数组 → 换 `OverlapSphereNonAlloc`）。
- ⚠️ **`NavMesh-Plane.asset` 已删除**（它曾被地面 `Plane` 上手工添加的 `NavMeshSurface` 引用）。场景级导航数据现在是唯一的 `Assets/Scenes/MainScene/NavMesh.asset`（被 `NavMeshSettings` 引用，工具原地更新）。
- ⚠️ **未验证**：本轮 AutoSceneBuilder 改动（现代烘焙 + 幽灵清理 + 自愈）**尚未编译、尚未运行**。回编辑器跑一次菜单，检查日志行 `NavMesh 已烘焙（N 个几何源 / M 个三角形）`；若 M=0 或英雄不动，说明 `CopySerialized` 落盘路径有问题，退回 `UnityEditor.AI.NavMeshBuilder.BuildNavMesh()`（在 amend 前的 `0b1237d` 版本里）。
- `TowerController`/`MatchResultView` 已就位未挂载；`Assets/Art` 目前只有 `Materials/HeroGreen.mat`。
- `EntityStatsData` 缺"旋转速度"；`CancelAttack()` 未实现。
