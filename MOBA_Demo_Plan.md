# 《MOBA Demo 迭代计划书》

> 本文档承载**实施细节与阶段计划**，需求权威以 `README.md` 为准。两者冲突时以 `README.md` 为准。
> 交付目标：**带有美术表现、核心机制完整、真正可玩的「简易版英雄联盟」（MOBA Vertical Slice）**。
> 阶段一 ~ 阶段七**已完成并封版**（阶段七实机验收通过）；**准备进入阶段八（美术表现与表现层分离）**。

---

## 1. 项目目标与架构约束

开发一个单机 Unity 3D 顶视角 MOBA Demo，最终验证并交付完整玩法闭环：

> 玩家右键移动/普攻 → 释放指向性 / 非指向性技能 → 双方规律出兵 → 小兵沿兵线推进交战
> → 防御塔按仇恨优先级还击 → 推掉防御塔 → 摧毁敌方基地 → 判定胜负并展示对局信息。

### 核心技术约束

- 使用 Unity 原生 `MonoBehaviour` **组件化架构（ECS 风格）**，不使用纯 DOTS / ECS。
- 每个脚本只承担一个明确职责，避免"上帝类"。
- 静态数值通过 `ScriptableObject` 配置，运行时状态保存在组件实例中，**绝不回写资产**。
- AI 使用基础有限状态机（FSM）；**可移动单位分两条互斥路线**：AI 单位走 FSM，玩家英雄走玩家指令层。
- 移动与追踪使用 Unity 内置 `NavMeshAgent`。
- **表现与逻辑分离（最高优先级铁律）**：逻辑层单向广播事件，表现层只读订阅；**动画与特效绝不能反向影响伤害判定**。
- 仅实现单机逻辑，不设计网络同步、预测、回滚或服务器权威。
- **所有场景装配必须通过 Editor 脚本自动化完成，严禁手动拖拽依赖。**

### 关于"表现与逻辑分离"的边界说明（重要）

| 允许 | 禁止 |
|---|---|
| 逻辑层广播 `OnAttackPerformed` / `OnDamaged` / `OnDied` / `OnSpellCast` 事件 | 逻辑层直接引用 `Animator` / `ParticleSystem` / `AudioSource` / UI 组件 |
| 表现层订阅事件播放动画、特效、音效、飘字 | 用 **Animation Event** 回调伤害结算或修改战斗数值 |
| 动画播放速度按 `AttackInterval` 做视觉适配 | 让逻辑层**等待**动画播完才结算（逻辑节奏与动画长度解耦） |
| 表现层整体关闭 | 关闭表现后对局结果发生变化 |

> 说明：早期草案曾采用"动画事件驱动伤害"，该方案**已废弃**——它会让伤害判定依赖表现层，违反单向数据流。伤害判定的唯一权威是 `CombatComponent`（距离 + CD + 目标合法性）与 `SkillComponent`（CD + 蓝耗 + 施法距离 + 目标合法性）。

---

## 2. 核心目录结构（目标态）

```text
Assets/
├── Art/
│   ├── Models/                     # 🆕 角色与建筑模型（Heroes / Minions / Towers / Bases）
│   ├── Materials/                  # ✅ 地面、角色、阵营材质
│   ├── Animations/                 # 🆕 动画片段与 AnimatorController
│   ├── VFX/                        # 🆕 攻击、受击、技能特效预制体
│   └── Textures/                   # 🆕 地面贴图与 UI 贴图
├── Audio/
│   ├── Music/
│   └── SFX/
├── Scenes/
│   ├── MainScene.scene             # ✅ 主玩法场景（.scene）
│   └── Test/
├── Scripts/
│   ├── Core/
│   │   ├── EntityBase.cs           # ✅ 实体根对象与组件引用入口
│   │   ├── EntityRegistry.cs       # ✅ 实体登记（战场冻结用）
│   │   ├── TeamType.cs             # ✅ 阵营枚举
│   │   ├── EntityType.cs           # ✅ 英雄、小兵、塔、基地类型
│   │   └── Interfaces/
│   │       ├── IDamageable.cs      # ✅（阶段六新增 source 重载，供击杀归属）
│   │       ├── ITargetable.cs      # ✅
│   │       └── （不新建 IStatusReceiver）  # 阶段六裁定：护盾落 HealthComponent、减速/眩晕落 BuffComponent，
│   │                                       # 均为具体组件的公开方法调用；无消费方的接口 = 死代码
│   ├── Components/
│   │   ├── HealthComponent.cs      # ✅ 生命值、伤害、治疗、死亡判定
│   │   ├── ManaComponent.cs        # 🆕 法力值与消耗（阶段六）
│   │   ├── MovementComponent.cs    # ✅ NavMesh 移动、停止、到达判断
│   │   ├── CombatComponent.cs      # ✅ 攻击距离、冷却、伤害结算
│   │   ├── TargetingComponent.cs   # ✅ 目标合法性、搜索与切换
│   │   ├── SkillComponent.cs       # 🆕 技能释放唯一入口（阶段六）
│   │   ├── BuffComponent.cs        # 🆕 状态效果容器（阶段六）
│   │   └── AnimationComponent.cs   # 🆕 逻辑状态 → Animator 参数（阶段八）
│   ├── Controllers/
│   │   ├── PlayerCommandController.cs  # ✅ 右键移动/攻击指令
│   │   ├── PlayerSkillController.cs    # 🆕 技能按键输入（阶段六）
│   │   └── CameraController.cs         # ✅ 相机跟随、平移、缩放
│   ├── AI/
│   │   ├── FSM/{IState.cs, StateMachine.cs}   # ✅
│   │   ├── EntityAIController.cs              # ✅
│   │   └── States/{Idle,Move,Chase,Attack,Dead}State.cs   # ✅
│   ├── Units/
│   │   ├── HeroController.cs       # ✅ 英雄（无 FSM，走玩家指令层；含复活）
│   │   ├── MinionController.cs     # ✅
│   │   ├── TowerController.cs      # ✅ 防御塔（含仇恨优先级）
│   │   └── BaseCoreController.cs   # ✅
│   ├── Skills/                     # 🆕 技能数据与效果（阶段六）
│   │   ├── SkillSlot.cs            # Q / W 槽位枚举
│   │   ├── SkillTargetType.cs      # 敌方单位 / 友方单位 / 地面点 / 方向
│   │   ├── SkillEffectType.cs      # 伤害 / 护盾 / 减速 / 眩晕
│   │   ├── SkillData.cs            # ScriptableObject 技能配置
│   │   ├── SkillTargetSelector.cs  # 目标选择器（范围敌方 / 指定目标）
│   │   └── SkillEffectResolver.cs  # 效果结算（伤害 / 护盾 / 状态）
│   ├── Gameplay/
│   │   ├── LanePath.cs             # ✅
│   │   ├── MinionSpawner.cs        # ✅ 出生点直接持有 Transform，不引入 SpawnPoint 标记组件
│   │   ├── MatchController.cs      # ✅
│   │   └── MatchStatsTracker.cs    # 🆕 击杀/死亡统计与播报事件（阶段七）
│   ├── UI/
│   │   ├── MatchResultView.cs      # ✅
│   │   ├── WorldHealthBarView.cs   # 🆕 头顶世界空间血条（阶段七）
│   │   ├── DamagePopupView.cs      # 🆕 伤害飘字（阶段七）
│   │   ├── HeroHUDView.cs          # 🆕 头像、血/蓝条、技能 CD（阶段七）
│   │   ├── SkillSlotView.cs        # 🆕 单个技能图标 + CD 遮罩（阶段七）
│   │   ├── MinimapView.cs          # 🆕 小地图映射（阶段七）
│   │   ├── KillFeedView.cs         # 🆕 击杀播报（阶段七）
│   │   ├── ScoreboardView.cs       # 🆕 计分板（阶段七）
│   │   └── RespawnOverlayView.cs   # 🆕 复活倒计时遮罩（阶段七）
│   ├── VFX/                        # 🆕 表现层（阶段八）
│   │   ├── Projectile.cs           # 弹道（飞行时间 + 命中回调）
│   │   ├── VfxSpawner.cs           # 事件 → 特效/音效播放
│   │   └── SimpleObjectPool.cs     # 最小可用对象池
│   ├── Debug/
│   │   ├── FSMDebugView.cs         # ✅
│   │   ├── MatchDebugView.cs       # ✅
│   │   └── TowerAggroDebugView.cs  # 🆕 塔仇恨可视化（阶段六 6B，本轮不做）
│   └── Editor/
│       └── AutoSceneBuilder.cs     # ✅ 一键组装（需持续升级，见 §6 铁律）
├── ScriptableObjects/
│   ├── EntityStats/                # ✅
│   ├── Attacks/                    # ✅
│   ├── Skills/                     # 🆕 Q / W 技能配置
│   ├── Spawns/                     # ✅
│   └── Match/                      # ✅
├── Prefabs/
│   ├── Characters/                 # 🆕 Heroes / Minions
│   ├── Buildings/                  # 🆕 Towers / Bases
│   ├── UI/                         # 🆕 血条、飘字、HUD、小地图
│   └── VFX/                        # 🆕 弹道、受击、技能特效
└── Settings/
    ├── Input/
    └── NavMesh/
```

