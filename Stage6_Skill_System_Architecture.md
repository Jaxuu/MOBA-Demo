# 阶段六：技能系统架构草案（Skill System Architecture）

> 版本 **v0.1（待审批）** ｜ 作者：阿MO ｜ 基线：阶段一 ~ 五已封版
> 权威来源：`README.md` §2.1.2 / §3.4 / §3.5 / §6；`MOBA_Demo_Plan.md` 阶段六
> **本草案不含大段实现代码**，仅给出字段设计、接口签名、链路与验收口径。批准后按此落地。

---

## 0. 结论先行

**推荐方案（一句话）**：沿用现有「数据 → 执行 → 组件 → 表现」四层，**不引入任何新框架**；新增 3 个运行时组件（`ManaComponent` / `SkillComponent` / `BuffComponent`）、2 个效果载体（`Projectile` / `AreaEffectZone`）、1 个输入层（`PlayerSkillController`）、1 个纯逻辑结算器（`SkillEffectResolver`）；**所有技能伤害与 Buff 只经 `SkillEffectResolver` 一条路径结算**；全部挂载与注入由 `AutoSceneBuilder` 新增的步骤 13~16 自动完成，**零手工拖拽**。

### 0.1 需要你拍板的 6 件事（按重要性排序）

| # | 议题 | 我的建议 | 影响 |
|---|---|---|---|
| **D1** | **技能槽位与需求文档冲突**：`README` §2.1.2 与 `Plan` 阶段六写的是 **Q = 非指向性 AOE**、**W = 指向性控制/护盾**；你本次指令是 **Q = 指向性非穿透火球**、**W = 非指向性 AOE 减速圈**（正好互换） | **以你本次指令为准**，同步修订 README 一行 + Plan 验收项文案。架构完全不变，只是两个资产填不同的枚举值 | 不改代码，只改文档；但**必须先改文档再开发**，否则阶段六验收会自己跟自己打架 |
| **D2** | **输入层归属与命名**：你说 `PlayerCommandController` 按下 Q/W → `SkillController`；但 `Plan` 定的是独立 `PlayerSkillController` | **独立 `PlayerSkillController`**，不塞进 `PlayerCommandController`。理由：后者已 749 行、职责是"右键→移动/攻击"，且它是**唯一接收右键的通道**，混入技能键会让"指令撤销/追击进度复位"这套状态机被污染 | 命名需你确认；文件数不变 |
| **D3** | **施法确认方式** | **V1：按下即释放**（按 Q/W 立刻以鼠标当前位置为目标结算）。"按 Q 进入瞄准 + 左键确认 + 右键取消"需要指示器 UI，归入阶段七 | 影响手感与工作量；V1 方案代码量约为瞄准方案的 1/4 |
| **D4** | **必须触碰 3 处已封版代码的接口增量**（全部为"新增、不改既有语义"）：① `MovementComponent` 加移动锁；② `CombatComponent` 加攻击锁；③ `IDamageable`/`HealthComponent` 加护盾吸收 + 伤害来源 | **建议批准**。理由见 §6 —— 眩晕/前摇这两个需求**绕不开**锁，否则只能靠在每个调用点判 `IsStunned`/`IsCasting`，那才是真正的漏洞来源 | 3 个文件各加 1~2 个方法，既有调用方零改动 |
| **D5** | **本轮是否一并做"防御塔仇恨优先级 + `TowerController` 挂载"**（`Plan` 阶段六含此项，你本次未提；`TowerController` 已就位但从未挂载） | **建议拆为 6B**，技能系统（6A）先封版再做，避免一次验收面过大 | 若合并，阶段六工作量约 +35% |
| **D6** | **弹道预制体的引用归属**：`ProjectileSpawner`（生成器，逻辑层不碰 prefab）vs `SkillComponent` 直持 prefab | **推荐 `ProjectileSpawner`**，与铁律 §3.4「逻辑层禁止引用表现资源」一致，且阶段八换对象池只改它内部 | 多 1 个类；若你认为多一层抽象不值，可降级 |

---

## 1. 分层与文件清单

### 1.1 四层职责（沿用 README §3.5，不做扩展）

