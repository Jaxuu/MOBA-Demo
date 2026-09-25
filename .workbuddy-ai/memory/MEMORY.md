# MOBA Demo 项目记忆

## 权威与硬约束
- `README.md`（需求）> `MOBA_Demo_Plan.md`（实施细节）。开发前先对齐，不自行发明架构。
- 单机 Unity 3D 顶视角 MOBA Demo；原生 MonoBehaviour + 基础 FSM + NavMeshAgent，不做网络/ECS。
- ScriptableObject = **只读模板**，运行时状态（当前生命/冷却/目标）绝不回写。
- 组件间走公开方法 + C# 事件，禁止跨类改私有字段。第一版不建对象池/技能系统/属性修改器/事件总线。

## 环境与工具
- Git：已推送 `origin` = `https://github.com/Jaxuu/MOBA-Demo.git`，分支 **`main`**（不是 master）。
  `.gitignore` = github/gitignore 官方 Unity 规则 + 追加 `/.codely-cli/`。
  抓 GitHub 原始文件走 `cdn.jsdelivr.net`（`raw.githubusercontent.com` 直连超时）。
  **推送必须走 `gh` 凭据通道**（已跑 `gh auth setup-git`）：默认 helper-selector(GCM) 会静默挂起 ~30s，
  且只报 `failed to push some refs` 无 fatal 行，易误判为网络问题。
- 团结引擎 Tuanjie 2022.3.61t14。**场景扩展名 `.scene`**（不是 .unity）；主场景 `Assets/Scenes/MainScene.scene`。
- 无 .NET SDK → 只能静态检查 `python .workbuddy-ai/tools/check_unity_cs.py [--fix-bom]`
  （六项：BOM / 括号配平 / 命名空间归属 / 双引号奇偶 / 危险命名空间末段 / 迭代器内裸 return）。
  改过该脚本必须跑 `python .workbuddy-ai/tools/test_check_unity_cs.py`。它替代不了编译，类型成员须人工核对。
- 第七类检查 `python .workbuddy-ai/tools/check_missing_usings.py`：跨命名空间引用类型但**缺 using**（CS0246）。
  引用新命名空间的类型后必跑；会剥离注释与字符串字面量以避免 `[Tooltip("…")]` 假阳性。
- 真实编译结果看 `C:\Users\14041\AppData\Local\Tuanjie\Editor\Editor.log`（是 **Tuanjie** 不是 Unity）：
  `error CS` 是**累积**的，必须按最后一次 `CompileScripts` 行号切分；编辑器**失焦时日志不刷新**。
- 所有 `.cs` 必须 **UTF-8 with BOM**（Write 工具不写 BOM，写完必须跑 `--fix-bom`）。
- 协程（迭代器）内只能用 `yield break;`，写 `return;` 报 CS1622 且静态检查查不出（已踩过）。

## 命名空间铁律
- `MOBA.<一级目录名>`，子目录并入父目录；例外：`ScriptableObjects`→`MOBA.Data`、`Debug`→`MOBA.Debugging`、`Editor`→`MOBA.Editor`。
- `MOBA.<X>` 的 X 绝不能与 UnityEngine 类型同名（`MOBA.Debug` 曾致 54 条 CS0234 + 全项目 Safe Mode）。
  禁用词见 `check_unity_cs.py` 的 `DANGEROUS_SEGMENTS`。**解法是改命名空间本身，别名救不了其他文件。**
- 引用**其它命名空间**的类型必须文件头补 `using`，C# 只自动搜索当前命名空间及其父级前缀（`AutoSceneBuilder`
  引用 `MOBA.AI.EntityAIController` 漏 `using MOBA.AI;` → 3 条 CS0246）。写完必跑 `check_missing_usings.py`。

## 生命周期 / 动态创建
- 组件引用在 `Awake` 缓存，配置注入在 `Start`；「配置→组件」只在 `EntityBase.ApplyStats` 一处实现（**Combat 必须先于 Targeting**）。
- 动态创建：**所有 AddComponent 完成后**才调 `Initialize(stats[,team,type])`（内部重跑 CacheComponents）。
  此前 `entity.Health/Combat/Targeting/Movement` 全为 null，取组件必须 `GetComponent<T>()`，不能读 `entity.X`
  （事故：`Stage2AutoTester` 静默中止）。`hasInitialized` 保证 Start 不重复注入（否则回满血）。
- **编辑模式下** Awake 不执行 → `entity.Health/Combat` 恒为 null；编辑器工具只能读 `Team/EntityType/StatsData` 等序列化字段。

## 接口与组件
- `IDamageable` 唯一实现 = `HealthComponent`；`ITargetable` 实现 = `EntityBase`（不同组件，Combat 必须
  `GetComponentInParent<IDamageable>()` 桥接）。接口引用销毁后不变 null，先做 `is UnityEngine.Object` 存活检查。
