# 阶段七：UI 与可视化架构草案（待架构师审批）

> 版本：Draft v1 · 2026-09-25
> 范围：`MOBA_Demo_Plan.md` 阶段七「信息可视化与对局 UI」
> 状态：**纯草案。本轮未改任何 `.cs`、未动 `MainScene.scene`、未改任何资产。**
> 权威顺序：`README.md` > `MOBA_Demo_Plan.md` > 本草案。

---

## 0. 结论先行

1. **UI 分三层**：`世界空间层`（血条 / 飘字）、`屏幕空间 HUD 层`（计分板 / 播报 / 技能面板 / 复活遮罩）、`逻辑数据层`（`MatchStatsTracker` 等）。
   三层之间只有一条通路：**逻辑层广播事件 → 表现层只读订阅**（README §3.4）。
2. **必须先做 1 项工程前置**：把内置包 `com.unity.ugui` 加回 `Packages/manifest.json`。
   当前工程**没有 uGUI**，`Image` / `Text` / `Radial Fill` 冷却遮罩**一个都用不了**（详见 §1.1）。这是阶段七的硬门槛，不是可选项。
3. **必须补 5 处逻辑层"无损增量"**（全部是新增成员，不改任何既有签名，阶段一~六代码零改动）：见 §8。
4. **血条与飘字不由单位自己挂载**，而是由中央管理器订阅 `EntityRegistry` 的"出现 / 消失"通知统一挂载 → **`HeroPrefab` / `MinionPrefab` / 基地 / 塔的预制体零改动**，动态生成的小兵自动覆盖。
5. **对象池只做一套**：泛型 `PrefabPool<T>` 放 `Core/`，阶段七的 4 个消费方（血条 / 飘字 / 播报条目 / 小地图标记）与阶段八的 VFX 共用，避免日后出现两套池。
6. **待你裁决 9 项**：见 §11（其中 D1 是"不做就写不出 HUD"的前置项）。

---

## 1. 前置事实核查（本轮实测，非推测）

| # | 事实 | 结论 / 影响 | 依据 |
|---|---|---|---|
| 1.1 | **工程未安装 uGUI**：`Packages/manifest.json` 无 `com.unity.ugui`，`Library/PackageCache` 无该目录，`Packages/packages-lock.json` 无该条目 | `using UnityEngine.UI;` 会直接 CS0246。`Image` / `Text` / `Button` / `GraphicRaycaster` / `EventSystem` / `Image.type=Filled`（Radial Fill）全部不可用 → **HUD 与 CD 遮罩无法实现**。修法：manifest 增加 `"com.unity.ugui": "1.0.0"`。该包**是编辑器内置包**（`Editor/Data/Resources/PackageManager/BuiltInPackages/com.unity.ugui` 已存在）→ **离线即可解析，不需要联网下载** | `grep ugui Packages/*.json` 无命中；`ls Library/PackageCache` 无该项；内置包目录存在 |
| 1.2 | **TextMeshPro 不可用** | TMP 不在内置包清单里（`BuiltInPackages` 下只有 `com.unity.ugui`、`com.unity.ui` 等，无 `com.unity.textmeshpro`）→ 需要走注册表联网下载。**本轮不用 TMP**，全部文字走 uGUI 旧版 `Text`。阶段八若要 TMP，再单独走一次加包流程 | `ls BuiltInPackages \| grep textmesh` 无命中 |
| 1.3 | 内置字体存在 | `Editor/Data/Resources/tuanjie default resources` 中同时含 `LegacyRuntime` 与 `Arial` 两个字体资产 → 工具按 `"LegacyRuntime.ttf"` → 回退 `"Arial.ttf"` → 再回退 `Font.CreateDynamicFontFromOSFont` 三级兜底。**字体为 null 时 uGUI Text 不报错、只是什么都不显示**（典型静默失效），故工具必须显式赋值并校验 | 二进制资源内字符串命中 |
| 1.4 | **场景里没有防御塔** | `TowerController` 仍"已就位、未挂载"（阶段六 6B 顺延），`AutoSceneBuilder` 也不建塔 → 血条 / 小地图的"防御塔"分支**本轮无实体可挂**。UI 侧按 `EntityType` 通用处理，**不写塔专属分支**（写了就是死代码） | 工具主流程步骤 3~16 无塔 |
| 1.5 | **`HealthComponent` 没有 `OnDamaged`** | README §3.4 已把 `OnDamaged ─▶ 受击特效 + 伤害飘字 + 血条刷新` 写进契约，但组件目前只有 `OnHealthChanged` / `OnShieldChanged` / `OnDied`。**飘字没有事件可订阅** → 必须补（§8-A） | `HealthComponent.cs` 事件区 |
| 1.6 | **`MatchConfigData` 只有 `startDelay`** | 无 `respawnTime`。该文件注释已预告"待真正需要时再按增量方式补充字段" → 阶段七正是那个时刻（§8-D） | `MatchConfigData.cs` |
| 1.7 | 击杀归属的数据已经就位 | `HealthComponent.LastDamageSource`（阶段六为此预留，注释明写"阶段七的击杀播报与计分板依赖它"）+ `TakeDamage(amount, source)` 重载 → **归属判定不需要新事件** | `HealthComponent.cs:79` |
| 1.8 | 技能面板的数据已经就位 | `SkillComponent.GetSkillData(slot)`（含 `Icon` / `ManaCost` / `Cooldown`）、`GetCooldownRemaining(slot)`、`ManaComponent.OnManaChanged`、`HeroController.OnHeroDied` 全部已存在 → **技能面板不需要改技能系统** | `SkillComponent.cs:310/328`、`SkillData.cs` |
| 1.9 | 现有 UI 栈是 `OnGUI` | `MatchResultView` 用 `OnGUI` 实现（阶段四已验收）。阶段七新增 UI 全走 uGUI → **双栈并存**，是否本轮统一见 D5 | `MatchResultView.cs` |
| 1.10 | `AutoSceneBuilder.cs` 已 2993 行 | 再塞约 600 行 UI 装配会到 3600 行。建议改成 `partial class` 并拆出 `AutoSceneBuilder.UIAssembly.cs`（见 §6.1） | `wc -l` |

---

## 2. 分层与文件清单

### 2.1 新增文件