---

## 3. 核心类图设计

```mermaid
classDiagram
    class EntityBase {
        +EntityStatsData StatsData
        +TeamType Team
        +EntityType EntityType
        +HealthComponent Health
        +MovementComponent Movement
        +CombatComponent Combat
        +TargetingComponent Targeting
        +SkillComponent Skills
        +BuffComponent Buffs
        +AnimationComponent Animation
        +Initialize()
    }

    class HealthComponent {
        -float currentHealth
        +TakeDamage(float)
        +Heal(float)
        +OnHealthChanged
        +OnDamaged
        +OnDied
    }

    class ManaComponent {
        -float currentMana
        +TrySpend(float) bool
        +OnManaChanged
    }

    class MovementComponent {
        -NavMeshAgent agent
        +MoveTo(Vector3) bool
        +Stop()
        +SetMoveSpeed(float)
        +HasReachedDestination()
    }

    class CombatComponent {
        -float nextAttackTime
        +TryAttack(ITargetable)
        +OnAttackPerformed
    }

    class TargetingComponent {
        +ITargetable CurrentTarget
        +SetTarget(ITargetable)
        +FindNearestEnemy()
        +ClearInvalidTarget()
    }

    class SkillComponent {
        -SkillData qSkill
        -SkillData wSkill
        +TryCast(SkillSlot, Vector3, ITargetable) bool
        +GetCooldownRemaining(SkillSlot) float
        +OnSpellCast
    }

    class BuffComponent {
        -List~ActiveBuff~ active
        +Apply(BuffData, float)
        +Remove(BuffType)
        +Has(BuffType) bool
    }

    class SkillData {
        <<ScriptableObject>>
        +SkillSlot Slot
        +float Cooldown
        +float ManaCost
        +float CastRange
        +float Radius
        +float Damage
        +SkillTargetType TargetType
        +SkillEffectType EffectType
        +float Duration
    }

    class AnimationComponent {
        -Animator animator
        +SetMoveSpeed(float)
        +PlayAttack()
        +PlaySpell()
        +PlayDeath()
    }

    class TowerController {
        -ITargetable currentAggro
        +EvaluateAggro()
        +NotifyHeroAttackedAlly(EntityBase)
    }

    class PlayerCommandController {
        +IssueMove(Vector3)
        +IssueAttack(ITargetable)
    }

    class MatchStatsTracker {
        +int blueKills
        +int redKills
        +OnKillLogged
    }

    class EntityAIController {
        -StateMachine stateMachine
        +InitializeAI(LanePath)
        +Tick()
    }

    EntityBase *-- HealthComponent
    EntityBase *-- ManaComponent
    EntityBase *-- MovementComponent
    EntityBase *-- CombatComponent
    EntityBase *-- TargetingComponent
    EntityBase *-- SkillComponent
    EntityBase *-- BuffComponent
    EntityBase *-- AnimationComponent
    EntityBase --> EntityStatsData
    SkillComponent --> SkillData
    SkillComponent --> ManaComponent
    SkillComponent --> TargetingComponent
    EntityAIController --> EntityBase
    EntityAIController *-- StateMachine
    MovementComponent --> NavMeshAgent
    TowerController --> TargetingComponent
    PlayerCommandController --> MovementComponent
    PlayerCommandController --> TargetingComponent
    MatchStatsTracker --> MatchController
```

### 3.1 基类与接口

| 类型 | 职责 |
|---|---|
| `EntityBase` | 统一实体身份、阵营、配置数据及核心组件引用；只负责初始化与聚合。 |
| `IDamageable` | 暴露受伤入口与存活状态，使攻击逻辑不依赖具体单位类型。 |
| `ITargetable` | 暴露目标位置、阵营和可选中状态。 |
| `IStatusReceiver` | 暴露状态效果接收入口（减速/眩晕/护盾），供 Buff 系统统一施加。 |
| `TeamType` | 定义玩家方、敌方和中立阵营，作为目标合法性判断依据。 |
| `EntityType` | 区分英雄 / 小兵 / 防御塔 / 基地，用于依赖校验与塔的仇恨优先级。 |

### 3.2 核心组件

| 组件 | 单一职责 | 主要依赖 |
|---|---|---|
| `HealthComponent` | 管理当前生命、伤害、治疗和死亡事件。 | `EntityStatsData` |
| `ManaComponent` | 管理当前法力、消耗与不足判定。 | `EntityStatsData` |
| `MovementComponent` | 封装 `NavMeshAgent`，处理目标点移动、停止与到达判断。 | `NavMeshAgent` |
| `CombatComponent` | 管理攻击距离、攻击冷却与单次伤害结算（**伤害判定权威**）。 | `AttackData`、`IDamageable` |
| `TargetingComponent` | 搜索敌人、保存目标、验证目标是否有效（**攻击指令的唯一存放处**）。 | `TeamType`、物理查询 |
| `SkillComponent` | 技能施放唯一入口：校验 CD / 蓝量 / 距离 / 目标合法性，扣蓝、起 CD、执行效果、广播事件。 | `SkillData`、`ManaComponent`、`TargetingComponent` |
| `BuffComponent` | 状态效果容器：施加、刷新、到期解除、查询（减速/眩晕/护盾）。 | `IStatusReceiver` |
| `AnimationComponent` | 将逻辑状态转换为 Animator 参数（**唯一允许引用 Animator 的组件**）。 | `Animator` |
| `EntityAIController` | 收集环境条件并驱动状态机，不直接实现状态行为。 | `StateMachine` |
| `TowerController` | 固定位置索敌 + 仇恨优先级评估 + 攻击；不持有移动组件。 | `CombatComponent`、`TargetingComponent` |

### 3.3 数据配置类

