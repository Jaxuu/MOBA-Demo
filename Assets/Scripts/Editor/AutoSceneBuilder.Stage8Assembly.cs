using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using MOBA.AI;
using MOBA.Components;
using MOBA.Controllers;
using MOBA.Core;
using MOBA.Data;
using MOBA.Debugging;
using MOBA.Gameplay;
using MOBA.Skills;
using MOBA.Units;

namespace MOBA.Editor
{
    /// <summary>
    /// AutoSceneBuilder 的「阶段八基建装配」分册：6 座防御塔 + 10 英雄小队（5v5）。
    ///
    /// 【为什么拆成 partial 分册】主文件在阶段七之后已经超过 3000 行，再塞进约 500 行阶段八装配会难以维护。
    /// partial 是编译期语法糖：菜单入口仍然只有一个（MOBA Demo/一键组装测试战场），
    /// 调用链、撤销组、清理逻辑全部与主文件共享，行为零变化。
    ///
    /// 【本分册产出的场景结构】
    /// <code>
    /// BlueTowers                      [空根节点]
    /// ├── BlueTower_Inner            [EntityBase + Health + Targeting + Combat + Buff + TowerController + ProjectileSpawner]
    /// ├── BlueTower_MidA
    /// └── BlueTower_MidB
    /// RedTowers                       [与蓝方沿桥心对称的 3 座]
    ///
    /// BlueHeroes                      [空根节点，共 5 个英雄 = 1 玩家 + 4 AI]
    /// ├── BlueHero_0                 [玩家英雄：HeroPrefab 实例 + 玩家指令层 + 技能输入层，相机跟随]
    /// ├── BlueHero_1 ~ 4             [蓝方 AI 英雄：+ EntityAIController + HeroAIController，禁用玩家输入层]
    /// RedHeroes                       [RedHero_0 ~ 4，5 个全是 AI 英雄]
    /// </code>
    ///
    /// 【5v5 的组成由常量决定，不靠读循环推】见 PlayerHeroCount / BlueAiHeroCount / RedAiHeroCount：
    /// 蓝方 = 1 玩家 + 4 AI，红方 = 5 AI，合计 TotalHeroCount = 10。
    /// 收尾校验 ValidateHeroRoster 会回到场景里把这三条重新数一遍（不信任本地变量）。
    ///
    /// 【幂等性】塔与英雄都是 BlueTowers / RedTowers / BlueHeroes / RedHeroes 的子节点，
    /// 这四个父节点在 ManagedRootNames 里，重复执行时随父节点一起被删掉重建，
    /// 因此不需要逐个登记塔名与英雄名（10 + 6 = 16 个名字逐个维护必然漏改）。
    ///
    /// 【塔为什么不用预制体】与基地完全同一取舍：塔是固定建筑，外观就是一个立方体，
    /// 做成预制体资产只会多一层"改工具要同时改资产"的间接性。英雄则相反——
    /// 组件多、依赖注入多，必须走预制体（CreateHeroPrefab 在主文件里）。
    /// </summary>
    public static partial class AutoSceneBuilder
    {
        #region 常量：阶段八资产与外观

        /// <summary>防御塔属性资产路径（确定性资产，由本工具按固定路径创建与取用）。</summary>
        private const string TowerStatsAssetPath = ScriptableObjectsFolder + "/TowerStats.asset";

        /// <summary>防御塔普攻资产路径（确定性资产）。</summary>
        private const string TowerAttackAssetPath = ScriptableObjectsFolder + "/TowerAttack.asset";

        /// <summary>
        /// 防御塔最大生命值 = <b>1000</b>（架构师强控值，**精确写入**，不是"上限"）。
        ///
        /// 【与攻击力的策略刻意不同，别把它们当成同一类阀门】
        ///   · 生命值：**精确写入 1000** —— 无论资产里原来是 1500 还是 600，一律改写成 1000。
        ///     这是架构师明确要求的"强控"，目的是让"塔有多肉"在白盒测试期是一个**确定值**，
        ///     而不是每次组装完还要去 Inspector 里确认的数字。
        ///   · 攻击力：仍是"只压高、不抬低"（见 TowerAttackDamage）—— 架构师未要求强控它，
        ///     而 45 点是上一轮刚下调过的值，没有理由每轮组装都覆盖一遍。
        ///
        /// 【数值依据】1000 介于上一轮的 600 与更早的 1500 之间：
        /// 英雄（25 伤害 / 秒）约 40 秒、一轮 3 个小兵（各 10 伤害）约 33 秒可以推掉一座 ——
        /// 单挑推不动、跟兵线推得动，且不会像 1500 那样把白盒测试拖成消耗战。
        ///
        /// 【生命周期】与攻击力的压高阀门同属**白盒测试期的临时强控**，
        /// 阶段九进入正式数值平衡时应改回"已存在一律沿用"。
        /// </summary>
        private const float TowerMaxHealth = 1000f;

        /// <summary>
        /// 防御塔单次普攻伤害。取 45（原 60）。
        ///
        /// 【为什么下调】与血量同步降一档，让"抗塔"从"8 下就死"变成"12 下才死"，
        /// 白盒期可以多试几次越塔而不必反复重开；同时 45 仍高于小兵（10）四倍以上，
        /// 塔对小兵依旧是碾压级威胁，"小兵不能无视塔硬推"这条设计意图没有被破坏。
        /// 本值同样是已存在资产的上限（只压高、不抬低）。
        /// </summary>
        private const float TowerAttackDamage = 45f;

        /// <summary>
        /// 防御塔攻击距离。取 <b>7.5</b> 米（阶段八修复：原 6.5）。
        ///
        /// 【这个数字不是手感参数，而是"单线推塔"能否成立的结构性约束】
        /// 它必须【大于桥面的可行走半宽】—— 否则单位只要贴着桥沿走，就能从两侧绕开全部塔。
        /// 阶段八第二步的失败正是这条：塔射程 6.5，桥宽 40 米（可行走半宽 19.5 米），
        /// 塔只封锁了 33% 的宽度，剩下的全是"免打绕行道"。
        ///
        /// 现在的取值与 <c>BridgeGroundSize</c>（120×14）是**绑定的一对**：
        ///   · 桥宽 14 → 可行走半宽 = 14/2 − 0.5（NavMesh 边缘内缩一个 Agent 半径）= 6.5 米；
        ///   · 塔射程 7.5 &gt; 6.5 → 桥面上不存在"离塔足够远而不挨打"的横向位置，沿 Z 的绕行被堵死。
        ///   · 同时相邻塔间距 15 米 ≤ 2 × 7.5 = 15 米 → 沿 X 方向相邻塔的射程**恰好首尾相接**，
        ///     单位沿桥心推进时会全程处于至少一座塔的射程内。
        /// 改这个值必须同步改 BridgeGroundSize（或反之），两处必须一起看 —— 因此这里用
        /// "精确写入"而不是"已存在沿用"（见 EnsureTowerAttackAsset）。
        /// </summary>
        private const float TowerAttackRange = 7.5f;

        /// <summary>防御塔攻击间隔（秒）。比小兵（1 秒）略慢，因为单次伤害高得多。</summary>
        private const float TowerAttackInterval = 1.2f;

        /// <summary>
        /// 防御塔外观（Cube）的尺寸。细高（1.6 × 3 × 1.6）：
        /// 与基地的方正体型（2.5³）形成对比，顶视角下一眼能区分"塔"与"基地"。
        /// </summary>
        private static readonly Vector3 TowerBodySize = new Vector3(1.6f, 3f, 1.6f);

        /// <summary>蓝方防御塔材质路径。</summary>
        private const string BlueTowerMaterialPath = MaterialsFolder + "/TowerBlue.mat";

        /// <summary>红方防御塔材质路径。</summary>
        private const string RedTowerMaterialPath = MaterialsFolder + "/TowerRed.mat";

        /// <summary>蓝方防御塔颜色。与蓝方基地同色系但更亮，便于在基地旁区分两者。</summary>
        private static readonly Color BlueTowerColor = new Color(0.35f, 0.7f, 1f, 1f);

        /// <summary>红方防御塔颜色。同色系更亮，理由同上。</summary>
        private static readonly Color RedTowerColor = new Color(1f, 0.45f, 0.4f, 1f);

        // 注：英雄 / 小兵的配色材质常量已全部收拢到 AutoSceneBuilder.VfxAssembly.cs 的
        // 「实体配色：材质资产」区（四格配色表是一件事，两个分册各存一半必然漂移）。
        // 英雄实例上的渲染器覆写（ApplyHeroBodyMaterial）也已随本轮修复删除 ——
        // 上色的唯一写入者是运行期的 TeamColorView。

        #endregion

        #region 常量：英雄编制（5v5 的组成与总人数）

