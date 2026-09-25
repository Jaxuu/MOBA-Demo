# MOBA Demo 项目记忆

## 硬约束
- `README.md`（需求）> `MOBA_Demo_Plan.md`（实施）；先对齐再动手，不自行发明架构。
- 单机 Unity3D 顶视角 MOBA Demo；MonoBehaviour 组件化 + 基础 FSM + NavMeshAgent，不做网络/ECS。
- ScriptableObject = **只读模板**，运行时状态绝不回写；组件间走公开方法 + C# 事件，禁止跨类改私有字段。
- **表现与逻辑分离**：逻辑层单向广播事件，表现层只读订阅；动画/特效/UI 严禁反向影响伤害判定。判定标准：**关掉它，对局结果会不会变？会变 = 逻辑，不变 = 表现**。据此裁定 `Projectile`/`AreaEffectZone` 属逻辑层（效果载体）。
- 场景装配必须走 Editor 脚本（`AutoSceneBuilder`），严禁手动拖拽依赖。

## 环境与工具
- 团结引擎 Tuanjie 2022.3.61t14；场景扩展名 **`.scene`**；主场景 `Assets/Scenes/MainScene.scene`。
- Git `origin`=`github.com/Jaxuu/MOBA-Demo.git`，分支 **main**；推送必须走 `gh` 凭据通道（默认 GCM 静默挂起且报错误导）。
- 无 .NET SDK → 只能静态检查：`check_unity_cs.py [--fix-bom]`（改过脚本须跑 `test_check_unity_cs.py`，现 13 用例）+ `check_missing_usings.py`（缺 using → CS0246）。替代不了编译，类型成员须人工核对。
- `.cs` 必须 **UTF-8 with BOM**（Write 不写 BOM，写完必跑 `--fix-bom`）；协程内只能 `yield break;`（`return;` 报 CS1622 且查不出）。
- 真实编译看 `C:\Users\14041\AppData\Local\Tuanjie\Editor\Editor.log`（Tuanjie 不是 Unity）；`error CS` 是**累计值**，须先 `grep -n "CompileScripts"` 找最后一次真实编译再切分；编辑器失焦不刷新。
- **校验引擎 API 是否真实存在**：`grep -o 'M:UnityEditor\.[A-Za-z.]*' "C:/Program Files/Tuanjie 2022.3.61t14/Editor/Data/Managed/UnityEditor.xml"`（`UnityEngine.xml` 同理）。猜 API 名会让整个编辑器程序集编译失败。
- **反查 guid → 资产路径**：`.meta` 里的 guid 是 base64、与场景内 32 位十六进制对不上。唯一可靠来源 = `Editor.log` 的 `Start importing <Assets/...> using Guid(<hex>)`。
- 装配结果不对时不要猜：读 `Editor.log` + 直接 `grep` 资产文件看序列化结果（比看告警可靠）。

## 命名空间铁律
- `MOBA.<一级目录名>`；例外 `ScriptableObjects`→`MOBA.Data`、`Debug`→`MOBA.Debugging`、`Editor`→`MOBA.Editor`。
- `MOBA.<X>` 的 X 不能与 UnityEngine 类型同名（`MOBA.Debug` 曾致 54 条 CS0234 + Safe Mode）；解法是改命名空间，别名救不了。
- 引用其它命名空间的类型必须补 `using`（C# 只自动搜索当前命名空间及父级前缀）。

## 生命周期与动态创建
- 组件引用 `Awake` 缓存，配置注入 `Start`；「配置→组件」只在 `EntityBase.ApplyStats` 一处（**Combat 先于 Targeting**）。
- 动态创建：**所有 AddComponent 完成后**才 `Initialize(stats[,team,type])`；此前 `entity.Health/Combat/...` 全 null，必须 `GetComponent<T>()`；`hasInitialized` 防 Start 重复注入（否则回满血）。
- 编辑模式 Awake 不执行 → `entity.Health/Combat` 恒 null；编辑器工具只能读 `Team/EntityType/StatsData`。
- **`OnValidate` 会在 `AddComponent` / `ApplyModifiedProperties()` 的同一瞬间被同步调用** → "先挂组件、后注入 statsData"必然在那一刻拿到 null，刷出假告警。修法：校验改 `EditorApplication.delayCall` 延迟（`EntityBase` 已有先例），并在工具收尾加确定性校验方法。