| 数据类 | 建议字段 |
|---|---|
| `EntityStatsData` | 最大生命、最大法力、移动速度、索敌距离、旋转速度、攻击配置引用。 |
| `AttackData` | 基础伤害、攻击距离、攻击间隔、攻击前摇、命中方式（即时 / 弹道）。 |
| `SkillData` | 槽位、图标、冷却、法力消耗、施法距离、作用半径、伤害/护盾值、效果类型、目标类型、持续时间。 |
| `BuffData` | 状态类型（减速/眩晕/护盾）、数值、持续时间、是否可叠加、刷新策略。 |
| `MinionSpawnData` | 每波数量、生成间隔、波次间隔、单位预制体。 |
| `MatchConfigData` | 开局延迟、复活时间、胜负目标、默认阵营配置。 |

配置数据必须视为只读模板。`currentHealth`、`currentMana`、攻击冷却和当前目标等运行时变量**不得写回 `ScriptableObject`**。

### 3.4 FSM 状态职责

- `IdleState`：无目标时等待或检查下一路径点。
- `MoveState`：沿路线或命令位置移动，并周期性检查敌人。
- `ChaseState`：追踪有效目标；目标失效或超出追击/牵引范围时退出。
- `AttackState`：进入攻击距离后停止移动，并按冷却触发攻击。
- `DeadState`：停止寻路和战斗，禁用选中与碰撞，播放死亡表现。
- 状态只编排组件能力（例如调用 `MoveTo()`、`TryAttack()`）；不得自行修改生命值或直接操作 `NavMeshAgent`。

---

## 4. 关键运行流程

1. `EntityBase.Initialize()` 读取 `EntityStatsData`，初始化各功能组件（**Combat 必须先于 Targeting**）。
2. 玩家右键地面 → `PlayerCommandController` 调用 `MovementComponent.MoveTo()`；右键敌方单位 → 写入 `TargetingComponent.CurrentTarget`。
3. 指令执行器每帧 `ClearInvalidTarget()` → 射程内 `Stop() + TryAttack()`，射程外按 `chaseRepathDistance` 追击。
4. 玩家按 Q/W → `PlayerSkillController` → `SkillComponent.TryCast()` 校验 CD / 蓝量 / 距离 / 目标合法性 → 扣蓝、起 CD、执行效果、广播 `OnSpellCast`。
5. 若技能/普攻命中方式为**弹道**，由 `Projectile` 飞行至目标后**在命中时**回调结算；逻辑层不等待表现。
6. AI 单位由 FSM 根据距离在 `ChaseState` 与 `AttackState` 之间切换。
7. `HealthComponent` 生命归零后发布 `OnDied`：单位控制器执行死亡处理；`MatchStatsTracker` 记录击杀并广播播报事件。
8. 英雄死亡 → 进入复活倒计时（UI 显示）→ 在己方出生点重生并恢复满状态。
9. 基地死亡后通知 `MatchController`，判定胜负、冻结战场并显示对局结果。
10. 防御塔每帧：`ClearInvalidTarget` → 超交战半径 `ClearTarget` → 节流评估仇恨优先级 → `TryAttack`。
11. 全程表现层（血条、飘字、特效、音效、小地图）只订阅事件，**不反向写入任何战斗数值**。

---

## 5. 分步开发里程碑

### 阶段一：场景、数据配置与玩家移动 ✅ 已完成

**输入**

- 一个可烘焙 NavMesh 的测试地图。
- 玩家占位模型和地面碰撞体。
- `EntityStatsData` 基础移动配置。
- Unity Input System 或旧输入系统中的鼠标点击输入。

**开发范围**

- 建立 `EntityBase`、`MovementComponent` 和 `PlayerCommandController`。
- 完成顶视角相机跟随、平移和缩放。
- 通过鼠标点击地面发送移动命令。
- 提供不可达位置和 NavMesh 外点击的安全处理。

**验收标准**

- 玩家可连续点击不同位置，角色能稳定更新目的地。
- 角色绕过至少一个静态障碍物到达目标点。
- 点击 NavMesh 外区域不会报错或导致角色失控。
- 调整 `EntityStatsData.MoveSpeed` 后，无需修改脚本即可改变移动速度。

---

### 阶段二：生命、目标选择与基础战斗 ✅ 已完成

**输入**

- 阶段一可移动角色。
- 一个敌方静态测试单位。
- `EntityStatsData` 和 `AttackData` 配置。
- 简易生命条预制体。

**开发范围**

- 实现 `HealthComponent`、`TargetingComponent` 和 `CombatComponent`。
- 支持选择敌方目标、追至攻击距离、按冷却执行普通攻击。
- 通过事件刷新生命条并处理死亡。
- 增加目标阵营、死亡状态和攻击距离校验。

**验收标准**

- 玩家不能攻击友方、已死亡或已销毁目标。
- 目标在攻击范围外时角色追击，进入范围后停止并攻击。
- 攻击间隔与伤害数值符合 `AttackData` 配置。
- 目标生命归零后只触发一次死亡事件，攻击与寻路立即停止。
- 更换配置资产后，可生成不同生命值和攻击力的单位。

---

### 阶段三：小兵 FSM 与自动交战 ✅ 已完成

**输入**

- 阶段二完整战斗组件。
- 小兵预制体。
- 至少包含起点、中间点和终点的 `LanePath`。
- 蓝红双方测试单位。

**开发范围**

- 建立 `IState`、`StateMachine` 及五个基础状态。
- 小兵默认沿路线推进，在索敌范围内发现敌人后追击并攻击。
- 目标死亡或超出最大追击范围后，小兵返回路线继续推进。
- 增加 FSM 当前状态与目标的调试显示。

**验收标准**

- 无敌人时，小兵能够依次经过所有路线节点。
- 敌人进入索敌范围后，小兵在一个检测周期内进入追击状态。
- 接近目标后能从追击切换为攻击，目标死亡后恢复推进。
- 被引离路线超过限制时，小兵会放弃目标并返回路线。
- 连续生成至少 10 个小兵运行 3 分钟，无空引用异常或明显状态卡死。

---

### 阶段四：防御塔、兵线生成与胜负闭环 ✅ 已完成

**输入**

- 阶段三的小兵 AI。
- 双方防御塔、基地和出生点预制体。
- `MinionSpawnData` 与 `MatchConfigData`。
- 简易对局结果 UI。

**开发范围**

- 实现 `MinionSpawner`，按配置定时生成双方兵线。
- 防御塔使用固定位置索敌与攻击逻辑，不使用移动组件。
- 基地实现生命值与死亡事件。
- `MatchController` 管理开局、运行、结束三个对局阶段。
- 补充攻击、受击、死亡动画或占位特效。

**验收标准**

- 双方能够按固定间隔自动生成小兵并沿相反方向推进。
- 防御塔只攻击有效敌方目标，目标离开范围或死亡后自动切换。
- 小兵、防御塔和基地复用同一套生命与伤害接口。
- 任一基地生命归零后停止生成单位、停止战斗逻辑并显示胜负结果。
- 从进入场景到对局结束可完整运行至少 5 次，无阻断流程的异常。

---

### 阶段五：英雄控制与自动化基建 ✅ 已完成

> **核心**：基于 Raycast 的右键移动/攻击指令、主相机平滑跟随 + 中键平移 + 滚轮缩放、`AutoSceneBuilder` 一键场景组装（含地面放大与 NavMesh 烘焙）与依赖注入。

**输入**

- 阶段一 ~ 四的完整战场（白盒英雄 + 兵线 + 塔 + 基地）。
- `PlayerCommandController`、`CameraController`、`AutoSceneBuilder`。
- 旧输入系统（`Input.GetMouseButtonDown(1)`）与 Layer `"Ground"` 射线拾取。
- 实机验证前置条件（已由工具自动满足）：地面覆盖战场范围 + 已烘焙 NavMesh。

