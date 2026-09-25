# 代码与双文档对齐自检报告

> 阶段六 6A（技能系统与战斗拓展）｜ 审计人：阿MO ｜ 2026-09-25
> 审计对象：`README.md`（需求唯一权威）与 `MOBA_Demo_Plan.md`（实施细节）
> 审计范围：阶段六全部新增/修改代码（9 个 `Skills/` 类型 + 3 个组件 + 1 个输入层 + 1 个测试 + 6 处既有文件增量 + 工具步骤 13~16）

---

## 0. 结论摘要

**代码与文档整体严丝合缝，未发现"擅自发明架构"的情况。** 逐条比对后共发现 **8 处需要处理的问题**，已全部处置：

| # | 类别 | 问题 | 处置 |
|---|---|---|---|
| **A** | 🔴 **文档内部矛盾**（非代码错） | `README §3.1` 把 `Projectile` 列为 Presentation 层，与 §3.4 铁律 4 直接冲突 | ✅ 已修订 §3.1 并写明判定标准 |
| **B** | 🟡 **做少了**（Plan 过度指定） | `Plan §（4）` 要求 `BuffData`，代码未建 | ✅ 已修订 Plan 记录裁定（无消费方 = 死代码） |
| **C** | 🟡 **文档未同步** | Plan 阶段六仍标"待开发"、验收标准仍含塔仇恨 | ✅ 已标注 6A/6B 拆版 |
| **D** | 🔴 **真实缺口（违背 §6.2 铁律）** | `SkillComponent` 缺 `EntityBase` 时**静默失效** | ✅ 已修：补一次性 `LogError` + `failReason` |
| **E** | 🔴 **违背 GC 验收标准** | 范围场对象名用字符串插值 → 每次施法一次 string 分配 | ✅ 已修：改为常量 |
| **F** | 🟡 **做少了**（Plan 类型名） | 未建名为 `SkillTargetSelector` 的独立类型 | ✅ 已修订 Plan（功能由三处覆盖，不引入转发层） |
| **G** | ⚪ 边界项（如实报告，不改） | `CastRangeTolerance` 未走 SO / `skillE·R` 无数据 / `Shield` 无技能使用 / `OnShieldChanged` 文档未列 | 说明理由，保留 |
| **H** | ⏳ 未验证 | GC ≤ 1 KB 阈值边界；`Stage6AutoTester` 未跑 | 已登记为待验证项 |

---

## 1. 对照 `README.md`

### 1.1 §2.1.2 技能系统需求

| 文档要求 | 代码实现 | 结论 |
|---|---|---|
| 技能由 `ScriptableObject`（`SkillData`）完全数据驱动，策划改资产即可调整 | `SkillData` 22 个字段 + 只读属性；工具步骤 13 生成 `HeroSkillQ/W.asset` | ✅ |
| 指向性技能：锁定目标 → 弹道有真实飞行时间 → **命中时**结算 | `Projectile` 自研距离判定，命中帧才调 `SkillEffectResolver` | ✅ |
| 非指向性 AOE：指定落点 → 半径内收集所有敌方单位并结算 | `AreaEffectZone` + `OverlapSphereNonAlloc` | ✅ |
| 辅助/控制技能：对指定友方施加护盾，或对指定敌方施加控制 | 对敌控制 ✅（`Slow`/`Stun`）；**对友方护盾未实现**（见 §4 偏离 G-3） | ⚪ 部分（文档允许的形态储备） |
| Buff/Debuff：减速、眩晕；支持持续时间、到期自动解除、重复施加刷新策略 | `BuffComponent`：绝对到期时间 + 只刷新时长 + 取更强值、不叠加 | ✅ |
| CD 与蓝耗管理；CD 中/蓝量不足/超距/目标非法时**拒绝释放且无任何副作用** | 校验链全过才扣蓝起 CD；四条拒绝路径不广播任何事件 | ✅ |
| 首批技能规划：`Q` = 指向性非穿透弹道；`W` = 非指向性 AOE 减速圈 | 资产数值与 §4 逐条一致 | ✅ |

### 1.2 §2.2.3 视觉特效与音效

| 文档要求 | 结论 |
|---|---|
| 普攻弹道有真实飞行时间，命中时结算 | ✅（阶段二既有） |
| 受击特效在命中位置播放 | ✅ 已备好锚点：`Projectile.OnHit(弹道, 目标, 命中位置)` |
| 技能特效（AOE 范围表现 / 控制表现 / 弹道表现） | ⏭️ 阶段八；阶段六已给弹道白盒球体 + 范围场 Gizmos 可视化 |
| **逻辑代码中不得出现具体特效/音频资源引用** | ✅ `SkillData` 只存字符串 ID（`castVfxId`/`hitVfxId`/`releaseVfxId`），逻辑层零资源引用（已 grep 验证） |

