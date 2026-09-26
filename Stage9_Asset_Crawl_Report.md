# 阶段九 · 全网资产爬取与多样化实装报告

> 执行时间：2026-09-26 17:37 ~ 18:20 ｜ 全程 MCP 自动化（无人工拖拽）
> 工程：`D:/Project/Wkbd-project/MOBA-Demo` ｜ 编辑器：团结 Tuanjie 2022.3.61t14

---

## 0. 执行摘要

| 任务 | 结果 |
|---|---|
| ① 10 个不同英雄的模型 | ✅ **完全成功** —— 10 个英雄各挂一份不同模型，实机校验「10 种不同模型」通过 |
| ② 防御塔 / 基地水晶模型 | ✅ **完全成功** —— 6 座塔 + 2 个基地全部换成原生模型，且挖洞校验恢复通过 |
| ③A 动画片段 | ⚠️ **部分成功** —— 外部来源全部受阻（详见 §3.1），改用**程序化生成的 5 个占位片段**，T-pose 已消除 |
| ③B 技能特效 | ⚠️ **部分成功** —— 外部特效包未采用，改用**程序化生成的 5 个粒子预制体**，技能画面反馈已接通 |
| ④ 重新组装 + Play 实测 | ✅ 组装 6 项校验全绿；Play **0 Error**（60 秒游戏时间 / 5464 帧） |

**一句话结论**：视觉上「10 个盖伦的克隆战争 + T-pose + 无特效 + 白盒建筑」四个问题**全部解决**；
其中英雄与建筑用的是**原生 LoL 资源**，动画与特效是**程序化占位实现**（外部来源全部受阻，已在 §3 逐条说明阻断原因）。

---

## 1. 资产爬取清单（成功了哪些、从哪爬的）

### 1.1 英雄模型 —— 10 个，来源 `tengge1/lol-model-viewer`（`.lmesh`）

| 席位 | 英雄 | ID | 三角面 | 源文件 | 落地 |
|---|---|---|---|---|---|
| 0（玩家） | 盖伦 Garen | 86 | 6336 | `models/86_0.lmesh` | `Assets/Art/Models/Heroes/Hero_Garen.prefab` |
| 1 | 寒冰 Ashe | 22 | 7406 | `models/22_0.lmesh` | `Hero_Ashe.prefab` |
| 2 | 诺手 Darius | 122 | 9311 | `models/122_0.lmesh` | `Hero_Darius.prefab` |
| 3 | 拉克丝 Lux | 99 | 3742 | `models/99_0.lmesh` | `Hero_Lux.prefab` |
| 4 | 安妮 Annie | 1 | 6248 | `models/1_0.lmesh` | `Hero_Annie.prefab` |
| 5 | 剑圣 MasterYi | 11 | 9144 | `models/11_0.lmesh` | `Hero_MasterYi.prefab` |
| 6 | 九尾妖狐 Ahri | 103 | 10322 | `models/103_0.lmesh` | `Hero_Ahri.prefab` |
| 7 | 提莫 Teemo | 17 | 3469 | `models/17_0.lmesh` | `Hero_Teemo.prefab` |
| 8 | 亚索 Yasuo | 157 | 9211 | `models/157_0.lmesh` | `Hero_Yasuo.prefab` |
| 9 | 劫 Zed | 238 | 8703 | `models/238_0.lmesh` | `Hero_Zed.prefab` |

- 每个英雄同时抓取 512²（剑圣 1024²）的官方 diffuse 贴图，并生成蓝 / 红两份阵营材质
  （贴图 + `_Color` 色调 —— LoL 贴图后缀 `_cm` 即 color mask，染色是它设计内的用法）。