**交付结果**

- `HeroController`：英雄身份（显示名 / 阵营 / 类型）+ 只读数据（生命 / 移速 / 攻击距离 / 存活）+ 英雄专属死亡收尾。**英雄刻意不挂 `EntityAIController`** —— FSM 会自主索敌并直接写 `MovementComponent` / `CombatComponent`，与玩家指令层争夺同一对组件的写入权（表现为「刚下达的移动令被 AI 改写」）。可移动单位因此分两条**互斥**路线：AI 单位走 FSM，玩家英雄走玩家指令层。英雄没有 `DeadState`，「死亡后原地静止、不再响应输入」由 `HandleDied` 承担。
- `PlayerCommandController`：右键 `Raycast`（`RaycastNonAlloc` 复用缓冲）同时获取敌方单位命中与地面命中，比较命中距离决定**攻击 / 移动**；点自己与友方忽略；点已死亡 / 已销毁单位被 `TargetingComponent.SetTarget` 的合法性校验拦下且不写入指令。攻击指令唯一存放于 `TargetingComponent.CurrentTarget`（全项目「当前目标」只有这一个存放处）。
- 指令执行器：每帧 `ClearInvalidTarget()` → 射程内 `Stop() + TryAttack()` / 射程外按 `chaseRepathDistance` 追击；**玩家指令不设牵引极限**（指令语义就是「打这个」）。`MovementComponent` 判空已提前到射程分支之前，无移动组件的单位不会每帧空引用。
- `CameraController`：固定世界偏移（默认 `(0, 10, -10)`，约 45° 俯角）+ `SmoothDamp`，在 `LateUpdate` 采样（Update 之后才是本帧最终位置），不跟随旋转。**平移与缩放是显示层增量**（`panOffset` / `zoomScale`，均不写进 `offset` 字段）：期望位置 = `目标 + offset × zoomScale + panOffset`，**注视点同步跟随 `panOffset`** —— 否则相机一边横移一边盯着英雄，平移会退化为「绕英雄转圈」，玩家永远推不开视野。
- `AutoSceneBuilder`：一键组装兵线、双方基地与出兵点、`MatchController`、**地面覆盖与 NavMesh 烘焙**、英雄（资产 / 材质 / 预制体 / 实例 / 相机跟随），并做缺口修补与收尾校验；**依赖注入零手工**（全部走 `SerializedObject` + `SerializedProperty`）。
- 步骤 8「地面覆盖、导航烘焙与手工残留自愈」：只放大不缩小 / 只动内置 Plane 网格 / 保持正方形；自动移除 Ground 上手工残留的 `NavMeshSurface` 组件；用现代 API `NavMeshBuilder.CollectSources`（配 `NavMeshBuildMarkup`）显式收集几何 + `BuildNavMeshData` 烘焙，再以 `EditorUtility.CopySerialized` **原地更新**场景的 NavMeshData 资产（保住 guid，场景引用不断链）→ `NavMesh.CalculateTriangulation()` 验证。**不再使用已弃用的 `StaticEditorFlags.NavigationStatic`（CS0618 已消除）**。
- 步骤 1 附加「幽灵对象清理」：名字命中接管清单、却不是场景根对象的游离副本（历史遗留：默认名空对象下挂着的 `BlueBase` / `RedBase` 副本，带残余 `EntityBase` 等组件）会被清掉，避免污染 `EntityRegistry`；判定规则收窄为「名字命中 + 有父节点」，清完即自限。

**本阶段的三条设计裁决（封版口径，不再复议）**

1. **`SpawnPoint.cs` 设计剔除**：`MinionSpawner` 直接持有出生点 `Transform` 即可完成落点绑定，再引入一个没有任何消费方的标记组件属于死代码，违反「单一职责 / 不建无用抽象」。**本阶段不实现该类**，`Assets/Scripts/Gameplay/` 只保留 `LanePath` / `MinionSpawner` / `MatchController`。
2. **相机平移只做中键拖拽**：**不做屏幕边缘平移** —— 边缘平移需要额外的边界阈值、鼠标离开窗口与全屏 / 窗口模式的差异化处理，收益不抵复杂度。中键双击回中作为唯一的视角复位入口。**以此为最终标准。**
3. **导航烘焙的唯一入口是工具，场景里不允许存在手工 `NavMeshSurface`**：手工挂的 `NavMeshSurface`（带 `[ExecuteAlways]`）会在编辑器里**再注册一份** navmesh 数据，且它引用的资产看起来「没人用」，极易被误删。工具在步骤 8 自愈式清理这类组件，并用现代 API 原地更新场景的 NavMeshData 资产。**推论（踩坑教训）**：任何**按 guid 被场景引用**的资产都不能用 `AssetDatabase.CreateAsset` 覆盖重建 —— 它会先删除旧资产、guid 随之改变、场景引用直接断链；必须**原地更新内容**（`EditorUtility.CopySerialized`）。

**开发范围（正式定义）**

- **英雄指令层**：Raycast 拾取 → 攻击/移动判定 → 指令执行器；支持连续下达与指令打断。
- **主相机**：平滑跟随 + 中键拖拽平移（双击回中）+ 滚轮缩放；`LateUpdate` 采样；编辑模式可即时摆位。
- **自动化基建**：`AutoSceneBuilder` 完成"清理旧场景 → 生成兵线/建筑/地面与 NavMesh/英雄/相机 → 注入全部 `[SerializeField]` 依赖 → 收尾校验"的全链路，**幂等且零手工拖拽**。
- 纯白盒验证：**本阶段不引入任何美术资产、动画或特效**。

**验收结果**

| 验收项 | 结论 | 依据 |
|---|---|---|
| 连续发出 20 次移动/攻击指令（含 NavMesh 外、点自己、点友方、点已死亡单位），零异常、零失控 | ✅ 闭环 | NavMesh 外 → `NavMesh.SamplePosition` 失败即忽略并告警；点自己 / 友方 → 跳过该命中并继续看它身后的地面；点已死亡单位 → `IsValidTarget` 校验拦下，指令不生效 |
| 点击敌方单位后英雄自动追至射程并持续平A；目标死亡后停止攻击且不残留失效指令 | ✅ 闭环 | 追击按 `chaseRepathDistance` 重寻路（含 `wasAttackingInPlace` 无条件重下，防「停下输出后卡死」）；射程内 `Stop() + TryAttack()`，冷却由 `CombatComponent` 判定；目标死亡 → `ClearInvalidTarget()` 清空 `CurrentTarget`，指令自然作废 |
| 相机跟随无抖动；平移与缩放不改变跟随目标与跟随偏移；连续平移/缩放 30 次无漂移 | ✅ 闭环 | `SmoothDamp` 在 `LateUpdate` 收敛；`target` / `offset` 字段无任何写入路径；平移夹在 `maxPanDistance` 内、缩放夹在 `[minZoomScale, maxZoomScale]` 内，无累积漂移 |
| 英雄在已烘焙 NavMesh 上无卡死；`MoveTo` 失败有一次告警且能自愈重试（不出现"永久卡死"） | ✅ 闭环 | `MoveTo` 返回 bool，失败不置「已下令」标记 → 下一帧自然重试；「代理不在 NavMesh 上」「目标点不可达」各只告警一次（`hasReportedXxx`） |
| **`AutoSceneBuilder` 幂等性**：连续执行 3 次，场景结构与依赖注入结果完全一致，无重复对象、无丢失引用 | ✅ 闭环 | 只按固定名字清理自己生成的对象；资产「缺失才创建、不覆盖」；地面「只放大不缩小」，第二次执行即判定为已覆盖 |
| **零手工验证**：全新克隆仓库 → 打开 `MainScene` → 执行一次菜单 → 直接 Play，完成一次完整对局（出兵 → 交战 → 基地摧毁 → 结算），全程**未在 Inspector 拖拽任何引用** | ✅ **实机确认** | 地面放大与 NavMesh 烘焙已由工具步骤 8 自动完成；右键移动控制已实机验证通过 |
| Profiler：白盒稳态 GC Alloc ≤ 1 KB/帧，单帧逻辑耗时 < 2 ms | ⏳ 待采样 | 需实机 Profiler 采样确认。**已知风险点**：`TargetingComponent.FindNearestEnemy` 使用 `Physics.OverlapSphere`（每次调用分配数组），单位数量上升时应替换为 `OverlapSphereNonAlloc` |