        /// <summary>玩家操控的英雄数量。恒为 1 —— 本 Demo 只有一块键盘。</summary>
        private const int PlayerHeroCount = 1;

        /// <summary>蓝方 AI 英雄数量。</summary>
        private const int BlueAiHeroCount = 4;

        /// <summary>红方 AI 英雄数量。</summary>
        private const int RedAiHeroCount = 5;

        /// <summary>AI 英雄总数（9 个）：技能池抽签的序号空间就是 [0, 本值)。</summary>
        private const int AiHeroCount = BlueAiHeroCount + RedAiHeroCount;

        /// <summary>
        /// 场上英雄总数（10 个）：**这是架构师强控的数字**，也是本分册所有循环的边界来源。
        ///
        /// 【为什么要拆成四个常量而不是继续用一个 HeroSquadSize】
        /// 上一版的写法是"每方各建 HeroSquadSize 个"，玩家英雄被塞在蓝方的下标 0 里 ——
        /// 于是"蓝方有几个 AI"这件事只存在于循环变量 `i == 0` 这个细节里，
        /// 读代码的人（与改代码的人）都要自己推一遍才知道蓝方是 1 玩家 + 4 AI。
        /// 现在四类英雄各有自己的常量，`PlayerHeroCount + BlueAiHeroCount + RedAiHeroCount = 10`
        /// 是编译期就能验算的恒等式，并且由收尾校验 ValidateHeroRoster 在场景里再数一遍。
        /// </summary>
        private const int TotalHeroCount = PlayerHeroCount + AiHeroCount;

        /// <summary>
        /// 每个 AI 英雄分到的技能数量（= 槽位数 Q / W / E / R）。
        /// 与 SkillComponent.skillSlots 的长度、HeroAIController 扫描的槽位数必须一致，
        /// 否则会出现"分了 4 个但只放得出 2 个"这种 Console 里看不出来的哑火。
        /// </summary>
        private const int AiHeroSkillSlotCount = 4;

        /// <summary>
        /// 英雄的避让优先级基准（阶段八第二步）。取值区间 [30, 38]，与小兵的 [60, 79] 明确错开。
        ///
        /// 【数值越小优先级越高】Unity 的约定是"优先级高的代理会挤开优先级低的"，
        /// 因此英雄用 30 段、小兵用 60 段 —— 小兵给英雄让路，而不是把英雄堵在兵线上。
        /// 同一阵营的 5 个英雄也各自不同（基准 + 序号），
        /// 因为"所有代理优先级相同"时局部避让是对称的，迎面撞上会互相顶死。
        /// </summary>
        private const int HeroAvoidancePriorityBase = 30;

        #endregion

        #region 防御塔：资产

        /// <summary>
        /// 确保防御塔普攻资产存在（伤害 45 / 射程 7.5 / 间隔 1.2 就配在这里）。
        ///
        /// 【已存在时的三条策略，刻意分开】
        /// 1. 沿用不覆盖：攻击间隔是策划数据，工具不该在每次一键组装时抹掉人工调过的值；
        /// 2. **只压高、不抬低**（伤害）：高于 TowerAttackDamage 时强制下调（白盒测试期的平衡阀门）。
        ///    上一轮留下的 60 伤害让抗塔体验过于惩罚，而"已存在一律沿用"意味着工具改不动它。
        ///    策划若主动调到更低的值，工具不会把它抬回来。
        /// 3. **精确写入**（射程）：射程与桥面宽度是**绑定的一对**（见 TowerAttackRange 的长注释），
        ///    两者共同决定"能不能从两侧绕开塔"这条关卡约束。资产里的旧值 6.5 是配 40 米宽桥留下的，
        ///    若这里沿用，桥收窄到 14 米之后射程仍然只有 6.5 —— 约束依然成立但只剩 0 米余量，
        ///    而下一轮任何人再动桥宽都会立刻破坏它。因此射程必须由工具精确控制，
        ///    与 TowerMaxHealth 同一类阀门（白盒测试期强控；阶段九正式平衡时改回「已存在一律沿用」）。
        /// </summary>
        /// <param name="notes">组装说明收集器。</param>
        /// <returns>可用的普攻资产；目录缺失导致无法创建时返回 null。</returns>
        private static AttackData EnsureTowerAttackAsset(List<string> notes)
        {
            AttackData existing = AssetDatabase.LoadAssetAtPath<AttackData>(TowerAttackAssetPath);
            if (existing != null)
            {
                notes.Add($"{TowerAttackAssetPath} 已存在，沿用现有数值（不整体覆盖，仅做下述钳制与强控）");

                bool changed = false;

                if (ClampFloatAboveCap(existing, "damage", TowerAttackDamage, out float previousDamage))
                {
                    changed = true;
                    notes.Add(
                        $"{TowerAttackAssetPath}.damage 已强制下调 {previousDamage:F0} → {TowerAttackDamage:F0}" +
                        "（白盒测试期压高阀门；阶段九正式平衡时移除）");
                }

                // 射程精确写入：它必须与 BridgeGroundSize 的桥宽成对，不能由资产单方面漂移。
                if (ForceFloatValue(existing, "attackRange", TowerAttackRange, out float previousRange))
                {
                    changed = true;
                    notes.Add(
                        $"{TowerAttackAssetPath}.attackRange 已强制设为 {TowerAttackRange:F1}" +
                        $"（原 {previousRange:F1}）（与桥宽 14 米绑定的关卡约束；阶段九正式平衡时改回「已存在一律沿用」）");
                }

                if (changed)
                {
                    EditorUtility.SetDirty(existing);
                    AssetDatabase.SaveAssets();
                }

                return existing;
            }

            if (!AssetDatabase.IsValidFolder(ScriptableObjectsFolder))
            {
                Debug.LogError(
                    $"[AutoSceneBuilder] 目录 {ScriptableObjectsFolder} 不存在，无法创建防御塔普攻资产，" +
                    "防御塔将没有攻击配置（CombatComponent 会报错且无法攻击）。");
                return null;
            }

            AttackData created = ScriptableObject.CreateInstance<AttackData>();
            AssetDatabase.CreateAsset(created, TowerAttackAssetPath);

            AssignFloat(created, "damage", TowerAttackDamage);
            AssignFloat(created, "attackRange", TowerAttackRange);
            AssignFloat(created, "attackInterval", TowerAttackInterval);

            EditorUtility.SetDirty(created);
            AssetDatabase.SaveAssets();

            notes.Add(
                $"新建 {TowerAttackAssetPath}（伤害 {TowerAttackDamage} / 射程 {TowerAttackRange} / 间隔 {TowerAttackInterval}）");
            return created;
        }

        /// <summary>
        /// 确保防御塔属性资产存在（生命 1000）。
        /// 与 EnsureBaseStatsAsset 同一约定：确定性资产，缺失才创建；
        /// 已存在时【只补空 + 强控血量】——
        ///   · 若已存在的资产没配普攻（attack 为空），补上而不改动其它字段；
        ///   · 生命值被**精确改写为 TowerMaxHealth(1000)**（架构师要求的强控，不是"上限"）。
        ///
        /// 移速 / 索敌半径刻意留 0：防御塔不可移动，且 EntityBase.ApplyStats 对塔类型会把
        /// TargetingComponent 的索敌半径直接注入成攻击距离（「看得见」必须等于「打得到」），
        /// 因此这里填任何值都不会被使用，留 0 反而能让"塔没有独立索敌半径"这件事一眼可见。
        /// </summary>
        /// <param name="towerAttack">防御塔普攻资产。</param>
        /// <param name="notes">组装说明收集器。</param>
        /// <returns>可用的属性资产；创建失败返回 null。</returns>
        private static EntityStatsData EnsureTowerStatsAsset(AttackData towerAttack, List<string> notes)
        {
            EntityStatsData existing = AssetDatabase.LoadAssetAtPath<EntityStatsData>(TowerStatsAssetPath);
            if (existing != null)
            {
                notes.Add($"{TowerStatsAssetPath} 已存在，沿用现有数值（不整体覆盖，仅做下述补写与钳制）");

                if (PatchObjectReferenceIfUnset(existing, "attack", towerAttack))
                {
                    EditorUtility.SetDirty(existing);
                    AssetDatabase.SaveAssets();
                    notes.Add($"{TowerStatsAssetPath}.attack 补写 ← {TowerAttackAssetPath}（只补空、不覆盖）");
                }

                // 白盒测试期【精确强控】血量：无论资产里原来是 1500 还是 600，一律改写成 1000。
                // 这是架构师明确要求的"强控"，因此这里用精确写入而不是 ClampFloatAboveCap（只压高）。
                if (ForceFloatValue(existing, "maxHealth", TowerMaxHealth, out float previousHealth))
                {
                    EditorUtility.SetDirty(existing);
                    AssetDatabase.SaveAssets();

                    notes.Add(
                        $"{TowerStatsAssetPath}.maxHealth 已强制设为 {TowerMaxHealth:F0}（原 {previousHealth:F0}）" +
                        "（白盒测试期强控；阶段九正式平衡时改回「已存在一律沿用」）");
                }

                return existing;
            }

            if (!AssetDatabase.IsValidFolder(ScriptableObjectsFolder))
            {
                Debug.LogError(
                    $"[AutoSceneBuilder] 目录 {ScriptableObjectsFolder} 不存在，无法创建防御塔属性资产，" +
                    "防御塔将没有生命值配置（运行时 EntityBase 会报错）。");
                return null;
            }

            EntityStatsData created = ScriptableObject.CreateInstance<EntityStatsData>();
            AssetDatabase.CreateAsset(created, TowerStatsAssetPath);

            AssignFloat(created, "maxHealth", TowerMaxHealth);
            AssignFloat(created, "moveSpeed", 0f);
            AssignFloat(created, "detectionRange", 0f);
            AssignObjectReference(created, "attack", towerAttack);

            EditorUtility.SetDirty(created);
            AssetDatabase.SaveAssets();

            notes.Add($"新建 {TowerStatsAssetPath}（塔生命 {TowerMaxHealth}，移速 / 索敌留空、攻击指向 TowerAttack）");
            return created;
        }