## 接口与组件
- `IDamageable`=`HealthComponent`；`ITargetable`=`EntityBase`（不同组件，Combat 用 `GetComponentInParent<IDamageable>()` 桥接）；**接口引用销毁后不变 null**，先做 `is UnityEngine.Object` 存活检查。
- `IsValidTarget` 是唯一目标合法性入口（未销毁 + activeInHierarchy + 有 Health + 未死 + isSelectable）。
- `MovementComponent` 唯一驱动 NavMeshAgent，速度只走 `SetMoveSpeed()`；建筑无 Movement → `EntityBase.Movement` 可能 null。
- **`MoveTo` 返回 bool**，FSM 必须用返回值作"已下令"标记，否则失败后永不重试、永久卡死。
- `TryAttack` 顺序：解析 IDamageable → 判 IsDead → 写 `nextAttackTime` → `TakeDamage`（**先重置冷却再结算伤害**，防同帧重入打出第二发）。
- 地面拾取用 Layer `"Ground"` + `NavMesh.SamplePosition`；旧输入 `Input.GetMouseButtonDown(1)`；日志分级 LogError/LogWarning/Log，重复告警用一次性 `hasReportedXxx`。
- `ValidateDependencies`：RequiresMovement=Hero/Minion，RequiresCombat=非 Base。**无 Collider 的单位永远索敌不到且不报异常**。
- **依赖自校验正确姿势**：`Awake` 校验只适用于"挂载时必然同时存在"的硬依赖（如 `[RequireComponent]`）；对"可能晚挂"的依赖（`EntityBase` 等）必须"延迟解析 + 首次使用时 `LogError` 一次"。静默失效典型症状：技能放不出去、蓝和 CD 都没动、Console 一片安静。

## 感知 / FSM（`MOBA.AI`）
- 感知唯一入口 `EntityAIController.TryDetectEnemy(out ITargetable)`（0.25s 节流）；Idle/Move/Chase 禁止直接调 `FindNearestEnemy`（GC Alloc）；缓存校验必须含「仍在索敌半径内」，否则 Chase↔Move 每帧互切；卡死检测：停滞 1s → 清"已下令"标记 → 重下 MoveTo。
- `StateMachine` 纯 C# 类；`ChangeState`：旧 `Exit()` → 赋值 → 新 `Enter()`；同状态 `ReferenceEquals` 忽略；状态名用字符串字面量（禁 `nameof`）；`TryEnter*State` 返回 bool 必须判。`EntityAIController` Awake 只 `new StateMachine()` + 取 EntityBase（**禁读 entity.Health**），保持纯逻辑。
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
- 死区内 `AttackState` **刻意什么都不做**（架构师裁定）；系数用 `Mathf.Max(1f, factor)` 夹住。
- 牵引极限：`ChaseAbandonDistance`（索敌×1.5，量**与目标距离**）、`ChaseLeashDistance`（索敌×2.0，量**锚点距离**）；锚点在 `ChaseState.Enter` 仅 `!hasChaseStartPos` 时记录，**唯一**清除时机 = `MoveState` 抵达路径点。

## 建筑（`MOBA.Units`）
- 塔/基地**不挂 EntityAIController**，用 `TowerController` 直线逻辑：Awake 只取 EntityBase；Start 用 `GetComponent<T>()` 解析并订阅 `OnDied`；Update = ClearInvalidTarget → 超交战半径 ClearTarget → 节流索敌 → TryAttack。
- **交战半径 = `CombatComponent.AttackRange`**，规则只在 `ApplyStats` 对 `EntityType.Tower` 注入（不放 TowerController.Start，Start 顺序不确定）。
- 死亡收尾统一 `hasHandledDeath` + `enabled=false`；`destroyOnDeath` 默认 false；`BaseCoreController` 只广播 `static event Action<TeamType> OnBaseDestroyed`，不判胜负；静态事件配 `[RuntimeInitializeOnLoadMethod(SubsystemRegistration)]` 重置。
- 基地 = 空根节点 + `CreatePrimitive(Cube)` 子节点 Body（2.5³，不用 3³：会视觉贴到英雄胶囊）；碰撞体复用 Cube 自带 BoxCollider（手工加会让同一基地在 `OverlapSphere` 里出现两次）；属性用确定性资产 `ScriptableObjects/BaseStats.asset`（生命 3000，用占位查找会退回 MinionStats 的 100 生命 → 几波兵推掉基地）。