- 转换工具：`.workbuddy-ai/tools/lmesh2obj.py`（上一轮已写好，本轮批量复用）。
- **实机校验**：`英雄模型分配校验通过：10 个英雄各挂一份不同模型（10 种）`，
  网格名互不相同（`garen_base_mat` / `ashe_base_2011_md_...` / `darius_tx` / `lux_base_md_v10_mat_lux1` /
  `anniebase_mat` / `masteryi_base_md_masteryi` / `gumiho_base_body_md_...` / `riotrig:teemo3:teemo1` /
  `yasuo_base_mat` / `lambert4`）。

### 1.2 建筑模型 —— 3 种，来源 **CommunityDragon**（`.skn`，LoL 原生格式）

上一轮判定「CD 不镜像建筑网格」是**错的** —— 本轮把 CD 的角色目录清单完整拉下来逐个核对，
发现 `turret/`、`nexus/`、`inhibitor/` 三个目录**都有 `.skn` 网格，而且自带蓝/红双贴图**：

| 建筑 | 源文件 | 三角面 | 落地 | 用途 |
|---|---|---|---|---|
| 防御塔 | `turret/skins/base/turret_base.skn` | 10202 | `Buildings/Tower.prefab` | 6 座塔全部替换 |
| 基地水晶 | `nexus/skins/base/nexus.skn` | 4518 | `Buildings/Nexus.prefab` | 双方基地替换 |
| 抑制器 | `inhibitor/skins/base/inhibitor.skn` | 2225 | `Buildings/Inhibitor.prefab` | 已转换，本局（桥图）无抑制器，**暂未使用** |

- 转换工具：`.workbuddy-ai/tools/skn2obj.py`（本轮**扩展为支持多对象**，详见 §2.1）。
- 塔的 `.skn` 含 8 个对象（Base / Stage1-3 / Rubble / Broken1-3），工具按语义**自动剔除残骸变体**
  （名字含 rubble/broken/destroyed），只导出完整的塔身四段。

### 1.3 技能图标 —— 4 张，来源 CommunityDragon（上一轮已接入，本轮沿用）

### 1.4 特效与动画 —— 外部来源全部受阻，改为程序化生成

**没有从外部爬取任何特效或动画资产**，原因见 §3。当前用的是工具生成的占位实现：

| 类别 | 产物 | 内容 |
|---|---|---|
| 特效（5 个） | `Assets/Prefabs/VFX/{Hit,Cast,ProjectileHit,Shield,Zone}Vfx.prefab` | 真实 `ParticleSystem`（Standard Unlit 粒子材质 + Stretch 渲染模式）：受击火花 / 施法光带 / 火球爆炸 / 护盾光环 / 地面范围圈 |
| 动画（5 个） | `Assets/Art/Animations/{Idle,Run,Attack,Spell,Die}.anim` | 驱动 `Model` 子节点 Transform 的占位动作：呼吸起伏 / 奔跑弹跳前倾 9° / 攻击前冲 / 施法上浮 / 死亡前扑 90° |

---

## 2. 关键技术攻关

### 2.1 `.skn` 多对象格式破解（本轮最大技术难点）

上一轮的解析器只支持 `objectCount == 1`（小兵）。塔是 8 个对象、水晶 2 个，直接报错退出。
通过字节级反解 + **方程闭合验证**，把完整布局解出来了：

```
0x00  u32 magic = 0x00112233
0x04  u16 major, u16 minor
0x08  u32 objectCount
------ 每对象 80 字节记录 ------
      char name[64] | u32 vertexStart | u32 vertexCount | u32 indexStart | u32 indexCount
------ 60 字节尾部 ------
      u32 ? | u32 totalIndexCount | u32 totalVertexCount | u32 vertexSize | u32 ? | f32[10]
------ 数据块 ------
      u16[totalIndexCount]              ← 索引块
      u8[vertexSize × totalVertexCount] ← 顶点块
      12 字节尾记录
```