        #endregion

        #region 防御塔：装配

        /// <summary>
        /// 创建双方的防御塔阵（阶段八：每方 3 座，共 6 座）。
        ///
        /// 塔的组件清单（缺任何一项都会造成静默失效）：
        ///   EntityBase + HealthComponent + TargetingComponent + CombatComponent
        ///   + BuffComponent（承载控制效果：塔同样可以被减速 / 眩晕）+ TowerController
        ///   + ProjectileSpawner（塔普攻弹道的外观持有者）+ Body 子节点（Cube，自带 BoxCollider）。
        ///
        /// 刻意【不挂】MovementComponent / NavMeshAgent：README 3.3 规定防御塔是不可移动实体，
        /// 挂上会被 EntityBase.ValidateDependencies 按类型报出误配告警。
        /// 刻意【不挂】EntityAIController：那是可移动单位的 FSM，塔用 TowerController 的直线逻辑。
        /// </summary>
        /// <param name="projectilePrefab">塔普攻弹道预制体（步骤 2b 已生成）。</param>
        /// <param name="notes">组装说明收集器。</param>
        private static void CreateTowerLine(GameObject projectilePrefab, List<string> notes)
        {
            AttackData towerAttack = EnsureTowerAttackAsset(notes);
            EntityStatsData towerStats = EnsureTowerStatsAsset(towerAttack, notes);

            if (towerAttack == null || towerStats == null)
            {
                // 资产不可用时明确报错并放弃：建一批"打不出伤害的塔"比不建更糟（占位、误导排查）。
                Debug.LogError("[AutoSceneBuilder] 防御塔的属性 / 普攻资产不可用，本次已跳过 6 座防御塔的创建。");
                return;
            }

            // 材质：模型贴图材质优先、纯色兜底（阶段九）。
            // 导入真实塔模型后，纯色材质盖上去会把塔的贴图整个抹掉（写的是整个 sharedMaterial）。
            Material blueTowerMaterial = ResolveEntityColorMaterial(
                TowerModelBlueMaterialPath, BlueTowerMaterialPath, BlueTowerColor, 0.1f, "蓝方防御塔材质（蓝）", notes);
            Material redTowerMaterial = ResolveEntityColorMaterial(
                TowerModelRedMaterialPath, RedTowerMaterialPath, RedTowerColor, 0.1f, "红方防御塔材质（红）", notes);

            GameObject blueRoot = CreateRootObject(BlueTowersRootName, Vector3.zero);
            GameObject redRoot = CreateRootObject(RedTowersRootName, Vector3.zero);

            for (int i = 0; i < BlueTowerPositions.Length; i++)
            {
                string towerName = BuildTowerObjectName(BlueTowerNamePrefix, i);

                EntityBase tower = CreateTower(
                    blueRoot.transform, towerName, BlueTowerPositions[i],
                    TeamType.Player, towerStats, towerAttack, blueTowerMaterial, projectilePrefab);

                // 阶段九：挂真实塔模型（白盒 Body 只关渲染器，碰撞体保留 —— 塔要被索敌与右键拾取）。
                if (tower != null)
                {
                    AttachBuildingModel(tower.gameObject, TowerModelPrefabPath, blueTowerMaterial, towerName, notes);
                }
            }

            for (int i = 0; i < RedTowerPositions.Length; i++)
            {
                string towerName = BuildTowerObjectName(RedTowerNamePrefix, i);

                EntityBase tower = CreateTower(
                    redRoot.transform, towerName, RedTowerPositions[i],
                    TeamType.Enemy, towerStats, towerAttack, redTowerMaterial, projectilePrefab);

                if (tower != null)
                {
                    AttachBuildingModel(tower.gameObject, TowerModelPrefabPath, redTowerMaterial, towerName, notes);
                }
            }

            string projectileText = projectilePrefab != null
                ? projectilePrefab.name
                : "缺失（塔将走降级路径：命中即结算）";

            notes.Add(
                $"已生成 6 座防御塔（{BlueTowersRootName} 3 座 + {RedTowersRootName} 3 座，沿桥心对称），" +
                $"属性 {TowerStatsAssetPath}、普攻 {TowerAttackAssetPath}、弹道 {projectileText}");
        }

        /// <summary>
        /// 创建一座防御塔：空根节点（贴地坐标原点）+ Body 子节点（Cube，自带 BoxCollider）+ 全套组件与注入。
        ///
        /// 层级结构与基地 / 英雄保持一致（空根 + Body 子节点），理由同 CreateBase：
        /// 根节点保持贴地坐标原点，所有关键点（塔坐标、覆盖校验）的语义统一；
        /// 视觉体上抬半格高度后，立方体是"立在地面上"而不是埋进地里。
        /// </summary>
        /// <param name="parent">父节点（BlueTowers / RedTowers）。</param>
        /// <param name="objectName">塔对象名。</param>
        /// <param name="position">塔位置（贴地）。</param>
        /// <param name="team">阵营。</param>
        /// <param name="stats">属性资产。</param>
        /// <param name="attack">普攻资产。</param>
        /// <param name="bodyMaterial">外观材质，允许为 null（退回引擎默认材质）。</param>
        /// <param name="projectilePrefab">弹道预制体，允许为 null（塔会走降级路径并告警）。</param>
        /// <returns>刚创建的塔实体。</returns>
        private static EntityBase CreateTower(
            Transform parent,
            string objectName,
            Vector3 position,
            TeamType team,
            EntityStatsData stats,
            AttackData attack,
            Material bodyMaterial,
            GameObject projectilePrefab)
        {
            GameObject towerObject = new GameObject(objectName);
            towerObject.transform.SetParent(parent, true);
            towerObject.transform.position = position;
            Undo.RegisterCreatedObjectUndo(towerObject, "创建 " + objectName);

            GameObject body = GameObject.CreatePrimitive(PrimitiveType.Cube);
            body.name = TowerBodyName;
            body.transform.SetParent(towerObject.transform, false);

            // 立方体底面贴地：中心抬到高度的一半，否则会有一半埋在地面下。
            body.transform.localPosition = new Vector3(0f, TowerBodySize.y * 0.5f, 0f);
            body.transform.localScale = TowerBodySize;

            if (bodyMaterial != null)
            {
                // sharedMaterial 而不是 material：写入资产引用，避免生成材质实例。
                body.GetComponent<MeshRenderer>().sharedMaterial = bodyMaterial;
            }

            // 烘焙期挖洞：把塔设为 Building 层，NavMesh 烘焙时会在这里天然形成一个洞，
            // 小兵与 AI 英雄会自动划出弧线绕行（详见 BuildingLayerName 的说明）。
            // 根节点与外观体都设：真正被烘焙收集的是有网格的 Body，根节点设上只是让层归属一眼可见。
            AssignBuildingLayer(towerObject);
            AssignBuildingLayer(body);

            Undo.RegisterCreatedObjectUndo(body, "创建 " + objectName + " 外观");

            // 组件顺序：EntityBase 必须最先挂（TowerController 带 [RequireComponent(typeof(EntityBase))]）。
            EntityBase entity = Undo.AddComponent<EntityBase>(towerObject);
            Undo.AddComponent<HealthComponent>(towerObject);
            Undo.AddComponent<TargetingComponent>(towerObject);
            CombatComponent combat = Undo.AddComponent<CombatComponent>(towerObject);

            // BuffComponent：塔同样可以被减速 / 眩晕（BuffComponent 对"没有移动组件"有明确的空分支，
            // 因此给建筑挂它是安全的）。缺它的症状是"技能打在塔上毫无反应且只打一条 Warning"。
            Undo.AddComponent<BuffComponent>(towerObject);

            Undo.AddComponent<TowerController>(towerObject);

            // 弹道生成器：塔普攻弹道的唯一外观持有者。TowerController 拿不到预制体，
            // 这样"逻辑层不引用表现资源"仍是结构上成立的（与技能弹道同一套约定）。
            ProjectileSpawner spawner = Undo.AddComponent<ProjectileSpawner>(towerObject);

            // ---- 依赖注入：全部由工具完成，用户不需要拖任何引用 ----
            AssignEnum(entity, "team", (int)team);
            AssignEnum(entity, "entityType", (int)EntityType.Tower);
            AssignObjectReference(entity, "statsData", stats);

            // 组件级兜底：EntityStatsData.Attack 为空时 CombatComponent 会沿用这里直挂的资产。
            AssignObjectReference(combat, "attackData", attack);

            AssignObjectReference(spawner, "projectilePrefab", projectilePrefab);

            return entity;
        }