## 对局编排（`MOBA.Gameplay`）
- `MinionSpawner`：一"方"一条兵线（阵营+出生点+LanePath 绑定），无阵营分支；**不在 Start 出兵**；注入顺序不可颠倒：`entity.Initialize(stats, team, Minion)` → 然后 `ai.InitializeAI(lane)`；`OnDisable` 必须 `StopSpawning()`；`StartSpawning()` 返回 bool，调用方按返回值计数。
- `MatchController`：延迟 `StartDelay` 统一出兵；只认第一次基地摧毁；`GetOpponentTeam` 是 public static；`FreezeBattlefield()` 遍历 `EntityRegistry.Snapshot()` 停用 AI + TowerController 并 `Movement.Stop()`，单向无解冻。
- 中立阵营：`IsEnemy` 退化路径加 `Team == Neutral → false`。

## 玩家英雄（`HeroController` + `MOBA.Controllers`）
- **英雄刻意不挂 EntityAIController**（FSM 会与玩家指令争夺 Movement/Combat）；英雄**没有 DeadState**，死亡收尾由 `HeroController.HandleDied` 承担（停寻路→清目标→禁指令层→禁 NavMeshAgent+Collider→SetSelectable(false)→退订→广播 `OnHeroDied`）。
- `PlayerCommandController`：右键射线 → 比较最近敌方单位与最近地面点命中距离决定攻击/移动；点自己与友方忽略；**攻击指令存在 `TargetingComponent.CurrentTarget`**（唯一存放处）；执行器每帧 ClearInvalidTarget → 射程内 Stop+TryAttack / 射程外按 `chaseRepathDistance` 追击；**不设牵引极限**。
- `CameraController`：固定世界偏移 + `SmoothDamp`，`LateUpdate` 取位置，不跟随旋转；`SnapToTarget()` 编辑模式可用。偏移默认 **`(0,10,-10)`**（45°）；**平移/缩放是显示层增量**（`panOffset`/`zoomScale`，都不写进 `offset` 字段）：期望位置 = `目标 + offset*zoomScale + panOffset`，**注视点必须跟着 panOffset 平移**（否则退化为绕英雄转圈）；中键拖拽平移、中键双击回中、滚轮缩放。

## 阶段六：技能系统（已实机验收通过）
- **命名空间 `MOBA.Skills`**（`Assets/Scripts/Skills/`）。链路：`PlayerSkillController`(Q/W) → `SkillComponent.TryCast(slot, groundPoint, target, out failReason)` → 前摇(逻辑计时) → `ProjectileSpawner`→`Projectile` / `AreaEffectZone` → **`SkillEffectResolver`（伤害/Buff 唯一结算入口）** → `HealthComponent` / `BuffComponent`。
- **`TryCast` 校验链顺序：①自身状态(死亡/眩晕/前摇) → ②配置 → ③冷却 → ④蓝量 → ⑤距离 → ⑥目标合法性**；`isCasting` 排在冷却之前（"刚放完立刻再按"命中的是前摇硬直）。**三参重载已删**，9 个拒绝分支各给人话原因 + 量化信息。
- **拒绝路径零副作用是结构保证**：扣蓝与写 CD 排在整条校验链之后 → 四条拒绝路径不广播任何事件 → 表现层自然不播。
- **眩晕与施法前摇共用同一把锁**（`SetMovementLocked` / `SetAttackLocked`），两个持有者互相检查对方状态。
- **弹道自研距离判定，绝不用物理碰撞体**（命中条件含"距离 ≤ 本帧位移"防高速穿透；抵达判定点必须无条件回收）。**弹道 prefab 严禁带 Collider**（否则成为右键可点候选、占索敌候选位）。
- **范围场必须对同一 tick 去重**（一个单位常有多碰撞体，不去重 = 范围伤害翻倍）；用 `OverlapSphereNonAlloc` + 32 缓冲。
- **`BuffComponent` 不是英雄专属**：减速/眩晕/护盾都经它落地，**任何需要被控制的单位都必须挂它**（含 `MinionPrefab`）。缺它的症状 = 运行期一条"找不到 BuffComponent"后静默放弃（技能放了、圈画了、小兵没慢）。
- 配置注入"双保险"：`EntityStatsData.skillQ/skillW` 为主，`SkillComponent` 直挂为兜底。**`PatchHeroStatsGaps` 只补空字段、不覆盖、不碰资产本体**（保 guid）；技能资产必须 `Hero` 前缀（被 `IsHeroDedicatedAsset` 拦下）。
- 刻意不做：`BuffData` 资产（无"非技能来源的 buff"）、`SkillTargetSelector`（三处已覆盖）、`SpawnPoint.cs`。**「指定友方单位」未实现**（`TryValidateTarget` 只接受敌方）——友方护盾技能的已知扩展点。

