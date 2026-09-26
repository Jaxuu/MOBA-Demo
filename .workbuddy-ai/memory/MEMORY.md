# MOBA Demo 项目记忆

## 铁律
- README（需求）> Plan（实施）；代码偏离文档就改代码，文档落后就改文档。
- 单机 Unity 顶视角 MOBA：MonoBehaviour + FSM + NavMeshAgent；不做网络/ECS。
- SO = 只读模板，运行时不回写；组件间只走公开方法 + C# 事件。
- 表现/逻辑分离，判据「关掉它对局结果会不会变」；Projectile / AreaEffectZone 属逻辑层。
- 场景装配只能走 Editor 脚本（AutoSceneBuilder）；工具必须幂等（ManagedRootNames 清理、guid 被引用资产只 CopySerialized、SerializedObject 注入）。

## 环境
- 团结 Tuanjie 2022.3.61t14；内置渲染管线；主场景 Assets/Scenes/MainScene.scene。
- 静态检查 `check_unity_cs.py [--fix-bom]` + `check_missing_usings.py`；.cs 必须 UTF-8 BOM；协程内只能 `yield break;`；[Tooltip]/日志里的 ASCII 双引号会提前闭合（CS1002）→ 用「」。
- 取证源 = Tuanjie/Editor/Editor.log；error CS 是累计值，先 grep 最后一次 CompileScripts。
- 层表只在编辑器启动时读一次：改 TagManager.asset 后 NameToLayer 返回 -1。正确做法 = `SerializedObject(LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0])` → `FindProperty("layers")` → 写空槽 + SaveAssets。
- 改 NavMeshData 后 `RemoveAllNavMeshData` → `AddNavMeshData`；改 Transform 后 `Physics.SyncTransforms()`。
- **Unity 自动化两个致命坑**：① `PlayerSettings.runInBackground` 默认 false → 编辑器失焦时播放循环被节流（`Time.time` 卡住而 `unscaledTime` 正常），"自动进 Play 跑 N 秒"必须先置 true；② 改 PlayerSettings 用 `execute_code` 写即可。排查"游戏时间不推进"：全仓 grep `timeScale` → 比对 `Time.time` vs `unscaledTime`。

## 命名空间与生命周期
- MOBA.<一级目录>；例外 SO→MOBA.Data、Debug→MOBA.Debugging、Editor→MOBA.Editor、VFX→MOBA.VFX；不得与 UnityEngine 类型同名。
- Awake 缓存引用、Start 注入配置（EntityBase.ApplyStats，Combat 先于 Targeting）；动态创建须 AddComponent 全完成后才 Initialize。
- OnValidate 在 AddComponent/ApplyModifiedProperties 瞬间同步触发 → 校验改 `EditorApplication.delayCall`。
- Instantiate 时 OnEnable 先于 Initialize → 读阵营/配置的表现组件必须用 Start。

## 战斗 / AI
- IsValidTarget 是唯一合法性入口；接口引用销毁后不变 null，先 `is UnityEngine.Object`；无 Collider 的单位永远索敌不到。
- MoveTo 返回 bool 当「已下令」标记；TryAttack 先写 nextAttackTime 再 TakeDamage；普攻必须传来源 Owner。
- 滞回：Chase↔Attack 退出 ×1.15；Abandon = 追击发起半径×1.5、Leash = ×2.0（Leash 唯一清除时机 = MoveState 抵达路径点）。
- 感知唯一入口 `EntityAIController.TryDetectEnemy`（0.25s 节流）；塔/基地/玩家英雄都不挂 EntityAIController。
- 死亡收尾在 `HeroController.HandleDied`；攻击指令唯一存放处 = `TargetingComponent.CurrentTarget`。
- `MovementComponent` 是 NavMeshAgent 唯一操作者（SetAgentEnabled / WarpTo）；不得直接持有 NavMeshAgent。