| 文件 | 命名空间 | 职责 |
|---|---|---|
| `Scripts/Core/PrefabPool.cs` | `MOBA.Core` | 泛型组件池（阶段七 4 方 + 阶段八 VFX 共用） |
| `Scripts/UI/WorldHealthBarManager.cs` | `MOBA.UI` | 血条编排者：订阅实体出现/消失 → 池取/还 → `Bind` |
| `Scripts/UI/WorldHealthBarView.cs` | `MOBA.UI` | 单条血条：订阅血量/护盾/死亡、billboard、恒定像素尺寸 |
| `Scripts/UI/DamagePopupManager.cs` | `MOBA.UI` | 飘字编排者 + 单循环动画驱动 |
| `Scripts/UI/DamagePopupView.cs` | `MOBA.UI` | 单个飘字（`Text` + 生命周期状态） |
| `Scripts/UI/HeroHUDView.cs` | `MOBA.UI` | 头像 + 血条 + 蓝条 + 两个技能槽的总装 |
| `Scripts/UI/SkillSlotView.cs` | `MOBA.UI` | 单技能槽：图标 + CD 径向遮罩 + 秒数 |
| `Scripts/UI/KillFeedView.cs` | `MOBA.UI` | 顶部播报队列（入队 / 自动淡出 / 不重叠） |
| `Scripts/UI/ScoreboardView.cs` | `MOBA.UI` | 双方击杀 / 死亡计分 |
| `Scripts/UI/RespawnOverlayView.cs` | `MOBA.UI` | 死亡遮罩 + 复活倒计时 |
| `Scripts/UI/MinimapView.cs` | `MOBA.UI` | 小地图映射（**是否本轮纳入见 D6**） |
| `Scripts/Gameplay/MatchStatsTracker.cs` | `MOBA.Gameplay` | 击杀/死亡唯一计数器 + 播报事件源 |
| `Scripts/Editor/AutoSceneBuilder.UIAssembly.cs` | `MOBA.Editor` | 步骤 17：UI 与可视化一键装配（`partial`，见 D7） |

> `WorldHealthBarManager` / `DamagePopupManager` / `PrefabPool` **不在 `MOBA_Demo_Plan.md` 第 2 节目录清单里**，是本草案新增的三个类型，理由：
> - 没有 Manager，就得给每个单位挂一个 `HealthBarBinder` 组件 → 单位预制体必须改、逻辑层预制体上出现 UI 概念（与铁律 2 的"逻辑层禁止引用 UI"擦边），且每新增一种单位都要记得挂；
> - 没有池，就退化成 `Instantiate/Destroy`（明确禁止）；
> - 池只做一份而不是两份（UI 一份、VFX 一份），是阶段八复用前提。

### 2.2 修改文件（全部为"新增成员"，不改既有签名）

| 文件 | 增量 |
|---|---|
| `Components/HealthComponent.cs` | `event Action<float,float,EntityBase> OnDamaged`；`void Revive()` |
| `Components/ManaComponent.cs` | `void RestoreFull()` |
| `Core/EntityRegistry.cs` | `static event Action<EntityBase> OnEntityRegistered / OnEntityUnregistered` |
| `ScriptableObjects/MatchConfigData.cs` | `float respawnTime`（默认 8s） |
| `Units/HeroController.cs` | `Revive(Vector3)`；`respawnAnchor`；`event Action<HeroController> OnHeroRespawned` |
| `Gameplay/MatchController.cs` | 复活编排（订阅 `OnHeroDied` → 协程 → 广播倒计时/完成事件） |
| `Editor/AutoSceneBuilder.cs` | `partial` 关键字 + 步骤 17 调用 + 接管名单增加 2 个根名 + 收尾校验 |
| `Packages/manifest.json` | `"com.unity.ugui": "1.0.0"` |

### 2.3 画布层级（工具生成的场景结构）

```
UIRoot                    [Canvas: Screen Space - Overlay, CanvasScaler 1920x1080, match 0.5]
├── TopBar                (顶部锚点，HorizontalLayout 由工具摆位，不用 LayoutGroup 省重建)
│   ├── ScoreboardView    (左上：蓝方击杀/死亡 | 红方击杀/死亡)
│   └── KillFeedView      (顶部居中：最多 5 条消息，逐条淡出)
├── BottomBar
│   └── HeroHUDView       (左下：头像 + 血条 + 蓝条 + SkillSlot Q + SkillSlot W)
└── Overlay
    └── RespawnOverlayView(全屏暗色遮罩 + "已阵亡" + 倒计时数字，默认隐藏)

WorldUIRoot               (空物体，仅作池宿主，便于按名清理)
├── HealthBar_0 … N       (World Space Canvas + 血条 Image ×N，池对象)
└── PopupCanvas           [World Space Canvas, 统一 billboard，池宿主]
    └── Popup_0 … N       (Text，池对象)
```

**关键取舍：整个 HUD 不需要 `EventSystem` / `GraphicRaycaster`。** 阶段七所有 HUD 元素都是**只读显示**（没有任何点击交互；"重新加载场景"按钮仍在已验收的 `MatchResultView` 的 `OnGUI` 里）。省掉射线检测层 = 省一次全屏 UI 射线遍历。
所有 `Image` / `Text` 一律 `raycastTarget = false`（工具统一设置），避免无意义的 `GraphicRaycaster` 候选。

---

## 3. 维度一：世界空间 UI

### 3.1 实体血条：怎么挂载

**方案：中央管理器 + 池 + 事件挂载（单位侧零改动）。**

```
EntityRegistry.OnEntityRegistered ──▶ WorldHealthBarManager ──▶ pool.Get() ──▶ bar.Bind(entity)
EntityRegistry.OnEntityUnregistered ─▶ WorldHealthBarManager ──▶ bar.Unbind() ─▶ pool.Release(bar)
```

- **挂载时机**：管理器 `OnEnable` 里 **先订阅** `EntityRegistry` 的两个事件，**再** `Snapshot()` 补挂"订阅前就已登记"的实体（基地、场景预置英雄）。
  ⚠️ 顺序不能反：先 Snapshot 后订阅 → 订阅瞬间到达的新实体会漏挂；两者都做则必须**按"该实体是否已有血条"去重**（用 `bar.BoundEntity == entity` 判定）。