> **封版说明**：本阶段已实机验证「右键移动控制」与「一键组装（含地面 / NavMesh 前置）」；其余验收项为代码级闭环确认（逐条对照实现路径核对）。Profiler 指标属量化采样项，留待阶段六开发前用 Profiler 补测。

---

### 阶段六：技能系统与战斗拓展（核心玩法闭环）✅ 6A 已完成（实机验收通过）/ 6B（塔仇恨）顺延

> **拆版说明**：本阶段按架构师裁决拆为 **6A（技能系统与战斗拓展）** 与 **6B（防御塔仇恨优先级）**。
> 6A 范围 = 下述 (1)~(5)，代码已交付且编译 / 资产注入 / 静态检查均已通过，**实机技能验收（`Stage6AutoTester`）待跑**；
> 6B 范围 = 下述 (6)，本轮【不做】。

> **核心**：引入完整的技能架构（`ScriptableObject` 驱动的 `SkillData`）。
> **机制**：指向性技能（弹道 Projectile）、非指向性 AOE 技能、基础 Buff/Debuff（减速、眩晕），以及技能 CD 与蓝耗管理。

**输入**

- 阶段五的英雄指令层与相机流。
- `TowerController`（已就位但尚未挂载到预制体/场景）。
- 技能数据结构设计（`SkillData` ScriptableObject）。
- 法力值组件 `ManaComponent`、状态效果容器 `BuffComponent`。

**开发范围**

**（1）技能系统架构**

- **数据层**：`SkillData`（槽位、图标、冷却、蓝耗、施法距离、作用半径、伤害/护盾值、效果类型、目标类型、持续时间）。
- **效果层**：`SkillEffectType`（伤害 / 护盾 / 减速 / 眩晕）+ 目标选择 + `SkillEffectResolver`（效果结算）。
  **实现期裁定（阶段六）**：不建名为 `SkillTargetSelector` 的独立类型 ——「目标选择」由三处各自承担：
  `SkillCastType`（`UnitTarget` / `GroundPoint`，决定这次施法需要一个什么目标）、
  `SkillComponent.TryValidateTarget`（指定敌方单位的合法性）、`AreaEffectZone` 的半径收集（范围内敌方）。
  功能全覆盖，只是不引入一个只做转发的中间层。
  **已知扩展点**：「指定友方单位」当前【未实现】—— `TryValidateTarget` 只接受敌方单位。
  README §2.1.2 把「对指定友方施加护盾」列为技能形态之一，但**不在首批 Q/W 范围内**；
  将来做友方护盾技能时需在此处扩展（`SkillEffectType.Shield` 与 `SkillEffectResolver.ApplyShield` 已就位，只差目标校验放行友方）。
- **执行层**：`SkillComponent.TryCast(slot, groundPoint, target, out failReason)` 唯一入口 —— 校验 CD → 蓝量 → 施法距离 → 目标合法性 → 扣蓝 → 起 CD → 执行效果 → 广播 `OnSpellCast`；被拒绝时通过 `failReason` 给出可读原因（含剩余冷却 / 所需与当前法力 / 实际距离与射程 / 目标阵营）。
- **输入层**：`PlayerSkillController` 采集 Q/W 按键与鼠标位置（非指向性技能需要方向/落点）。
- 首批技能：**Q = 指向性非穿透弹道**（锁定敌方单位 → 弹道命中结算伤害并销毁）；**W = 非指向性 AOE 减速圈**（指定落点 → 持续范围效果 → 周期减速）。

**（2）指向性技能与弹道**

- 锁定目标 → 发射 `Projectile`（真实飞行时间）→ **命中时**结算伤害与效果。
- 目标在弹道飞行途中死亡/销毁时，弹道须安全回收，不得空引用。

**（3）非指向性 AOE 技能**

- 以指定落点/方向为中心，`OverlapSphereNonAlloc` 收集范围内敌方单位并结算；友方与中立不受影响。
- 作用范围需要**白盒可视化**（Gizmos 或临时圆环）便于验证与调试。

**（4）Buff / Debuff**

- `BuffComponent`（状态类型：减速 / 眩晕 / 护盾、数值、持续时间、叠加与刷新策略）。
  **实现期裁定（阶段六）**：不单独建 `BuffData` 资产 —— V1 的 buff 参数（减速比例、减速时长、眩晕时长、护盾值）全部来自 `SkillData`，
  而当前**没有任何"独立于技能的 buff 来源"**（装备 / 光环 / 中立生物光环）。建出来就是无消费方的死代码，
  与 README §3.5「数据层 = `SkillData`」一致，也与本项目「无消费方的标记组件 = 死代码」的既有裁决同一标准
  （参见阶段五"不做 `SpawnPoint.cs`"）。将来出现非技能来源的 buff 时再抽 `BuffData`，
  届时 `BuffComponent.ApplySlow / ApplyStun / ApplyShield` 的签名无需改动。
- 减速：修改移动速度（只经 `MovementComponent.SetMoveSpeed()`，禁止直接改 `NavMeshAgent`）。
- 眩晕：禁止移动与攻击，到期自动解除。
- 护盾：优先吸收伤害，耗尽或到期移除。

**（5）CD 与蓝耗管理**

- `ManaComponent`：当前法力、消耗、不足判定，事件广播供 HUD 订阅。
- 技能冷却剩余时间可查询（供阶段七的 CD 遮罩读取）。

**（6）防御塔仇恨优先级**（⚠️ 已拆分为 **6B**，本阶段【不做】）

> **拆版裁定（架构师 D5）**：「本阶段绝对不碰防御塔仇恨，保持目标纯粹。」
> 本节内容连同下方验收标准里的「塔仇恨」条目一并顺延到 **6B** 单独实施。
> 6A（技能系统与战斗拓展）已按上述 (1)~(5) 封版；`TowerController` 仍处于"已就位、未挂载"状态。

- 默认优先级 **小兵 > 英雄**（同级取最近）；敌方英雄在塔范围内攻击己方英雄 → **仇恨转移到该英雄**；目标死亡/离场/非法 → 重选；重评估节流 ≤ 0.25 s；**同一时刻单目标**。
- 新增 `TowerAggroDebugView` 可视化当前仇恨目标与优先级判定结果。（随 6B 顺延）

**验收标准**