- `IsValidTarget` 是全项目唯一目标合法性入口（未销毁 + activeInHierarchy + 有 Health + 未死亡 + isSelectable）。
- `MovementComponent` 唯一驱动 NavMeshAgent，速度只走 `SetMoveSpeed()`；建筑无 Movement → `EntityBase.Movement` 可能 null；
  `Stop()` 有 `!agent.enabled` 早退分支。
- **`MovementComponent.MoveTo` 返回 bool**，FSM 必须用返回值作"已下令"标记，否则失败后永不重试、单位永久卡死
  （一次性告警 `hasReportedNotOnNavMesh/UnreachableDestination`）。
- `CombatComponent.TryAttack` 顺序铁律：解析 IDamageable → 判 IsDead → 写 `nextAttackTime` → `TakeDamage`。
- 地面拾取用 Layer `"Ground"` + `NavMesh.SamplePosition`；旧输入 `Input.GetMouseButtonDown(1)`。
- 日志分级：致命 LogError / 可疑 LogWarning / 正常 Log（受开关控制）；重复告警用一次性 `hasReportedXxx`；
  致命配置错误 Awake 报出后 `enabled=false`。
- `EntityBase.ValidateDependencies` 按类型校验（`RequiresMovement`=Hero/Minion；`RequiresCombat`=非 Base）；
  **无 Collider 的单位永远不会被索敌到，且不报任何异常**（最隐蔽的配置错误）。

## 感知 / FSM（`MOBA.AI`）
- 感知唯一入口 `EntityAIController.TryDetectEnemy(out ITargetable)`（`detectionInterval` 0.25s 节流）；
  Idle/Move/Chase **禁止**直接调 `FindNearestEnemy`（内含 OverlapSphere GC Alloc）。`detectionTimer` 在 `Update` 递减。
  缓存校验必须含「仍在索敌半径内」，否则 Chase↔Move 每帧互切；缓存失效立刻补查。
- 卡死检测 `IsStagnant(MovementComponent)` + `StagnationDuration`(1s)/`StagnationVelocitySqrThreshold`(0.1)；
  计时器归各状态所有。连续停滞 1s → 清"已下令"标记 → 重下 MoveTo。
- `StateMachine` 是纯 C# 类（`MOBA.AI`）。`ChangeState` 时序：旧 `Exit()` → 赋值 → 新 `Enter()`；同状态 `ReferenceEquals` 忽略。
  状态由 `EntityAIController` 构造持有、互不引用；状态名用**字符串字面量**（禁 `nameof`）；`TryEnter*State` 返回 bool 必须判。
- `EntityAIController`：Awake 只 `new StateMachine()` + 取 EntityBase（**禁读 entity.Health**）；Start 兜底 `InitializeAI(lanePath)`；
  Update→Tick；保持纯逻辑（不得 MoveTo/Stop/TryAttack、不打日志、无 Gizmos → 交 `Debug/FSMDebugView`）。
- `IdleState`：先 `SetTarget` 再 `TryEnterChaseState()`。`DeadState` 收尾放 `Enter`、`Exit` 空实现：
  停寻路 → 清目标 → **禁用全部 Collider + NavMeshAgent** → `SetSelectable(false)`。
- `LanePath.GetWaypointPosition` 越界返回 `transform.position`（绝不 Vector3.zero）。蓝红各持一条 LanePath，用**节点顺序相反**表达方向。

## 防震荡不变量（改阈值前必读）
| 通道 | 进入 | 退出 |
|---|---|---|
| Chase↔Attack | d ≤ AttackRange | d > AttackRange × 1.15（滞回死区，退出 > 进入）|
| Move/Idle↔Chase | 索敌命中 | d > 索敌半径 × 1.5 |
| Move↔Idle | 还有路径点 | 路线走完（Idle 切回前查 `HasRemainingWaypoints`）|
| 无追击能力 → Chase | 用 `CanChase`（有攻击距离**且**有 Movement）拦下 | — |
| 已被牵出界 → Chase | 用 `IsBeyondChaseLeash` 拦下 | — |

- 死区内 `AttackState` **刻意什么都不做**（架构师裁定：稳定性优先，容忍 15% 发呆），不要"顺手优化"。
  系数在代码层 `Mathf.Max(1f, factor)` 夹住，不能只靠 `[Min(1f)]`。
- `ChaseState` 放弃追击必须 `ClearTarget()`；`MoveState` 用 `hasIssuedMoveForCurrentWaypoint`（`Enter` 清标记）。
- **牵引极限**两道闸门：`ChaseAbandonDistance`（索敌×1.5，量**与目标距离**）、`ChaseLeashDistance`（索敌×2.0，量**锚点距离**）。
  锚点：① `ChaseState.Enter` 仅 `!hasChaseStartPos` 时记录；② **唯一**清除时机 = `MoveState` 抵达路径点 → `ClearChaseStartPos()`
  （不能放 `MoveState.Enter`）；③ `TryEnterChaseState` 必须用 `IsBeyondChaseLeash` 拒绝重追。

