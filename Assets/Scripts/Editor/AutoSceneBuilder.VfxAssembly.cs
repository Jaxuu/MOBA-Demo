using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using MOBA.VFX;

namespace MOBA.Editor
{
    /// <summary>
    /// AutoSceneBuilder 的「白盒视觉反馈装配」分册（阶段八实机修复新增）。
    ///
    /// 【为什么需要这个分册】阶段八实机测试发现 5v5 团战缺最基本的视觉区分与技能反馈：
    ///   ① 小兵分不清敌我；
    ///   ② 技能释放后除了进 CD 没有任何画面反馈（盖伦的 QWER 全靠想象）。
    /// 两者都属于「表现层」缺失，且都必须在【组装阶段】被确定下来（README §6.1 铁律 1：
    /// 所有场景装配必须通过 Editor 脚本完成，严禁手动拖拽）—— 因此这里补一个装配步骤，
    /// 与 DebugAssembly 分册同一做法：受管理的根对象 + 幂等的注入。
    ///
    /// 【本轮（英雄分配 / 材质区分 / 技能池修复）本分册的改动】
    /// 配色从"按阵营一色到底"升级为**「阵营 × 单位类型」四格配色表**：
    /// <code>
    ///                  | 英雄（EntityType.Hero） | 小兵（EntityType.Minion）
    /// 蓝方 TeamType.Player | 蓝色（含玩家英雄）      | 白色
    /// 红方 TeamType.Enemy  | 红色                   | 黑色
    /// </code>
    /// 上一版蓝方英雄与蓝方小兵同色，团战里"那一坨蓝色是 4 个小兵还是 1 个英雄"根本分不清；
    /// 现在英雄拿高饱和阵营色（主角），小兵拿无色（白 / 黑，背景兵线），一眼可辨。
    ///
    /// 【为什么配色逻辑只能有一处实现】英雄与小兵共用同一个 <see cref="TeamColorView"/> 组件，
    /// 判据是运行期读到的 (EntityBase.Team, EntityBase.EntityType)。因此本轮同步【删掉】了
    /// 工具原先在英雄实例上写的渲染器覆写（ApplyHeroBodyMaterial）——
    /// 同一件事有两个写入者时，谁生效取决于执行顺序，而且不会报任何错。
    ///
    /// 【四份材质为什么注入给两个预制体】解析是二维查表，缺任何一格都会退化成"保持原材质"，
    /// 而那正是本轮要修的"分不清谁是谁"。两个预制体各注入四份（多出来的一份永远不会被用到，
    /// 只是一次引用赋值）可以把"漏注入"这个失败模式从根上删掉。
    /// </summary>
    public static partial class AutoSceneBuilder
    {
        /// <summary>
        /// 白盒特效根对象名（进入本工具的接管清单，重复执行时先删后建）。
        /// 用独立根节点而不是挂在 Battlefield / DebugViews 上：特效是"可整体关闭的表现层"，
        /// 与逻辑对象、调试视图分开，符合"表现层可整体关闭"这条铁律的意图。
        /// </summary>
        private const string VfxRootName = "VfxRoot";

        #region 实体配色：材质资产

        /// <summary>蓝方英雄材质路径（四格表的左上格）。</summary>
        private const string BlueHeroMaterialPath = MaterialsFolder + "/HeroBlue.mat";

        /// <summary>红方英雄材质路径（右上格）。</summary>
        private const string RedHeroMaterialPath = MaterialsFolder + "/HeroRed.mat";

        /// <summary>蓝方小兵材质路径（左下格，白）。</summary>
        private const string MinionWhiteMaterialPath = MaterialsFolder + "/MinionWhite.mat";

        /// <summary>红方小兵材质路径（右下格，黑）。</summary>
        private const string MinionBlackMaterialPath = MaterialsFolder + "/MinionBlack.mat";

        /// <summary>蓝方英雄的**模型贴图**材质路径（阶段九新增，存在时优先于 HeroBlue.mat）。</summary>
        private const string HeroModelBlueMaterialPath = MaterialsFolder + "/HeroModelBlue.mat";