| 层 | 职责 | 载体 | 硬约束 |
|---|---|---|---|
| **数据层** | 技能全部可配置参数 | `SkillData`（ScriptableObject） | 只读模板，运行时**绝不写回**；**不持有任何 prefab / 特效 / 音频引用**（只存字符串 ID） |
| **效果层** | 原子效果结算 + 目标选择 | `SkillEffectResolver`（纯静态逻辑）+ `SkillEffectType` / `SkillCastType` 枚举 | 唯一伤害入口；不引用任何表现 |
| **执行层** | **唯一施放入口** | `SkillComponent.TryCast(...)` | 校验 → 扣蓝 → 起 CD → 前摇 → 分派；**拒绝路径零副作用** |
| **表现层** | 施法动画 / 弹道外观 / 命中特效 / 音效 | `ProjectileSpawner`、`Projectile` 的视觉子物体、阶段八的 `VfxSpawner` | **只读订阅事件**，可整体关闭且不影响胜负 |

### 1.2 新增文件（9 个）

```
Assets/Scripts/
├── Skills/                              # 🆕 整个目录
│   ├── SkillData.cs                     # 数据层：ScriptableObject
│   ├── SkillSlot.cs                     # 枚举 Q/W/E/R（V1 只实现 Q/W，E/R 预留）
│   ├── SkillCastType.cs                 # 枚举 UnitTarget / GroundPoint
│   ├── SkillEffectType.cs               # 枚举 Damage / Shield / Slow / Stun
│   ├── SkillComponent.cs                # 执行层：TryCast 唯一入口 + 前摇计时
│   ├── SkillEffectResolver.cs           # 效果层：伤害/护盾/减速/眩晕的统一结算
│   ├── Projectile.cs                    # 弹道：飞行 + 命中判定 + 回收
│   ├── ProjectileSpawner.cs             # 弹道生成器（唯一持有弹道 prefab 的组件）
│   └── AreaEffectZone.cs                # 非指向性"场"：半径 + 持续 + 周期结算
├── Components/
│   ├── ManaComponent.cs                 # 🆕 法力值（消耗 / 回复 / OnManaChanged）
│   └── BuffComponent.cs                 # 🆕 状态效果容器（减速/眩晕/护盾的时长与刷新）
└── Controllers/
    └── PlayerSkillController.cs         # 🆕 Q/W 按键 → SkillComponent.TryCast
```

### 1.3 修改文件（6 个）

| 文件 | 改动 |
|---|---|
| `Core/Interfaces/IDamageable.cs` | **新增重载** `TakeDamage(float amount, EntityBase source)`；旧签名保留并转发 `source = null`（零破坏） |
| `Components/HealthComponent.cs` | 实现新重载；新增护盾吸收（`SetShield` / `ClearShield` / `CurrentShield`） |
| `Components/MovementComponent.cs` | 新增 `SetMovementLocked(bool)`（拒绝 `MoveTo` + 强制 `Stop`） |
| `Components/CombatComponent.cs` | 新增 `SetAttackLocked(bool)`（`CanAttack` 直接返回 false） |
| `ScriptableObjects/EntityStatsData.cs` | 新增 `maxMana` / `manaRegenPerSecond` / `skillQ` / `skillW` |
| `Editor/AutoSceneBuilder.cs` | 新增步骤 13~16 + 资产补缺 + 收尾校验（见 §5） |

### 1.4 新增资产（3 个，全部由工具生成）

```
Assets/ScriptableObjects/Skills/HeroSkillQ.asset     # 指向性弹道（火球）
Assets/ScriptableObjects/Skills/HeroSkillW.asset     # 非指向性 AOE（减速圈）
Assets/Prefabs/VFX/Projectile.prefab                 # 白盒弹道：球体 + Projectile 组件
```

---

## 2. 数据载体：`SkillData`（ScriptableObject）

### 2.1 字段设计

