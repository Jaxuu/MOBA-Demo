# MCP 自动化接管与实机测试报告

> 阶段九（美术表现与表现层分离）· 资产实装环节
> 执行时间：2026-09-26 17:02 ~ 17:23 ｜ 执行方式：Unity MCP 全自动（无人工拖拽）
> 工程：`D:/Project/Wkbd-project/MOBA-Demo` ｜ 编辑器：团结 Tuanjie 2022.3.61t14

---

## 0. 执行摘要

| 任务 | 结果 |
|---|---|
| 盖伦 QWER 技能图标抓取与注入 | ✅ **成功**（4 张 64×64 官方图标，已写入四个技能资产） |
| 盖伦 3D 模型抓取与挂载 | ✅ **成功**（真实网格 + 贴图，10/10 英雄已挂载） |
| 小兵 3D 模型抓取与挂载 | ✅ **成功**（真实网格 + 蓝/红双贴图，11/11 小兵已挂载） |
| 白盒残留清理 | ✅ 完成（3 处隐身调用点删除 + `EntityVisuals` 类删除 + 白盒特效禁用） |
| MCP 一键组装 | ✅ 全绿（6 项收尾校验全部通过，0 错误 0 警告） |
| Play Mode 实机测试 | ✅ **1 分 50 秒无 Error**；33 条 Warning 全部是阶段八既有的拥堵/牵引诊断 |
| 建筑模型（塔/基地） | ❌ **未获取**（无可用来源，见 §1.4） |
| 动画片段 / 特效 / 音效 | ❌ **未获取**（不在本轮任务单内，且无可用来源） |

**一句话结论**：白盒胶囊体已被真实 3D 模型替换，且**逻辑代码一行未改**（阶段九"表现层解耦"的验收前提成立）；Play 期间没有任何材质丢失或动画绑定报错。

---

## 1. 资产获取结果

### 1.1 UI 技能图标 —— ✅ 成功

- **来源**：CommunityDragon 官方镜像（`raw.communitydragon.org`），路径由
  `v1/champions/86.json` 的 `spells[].abilityIconPath` 精确给出（不是猜路径）。
- **文件**：
  | 槽位 | 源路径（CD 内部） | 落地路径 |
  |---|---|---|
  | Q | `ASSETS/Characters/Garen/HUD/Icons2D/Garen_Q.png` | `Assets/Art/Textures/UI/HeroSkillQ.png` |
  | W | `.../Garen_W.png` | `Assets/Art/Textures/UI/HeroSkillW.png` |
  | E | `.../Garen_E1.png` | `Assets/Art/Textures/UI/HeroSkillE.png` |
  | R | `.../Garen_R.png` | `Assets/Art/Textures/UI/HeroSkillR.png` |
- **导入**：4 张均为 64×64 PNG。MCP `execute_code` 把 `TextureImporter.textureType` 设为 `Sprite`、
  `spriteImportMode = Single`、关 mipmap、不压缩 —— 与工程既有基准 `UIWhite.png.meta` 完全一致。
- **注入**：MCP `manage_scriptable_object` 写 `icon` 字段（先 dry-run 验证属性路径，再实写）。
  **读回校验**：`HeroSkillQ/W/E/R.asset` 的 `Icon` 分别解析为 `HeroSkillQ/W/E/R` 四张 Sprite ✅
- **顺带修掉一个旧缺口**：E / R 两槽的图标此前一直是空的（阶段七的 `PatchSkillIcons` 只覆盖 Q/W，
  阶段八扩到四槽后没人补）→ 已把工具扩为四槽，且**优先注入正式图标、找不到才退回占位白图**。

### 1.2 盖伦 3D 模型 —— ✅ 成功（逆向自定义格式）