        /// <summary>按前缀与下标拼出塔对象名（BlueTower_Inner / BlueTower_MidA / BlueTower_MidB）。</summary>
        /// <param name="prefix">阵营前缀（BlueTower_ / RedTower_）。</param>
        /// <param name="index">塔下标（与坐标数组同序）。</param>
        /// <returns>塔对象名；下标越界时退化为前缀 + 下标数字。</returns>
        private static string BuildTowerObjectName(string prefix, int index)
        {
            if (index < 0 || index >= TowerNameSuffixes.Length)
            {
                return prefix + index;
            }

            return prefix + TowerNameSuffixes[index];
        }

        #endregion

        #region 阶段八第三步：烘焙期挖洞（决策记录）

        // 【为什么放弃 NavMeshObstacle + Carve】
        // 第二步曾用运行时 NavMeshObstacle 给建筑挖洞。架构师复核后要求改为【烘焙期挖洞】：
        // 由 NavMesh 烘焙阶段直接把建筑几何抠掉，运行时零开销、且编辑模式就能看到洞。
        // 该方案成立的前提是"建筑必须被烘焙收集到"，而收集按 Layer 掩码筛选 ——
        // 因此配套新增了 Building 层（见 BuildingLayerName）与 AssignBuildingLayer（见主文件），
        // 并把 BakeNavMeshModern 的收集掩码从"只收 Ground"改为"Ground + Building"。
        // 运行时 NavMeshObstacle 已全部移除，场景里不会再有 Carve 组件。

        #endregion

        #region 英雄小队：装配

        /// <summary>
        /// 取某一方的英雄出生坐标。
        /// 出生点在各自基地【后方】3 米，并沿 Z 轴横向铺开（见 HeroSquadZOffsets），
        /// 避免多个胶囊体叠在同一个点上（NavMeshAgent 会把它们互相推开，开局瞬间一片混乱）。
        ///
        /// 【为什么要把人数作为参数传进来，而不是继续读 HeroSquadSize】
        /// 蓝方是「1 玩家 + 4 AI」、红方是「5 AI」——两边的席位数量恰好都是 5，但**含义不同**。
        /// 若这里固定读 HeroSquadSize，那么日后只要有人单独调整 BlueAiHeroCount（例如试 4v5 手感），
        /// 出生点数组就会与循环边界悄悄错位：少建的英雄没有出生点、多出来的出生点被忽略，
        /// 而且不会有任何报错。把人数交给调用方显式传入，"谁建几个英雄"与"给几个出生点"就永远一致。
        /// </summary>
        /// <param name="team">阵营。</param>
        /// <param name="count">该方需要的出生点数量（蓝方 = 玩家 + AI，红方 = AI）。</param>
        /// <returns>按英雄下标排列的出生坐标（下标 0 为玩家英雄 / 红方第一个 AI 英雄）。</returns>
        private static Vector3[] GetHeroSpawnPositions(TeamType team, int count)
        {
            float spawnX = team == TeamType.Enemy ? RedHeroSpawnX : BlueHeroSpawnX;

            Vector3[] positions = new Vector3[Mathf.Max(0, count)];

            for (int i = 0; i < positions.Length; i++)
            {
                float offsetZ = i < HeroSquadZOffsets.Length ? HeroSquadZOffsets[i] : 0f;
                positions[i] = new Vector3(spawnX, 0f, offsetZ);
            }

            return positions;
        }

        /// <summary>
        /// 生成英雄显示名：玩家英雄固定叫「玩家英雄」，其余按阵营 + 序号命名（蓝方英雄 2 … 红方英雄 5）。
        ///
        /// 【为什么必须逐实例命名】HeroPrefab 上的显示名是默认的「英雄」，10 个实例共享它会让
        /// 击杀播报与计分板变成一屏「英雄 击杀了 英雄」——阶段七好不容易做出的播报直接失去信息量。
        /// 序号从 1 起（面向人的编号），蓝方玩家英雄固定占 1 号位。
        /// </summary>
        /// <param name="team">阵营。</param>
        /// <param name="index">英雄下标（0 起）。</param>
        /// <param name="isPlayer">是否玩家英雄。</param>
        /// <returns>显示名。</returns>
        private static string BuildHeroDisplayName(TeamType team, int index, bool isPlayer)
        {
            if (isPlayer)
            {
                return "玩家英雄";
            }

            string teamText = team == TeamType.Enemy ? "红方英雄" : "蓝方英雄";
            return $"{teamText}{index + 1}";
        }