        /// <summary>红方英雄的模型贴图材质路径（阶段九新增）。</summary>
        private const string HeroModelRedMaterialPath = MaterialsFolder + "/HeroModelRed.mat";

        /// <summary>蓝方小兵的模型贴图材质路径（阶段九新增，存在时优先于 MinionWhite.mat）。</summary>
        private const string MinionModelBlueMaterialPath = MaterialsFolder + "/MinionModelBlue.mat";

        /// <summary>红方小兵的模型贴图材质路径（阶段九新增）。</summary>
        private const string MinionModelRedMaterialPath = MaterialsFolder + "/MinionModelRed.mat";

        /// <summary>
        /// 上一版"按阵营一色到底"留下的废弃小兵材质（蓝 / 红）。
        ///
        /// 【为什么工具要负责删掉它们】它们是上一次组装生成的派生工件（git 里都还是未跟踪状态），
        /// 语义已被本轮的四格表取代 —— 留在 Art/Materials 下就是两个"名字与用途完全误导人"的资产
        /// （日后有人看到 MinionBlue.mat，第一反应会是"小兵不是该用白色的吗"）。
        /// 删除时机严格排在"预制体已经改引新材质"之后，因此不存在把在用引用删掉的可能。
        /// </summary>
        private static readonly string[] ObsoleteMinionMaterialPaths =
        {
            MaterialsFolder + "/MinionBlue.mat",
            MaterialsFolder + "/MinionRed.mat"
        };

        /// <summary>蓝方英雄颜色（蓝）。高饱和：英雄是战场主角，必须一眼抓住。</summary>
        private static readonly Color BlueHeroColor = new Color(0.25f, 0.55f, 0.95f, 1f);

        /// <summary>红方英雄颜色（红）。</summary>
        private static readonly Color RedHeroColor = new Color(0.9f, 0.3f, 0.3f, 1f);

        /// <summary>蓝方小兵颜色（白）。刻意用纯白：兵线是"背景板"，越素净越不会抢英雄的注意力。</summary>
        private static readonly Color WhiteMinionColor = new Color(1f, 1f, 1f, 1f);

        /// <summary>
        /// 红方小兵颜色（黑）。
        ///
        /// 【为什么取 0.05 而不是纯黑 (0, 0, 0)】纯黑在浅灰的桥面上会与"材质丢失 / 渲染异常"的观感混淆
        /// （Unity 的缺失材质是洋红，但被压到全黑时人第一反应仍然是"这里是不是坏了"）。
        /// 0.05 在肉眼上就是黑色，同时保留一点点漫反射，顶视角下能看清胶囊体的圆角与朝向。
        /// </summary>
        private static readonly Color BlackMinionColor = new Color(0.05f, 0.05f, 0.05f, 1f);

        /// <summary>
        /// 实体四格配色（阵营 × 单位类型）的材质集合。
        ///
        /// 【为什么用一个结构体而不是四个参数】四个材质总是一起产生、一起消费（都是
        /// <see cref="EnsureEntityColorMaterials"/> 的产物，又都要注入给两个预制体）；
        /// 拆成四个参数会让 CreateHeroPrefab / BuildWhiteboxVfx 的签名继续膨胀，
        /// 也让"少传一个"变成一个编译器不报错的失误。
        /// </summary>
        private readonly struct EntityColorSet
        {
            /// <summary>蓝方英雄材质（蓝）。</summary>
            public readonly Material AllyHero;

            /// <summary>红方英雄材质（红）。</summary>
            public readonly Material EnemyHero;

            /// <summary>蓝方小兵材质（白）。</summary>
            public readonly Material AllyMinion;

            /// <summary>红方小兵材质（黑）。</summary>
            public readonly Material EnemyMinion;

            public EntityColorSet(Material allyHero, Material enemyHero, Material allyMinion, Material enemyMinion)
            {
                AllyHero = allyHero;
                EnemyHero = enemyHero;
                AllyMinion = allyMinion;
                EnemyMinion = enemyMinion;
            }

            /// <summary>四格是否都已就绪。缺任何一格都意味着"某类单位会保持原材质"。</summary>
            public bool IsComplete => AllyHero != null && EnemyHero != null && AllyMinion != null && EnemyMinion != null;

            /// <summary>拼成一行可读文本，仅用于日志。</summary>
            public string Describe()
            {
                return $"蓝英雄 {DescribeMaterial(AllyHero)} / 红英雄 {DescribeMaterial(EnemyHero)} / " +
                       $"蓝小兵 {DescribeMaterial(AllyMinion)} / 红小兵 {DescribeMaterial(EnemyMinion)}";
            }

            private static string DescribeMaterial(Material material)
            {
                return material != null ? material.name : "（缺失）";
            }
        }