- **为什么不让单位自己挂**：血条是**表现层**。让 `HeroPrefab` 挂一个 `HealthBarBinder`，等于逻辑预制体上出现"UI 挂点"概念；且 `MinionSpawner` 动态生成的小兵、工具新建的基地都要各自记得挂。中央管理器把这件事收口成一处，且天然满足"表现层可整体关闭"（禁用管理器 → 血条全消失，对局结果不变）。
- **池**：`PrefabPool<WorldHealthBarView>`，预生成 **24**（同屏 30 单位目标下留余量）。池耗尽 → `Get()` 返回 null，血条不显示并**只告警一次**；**绝不阻塞、绝不 Instantiate**（铁律 3"表现可丢失"）。
- **锚点（高度）**：`Bind()` 时**一次性测量**——遍历单位及其子节点的 `Collider` 求合并包围盒，取 `bounds.max.y - transform.position.y` 作为 `anchorOffsetY` 缓存，随后释放数组。
  - 收益：运行期每帧只做一次 `position + up * anchorOffsetY` 加法；**无需按 `EntityType` 维护高度表**；预制体换模型（阶段八）自动适配。
  - 兜底：单位无 `Collider` 时用 `[SerializeField] defaultAnchorHeight = 1.5f`（并 `LogWarning` 一次——无碰撞体的单位本来就索敌不到，属既有已知问题）。
- **Billboard**：在 `LateUpdate` 里 **直接复制主相机的旋转**（`barTransform.rotation = cameraTransform.rotation`），**不用 `LookAt`**。
  - 理由：血条只需要"朝向相机"，不需要"注视相机"；复制旋转是 4 次赋值，`LookAt` 要算一次正交化；而且所有血条共用同一旋转，行为完全一致（不会有某个血条因为浮点误差轻微歪头）。
- **恒定屏幕像素高度**：`localScale = Vector3.one * (distanceToCamera * pixelHeightConstant)`。
  - 透视相机下物体的屏幕像素高度 ∝ 世界高度 / 距离，乘上距离即得恒定像素高度——这样滚轮缩放（`CameraController.zoomScale` 0.5~2）时血条**不会跟着变大变小**，验收项"偏差 < 5 px（1080p）"才有意义。
- **显隐策略**（三项都是 O(1)）：
  1. **满血隐藏**（`hideWhenFull`，默认开，README 明确要求）；
  2. **死亡隐藏**：订阅 `OnDied` → 隐藏（尸体上留一条空血条没有意义；`HealthComponent` 死亡时已清盾，此处再隐藏条）；
  3. **相机背后隐藏**：`Vector3.Dot(cameraForward, barPos - cameraPos) <= 0` → `canvas.enabled = false`。视锥内剔除交给渲染器（World Space Canvas 的几何参与正常视锥剔除），本项只解决"相机背后的条被画成镜像"这一观感问题。
- **配色**：`localTeam`（工具从英雄阵营注入，默认 `Player`）→ 同阵营 **绿**、异阵营 **红**（README："敌方红 / 友方绿"）。中立单位本轮不存在，色表留一项灰色即可（不写分支）。
- **护盾覆盖层**（D8）：血条上叠一条浅蓝 `Image`，宽度 = `currentShield / maxHealth`，订阅 `OnShieldChanged`。阶段六护盾已落地，不显示会让"技能放了没效果"。
- **禁止每帧轮询数值**（README 硬要求）：数值只走 `OnHealthChanged` / `OnShieldChanged`；每帧只写 `position` / `rotation` / `localScale`（纯 Transform 写入，零分配）。

### 3.2 伤害飘字：对象池与动画

**技术路线（推荐）：共享世界空间画布 + 子 `Text`（D2）**

```
PopupCanvas (1 个 World Space Canvas，每帧只做 1 次 billboard 旋转)
└── Popup_i (Text，池对象；localPosition = canvas.InverseTransformPoint(受击点))
```

- **为什么不是"每个飘字一个 Canvas"**：N 个 Canvas = N 个 draw call + N 次 billboard；共享画布后全部飘字合并成 **1 个 draw call**、**1 次旋转**。飘字是"永远面向相机、不参与世界遮挡排序"的元素，共用一个画布在语义上也是对的。
- **为什么不是屏幕空间**：飘字必须"钉在世界里的受击点"——相机平移（中键拖拽）或缩放时，飘字要留在原地而不是跟着屏幕走。屏幕空间方案每帧都要重投影，本质是在模拟世界空间，白做一层。
- **已知代价**：World Space Canvas 默认 `ZTest LEqual`，飘字会被场景几何（例如 2.5³ 的基地方块）遮挡。阶段七白盒可接受；阶段八的正式解法是"UI 专用 Layer + 只渲染该层的第二相机"，本轮不做，登记为已知项。

**对象池方案**

- `PrefabPool<DamagePopupView>`，预生成 **32**，池耗尽同血条（返回 null + 一次性告警，不阻塞）。
- **`Get()` 契约**：返回的实例一定是 active、已 `Reset()` 的干净状态。`Release()` 时执行 `Reset()`：停用、清文本、复位颜色/缩放、清空生命周期计时、退回宿主父节点。
- **动画驱动：单管理器循环，不给飘字挂 `MonoBehaviour.Update`。**
  - `DamagePopupManager.Update()` 遍历 `activeList`（倒序 for 循环，避免迭代器分配），逐个推进生命周期：
    | 阶段 | 时长占比 | 表现 |
    |---|---|---|
    | 上浮 | 0 → 60% | `y = anchorY + riseHeight * easeOutQuad(t)`（先快后慢，符合"打出去"的手感）；同时 `scale = 0.8 → 1.15 → 1.0` |
    | 淡出 | 60% → 100% | `color.a = 1 - smoothstep(t)`；`Text.color` 直接写，无分配 |
  - 总时长 `lifetime = 0.8s`，到期 `Release()`。
  - 收益：32 个飘字 = **1 个 Update**（而不是 32 个），且生命周期逻辑集中一处，池回收不可能漏。
- **数据源与配色**（订阅 §8-A 的 `OnDamaged`）：
  - `healthDamage > 0` → **红字**（实际扣血量，整数显示）；
  - `shieldAbsorbed > 0` → **灰蓝字**"吸收 N"（护盾挡下的伤害也是"打中了"，必须给反馈，否则玩家会以为技能没命中）；
  - `source == null` → 正常显示（环境伤害）。
- **零 GC 细节**：
  - 文本**只在创建时写一次**（`damage.ToString("0")` → 每次伤害 1 次短字符串分配，属"每次事件固有分配"，与阶段六弹道同级；稳态（无伤害发生时）为 0）；
  - 位置 / 颜色 / 缩放只写属性，不产生分配；
  - 不用 `foreach`、不用 LINQ、不用闭包；池的 `activeList` 复用同一 `List` 实例。
- **治疗飘字**：`HealthComponent.Heal()` 目前**零调用方**（无恢复道具/技能）。按本项目"无消费方的能力 = 死代码"标准，**本轮不加 `OnHealed`**；将来出现治疗来源时再加一行事件 + 绿色配色分支。