- **技能拒绝路径**：CD 中 / 蓝量不足 / 超出施法距离 / 目标非法（友方、已死亡、已销毁）时，释放被拒绝且**不产生任何副作用**（不扣蓝、不起 CD、不播动画、不生成特效）。
- **拒绝原因可观测**：上述每一条拒绝路径都必须通过 `TryCast` 的 `failReason` 出参给出**可读且带量化信息**的原因（剩余冷却秒数 / 所需与当前法力 / 实际距离与射程 / 目标阵营 / 前摇硬直剩余时间），并由 `PlayerSkillController` 打进 Console —— **不允许只输出"被拒绝"三个字**（使用者无法区分"按了没反应"的具体成因，是最消耗调试时间的一类体验问题）。
- **小兵可被控制**：减速 / 眩晕等状态效果对所有可被选中的敌方单位生效，**不限于英雄** —— 因此 `MinionPrefab` 必须挂载 `BuffComponent`（缺它时效果无处落地，且只在运行期打一条告警后静默放弃）。
- **Q（指向性非穿透弹道）**：锁定敌方单位后发射弹道，**命中时**才结算伤害；`maxHitCount = 1` 保证非穿透（命中即销毁）；CD / 蓝耗 / 射程 / 弹速 / 伤害与 `SkillData` **完全一致**；改资产即可改技能，无需改代码。
- **W（非指向性 AOE 减速）**：以指定落点为中心，`OverlapSphereNonAlloc` 收集范围内**敌方**单位并周期施加减速，友方与中立零影响；减速生效期间移速符合配置且**到期精确恢复原值**；CD / 蓝耗 / 半径 / 减速比例 / 持续时长与 `SkillData` **完全一致**。
- **弹道**：有可见飞行时间，**命中与伤害结算严格一致**（不允许"先扣血、后飞弹"）；目标中途死亡时弹道安全回收，无空引用。
- **Buff/Debuff**：减速生效期间移速符合配置且到期恢复原值；眩晕期间无法移动与攻击；重复施加按既定策略（刷新时长）执行，不产生叠加失控。
- **蓝耗**：法力不足时无法施法；法力随时间/事件回复（若配置）符合预期。
- **塔仇恨**（⏭️ 顺延至 6B，本阶段不验收）：默认打小兵；英雄 A 在塔范围内攻击己方英雄后，塔在 **1 个重评估周期（≤ 0.25 s）内**把仇恨切到英雄 A；英雄 A 离开交战半径或死亡后，塔在 1 个周期内回到小兵；**塔不会同时攻击两个目标**。
- 技能释放瞬间 GC Alloc ≤ 1 KB；战斗中不因技能产生持续 GC。
  **实现期说明（阶段六）**：施法路径上的**可避免分配已清零**（范围场对象名由字符串插值改为常量，省掉每次施法一次 string 分配）。
  但弹道 `Instantiate` / `Destroy` 与范围场 `new GameObject` 属**每次施法的固有分配**（数百字节 ~ KB 级），
  处于该阈值的边界。**测量前提**：关闭 `SkillComponent.logCastEvents` 与 `PlayerSkillController.logCastCommands`
  （调试日志的字符串插值会额外分配，开着必然超标）。**彻底归零**依赖阶段八的对象池，已在架构草案 §7 登记为已知风险点。
- **表现关闭一致性**：禁用全部技能特效后，技能伤害与胜负结果完全一致。

---

### 阶段七：信息可视化与对局 UI（玩家心流体验）✅ **[x] 已完成**（2026-09-25 封版，实机验收通过）

> **核心**：世界空间（World Space）血条显示、伤害飘字（Damage Popups）、小地图（Minimap）映射、屏幕顶部的击杀播报与计分板。
> **机制**：英雄死亡后的复活倒计时与出生点重生机制。

**输入**

- 阶段六的技能与战斗逻辑（白盒表现）。
- 血条、飘字、HUD、小地图预制体与 UI 素材。
- `MatchStatsTracker`（击杀/死亡统计与播报事件）。
- `MatchConfigData` 中的复活时间配置。

**开发范围**

**（1）世界空间血条**

- `World Space Canvas` 跟随单位头顶；敌我配色（敌方红 / 友方绿）；始终 billboard 朝向相机。
- 由 `OnHealthChanged` 事件驱动更新，**禁止每帧轮询**。
- **池化复用**，禁止每个单位 `Instantiate` 一个 Canvas；支持满血隐藏与超出视野裁剪。

**（2）伤害飘字**

- 伤害/治疗数字从受击位置飘出并淡出；由 `OnDamaged` 事件驱动。
- 必须池化；连续战斗下对象数量稳定。

**（3）玩家 HUD**

- 英雄头像（死亡置灰）、生命条、法力条、Q/W 技能图标 + **Radial Fill 冷却遮罩** + 秒数倒计时。
- 数据全部来自事件订阅（`OnHealthChanged` / `OnManaChanged` / 技能冷却查询）。

**（4）小地图**

- 世界坐标 → 小地图坐标映射；显示双方单位、防御塔与基地；英雄用更醒目的标记。
- 单位死亡/销毁后小地图标记同步移除，无残留。

**（5）击杀播报与计分板**

- `MatchStatsTracker` 统计双方击杀/死亡，广播播报事件。
- `KillFeedView`：屏幕顶部消息队列（谁击杀了谁），自动淡出，不阻塞不重叠。
- `ScoreboardView`：双方击杀数/死亡数实时显示。

**（6）复活机制**

- 英雄死亡 → 禁用输入与碰撞 → `RespawnOverlayView` 显示复活倒计时 → 倒计时结束在己方出生点重生并恢复满生命/满法力 → 恢复输入与可选中。
- 复活时间从 `MatchConfigData` 读取；对局结束时不再复活。

**验收标准**

- 血条位置与实体头顶偏差 **< 5 px**（1080p），billboard 始终朝向相机；同屏 20 个血条不产生逐帧 GC。
- 血条数值与实体实际生命实时一致（事件驱动，**无每帧轮询**）。
- **飘字**：每次伤害产生一个飘字，数值与 `TakeDamage` 实际扣血量一致；连续战斗 3 分钟后池对象数量稳定不增长。
- **HUD**：血/蓝条与实体数值一致；**CD 遮罩进度与技能实际可释放时刻一致（误差 ≤ 0.05 s）**；死亡时头像置灰且技能不可点。
- **小地图**：英雄标记位置与实际世界位置映射误差 ≤ 1 个标记宽度；单位死亡后标记 1 帧内移除。
- **击杀播报**：每次击杀恰好产生 1 条播报；连续 10 次击杀播报不重叠、不丢失、自动淡出。
- **计分板**：击杀数与 `MatchStatsTracker` 统计完全一致，无重复计数。
- **复活**：死亡到复活的耗时与 `MatchConfigData` 配置一致（误差 ≤ 0.1 s）；复活后满血满蓝、位置在己方出生点、可正常移动与施法；对局结束后不再复活。
- 同屏 30 单位 + 全部 UI 开启时 ≥ 60 FPS，UI 相关稳态 GC Alloc ≈ 0 B/帧。

**交付结果（2026-09-25 封版）**