| 分组 | 字段 | 类型 | 说明 |
|---|---|---|---|
| **标识** | `slot` | `SkillSlot` | `Q/W/E/R`，与英雄的技能槽一一对应 |
| | `displayName` / `description` | `string` | 阶段七 HUD 用 |
| | `icon` | `Sprite` | 阶段七 HUD 用（资源引用，不参与战斗逻辑） |
| **施法类型** | `castType` | `SkillCastType` | `UnitTarget`（指向性，需一个敌方单位）/ `GroundPoint`（非指向性，需一个地面落点） |
| **施法约束** | `castRange` | `float` | 施法距离；超出即拒绝 |
| | `castTime` | `float` | **前摇（秒，逻辑计时）**；0 = 瞬发 |
| | `cooldown` | `float` | 冷却时间 |
| | `manaCost` | `float` | 蓝耗 |
| **弹道**（`UnitTarget` 用） | `projectileSpeed` | `float` | 弹道速度（米/秒），**决定可见飞行时间** |
| | `projectileRadius` | `float` | 命中判定半径 |
| | `maxHitCount` | `int` | 命中数上限：**1 = 非穿透**（本次火球）；>1 = 穿透 |
| | `maxTravelDistance` | `float` | 超距自动回收，防目标不可达时弹道永生 |
| **范围**（`GroundPoint` 用） | `effectRadius` | `float` | 作用半径 |
| | `areaDuration` | `float` | 持续时长；0 = 一次性爆发（生成即结算即销毁） |
| | `tickInterval` | `float` | 周期结算间隔；0 = 只结算一次 |
| **效果** | `effectType` | `SkillEffectType` | **单主效果**：`Damage` / `Shield` / `Slow` / `Stun` |
| | `damage` / `shieldValue` | `float` | 伤害值 / 护盾值 |
| | `slowPercent` / `slowDuration` | `float` | 减速比例（0~1）/ 减速时长 |
| | `stunDuration` | `float` | 眩晕时长 |
| **表现槽位**（阶段八接入） | `castVfxId` / `hitVfxId` / `castSfxId` | `string` | **只存字符串 ID**，由表现层查表播资源 |

### 2.2 三条设计约束（为什么这么设计）

1. **`effectType` 用单主效果 + 扁平数值，不用效果数组**。依据 README §3.5「不做完整属性修饰器聚合体系」。Q 是纯伤害、W 是纯减速，单效果已够。
   **演进路径已留好**：若日后要"减速 + 伤害"复合技能，只需把 `effectType` 升级为 `SkillEffectData[]`，`SkillEffectResolver.Apply(effect, ...)` 的签名不变，`Projectile` / `AreaEffectZone` 一行不用改。
2. **表现槽位存字符串 ID，不存 `GameObject` / `ParticleSystem` 引用**。若直接存 prefab，`SkillComponent`（逻辑层）就会间接持有表现资源，直接违反 README §3.4 第 1 条。ID 查表方案同时让阶段八接 Addressables 时零改动。
3. **`castTime` 是逻辑计时，不是动画时长**。README §3.4 第 3 条明确"逻辑层不等待动画"。前摇用 `SkillComponent` 内部计时器（`Time.time` 绝对时间点，与 `CombatComponent.nextAttackTime` 同一模式），到点即释放；动画只是**事后对齐**（必要时调播放速度），动画被裁剪不影响释放时机。

---

## 3. 核心流转生命周期

### 3.1 链路总览

```
[输入] PlayerSkillController.Update()
   │  按下 Q/W  →  射线拾取目标（UnitTarget：取敌方单位；GroundPoint：取地面落点）
   │              ↓ 目标获取优先级：鼠标下单位 → TargetingComponent.CurrentTarget → 拒绝
   ↓
[执行] SkillComponent.TryCast(slot, target, groundPoint)
   │  ① 校验链（任一失败 → return false，零副作用）
   │  ② 扣蓝  →  ③ 起 CD  →  ④ 广播 OnCastStarted（表现层播施法动画）
   │  ⑤ 前摇 castTime（>0 时挂逻辑计时；=0 当帧直接 Release）
   ↓
[分派] Release()
   ├─ UnitTarget + 弹道 → ProjectileSpawner.Spawn(origin, target, speed, radius, damage, source, hitVfxId)
   │                          └─ Projectile.Update()：匀速推进 → 命中判定 → 结算 → 回收
   └─ GroundPoint        → AreaEffectZone（一次性爆发 或 周期 tick）
                              └─ OverlapSphereNonAlloc 收集范围内敌方单位 → 逐个结算
   ↓
[结算] SkillEffectResolver.ApplyXxx(target, value, source)   ← 唯一伤害/Buff 入口
   ├─ 伤害 → HealthComponent.TakeDamage(amount, source)（护盾优先吸收）
   ├─ 护盾 → HealthComponent.SetShield(value)
   ├─ 减速 → BuffComponent.Apply(BuffType.Slow, …) → MovementComponent.SetMoveSpeed(…)
   └─ 眩晕 → BuffComponent.Apply(BuffType.Stun, …) → MovementComponent/CombatComponent 上锁
   ↓
[表现] 事件单向广播：OnSpellReleased / OnProjectileHit / OnBuffApplied / OnBuffExpired
```