**四条交叉判据（脚本内置，任一不满足即报错退出，绝不输出疑似垃圾的资产）**：
1. `12 + objectCount×80 + 60 + totalIndex×2 + totalVertex×vertexSize + 12 == 文件长度`（四个文件全部精确闭合）
2. 索引零越界
3. 单位法线比例 ≥ 99%（实测 turret 有 7/22849 个源数据退化法线，已修补为 (0,1,0)）
4. 每对象的 `vertexStart / indexStart` 与前面所有对象的计数累加**严格吻合**

### 2.2 实机踩出来的三个真问题（都已修复）

| # | 症状 | 根因 | 修法 |
|---|---|---|---|
| 1 | **玩家英雄没模型**（9 个 AI 都有，唯独主角没有） | `CreateHeroInstance` 对玩家英雄有一处**提前 `return`**，而我把模型挂载追加在方法末尾 → 玩家永远走不到 | 把挂载调用前移到提前 return **之前**，并写明教训：往已有方法追加"两条分支都要执行"的步骤时，必须先确认它排在所有提前 return 之前 |
| 2 | **建筑挖洞全部失效**（8 个建筑中心都可行走；烘焙几何源从 9 个掉到 1 个） | 我在**烘焙之前**关掉了建筑 Body 的 `MeshRenderer` —— 而烘焙是按层掩码收集 `MeshRenderer` 来抠洞的，渲染器一关建筑就不参与几何收集了 | 新增 `HideBuildingWhiteboxVisuals`，把"白盒让位"挪到 **NavMesh 烘焙之后**（烘焙是一次性编辑期操作，运行期只需要碰撞体） |
| 3 | **受击特效被打空**（`SimpleObjectPool: HitVfx 池已耗尽（容量 16）`） | 5v5 团战 21+ 单位互殴，每次挨打都要播一次受击特效，16 个容量不够 | 预生成数量默认 16 → **40** |

> 问题 2 是典型的「**表现反向影响逻辑**」：关一个渲染器看起来纯表现，实际却改掉了导航网格的挖洞。
> 这类问题的判据正是项目铁律里那条「关掉它对局结果会不会变」—— 答案是"会"（单位开始穿模）。

### 2.3 自动化实测的环境坑：`runInBackground`

第一次 Play 实测时发现 **`Time.time` 卡在 1.2 秒**（unscaled 已 61 秒），游戏根本没跑起来。
排查确认：**项目里没有任何代码写 `Time.timeScale`**（全仓 grep 为空），
根因是 **Unity 的 `PlayerSettings.runInBackground` 默认为 false** —— 编辑器一失焦，播放循环就被节流到近乎停止。

这会让"自动进入 Play 跑 1 分钟"变成一句空话（跑的是墙钟时间，不是游戏时间）。
已把该设置改为 **true**，之后的实测立刻恢复正常（200 FPS / 5464 帧）。

---

## 3. 受阻清单（明确未能突破的）

### 3.1 动画片段 —— 三条路全部受阻

| 尝试 | 结果 |
|---|---|
| **Mixamo**（`mixamo animations fbx pack`） | ❌ Adobe 账号登录后才能下载，**无法自动化**（需要交互式登录 + 接受许可） |
| **LoL `.anm` 转换** | ❌ `.anm` 是**骨骼空间**动画数据，要变成 Unity 的 `AnimationClip` 需要：完整骨骼层级 + 蒙皮权重绑定 + 动画重定向。本项目导入的是**静态绑定姿态网格（无骨骼）**，等于要重建整套 rig —— 远超一轮任务范围 |
| **GitHub 免费动画包** | ❌ 检索到的多为 Unity `.unitypackage`（需要编辑器导入流程，且多数是武器/道具动画，与双足角色不符） |

**采用的替代方案**：程序化生成 5 个占位片段，绑定 `Model` 子节点的 Transform（**不驱动骨骼**）。
它们解决的是「T-pose 站着不动」这个观感问题：呼吸起伏 / 奔跑弹跳前倾 / 攻击前冲 / 施法上浮 / 死亡前扑。
**接入真片段的方式**：把片段拖到 `UnitAnimator.controller` 对应状态即可 ——
工具的注入规则是「状态的 `motion` 为空才写」，**不会覆盖美术的连线**。