        /// <summary>
        /// 取（必要时创建）四格配色材质。四份都是确定性资产（颜色由本工具定义），
        /// 因此与 Q/W/E/R 技能资产同一策略：缺失才创建、已存在一律沿用（策划可在 Inspector 里微调色值）。
        /// </summary>
        /// <param name="notes">组装说明收集器。</param>
        /// <returns>四格材质集合（任一份创建失败时为 null 字段，由调用方决定是否继续）。</returns>
        private static EntityColorSet EnsureEntityColorMaterials(List<string> notes)
        {
            Material allyHero = ResolveEntityColorMaterial(
                HeroModelBlueMaterialPath, BlueHeroMaterialPath, BlueHeroColor, 0.1f,
                "蓝方英雄材质（蓝，含玩家英雄）", notes);

            Material enemyHero = ResolveEntityColorMaterial(
                HeroModelRedMaterialPath, RedHeroMaterialPath, RedHeroColor, 0.1f,
                "红方英雄材质（红）", notes);

            Material allyMinion = ResolveEntityColorMaterial(
                MinionModelBlueMaterialPath, MinionWhiteMaterialPath, WhiteMinionColor, 0.1f,
                "蓝方小兵材质（白 / 模型贴图）", notes);

            Material enemyMinion = ResolveEntityColorMaterial(
                MinionModelRedMaterialPath, MinionBlackMaterialPath, BlackMinionColor, 0.1f,
                "红方小兵材质（黑 / 模型贴图）", notes);

            EntityColorSet colors = new EntityColorSet(allyHero, enemyHero, allyMinion, enemyMinion);

            if (!colors.IsComplete)
            {
                // 材质创建失败只可能是"取不到 Standard 着色器"（EnsureColorMaterial 已告警）。
                // 此时绝不能往下注入：把 null 写进 TeamColorView 会覆盖掉预制体上可能已有的正确引用，
                // 让"颜色不对"从"暂时的"变成"确定坏的"。
                Debug.LogWarning(
                    "[AutoSceneBuilder] 实体配色材质未能全部创建，预制体上的配色注入已跳过。" +
                    "这不影响任何对局逻辑（只是外观退化），修好着色器后重新执行本菜单即可。");
                return colors;
            }

            notes.Add($"{MaterialsFolder} 四格配色材质已就绪（{colors.Describe()}）");
            return colors;
        }