| 项 | 交付内容 |
|---|---|
| 世界空间血条 | `WorldHealthBarManager` + `WorldHealthBarView`：管理器订阅 `EntityRegistry` 统一挂载（**单位预制体零改动**）；锚点取碰撞体包围盒顶部（绑定时一次性测量并缓存偏移）；billboard = 复制相机旋转；恒定屏幕像素高度 = 缩放 ∝ 与相机距离；满血 / 死亡 / 相机背后三条显隐规则；护盾覆盖层 |
| 伤害飘字 | `DamagePopupManager` + `DamagePopupView`：**共享世界空间画布**（1 draw call、1 次 billboard）+ 单管理器循环 Tick（不给每个飘字挂 `Update`）；池预生成 32；上浮（easeOutQuad）+ 缩放脉冲 + 淡出 |
| 玩家 HUD | `HeroHUDView` + `SkillSlotView`：头像（死亡置灰）+ 血条 + 蓝条 + Q/W 技能槽（图标 / 蓝耗 / 按键 / 秒数）；CD 径向遮罩每帧读 `GetCooldownRemaining`，**秒数只在整秒跳变时写文本** |
| 小地图 | `MinimapView`：世界 XZ → 小地图 XY 等比例映射（映射范围取地面覆盖包围盒，**与 NavMesh 烘焙同源**）；标记池化 + 登记/注销事件驱动；英雄用更醒目的独立标记；死亡即隐藏、复活自动重现 |
| 击杀播报与计分板 | `MatchStatsTracker`（**唯一计数器**，只统计英雄击杀，归属读 `HealthComponent.LastDamageSource`）+ `KillFeedView`（固定 5 槽位、下移式 FIFO、自动淡出）+ `ScoreboardView`（只读统计器，不自己计数） |
| 复活机制 | `MatchController` 统筹倒计时（读 `MatchConfigData.RespawnTime`，对局结束不再复活）+ `HeroController.Revive()` 执行逆操作（满血满蓝、回出生点、恢复寻路/碰撞/选中/输入）+ `RespawnOverlayView`（挂常驻物体，只开关面板） |
| 逻辑层增量 | `HealthComponent.OnDamaged(HealthComponent,float,float,EntityBase)` + `Revive()`、`ManaComponent.RestoreFull()`、`EntityRegistry.OnEntityRegistered/OnEntityUnregistered`、`MatchConfigData.respawnTime`、`HeroController.Revive/OnHeroRespawned` —— **全部为新增成员，阶段一~六代码零改动** |
| 对象池 | `Core/PrefabPool.cs` 泛型组件池：血条 / 飘字 / 小地图标记共用（阶段八 VFX 可直接复用）；池空返回 null + 一次性告警（**绝不 Instantiate**）；归还只 `SetActive(false)`，视图在自身 `OnDisable` 里退订与复位 |
| 自动化基建 | `AutoSceneBuilder` 改 `partial` 并拆出 `AutoSceneBuilder.UIAssembly.cs`（步骤 17）：一键生成全部 Canvas / 血条 / HUD / 小地图 / 播报 / 复活遮罩并注入全部引用；占位白图与内置字体三级回退；`ValidateUISetup` 给出确定性校验结论 |
| 工程前置 | `Packages/manifest.json` 加入 `com.unity.ugui: 1.0.0`（编辑器内置包，**离线可解析**）。**TextMeshPro 不在内置包清单** → 本阶段全部文字走 uGUI 旧版 `Text` |

**验收结果**

| 验收项 | 结论 | 依据 |
|---|---|---|
| 血条位置偏差 < 5 px（1080p）、billboard 始终朝向相机 | ✅ 实机确认 | 复制相机旋转 + `localScale ∝ 距离`；滚轮缩放时屏幕像素高度恒定 |
| 同屏 20 血条无逐帧 GC、数值事件驱动（无每帧轮询） | ✅ 闭环 | 数值只走 `OnHealthChanged` / `OnShieldChanged`；每帧只写 Transform，无字符串、无 foreach 分配 |
| 每次伤害 1 条飘字、数值 = 实际扣血量 | ✅ 实机确认 | `OnDamaged` 第一参数即"实际扣减量"；被护盾完全吸收时画灰蓝"吸收 N"（保证"每次伤害必有反馈"） |
| 连续战斗 3 分钟池对象数量稳定不增长 | ✅ 实机确认 | 池预生成 + `Release` 复位；池耗尽只隐藏、不影响逻辑 |
| HUD 血/蓝条与实体数值一致 | ✅ 实机确认 | 事件驱动（`ManaComponent` 内部已按 0.5 点节流广播） |
| CD 遮罩进度与实际可释放时刻误差 ≤ 0.05 s | ✅ 实机确认 | 每帧读 `GetCooldownRemaining`（60 FPS 下单帧误差 ≤ 16.7 ms） |
| 死亡时头像置灰、技能不可点 | ✅ 实机确认 | 置灰订阅 `OnDied`；"不可点"由 `SkillComponent.TryCast` 自身的 `IsDead` 校验保证（表现层不参与规则判定） |
| 小地图映射误差 ≤ 1 标记宽、死亡 1 帧内移除 | ⏳ 待补一次实机确认 | 映射范围与地面 / NavMesh 同源；标记在 `LateUpdate` 按 `IsDead` 隐藏（同一帧伤害结算 → 至多下一帧消失） |
| 每次击杀恰好 1 条播报、连续 10 次不重叠不丢 | ✅ 实机确认 | 固定 5 槽位 FIFO + 下移式队列；唯一计数器 |
| 计分板与统计完全一致、无重复计数 | ✅ 实机确认 | 计分板只读 `MatchStatsTracker`（结构性保证，而非两处各自算对） |
| 复活耗时与配置一致（≤ 0.1 s）、满血满蓝、在出生点、可移动施法 | ✅ 实机确认 | `RespawnRoutine` + `Revive()` 的 9 步逆操作（**先启用 agent 再 `Warp`**，并校验 `isOnNavMesh`） |
| 对局结束后不再复活 | ✅ 闭环 | `MatchController.IsMatchOver` 早退 + `StopRespawnCountdown()` |
| 同屏 30 单位 + 全部 UI 开启 ≥ 60 FPS、UI 稳态 GC Alloc ≈ 0 B/帧 | ⏳ 待 Profiler 采样 | 与阶段五同口径：量化采样项留待阶段八开发前用 Profiler 补测（风险点见"已知风险"） |

**本阶段的四项设计裁决（封版口径，不再复议）**

1. **`OnDamaged` 的发送者放在第一个参数**（`Action<HealthComponent, float, float, EntityBase>`）：受伤是高频事件，带上发送者后飘字管理器可以用**一个无捕获回调**订阅全部单位；若按单位建闭包，每次订阅都会产生一次堆分配，直接顶掉"稳态 GC ≈ 0"的验收目标。
2. **血条与飘字不由单位挂载**，而由管理器订阅 `EntityRegistry` 统一挂 / 还池：换来的是**单位预制体零改动**、动态小兵自动覆盖、逻辑层零 UI 引用（禁用管理器 = 关掉全部血条，对局结果完全不变，符合"表现层可整体关闭"铁律）。
3. **小地图标记的显隐用"每帧读 `IsDead`"而不是订阅 `OnDied`**：英雄死亡后并不注销（它会复活），用事件就必须再补一个"复活 → 显示"的事件并成对维护；读一个 bool 字段零成本，且天然覆盖"死亡隐藏 → 复活重现"整条链路。
4. **补装既有缺口**：`MatchResultView`（阶段四已验收的类）此前**从未被任何工具装配进场景**——基地被摧毁后屏幕上什么都不显示。步骤 17 现已创建它并注入 `matchController`（**未改动其任何代码**，遵守 D5"本轮不碰 MatchResultView"）。

**已知风险（登记，不阻断封版）**

- 世界空间 UI 的批次成本：每条血条是一个独立的 World Space Canvas（无法跨 Canvas 合批）→ 24 条血条 = 24 次 draw call。在 60 FPS 预算内；若超标，只需把 `WorldHealthBarView` 内部换成"屏幕空间投影"实现，**对外契约（`Bind` / 事件订阅）完全不变**。
- 世界空间 UI 会被场景几何遮挡（Canvas 默认 `ZTest LEqual`）：阶段八用"UI 专用 Layer + 只渲染该层的第二相机"解决。
- 阶段六遗留：`TargetingComponent.FindNearestEnemy` 使用 `Physics.OverlapSphere`（每次调用分配数组）→ 会让"UI 稳态 GC ≈ 0"的实测被它掩盖，建议顺手换成 `OverlapSphereNonAlloc`（`AreaEffectZone` 已有同族范式）。