        /// <summary>
        /// 实例化 10 英雄小队（蓝方 1 玩家 + 4 AI / 红方 5 AI），并完成"玩家英雄 / AI 英雄"的差异化装配。
        ///
        /// 差异化装配清单（这是本方法的核心价值，也是最容易漏一处就静默失效的地方）：
        ///
        /// | 项 | 玩家英雄（{BlueHeroInstancePrefix}0） | AI 英雄（其余 9 个） |
        /// |---|---|---|
        /// | 阵营 | Player（预制体自带） | 红方覆写为 Enemy，蓝方保持 Player |
        /// | PlayerCommandController | 保持启用 | **禁用**（否则 9 个英雄一起抢玩家的右键指令） |
        /// | PlayerSkillController | 保持启用 | **禁用** |
        /// | EntityAIController | **不加**（会与指令层争夺 Movement/Combat） | 加上并注入本阵营的【英雄推进线】 |
        /// | EntityAIController.chaseEngageRange | 不适用 | 注入 AiHeroChaseEngageRange（8 米，与索敌半径解耦） |
        /// | HeroAIController | **不加** | 加上并置 enableSkillDecisions = true |
        /// | SkillComponent 技能槽 | 预制体自带的盖伦 Q/W/E/R 四技能（**只有它**） | **无条件覆写**为技能池抽出的 4 个不同技能（Q/W/E/R） |
        /// | MovementComponent.avoidancePriority | 预制体默认 50 | 注入 HeroAvoidancePriorityBase + 序号（30 段） |
        /// | HeroController.playerControlled | true（预制体自带） | **置 false**（死亡/复活时不碰输入控制器） |
        /// | EntityAIController.destroyCorpseOnDeath | 不适用 | **置 false**（否则阵亡即销毁，复活无从谈起） |
        /// | 外观材质 | 运行期由 TeamColorView 按阵营上色（蓝） | 同上（红 / 蓝） |
        ///
        /// 【编制为什么写成三个常量相加】蓝方 = PlayerHeroCount + BlueAiHeroCount、红方 = RedAiHeroCount，
        /// 合计 TotalHeroCount = 10。上一版把玩家英雄藏在"蓝方下标 0"里，于是"蓝方有几个 AI"
        /// 只存在于 `i == 0` 这个细节中，改动时极易变成 6v5 —— 现在循环边界直接由常量给出，
        /// 并且收尾校验 ValidateHeroRoster 会回场景里重新数一遍。
        ///
        /// 【为什么 AI 英雄复用同一预制体而不是另建一个】README 4.5 明确要求二者共用英雄预制体与
        /// HeroController（死亡收尾 / 复活链路一致）。差异化只体现在"实例上多挂 / 少挂 / 禁用哪些组件"，
        /// 这些全部是可序列化的实例覆盖，会随场景一起保存。
        ///
        /// 【AI 英雄为什么注入【英雄推进线】而不是小兵兵线（阶段八第二步修正）】
        /// 第一步让 AI 英雄直接复用小兵兵线，于是 10 个英雄与两条兵线全部挤在同一条宽度为 0 的直线上，
        /// 而桥面可行走半宽只有 6.5 米 —— 互相顶住、推进效率骤降。
        /// 第二步给英雄单独一条沿 Z 偏移 -2.5 的推进线（小兵走 +2.5），两者相距 5 米、
        /// 仍各自处在塔的射程内，但互相干扰显著下降。FSM 的全部防震荡不变量依旧原样复用
        /// （牵引极限、卡死检测、追击放弃阈值一个都不用重新设计）。
        /// </summary>
        /// <param name="heroPrefab">英雄预制体。</param>
        /// <param name="blueLane">蓝方小兵推进路线（交给出兵点，不交给英雄）。</param>
        /// <param name="redLane">红方小兵推进路线（同上）。</param>
        /// <param name="blueHeroLane">蓝方英雄推进线。</param>
        /// <param name="redHeroLane">红方英雄推进线。</param>
        /// <param name="skillPool">技能池（9 个 AI 英雄从中按固定 seed 各抽 4 个）。</param>
        /// <param name="notes">组装说明收集器。</param>
        /// <param name="allHeroes">输出：全部 10 个英雄（蓝方 5 个在前，下标 0 为玩家英雄）。</param>
        /// <returns>玩家英雄；预制体不可用时返回 null。</returns>
        private static HeroController CreateHeroSquad(
            GameObject heroPrefab,
            LanePath blueLane,
            LanePath redLane,
            LanePath blueHeroLane,
            LanePath redHeroLane,
            List<SkillData> skillPool,
            List<string> notes,
            out HeroController[] allHeroes)
        {
            allHeroes = new HeroController[TotalHeroCount];

            if (heroPrefab == null)
            {
                Debug.LogWarning(
                    $"[AutoSceneBuilder] 英雄预制体不可用，跳过 {TotalHeroCount} 英雄小队的实例化。");
                return null;
            }

            GameObject blueRoot = CreateRootObject(BlueHeroesRootName, Vector3.zero);
            GameObject redRoot = CreateRootObject(RedHeroesRootName, Vector3.zero);

            HeroController playerHero = null;

            // 出生点数量与各自的编制严格对应：蓝方 = 玩家 + AI（= HeroSquadSize），红方 = AI
            // （理由见 GetHeroSpawnPositions）。
            Vector3[] blueSpawns = GetHeroSpawnPositions(TeamType.Player, HeroSquadSize);
            Vector3[] redSpawns = GetHeroSpawnPositions(TeamType.Enemy, RedAiHeroCount);

            int poolSize = skillPool != null ? skillPool.Count : 0;

            // 技能池抽签的序号：跨双方连续编号（0 ~ AiHeroCount-1），
            // 保证 9 个 AI 英雄各自拿到不同的派生种子（否则两队的同号英雄会抽到一模一样的 4 个技能）。
            int aiHeroIndex = 0;

            if (poolSize < AiHeroSkillSlotCount)
            {
                // 池子不足 4 个时，部分 AI 英雄的槽位会是空的（RollAiSkillSlots 用 null 补齐）。
                // 这不是致命错误（技能池是"派生工件"，下次组装就会重建），但必须说出来：
                // 症状是"某些 AI 英雄只放得出 2 个技能"，而 Console 里若不点名就无从判断是池子小了还是注入漏了。
                Debug.LogWarning(
                    $"[AutoSceneBuilder] 技能池只有 {poolSize} 个技能，少于每个 AI 英雄需要的 " +
                    $"{AiHeroSkillSlotCount} 个，部分 AI 英雄会有空技能槽。请检查 {SkillPoolFolder} 下的资产是否齐全。");
            }

            // ---- 蓝方：1 个玩家英雄 + BlueAiHeroCount 个 AI 英雄（合计 = HeroSquadSize） ----
            int blueSeats = HeroSquadSize;

            for (int i = 0; i < blueSeats; i++)
            {
                bool isPlayer = i < PlayerHeroCount;

                // 玩家英雄的 Q/W/E/R 来自预制体（盖伦那套），AI 英雄则一律由技能池按实例覆写。
                SkillData[] aiSkills = isPlayer ? null : RollAiSkillSlots(aiHeroIndex, skillPool);

                HeroController hero = CreateHeroInstance(
                    heroPrefab, blueRoot.transform, BlueHeroInstancePrefix + i, blueSpawns[i],
                    TeamType.Player, isPlayer, blueHeroLane, aiSkills,
                    isPlayer ? -1 : aiHeroIndex,
                    i,
                    BuildHeroDisplayName(TeamType.Player, i, isPlayer), notes);

                allHeroes[i] = hero;

                if (isPlayer)
                {
                    playerHero = hero;
                }
                else
                {
                    aiHeroIndex++;
                }
            }

            // ---- 红方：RedAiHeroCount 个 AI 英雄（没有玩家英雄） ----
            for (int i = 0; i < RedAiHeroCount; i++)
            {
                SkillData[] aiSkills = RollAiSkillSlots(aiHeroIndex, skillPool);

                HeroController hero = CreateHeroInstance(
                    heroPrefab, redRoot.transform, RedHeroInstancePrefix + i, redSpawns[i],
                    TeamType.Enemy, false, redHeroLane, aiSkills,
                    aiHeroIndex,
                    blueSeats + i,
                    BuildHeroDisplayName(TeamType.Enemy, i, false), notes);

                allHeroes[blueSeats + i] = hero;
                aiHeroIndex++;
            }

            notes.Add(
                $"已实例化 {TotalHeroCount} 个英雄：{BlueHeroesRootName} {blueSeats} 个" +
                $"（{PlayerHeroCount} 玩家 {BlueHeroInstancePrefix}0 + {BlueAiHeroCount} AI）；" +
                $"{RedHeroesRootName} {RedAiHeroCount} 个全为 AI；" +
                $"{AiHeroCount} 个 AI 英雄已按固定 seed（{SkillPoolRandomSeed}）从 {poolSize} 技能池中" +
                $"各抽 {AiHeroSkillSlotCount} 个不同技能挂入 Q / W / E / R 槽");

            return playerHero;
        }

        /// <summary>
        /// 为第 index 个 AI 英雄从技能池抽取 <see cref="AiHeroSkillSlotCount"/> 个技能（无放回），
        /// 按槽位顺序（Q / W / E / R）返回。
        ///
        /// 【算法】用 <c>SkillPoolRandomSeed + index × SkillPoolSeedStride</c> 派生一个独立的
        /// System.Random，然后在池下标空间上做一次部分 Fisher-Yates（洗牌），取前 N 个。
        /// 洗牌天然保证"同一英雄的 4 个技能互不相同"（无放回），且不需要手写去重重试逻辑。
        ///
        /// 【为什么每个英雄各派生一个 Random，而不是共用一个顺序抽】
        /// 共用时，抽到的结果取决于"前面抽了几次"：日后新增一个英雄、或调整池子顺序，
        /// 所有英雄的分配会整体错位，diff 里看不出"谁变了"。各自派生后，
        /// 每个英雄的结果只取决于它自己的序号与池大小，改动是局部的、可预期的。
        ///
        /// 【无放回是"每个英雄内部"的约束，不是全局的】池子只有 10 个技能而 AI 有 9 个 × 4 = 36 个槽位，
        /// 因此不同英雄之间必然重复 —— 这正是 ARAM"随机技能"的乐趣所在：
        /// 每一局里"谁拿到了火球、谁拿到了治疗"是随机的，但同一个英雄身上不会出现两个一样的技能。
        ///
        /// 【池子为空时为什么返回"四个 null"而不是 null】返回定长数组（不足处为 null）才能让调用方
        /// 把四个槽位【整体覆写】成空 —— 这是"AI 英雄绝不继承盖伦 QWER"的结构保证。
        /// 若这里返回 null 表示"不注入"，池子一旦因为资产缺失而变空，9 个 AI 英雄就会悄悄
        /// 全部退回预制体的玩家四技能，而且 Console 里什么都看不出来。
        /// </summary>
        /// <param name="aiHeroIndex">AI 英雄序号（0 ~ 8，按蓝方 1~4、红方 0~4 的顺序）。</param>
        /// <param name="pool">技能池。</param>
        /// <returns>长度恒为 AiHeroSkillSlotCount 的数组（池子不足时为 null 元素）。</returns>
        private static SkillData[] RollAiSkillSlots(int aiHeroIndex, List<SkillData> pool)
        {
            SkillData[] slots = new SkillData[AiHeroSkillSlotCount];

            int poolSize = pool != null ? pool.Count : 0;
            if (poolSize <= 0)
            {
                return slots;
            }

            System.Random random = new System.Random(
                SkillPoolRandomSeed + aiHeroIndex * SkillPoolSeedStride);

            // 洗牌用的下标数组：在 [i, poolSize) 里随机挑一个换到第 i 位（部分 Fisher-Yates）。
            int[] indices = new int[poolSize];
            for (int i = 0; i < poolSize; i++)
            {
                indices[i] = i;
            }

            int take = Mathf.Min(AiHeroSkillSlotCount, poolSize);

            for (int i = 0; i < take; i++)
            {
                int swapIndex = random.Next(i, poolSize);

                int temp = indices[i];
                indices[i] = indices[swapIndex];
                indices[swapIndex] = temp;
            }

            for (int i = 0; i < take; i++)
            {
                slots[i] = pool[indices[i]];
            }

            return slots;
        }