### 3.2 校验链（顺序不可调换，逐条对应验收标准）

严格按 README §3.5 / Plan 阶段六的顺序，**全部通过才产生副作用**：

1. **施法者自身状态**：已死亡 → 拒绝；眩晕中 → 拒绝；正在前摇（`IsCasting`）→ 拒绝（V1 不做技能排队）
2. **`SkillData` 存在**：该槽位未配置 → 拒绝并一次性告警
3. **冷却就绪**：`Time.time >= nextCastTime[slot]`（按槽位各自独立，用数组而非 4 个字段）
4. **蓝量充足**：`ManaComponent.HasEnough(manaCost)`
5. **施法距离**：`UnitTarget` 量"施法者→目标"；`GroundPoint` 量"施法者→落点"；统一加一个 `castRangeTolerance`（建议 0.25m）吸收 NavMesh/模型半径误差
6. **目标合法性**：`UnitTarget` 必须过 `EntityBase.IsValidTarget` + `TargetingComponent.IsEnemy`（**复用全项目唯一入口，不重写规则**）；`GroundPoint` 落点必须有限且与施法者在同一平面附近

> **拒绝路径零副作用的实现要点**：扣蓝与写 CD 都放在校验链**之后**、广播事件**之前**；因此"CD 中/蓝不足/超距/目标非法"四条路径不会扣蓝、不会起 CD、不会广播任何事件 → 表现层收不到事件 → 自然"不播动画、不生成特效"，验收项自动成立。

### 3.3 两个事件而不是一个（对 `Plan` 的细化建议）

`Plan` 只写了 `OnSpellCast` 一个事件。但前摇 > 0 时，动画必须在前摇**开始**时播，效果在**结束**时生效，一个事件无法同时满足。建议拆为：

| 事件 | 触发时机 | 表现层用途 |
|---|---|---|
| `OnCastStarted(slot, data)` | 校验通过、扣蓝起 CD 之后 | 播施法动画、前摇特效、施法音效 |
| `OnSpellReleased(slot, data)` | 前摇结束、效果真正生效时 | 播释放特效（弹道生成 / AOE 圈出现） |

这**不违反**"拒绝路径不播动画"：两条事件都只在校验通过后才可能广播。

### 3.4 前摇期间的移动/攻击（关键闸门）

前摇不是"什么都不做"，否则玩家可以在 0.25s 前摇里把英雄点走，弹道却从新位置飞出。处理方式：

- `TryCast` 成功后立即 `movement.Stop()` + `movement.SetMovementLocked(true)` + `combat.SetAttackLocked(true)`
- `Release()` 或前摇结束（含死亡打断）时解锁
- **死亡是唯一打断路径**（V1 不做玩家主动打断/取消，与 README §3.5"不做技能打断"一致）；`HealthComponent.OnDied` → `SkillComponent` 取消前摇并解锁

**为什么用锁而不是在各调用点判 `IsCasting`**：调用点有 4 处（玩家指令层、FSM 的 `MoveState`/`ChaseState`/`AttackState`），漏判任何一处都会出现"前摇期间被拖走"。锁只有一个入口，不可能漏。

### 3.5 弹道 `Projectile` 的命中判定与回收

**判定方式：自研距离检测，不用物理 Trigger/Collision。** 理由：① 与"命中与伤害结算严格一致"的验收项天然吻合（同一帧、同一处代码）；② 不依赖物理矩阵配置，不产生 `OnTriggerEnter` 的隐式时序；③ 无 `Rigidbody` 依赖，白盒阶段即可跑。

每帧逻辑：
1. 目标存活 → 更新目标位置；目标已销毁/已死亡 → **保留最后已知位置**，继续飞完
2. 朝目标位置匀速推进（`speed * Time.deltaTime`）
3. 命中判定：`距离(弹道, 目标) <= projectileRadius + 目标半径容差` → 命中
4. 命中 → `SkillEffectResolver` 结算 → `hitCount++` → 广播 `OnProjectileHit`（表现层播命中特效）
5. `hitCount >= maxHitCount`（火球为 1）→ 销毁

**四种终止条件**（缺一不可，否则会出现"弹道永生"或"空引用"）：