- **来源**：[tengge1/lol-model-viewer](https://github.com/tengge1/lol-model-viewer) 仓库内置的
  `resource/models/86_0.lmesh`（252 KB）+ `resource/textures/86/garen_base_tx_cm.png`（222 KB）。
- **阻断**：该仓库**没有转换器源码**（`.lmesh` 是作者离线生成的），也没有 glTF/FBX 版本 →
  必须自己解格式。
- **做法**：读 `viewer.js` 的 `Lol.Model.prototype.loadMesh` / `Lol.Vertex` 得到格式定义，
  写 `lmesh2obj.py` 转 OBJ。格式：`magic 604210091` + 版本 + 字符串 + 网格表 + 顶点(52B/个) + 索引(u16)。
- **自校验**：**索引越界 0 个**（证明顶点步长 52 解码正确）+ 3932 顶点 / 19008 索引。
- **产物**：
  - `Assets/Art/Models/Heroes/HeroModel.obj` → Unity 导入为 **4110 顶点 / 6336 三角面**
  - `Assets/Art/Models/Heroes/HeroModel_Texture.png`（512×512）
  - `Assets/Art/Models/Heroes/HeroModel.prefab`（预留槽位，含网格 + 材质 + 贴图）
- **归一化**：原始包围盒 183×243×163（LoL 内部单位）→ 按身高缩放到 **2.2 米**、脚底归零、X/Z 居中。

### 1.3 小兵 3D 模型 —— ✅ 成功（逆向 LoL 原生 .skn）

- **来源**：CommunityDragon 镜像的 `game/assets/characters/sru_orderminionmelee/skins/base/`
  —— 含 `order_minion_melee.skn`（47 KB）与**蓝/红两份贴图**（`..._tx_cm.png` / `..._tx_cm_red.png`）。
- **做法**：`.skn` 是 LoL 原生格式，无现成 Python 解析器 → 通过**字节级实测反解**：
  - 头部 0x98 字节 → 索引块（u16 × 3189）→ 顶点块（52 B × 789）→ 12 字节尾记录；
  - 顶点布局 `position@0 / boneIndices@12 / weights@16 / normal@32 / uv@44`。
  - **三条独立判据交叉验证**：① 索引块对齐在 0x98 时**零越界**（挪到 0xA4 会出现 6 个越界）；
    ② 789 个顶点法线**全部为单位向量**；③ 块边界与文件长度**完全吻合**。
    脚本内置同样校验，任一条不满足即报错退出（绝不输出疑似垃圾的 OBJ）。
- **产物**：
  - `Assets/Art/Models/Minions/MinionModel.obj` → **823 顶点 / 1063 三角面**
  - `Assets/Art/Models/Minions/MinionModel_Blue.png` / `MinionModel_Red.png`（Riot 自带双阵营贴图）
  - `Assets/Art/Models/Minions/MinionModel.prefab`（归一化到 **1.15 米**高）

### 1.4 ❌ 未获取的资源与**具体阻断原因**

| 缺失项 | 阻断原因 | 解除条件 |
|---|---|---|
| **塔 / 基地模型** | 上述两个来源都没有建筑网格（model-viewer 只有英雄 + 野怪；CD 的建筑目录只有 `hud/` 与贴图，**不镜像 `.skn`**） | 提供建筑模型资产（或指定可下载来源） |
| **模型市场（Sketchfab）** | MCP `import_model` 报告 `sketchfab: configured = false` —— **编辑器安全存储里没有 API Key** | 在 Unity 里配置 Sketchfab Token |
| **AI 生成模型（Tripo / Meshy）** | MCP `generate_model` 报告 `tripo/meshy: configured = false` —— 同样缺 API Key | 配置 Tripo 或 Meshy 的 Key |
| **本机 LoL 客户端** | 注册表（`HKLM/HKCU\SOFTWARE\Riot Games`、`Tencent`）与各盘根目录均**未找到安装**；只有 `C:\Program Files\Tencent\Androws`（安卓模拟器，非 LoL） | 安装 LoL 客户端后可从 `.wad.client` 解包 |
| **动画片段（Idle/Run/Attack/Spell/Die）** | `.lmesh` / `.skn` 的动画存在独立格式（`.lanim`），且 LoL 动画是**骨骼空间**数据 —— 需要骨骼层级 + 蒙皮 + 动画重定向才能变成 Unity 可用的 `AnimationClip`，工作量远超本轮范围 | 单独排期（或改用带骨骼的标准模型） |
| **特效 / 音效资源** | 本轮任务单未包含；且正式特效需要美术制作 | 制作后放入 `Assets/Prefabs/VFX/` |

> **关于模型授权的提醒**：盖伦与小兵的美术资源版权归 Riot Games 所有。
> Riot 官方允许**非商业**的粉丝/学习项目使用其素材，本项目定位为单机学习 Demo，符合该范围；
> 但**不得用于任何商业发行**。若后续要商业化，必须替换为自有或已授权资产。

---

## 2. 代码改造（第二步：清理白盒残留）

### 2.1 删除尸体隐身逻辑（3 处调用点 + 1 个类）

| 文件 | 位置 | 处理 |
|---|---|---|
| `Units/HeroController.cs` | `HandleDied` 第 5 步 | 删除 `EntityVisuals.SetRenderersEnabled(..., false)` |
| `Units/HeroController.cs` | `Revive` 第 6 步 | 删除对称的 `SetRenderersEnabled(..., true)`（只删一半会留下"只恢复不隐藏"的孤儿写入者） |
| `AI/EntityAIController.cs` | `HandleCorpseCleanup` 第 1 步 | 删除隐藏；**保留**延时销毁（飘字/播报的锚点还需要它多活 2 秒） |
| `Core/EntityVisuals.cs` | 整个文件 | **删除**（三处调用点消失后无消费方 = 死代码，工程明令不留） |

> **为什么必须成对删除**：原先"隐藏 + 恢复"是两个调用点、分属两个类，靠注释维持对称。
> 只删隐藏会得到"死亡时不隐藏、复活时强行打开渲染器"的怪异组合；
> 接入 `Die` 动画后，渲染器开关权必须完整交给表现层。

### 2.2 禁用白盒占位特效

`AutoSceneBuilder.VfxAssembly.BuildWhiteboxVfx` 现在会在装配时显式写入
`WhiteboxVfxManager.enableVfx = false`（**禁用而非删除对象**，保留一键回退能力）。

> ⚠️ **副作用（需知悉）**：正式特效预制体尚未导入，`VfxSpawner` 三个槽位为空且完全惰性，
> 因此**当前技能没有任何画面反馈**（Q/W/E/R 与 AI 技能都是"只有数值变化，没有视觉"）。
> 这是"白盒让位正式层"的必然结果；把该组件的 `enableVfx` 勾回即可临时回退。

### 2.3 组装工具升级（保证"重跑菜单不会被冲掉"）

| 改动 | 位置 | 目的 |
|---|---|---|
| 新增 `AttachReservedModelToPrefab` | `AutoSceneBuilder.Stage9Assembly.cs` | 把预留路径上的模型挂成 `Model` 子节点；**只关白盒 Renderer、绝不碰 Collider**（索敌与右键拾取依赖碰撞体）；幂等（已存在则不重复实例化） |
| 新增 `AppendModelProblems` 读回校验 | 同上 | 校验模型子节点存在、白盒已让位、**碰撞体仍在** |
| 配色材质改为"模型贴图优先、纯色兜底" | `AutoSceneBuilder.VfxAssembly.cs` | `TeamColorView` 写的是整个 `sharedMaterial`，纯色材质盖到模型上会把贴图抹掉 → 存在 `HeroModelBlue/Red.mat`、`MinionModelBlue/Red.mat` 时优先使用 |
| `PatchSkillIcons` 扩为四槽 + 正式图标优先 | `AutoSceneBuilder.UIAssembly.cs` | 让"全新克隆仓库 → 一键组装"也能得到正确图标；写入器换成"值不同才写"，保持幂等 |

> **架构约束的体现**：模型是挂在实体根节点**下**的子节点，根节点仍贴地、仍由 `NavMeshAgent` 驱动、
> 逻辑组件位置不变 —— 因此"换模型"是一次**纯表现层操作**，`CombatComponent` / `MovementComponent` /
> `SkillComponent` 等逻辑代码**一行未改**。这正是阶段九"表现层解耦"要验证的核心命题。

---

## 3. MCP 一键组装结果

执行菜单 `MOBA Demo / 一键组装测试战场`（MCP `execute_menu_item`），**0 Error / 0 Warning**，六项收尾校验全绿：

```
✅ 阶段九表现层接线校验通过：英雄 / 小兵预制体均已挂 Animator + AnimationComponent，
   控制器 UnitAnimator.controller 已注入且根运动已关闭；含 Idle/Run/Attack/Spell/Die 五状态与四参数
✅ 技能装配校验通过：Q/W/E/R 四槽配置、法力、状态效果容器、弹道生成器与技能输入层均已就位
✅ 英雄编制校验通过：场上共 10 个英雄 —— 蓝方 5 个（1 玩家 + 4 AI）、红方 5 个（全 AI）；无游离英雄
✅ AI 技能分配校验通过：9 个 AI 英雄 × 4 槽 = 36 个技能全部来自技能池，玩家专属盖伦 QWER 未泄漏
✅ 实体配色校验通过：蓝英雄 HeroModelBlue / 红英雄 HeroModelRed / 蓝小兵 MinionModelBlue / 红小兵 MinionModelRed
✅ Stats Data 注入校验通过：全部 EntityBase 均已正确注入属性资产
```

关键装配动作（摘自组装日志）：
- `HeroPrefab.prefab：挂载模型子节点 Model ← HeroModel；关闭白盒外观渲染器 Body（保留碰撞体）`
- `MinionPrefab.prefab：挂载模型子节点 Model ← MinionModel；关闭白盒外观渲染器 MinionPrefab（保留碰撞体）`
- `VfxRoot 已装配但**已禁用**（enableVfx = false）`
- `VfxSpawnerRoot 已装配`（三个特效槽位：未导入，槽位预留）

---

## 4. Play Mode 实机测试结果

**运行时长**：约 **110 秒**（对局从开局跑到 蓝方 10 杀 / 红方 7 杀，双方均已在推塔）

### 4.1 Console 统计

| 类别 | 数量 | 说明 |
|---|---|---|
| **Error** | **0** | ✅ 无材质丢失、无动画绑定失败、无空引用 |
| Warning | 33 | **全部是阶段八既有的拥堵/牵引诊断**，非本轮回归（见下） |

33 条 Warning 的构成（全部为**设计内的诊断日志**，不是缺陷）：

| 日志 | 含义 |
|---|---|
| `[MoveState]/[ChaseState] XXX 连续 1.0 秒几乎未移动，判定为被卡住，已强制重新下达移动指令` | 桥面仅 14 米宽、21+ 单位同屏，局部避让导致的正常拥堵；这是**自愈机制在工作**（检测到卡住就重下指令） |
| `[EntityAIController] XXX 已被牵引出界（距追击起点超过 16.0），拒绝重新进入追击` | 滞回/牵引极限的**正常触发**（防止单位为了远处敌人整段脱线） |
| `[MoveState] XXX 的路径点 N 被长期占住（连续 3 次重新下令仍未移动），已跳过它继续推进` | 卡死自愈的**兜底路径**（避免永久停滞） |

> 这些告警在阶段八封版时即已存在（本轮之前的日志里就有同样内容），本轮**没有新增任何一类告警**。

### 4.2 运行期层级取证（Play 中执行）

```
BlueHero_0     | children=2 | Model=OK
    mesh=garen_base_mat v=4110 | mat=HeroModelBlue
    Body renderer enabled=False        ← 白盒胶囊体已让位
    colliders=1                        ← 碰撞体保留（索敌/拾取不受影响）
RedHero_0      | children=2 | Model=OK | mat=HeroModelRed
BlueBase       | Model=NULL | Body renderer enabled=True    ← 建筑无模型，白盒保留
BlueTower_Inner| Model=NULL | Body renderer enabled=True
minions: withModel=11/11
heroes:  withModel=10/10
```

### 4.3 视觉验证

| 截图 | 内容 |
|---|---|
| `Assets/Screenshots/mcp_playtest_01.png` | 开局全景：模型化单位、绿色血条、HUD 四技能图标、计分板 |
| `Assets/Screenshots/mcp_hero_closeup.png` | 近景：**蓝方队伍**与**红方队伍**的模型与阵营配色对比 |
| `Assets/Screenshots/mcp_playtest_02.png` | 中期：盖伦英雄模型站在蓝方基地旁，技能图标与血/蓝条正常 |

**肉眼确认**：单位已从胶囊体变为带盾牌/长枪/大剑的真实模型；蓝方偏蓝、红方偏红，敌我一眼可辨。

### 4.4 Play 未污染工程

退出 Play 后核对：`MainScene.scene` / `HeroPrefab.prefab` / `MinionPrefab.prefab` 的 mtime
均为 **17:19:49（组装时刻）**，Play 期间**没有任何写盘** ✅

---

## 5. 已知问题与后续建议（按优先级）

1. **技能没有任何画面反馈**（本轮引入的可见退化）
   白盒占位特效已禁用、正式特效预制体未导入 → 技能只有数值变化。
   *建议*：要么先做 3 个特效预制体放进 `Assets/Prefabs/VFX/`，要么临时把 `VfxRoot` 的 `enableVfx` 勾回。

2. **死亡表现缺失**：尸体隐身已删、`Die` 动画未接入 → 阵亡单位会**原样站着**（小兵 2 秒后销毁，英雄到复活为止）。
   *建议*：优先补 `Die` 动画片段；这是"为死亡动画让路"的必然过渡态。

3. **动画控制器是空壳**：`UnitAnimator.controller` 五状态均**未挂片段** → 参数会正确变化，但画面无动作。
   *建议*：导入 `Idle/Run/Attack/Spell/Die` 五个片段并拖到对应状态，逻辑代码零改动。

4. **建筑仍是白盒**：塔与基地没有可用模型来源。
   *建议*：提供建筑模型，或接受白盒建筑（它们有独立配色材质，可读性尚可）。

5. **地面仍是纯白 Plane**：README §2.2.4 要求替换为 MOBA 贴图场景。
   *建议*：制作地面贴图（含兵线视觉引导），注意贴图与 NavMesh 可行走区必须一致。

6. **拥堵告警偏多**：33 条/110 秒。当前是"自愈机制在工作"，不影响胜负，但可读性差。
   *建议*（阶段八已登记）：`MoveState`/`ChaseState` 增加"路径被外部清空即立刻重下指令"的分支。

7. **UI 图标版权**：技能图标与模型同属 Riot Games 资产，仅限非商业使用。

---

## 6. 附录：本轮产物清单

**新增脚本 / 工具**
```
.workbuddy-ai/tools/lmesh2obj.py       # .lmesh → OBJ（含格式注释与自校验）
.workbuddy-ai/tools/skn2obj.py         # .skn → OBJ（含三条交叉校验，解不出就报错退出）
```

**新增美术资产**
```
Assets/Art/Textures/UI/HeroSkill{Q,W,E,R}.png           # 盖伦技能图标（64×64，Sprite 导入）
Assets/Art/Models/Heroes/HeroModel.obj / .mtl / .prefab / HeroModel_Texture.png
Assets/Art/Models/Minions/MinionModel.obj / .mtl / .prefab / MinionModel_{Blue,Red}.png
Assets/Art/Materials/HeroModel{Blue,Red}.mat            # 盖伦贴图 + 阵营色调
Assets/Art/Materials/MinionModel{Blue,Red}.mat          # 小兵蓝/红贴图
Assets/Screenshots/mcp_playtest_01.png / mcp_hero_closeup.png / mcp_playtest_02.png
```

**改动的代码**
```
删除  Assets/Scripts/Core/EntityVisuals.cs
改    Assets/Scripts/Units/HeroController.cs            # 删除 2 处隐身调用
改    Assets/Scripts/AI/EntityAIController.cs           # 删除 1 处隐身调用
改    Assets/Scripts/Editor/AutoSceneBuilder.Stage9Assembly.cs   # 模型挂载 + 读回校验
改    Assets/Scripts/Editor/AutoSceneBuilder.VfxAssembly.cs      # 禁用白盒特效 + 配色材质优先级
改    Assets/Scripts/Editor/AutoSceneBuilder.UIAssembly.cs       # 图标四槽 + 正式图标优先
改    Assets/Scripts/Editor/AutoSceneBuilder.cs                  # 组装日志文案同步
```

**MCP 工具使用记录**：`execute_code`(4) / `manage_scriptable_object`(5) / `refresh_unity`(3) /
`execute_menu_item`(1) / `manage_editor`(2) / `read_console`(6) / `manage_camera`(4) /
`manage_prefabs`(2) / `generate_model.list_providers` / `import_model.list_providers`