        /// <summary>
        /// 实例化一个英雄并完成差异化装配（玩家英雄与 AI 英雄共用本方法，靠 isPlayer 分叉）。
        ///
        /// 用 PrefabUtility.InstantiatePrefab 而不是 Object.Instantiate：场景里留下的必须是「预制体实例」，
        /// 这样日后 HeroPrefab 被重新生成时，场景实例会同步更新，而不是变成一份游离的拷贝。
        ///
        /// 落点会先做 NavMesh 采样：NavMeshAgent 若不在网格上会报「not close enough to the NavMesh」
        /// 并永久无法移动，而这属于「跑起来才发现」的静默失效。这里直接从源头把英雄放到网格表面，
        /// 采样失败（场景未烘焙 NavMesh）时才退回配置坐标，并由 ValidateNavMeshCoverage 统一告警。
        /// </summary>
        /// <param name="heroPrefab">英雄预制体。</param>
        /// <param name="parent">父节点（BlueHeroes / RedHeroes）。</param>
        /// <param name="instanceName">实例名（BlueHero_0 等）。</param>
        /// <param name="spawnPosition">出生坐标。</param>
        /// <param name="team">阵营。</param>
        /// <param name="isPlayer">是否玩家英雄。</param>
        /// <param name="lane">注入给 EntityAIController 的推进路线（玩家英雄不使用）。</param>
        /// <param name="aiSkillSlots">
        /// AI 英雄的四个技能槽（长度 = AiHeroSkillSlotCount，下标 = (int)SkillSlot，元素可为 null）；
        /// 玩家英雄传 null，表示"沿用预制体上的盖伦 Q/W/E/R"。
        /// </param>
        /// <param name="aiHeroIndex">AI 英雄序号（0~8）；玩家英雄传 -1。用于派生避让优先级。</param>
        /// <param name="championIndex">
        /// 英雄模型席位号（0~9，阶段九新增）。0 号是盖伦（玩家英雄），其余按席位顺序取 1~9。
        /// 模型与阵营材质都由它决定，见 <see cref="AttachHeroModelToInstance"/>。
        /// </param>
        /// <param name="displayName">英雄显示名（击杀播报与计分板读它）。</param>
        /// <param name="notes">组装说明收集器。</param>
        /// <returns>实例上的英雄控制器；实例化失败返回 null。</returns>
        private static HeroController CreateHeroInstance(
            GameObject heroPrefab,
            Transform parent,
            string instanceName,
            Vector3 spawnPosition,
            TeamType team,
            bool isPlayer,
            LanePath lane,
            SkillData[] aiSkillSlots,
            int aiHeroIndex,
            int championIndex,
            string displayName,
            List<string> notes)
        {
            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(heroPrefab, SceneManager.GetActiveScene());
            if (instance == null)
            {
                Debug.LogError($"[AutoSceneBuilder] 英雄 {instanceName} 实例化失败。");
                return null;
            }

            // 改名 + 挂父节点后再登记撤销：物体名要满足本工具「按名字清理」的约定。
            instance.name = instanceName;
            instance.transform.SetParent(parent, true);
            Undo.RegisterCreatedObjectUndo(instance, "创建 " + instanceName);

            Vector3 resolvedPosition = spawnPosition;
            if (NavMesh.SamplePosition(spawnPosition, out NavMeshHit navHit, HeroSpawnSampleRadius, NavMesh.AllAreas))
            {
                resolvedPosition = navHit.position;
            }
            else
            {
                Debug.LogWarning(
                    $"[AutoSceneBuilder] 英雄出生点{FormatPosition(spawnPosition)}附近 {HeroSpawnSampleRadius} 米内" +
                    $"没有 NavMesh，{instanceName} 将按原始坐标放置，运行时会因「不在 NavMesh 上」而无法移动。" +
                    "请先烘焙 NavMesh 后重新组装。");
            }

            instance.transform.position = resolvedPosition;

            EntityBase entity = instance.GetComponent<EntityBase>();
            HeroController hero = instance.GetComponent<HeroController>();

            if (entity == null || hero == null)
            {
                // 理论上不可能（预制体由本工具生成），只做防御性兜底并明确报出缺哪个组件。
                Debug.LogError(
                    $"[AutoSceneBuilder] 英雄实例 {instanceName} 缺少 " +
                    $"{(entity == null ? "EntityBase" : "HeroController")}，该英雄的阵营与死亡收尾将不可用。");
                return hero;
            }

            // ---- 1. 阵营：预制体自带 Player，红方必须覆写 ----
            // 这是"10 个英雄分成两队"的唯一数据来源（TargetingComponent.IsEnemy 只比阵营）。
            AssignEnum(entity, "team", (int)team);

            // ---- 1b. 显示名：10 个英雄必须能互相区分 ----
            // 击杀播报与计分板读的是 HeroController.HeroDisplayName，而预制体上只有默认的"英雄"。
            // 不注入的话，5v5 的播报会变成一屏"英雄 击杀了 英雄"，完全无法阅读。
            AssignString(hero, "heroDisplayName", displayName);

            // ---- 2. 外观：交给运行期的 TeamColorView（英雄预制体上已挂）----
            // 【本轮修复：这里原先有一处 ApplyHeroBodyMaterial（写实例级渲染器覆写）】
            // 它与运行期的 TeamColorView 是"同一件事的两个写入者"：谁生效取决于执行顺序，
            // 而且不报任何错。更糟的是它给玩家英雄传 null（保持预制体的绿色），
            // 于是"蓝方英雄 = 蓝色"这条规则在玩家身上直接不成立 —— 5v5 里反而最难认出自己那一队。
            // 现在英雄配色统一由 TeamColorView 在 Start 按 (Team, EntityType) 四格查表决定，
            // 本方法不再写任何材质。

            // ---- 2b. 挂本英雄自己的模型与阵营材质（阶段九：打破"10 个盖伦"）----
            // 【为什么必须放在这里、而不是方法末尾】下面第 3 步对玩家英雄有一处**提前 return**
            // （玩家不需要 AI 那一整套装配）。上一版把模型挂载放在方法末尾，结果玩家英雄
            // 一个模型都没拿到 —— 而它恰好是全场的"主角"。教训：往已有方法里追加"两条分支都要执行"
            // 的步骤时，必须先确认它排在所有提前 return 之前。
            // 【为什么必须按实例做】10 个英雄共用同一个 HeroPrefab，预制体上只能有一份模型。
            AttachHeroModelToInstance(instance, championIndex, team, notes);

            // ---- 3. 玩家英雄：保持预制体上的玩家指令层、技能输入层与盖伦 QWER ----
            if (isPlayer)
            {
                notes.Add(
                    $"{instanceName}{FormatPosition(resolvedPosition)}（玩家英雄：指令层 + 技能输入层启用，" +
                    "相机跟随，盖伦 Q/W/E/R 四技能由预制体直挂 —— 全场只有它使用这套技能）");
                return hero;
            }

            // ---- 4. AI 英雄：掐断玩家输入 + 挂 AI 组件 + 关闭尸体销毁 ----
            DisableComponentOnInstance<PlayerCommandController>(instance, instanceName, "玩家右键指令层");
            DisableComponentOnInstance<PlayerSkillController>(instance, instanceName, "玩家技能输入层");

            // HeroController.playerControlled 必须置 false：
            // 否则它会在复活时把刚禁用的 PlayerCommandController 重新启用（见 HeroController 的字段注释），
            // 症状是"某个 AI 英雄复活后开始跟着鼠标跑"。
            AssignBool(hero, "playerControlled", false);

            // 用 Undo.AddComponent 而不是 AddComponent：本工具所有新建对象与组件都登记撤销，
            // 保证一次 Ctrl+Z 能完整回退整套战场（与主文件 CreateTower / CreateBase 同一约定）。
            EntityAIController ai = Undo.AddComponent<EntityAIController>(instance);
            AssignObjectReference(ai, "entity", entity);
            AssignObjectReference(ai, "lanePath", lane);

            // 追击发起半径：决定 AI 英雄"会不会为了一个目标脱离兵线"。
            // 【为什么必须显式注入，而不是靠字段默认值】它与索敌半径是两件事：
            // 索敌半径（HeroStats.detectionRange = 20）决定"看得见多远"，本值决定"值不值得离线去追"。
            // 阶段八第二步让两者共用一个值，于是 AI 英雄被 20 米内的任何敌人勾走、
            // 被牵引到几十米外再走回来，全队在兵线节点上挤成一团（问题 1 的成因之一）。
            // 显式注入的另一个好处：这个值在 Inspector 里可见，排查"为什么 AI 不追"时一眼能核对。
            AssignFloat(ai, "chaseEngageRange", AiHeroChaseEngageRange);

            // 英雄死亡后要走"倒计时 → 复活"，销毁物体就永远复活不回来。
            // 注意这只影响"是否销毁"，肉身仍会被立刻隐藏（HeroController.HandleDied 负责）。
            AssignBool(ai, "destroyCorpseOnDeath", false);

            // ---- 5. 避让优先级（阶段八第二步）----
            // 所有代理都取同一个值时，Unity 的局部避让是对称的：两个单位迎面撞上会互相顶住。
            // 桥面只有 14 米宽、塔洞两侧的通行带各 5.2 米，这种对称顶死在实机里就是"整队挤成一团"。
            // 英雄取 30 段、小兵取 60 段（见 MinionSpawner），于是小兵会给英雄让路；
            // 同一个阵营内的 5 个英雄也各自不同，彼此之间同样不会顶死。
            MovementComponent heroMovement = instance.GetComponent<MovementComponent>();
            if (heroMovement != null)
            {
                AssignInt(heroMovement, "avoidancePriority", HeroAvoidancePriorityBase + aiHeroIndex);
            }
            else
            {
                Debug.LogWarning(
                    $"[AutoSceneBuilder] 英雄实例 {instanceName} 上没有 MovementComponent，" +
                    "避让优先级未写入（该英雄会与其它单位使用同一个默认优先级，窄道里可能互相顶住）。");
            }

            HeroAIController heroAi = Undo.AddComponent<HeroAIController>(instance);
            AssignObjectReference(heroAi, "entity", entity);

            // ---- 6. 技能决策开关与决策参数（阶段八第二步）----
            // 字段默认值已经是设计值，这里仍然显式写入：Inspector 里能看到它们，
            // 排查"AI 为什么不放技能 / 反应为什么慢"时不必再去猜默认值是什么
            // （与"场景装配必须自动化"同一思路；设计值只有一个来源，就是工具常量）。
            AssignBool(heroAi, "enableSkillDecisions", true);
            AssignFloat(heroAi, "decisionInterval", AiHeroDecisionInterval);
            AssignFloat(heroAi, "castRangeSlack", AiHeroCastRangeSlack);
            AssignFloat(heroAi, "selfCastEngageRange", AiHeroSelfCastEngageRange);
            AssignFloat(heroAi, "allyHealHealthThreshold", AiHeroAllyHealHealthThreshold);

            // ---- 7. 技能槽【无条件整体覆写】（本轮修复：AI 专属技能池）----
            // 【为什么必须按实例注入而不是写共享资产】9 个 AI 英雄的技能互不相同，
            // 而 EntityStatsData 是共享资产，无法表达"每个实例不一样"。
            // SkillComponent 的 skillSlots 是实例上的数组字段，因此这里是唯一可行的注入点；
            // 玩家英雄在更上面就 return 了（预制体上直挂的盖伦 Q/W/E/R 不被触碰）。
            //
            // 【为什么是"无条件"覆写，而不是"抽到了才写" —— 这是本轮修掉的偷懒行为】
            // 上一版的判据是 `primarySkill != null || secondarySkill != null`：
            // 一旦技能池资产缺失（抽签结果为空），9 个 AI 英雄就会【静默地】继续使用预制体上的
            // 盖伦 QWER —— 症状正是"10 个英雄全在用同一套技能"，而 Console 里一片安静。
            // 现在的写法无论池子是否可用，都把四个槽位整体写成"抽到的技能 / null"：
            // 池子异常时 AI 英雄宁可没有技能（Inspector 里四个槽位显示 None，一眼可见），
            // 也绝不会继承玩家的专属技能。
            SkillComponent heroSkills = instance.GetComponent<SkillComponent>();

            if (heroSkills == null)
            {
                Debug.LogWarning(
                    $"[AutoSceneBuilder] 英雄实例 {instanceName} 上没有 SkillComponent，" +
                    "技能池分配无法注入（该 AI 英雄将没有任何技能）。");
            }
            else
            {
                // 用 AiHeroSkillSlotCount 长度的数组整体写入：长度与 SkillSlot 的显式编号强绑定
                // （0=Q / 1=W / 2=E / 3=R），一次性把"有几个槽"与"每个槽是什么"都确定下来。
                SkillData[] slots = aiSkillSlots ?? new SkillData[AiHeroSkillSlotCount];

                UnityEngine.Object[] slotObjects = new UnityEngine.Object[AiHeroSkillSlotCount];
                for (int i = 0; i < slotObjects.Length; i++)
                {
                    slotObjects[i] = i < slots.Length ? slots[i] : null;
                }

                AssignObjectArray(heroSkills, "skillSlots", slotObjects);
            }

            // ---- 8. FSM 调试视图（阶段八自审补齐）----
            // 顺序要求：必须在 EntityAIController 挂好之后（它的 [RequireComponent] 依赖它）。
            AttachAiDebugView(instance, instanceName);

            // 刻意【不】给每个 AI 英雄都写一条组装说明：10 条记录会把 Console 的结论冲淡，
            // 而"AI 英雄装配完成"这件事已经由 CreateHeroSquad 的汇总条目表达。
            // 单个英雄若落在 NavMesh 之外，上面的 SamplePosition 失败告警会点名到具体实例。
            return hero;
        }