        /// <summary>
        /// 取一格配色材质：**模型贴图材质优先，纯色材质兜底**。
        ///
        /// 【为什么需要这个优先级（阶段九）】四格配色原本是四份纯色材质（蓝 / 红 / 白 / 黑），
        /// 那是白盒胶囊体时代的产物 —— 单位没有贴图，只能靠颜色区分。导入真实模型后，
        /// 把纯色材质盖到模型上会把贴图整个抹掉（TeamColorView 写的是 sharedMaterial，
        /// 一写就是整个材质的替换），模型会变成一坨纯色。
        ///
        /// 因此约定：`Assets/Art/Materials/` 下若存在 `HeroModelBlue/Red.mat`、`MinionModelBlue/Red.mat`
        /// （带贴图的材质），就用它们；否则退回原来那四份纯色材质。
        /// 与"模型槽位"是同一套策略：**资产在就接入，不在就保持白盒形态**，不需要改代码。
        ///
        /// 【为什么不直接改纯色材质的贴图】那会让"纯色兜底"这条退路消失 ——
        /// 模型没导入时纯色材质是唯一能区分敌我的手段，两者必须能各自独立存在。
        /// </summary>
        /// <param name="modelMaterialPath">模型贴图材质路径（优先）。</param>
        /// <param name="solidMaterialPath">纯色材质路径（兜底，缺失才创建）。</param>
        /// <param name="solidColor">纯色材质颜色。</param>
        /// <param name="glossiness">纯色材质光泽度。</param>
        /// <param name="label">中文标签（日志用）。</param>
        /// <param name="notes">组装说明收集器。</param>
        /// <returns>可用的材质；两者都拿不到时为 null。</returns>
        private static Material ResolveEntityColorMaterial(
            string modelMaterialPath,
            string solidMaterialPath,
            Color solidColor,
            float glossiness,
            string label,
            List<string> notes)
        {
            Material modelMaterial = AssetDatabase.LoadAssetAtPath<Material>(modelMaterialPath);

            if (modelMaterial != null)
            {
                notes.Add($"{label} ← {modelMaterialPath}（模型贴图材质）");
                return modelMaterial;
            }

            return EnsureColorMaterial(solidMaterialPath, solidColor, glossiness, label + "（纯色兜底）", notes);
        }

        #endregion

        /// <summary>
        /// 装配白盒视觉反馈：场景级占位特效管理器 `VfxRoot`。
        ///
        /// 【实体配色为什么不在本步骤做】四格配色的注入必须在【步骤 15 之后、实例化之前】完成
        /// （英雄预制体要在 CreateHeroPrefab 里就地注入，小兵预制体紧随其后，两者用的是同一套材质）。
        /// 上一版把"小兵预制体的注入"放在本步骤（步骤 19），结果是：
        /// 英雄预制体在步骤 15 注入、小兵预制体在步骤 19 注入 —— 同一件事分在两个相隔很远的步骤里，
        /// 而"读回校验"无论放在哪一边都会漏掉另一边。现在两者都收在步骤 15，校验收在步骤 16。
        ///
        /// 【为什么排在步骤 18（调试视图）之后】本步骤不参与任何逻辑，也不被其它步骤依赖；
        /// 放在最后可以保证"战场 / UI / 调试视图都已成型"之后再挂表现层，出问题时的排查顺序更直观。
        ///
        /// 【幂等性】根对象在 ManagedRootNames 里，重复执行先删后建。
        /// </summary>
        /// <param name="notes">组装说明收集器。</param>
        private static void BuildWhiteboxVfx(List<string> notes)
        {
            GameObject root = CreateRootObject(VfxRootName, Vector3.zero);
            WhiteboxVfxManager manager = Undo.AddComponent<WhiteboxVfxManager>(root);

            // ---- 阶段九：白盒占位特效置为禁用 ----
            // 【为什么是"禁用"而不是"删除对象"】README §2.1.6 把 VfxRoot 定义为"表现层的权宜实现，
            // 美术接入后整体删除"。阶段九的正式特效层（VfxSpawnerRoot）已经就位，两套并存会重复播放，
            // 因此这里把白盒管理器关掉。保留对象与组件（而不是删掉）有两个理由：
            //   ① 正式特效预制体尚未导入，VfxSpawner 三个槽位是空的 —— 万一需要临时回退观察技能反馈，
            //      把 enableVfx 勾回来即可，不必重跑组装；
            //   ② 组装工具对"工具生成的对象"一律走"先删后建"的幂等路径，保留一个可复现的禁用状态
            //      比"这次删、下次不建"更容易推理（工具的清理清单里没有它，就不会出现半套残留）。
            bool changed = AssignBool(manager, "enableVfx", false);

            notes.Add(
                $"{VfxRootName} 已装配但**已禁用**（WhiteboxVfxManager.enableVfx = false{(changed ? "，本次写入" : "，已是禁用态")}）" +
                "—— 白盒占位特效让位给正式特效层 VfxSpawnerRoot（五个粒子预制体已注入，技能画面反馈已接通）。" +
                "正式特效资源完备后本对象应整体删除。");
        }