| 条件 | 处理 |
|---|---|
| 命中数达上限 | 正常回收 |
| 超出 `maxTravelDistance` | 静默回收（目标不可达 / 被 NavMesh 挡住） |
| 超出最大存活时间（建议 `castRange / projectileSpeed * 3`） | 静默回收，兜底 |
| 目标中途销毁 | 判空 + 飞至最后位置后回收，全程 `IsAlive` 检查（接口引用销毁后不为 null） |

### 3.6 减速 / 眩晕 / 护盾的落地路径

| 效果 | 权威载体 | 落地方式 |
|---|---|---|
| **减速** | `BuffComponent` | 施加：`MovementComponent.SetMoveSpeed(baseSpeed * (1 - slowPercent))`；到期：恢复 `baseSpeed`。**`baseSpeed` 由 `BuffComponent` 在施加第一个减速时快照一次**，同类型 buff **只刷新时长 + 取更强数值，不叠加**（README「不产生叠加失控」）。**禁止**多个来源各自记录 `baseSpeed`，否则恢复值互相污染 |
| **眩晕** | `BuffComponent` | `movement.SetMovementLocked(true)` + `combat.SetAttackLocked(true)`；到期解锁。**顺带收益**：小兵被眩晕时 FSM 的 `MoveTo` 会返回 false → 不置"已下令"标记 → 每帧重试 → 眩晕一结束自动恢复，无需 FSM 感知眩晕 |
| **护盾** | **`HealthComponent`**（不是 `BuffComponent`） | 护盾是伤害管线的一部分，**必须与扣血在同一处**，否则"先扣盾还是先扣血"会出现两套顺序。`TakeDamage` 内部：先扣盾、溢出部分扣血。`BuffComponent` 只负责到期时调 `ClearShield()` |

> **减速只经 `MovementComponent.SetMoveSpeed()`，禁止直接改 `NavMeshAgent.speed`** —— 与 README §阶段六 (4) 逐字一致。

---

## 4. Demo 两技能配置（按你的指令）

### 4.1 Q「指向性非穿透火球」

| 字段 | 值 | 理由 |
|---|---|---|
| `slot` / `castType` | `Q` / `UnitTarget` | 指向性：必须锁定一个敌方单位 |
| `castRange` | `8` | 略大于普攻射程 2.5，形成"技能消耗"手感 |
| `castTime` | `0.25` | 可感知的前摇，但不至于拖沓 |
| `cooldown` / `manaCost` | `6` / `30` | 英雄 300 法力 → 满蓝可放 10 次 |
| `projectileSpeed` | `14` | 8m 距离 → **飞行 0.57s**，肉眼可见（满足"有可见飞行时间"） |
| `projectileRadius` | `0.5` | 白盒胶囊体半径量级，命中不苛刻 |
| `maxHitCount` | **`1`** | **非穿透**：命中即销毁 |
| `maxTravelDistance` | `12` | 8m 射程的 1.5 倍，兜底回收 |
| `effectType` / `damage` | `Damage` / `120` | 约为普攻 25 的 5 倍，一次技能 ≈ 2.4 次平A |
| `castVfxId` / `hitVfxId` | `SkillQ_Cast` / `SkillQ_Hit` | 阶段八接入 |

**链路走法**：按 Q → 鼠标下敌方单位（或已锁定的 `CurrentTarget`）→ `TryCast` 六项校验 → 扣蓝起 CD → 播施法动画 → 0.25s 后生成弹道 → 0.57s 飞行 → 命中目标 `TakeDamage(120)` → 播命中特效 → 弹道销毁。**目标中途死亡** → 弹道飞至最后位置后回收，不结算、不报错。

### 4.2 W「非指向性 AOE 减速圈」

| 字段 | 值 | 理由 |
|---|---|---|
| `slot` / `castType` | `W` / `GroundPoint` | 非指向性：只需一个地面落点 |
| `castRange` | `10` | 略远于 Q，鼓励"先减速再进场" |
| `castTime` | `0.15` | 短前摇 |
| `cooldown` / `manaCost` | `10` / `50` | 控制技能更贵，满蓝 6 次 |
| `effectRadius` | `3.5` | 约 2 个英雄身位，可同时覆盖 3~4 个小兵 |
| `areaDuration` | `4` | 持续 4 秒的"场" |
| `tickInterval` | `0.5` | 每 0.5s 结算一次 → 4s 内 8 次；**用 `OverlapSphereNonAlloc` + 预分配 32 缓冲，0 GC** |
| `effectType` | `Slow` | 纯控制，V1 无伤害 |
| `slowPercent` / `slowDuration` | `0.4` / `0.6` | **`slowDuration` 刻意 > `tickInterval`**：让敌人离开圈后仍残留 0.1~0.6s 减速，手感更"粘" |
| `damage` / `shieldValue` | `0` / `0` | 不用 |
| 落点可视化 | Gizmos 线框圆环（`OnDrawGizmosSelected`） | 满足"作用范围需白盒可视化" |