        /// <summary>
        /// 给一个 AI 英雄实例挂上 FSM 调试视图（阶段八自审补齐）。
        ///
        /// 【为什么需要它】`FSMDebugView` 此前只挂在 `MinionPrefab` 上（阶段三的既有做法），
        /// 于是阶段八新增的 **9 个 AI 英雄的 FSM 完全不可观测** —— 而"AI 英雄沿英雄推进线依次经过
        /// 7 个节点、看得见但不脱线、被牵引出界能返回"正是本阶段的核心验收对象。
        /// 观测手段缺失的后果是：验收时只能靠肉眼看单位位置，无法判断它处在哪个状态、目标是谁、
        /// 牵引极限有没有生效。
        ///
        /// 【为什么必须按实例挂、不能挂到预制体上】`FSMDebugView` 带
        /// `[RequireComponent(typeof(EntityAIController))]`，而英雄预制体由玩家英雄与 AI 英雄**共用** ——
        /// 玩家英雄按铁律绝不能挂 `EntityAIController`（会与指令层争夺 Movement/Combat 的写入权）。
        /// 往预制体上 `AddComponent&lt;FSMDebugView&gt;()` 会被 `RequireComponent` 自动补上一个
        /// `EntityAIController`，那正是要避免的双写。因此只能在 AI 实例上、且必须在
        /// `EntityAIController` 挂好之后再加。
        ///
        /// 【日志为什么关掉】9 个英雄的状态变化会把 Console 刷满，而验证"AI 不脱线"最有效的手段
        /// 是 Scene 视图里的四个球（索敌半径 / 追击发起半径 / 最大追击半径 / 牵引极限）与目标连线。
        /// 需要日志时在 Inspector 里把 Log State Changes 打开即可（工具不覆盖策划的手工调整）。
        /// </summary>
        /// <param name="instance">AI 英雄实例。</param>
        /// <param name="instanceName">实例名（日志用）。</param>
        private static void AttachAiDebugView(GameObject instance, string instanceName)
        {
            if (instance.GetComponent<EntityAIController>() == null)
            {
                // 走不到这里（调用点刚挂完 EntityAIController）。保留这条分支是为了把
                // "顺序错了会静默产生一个不画任何东西的视图"这件事变成一条可定位的日志。
                Debug.LogWarning(
                    $"[AutoSceneBuilder] 英雄实例 {instanceName} 上没有 EntityAIController，" +
                    "FSMDebugView 未挂载（该 AI 英雄的状态将不可观测）。");
                return;
            }

            FSMDebugView debugView = Undo.AddComponent<FSMDebugView>(instance);
            AssignBool(debugView, "logStateChanges", false);
        }