        /// <summary>
        /// 把四格配色注入【小兵预制体】，并清理上一版的废弃材质。
        ///
        /// 英雄预制体的注入在 CreateHeroPrefab 里就地完成（那时它还是个临时对象，直接写字段最省事）。
        /// 两者必须在【实例化英雄 / 出兵之前】都完成，理由见 BuildWhiteboxVfx 的说明。
        /// </summary>
        /// <param name="minionPrefab">小兵预制体资产。</param>
        /// <param name="colors">四格配色材质。</param>
        /// <param name="notes">组装说明收集器。</param>
        private static void ApplyEntityColorsToMinionPrefab(
            GameObject minionPrefab, EntityColorSet colors, List<string> notes)
        {
            if (!colors.IsComplete)
            {
                // 材质没齐时绝不往下注入：把 null 写进 TeamColorView 会把"颜色不对"从"暂时的"变成"确定坏的"。
                // EnsureEntityColorMaterials 已经报过告警，这里不重复刷屏。
                return;
            }

            if (PatchPrefabTeamColor(minionPrefab, colors, "小兵预制体", notes))
            {
                // 只有"预制体确实已经改引新材质"之后才动删除（见 ObsoleteMinionMaterialPaths 的说明）。
                RemoveObsoleteMinionMaterials(notes);
            }
        }

        /// <summary>
        /// 删除上一版遗留的废弃小兵材质（蓝 / 红）。幂等：不存在时什么都不做、也不报错。
        /// </summary>
        /// <param name="notes">组装说明收集器。</param>
        private static void RemoveObsoleteMinionMaterials(List<string> notes)
        {
            for (int i = 0; i < ObsoleteMinionMaterialPaths.Length; i++)
            {
                string assetPath = ObsoleteMinionMaterialPaths[i];

                if (AssetDatabase.LoadAssetAtPath<Material>(assetPath) == null)
                {
                    continue;
                }

                if (AssetDatabase.DeleteAsset(assetPath))
                {
                    notes.Add($"{assetPath} 已删除（上一版「按阵营一色到底」的遗留材质，已被四格配色取代）");
                }
                else
                {
                    Debug.LogWarning(
                        $"[AutoSceneBuilder] 废弃材质 {assetPath} 删除失败（可能有引用锁定），" +
                        "它已不再被任何预制体引用，可手工删除。");
                }
            }
        }