**链路走法**：按 W → 鼠标地面落点 → `TryCast` 六项校验（含落点与施法者距离 ≤ 10）→ 扣蓝起 CD → 0.15s 前摇 → 生成 `AreaEffectZone` → 每 0.5s 对半径 3.5m 内**敌方**单位施加/刷新减速 → 4s 后销毁，所有被减速单位恢复原速。**友方与中立零影响**（复用 `TargetingComponent.IsEnemy`）。

> ⚠️ 这两个技能与 `README` §2.1.2 记载的槽位**正好互换**，见 D1 —— 请确认后我同步修订文档。

---

## 5. 基建升级计划（`AutoSceneBuilder`）

### 5.1 新增步骤（接在现有步骤 12 之后）

| 步骤 | 方法 | 内容 |
|---|---|---|
| **13** | `EnsureSkillAssets(notes)` | 生成 `Assets/ScriptableObjects/Skills/` 目录 + `HeroSkillQ.asset` / `HeroSkillW.asset`（**缺失才创建，已存在不覆盖** —— 因为预制体会按 guid 引用它，覆盖重建会断链）；用 `SerializedObject` 写入 §4 的数值（枚举写 `intValue`，字符串写 `stringValue`） |
| **14** | `EnsureProjectilePrefab(notes)` | 生成 `Assets/Prefabs/VFX/Projectile.prefab`（球体 + `Projectile` 组件 + 可选 Trail）。派生工件，每次重建（`SaveAsPrefabAsset` 保留 guid，与 `HeroPrefab` 同一模式） |
| **15** | `CreateHeroPrefab` **扩写** | 追加 `ManaComponent` / `SkillComponent` / `BuffComponent` / `ProjectileSpawner` / `PlayerSkillController` 五个组件，并注入引用（见 5.2） |
| **16** | `ValidateSkillSetup(notes)` | 收尾校验，**只提示不改场景**：英雄实例上 5 个组件齐全、`skillQ/skillW` 非空、弹道 prefab 存在、`ManaComponent` 的 `maxMana > 0` |

### 5.2 注入清单（全部走 `SerializedObject` / `SerializedProperty`，字段必须 `[SerializeField]`）

```
heroPrefab（临时对象上）：
  + ManaComponent          → EntityBase.ApplyStats 注入 maxMana / manaRegen（与 Health 同一模式）
  + SkillComponent         → AssignObjectReference(skill, "skillQ", heroSkillQ)
                             AssignObjectReference(skill, "skillW", heroSkillW)   ← 兜底直挂
  + BuffComponent          → 无需注入（运行时自解析同物体的 Movement/Combat/Health）
  + ProjectileSpawner      → AssignObjectReference(spawner, "projectilePrefab", projectilePrefab)
  + PlayerSkillController  → AssignObjectReference(input, "controlledEntity", entity)
                             AssignObjectReference(input, "skillComponent", skill)

EntityStatsData（HeroStats.asset）：
  maxMana = 300, manaRegenPerSecond = 10
  skillQ ← HeroSkillQ.asset, skillW ← HeroSkillW.asset      ← 主数据流（与 Attack 同模式）
```

**双保险设计沿用既有模式**：`EntityStatsData.skillQ/skillW` 是主数据流（`EntityBase.ApplyStats` 注入，与 `Attack` 完全对称），`SkillComponent` 上的直挂引用是兜底 —— 与 `CombatComponent.attackData` 的双保险一致，任何一条数据流被破坏都不会导致"英雄放不出技能"。

### 5.3 ⚠️ 三个必然会踩的坑（请重点看）

1. **`EnsureHeroStatsAsset` 是"已存在就不覆盖"** —— 仓库里 `HeroStats.asset` 已存在，本次新增的 `maxMana` / `manaRegenPerSecond` / `skillQ` / `skillW` 会**全是 0 / null**，运行时表现为"英雄没蓝条、放不出技能"且**不报错**。
   **对策**：新增 `PatchHeroStatsGaps(heroStats, skillQ, skillW, notes)` —— **只补空字段（`maxMana <= 0` / 引用为 null），非空一律不动**，并在 notes 里报告补了哪几项。这是"补缺"不是"覆盖"，与铁律 §6.1 的幂等性要求一致。