## 编辑器工具（`MOBA.Editor`，`Assets/Scripts/Editor/`）
- `AutoSceneBuilder`（菜单 `MOBA Demo/一键组装测试战场`，现 2993 行）：清理旧战场 → 兵线 → 蓝红基地+Spawner → MatchController → **地面覆盖+NavMesh 烘焙** → 英雄（资产/材质/预制体/实例/相机）→ 技能资产 → 弹道 → 校验；**只按固定名字清理**自己生成的对象（`ManagedRootNames`），不做全场景扫描。英雄数值：生命 **500** / 移速 **6** / 射程 **2.5**（伤害 25、间隔 1s、索敌 8 自选）。
- **步骤 8（地面与导航）**：只放大不缩小 / 只动内置 Plane 网格（按 `sharedMesh.name=="Plane"`）/ 保持正方形；范围 = `GetKeyPoints()` 外扩 8m（38×38 中心 (-1,0)）；自愈清理 Ground 上手工 `NavMeshSurface` → `NavMeshBuilder.CollectSources`(配 `NavMeshBuildMarkup`) + `BuildNavMeshData` 烘焙 → `EditorUtility.CopySerialized` **原地更新**场景 NavMeshData 资产 → `NavMesh.CalculateTriangulation()` 验证。**改完 Transform 必须 `Physics.SyncTransforms()`**。
- **资产 guid 铁律**：任何**按 guid 被场景引用**的资产（NavMeshData、材质、预制体…）都**不能**用 `AssetDatabase.CreateAsset` 覆盖重建（会先删旧资产、guid 改变、场景引用断链）；必须原地更新内容（`CopySerialized` + `SetDirty` + `SaveAssets`）。
- **编辑器导航世界不自动刷新**：改 NavMeshData 后 `CalculateTriangulation`/`SamplePosition` 仍读旧数据 → 修法 `NavMesh.RemoveAllNavMeshData()` → `NavMesh.AddNavMeshData(baked)`（先清再加）。
- **步骤 1 附加「幽灵对象清理」**：名字命中 `ManagedRootNames`、**但有父节点**的游离副本 → `Undo.DestroyObjectImmediate`；宿主变空壳且名为 Unity 默认名时一并收掉。判定收窄为「名字命中 + 有父节点」，不做全场景组件扫描。
- 写 private `[SerializeField]` 必须用 `SerializedObject`+`SerializedProperty`；枚举写 `intValue`；**要被工具注入的字段必须 `[SerializeField]`**。Unity 内置组件的**公开属性**（`Image.type` / `Text.font` 等）直接赋值即可。
- 资产查找 `AssetDatabase.FindAssets("t:<Type>", folders)`；`t:GameObject` 会命中 .fbx，取预制体限定 `.prefab`；专用资产必须排除出通用查找（`IsHeroDedicatedAsset()` 按 `Hero` 前缀过滤）。
- 创建/删除走 `Undo.*`（同一撤销组）；临时对象不登记 Undo，用完 `DestroyImmediate`；收尾只提示不改场景。