---

## 4. 维度二：屏幕空间 HUD

### 4.1 计分板与击杀播报

**归属判定链路（复用阶段六预留的数据，不新增事件）**

```
EntityRegistry.OnEntityRegistered ──▶ MatchStatsTracker 挂钩 entity.Health.OnDied
EntityRegistry.OnEntityUnregistered ─▶ MatchStatsTracker 摘钩（防已销毁对象回调）
HealthComponent.OnDied ──────────────▶ 读 health.LastDamageSource → 归属判定 → 累加计数 → 广播 OnKillLogged
```

**归属判定规则（唯一实现处）**

| 情形 | 判定 |
|---|---|
| `source == null` | **不计击杀**，只给受害者阵营 +1 死亡；播报文案"XX 阵亡" |
| `source.Team == victim.Team` | 不计击杀（防"自己人打死自己人"被算成敌方击杀）；只 +1 死亡 |
| `source.Team != victim.Team` | `source.Team` 击杀数 +1，`victim.Team` 死亡数 +1 |
| `source` 已被销毁 | 视为 `null`（`is UnityEngine.Object` 存活检查，接口/组件引用销毁后不会自动变 null，这是本项目既有坑） |

**统计口径（D3）**：建议**只统计 `EntityType.Hero` 的死亡**。
理由：小兵每几秒死一个，若纳入统计，`KillFeedView` 会变成刷屏列表、计分板数字会以"补刀数"的量级跳动，而 README §2.1.5 的语义是"击杀播报 / 双方击杀数"（MOBA 语境下指英雄击杀）。**扩展点**：将来要做补刀数，在 `MatchStatsTracker` 内加一个 `MinionDeaths` 计数即可，播报仍只播英雄。

**数据出口（供 UI 订阅，单向）**
- `event Action<KillRecord> OnKillLogged`（`KillRecord` 为 `readonly struct`：击杀者名 / 阵营、受害者名 / 阵营、是否英雄击杀）→ 播报用；
- `int GetKills(TeamType)` / `int GetDeaths(TeamType)` + `event Action OnScoreChanged` → 计分板用；
- **`MatchStatsTracker` 是计数的唯一权威**：`ScoreboardView` 只读它的值（验收项"击杀数与统计完全一致、无重复计数"由此结构性保证，而不是靠两处各自算对）。

**`KillFeedView`：队列 + 自动淡出 + 不重叠**
- 固定 5 个槽位的 `Text`（预生成，**不池化也不 Instantiate** —— 槽位数量固定，循环复用最省）；
- 新播报入队 → 填充最旧的空槽 / 顶掉最旧一条（FIFO）；每条独立计时，到 `fadeDelay = 3s` 后 0.5s 淡出；
- 布局用**工具摆好的固定 `anchoredPosition`**（不用 `VerticalLayoutGroup`）→ 避免内容变化触发布局重建（`LayoutRebuilder`）导致 GC；
- 文本只在"播报内容变化"时写（缓存 `lastText` 比较），不每帧写。

**`ScoreboardView`**：4 个 `Text`（蓝方击杀/死亡、红方击杀/死亡），只订阅 `OnScoreChanged` 时刷新，稳态零分配。

### 4.2 技能面板（Q / W 图标 + 蓝耗 + CD 遮罩）

**数据来源（全部已就位，技能系统零改动）**

| 元素 | 来源 | 更新方式 |
|---|---|---|
| 图标 | `SkillData.Icon`（`GetSkillData(slot).Icon`） | `Start` 一次 |
| 蓝耗 | `SkillData.ManaCost` | `Start` 一次（静态配置） |
| 血条 | `HeroController` / `HealthComponent.OnHealthChanged` | 事件 |
| 蓝条 | `ManaComponent.OnManaChanged` | 事件（`ManaComponent` 内部已做 0.5 点节流） |
| CD 遮罩 + 秒数 | `SkillComponent.GetCooldownRemaining(slot)` | **每帧查询**（见下） |
| 死亡置灰 | `HeroController.OnHeroDied` / `OnHeroRespawned` | 事件 |

**CD 遮罩实现（Radial Fill）**
```
Image.type          = Filled
Image.fillMethod    = Radial360
Image.fillOrigin    = Top          // 12 点方向起
Image.fillClockwise = false        // 顺时针扫过（视觉习惯）
Image.fillAmount    = remaining / totalCooldown   // 1 → 0
```
- 未冷却时 `fillAmount = 0` 并隐藏遮罩 `Image`（`enabled = false`），避免无谓绘制。
- 遮罩色：半透明黑（`0,0,0,0.65`），压在图标上。
- 秒数文本：`Mathf.CeilToInt(remaining)` **只在整秒变化时写 `Text`**（缓存 `lastDisplayedSecond`）。
  ⚠️ 这是本阶段最容易被忽略的 GC 点：若每帧 `text = remaining.ToString("F1")`，60 FPS 下每秒产生 60 次字符串分配，验收项"UI 稳态 GC Alloc ≈ 0 B/帧"必挂。

**"为什么 CD 可以每帧查询"（对 README"禁止每帧轮询"的边界澄清）**
- README 的"禁止每帧轮询"针对的是**数值**（血量/蓝量——它们有事件，轮询等于白读）；
- **冷却没有"变化事件"**（它是一个随时间递减的剩余量，不是离散状态），唯一能保证"遮罩进度与实际可释放时刻误差 ≤ 0.05s"的方式就是每帧读 `GetCooldownRemaining`；
- 代价是**一次 `float` 减法 + 一次比较**，零分配、零物理查询。
- 若你希望连这个都省掉，替代方案是让 `SkillComponent` 在 CD 结束时广播一次 `OnCooldownFinished(slot)`（`Update` 里比较 `remaining > 0 → == 0` 跳变）→ 但遮罩进度仍要每帧读。**结论：遮罩必须每帧读，事件只用来省"整秒文本"的更新**。

**美术占位**：本轮全部用纯色 `Image` + 工具生成的 1 个白色 Sprite（§6.2）。
`SkillData.Icon` 目前为空 → 工具把占位 Sprite **注入空字段**（沿用 `PatchHeroStatsGaps` 的"只补空、不覆盖已有值、不碰资产本体"原则，保证 guid 恒定）。

---

## 5. 维度三：核心机制补充 —— 英雄复活

### 5.1 职责归属（D4）

**推荐：`MatchController` 编排倒计时，`HeroController` 执行复活。**