2. **资产 guid 铁律**：`HeroSkillQ/W.asset` 会被 `HeroPrefab` 按 guid 引用，因此**绝不能**用 `AssetDatabase.CreateAsset` 覆盖重建。已存在时只允许 `SerializedObject` 原地改字段 + `EditorUtility.SetDirty` + `SaveAssets`。同理适用于 `Projectile.prefab`（若日后把它的引用存进任何资产，就必须改为原地更新）。
3. **专用资产必须排除出通用查找**：两个技能资产命名以 **`Hero`** 开头，天然被现有 `IsHeroDedicatedAsset()` 拦下，不会污染 `FindAssetPaths<T>()` 的通用候选。**不要**把它们命名成 `SkillQ.asset`（会进通用候选池）。

### 5.4 清理清单

技能系统全部以**组件形式挂在英雄身上**，不新增场景根对象，因此 `ManagedRootNames` **无需新增条目**。若 D6 选择新增 `SkillSystem` 根对象（放对象池），则必须同时把该名字加进 `ManagedRootNames`，否则重复执行会堆叠。

---

## 6. 对已封版代码的接口增量（D4 明细）

三处改动**全部是"新增成员"，不修改任何既有成员签名与语义**，因此阶段一~五的调用方零改动、行为零变化。

| 文件 | 新增 | 为什么绕不开 |
|---|---|---|
| `MovementComponent` | `SetMovementLocked(bool)`：`locked` 时 `MoveTo` 直接返回 false 并 `Stop()` | 眩晕与前摇都需要"禁止位移"。若不加锁，就得在玩家指令层 + FSM 三个状态共 4 处判 `IsStunned`/`IsCasting`，漏一处就是 bug |
| `CombatComponent` | `SetAttackLocked(bool)`：`CanAttack` 直接返回 false | 同上，眩晕必须禁攻击；`TryAttack` 调用点同样有 4 处 |
| `IDamageable` / `HealthComponent` | `TakeDamage(float, EntityBase source)` 重载 + 护盾（`SetShield`/`ClearShield`/`CurrentShield`） | ① 护盾必须与扣血同处（§3.6）；② **现在不做伤害来源，阶段七的击杀播报/计分板就要回头改接口**，而现在全项目只有 1 个调用点（`CombatComponent.TryAttack`），是改动成本的最低点。旧签名 `TakeDamage(float)` 保留并转发 `source = null`，零破坏 |

---

## 7. 量化验收指标

| 指标 | 目标 | 验证方式 |
|---|---|---|
| 技能拒绝路径副作用 | **严格为 0**（不扣蓝 / 不起 CD / 不广播事件 / 不生成对象 / 不播动画） | 新增 `Stage6AutoTester`（仿 `Stage2AutoTester` 的纯代码建单位模式），覆盖 4 条拒绝路径各断言一次 |
| 技能释放瞬时 GC Alloc | 校验路径 **0 B**；含 `Instantiate` 弹道的整体 ≤ **1 KB** | Profiler 采样；**诚实标注**：阶段六弹道走 `Instantiate`/`Destroy`，稳态会有周期分配；**阶段八池化后归零** |
| `AreaEffectZone` 周期结算 | **0 B / tick** | `OverlapSphereNonAlloc` + 预分配 32 缓冲 |
| 弹道飞行时间可感知 | Q：8m / 14 m/s = **0.57s** | 实机目视 + 日志时间戳 |
| 前摇帧率无关 | 0.25s 前摇在 30/60/144 fps 下释放时刻误差 ≤ 1 帧 | 逻辑计时用 `Time.time` 绝对时间点，非帧计数 |
| Q 命中与伤害严格一致 | 弹道消失帧 == 伤害结算帧（**不允许"先扣血后飞弹"**） | 日志断言同一帧 |
| W 友方零伤害 | 圈内友方血量在 4s 内**零变化** | `Stage6AutoTester` 断言 |
| 减速到期恢复 | 移速精确恢复至 `EntityStatsData.MoveSpeed`（误差 0） | 断言 `movement.Speed` 前后相等 |
| 表现关闭一致性 | 禁用全部技能特效后，技能伤害与胜负结果**完全一致** | 逻辑层不等待表现，结构性保证 |

---