## 技能
- 链路 PlayerSkillController → SkillComponent.TryCast → 前摇 → Projectile/AreaEffectZone → SkillEffectResolver（唯一结算入口）；扣蓝写 CD 在链尾 → 拒绝零副作用是结构保证。
- 事件：`OnCastStarted`（校验通过后）/ `OnSpellReleased`（真的生成效果载体后）。**没有 OnSpellCast 这个事件。**
- Self 分派靠显式字段 `spawnZoneAtSelf`，绝不能用 areaDuration>0 反推。通用教训：**意图必须显式字段表达，不能从别的字段默认值反推**。
- 新枚举值一律追加末尾（插中间会让旧资产被静默重新解释）。
- 前摇为 0 不上控制锁（上锁会 Stop 清路径且同帧不恢复 = 瞬发变刹车）。
- 移速修饰：减速与加速共用一份原速快照，系数相乘；`ApplySpeedModifier` 是唯一写入点。
- 强化普攻状态存 BuffComponent（TryConsume 原子取走）；沉默不上控制锁，只在 TryCast 状态链拦一道。
- `skillSlots[4]` 下标 = `(int)SkillSlot`；**AI 英雄按实例注入 4 槽（Q/W/E/R），玩家走预制体直挂**。
- **技能槽规则 = 实例整体接管**：`SkillComponent.Initialize` 只要发现任意一槽非空就整体跳过模板（`EntityStatsData.skillQ..R` 已废弃、由工具清空）。**绝不能用「只补空」**。
- **AI 技能注入必须「无条件整体覆写」**：写成「抽到才写」会让池子异常时 AI 静默继承预制体的盖伦 QWER（症状 = 10 个英雄同一套技能、Console 全静音）。池子 = 10 个（Skills/Pool/），每人无放回抽 4 个，seed 2026 + 序号×7919。
- 弹道 prefab 严禁 Collider；范围场同 tick 去重 + `OverlapSphereNonAlloc(32)`。
- `SkillComponent.LastCastGroundPoint`：表现层要知道地面 AOE 的真实落点（与 LastCastTarget 同一模式）。

## UI / 表现层
- PrefabPool 池空返回 null + 一次性告警（绝不 Instantiate）；被拒绝的单位不会自动重试 → 需要重试队列。
- 血条：`hideWhenFull` 会让满血单位完全无条（英雄复活即满血 = 观感上英雄没血条）；血条/飘字由 Manager 订阅 EntityRegistry 统一挂，预制体零改动。
- CreatePrimitive 生成的占位物自带 Collider：必须 enabled=false + Destroy 并置 Ignore Raycast(2) 层，否则会拦截右键拾取射线 = 表现反向影响逻辑。
- **实体配色 = TeamColorView 的四格表**（阵营 × EntityType）：蓝英雄/红英雄/白小兵/黑小兵；**上色只有运行期一个写入者**。编辑器 Scene 视图里英雄仍是预制体默认绿色，**必须进 Play 才看到阵营色**。
- 技能特效按 SkillData 的**语义字段**分派（不是 Q/W/E/R 槽位）—— AI 的 4 个技能各不相同，按槽位写死会整体错位。
- **`AnimationComponent` 是唯一引用 Animator 的组件**，且**不聚合到 EntityBase**（自解析依赖）。
- 动画映射：速度取 `MovementComponent.CurrentVelocity` 模长 / 配置移速（**不能用 `MovementComponent.Speed`，那是配置值，会得到"永远在跑"的动画**）；`SetFloat(id, v, dampTime, dt)` 交给引擎阻尼；参数哈希在 Awake 由序列化字段名解析。
- 事件配对：**动画订阅 `OnCastStarted`（前摇开始就摆动作），特效订阅 `OnSpellReleased`（效果落地才炸开）**——刻意相反。
- `CombatComponent.OnAttackPerformed` 在 **`TryCommitAttack`** 广播（近战与塔弹道共用的唯一收敛点）；语义是「打出去了」不是「打中了」。
- `ProjectileSpawner.OnProjectileSpawned` 是订阅弹道 `OnHit` 的**唯一接入点**（弹道不在 EntityRegistry 里、无法枚举）。
- 池的分工：`Core/PrefabPool<T>` 池化"带组件的视图"（模板须已挂组件、归还只做 SetActive）；`VFX/SimpleObjectPool` 池化"任意 GameObject 特效"。两者都是**耗尽返回 null + 一次性告警，绝不 Instantiate**。
- `VfxSpawner`（正式层，`VfxSpawnerRoot`）与 `WhiteboxVfxManager`（白盒占位，`VfxRoot`）是**两个独立根对象**：三特效槽位为空时 VfxSpawner 完全惰性；美术接入后删 VfxRoot。
- 动画接线必须排在**实例化英雄之前**（步骤 15a）。
- 控制器 `UnitAnimator.controller`：5 状态 + 4 参数 + 6 过渡，**幂等策略 = 已存在一律沿用（只加不减不改）**；`CreateAnimatorControllerAtPath` 的 `defaultState` 为 null，必须显式设 Idle。
- **一个组件上只能有一个 `SerializedObject` 包装**：包两次只 Apply 后一份，前一份的写入被静默丢弃。
- 预留槽位路径（导入后重跑菜单即自动接入）：模型 `Assets/Art/Models/{Heroes/HeroModel,Minions/MinionModel}.prefab`；特效 `Assets/Prefabs/VFX/{HitVfx,CastVfx,ProjectileHitVfx}.prefab`。