### 1.3 §3.1 组件化分层 — 🔴 **发现文档内部矛盾（偏离 A）**

`§3.1` 的层表把 `Projectile` 归入 **Presentation 层**。但：

- **§3.4 铁律 4**：`表现层可整体关闭：关闭所有动画/特效/音效后，对局逻辑与胜负结果必须完全一致。`
  → 若弹道属表现层，关掉它等于关掉所有指向性技能的伤害，**胜负结果必然改变**。
- **§3.5 效果层**：`目标选择器（范围内敌方 / 指定目标 / 指定友方）` → 目标选择本来就属效果层，而弹道/范围场正是它的运行期载体。

**处置**：判定「某类型算逻辑还是表现」的统一标准就是铁律 4——**关掉它，对局结果会不会变**。
据此已修订 §3.1：从 Presentation 行移除 `Projectile`，并补一段裁定说明。
代码无需改动（若把 `Projectile` 改成"只报到达、由 `SkillComponent` 结算"，反而会把"目标选择"从 §3.5 指定的效果层搬进执行层，**是更严重的层级错位**）。

### 1.4 §3.4 表现与逻辑分离（四条硬性约束逐条）

| 约束 | 验证方式与结论 |
|---|---|
| 1. 单向数据流；逻辑层禁止引用 `Animator`/`ParticleSystem`/`AudioSource`/UI 组件 | ✅ 已 grep 全部阶段六逻辑文件：零命中（仅注释里提到这些名字） |
| 2. **严禁反向影响伤害判定**；伤害判定只由 `CombatComponent`/`SkillComponent` 决定 | ✅ 无任何 Animation Event 参与；校验门在 `SkillComponent.TryCast`，效果结算在 §3.5 指定的效果层 `SkillEffectResolver` |
| 3. 表现可丢失；**逻辑层不等待动画** | ✅ 前摇用 `Time.time` 绝对时间点计时，与动画片段长度无关 |
| 4. **表现层可整体关闭**，对局结果完全一致 | ✅ 逻辑不订阅任何表现事件；弹道/范围场已按偏离 A 正名为逻辑载体 |

**阶段六广播的事件清单**（§3.4 要求"逻辑层单向广播"）：
`OnCastStarted`、`OnSpellReleased`、`Projectile.OnHit`、`OnBuffApplied`、`OnBuffExpired`、`OnManaChanged`、`OnShieldChanged`。
> ⚠️ 目前唯一订阅方是 `Stage6AutoTester`。这不是"死代码"——§3.4 的事件清单本身就是架构契约，
> 阶段七 HUD 与阶段八 VFX 是这些事件的既定消费者。但**在阶段七落地前，它们确实处于"无人订阅"状态**，如实登记。

### 1.5 §3.5 技能四层 + 设计边界

| 文档 | 实现 | 结论 |
|---|---|---|
| 数据层 = `SkillData` | `SkillData` | ✅ |
| 效果层 = 效果枚举 + 目标选择 + 结算 | `SkillEffectType` + `SkillEffectResolver` + `SkillCastType`/`AreaEffectZone`（见偏离 F） | ✅ |
| 执行层 = `TryCast(slot, groundPoint, target, out failReason)` 唯一入口 | 签名与顺序完全一致（另加 ①自身状态 ②配置存在 两道前置校验，属必要补充） | ✅ |
| 表现层 = 订阅 `OnSpellCast` | 拆为 `OnCastStarted` + `OnSpellReleased`（草案 §10 已登记并经批准） | ✅ |
| **刻意不做**：行为树 / 连招编辑 / 技能打断·蓄力·多段 | 全部未做；死亡是唯一打断路径 | ✅ |
| **刻意不做**：完整属性修饰器聚合体系 | `BuffComponent` 只有 3 种状态、只刷新不叠加 | ✅ |
| 首批只支持 Q/W，槽位机制预留扩展 | `SkillSlot` 四值 + `skillE/skillR` 字段（见偏离 G-2） | ✅ |

### 1.6 §6 开发流铁律（六条）

| 铁律 | 结论 |
|---|---|
| 1. 场景装配全自动化；新增注入字段必须 `[SerializeField]` + 工具用 `SerializedObject` 写入；工具幂等、只按固定名字清理 | ✅ 新增 5 个组件的注入全部走 `AssignObjectReference`；`ManagedRootNames` 未扩（技能系统全挂在英雄身上，无新根对象） |
| 2. **依赖自校验：缺失即 `LogError`，不允许静默失败** | 🔴 **原实现不达标（偏离 D）→ 已修正** |
| 3. 逻辑不依赖表现 | ✅ |
| 4. 配置只读（运行时不回写 SO） | ✅ 已 grep：运行时代码零 `SetDirty`/`SaveAssets` |
| 5. 专用资产排除出通用查找；`t:GameObject` 限 `.prefab` | ✅ `HeroSkillQ/W` 以 `Hero` 前缀被 `IsHeroDedicatedAsset` 拦下；新增 `BaseStats.asset` 改为确定性路径直接取用 |
| 6. 提交前静态检查（UTF-8 BOM + 结构检查） | ✅ 49 个 `.cs` 七项检查全通过、缺 using 扫描 0 处、工具自测 13/13 |