## 8. 明确不做（防止范围蔓延）

沿用 README §3.5 边界：**不建**技能行为树 / 连招编辑 / 蓄力 / 多段位移 / 技能打断 / 技能排队；**不做**完整属性修饰器聚合体系（护盾+减速+眩晕三种状态已够）；**不做**独立 `BuffData` ScriptableObject —— V1 的 Buff 参数全部来自 `SkillData`，运行时用纯 C# 结构 `ActiveBuff` 承载，等出现"独立于技能的 buff 来源"（装备、光环）时再抽 SO。

---

## 9. 批准后我的落地顺序

1. 先改 `README` §2.1.2 + `Plan` 阶段六的槽位文案（D1）
2. 数据层 + 效果层（`SkillData` / 枚举 / `SkillEffectResolver`）+ 三处封版接口增量
3. 组件层（`ManaComponent` / `BuffComponent` / `SkillComponent`）
4. 效果载体（`Projectile` / `ProjectileSpawner` / `AreaEffectZone`）+ 输入层
5. `AutoSceneBuilder` 步骤 13~16 + 补缺逻辑
6. `Stage6AutoTester` + 静态检查（UTF-8 BOM / `check_unity_cs.py`）+ 一键组装实机验证

**请审批 §0.1 的 D1~D6，以及 §4 的两个技能数值。** 你确认后我按上述顺序开工。

---

## 10. 实现期增补（代码落地后回填，图纸正文未改动）

以下 4 处在编码阶段被发现是草案的疏漏或可简化项，已在实现中处理并在此登记。
**不影响 §0.1 的任何裁决**，架构分层与链路与草案完全一致。

| # | 增补内容 | 原因 |
|---|---|---|
| **A1** | `SkillData` 新增 `shieldDuration` 字段 | 草案 §2.1 的字段表里只有 `shieldValue` 而没有护盾时长，导致"护盾到期自动移除"这条验收项**无处可配**。补上后 `SkillEffectType.Shield` 才真正可用：`shieldDuration > 0` 走 `BuffComponent`（记时长，到期调 `ClearShield`），`= 0` 则为永久护盾直接写 `HealthComponent` |
| **A2** | 新增 `Components/BuffType.cs`（`Slow` / `Stun` / `Shield`） | 草案 §1.2 的文件清单漏了这个枚举。它刻意与 `SkillEffectType` 分开：前者是"单位身上挂着什么状态"（组件层视角），后者是"技能想造成什么效果"（数据层视角）；日后装备/光环等非技能来源的 buff 只增加本枚举，不会污染技能配置表 |
| **A3** | `AreaEffectZone` 用**代码创建**（`AreaEffectZone.Spawn`），不生成预制体 | 草案 §5.2 曾预留"新增 `SkillSystem` 根对象放对象池"的可能。实际上范围场当前没有任何美术资源（逻辑 + Gizmos 已满足"作用范围白盒可视化"），引入预制体只会多一个必须由工具维护的资产。**收益**：`ManagedRootNames` 无需新增条目，清理逻辑不变；阶段八要加视觉时只需在 `Spawn` 里挂一个视觉子物体 |
| **A4** | 弹道的追踪点 y 统一取发射点高度 | 目标位置是实体根节点（贴地 y=0），而发射点在胸口高度。若直接朝根节点飞，弹道会从胸口扎到地面，顶视角下像"钻地"。统一高度后弹道平飞，视觉上才是"打在身上"；命中判定用的也是这个点，因此"看起来打到"与"判定打到"仍是同一个位置 |

### 工具侧改进（非架构变更）

- **`check_unity_cs.py` 新增第 7 项检查：中文出现在注释与字符串之外。**
  落地过程中连续两次踩到"在 `Debug.LogWarning` 的中文说明里误用英文双引号"——
  字符串被提前闭合、中间那段中文变成标识符，而**前六项检查全部通过**（引号总数仍是偶数、括号仍配平），
  只有编译器会报 CS1002/CS1026。第 7 项用"剥离注释与字符串后不应残留任何中文字符"来判定，
  并排除 `#region 中文名` 这类合法的预处理器指令行。同时修正了剥离器对**插值字符串洞**的处理
  （`$"{(x ? "中文" : "英文")}"` 是合法写法，旧剥离器会因此错位并把 Stage2AutoTester / AutoSceneBuilder 误报成错误）。
  自测用例已从 6 条扩到 13 条，其中 6 条专门防假阳性。