## 建筑（`MOBA.Units`）
- 塔/基地**不挂 `EntityAIController`**（无 Movement → `CanChase` false → FSM 空转且 MoveState 报错），用 `TowerController` 直线逻辑：
  Awake 只取 EntityBase；Start 用 `entity.GetComponent<T>()` 解析并订阅 `OnDied`；
  Update = `ClearInvalidTarget` → 超交战半径 `ClearTarget` → 节流索敌 → `TryAttack`。
- **交战半径 = `CombatComponent.AttackRange`**（塔不再持有独立索敌半径）。规则只在 `ApplyStats` 对 `EntityType.Tower` 注入
  （不能放 `TowerController.Start`，同物体 Start 顺序不确定）；副作用：塔的 `DetectionRange` 不生效。
- 死亡收尾统一 `hasHandledDeath` + `enabled=false`；`destroyOnDeath` 默认 false；不重复 `SetSelectable(false)`。
- `BaseCoreController` 只广播 `static event Action<TeamType> OnBaseDestroyed`，不判胜负；静态事件配
  `[RuntimeInitializeOnLoadMethod(SubsystemRegistration)]` 重置。

## 对局编排（`MOBA.Gameplay`）
- `MinionSpawner`：一"方"一条兵线（阵营 + 出生点 + LanePath 三者绑定），代码里无阵营分支；**不在 Start 出兵**（由 MatchController 控制）。
  注入顺序不可颠倒：`entity.Initialize(stats, team, EntityType.Minion)` → **然后** `ai.InitializeAI(targetLane)`。
- `OnDisable` 必须 `StopSpawning()`（Unity 会停协程但**不清句柄**，否则再也出不了兵）。
  `StartSpawning()` 返回 bool（含 `isActiveAndEnabled` 校验），调用方按返回值计数。
  波次基准 = 上一波首个出生时刻（`waveInterval - elapsed`），等待值 `Mathf.Max(0.02f, …)` 夹住。
- `MatchController`：OnEnable/OnDisable 订阅注销静态事件；延迟 `StartDelay` 秒统一出兵；只认第一次基地摧毁；
  对局结束日志不受开关控制。`GetOpponentTeam` 是 public static（`MatchDebugView` 复用）。
- `FreezeBattlefield()` 遍历 `EntityRegistry.Snapshot()`（EntityBase 自动登记，静态字段配 SubsystemRegistration 重置），
  停用 `EntityAIController` + `TowerController` 并 `Movement.Stop()`；`freezeBattlefieldOnMatchEnd` 默认开；
  不碰玩家输入、不销毁物体、**单向无解冻**。
- 场景依赖：出生点必须在已烘焙 NavMesh 上；小兵预制体需挂 EntityBase + EntityAIController + Health + Targeting
  + Combat + Movement + **Collider**。
- 中立阵营：`IsEnemy` 退化路径加 `Team == Neutral → false`（单向约束"能打谁"）。

## 编辑器工具（`MOBA.Editor`，`Assets/Scripts/Editor/`）
- 放 `Assets/Scripts/Editor/`（自动编入 Assembly-CSharp-Editor，不进运行时包）。
- `AutoSceneBuilder`（菜单 `MOBA Demo/一键组装测试战场`）：清理旧战场 → MidLane/Waypoint → 蓝红基地 + Spawner
  → Battlefield(MatchController) → 资产自动注入。**只按固定名字**清理自己生成的对象，不做全场景组件扫描删除。
- 写 private `[SerializeField]` 必须用 `SerializedObject` + `SerializedProperty`（反射不被序列化系统承认）；
  枚举写 `intValue`（`enumValueIndex` 只在枚举值从 0 连续时才等于枚举值）；字段名是字符串，取不到必须报错。
- 资产查找：`AssetDatabase.FindAssets("t:<TypeName>", folders)` + `LoadAssetAtPath<T>` 二次确认；
  `t:GameObject` 会命中 .fbx，取预制体要限定 `.prefab`。
- 创建/删除走 `Undo.RegisterCreatedObjectUndo` / `Undo.AddComponent<T>` / `Undo.DestroyObjectImmediate`，
  整次操作归入同一个撤销组。

## 已知待办 / 状态
- 资产：`Assets/ScriptableObjects/{MinionStats, MinionAttack, SpawnConfig, MatchConfig}.asset`；预制体仅
  `Assets/Prefabs/MinionPrefab.prefab`（组件齐全含 CapsuleCollider）。**无基地/塔专用资产**（基地暂借 MinionStats）。
- 已就位但未挂到预制体/场景：`TowerController` / `MatchResultView`。
- 剩余：顶视角相机（跟随/平移/缩放）；`SpawnPoint.cs`；塔/基地预制体；把 Gameplay 场景加入 Build Settings
  （`MatchResultView` 的 `LoadScene(0)` 依赖）。
- `EntityStatsData` 缺"旋转速度"；`CombatComponent.CancelAttack()` 未实现（等做前摇再补，不要写空方法）。
  `FindNearestEnemy` 的 `OverlapSphere` 可换 `OverlapSphereNonAlloc`。
- 场景装配后必须实测：停滞自愈、出生点是否在 NavMesh 上、预制体是否挂了 Collider（三条都有告警兜底，但告警≠跑通）。