## 阶段八地图
- 桥面 120×14；可行走半宽 6.5 < 塔射程 7.5，相邻塔间距 15 = 2×7.5（改桥宽/塔射程必须成对改）。
- 建筑挖洞 = 烘焙期方案：塔/基地 Body 设 Building 层（TagManager 9），烘焙掩码 Ground|Building；层缺失步骤 0 中止；**建建筑必须早于烘焙 NavMesh；建筑的"白盒让位"必须晚于烘焙**（先关掉就没洞 → 单位穿模）。
- LanePath 节点禁止落在建筑中心（洞心不可达 → HasReachedDestination 恒假 → 整队发呆）。
- 四条线：小兵 z=+2.5、英雄 z=-2.5（必须偏离桥心，否则绕洞左右等价 → 单位在塔前摆动）。
- arrivalStoppingDistance=0.5；avoidancePriority 英雄 30 段 / 小兵 60 段轮转（全同值 → 窄道顶死）。
- 索敌半径只决定「看得见」，`chaseEngageRange` 才决定「值不值得脱线」。
- **英雄编制由常量给出、由校验兜底**：PlayerHeroCount=1 / BlueAiHeroCount=4 / RedAiHeroCount=5 / TotalHeroCount=10；HeroSquadSize 是「蓝方席位」的派生值。ValidateHeroRoster 读场景重数（含全场景 EntityType.Hero 计数，抓游离副本）；ValidateAiSkillAssignment 读回 9×4 技能槽核对资产路径。**改编制要同时改常量与出生点数量**。

## 编辑器期陷阱（实机踩过）
- **MonoBehaviour 的字段初始化器里只能放常量与 new**：`LayerMask.NameToLayer` / `Shader.Find` 等引擎查询放进去会抛「not allowed to be called from a MonoBehaviour constructor」；静态字段初始化器还会升级成 TypeInitializationException，**连 `Undo.AddComponent` 一起失败**（症状：对象建出来了、组件没挂上、组装却"成功"）→ 一律挪到 Awake。
- **`OnValidate` 不能基于跨字段推断改写别的字段**：脚本加载时就会跑，新增字段在旧资产里尚未序列化 → 必然取默认值 → 误判（还会抹掉别处赖以判断的旁证）。归一化交给工具在组装时显式做。E 审判曾因此退化成"对自己造成 18 点伤害"。
- **工具注入字段前必须确认字段真的存在**：往不存在的字段写引用会每次组装刷红色假错误，比没有校验更糟。改字段名要同时改工具与 `Validate*Setup`。
- **改序列化字段名 = 一次静默数据迁移**：资产里存的旧值被 Unity **静默丢弃**（不报错）→ 新字段全 null。改名后必须让工具**读回校验**（`allyMaterial → allyHeroMaterial` 就是这样让红方小兵没变黑的）。
- **删除 partial class 里的私有方法前必须全仓 grep**：它其实是跨分册共用的工具（删 `PatchObjectReferenceIfUnset` 时漏看塔与 UI 分册仍在调用 → 差点 CS0103）。
- **`#region` 必须配对 `#endregion`**（少一个报 CS1038，错误指向【文件末尾】）；`#if` 同理（CS1027）。**往已有 region 里插新 region 时最容易漏**——`check_unity_cs.py` 已内置这项配对检查。