## 阶段七：UI 与可视化（代码已落地，待实机验收）
- **`com.unity.ugui: 1.0.0` 已加入 `Packages/manifest.json`**（编辑器内置包，离线可解析）。`UnityEngine.UI` 可用；**TMP 仍不可用**（不在内置包清单）→ 全部文字走旧版 `Text`。
- **新增 13 个文件**：`Core/PrefabPool.cs`；`UI/` 下 `WorldHealthBarManager/View`、`DamagePopupManager/View`、`HeroHUDView`、`SkillSlotView`、`KillFeedView`、`ScoreboardView`、`RespawnOverlayView`、`UIFontProvider`；`Gameplay/MatchStatsTracker.cs`；`Editor/AutoSceneBuilder.UIAssembly.cs`（partial 分册，步骤 17）。
- **逻辑层增量（全为新增成员，阶段一~六零改动）**：`HealthComponent.OnDamaged(HealthComponent,float,float,EntityBase)`（**发送者在前**：healthDamage=实际扣减量 / shieldAbsorbed / source；**护盾全吸收也广播**）+ `Revive()`；`ManaComponent.RestoreFull()`；`EntityRegistry.OnEntityRegistered/OnEntityUnregistered`（只在真正增删时广播，`ResetStaticState` 里置 null）；`MatchConfigData.respawnTime`；`HeroController.Revive()/OnHeroRespawned/respawnAnchor`；`MatchController` 复活编排。
- **表现层挂载策略**：血条与飘字**不由单位挂载**，由 Manager 订阅 `EntityRegistry` 事件统一挂/还池 → **单位预制体零改动**，动态小兵自动覆盖。
- **`PrefabPool<T>` 契约**：`Get()` 池空返回 null + 一次性告警（绝不 Instantiate）；`Release()` 只 SetParent + 复位变换 + `SetActive(false)`，**视图在自身 `OnDisable` 里退订与复位**（池不认识具体视图类型）；`ActiveItems` 只许遍历不得增删。
- **血条**：锚点 = Bind 时**一次性测量 collider 合并包围盒顶部**并缓存偏移；billboard = 复制相机旋转；恒定像素高度 = `localScale ∝ 与相机距离`；显隐 = 死亡/满血/相机背后；**隐藏只禁 Canvas 组件、绝不禁自身物体**（禁自身会触发 OnDisable → 自动解绑）。`[DefaultExecutionOrder(100)]` 保证晚于相机 LateUpdate。
- **飘字**：**共享世界画布 + 子 Text**（1 draw call）；**单管理器循环 Tick**（不给每个飘字挂 Update）；**一次伤害一条飘字**（全吸收→灰蓝"吸收 N"，否则红色实际扣减量）。
- **复活归属（D4）**：`MatchController` 统筹倒计时（`isRespawning` 独立布尔——**StartCoroutine 会同步执行到第一个 yield**，用协程句柄判断会让订阅方读到"没在复活"），`HeroController.Revive()` 执行逆操作。**三个致命顺序点**：重新订阅 `OnDied` → **先启用 NavMeshAgent 再 Warp** → Warp 失败退化直写坐标并 LogError。
- **CD 遮罩**：每帧读 `GetCooldownRemaining`（冷却没有变化事件），**秒数只在整秒跳变时写文本**（否则 60 次/秒 ToString 必顶掉 GC≈0）。`Image.fillMethod` 的 setter **会重置 fillOrigin** → 必须先设 fillMethod 再设 fillOrigin；`Image.Origin360.Top = 2`；`fillClockwise` 默认 true。
- **击杀统计（D3）**：只统计英雄；归属读 `HealthComponent.LastDamageSource`；`MatchStatsTracker` 是**唯一计数器**（计分板只读它）。`OnDied` 是既有 `Action`（无发送者、不能改签名）→ 统计侧用每单位闭包（死亡低频）；受伤高频所以新事件带发送者以避免闭包。
- **工具步骤 17**：`AutoSceneBuilder` 已改 `partial` + 拆分册；`ManagedRootNames` 增加 `UIRoot`/`WorldUIRoot`。占位白图 = 运行时生成 4×4 PNG → `TextureImporter` 改 Sprite（**所有 Image 共用一张主贴图才能合批**）；字体三级回退（`LegacyRuntime.ttf` → `Arial.ttf` → 运行期 `UIFontProvider` 系统字体）；`ValidateUISetup` 逐个读 `SerializedObject` 给确定性结论。
- **uGUI 的两个静默失效**（必须显式查）：`Text.font == null` 与 `Image.sprite == null` 都**不报错、只是不显示**。
- **补齐既有缺口**：`MatchResultView` 此前从未被装配进场景 → 步骤 17 现已创建并注入（基地摧毁后屏幕上有结算界面）。
- ⚠️ **待实机**：本轮只做了静态检查（62 文件七项全绿、using 0 疑点、自测 13/13），**无 .NET SDK → 未经编译**。回编辑器后先跑菜单 `MOBA Demo/一键组装测试战场`，看日志行「UI 与可视化（阶段七）：…」与是否有 `UI 装配存在 N 处问题`。