### 3.2 技能特效 —— 外部包未采用

| 尝试 | 结果 |
|---|---|
| `Unity free VFX pack` / `RPG magic particle effects` | ⚠️ 检索到的主要是付费商店包或需要账号的资源站；GitHub 上的免费包多为 `.unitypackage`，且风格与本项目的白盒低模不统一 |
| **MCP 特效生成** | ❌ 无此能力（MCP 只有 `generate_model` / `generate_image`，没有特效生成） |

**采用的替代方案**：程序化生成 5 个真实 `ParticleSystem` 预制体，注入 `VfxSpawner` 的五个通道。
**这不是"占位几何体"** —— 是真正的粒子系统（有生命周期、有对象池、有自动回收），只是外观朴素（无贴图，用 Stretch 拉成光带）。

### 3.3 其它未获取项

| 项 | 原因 |
|---|---|
| 建筑模型市场包（Kenney Tower Defense / ModKit 等） | 检索到了，但**主动放弃** —— CD 的原生 `turret/nexus` 与项目美术风格完全一致，用第三方低模反而更差 |
| 本机 LoL 客户端解包 | 本机未安装 LoL（上一轮已核实注册表与各盘根目录） |
| 地面贴图（MOBA 场景） | 本轮任务单未包含；仍是纯白 Plane（已知项） |

---

## 4. 代码改造清单

| 文件 | 改动 |
|---|---|
| `Editor/AutoSceneBuilder.Stage9Assembly.cs` | 新增：`HeroChampionNames` 席位↔模型表、`AttachHeroModelToInstance`（按实例挂模型 + 写实例级阵营材质）、`AttachBuildingModel`、`HideBuildingWhiteboxVisuals`、`ValidateHeroModelAssignment`（读回校验"10 种不同模型"）、`EnsureAnimationClips` + 5 个片段生成器 + `AssignStateMotionIfEmpty`、5 个特效槽位注入 |
| `Editor/AutoSceneBuilder.Stage8Assembly.cs` | `CreateHeroInstance` 增加 `championIndex` 参数并在**提前 return 之前**挂模型；`CreateHeroSquad` 按席位分配英雄（蓝 0-4 / 红 5-9）；`CreateTowerLine` 改用模型贴图材质并挂塔模型 |
| `Editor/AutoSceneBuilder.cs` | 步骤 4/5 基地改用模型贴图材质并挂水晶模型；新增**步骤 8b**（烘焙后让位）；新增 `ValidateHeroModelAssignment` 调用；日志文案同步 |
| `Editor/AutoSceneBuilder.VfxAssembly.cs` | 配色材质改为「模型贴图优先、纯色兜底」；白盒特效禁用文案同步 |
| `VFX/VfxSpawner.cs` | 新增**护盾 / 范围场**两个语义通道（按 `SkillData` 的 `EffectType` / `SpawnZoneAtSelf` / `CastType` 分派）；预生成数量 16 → 40 |
| `Skills/SkillComponent.cs` | 新增只读属性 `LastCastGroundPoint`（表现层要知道地面 AOE 的真实落点） |
| `Assets/Scripts/Core/EntityVisuals.cs` | **已删除**（上一轮的白盒尸体隐藏，本轮延续） |

---

## 5. 实机测试结果

### 5.1 组装（AutoSceneBuilder 一键）

**0 Error / 0 Warning**，收尾校验全绿：