| 依据 | 说明 |
|---|---|
| 配置在谁手上 | `respawnTime` 属 `MatchConfigData`，而 `MatchController` 已经持有它（`startDelay` 同源）→ 不需要让"一个单位"去读对局配置 |
| 规则在谁手上 | "对局结束时不再复活"是**对局级裁决**，`MatchController.IsMatchOver` 是唯一权威 → 复活的启停天然属于它 |
| 谁该动单位的组件 | `HeroController.HandleDied` 亲手禁用了 `NavMeshAgent` / `Collider` / `PlayerCommandController`，**它的逆操作必须由它自己做**（否则会出现"谁解锁谁"的归属混乱，与阶段六"眩晕与前摇共用锁"的教训同类） |
| 与计划书的一致性 | 计划书 `HeroController.cs # 含复活` → 本方案中 `Revive()` 确实在 `HeroController`，只是**计时不由它持有** |

**被否决的方案**：`HeroController` 自持 `MatchConfigData` 引用 + 自己起协程。
否决理由：一个"单位"持有"对局配置资产"是层级倒挂；且它还要再拿一个 `MatchController` 引用来问 `IsMatchOver` —— 变成两处状态源，违反"单一职责 + 单一数据源"。

### 5.2 状态机与流程

```
Alive ──(Health.OnDied)──▶ Dead(倒计时) ──(倒计时结束 & !IsMatchOver)──▶ Alive
                              └──(对局结束)──▶ 永久 Dead（不再复活）
```

1. `HealthComponent` 血量归零 → 广播 `OnDied`；
2. `HeroController.HandleDied()`（**已存在，不改**）→ 停寻路 → 清目标 → 禁指令层 → 禁 `NavMeshAgent` → 禁 `Collider` → `SetSelectable(false)` → 广播 `OnHeroDied`；
3. `MatchController` 收到 `OnHeroDied`：若 `IsMatchOver` → 直接返回（不复活）；否则 `StartCoroutine(RespawnRoutine)` 并广播 `OnRespawnCountdownStarted(hero, duration)`；
4. 协程每帧广播 `OnRespawnCountdownChanged(remaining)`（或让 `RespawnOverlayView` 每帧读 `MatchController.GetRespawnRemaining(hero)` —— **推荐后者**，事件只报"开始/结束"两个跳变，倒计时数字每帧读，理由同 CD 遮罩）；
5. 到时 → `hero.Revive(respawnAnchor)`；
6. `HeroController.Revive()` 内部完成全部逆操作（见下），成功后广播 `OnHeroRespawned` → 遮罩隐藏、HUD 恢复彩色。

### 5.3 `HeroController.Revive(Vector3)` 的逆操作清单（严格与 `HandleDied` 对称）

| 顺序 | 动作 | 为什么必须是这个顺序 |
|---|---|---|
| 1 | `hasHandledDeath = false` | 否则下次死亡收尾会被自己的幂等标记挡掉 |
| 2 | 重新订阅 `health.OnDied` | `HandleDied` 里退订了，不补订阅 → **第二次死亡不会再触发任何收尾**（静默失效，最难查） |
| 3 | `health.Revive()` | 复位 `isDead` / 满血 / 清盾 / 清 `LastDamageSource`；**内部广播 `OnHealthChanged` + `OnShieldChanged`**（血条自动刷新） |
| 4 | `mana.RestoreFull()` | 满蓝；广播 `OnManaChanged` |
| 5 | **先启用 `NavMeshAgent`**，再 `agent.Warp(spawnPos)` | 代理被禁用时 `Warp` 不生效 → 顺序反了会"复活在原地" |
| 6 | 校验 `agent.isOnNavMesh` | 失败即 `LogError`（防"复活到 NavMesh 之外 → 永久卡死"，这是本项目踩过的同族坑） |
| 7 | 重新启用 `Collider`（与 `HandleDied` 用同一批：`GetComponentsInChildren`） | 不恢复 → 复活后**永久索敌不到、也点不中**，且不报错 |
| 8 | `entity.SetSelectable(true)` | 恢复可被选中 |
| 9 | `commandController.enabled = true` | 恢复玩家输入 |
| 10 | `movement.Stop()` + 清 `Targeting` | 清掉死亡前残留的路径与攻击指令 |
| 11 | 广播 `OnHeroRespawned` | 表现层（HUD 置灰解除、遮罩隐藏）据此恢复 |

**复活点（`respawnAnchor`）**：
`HeroController` 在 `Start` 记录 `respawnAnchor = transform.position`（= 工具实例化英雄的位置，即蓝方出生点 `(-12, 0, 0)`），并暴露 `[SerializeField] Transform respawnPoint`（工具可注入覆盖，留空则用初始位置）。
**不新建 `SpawnPoint` 组件**（沿用阶段五"无消费方的标记组件 = 死代码"裁决）。

### 5.4 防重入与边界

- **协程句柄唯一**：`MatchController` 持有 `respawnRoutine`；对局结束时 `StopCoroutine` 并置空（与既有 `startRoutine` 同一处理）；
- **英雄不可能"死两次"**：`HealthComponent.isDead` 保证 `OnDied` 只广播一次 → 只有一个倒计时；
- **复活期间对局结束**：协程被掐断，英雄保持死亡状态，结算界面照常显示；
- **`HealthComponent.Revive()` 刻意不复用 `Initialize()`**：`Initialize` 的注释已明确禁止复用（它同时承担"首次生成"语义，且被 `EntityBase.ApplyStats` 调用），复用会让"复活"与"初始化"两条路径纠缠。

### 5.5 复活遮罩（`RespawnOverlayView`）

- 全屏半透明黑（`0,0,0,0.45`）+ 居中大字"已阵亡" + 倒计时秒数；
- 只订阅 `MatchController` 的 `OnRespawnCountdownStarted` / `OnRespawnCompleted` 两个跳变事件，倒计时数字每帧读 `GetRespawnRemaining()`；
- 默认隐藏（`SetActive(false)`），`OnDisable` 退订（与 `MatchController` / `MatchResultView` 的订阅姿势一致）；
- **与 `MatchResultView` 的关系**：两者互不感知。对局结束时复活被取消、`MatchResultView` 显示结算 → 不会同时出现两层遮罩。

---

## 6. 维度四：基建升级（`AutoSceneBuilder`）

### 6.1 步骤 17：UI 与可视化装配

新增一个步骤，插在 **步骤 15（英雄实例化）之后、步骤 16（收尾校验）之前**（UI 需要英雄实例作为注入源）。