## 调试方法论
- **「写进去了」≠「生效了」**：同一字段若在运行期还会被写一次（如 `EntityBase.ApplyStats`），编辑器侧的"读回序列化字段"校验只能证明磁盘上写了什么，**看不见运行期覆盖**。这类字段要么收敛成单一写入者，要么把优先级写成显式规则 + 运行期留一条自证日志（如 `HeroAIController` 启动时打印最终生效的 4 个技能）。
- 排查实机问题**先比对「上次组装」与「上次编译」的日志行号**：能立刻区分"代码没生效"与"根本没重跑组装菜单"。
- 取证一律读**落盘资产 YAML**（场景/预制体/SO），不要读代码意图。

## 阶段九资产实装（10 英雄 + 建筑 + 占位动画/特效已接入）
- **10 个英雄各挂不同模型**：席位号 ↔ `HeroChampionNames`（0=Garen 玩家 / 1..9 = Ashe, Darius, Lux, Annie, MasterYi, Ahri, Teemo, Yasuo, Zed）。模型**按实例**挂（预制体只能有一份 → 挂预制体就等于 10 个盖伦），阵营材质也按实例写进 TeamColorView。
- **建筑用 CD 原生模型**：`turret/skins/base/turret_base.skn`（取 Base/Stage1-3）+ `nexus/nexus.skn` + `inhibitor/inhibitor.skn`，**都自带蓝/红双贴图**。**上一轮"CD 不镜像建筑网格"的结论是错的** —— 要完整拉目录清单逐个核对。
- **`Model` 子节点名是幂等的前提**（`transform.Find("Model")` 存在即跳过），改名会导致每次组装多叠一层模型。
- **英雄预制体外观在 `Body` 子节点；小兵预制体网格与碰撞体都在根节点** → 工具 `ResolveWhiteboxVisual` 两种结构都要兜住。
- **白盒让位只关 Renderer、绝不碰 Collider**（索敌/拾取依赖它）。
- **配色材质必须带贴图**：`TeamColorView` 写的是整个 `sharedMaterial`，纯色材质盖到模型上会把贴图抹掉 → 工具「模型贴图材质优先、纯色兜底」。
- **白盒残留已清**：`EntityVisuals.cs` 已删除（三处调用点**必须成对删** —— 只删隐藏会留下"只恢复不隐藏"的孤儿写入者）；`WhiteboxVfxManager.enableVfx` 由工具置 false。**代价：正式特效未导入前技能没有任何画面反馈**。
- 动画：`Assets/Art/Animations/{Idle,Run,Attack,Spell,Die}.anim` 是**程序化占位片段**（驱动 `Model` 子节点 Transform，不驱动骨骼）。注入规则 = **状态的 motion 为空才写**，不覆盖美术的连线。真片段来源（Mixamo/LoL .anm）全部受阻。
- 特效：5 个程序化 `ParticleSystem` 预制体（Hit/Cast/ProjectileHit/Shield/Zone）注入 VfxSpawner **五个通道**；护盾与范围场**按 SkillData 语义字段分派**。预生成容量 40（16 会在 5v5 团战里被打空）。
- 实机判据：组装 6 项校验全绿 + Play 期间 **0 Error**；`heroes: withModel=10/10`、`minions: withModel=11/11`；英雄材质 `HeroModelBlue/Red`。**Play 期间的 Warning 全部是阶段八既有的拥堵/牵引诊断，不是回归**。