### 1.7 §7 里程碑 / §8 Out of Scope

- §7 阶段六状态行与 §1 当前进度：🟡 原仍写"待开发" → **已同步为 6A 已交付 / 6B 顺延（偏离 C）**。
- §8 明确不做：多人网络、装备经济、多兵线野区、英雄成长天赋、技能连招/蓄力/多段位移 → ✅ **一项未碰**。

---

## 2. 对照 `MOBA_Demo_Plan.md` 阶段六

### 2.1 开发范围

| 条目 | 结论 |
|---|---|
| (1) 技能系统架构（数据/效果/执行/输入四层） | ✅（`SkillTargetSelector` 见偏离 F） |
| (2) 指向性技能与弹道：真实飞行时间、命中时结算、目标中途死亡安全回收 | ✅ 四种回收条件齐备，`Stage6AutoTester` 阶段 3 专门覆盖 |
| (3) 非指向性 AOE：`OverlapSphereNonAlloc`、友方与中立零影响、作用范围白盒可视化 | ✅ 用 `NonAlloc` + 32 缓冲；同一 tick 对目标去重；`OnDrawGizmos` 画线框圆 + 半透明实心圆 |
| (4) Buff/Debuff：三种状态、只经 `SetMoveSpeed` 改移速、眩晕禁移动与攻击、护盾优先吸收 | ✅（`BuffData` 见偏离 B） |
| (5) CD 与蓝耗：`ManaComponent` + 事件广播；冷却剩余可查询供阶段七读 | ✅ `OnManaChanged` + `GetCooldownRemaining(slot)` |
| (6) 防御塔仇恨优先级 | ⏭️ 按 D5 拆为 6B，本轮不做（偏离 C，已在文档标注） |

### 2.2 验收标准（12 条）

| 验收项 | 代码支撑 | 结论 |
|---|---|---|
| 技能拒绝路径零副作用 | 扣蓝与写 CD 排在整条校验链之后 | ✅ 结构性保证 |
| 拒绝原因可观测 | `out string failReason`，9 个分支各自给可读原因 + 量化信息 | ✅ |
| 小兵可被控制 | `RepairPrefabComponents` 清单已补 `BuffComponent` | ✅ |
| Q 指向性非穿透弹道，数值与 `SkillData` 完全一致 | `maxHitCount = 1`；数值全部读自 `SkillData` | ✅ |
| W 非指向性 AOE 减速，友方与中立零影响，到期精确恢复原值 | `IsEnemy` 复用唯一规则入口；恢复走 `baseSpeed` 快照 | ✅ |
| 弹道可见飞行时间；命中与伤害结算严格一致；目标中途死亡安全回收 | 0.57 s 飞行；同帧判定同帧结算；`IsAlive` 全链路判空 | ✅ |
| Buff/Debuff：移速符合配置、眩晕禁移动与攻击、重复施加按刷新策略 | 取更强值 + 刷新时长，不叠加 | ✅ |
| 蓝耗：法力不足无法施法；法力随时间回复符合预期 | `HasEnough` + `TrySpend` 原子化；回复按阈值节流广播 | ✅ |
| 塔仇恨 | — | ⏭️ 顺延 6B |
| 技能释放瞬间 GC Alloc ≤ 1 KB；无持续 GC | 可避免分配已清零（偏离 E 已修）；固有分配处于阈值边界 | ⚠️ **待实机 Profiler 采样**（偏离 H） |
| 表现关闭一致性 | 逻辑不等待、不订阅表现 | ✅ |

---

## 3. 本轮代码修正明细（两处）

### 偏离 D：`SkillComponent` 缺少身份依赖自校验（违背 README §6.2）

**问题**：`Owner` 为 null 时**不报任何错**。后果链条是静默的——`ProjectileSpawner.Spawn(source == null)` 直接返回 `null` → `released = false` → 不广播释放事件 → 表现为"技能放不出去（连蓝和 CD 都没动）且 Console 一片安静"。

**修正**：在 `TryCast` 校验链最前面加 **⓪ 身份依赖** 闸门：`Owner == null` → 一次性 `LogError` + `failReason = "缺少 EntityBase（无法判断阵营与伤害归属）"` + 返回 false。