```
17.1 前置包校验        UnityEngine.UI 可用性（Type.GetType("UnityEngine.UI.Image, UnityEngine.UI")）
                       → 不可用：LogError + 跳过整个步骤 17（不中止本次组装，战场仍可用）
17.2 占位美术资产      Assets/Art/UI/UIWhite.png（4x4 纯白）+ TextureImporter(textureType=Sprite)
                       字体：LegacyRuntime.ttf → Arial.ttf → CreateDynamicFontFromOSFont（三级兜底）
17.3 屏幕 HUD          UIRoot(Canvas + CanvasScaler) → TopBar / BottomBar / Overlay → 各 View
                       所有 Image/Text：raycastTarget = false；不用 LayoutGroup（工具摆好固定坐标）
17.4 世界 UI 根        WorldUIRoot（血条池宿主）+ PopupCanvas（共享世界画布 + 飘字池宿主）
17.5 依赖注入          私有 [SerializeField] → SerializedObject/SerializedProperty
                       Unity 内置组件（Image/Text/Canvas/CanvasScaler）→ 直接写公开属性
17.6 收尾校验          ValidateUISetup()：只提示不改场景
```

**注入清单（零手工拖拽的全部落点）**

| 被注入方 | 注入内容 | 注入方式 |
|---|---|---|
| `WorldHealthBarManager` | `mainCamera` / `localTeam`（取自英雄）/ `barPrefabRoot` / `poolHost` / 配色 | `SerializedObject` |
| `DamagePopupManager` | `popupCanvas` / `poolHost` / 字号 / 生命时长 | `SerializedObject` |
| `HeroHUDView` | `hero`（`HeroController` 实例）/ 各 `Image`/`Text` 引用 | `SerializedObject` |
| `SkillSlotView` ×2 | `skillComponent` / `slot`（枚举，写 `intValue`）/ `iconImage` / `cooldownOverlay` / `cooldownText` | `SerializedObject` |
| `ScoreboardView` / `KillFeedView` | `matchStatsTracker` / 各 `Text` | `SerializedObject` |
| `RespawnOverlayView` | `matchController` / `hero` / 遮罩 `Image` / 倒计时 `Text` | `SerializedObject` |
| `MatchStatsTracker` | 无需注入（纯订阅） | — |
| `MatchController` | `playerHero`（复活编排需要）/ `matchConfig.respawnTime`（只补空） | `SerializedObject` |
| `HeroController` | `respawnPoint`（可空） | `SerializedObject` |

> `Image.type` / `fillMethod` / `fillOrigin` / `fillAmount` / `sprite` / `color` / `Text.font` / `fontSize` / `alignment` 等**都是 Unity 内置组件的公开属性**，工具直接赋值即可，不必走 `SerializedProperty`（`SerializedObject` 只用于"我们自己的 private `[SerializeField]`"）。

### 6.2 幂等性与清理

- `ManagedRootNames` 增加 **`UIRoot` / `WorldUIRoot`** → 步骤 1 自动清理上次生成的全部 UI（池对象是这两个根的子孙，随根一起删）；
- **不做全场景扫描删除**（既有铁律）；不碰用户手工搭的其它 Canvas；
- 占位资产 `Assets/Art/UI/UIWhite.png`：**存在即复用**（不覆盖，避免纹理导入设置被重置）；
- 整个步骤 17 在**同一个 Undo 组**内（沿用现有 `Undo.IncrementCurrentGroup` / `CollapseUndoOperations` 结构），临时对象用完 `DestroyImmediate` 且不登记 Undo。

### 6.3 必须提前规避的三个坑（均为本项目已踩过的同类）

1. **`AddComponent` 瞬间会同步触发 `OnValidate`** → 新 View 组件里的"引用未赋值"校验**必须用 `EditorApplication.delayCall` 延迟**（`EntityBase.OnValidate` 已有先例），否则每次组装刷一片假告警，会训练人忽略 Console。
2. **字体 / Sprite 为 null 时 uGUI 不报错、只是不显示** → `ValidateUISetup()` 必须显式检查 `font != null`、`sprite != null`、`SkillData.Icon != null`，把静默失效变成确定性结论（与 `ValidateStatsInjection` 同一目的）。
3. **按 guid 被场景引用的资产不能 `CreateAsset` 覆盖重建** → 占位 Sprite 走"缺失才创建"；`SkillData.Icon` 只补空字段。

### 6.4 预制体改动 = 零

`HeroPrefab` / `MinionPrefab` / 基地 / 塔**都不需要挂任何 UI 相关组件**。
这是本方案对"零手工拖拽"的最大化：不是"工具帮你把引用拖好"，而是**根本没有引用要拖**。
（若日后改成"单位自带血条挂点"，这条性质就没了 → 明确不做。）

### 6.5 文件拆分（D7）

`AutoSceneBuilder.cs` 现 2993 行，UI 装配约 +600 行。建议把类改为 `partial class AutoSceneBuilder`，UI 装配放 `AutoSceneBuilder.UIAssembly.cs`。
- 收益：一键组装工具仍然只有"一个菜单入口"，但文件可维护性显著改善；
- 风险：极低（`partial` 是编译期语法糖，不改任何既有行为）；
- 若不采纳，则 UI 装配直接追加在原文件末尾。

---

## 7. 对象池设计（统一，唯一一套）

```csharp
namespace MOBA.Core
{
    /// 泛型组件池：阶段七（血条/飘字/播报/小地图标记）与阶段八（VFX/弹道）共用。
    public class PrefabPool<T> where T : Component
    {
        public PrefabPool(T prefab, Transform host, int prewarmCount);
        public T Get();               // 池空 → 返回 null + 一次性告警（绝不 Instantiate）
        public void Release(T item);  // 内部 Reset + SetActive(false) + 回宿主
        public void ReleaseAll();
        public int ActiveCount { get; }
        public int IdleCount { get; }
    }
}
```

**契约（写进类注释，供阶段八复用）**

| 条款 | 内容 | 理由 |
|---|---|---|
| 预生成 | `Prewarm(n)` 在宿主下生成 n 个 inactive 实例，**不登记 Undo** | 运行期零 `Instantiate` |
| `Get()` | 返回 active + `OnSpawn` 已调用的实例；**池空返回 null** | 表现可丢失，逻辑不受影响 |
| `Release()` | 调 `IPooled.OnDespawn()`（退订事件、清状态）→ `SetActive(false)` → 回宿主 | 防"池对象带着上一轮订阅"造成重复回调（本项目接口引用销毁后不变 null 的坑同族） |
| 上限 | 无硬上限（预生成量即上限） | 避免"无界增长"，也避免动态扩容带来的分配 |
| 宿主 | 场景对象（`WorldUIRoot` / `PopupCanvas`），随根名清理 | 幂等 |