## 逆向 LoL 资产的工具（`.workbuddy-ai/tools/`）
- `lmesh2obj.py`：`.lmesh`（lol-model-viewer 私有格式，magic 604210091，顶点 52B）→ OBJ。**仓库里没有转换器源码**，格式是从 `viewer.js` 的 `loadMesh`/`Lol.Vertex` 读出来的。
- `skn2obj.py`：`.skn`（LoL 原生）→ OBJ。头部 0x98 / 索引块 u16 / 顶点块 52B（`pos@0, boneIdx@12, weights@16, nrm@32, uv@44`）/ 末尾 12B。
- **解未知二进制的通用方法**：用两种布局假设分别解析，**越界索引为 0 的那个才是对的**；再叠加"法线必须是单位向量"与"块边界 == 文件长度"两条交叉判据。脚本内置校验，任一条不满足就报错退出，**绝不输出疑似垃圾的资产**。
- CommunityDragon 可用路径：图标 `plugins/rcp-be-lol-game-data/global/default/assets/characters/<champ>/hud/icons2d/*.png`（或从 `v1/champions/<id>.json` 的 `spells[].abilityIconPath` 取精确路径）；网格 `game/assets/characters/<unit>/skins/base/*.skn` + 贴图同目录。

## MCP 使用要点（`mcp__unityMCP__*`）
- 先 `ToolSearch`（tool_names 精确名）再 `DeferExecuteTool`。
- **`execute_code` 最好用**：能直接跑 C#，绕开各工具的 schema 猜测；但它是 **CodeDom（C# 6）**，别用新语法。
- `manage_camera` 截图：`capture_source=scene_view` 会**带出全部 Gizmo**（本项目 FSM 调试球会糊满画面）→ 看模型细节用 `game_view` + `view_target`。
- `refresh_unity(compile="request")` 可强制重编译；`read_console` 支持 `types` 过滤与 `clear`。
- **模型市场（Sketchfab）与 AI 生成（Tripo/Meshy）都需要 API Key，本机均未配置**。

## 状态
- 阶段一~七已封版（commit b8a8d54 已推送）；阶段八 = 5v5 代码已落盘；**阶段九：表现层框架 + 10 个不同英雄模型 + 塔/水晶模型 + 技能图标 + 占位动画 + 粒子特效，已实机验证通过（0 Error）**。
- **待办**：真动画片段 / 特效贴图 / 地面贴图 / 建筑抑制器模型。
- **第四轮（实机打回）已修复两个静默失效**：① 小兵配色（字段改名导致旧值被静默丢弃 → 前移注入 + 读回校验 + 运行期报错）；② AI 技能被运行期共享模板覆盖（→ 实例整体接管 + 工具清空模板技能字段 + AI 启动时技能自证日志）。**两个修复都要求重跑一次组装菜单。**
- 判据：MainScene.scene 的 mtime 必须晚于 NavMesh.asset；组装后核对 Console 无红色错误（NavMesh 9 几何源 / 建筑挖洞 / 技能池 10 / 英雄编制 10 人 / AI 技能 9×4=36 / 实体配色四格 / 血条 N≥18）；Play 后核对 **9 行 `[HeroAIController] … 技能槽已就绪`** 与「蓝英雄蓝 / 红英雄红 / 蓝兵白 / 红兵黑」。待补测 Profiler（GC≤1KB/帧、逻辑<2ms、60FPS）。
- 编辑器常驻运行时无法 batchmode 编译（工程被锁）：只能用静态检查脚本 + 等编辑器刷新后读 Editor.log 的最后一次 CompileScripts。