**为什么用"延迟解析 + 首次使用时报错"而不是 `Awake` 校验**：动态创建单位时（`MinionSpawner` / `Stage6AutoTester`）组件挂载顺序不可控，`EntityBase` 可能晚于 `SkillComponent` 挂上，`Awake` 校验会对这种**合法用法误报**。这与 `CombatComponent.Owner` / `TargetingComponent` 的既有处理方式逐条一致。

### 偏离 E：施法路径上的字符串分配（违背 GC 验收标准）

**问题**：`AreaEffectZone.Spawn` 用 `$"AreaEffectZone_{data.DisplayName}"` 拼对象名 → 每次施法一次 `string` 分配，与"技能释放瞬间 GC Alloc ≤ 1 KB"直接冲突。

**修正**：改为常量 `ZoneObjectName = "AreaEffectZone"`。对象名对排查没有帮助（Hierarchy 里多个场靠位置即可分辨），零成本省掉这次分配。

---

## 4. 边界项（如实报告，刻意不改）

| # | 项 | 为什么保留 |
|---|---|---|
| **G-1** | `SkillComponent.CastRangeTolerance = 0.25f` 是代码内常量，未走 `SkillData`（README §3.3 要求"技能参数等"走 SO） | 它是**输入容差**（吸收模型半径与 NavMesh 吸附误差），不是设计数值。放进 SO 会让每个技能资产多一个没人会调的字段，而调错它只会造成"技能时灵时不灵"这类难查的手感问题。**如果你认为它该可配，我一句话就能挪进 `SkillData`。** |
| **G-2** | `skillE` / `skillR` 两个 `[SerializeField]` 无消费方 | README §3.5 明确"槽位机制预留扩展"，属文档授权。**注意**：`SkillComponent` 里没有"空槽位"的特殊分支——未配置时 `TryCast` 会以 `"{槽位} 槽位未配置技能"` 拒绝，行为明确。 |
| **G-3** | `SkillEffectType.Shield` + `SkillEffectResolver.ApplyShield` 已实现，但**首批技能无使用者**；`TryValidateTarget` 只接受敌方，故"对指定友方施加护盾"当前不可用 | README §3.5 授权"护盾"作为基础状态效果，且 §2.1.2 把"对指定友方施加护盾"列为**技能形态**（非首批）。实现已就位，做友方技能时只需放开 `TryValidateTarget` 的敌方限制。**这是已知的、有明确扩展点的缺口，不是遗漏。** |
| **G-4** | `HealthComponent.OnShieldChanged` 事件不在 §3.4 的事件清单里 | §2.3 要求血条"由事件驱动、**禁止每帧轮询**"，且 §2.1.2 要求护盾存在。若将来要显示护盾条，就必须有这个事件；现在补上比到时候回头改伤害管线便宜。 |

---

## 5. 未验证项与风险（如实登记）

| 项 | 说明 |
|---|---|
| **H-1** | `Stage6AutoTester` **尚未实机运行**。它覆盖 8 组共 30+ 条断言（4 条拒绝路径 + 前摇 + 冷却的原因校验、弹道命中伤害一致、非穿透回收、飞行中目标销毁、护盾吸收、眩晕双锁、减速到期恢复、AOE 敌我区分）。建议在 `MainScene` 跑（被减速单位需要 `NavMeshAgent`）。 |
| **H-2** | GC 验收（≤ 1 KB）**需实机 Profiler 采样**。测量前提：关闭 `SkillComponent.logCastEvents` 与 `PlayerSkillController.logCastCommands`（调试日志的字符串插值会额外分配）。彻底归零依赖阶段八对象池。 |
| **H-3** | 弹道 `Instantiate` / `Destroy` 与范围场 `new GameObject` 属每次施法的固有分配，**阶段八对象池**是既定的收口方案（草案 §7 已登记）。 |
| **H-4** | 阶段七的 `OnDamaged` 事件（README §3.4 事件清单要求，用于伤害飘字）**尚未实现** —— 属阶段七范围，但技能侧已铺好路：`TakeDamage(amount, source)` 会记录 `LastDamageSource`。 |

---

## 6. 最终结论

> **代码与 `README.md` / `MOBA_Demo_Plan.md` 在阶段六 6A 范围内完全对齐。**
>
> 未发现"擅自发明架构"：所有新增类型与接口增量都能追溯到文档条款或经你批准的裁决（D1~D6）。
> 8 处发现中，**2 处是真实代码缺陷（D：静默失效；E：违背 GC 标准）已修**，
> **1 处是文档内部矛盾（A）已修订文档**（因为代码侧改法会造成更严重的层级错位），
> **3 处是 Plan 过度指定或未同步（B/C/F）已修订文档**，
> **2 处是如实报告的边界项与待验证项（G/H）**。