> **阶段七之外的收尾补丁（同日）**：白盒期尸体清理——新增 `Core/EntityVisuals.cs` 统一开关渲染器；英雄 `HandleDied` 隐藏肉身 / `Revive` 恢复；小兵 `EntityAIController` 隐藏 + `corpseLingerSeconds`（默认 2 秒）后销毁整个 GameObject。该补丁属白盒期权宜手段，**阶段八接入真实模型与死亡动画后应由表现层接管，届时删除这两处调用点即可**。

---

### 阶段八：美术表现与表现层分离（最终商业化包装）⏳ 待开发

> **核心**：全面替换白盒胶囊体，导入标准的 3D 角色模型。
> **机制**：引入 Animator 状态机，将 FSM 逻辑状态映射到美术动画（Idle / Run / Attack / Death）。
> **视觉**：添加攻击特效（VFX）与基础音效（Audio），将地面替换为真正的 MOBA 贴图场景。

**输入**

- 阶段七的完整对局逻辑与 UI（白盒形态）。
- 标准 3D 模型：英雄、小兵、防御塔、基地。
- 动画片段：Idle / Run / Attack / Death（可选 Spell）。
- 特效资源（弹道、受击、技能）与音效资源（普攻、受击、技能、死亡、建筑摧毁）。
- MOBA 地面贴图与场景素材。
- 新建 `AnimationComponent`、`VfxSpawner`、`SimpleObjectPool`。

**开发范围**

**（1）模型替换**

- 模型挂到现有实体根节点下（根节点仍贴地、仍由 `NavMeshAgent` 驱动），逻辑组件位置不变。
- 阵营材质区分（蓝/红）；白盒保留为调试后备。

**（2）Animator 状态机（逻辑 → 表现的映射）**

- 五状态 `Idle / Run / Attack / Death`（+ 可选 `Spell`）；参数 `Speed`(float)、`Attack`(trigger)、`Spell`(trigger)、`Die`(trigger)。
- `Idle ↔ Run` 由 `Speed` 参数驱动过渡（约 0.1 s），不硬切状态，避免抖动。
- `AnyState → Death` 立即过渡，且**只播一次**。
- `AnimationComponent` 把逻辑状态映射为 Animator 参数，是**唯一允许引用 Animator 的组件**。

**（3）表现层与逻辑分离的最终验证**

- **Animation Event 只允许触发特效/音效**，严禁回调伤害结算（见 §1 边界说明）。
- 攻击节奏由 `AttackInterval` 决定，动画只做视觉匹配（必要时调整播放速度），**逻辑不等待动画**。
- **关闭全部动画/特效/音效后，对局逻辑与胜负结果必须完全一致**（本阶段必须实测通过）。

**（4）特效与音效**

- 普攻弹道、受击特效、技能特效（AOE 范围、护盾/控制）。
- 基础音效：普攻、受击、技能释放、死亡、建筑摧毁。
- 特效与音效走**对象池**（预生成 + 回收），禁止运行时 `Instantiate/Destroy` 抖动。

**（5）场景美术**

- 地面替换为真正的 MOBA 贴图场景（兵线视觉引导、区域化贴图），替换默认灰白 Plane。
- 确认贴图场景与 NavMesh 烘焙结果一致（地面视觉与可行走区域不矛盾）。

**（6）工具收尾**

- `AutoSceneBuilder` 升级为**一键组装带模型、动画、特效、音效与 UI 的完整可玩场景**（含全部依赖注入与 Animator 连线）。

**验收标准**

- **表现不影响逻辑（最高优先级）**：关闭全部动画/特效/音效后，同一局面下的伤害数值、击杀顺序与胜负结果**完全一致**。
- 四（五）个动画状态均能正确触发；`Idle ↔ Run` 过渡无抖动（连续小范围移动 10 次，不出现同帧来回切换）。
- 死亡动画只播放一次；死亡后 `Collider` 与 `NavMeshAgent` 被禁用，不再被索敌。
- **模型替换后逻辑代码零改动**（只改预制体、Animator 与表现层），验证表现层解耦成立。
- **弹道与命中一致**：弹道有可见飞行时间，命中位置与受击特效位置一致，伤害在命中时结算。
- 特效/音效对象池连续战斗 3 分钟后对象数量稳定不增长，无泄漏、无残留。
- 地面贴图场景与 NavMesh 可行走区域一致：右键点任意可见地面，英雄均可正常寻路（无"看着能走却点不动"的区域）。
- **综合性能**：同屏 30 单位 + 20 弹道 + 血条/飘字/HUD/小地图全开，目标机型 ≥ 60 FPS，战斗稳态 **GC Alloc ≈ 0 B/帧**。
- **一键交付验证**：全新克隆仓库 → 打开 `MainScene` → 执行一次 `AutoSceneBuilder` 菜单 → 直接 Play，即可完成一次带模型、动画、特效与 UI 的完整对局（出兵 → 交战 → 推塔 → 基地摧毁 → 结算），**全程零手工拖拽**。
- 完整跑通 **≥ 5 次**，无阻断流程的异常。

---

## 6. 开发铁律与推荐实施原则

### 6.1 铁律（不可违反）

1. **场景装配全自动化**：所有场景装配**必须通过 Editor 脚本完成**（`AutoSceneBuilder`），**严禁手动拖拽依赖**。
   - 凡需要被工具注入的字段必须标 `[SerializeField]`，由 `SerializedObject` / `SerializedProperty` 写入。
   - 手工在 Inspector 里拖的引用**不算交付**：必须回写到工具脚本，否则场景重建即丢失。
   - 工具必须**幂等**：重复执行结果一致；且只按固定名字清理自己生成的对象，不做全场景组件扫描删除。
2. **表现与逻辑分离**：逻辑层禁止引用 Animator / VFX / Audio / UI；**动画事件与特效绝不能反向影响伤害判定**。
3. **配置只读**：运行时不回写 `ScriptableObject`。
4. **单一职责**：禁止"上帝类"；组件间通过公开方法与 C# 事件协作，禁止跨类改私有字段。
5. **依赖自校验**：`Awake()` 校验必需依赖，缺失即 `LogError` 并 `enabled = false`，不允许静默失败。
6. **静态检查前置**：所有 `.cs` 为 UTF-8 with BOM；提交前运行项目内置结构检查脚本。

### 6.2 推荐实施原则

- 使用 `SerializeField` 暴露依赖，并在 `Awake()` 或 `OnValidate()` 中验证必需引用。
- AI 感知采用固定周期检测（节流），而非所有单位每帧执行物理范围查询。
- 状态切换采用滞回死区设计，防止边界距离上反复横跳。
- 对象池只覆盖**弹道、特效与 UI 元素**；不提前泛化到全部实体，也不引入全局事件总线。
- 技能系统保持最小可用：数据 + 效果 + 执行 + 表现四层，不建行为树、不做连招编辑。
- 测试优先覆盖：伤害结算、死亡仅触发一次、目标合法性、FSM 状态切换、塔仇恨切换、技能拒绝路径（CD / 蓝量 / 距离 / 非法目标）、**表现关闭后的结果一致性**。
- 性能验收以 Profiler 数据为准（逻辑 < 2 ms/帧、战斗稳态 GC Alloc ≈ 0 B/帧），拒绝"感觉卡"的臆断式优化。
- 完成阶段八后，再评估多兵线、野区、装备经济与网络同步。