        /// <summary>
        /// 禁用实例上的某个组件，并在它不存在时告警。
        ///
        /// 【为什么要告警而不是静默跳过】"禁用 AI 英雄的玩家输入层"是防止 9 个 AI 英雄抢玩家右键指令的
        /// 唯一手段。若预制体日后不再挂该组件（或改了类型名），静默跳过就会让本步骤形同虚设，
        /// 而症状（多个英雄跟着鼠标跑）看起来完全不像"组件缺失"。
        /// </summary>
        /// <typeparam name="T">要禁用的组件类型。</typeparam>
        /// <param name="instance">英雄实例。</param>
        /// <param name="instanceName">实例名（日志用）。</param>
        /// <param name="description">该组件的中文描述（日志用）。</param>
        private static void DisableComponentOnInstance<T>(GameObject instance, string instanceName, string description)
            where T : Behaviour
        {
            T component = instance.GetComponent<T>();

            if (component == null)
            {
                Debug.LogWarning(
                    $"[AutoSceneBuilder] 英雄实例 {instanceName} 上没有 {description}（{typeof(T).Name}），" +
                    "无需禁用；若该组件本应存在，请检查英雄预制体的组件清单。");
                return;
            }

            // 登记撤销：在预制体实例上禁用组件属于实例覆盖，应能用一次 Ctrl+Z 回退。
            Undo.RecordObject(component, "禁用 " + description);

            component.enabled = false;
        }

        #endregion

        #region 小兵预制体：AI 交战参数注入

        /// <summary>
        /// 把小兵预制体上"阶段八新增 / 需要由工具锁定"的代理参数写成设计值：
        /// <list type="bullet">
        ///   <item>`EntityAIController.chaseEngageRange` —— 追击发起半径（与索敌半径解耦）；</item>
        ///   <item>`MovementComponent.arrivalStoppingDistance` —— 到达停靠距离；</item>
        ///   <item>`MovementComponent.avoidancePriority` —— 避让优先级基准。</item>
        /// </list>
        ///
        /// 【为什么必须由工具显式写入，而不是靠字段默认值】
        /// 默认值只是"新建组件时的初值"，而 MinionPrefab 是仓库里长期存在的资产 ——
        /// 它的序列化数据里【根本没有这三个字段】（阶段八才新增）。旧资产加载时是否套用
        /// C# 字段初始值，取决于引擎版本与资产的序列化状态，属于"能用但不可依赖"的行为。
        /// 本项目的既有裁决是：**这类值一律由工具显式写入**，保证"每次组装后它一定等于设计值"。
        /// 上一轮的实机自审正是抓到了这条：英雄预制体由工具每次重建（字段齐全），
        /// 而小兵预制体只被补过 `chaseEngageRange`，于是"到达距离 0.5"的修复**只对英雄生效**，
        /// 小兵的 `stoppingDistance` 仍是 0 —— 症状是"小兵照旧假卡死"，而 Console 里什么都看不出来。
        ///
        /// 【为什么走 LoadPrefabContents / SaveAsPrefabAsset】直接对预制体资产上的组件做
        /// SerializedObject 修改，在部分引擎版本下不会稳定落盘；官方推荐路径是
        /// "加载预制体内容 → 改 → 存回"。这也与 RepairPrefabComponents 的做法保持一致。
        ///
        /// 【幂等】三个字段都已经等于设计值时一个字节都不写盘（重复执行不产生资产改动）。
        /// </summary>
        /// <param name="minionPrefab">小兵预制体资产。</param>
        /// <param name="notes">组装说明收集器。</param>
        private static void PatchMinionPrefabTuning(GameObject minionPrefab, List<string> notes)
        {
            if (minionPrefab == null)
            {
                return;
            }

            string prefabPath = AssetDatabase.GetAssetPath(minionPrefab);

            if (string.IsNullOrEmpty(prefabPath) ||
                !prefabPath.EndsWith(".prefab", System.StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            GameObject contents = PrefabUtility.LoadPrefabContents(prefabPath);

            try
            {
                List<string> changes = new List<string>();

                EntityAIController ai = contents.GetComponent<EntityAIController>();

                if (ai == null)
                {
                    // RepairPrefabComponents 已经补过组件并报过告警，这里只提示一句，不重复报错。
                    Debug.LogWarning(
                        $"[AutoSceneBuilder] 小兵预制体 {prefabPath} 上没有 EntityAIController，" +
                        "追击发起半径未写入（小兵将使用组件默认值）。");
                }
                else
                {
                    SerializedObject serialized = new SerializedObject(ai);

                    if (SetFloatIfDifferent(serialized, "chaseEngageRange", AiMinionChaseEngageRange, out float previousChase))
                    {
                        serialized.ApplyModifiedProperties();
                        changes.Add(
                            $"EntityAIController.chaseEngageRange = {AiMinionChaseEngageRange:F1}（原 {previousChase:F1}，" +
                            "追击发起半径，与索敌半径解耦）");
                    }
                }

                MovementComponent movement = contents.GetComponent<MovementComponent>();

                if (movement == null)
                {
                    // 没有移动组件的小兵无法寻路，属于致命配置缺陷（EntityBase 也会报错），这里只补一句。
                    Debug.LogWarning(
                        $"[AutoSceneBuilder] 小兵预制体 {prefabPath} 上没有 MovementComponent，" +
                        "到达停靠距离与避让优先级未写入。");
                }
                else
                {
                    SerializedObject serialized = new SerializedObject(movement);
                    bool changed = false;

                    if (SetFloatIfDifferent(
                            serialized, "arrivalStoppingDistance",
                            MovementComponent.DesignArrivalStoppingDistance, out float previousStop))
                    {
                        changed = true;
                        changes.Add(
                            $"MovementComponent.arrivalStoppingDistance = {MovementComponent.DesignArrivalStoppingDistance:F2}" +
                            $"（原 {previousStop:F2}，取 0 会让拥堵时「到达路径点」永远为假）");
                    }

                    if (SetIntIfDifferent(
                            serialized, "avoidancePriority",
                            MinionSpawner.MinionAvoidancePriorityBase, out int previousPriority))
                    {
                        changed = true;
                        changes.Add(
                            $"MovementComponent.avoidancePriority = {MinionSpawner.MinionAvoidancePriorityBase}" +
                            $"（原 {previousPriority}，运行时还会按生成序号在 +0~{MinionSpawner.MinionAvoidancePrioritySpan - 1} 之间轮转）");
                    }

                    if (changed)
                    {
                        serialized.ApplyModifiedProperties();
                    }
                }

                if (changes.Count == 0)
                {
                    // 三个值都已是设计值：不写盘，保持幂等（重复执行不会产生任何资产改动）。
                    return;
                }

                PrefabUtility.SaveAsPrefabAsset(contents, prefabPath);
                AssetDatabase.SaveAssets();

                notes.Add($"{prefabPath} 已写入 AI 代理参数：{string.Join("；", changes)}");
            }
            catch (System.Exception exception)
            {
                Debug.LogError(
                    $"[AutoSceneBuilder] 写入小兵预制体的代理参数时出错，这些值可能仍是旧值：{exception.Message}");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        /// <summary>
        /// 把浮点字段写成目标值；已是目标值时返回 false（不产生任何修改）。
        /// 字段不存在或类型不符时报错并返回 false。
        /// 注意：本方法只改 SerializedProperty，**不调用 ApplyModifiedProperties** ——
        /// 由调用方在同一批修改里统一提交，避免"每个字段各 apply 一次"造成的多余落盘。
        /// </summary>
        /// <param name="serialized">目标组件的序列化包装。</param>
        /// <param name="fieldName">字段名。</param>
        /// <param name="value">目标值。</param>
        /// <param name="previousValue">输出：修改前的值。</param>
        /// <returns>确实需要修改返回 true。</returns>
        private static bool SetFloatIfDifferent(
            SerializedObject serialized, string fieldName, float value, out float previousValue)
        {
            previousValue = 0f;

            SerializedProperty property = FindProperty(serialized, fieldName);
            if (property == null || property.propertyType != SerializedPropertyType.Float)
            {
                return false;
            }

            previousValue = property.floatValue;

            if (Mathf.Approximately(previousValue, value))
            {
                return false;
            }

            property.floatValue = value;
            return true;
        }

        /// <summary>
        /// 把整型字段写成目标值；已是目标值时返回 false。约定与 <see cref="SetFloatIfDifferent"/> 完全一致。
        /// </summary>
        /// <param name="serialized">目标组件的序列化包装。</param>
        /// <param name="fieldName">字段名。</param>
        /// <param name="value">目标值。</param>
        /// <param name="previousValue">输出：修改前的值。</param>
        /// <returns>确实需要修改返回 true。</returns>
        private static bool SetIntIfDifferent(
            SerializedObject serialized, string fieldName, int value, out int previousValue)
        {
            previousValue = 0;

            SerializedProperty property = FindProperty(serialized, fieldName);
            if (property == null || property.propertyType != SerializedPropertyType.Integer)
            {
                return false;
            }

            previousValue = property.intValue;

            if (previousValue == value)
            {
                return false;
            }

            property.intValue = value;
            return true;
        }

        #endregion
    }
}