**为什么不引入第三方池 / 为什么不泛化到全部实体**：README §6.2 明确"对象池只覆盖弹道、特效与 UI 元素；不提前泛化到全部实体"。本池正好只服务这两类。

---

## 8. 逻辑层事件契约增量（无损增量汇总）

> 全部是**新增成员**。不改任何既有方法签名、不改任何既有事件签名 → 阶段一~六的代码与测试**零改动**（含 `Stage6AutoTester`）。

| # | 类型 | 增量 | 消费方 | 为什么必须加 |
|---|---|---|---|---|
| A | `HealthComponent` | `event Action<float,float,EntityBase> OnDamaged`（参数：**实际扣减的生命值**、**护盾吸收量**、来源） | 飘字、阶段八受击特效 | README §3.4 已把它列为契约，但组件里没有。**护盾全吸收时也广播**（`healthDamage=0`），否则"护盾挡下的那一下"没有任何反馈 |
| B | `HealthComponent` | `void Revive()` | 英雄复活 | 复位 `isDead` / 满血 / 清盾 / 清 `LastDamageSource` + 广播血量与护盾事件。**不复用 `Initialize`**（其注释已禁止） |
| C | `ManaComponent` | `void RestoreFull()` | 英雄复活 | 满蓝 + 广播 |
| D | `EntityRegistry` | `static event Action<EntityBase> OnEntityRegistered` / `OnEntityUnregistered` | 血条、飘字、统计（3 个） | 三个消费方都需要"单位出现/消失"通知。**不加则各自低频轮询 `Snapshot()`**（3 处轮询 = 3 份重复逻辑 + 各自的去重问题）。注册表本身已存在、职责单一，**这不是"全局事件总线"**（只广播"登记/注销"一件事） |
| E | `MatchConfigData` | `float respawnTime`（默认 8s） | 复活 | 计划书明确"复活时间从 `MatchConfigData` 读取" |
| F | `HeroController` | `Revive(Vector3)` + `respawnAnchor` + `event Action<HeroController> OnHeroRespawned` | 复活编排、HUD | 复活的执行方必须是"死亡收尾的执行方" |
| G | `MatchController` | 订阅 `OnHeroDied` + `RespawnRoutine` + `GetRespawnRemaining(hero)` + `OnRespawnCountdownStarted/Completed` | 复活遮罩 | 对局级规则的持有者 |
| H | `SkillComponent` | **无需增量** | — | `GetCooldownRemaining` / `GetSkillData` 阶段六已就位 |
| I | `HealthComponent` | `void Revive()` 内广播顺序：`OnHealthChanged` → `OnShieldChanged` | 血条 | 保证血条在复活瞬间立刻刷新（不依赖事件补发） |

**`OnDamaged` 的广播位置（顺序不可改）**

```
记录来源 → 护盾吸收 → 扣血 → RaiseHealthChanged() → OnDamaged?.Invoke(...) → [若归零] isDead = true → OnDied
```
- 与 README §3.4 的事件顺序一致（`OnDamaged` 先于 `OnDied`）；
- 与既有注释"UI 先看到血量变成 0、再收到死亡"一致；
- **护盾全吸收分支**（当前 `return` 前）补一次 `OnDamaged(0, absorbed, source)`。

---

## 9. 验收标准 → 实现路径对照

| 阶段七验收项 | 实现路径 | 风险 |
|---|---|---|
| 血条位置偏差 < 5px、始终 billboard | 复制相机旋转 + `localScale ∝ 距离`；锚点取包围盒顶部 | 极低 |
| 同屏 20 血条无逐帧 GC | 数值事件驱动；每帧只写 Transform；无字符串/无 foreach | 极低 |
| 血条数值实时一致、无每帧轮询 | `OnHealthChanged` / `OnShieldChanged` | 极低 |
| 每次伤害 1 个飘字、数值 = 实际扣血量 | `OnDamaged` 第 1 参数即"实际扣减量"（`healthBefore - currentHealth`） | **口径需裁决（D9）**：若你要显示"溢出伤害"（打 100、实际扣 10 时显示 100），改成传 `remainingDamage` 即可 |
| 连续战斗 3 分钟池对象稳定不增长 | `PrefabPool` 预生成 + `Release` 复位；`ReleaseAll` 无泄漏 | 需实机验证 |
| HUD 血/蓝条一致 | 事件驱动 | 极低 |
| **CD 遮罩与实际可释放时刻误差 ≤ 0.05s** | 每帧读 `GetCooldownRemaining`（60FPS 下单帧误差 ≤ 16.7ms） | 极低 |
| 死亡时头像置灰、技能不可点 | 置灰：`OnHeroDied`；"不可点"由 `SkillComponent.TryCast` 自身的 `IsDead` 校验保证 | 极低 |
| 小地图映射误差 ≤ 1 标记宽、死亡 1 帧内移除 | 世界包围盒归一化 + `EntityRegistry` 注销事件 | **取决于 D6** |
| 每次击杀恰好 1 条播报、10 次不重叠不丢 | `MatchStatsTracker` 唯一计数 + 5 槽位 FIFO | 极低 |
| 计分板与统计完全一致 | `ScoreboardView` 只读 `MatchStatsTracker` | 结构性保证 |
| **复活耗时与配置一致（≤0.1s）、满血满蓝、在出生点、可移动施法** | `RespawnRoutine` + `Revive()` 的 11 步逆操作 | **中**：`NavMeshAgent` 启用顺序与 `Warp` 后 `isOnNavMesh` 校验是唯一易错点 |
| 对局结束后不再复活 | `MatchController.IsMatchOver` 早退 + `StopCoroutine` | 极低 |
| 同屏 30 单位 + UI 全开 ≥ 60FPS、UI 稳态 GC ≈ 0 | 见 §10 | 需实机 Profiler |

---

## 10. 性能预算与已知风险