        /// <summary>
        /// 给一个单位预制体挂上 <see cref="TeamColorView"/> 并注入四格配色材质。
        ///
        /// 【英雄预制体走的是另一条路】它在 CreateHeroPrefab 里就地 AddComponent + 注入
        /// （那时它还是个临时对象，直接写字段最省事，也避免"预制体先建好、实例再被创建"之间
        /// 隔着一次预制体资产重写）。本方法服务的是【已经存在于磁盘上】的小兵预制体。
        ///
        /// 【为什么走 LoadPrefabContents / SaveAsPrefabAsset】与 PatchMinionPrefabTuning 同一理由：
        /// 直接对预制体资产上的组件做 SerializedObject 修改，在部分引擎版本下不会稳定落盘；
        /// 官方推荐路径是"加载预制体内容 → 改 → 存回"。
        ///
        /// 【幂等】组件已存在、且四个材质引用都已经等于目标值时，一个字节都不写盘。
        /// 判据必须逐字段比较（而不是复用 AssignObjectReference 的返回值）：
        /// 那个辅助方法在"字段存在"时就返回 true，用它做判据会导致每次组装都判定为"有改动"从而无条件写盘。
        /// </summary>
        /// <param name="prefab">目标预制体资产。</param>
        /// <param name="colors">四格配色材质。</param>
        /// <param name="prefabLabel">预制体的中文标签（仅用于日志）。</param>
        /// <param name="notes">组装说明收集器。</param>
        /// <returns>预制体上确实存在（或刚刚写入）正确的四格配色返回 true；无法处理时返回 false。</returns>
        private static bool PatchPrefabTeamColor(
            GameObject prefab, EntityColorSet colors, string prefabLabel, List<string> notes)
        {
            if (prefab == null)
            {
                return false;
            }

            string prefabPath = AssetDatabase.GetAssetPath(prefab);

            if (string.IsNullOrEmpty(prefabPath) ||
                !prefabPath.EndsWith(".prefab", System.StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            GameObject contents = PrefabUtility.LoadPrefabContents(prefabPath);

            try
            {
                List<string> changes = new List<string>();
                List<string> missingFields = new List<string>();

                TeamColorView view = contents.GetComponent<TeamColorView>();

                if (view == null)
                {
                    // 用 AddComponent 而不是 Undo.AddComponent：这里的对象是"预制体内容"的临时副本，
                    // 撤销栈对它没有意义 —— 真正的落盘动作是下面的 SaveAsPrefabAsset。
                    view = contents.AddComponent<TeamColorView>();
                    changes.Add("新增 TeamColorView（运行期按 EntityBase 的 Team × EntityType 给身体换色）");
                }

                SerializedObject serialized = new SerializedObject(view);

                WriteColorField(serialized, "allyHeroMaterial", colors.AllyHero, "蓝方英雄", changes, missingFields);
                WriteColorField(serialized, "enemyHeroMaterial", colors.EnemyHero, "红方英雄", changes, missingFields);
                WriteColorField(serialized, "allyMinionMaterial", colors.AllyMinion, "蓝方小兵", changes, missingFields);
                WriteColorField(serialized, "enemyMinionMaterial", colors.EnemyMinion, "红方小兵", changes, missingFields);

                // 【关键：把"字段不存在"与"值已经是目标值"区分开】
                // 两者都会让 changes 为空，但含义完全相反：前者是注入【失败】（脚本字段被改名了），
                // 后者才是幂等成功。上一版把两者合并成"return true"，于是字段名一旦对不上，
                // 工具会一边报字段不存在的错误、一边声称"配色已就绪"，还会继续去删废弃材质。
                if (missingFields.Count > 0)
                {
                    Debug.LogError(
                        $"[AutoSceneBuilder] {prefabPath}（{prefabLabel}）的四格配色注入失败：" +
                        $"TeamColorView 上找不到字段 {string.Join("、", missingFields)}（字段可能已被改名）。" +
                        "该类单位会保持预制体自带材质 —— 请同步修改本工具的字段名与 TeamColorView 的声明。");
                    return false;
                }

                if (changes.Count == 0)
                {
                    // 组件与四个材质都已是设计值：不写盘，保持幂等（重复执行不产生任何资产改动）。
                    return true;
                }

                serialized.ApplyModifiedProperties();

                PrefabUtility.SaveAsPrefabAsset(contents, prefabPath);
                AssetDatabase.SaveAssets();

                notes.Add($"{prefabPath}（{prefabLabel}）已写入四格配色：{string.Join("；", changes)}");
                return true;
            }
            catch (System.Exception exception)
            {
                Debug.LogError(
                    $"[AutoSceneBuilder] 写入{prefabLabel} {prefabPath} 的四格配色时出错，" +
                    $"该类单位可能仍保持原材质：{exception.Message}");
                return false;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        /// <summary>
        /// 写一个配色字段：值不同就写进 changes，字段不存在就写进 missingFields。
        /// 抽出来的原因与其它 "Append*/Write*" 辅助方法一致：四个字段的逻辑完全一致，
        /// 逐个写四遍容易在复制时把字段名或标签写错，而这类错误恰恰是本轮要防的"静默失效"。
        /// </summary>
        /// <param name="serialized">预制体内容上的组件包装。</param>
        /// <param name="fieldName">字段名。</param>
        /// <param name="value">目标材质。</param>
        /// <param name="label">中文标签（日志用）。</param>
        /// <param name="changes">改动记录。</param>
        /// <param name="missingFields">缺失字段记录。</param>
        private static void WriteColorField(
            SerializedObject serialized,
            string fieldName,
            Material value,
            string label,
            List<string> changes,
            List<string> missingFields)
        {
            bool fieldFound;
            bool changed = SetObjectReferenceIfDifferent(serialized, fieldName, value, out fieldFound);

            if (!fieldFound)
            {
                missingFields.Add(fieldName);
                return;
            }

            if (changed)
            {
                changes.Add($"{fieldName} = {DescribeMaterialName(value)}（{label}）");
            }
        }

        /// <summary>取材质名用于日志；引用为空时返回「（空）」，避免拼出 "= " 这种读不出结论的文本。</summary>
        /// <param name="material">材质引用，允许为 null。</param>
        /// <returns>材质名或占位文本。</returns>
        private static string DescribeMaterialName(Material material)
        {
            return material != null ? material.name : "（空）";
        }

        /// <summary>
        /// 实体配色注入的**读回校验**（步骤 16 收尾）。
        ///
        /// 【为什么必须有这一步 —— 这是被实机打回逼出来的第二道保险】
        /// 上一轮"红方小兵不是黑色"的真凶是：`TeamColorView` 的字段改名后，
        /// `MinionPrefab.prefab` 里存的还是旧字段名，Unity 反序列化时静默丢弃 → 四个新字段全为 null →
        /// 运行期判定"没有配色"→ 保持原材质（双方小兵同色）。而当时工具**没有任何一步**去读回
        /// 预制体上的实际字段值，于是"注入没生效"这件事在组装日志里也看不出来。
        ///
        /// 本方法按项目既定的"读回序列化字段做确定性校验"套路，对【英雄预制体 + 小兵预制体】各核四格：
        ///   ① 组件是否存在；② 四个字段是否存在（改名了会在这里暴露）；③ 引用是否为空；
        ///   ④ 引用是否就是本次组装用的那份材质。
        /// 全部通过才输出结论；否则 LogError 点名到"哪个预制体的哪个字段"。
        /// </summary>
        /// <param name="heroPrefab">英雄预制体资产。</param>
        /// <param name="minionPrefab">小兵预制体资产。</param>
        /// <param name="colors">本次组装使用的四格配色材质。</param>
        private static void ValidateEntityColorInjection(
            GameObject heroPrefab, GameObject minionPrefab, EntityColorSet colors)
        {
            List<string> problems = new List<string>();

            AppendPrefabColorProblems(problems, heroPrefab, "英雄预制体", colors);
            AppendPrefabColorProblems(problems, minionPrefab, "小兵预制体", colors);

            if (problems.Count == 0)
            {
                Debug.Log(
                    "[AutoSceneBuilder] 实体配色校验通过：英雄预制体与小兵预制体上的四格材质均已注入" +
                    $"（{colors.Describe()}）；运行期 TeamColorView 会按「阵营 × 单位类型」换色，" +
                    "蓝英雄蓝 / 红英雄红 / 蓝小兵白 / 红小兵黑。");
                return;
            }

            Debug.LogError(
                "[AutoSceneBuilder] 实体配色校验失败（实机症状：双方小兵 / 英雄同色，团战分不清敌我）：\n  · " +
                string.Join("\n  · ", problems));
        }

        /// <summary>核对一个预制体上的四格配色字段，把问题追加进清单。</summary>
        /// <param name="problems">问题清单。</param>
        /// <param name="prefab">目标预制体。</param>
        /// <param name="label">中文标签（日志用）。</param>
        /// <param name="colors">期望的四格材质。</param>
        private static void AppendPrefabColorProblems(
            List<string> problems, GameObject prefab, string label, EntityColorSet colors)
        {
            if (prefab == null)
            {
                problems.Add($"{label}不存在（无法校验配色）");
                return;
            }

            TeamColorView view = prefab.GetComponent<TeamColorView>();
            if (view == null)
            {
                problems.Add($"{label}（{prefab.name}）上没有 TeamColorView，运行期不会有任何阵营配色");
                return;
            }

            AppendColorFieldProblem(problems, view, label, "allyHeroMaterial", colors.AllyHero);
            AppendColorFieldProblem(problems, view, label, "enemyHeroMaterial", colors.EnemyHero);
            AppendColorFieldProblem(problems, view, label, "allyMinionMaterial", colors.AllyMinion);
            AppendColorFieldProblem(problems, view, label, "enemyMinionMaterial", colors.EnemyMinion);
        }

        /// <summary>核对单个配色字段：存在、非空、且等于期望材质。</summary>
        /// <param name="problems">问题清单。</param>
        /// <param name="view">预制体上的配色组件。</param>
        /// <param name="label">中文标签。</param>
        /// <param name="fieldName">字段名。</param>
        /// <param name="expected">期望材质。</param>
        private static void AppendColorFieldProblem(
            List<string> problems, TeamColorView view, string label, string fieldName, Material expected)
        {
            // 这里刻意不用 FindProperty 辅助方法：它会直接打一条 LogError，
            // 而本方法要的是"汇总成一条可读的结论"，重复报错只会让 Console 更乱。
            SerializedProperty property = new SerializedObject(view).FindProperty(fieldName);

            if (property == null)
            {
                problems.Add($"{label}：TeamColorView 上找不到字段 {fieldName}（字段可能已被改名）");
                return;
            }

            Material actual = property.objectReferenceValue as Material;

            if (actual == null)
            {
                problems.Add($"{label}.{fieldName} 为空（该类单位会保持预制体自带材质 = 分不清敌我）");
                return;
            }

            if (expected != null && actual != expected)
            {
                problems.Add($"{label}.{fieldName} = {actual.name}，应为 {expected.name}");
            }
        }

        /// <summary>
        /// 把对象引用字段写成目标值；已经是目标值时返回 false（不产生任何修改）。
        ///
        /// 字段不存在时报错并返回 false —— 与 Assign* 系列同一口径：字段名写错必须立刻暴露，
        /// 否则表现层的注入会静默失效（症状是"颜色还是不对"，而 Console 一片安静）。
        /// </summary>
        /// <param name="serialized">目标对象的序列化包装（由调用方统一提交）。</param>
        /// <param name="fieldName">字段名。</param>
        /// <param name="value">目标引用，允许为 null。</param>
        /// <param name="fieldFound">输出：该字段在类型上是否存在。false 表示"这次调用什么都没写"。</param>
        /// <returns>确实发生了修改返回 true。</returns>
        private static bool SetObjectReferenceIfDifferent(
            SerializedObject serialized, string fieldName, UnityEngine.Object value, out bool fieldFound)
        {
            fieldFound = false;

            SerializedProperty property = FindProperty(serialized, fieldName);

            if (property == null)
            {
                // 字段不存在：FindProperty 已经报过一条明确的错误（含"字段可能已被改名"的提示），
                // 这里只返回 false，避免同一问题打两条日志。
                return false;
            }

            fieldFound = true;

            if (property.propertyType != SerializedPropertyType.ObjectReference)
            {
                Debug.LogError(
                    $"[AutoSceneBuilder] {serialized.targetObject.GetType().Name}.{fieldName} " +
                    "不是对象引用字段，注入失败（请检查字段类型是否与脚本一致）。");
                fieldFound = false;
                return false;
            }

            if (property.objectReferenceValue == value)
            {
                return false;
            }

            property.objectReferenceValue = value;
            return true;
        }
    }
}