## 白盒期尸体清理（阶段七验收后补丁，2026-09-25）
- **`Core/EntityVisuals.cs`（新增）**：`SetRenderersEnabled(GameObject, bool)`，用 `GetComponentsInChildren<Renderer>(true)`（基类 Renderer → 覆盖 Mesh/SkinnedMesh/粒子）。**英雄与小兵共用这一个工具**，避免两处各写一遍遍历。
- **英雄**：`HeroController.HandleDied` 第 5 步隐藏肉身、`Revive` 第 6 步恢复，**严格对称**（漏恢复的症状 = 复活后能走能打但看不见人，极易误判成相机问题）。
- **小兵**：`EntityAIController` 新增 `corpseLingerSeconds = 2f`（+ `logDeathCleanup`）+ `HandleCorpseCleanup()` + 协程 `DestroyCorpseAfterDelay()` → 立刻隐藏 + 2 秒后 `Destroy(gameObject)`。**两条死亡路径都要接**：`HandleOwnerDied()` 与 `InitializeAI` 里"初始化时已死亡"的兜底分支。
- 隐藏与销毁**分两步**：死亡瞬间的伤害飘字/击杀播报还在播，飘字锚点就在该单位身上 → 先"看起来死了"再"真的消失"。不用 `Destroy(gameObject, delay)`（无法查询、无法取消）。
- **销毁单位是安全的**（已核查）：`MinionSpawner` 不持有已生成单位引用；`Projectile` 有 `is UnityEngine.Object` 存活检查；`TargetingComponent.IsAlive` 同族检查；`EntityBase.OnDisable` 自动注销注册表 → 血条归还池、飘字/统计同步退订；`LastDamageSource` 在 `OnDied` 时就已读完。
- ⚠️ **阶段八迁移点**：本补丁属白盒期权宜手段（逻辑层写 `Renderer.enabled`，虽幂等且结果无关，但严格说属表现）。接入真实模型与死亡动画后应由表现层接管，届时**删掉这两处调用点**（这也是把调用点集中成两处的原因）。
- ⚠️ **教训（本轮第二次踩）**：在 `[Tooltip]` / 日志字符串里写 ASCII 双引号会提前闭合字符串（CS1002）。写完必须立刻跑 `check_unity_cs.py --fix-bom`（第 7 项「中文越界」专治此类）。

## 状态 / 待办
- **阶段一~五已封版**（阶段五 commit `2fb7e10`）。两条封版裁决：不做 `SpawnPoint.cs`；相机平移只做中键拖拽（中键双击回中），不做屏幕边缘平移。
- **阶段六已封版并通过实机验收**。
- **阶段七代码已全部落地**（7.0~7.8，D1~D10 全按建议执行），**UI 已通过实机验收**；白盒期尸体清理补丁已实装（见上一节），待实机确认。
- ⚠️ 待补测：Profiler 白盒稳态 GC Alloc ≤ 1KB/帧、单帧逻辑 < 2ms（风险点：`TargetingComponent.FindNearestEnemy` 用 `OverlapSphere` 每次分配数组 → 换 `OverlapSphereNonAlloc`，`AreaEffectZone` 已有同族范式）。
- ⚠️ `MainScene.scene` 历史上有"改动只在编辑器内存里"的问题：测出效果后务必 **Ctrl+S 并补一次提交**。
- `TowerController` 仍"已就位、未挂载"（6B 顺延）；`Assets/Art` 目前只有 `Materials/HeroGreen.mat` 与阶段七新增的 `UI/UIWhite.png`。
- `EntityStatsData` 缺"旋转速度"；`CancelAttack()` 未实现；`HealthComponent.Heal()` 仍无调用方（故未加 `OnHealed`）。