```
✅ 阶段九表现层接线校验通过（Animator + AnimationComponent + 控制器 + 根运动关闭）
✅ 技能装配校验通过（Q/W/E/R 四槽 / 法力 / Buff / 弹道生成器 / 技能输入层）
✅ 英雄编制校验通过（10 个英雄：蓝 5 = 1 玩家 + 4 AI；红 5 全 AI；无游离）
✅ AI 技能分配校验通过（9 × 4 = 36 个技能全部来自技能池）
✅ 英雄模型分配校验通过：10 个英雄各挂一份不同模型（10 种），白盒外观均已让位
✅ 实体配色校验通过（四格材质已注入）
✅ Stats Data 注入校验通过
✅ 建筑挖洞校验通过：8 个建筑中心均不可行走（最近可行走点 ≥ 1.0 米）
✅ NavMesh 已烘焙（9 个几何源，其中建筑 8 个 / 124 个三角形）
```

### 5.2 Play Mode（游戏时间 60 秒 / 5464 帧）

**Console：0 Error。**

| 指标 | 实测值 |
|---|---|
| 英雄 | 10 个，**各自不同模型** |
| 死亡动画 | `dyingAnim = 4` —— 4 个英雄已阵亡并**扑倒在地**（不再是站着不动的 T-pose） |
| 移动动画 | `runningAnim = 9 / idleAnim = 1` —— Run 状态带 9° 前倾 |
| 小兵 | `minions = 11, withModel = 11` |
| 技能特效 | `VfxSpawner active = 20` —— **20 个粒子实例同时在播** |
| 建筑 | 塔 / 基地 `Model=OK`、白盒渲染器已关、**碰撞体保留**（`colliders=1`） |
| 战斗推进 | 蓝方 1 杀 3 死 / 红方 3 杀 1 死（真实交战发生） |
| 性能 | 约 200 FPS（5464 帧 / 27.7 秒 unscaled） |

**Warning：34 条**，构成：
- **33 条**是阶段八既有的拥堵 / 牵引诊断（`MoveState`/`ChaseState` 卡住自愈、牵引出界拒绝追击、路径点被占跳过）——
  桥面仅 14 米宽、21+ 单位同屏，这是**设计内的自愈机制在工作**，不是缺陷，也不是本轮引入的。
- **1 条**是本轮新暴露的 `HitVfx 池已耗尽`，**已修复**（容量 16 → 40），修复后未再出现。

### 5.3 截图证据

| 文件 | 内容 |
|---|---|
| `Assets/Screenshots/mcp_v2_final.png` | 5 个蓝方英雄（盖伦 / 亚索 / 拉克丝 / 安妮 / 艾希）**各不相同的模型**沿桥推进 |
| `Assets/Screenshots/mcp_v2_hero_closeup.png` | 近景：盖伦模型站在蓝方水晶（真实 Nexus 模型）旁 |
| `Assets/Screenshots/mcp_v2_combat.png` | 团战瞬间：多英雄混战 + 红色粒子特效 + 伤害数字 + 倒地单位 |

---

## 6. 后续建议（按优先级）

1. **接真动画片段**（最高价值）：程序化片段解决了 T-pose，但动作仍僵硬。
   建议用 Mixamo 手工导出一次（一次性人工步骤），或采购一个双足 MOBA 动画包。
2. **特效贴图**：给 5 个粒子预制体换上柔和的圆形贴图（当前是无贴图的 Stretch 光带），观感会明显提升。
3. **地面贴图**：README §2.2.4 要求替换纯白 Plane（含兵线视觉引导）。
4. **英雄相对体型**：当前 10 个英雄统一归一化到 2.2 米高（提莫与盖伦一样高）。
   若要还原 LoL 的相对体型，可改成"按原始包围盒高度等比缩放"。
5. **拥堵告警**：33 条/分钟偏多。当前是自愈机制在工作，但可读性差
   （阶段八已登记：`MoveState`/`ChaseState` 增加"路径被外部清空即立刻重下指令"的分支）。
6. **版权**：英雄 / 小兵 / 建筑模型与图标均属 Riot Games，仅限**非商业**学习使用；商业化前必须替换。