| 项 | 预算 | 说明 |
|---|---|---|
| HUD draw call | ~1（Screen Space Overlay 一个 Canvas，全部同图集） | 需要同一张 Sprite 才能合批 → 占位阶段天然满足 |
| 血条 draw call | **= 血条数量**（24 个 World Space Canvas = 24 次） | World Space Canvas **无法跨 Canvas 合批**。30 单位下 24~30 次 draw call 在 60FPS 预算内完全可接受 |
| 飘字 draw call | 1（共享画布） | 见 §3.2 |
| UI 稳态 GC Alloc | **0 B/帧** | 关键点：CD 秒数只在整秒变化时写 Text；播报文本只在内容变化时写；不用 `foreach`/LINQ/闭包；不用 `LayoutGroup` |
| 事件固有分配 | 每次伤害 1 个短字符串（`damage.ToString`） | 与阶段六弹道 `Instantiate` 同级的"每次事件固有分配"，非稳态泄漏 |
| 已知风险 1 | World Space Canvas 数量 = draw call 数 | 若实机超预算 → 切换"屏幕空间投影方案"（世界点 `WorldToScreenPoint` → 摆 HUD 层 `Image`），**对外契约（`Bind`/事件订阅）完全不变**，只换 `WorldHealthBarView` 内部实现 |
| 已知风险 2 | World Space UI 被场景几何遮挡（`ZTest LEqual`） | 阶段八用"UI 专用 Layer + 第二相机"解决；本轮登记为已知项 |
| 已知风险 3 | 阶段六遗留：`TargetingComponent.FindNearestEnemy` 用 `OverlapSphere`（每次分配数组） | 与本阶段无关，但会让"UI 稳态 GC ≈ 0"的实测被它掩盖 → 建议顺手换成 `OverlapSphereNonAlloc`（阶段六已把同族写法用在 `AreaEffectZone`，有现成范式） |

---

## 11. 待裁决清单

| # | 裁决项 | 我的建议 | 影响面 |
|---|---|---|---|
| **D1** | 是否把 `com.unity.ugui` 加进 `Packages/manifest.json` | **必须加**（内置包、离线可解析）。不加则 HUD / CD 遮罩 / 血条全部写不出来 | 阻塞项 |
| **D2** | 飘字技术路线：共享世界空间画布 vs 每飘字一个 Canvas | **共享画布**（1 draw call / 1 次 billboard） | 中 |
| **D3** | 击杀统计口径：只统计英雄 vs 含小兵 | **只统计英雄**（否则播报刷屏、计分板语义变成补刀数） | 小 |
| **D4** | 复活编排归属：`MatchController` 编排 vs `HeroController` 自持倒计时 | **`MatchController` 编排**（配置与对局状态都在它手上） | 中 |
| **D5** | `MatchResultView`（现 `OnGUI`）是否本轮迁到 uGUI | **本轮不迁**，双栈并存；阶段八统一。迁它会牵动已验收的阶段四代码 | 小 |
| **D6** | 小地图是否本轮纳入（计划书 §7 有，你的指令未列） | 建议**纳入但排在实施顺序最后**；若想控范围，可顺延到 7.5 之后单独做 | 中 |
| **D7** | `AutoSceneBuilder` 是否拆 `partial`（`AutoSceneBuilder.UIAssembly.cs`） | **拆**（原文件已 2993 行） | 极小 |
| **D8** | 血条是否叠一条护盾覆盖层 | **叠**（阶段六护盾已落地，不显示会让"技能放了没效果"） | 小 |
| **D9** | `OnDamaged` 的伤害数值口径：实际扣减量（打 100 扣 10 → 显示 10）vs 本次伤害量（显示 100） | **实际扣减量**（与验收标准"数值与实际扣血量一致"字面吻合）；想显示溢出伤害则传 `remainingDamage` | 小（一行） |
| **D10** | `EntityRegistry` 增加注册/注销静态事件 vs 三个消费方各自低频轮询 `Snapshot()` | **加事件**（避免 3 份重复簿记；它不是"全局事件总线"，只广播登记/注销一件事） | 小 |

---

## 12. 若批准：实施顺序（每步都可独立验证）

| 步 | 内容 | 验证方式 |
|---|---|---|
| 7.0 | `manifest.json` 加 `com.unity.ugui`（D1） | 编辑器编译通过、`UnityEngine.UI` 可解析 |
| 7.1 | 逻辑增量：`OnDamaged` / `Revive` / `RestoreFull` / `EntityRegistry` 事件 / `respawnTime` | 静态检查 + 现有 `Stage6AutoTester` 仍全绿 |
| 7.2 | `PrefabPool<T>` + 血条（世界空间 + billboard + 池） | 实机：血条跟随、朝向、满血隐藏、滚轮缩放时像素高度不变 |
| 7.3 | 飘字（共享画布 + 池 + 上浮淡出） | 实机：连续战斗 3 分钟池对象数稳定 |
| 7.4 | HUD（血/蓝条 + Q/W 技能面板 + CD 遮罩） | 实机：CD 遮罩与真实可释放时刻对齐 |
| 7.5 | `MatchStatsTracker` + 播报 + 计分板 | 实机：10 次击杀播报不重叠不丢、计数一致 |
| 7.6 | 复活机制 + 复活遮罩 | 实机：死亡 → 倒计时 → 满血满蓝回到出生点 → 可移动可施法 |
| 7.7 | 工具步骤 17 + `ValidateUISetup()` | 全新克隆 → 一次菜单 → 直接 Play，全程零拖拽 |
| 7.8 | 静态检查（`check_unity_cs.py --fix-bom`、`test_check_unity_cs.py`、`check_missing_usings.py`）+ 实机验收 | 逐条对照 §9 |

---

## 13. 本草案遵守的既有铁律（自检）

| 铁律 | 本草案的落实 |
|---|---|
| 表现与逻辑分离（最高优先级） | 逻辑层零 UI 引用；UI 全部只订阅事件；血条/飘字**不参与任何数值写入**；禁用全部 UI 后对局结果不变 |
| 场景装配全自动化 | 步骤 17 生成全部 Canvas/血条/HUD 并注入全部 `[SerializeField]`；**单位预制体零改动** |
| 配置只读 | `respawnTime` 只读；`SkillData.Icon` 只补空不覆盖 |
| 单一职责 | Manager（编排）/ View（显示）/ Tracker（计数）三权分立，无上帝类 |
| 依赖自校验 | 新组件全部"延迟解析 + 首次使用 `LogError`"（不适用 `Awake` 硬校验）；`ValidateUISetup()` 给确定性结论 |
| 静态检查前置 | 7.8 步强制走三个脚本；新增 `.cs` 一律 UTF-8 with BOM |
| 对象池不提前泛化 | 池只覆盖 UI 元素（+ 阶段八弹道/特效），不泛化到实体，不引入全局事件总线 |
