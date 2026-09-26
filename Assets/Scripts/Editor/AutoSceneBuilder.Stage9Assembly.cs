using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using MOBA.Components;
using MOBA.Core;
using MOBA.Units;
using MOBA.VFX;

namespace MOBA.Editor
{
    /// <summary>
    /// AutoSceneBuilder 的「表现层基建装配」分册（阶段九新增）。
    ///
    /// ==================== 本分册只做三件事 ====================
    /// ① **动画控制器**：用代码生成 <c>Assets/Art/Animations/UnitAnimator.controller</c>，
    ///    含 Idle / Run / Attack / Spell / Die 五个状态与 Speed / Attack / Spell / Die 四个参数，
    ///    以及 Idle ↔ Run（由 Speed 驱动）与 AnyState → Attack / Spell / Die 的过渡。
    ///    **本阶段刻意不挂任何动画片段** —— 控制器是"连线骨架"，美术资源导入后再把片段挂到状态上。
    /// ② **预制体接线**：给英雄 / 小兵预制体挂 Animator + <see cref="AnimationComponent"/>，
    ///    注入控制器，并把 Animator 的根运动关掉（位移权威永远是 NavMeshAgent）。
    /// ③ **预留真实模型槽位**：<c>Assets/Art/Models/**</c> 下的模型预制体若存在就注入
    ///    <see cref="AnimationComponent"/> 的 modelPrefab 字段；不存在则明确报告"槽位已预留"。
    ///    本阶段**不替换白盒外观**（模型尚未导入），因此不做任何外观改动。
    ///
    /// ==================== 为什么控制器用代码生成，而不是让美术手工建 ====================
    /// README §6.1 铁律 1：所有装配必须通过 Editor 脚本完成，手工在 Inspector 里连的线不算交付
    /// （场景重建即丢失）。控制器虽然是个资产，但它承载的"逻辑状态 → 动画参数"映射是**架构约定**
    /// （参数名必须与 <see cref="AnimationComponent"/> 的字段一致、Idle ↔ Run 必须由 Speed 驱动），
    /// 手工建等于把架构约定放进一个随时可能被误改、且没有任何校验的资产里。
    /// 生成器保证「一键组装后控制器一定存在且参数齐全」，并把名字集中成常量（改一处即全改）。
    ///
    /// ==================== 幂等性 ====================
    /// 与项目其它装配步骤同一策略：**缺失才创建、已存在一律沿用**，但会补齐缺失的参数 / 状态 / 过渡
    /// （只做加法，绝不删除或改写美术已经调好的值）。因此重复执行不会产生资产 diff，
    /// 也不会把美术调整过的过渡时长打回去。
    /// </summary>
    public static partial class AutoSceneBuilder
    {
        #region 常量：动画

        /// <summary>动画资产目录（阶段九新增）。</summary>
        private const string AnimationsFolder = "Assets/Art/Animations";

        /// <summary>单位动画控制器资产路径（英雄与小兵共用一份）。</summary>
        private const string UnitAnimatorControllerPath = AnimationsFolder + "/UnitAnimator.controller";

        /// <summary>Animator 参数名：移动速度（float）。必须与 AnimationComponent 的默认字段值一致。</summary>
        private const string AnimSpeedParameter = "Speed";

        /// <summary>Animator 参数名：普攻触发（trigger）。</summary>
        private const string AnimAttackTrigger = "Attack";

        /// <summary>Animator 参数名：施法触发（trigger）。</summary>
        private const string AnimSpellTrigger = "Spell";

        /// <summary>Animator 参数名：死亡触发（trigger）。</summary>
        private const string AnimDieTrigger = "Die";

        /// <summary>动画状态名：待机。</summary>
        private const string AnimStateIdle = "Idle";

        /// <summary>动画状态名：移动。</summary>
        private const string AnimStateRun = "Run";

        /// <summary>动画状态名：攻击。</summary>
        private const string AnimStateAttack = "Attack";

        /// <summary>动画状态名：施法。</summary>
        private const string AnimStateSpell = "Spell";

        /// <summary>动画状态名：死亡。</summary>
        private const string AnimStateDie = "Die";

        /// <summary>Idle ↔ Run 的速度阈值（归一化后）。与 AnimationComponent.speedDeadZone 取同值，避免两处打架。</summary>
        private const float AnimIdleRunThreshold = 0.1f;

        /// <summary>Idle ↔ Run 的过渡时长（秒）。README §2.2.2 明确要求"约 0.1 秒、不硬切"。</summary>
        private const float AnimIdleRunTransitionDuration = 0.1f;

        /// <summary>进入攻击 / 施法状态的过渡时长（秒）。短促，让出手动作"跟手"。</summary>
        private const float AnimActionEnterDuration = 0.05f;

        /// <summary>攻击 / 施法状态回到 Idle 的过渡时长（秒）。</summary>
        private const float AnimActionReturnDuration = 0.1f;

        /// <summary>攻击 / 施法状态回到 Idle 的退出时刻（归一化）。挂上片段后即按它收尾。</summary>
        private const float AnimActionReturnExitTime = 0.9f;

        #endregion

        #region 常量：预留的美术资产槽位

        /// <summary>模型资产根目录（阶段九预留；工具会创建这些空目录，让美术知道模型该放哪）。</summary>
        private const string ModelsFolder = "Assets/Art/Models";

        /// <summary>英雄模型目录（预留）。</summary>
        private const string HeroModelFolder = ModelsFolder + "/Heroes";

        /// <summary>小兵模型目录（预留）。</summary>
        private const string MinionModelFolder = ModelsFolder + "/Minions";

        /// <summary>建筑模型目录（预留）。</summary>
        private const string BuildingModelFolder = ModelsFolder + "/Buildings";

        /// <summary>英雄模型预制体路径（预留槽位）。把模型放到这里、重新执行一键组装即自动接入。</summary>
        private const string HeroModelPrefabPath = HeroModelFolder + "/HeroModel.prefab";

        /// <summary>小兵模型预制体路径（预留槽位）。</summary>
        private const string MinionModelPrefabPath = MinionModelFolder + "/MinionModel.prefab";

        /// <summary>
        /// 真实模型在单位预制体里的子节点名。
        /// 固定名字是"幂等"的前提：工具靠它判断"模型是不是已经挂过了"，否则每次组装都会多叠一层模型。
        /// </summary>
        private const string ModelChildName = "Model";

        /// <summary>受击特效预制体路径（预留槽位）。</summary>
        private const string HitVfxPrefabPath = VfxPrefabsFolder + "/HitVfx.prefab";

        /// <summary>施法特效预制体路径（预留槽位）。</summary>
        private const string CastVfxPrefabPath = VfxPrefabsFolder + "/CastVfx.prefab";

        /// <summary>弹道命中特效预制体路径（预留槽位）。</summary>
        private const string ProjectileHitVfxPrefabPath = VfxPrefabsFolder + "/ProjectileHitVfx.prefab";

        /// <summary>护盾特效预制体路径（预留槽位）。</summary>
        private const string ShieldVfxPrefabPath = VfxPrefabsFolder + "/ShieldVfx.prefab";

        /// <summary>范围场特效预制体路径（预留槽位）。</summary>
        private const string ZoneVfxPrefabPath = VfxPrefabsFolder + "/ZoneVfx.prefab";

        /// <summary>
        /// 场景级正式特效层的根对象名（进入本工具接管清单，重复执行先删后建）。
        /// 与白盒期的 <c>VfxRoot</c> 并列：两者是"正式资源"与"占位资源"的关系，
        /// 三个槽位为空时 VfxSpawner 完全惰性，因此现在并存不会播两套特效。
        /// </summary>
        private const string VfxSpawnerRootName = "VfxSpawnerRoot";

        /// <summary>防御塔模型预制体路径（预留槽位）。</summary>
        private const string TowerModelPrefabPath = BuildingModelFolder + "/Tower.prefab";

        /// <summary>基地（水晶）模型预制体路径（预留槽位）。</summary>
        private const string NexusModelPrefabPath = BuildingModelFolder + "/Nexus.prefab";

        /// <summary>防御塔模型材质（蓝 / 红）。</summary>
        private const string TowerModelBlueMaterialPath = MaterialsFolder + "/Building_Tower_Blue.mat";

        /// <summary>防御塔模型材质（红）。</summary>
        private const string TowerModelRedMaterialPath = MaterialsFolder + "/Building_Tower_Red.mat";

        /// <summary>基地模型材质（蓝）。</summary>
        private const string NexusModelBlueMaterialPath = MaterialsFolder + "/Building_Nexus_Blue.mat";

        /// <summary>基地模型材质（红）。</summary>
        private const string NexusModelRedMaterialPath = MaterialsFolder + "/Building_Nexus_Red.mat";

        #endregion

        #region 程序化动画片段（阶段九：消除 T-pose）

        /// <summary>
        /// 动画片段资产目录与文件名。
        ///
        /// 【为什么是"程序化生成"而不是导入美术片段】
        /// 本机与可用来源都拿不到可用的骨骼动画：LoL 的 `.anm` 是**骨骼空间**数据，
        /// 要变成 Unity 的 `AnimationClip` 需要完整的骨骼层级 + 蒙皮 + 动画重定向（远超一轮任务）；
        /// Mixamo 需要 Adobe 账号登录（无法自动化）。而 T-pose 站立是实机上最刺眼的问题，
        /// 因此这里用代码生成一组**占位动作片段**：它们不驱动骨骼，而是驱动 `Model` 子节点的
        /// Transform（呼吸起伏 / 奔跑弹跳 + 前倾 / 攻击前冲 / 施法上浮 / 死亡前扑）。
        ///
        /// 【为什么绑定路径是 Model】动画绑定路径是相对 Animator 所在对象（单位根节点）的。
        /// 单位根节点的位移权威是 NavMeshAgent，**绝不能**被动画驱动（会与寻路争夺同一个 Transform）；
        /// 而 `Model` 只是外观子节点，动画它 100% 是纯表现，不影响任何逻辑。
        /// 英雄与小兵的模型子节点同名（都是 Model），因此同一套片段对两者都生效。
        ///
        /// 【与真美术片段的关系】美术片段导入后，把片段拖到对应状态上即可 ——
        /// 本工具的注入规则是"**状态的 motion 为空才写**"，因此不会覆盖美术的连线。
        /// </summary>
        private static readonly (string FileName, float Length, bool Loop)[] AnimationClipSpecs =
        {
            ("Idle",   2.00f, true),
            ("Run",    0.50f, true),
            ("Attack", 0.35f, false),
            ("Spell",  0.60f, false),
            ("Die",    1.20f, false)
        };

        /// <summary>
        /// 确保五个动画片段资产存在（缺失才创建，已存在一律沿用）。
        /// </summary>
        /// <param name="notes">组装说明收集器。</param>
        private static void EnsureAnimationClips(List<string> notes)
        {
            EnsureFolderRecursive(AnimationsFolder);

            List<string> created = new List<string>();

            for (int i = 0; i < AnimationClipSpecs.Length; i++)
            {
                string fileName = AnimationClipSpecs[i].FileName;
                float length = AnimationClipSpecs[i].Length;
                bool loop = AnimationClipSpecs[i].Loop;
                string path = AnimationsFolder + "/" + fileName + ".anim";

                if (AssetDatabase.LoadAssetAtPath<AnimationClip>(path) != null)
                {
                    continue;
                }

                AnimationClip clip = BuildProceduralClip(fileName, length, loop);

                if (clip == null)
                {
                    continue;
                }

                AssetDatabase.CreateAsset(clip, path);
                created.Add(fileName);
            }

            AssetDatabase.SaveAssets();

            if (created.Count > 0)
            {
                notes.Add(
                    $"{AnimationsFolder} 已生成占位动画片段：{string.Join(" / ", created)}" +
                    "（驱动 Model 子节点的 Transform —— 呼吸起伏 / 奔跑弹跳前倾 / 攻击前冲 / 施法上浮 / 死亡前扑；" +
                    "**不驱动骨骼**，导入真美术片段后拖到对应状态即可覆盖）");
            }
        }

        /// <summary>
        /// 按名字生成一个占位动画片段。
        /// </summary>
        /// <param name="clipName">片段名（同时决定动作内容）。</param>
        /// <param name="length">片段时长（秒）。</param>
        /// <param name="loop">是否循环。</param>
        /// <returns>生成的片段；名字不认识时返回 null。</returns>
        private static AnimationClip BuildProceduralClip(string clipName, float length, bool loop)
        {
            AnimationClip clip = new AnimationClip
            {
                name = clipName,
                frameRate = 30f
            };

            switch (clipName)
            {
                case "Idle":
                    // 呼吸：上下起伏 + 极轻微左右摇摆。
                    SetCurve(clip, "localPosition.y", 0f, 0f, 1.0f, 0.03f, 2.0f, 0f);
                    SetCurve(clip, "localEulerAnglesRaw.y", 0f, 0f, 1.0f, 4f, 2.0f, 0f);
                    break;

                case "Run":
                    // 奔跑：更快的弹跳 + 持续前倾（前倾是常驻值，用两个等值关键帧表达）。
                    SetCurve(clip, "localPosition.y", 0f, 0f, 0.125f, 0.09f, 0.25f, 0f, 0.375f, 0.09f, 0.5f, 0f);
                    SetCurve(clip, "localEulerAnglesRaw.x", 0f, 9f, 0.5f, 9f);
                    break;

                case "Attack":
                    // 攻击：向前冲刺一下再收回，同时上身前倾。
                    SetCurve(clip, "localPosition.z", 0f, 0f, 0.12f, 0.3f, 0.35f, 0f);
                    SetCurve(clip, "localEulerAnglesRaw.x", 0f, 0f, 0.12f, 18f, 0.35f, 0f);
                    break;

                case "Spell":
                    // 施法：上浮 + 转身，像在吟唱。
                    SetCurve(clip, "localPosition.y", 0f, 0f, 0.3f, 0.22f, 0.6f, 0f);
                    SetCurve(clip, "localEulerAnglesRaw.y", 0f, 0f, 0.3f, 20f, 0.6f, 0f);
                    break;

                case "Die":
                    // 死亡：绕脚底向前扑倒，并略微下沉。
                    SetCurve(clip, "localEulerAnglesRaw.x", 0f, 0f, 0.8f, 88f, 1.2f, 90f);
                    SetCurve(clip, "localPosition.y", 0f, 0f, 1.2f, -0.25f);
                    break;

                default:
                    return null;
            }

            AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = loop;
            AnimationUtility.SetAnimationClipSettings(clip, settings);

            return clip;
        }

        /// <summary>
        /// 给片段写一条 Transform 曲线（绑定路径固定为 <see cref="ModelChildName"/>）。
        /// </summary>
        /// <param name="clip">目标片段。</param>
        /// <param name="propertyName">属性名（如 localPosition.y / localEulerAnglesRaw.x）。</param>
        /// <param name="keys">成对的 (时间, 值) 序列。</param>
        private static void SetCurve(AnimationClip clip, string propertyName, params float[] keys)
        {
            if (keys.Length < 4 || keys.Length % 2 != 0)
            {
                return;
            }

            Keyframe[] keyframes = new Keyframe[keys.Length / 2];

            for (int i = 0; i < keyframes.Length; i++)
            {
                keyframes[i] = new Keyframe(keys[i * 2], keys[i * 2 + 1]);
            }

            EditorCurveBinding binding = new EditorCurveBinding
            {
                path = ModelChildName,
                type = typeof(Transform),
                propertyName = propertyName
            };

            AnimationUtility.SetEditorCurve(clip, binding, new AnimationCurve(keyframes));
        }

        /// <summary>
        /// 把约定路径上的动画片段接到状态的 motion 上。
        ///
        /// 【规则：只在 motion 为空时写】这与工具对资产的一贯策略一致（"已存在一律沿用"）：
        /// 美术把真片段拖到状态上之后，重跑组装不会把它换回占位片段。
        /// </summary>
        /// <param name="state">目标状态。</param>
        /// <param name="clipName">片段文件名（不含扩展名）。</param>
        /// <returns>确实写入了 motion 返回 true。</returns>
        private static bool AssignStateMotionIfEmpty(AnimatorState state, string clipName)
        {
            if (state == null || state.motion != null)
            {
                return false;
            }

            AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(
                AnimationsFolder + "/" + clipName + ".anim");

            if (clip == null)
            {
                return false;
            }

            state.motion = clip;
            return true;
        }

        #endregion

        #region 英雄编制 ↔ 模型映射（阶段九：打破"10 个盖伦"）

        /// <summary>
        /// 10 个英雄的模型清单（下标 = 英雄席位号）。
        ///
        /// 【为什么是"下标 = 席位号"而不是"下标 = AI 抽签号"】
        /// 玩家英雄固定用 0 号（盖伦，与它的 QWER 四技能同一套身份），其余 9 个按席位顺序取 1~9。
        /// 这样"谁长什么样"是**确定性的**：同一份场景重复组装得到同一张脸，
        /// 排查"某个英雄模型不对"时不需要先复现随机种子。
        ///
        /// 【资产缺失时怎么办】逐个判空：某个英雄的模型没导入时该英雄保持白盒外观，
        /// 其余英雄照常换模型 —— 一格缺失不会让整队退回白盒。
        /// </summary>
        private static readonly string[] HeroChampionNames =
        {
            "Garen",     // 0 号：玩家英雄（与它的 QWER 四技能同一套身份）
            "Ashe",      // 1 号
            "Darius",    // 2 号
            "Lux",       // 3 号
            "Annie",     // 4 号
            "MasterYi",  // 5 号
            "Ahri",      // 6 号
            "Teemo",     // 7 号
            "Yasuo",     // 8 号
            "Zed"        // 9 号
        };

        /// <summary>取某个席位的英雄模型预制体路径。</summary>
        /// <param name="championIndex">席位号（0~9）。</param>
        /// <returns>模型预制体路径；席位越界时返回 null。</returns>
        private static string ResolveHeroModelPrefabPath(int championIndex)
        {
            if (championIndex < 0 || championIndex >= HeroChampionNames.Length)
            {
                return null;
            }

            return HeroModelFolder + "/Hero_" + HeroChampionNames[championIndex] + ".prefab";
        }

        /// <summary>取某个席位的英雄阵营材质路径。</summary>
        /// <param name="championIndex">席位号（0~9）。</param>
        /// <param name="blue">true = 蓝方材质，false = 红方材质。</param>
        /// <returns>材质路径；席位越界时返回 null。</returns>
        private static string ResolveHeroMaterialPath(int championIndex, bool blue)
        {
            if (championIndex < 0 || championIndex >= HeroChampionNames.Length)
            {
                return null;
            }

            return MaterialsFolder + "/Hero_" + HeroChampionNames[championIndex] + (blue ? "_Blue" : "_Red") + ".mat";
        }

        /// <summary>
        /// 给**英雄实例**挂上它自己那一份模型与阵营材质（阶段九"打破 10 个盖伦"的核心）。
        ///
        /// 【为什么必须按实例挂，而不是在预制体上挂一次】
        /// 10 个英雄共用同一个 HeroPrefab。预制体上只能有一份模型，按预制体挂就等于 10 个盖伦。
        /// 因此模型、以及"该英雄的蓝/红阵营材质"，都必须是**实例级覆盖**：
        /// 模型挂成实例的子节点，材质写进实例上 TeamColorView 的 allyHeroMaterial / enemyHeroMaterial。
        /// 这些都是可序列化的实例覆盖，会随场景一起保存，不会污染共享预制体。
        ///
        /// 【为什么材质要按英雄区分】每个英雄有自己的贴图。若沿用预制体上那两份通用材质
        /// （Garen 的贴图 + 阵营色调），10 个英雄会全部变成"同一张贴图 + 蓝/红"，等于没换模型。
        ///
        /// 【白盒让位仍然只关 Renderer】碰撞体是索敌与右键拾取的依据，绝不能碰。
        /// </summary>
        /// <param name="instance">英雄实例（场景对象）。</param>
        /// <param name="championIndex">席位号（0~9）。</param>
        /// <param name="team">阵营（决定用蓝还是红材质）。</param>
        /// <param name="notes">组装说明收集器。</param>
        private static void AttachHeroModelToInstance(
            GameObject instance, int championIndex, TeamType team, List<string> notes)
        {
            if (instance == null)
            {
                return;
            }

            string championName = (championIndex >= 0 && championIndex < HeroChampionNames.Length)
                ? HeroChampionNames[championIndex]
                : "?";

            string modelPath = ResolveHeroModelPrefabPath(championIndex);
            GameObject modelAsset = modelPath != null ? AssetDatabase.LoadAssetAtPath<GameObject>(modelPath) : null;

            if (modelAsset == null)
            {
                notes.Add($"英雄席位 {championIndex}（{championName}）模型未导入（槽位预留 {modelPath}），保持白盒外观");
                return;
            }

            // ---- ① 挂模型（幂等：已有 Model 子节点就跳过）----
            if (instance.transform.Find(ModelChildName) == null)
            {
                GameObject model = PrefabUtility.InstantiatePrefab(modelAsset, instance.transform) as GameObject;

                if (model == null)
                {
                    // 退回普通实例化：保证"有模型就一定挂得上"（会失去与模型预制体的嵌套关系）。
                    model = UnityEngine.Object.Instantiate(modelAsset, instance.transform);
                }

                if (model == null)
                {
                    Debug.LogWarning($"[AutoSceneBuilder] 英雄 {instance.name} 的模型 {modelPath} 实例化失败，保持白盒外观。");
                    return;
                }

                model.name = ModelChildName;
                model.transform.localPosition = Vector3.zero;
                model.transform.localRotation = Quaternion.identity;
                model.transform.localScale = Vector3.one;

                Undo.RegisterCreatedObjectUndo(model, "挂载英雄模型 " + championName);
            }

            // ---- ② 白盒外观让位（只关 Renderer，保留 Collider）----
            GameObject whitebox = ResolveWhiteboxVisual(instance);

            if (whitebox != null)
            {
                Renderer whiteboxRenderer = whitebox.GetComponent<Renderer>();

                if (whiteboxRenderer != null)
                {
                    whiteboxRenderer.enabled = false;
                }
            }

            // ---- ③ 该英雄的阵营材质写进实例上的 TeamColorView ----
            TeamColorView view = instance.GetComponent<TeamColorView>();

            if (view != null)
            {
                Material blue = AssetDatabase.LoadAssetAtPath<Material>(ResolveHeroMaterialPath(championIndex, true));
                Material red = AssetDatabase.LoadAssetAtPath<Material>(ResolveHeroMaterialPath(championIndex, false));

                if (blue != null)
                {
                    AssignObjectReference(view, "allyHeroMaterial", blue);
                }

                if (red != null)
                {
                    AssignObjectReference(view, "enemyHeroMaterial", red);
                }

                if (blue == null || red == null)
                {
                    Debug.LogWarning(
                        $"[AutoSceneBuilder] 英雄 {instance.name}（{championName}）的阵营材质缺失" +
                        $"（蓝 {(blue != null ? "有" : "缺")} / 红 {(red != null ? "有" : "缺")}），" +
                        "该英雄会沿用预制体上的通用材质（外观仍是本英雄的模型，只是阵营色调不区分）。");
                }
            }

            notes.Add($"英雄 {instance.name} ← 模型 {championName}（{team}）");
        }

        /// <summary>
        /// 给**建筑**（防御塔 / 基地）挂上真实模型并换掉白盒外观。
        ///
        /// 【为什么建筑可以直接改材质】建筑没有"同一预制体长成两方"的需求（塔与基地都是逐个创建的
        /// 场景对象），因此可以直接把模型的所有渲染器刷成该阵营的材质，不必绕 TeamColorView。
        ///
        /// 【不把模型放进 Building 层】NavMesh 的"挖洞"由白盒 Body 的碰撞盒决定，
        /// 而模型的实际占地**小于**白盒体（塔：模型 1.34×1.58 < 白盒 1.6×1.6），
        /// 因此不需要也不应该把模型加进烘焙几何 —— 加了反而会让洞比可见占地更大，
        /// 单位会在离塔还有一段距离的地方就绕开（"明明看着能走却走不过去"）。
        /// </summary>
        /// <param name="building">建筑根对象（场景对象）。</param>
        /// <param name="modelPrefabPath">模型预制体路径。</param>
        /// <param name="teamMaterial">该阵营的模型材质。</param>
        /// <param name="label">中文标签（日志用）。</param>
        /// <param name="notes">组装说明收集器。</param>
        private static void AttachBuildingModel(
            GameObject building, string modelPrefabPath, Material teamMaterial, string label, List<string> notes)
        {
            if (building == null)
            {
                return;
            }

            GameObject modelAsset = AssetDatabase.LoadAssetAtPath<GameObject>(modelPrefabPath);

            if (modelAsset == null)
            {
                notes.Add($"{label}：模型未导入（槽位预留 {modelPrefabPath}），保持白盒外观");
                return;
            }

            if (building.transform.Find(ModelChildName) == null)
            {
                GameObject model = PrefabUtility.InstantiatePrefab(modelAsset, building.transform) as GameObject;

                if (model == null)
                {
                    model = UnityEngine.Object.Instantiate(modelAsset, building.transform);
                }

                if (model == null)
                {
                    Debug.LogWarning($"[AutoSceneBuilder] {label} 的模型 {modelPrefabPath} 实例化失败，保持白盒外观。");
                    return;
                }

                model.name = ModelChildName;
                model.transform.localPosition = Vector3.zero;
                model.transform.localRotation = Quaternion.identity;
                model.transform.localScale = Vector3.one;

                Undo.RegisterCreatedObjectUndo(model, "挂载建筑模型 " + label);

                // 模型整体刷成该阵营材质（建筑的贴图是蓝/红两份现成的，不需要染色）。
                if (teamMaterial != null)
                {
                    Renderer[] renderers = model.GetComponentsInChildren<Renderer>(true);

                    for (int i = 0; i < renderers.Length; i++)
                    {
                        if (renderers[i] != null)
                        {
                            renderers[i].sharedMaterial = teamMaterial;
                        }
                    }
                }
            }

            // 【白盒外观为什么**不在这里**关掉 —— 这是实机踩出来的】
            // 关掉 Body 的 MeshRenderer 会让 NavMesh 烘焙**收不到这个建筑的几何**：
            // 烘焙是按层掩码收集 MeshRenderer 的，渲染器一关，塔与基地就从"挖洞几何"里消失了，
            // 于是导航网格上不再有洞，单位会直接从塔体里穿过去（实机表现：挖洞校验一次性报 8 个建筑全部失败，
            // 且烘焙日志从"9 个几何源"掉到"1 个几何源"）。
            // 因此让位动作必须排在**烘焙之后**，见 HideBuildingWhiteboxVisuals。
            string materialName = teamMaterial != null ? teamMaterial.name : "无材质";
            notes.Add($"{label} 已挂载模型 ← {System.IO.Path.GetFileName(modelPrefabPath)}（{materialName}）");
        }

        /// <summary>
        /// 让建筑的白盒外观让位（关掉 Body 的渲染器，**保留碰撞体**）。
        ///
        /// 【必须在 NavMesh 烘焙之后调用 —— 这是本方法独立存在的唯一理由】
        /// 建筑在导航网格上是靠"烘焙期挖洞"表达阻挡的：烘焙按层掩码收集 `MeshRenderer`，
        /// 把它们抠成空洞。若在烘焙前就把 Body 的渲染器关掉，建筑就不会被收集，
        /// 洞不存在 → 单位穿模。实机症状是挖洞校验一次性报 8 个建筑全部"中心仍可行走"，
        /// 而烘焙日志里几何源从 9 个掉到 1 个。
        ///
        /// 【为什么可以安全地在烘焙后关】烘焙是一次性的编辑期操作，产物已经落盘到 NavMesh 资产；
        /// 运行期不再需要 Body 的网格，只需要它的碰撞体（索敌 `OverlapSphere` 与右键拾取 `Raycast`）。
        /// 所以"烘焙时开着、运行期关掉"是正确顺序，而不是"两个写入者打架"。
        /// </summary>
        /// <param name="notes">组装说明收集器。</param>
        private static void HideBuildingWhiteboxVisuals(List<string> notes)
        {
            EntityBase[] entities = UnityEngine.Object.FindObjectsOfType<EntityBase>(true);
            List<string> hidden = new List<string>();

            for (int i = 0; i < entities.Length; i++)
            {
                EntityBase entity = entities[i];

                if (entity == null)
                {
                    continue;
                }

                // 只处理建筑：此刻英雄与小兵还没被实例化，因此这个过滤是"保险"而不是"必需"。
                if (entity.EntityType != EntityType.Tower && entity.EntityType != EntityType.Base)
                {
                    continue;
                }

                // 没挂上模型的建筑保持白盒外观（模型缺失时它仍然要可见）。
                if (entity.transform.Find(ModelChildName) == null)
                {
                    continue;
                }

                GameObject whitebox = ResolveWhiteboxVisual(entity.gameObject);

                if (whitebox == null)
                {
                    continue;
                }

                Renderer whiteboxRenderer = whitebox.GetComponent<Renderer>();

                if (whiteboxRenderer != null && whiteboxRenderer.enabled)
                {
                    whiteboxRenderer.enabled = false;
                    hidden.Add(entity.name);
                }
            }

            if (hidden.Count > 0)
            {
                notes.Add(
                    $"建筑白盒外观已让位 {hidden.Count} 个（{string.Join("、", hidden)}）——" +
                    "**在 NavMesh 烘焙之后执行**：烘焙需要 Body 的 MeshRenderer 参与几何收集，先关掉就没有挖洞了");
            }
        }

        #endregion

        /// <summary>
        /// 阶段九表现层基建：生成动画控制器 → 给英雄 / 小兵预制体接线 → 装配场景级特效生成器。
        ///
        /// 【调用时机是硬约束】必须在【步骤 15b 实例化 10 个英雄之前】完成预制体接线：
        /// 若放在实例化之后，就得依赖"改预制体资产后已有实例自动同步"这条引擎行为 ——
        /// 能work，但顺序上不直观，而且一旦某次同步失败，症状是"10 个英雄全都没有动画组件"，
        /// 排查时很难想到是装配顺序问题（与 TeamColorView 那次踩坑同源）。
        /// </summary>
        /// <param name="heroPrefab">英雄预制体资产。</param>
        /// <param name="minionPrefab">小兵预制体资产。</param>
        /// <param name="notes">组装说明收集器。</param>
        private static void BuildStage9Presentation(GameObject heroPrefab, GameObject minionPrefab, List<string> notes)
        {
            EnsureFolderRecursive(AnimationsFolder);

            // 先生成占位动画片段，再生成控制器 —— 控制器的状态要在这一步把片段接上。
            EnsureAnimationClips(notes);

            AnimatorController controller = EnsureUnitAnimatorController(notes);

            EnsureReservedModelFolders(notes);

            PatchUnitPrefabAnimation(heroPrefab, "英雄预制体", HeroModelPrefabPath, controller, notes);
            PatchUnitPrefabAnimation(minionPrefab, "小兵预制体", MinionModelPrefabPath, controller, notes);

            // ---- 挂载真实 3D 模型（资产已导入时才有动作） ----
            // 【英雄为什么不在预制体上挂模型】10 个英雄共用同一个 HeroPrefab，预制体上只能有一份模型
            // —— 按预制体挂就等于 10 个盖伦。英雄模型一律**按实例**挂（见 AttachHeroModelToInstance），
            // 由 CreateHeroInstance 在实例化后调用。
            // 【小兵为什么可以在预制体上挂】双方小兵共用同一套模型（只靠蓝/红贴图区分阵营），
            // 没有"每个实例不同"的需求，因此挂在预制体上最省事。
            AttachReservedModelToPrefab(minionPrefab, "小兵预制体", MinionModelPrefabPath, notes);

            ValidateStage9Setup(heroPrefab, minionPrefab, controller);
        }

        /// <summary>
        /// 装配场景级正式特效层（<c>VfxSpawnerRoot</c>）。
        ///
        /// 【为什么与白盒 VfxRoot 分开成两个根对象】两者的生命周期不同：白盒占位特效在美术资源
        /// 接入后应整体删除，而正式特效层要长期存在。放在同一个根下会让"删掉白盒"变成一件
        /// 需要小心翼翼区分对象的事情（白盒管理器还会生成一堆临时几何体作为子节点）。
        /// 分开之后，接入美术资源时的操作就是一句话：把 VfxRoot 删掉（或把它的组件禁用）。
        /// </summary>
        /// <param name="notes">组装说明收集器。</param>
        private static void BuildVfxSpawner(List<string> notes)
        {
            GameObject root = CreateRootObject(VfxSpawnerRootName, Vector3.zero);
            VfxSpawner spawner = Undo.AddComponent<VfxSpawner>(root);

            // 预留槽位：五个特效预制体若已存在于约定路径，直接注入；不存在则留空（通道惰性）。
            GameObject hitVfx = AssetDatabase.LoadAssetAtPath<GameObject>(HitVfxPrefabPath);
            GameObject castVfx = AssetDatabase.LoadAssetAtPath<GameObject>(CastVfxPrefabPath);
            GameObject projectileHitVfx = AssetDatabase.LoadAssetAtPath<GameObject>(ProjectileHitVfxPrefabPath);
            GameObject shieldVfx = AssetDatabase.LoadAssetAtPath<GameObject>(ShieldVfxPrefabPath);
            GameObject zoneVfx = AssetDatabase.LoadAssetAtPath<GameObject>(ZoneVfxPrefabPath);

            AssignObjectReference(spawner, "hitVfxPrefab", hitVfx);
            AssignObjectReference(spawner, "castVfxPrefab", castVfx);
            AssignObjectReference(spawner, "projectileHitVfxPrefab", projectileHitVfx);
            AssignObjectReference(spawner, "shieldVfxPrefab", shieldVfx);
            AssignObjectReference(spawner, "zoneVfxPrefab", zoneVfx);

            notes.Add(
                $"{VfxSpawnerRootName} 已装配（VfxSpawner：订阅受击 / 施法 / 弹道命中，从对象池取特效播放）。" +
                $"特效槽位：受击 {DescribeOptionalAsset(hitVfx, HitVfxPrefabPath)} / " +
                $"施法 {DescribeOptionalAsset(castVfx, CastVfxPrefabPath)} / " +
                $"命中 {DescribeOptionalAsset(projectileHitVfx, ProjectileHitVfxPrefabPath)} / " +
                $"护盾 {DescribeOptionalAsset(shieldVfx, ShieldVfxPrefabPath)} / " +
                $"范围场 {DescribeOptionalAsset(zoneVfx, ZoneVfxPrefabPath)}");
        }

        #region 动画控制器

        /// <summary>
        /// 取（必要时创建并补齐）单位动画控制器。
        ///
        /// 五状态 + 四参数 + 六条过渡：
        /// <code>
        ///   Idle  ──Speed &gt; 0.1──▶  Run          （0.1 秒过渡，不硬切）
        ///   Run   ──Speed &lt; 0.1──▶  Idle
        ///   AnyState ──Attack 触发──▶ Attack ──(退出时间 0.9)──▶ Idle
        ///   AnyState ──Spell  触发──▶ Spell  ──(退出时间 0.9)──▶ Idle
        ///   AnyState ──Die    触发──▶ Die     （终态，不返回）
        /// </code>
        /// </summary>
        /// <param name="notes">组装说明收集器。</param>
        /// <returns>可用的控制器；创建失败时返回 null（调用方据此跳过预制体接线）。</returns>
        private static AnimatorController EnsureUnitAnimatorController(List<string> notes)
        {
            AnimatorController controller =
                AssetDatabase.LoadAssetAtPath<AnimatorController>(UnitAnimatorControllerPath);

            bool created = false;

            if (controller == null)
            {
                controller = AnimatorController.CreateAnimatorControllerAtPath(UnitAnimatorControllerPath);
                created = true;

                if (controller == null)
                {
                    Debug.LogError(
                        $"[AutoSceneBuilder] 无法创建动画控制器 {UnitAnimatorControllerPath}。" +
                        "英雄与小兵将不会获得动画（不影响任何对局逻辑）。请检查该路径是否可写。");
                    return null;
                }
            }

            // ---- 参数 ----
            EnsureAnimatorParameter(controller, AnimSpeedParameter, AnimatorControllerParameterType.Float);
            EnsureAnimatorParameter(controller, AnimAttackTrigger, AnimatorControllerParameterType.Trigger);
            EnsureAnimatorParameter(controller, AnimSpellTrigger, AnimatorControllerParameterType.Trigger);
            EnsureAnimatorParameter(controller, AnimDieTrigger, AnimatorControllerParameterType.Trigger);

            // ---- 状态 ----
            AnimatorStateMachine machine = controller.layers[0].stateMachine;

            AnimatorState idle = EnsureAnimatorState(machine, AnimStateIdle);
            AnimatorState run = EnsureAnimatorState(machine, AnimStateRun);
            AnimatorState attack = EnsureAnimatorState(machine, AnimStateAttack);
            AnimatorState spell = EnsureAnimatorState(machine, AnimStateSpell);
            AnimatorState die = EnsureAnimatorState(machine, AnimStateDie);

            // 默认状态必须是 Idle：新建控制器时 stateMachine.defaultState 为 null，
            // 而"没有默认状态"的控制器在运行期会停在第一个状态且不报错（表现为"单位一动不动"）。
            if (idle != null && machine.defaultState != idle)
            {
                machine.defaultState = idle;
            }

            // ---- 过渡 ----
            EnsureIdleRunTransition(idle, run, AnimatorConditionMode.Greater);
            EnsureIdleRunTransition(run, idle, AnimatorConditionMode.Less);

            EnsureActionTransition(machine, attack, AnimAttackTrigger, idle);
            EnsureActionTransition(machine, spell, AnimSpellTrigger, idle);

            // 死亡是终态：AnyState → Die，且【不】返回 Idle。
            // 不做"回到 Idle"的过渡，是为了让"只播一次"这条要求在动画图上也是结构性的 ——
            // 尸体不会因为任何参数变化而回到待机状态。
            EnsureAnyStateTransition(machine, die, AnimDieTrigger, AnimActionEnterDuration);

            // ---- 片段（阶段九：占位动画，消除 T-pose）----
            // 只在状态的 motion 为空时写：美术把真片段拖上去之后，重跑组装不会把它换回占位片段。
            int motionsAssigned = 0;
            if (AssignStateMotionIfEmpty(idle, AnimStateIdle)) { motionsAssigned++; }
            if (AssignStateMotionIfEmpty(run, AnimStateRun)) { motionsAssigned++; }
            if (AssignStateMotionIfEmpty(attack, AnimStateAttack)) { motionsAssigned++; }
            if (AssignStateMotionIfEmpty(spell, AnimStateSpell)) { motionsAssigned++; }
            if (AssignStateMotionIfEmpty(die, AnimStateDie)) { motionsAssigned++; }

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();

            if (motionsAssigned > 0)
            {
                notes.Add(
                    $"{UnitAnimatorControllerPath} 已接入 {motionsAssigned} 个动画片段" +
                    "（占位动作片段，用于消除 T-pose；导入真美术片段后拖到对应状态即可覆盖）");
            }

            if (created)
            {
                notes.Add(
                    $"{UnitAnimatorControllerPath} 已生成（5 状态 Idle / Run / Attack / Spell / Die；" +
                    "4 参数 Speed(float) / Attack / Spell / Die；**本阶段不挂动画片段**，" +
                    "导入美术资源后把片段拖到对应状态上即可）");
            }
            else
            {
                notes.Add($"{UnitAnimatorControllerPath} 已存在，仅补齐缺失的参数 / 状态 / 过渡（不覆盖已有配置）");
            }

            return controller;
        }

        /// <summary>确保某个参数存在（按名字与类型比对）。已存在则不写盘。</summary>
        /// <param name="controller">目标控制器。</param>
        /// <param name="parameterName">参数名。</param>
        /// <param name="type">参数类型。</param>
        /// <returns>确实新增了参数返回 true。</returns>
        private static bool EnsureAnimatorParameter(
            AnimatorController controller, string parameterName, AnimatorControllerParameterType type)
        {
            AnimatorControllerParameter[] parameters = controller.parameters;

            for (int i = 0; i < parameters.Length; i++)
            {
                if (string.Equals(parameters[i].name, parameterName, System.StringComparison.Ordinal))
                {
                    return false;
                }
            }

            controller.AddParameter(parameterName, type);
            return true;
        }

        /// <summary>确保某个状态存在（按名字查找）。已存在则直接返回它，绝不重复添加。</summary>
        /// <param name="machine">目标状态机。</param>
        /// <param name="stateName">状态名。</param>
        /// <returns>状态；名称为空时返回 null。</returns>
        private static AnimatorState EnsureAnimatorState(AnimatorStateMachine machine, string stateName)
        {
            AnimatorState existing = FindAnimatorState(machine, stateName);

            if (existing != null)
            {
                return existing;
            }

            return machine.AddState(stateName);
        }

        /// <summary>按名字查找状态（找不到返回 null）。</summary>
        /// <param name="machine">目标状态机。</param>
        /// <param name="stateName">状态名。</param>
        /// <returns>状态；不存在时返回 null。</returns>
        private static AnimatorState FindAnimatorState(AnimatorStateMachine machine, string stateName)
        {
            ChildAnimatorState[] states = machine.states;

            for (int i = 0; i < states.Length; i++)
            {
                AnimatorState state = states[i].state;

                if (state != null && string.Equals(state.name, stateName, System.StringComparison.Ordinal))
                {
                    return state;
                }
            }

            return null;
        }

        /// <summary>
        /// 确保 Idle ↔ Run 之间存在一条由 Speed 驱动的过渡。
        /// 方向由 <paramref name="mode"/> 表达：Greater = Idle → Run，Less = Run → Idle。
        /// 【幂等策略】已存在就完全不动它 —— 过渡时长是美术会调的参数，
        /// 每次组装都写一遍等于把人的调整打回去（与项目"已存在一律沿用"的资产策略一致）。
        /// </summary>
        /// <param name="from">起始状态。</param>
        /// <param name="to">目标状态。</param>
        /// <param name="mode">条件比较方式。</param>
        private static void EnsureIdleRunTransition(AnimatorState from, AnimatorState to, AnimatorConditionMode mode)
        {
            if (from == null || to == null || FindStateTransition(from, to) != null)
            {
                return;
            }

            AnimatorStateTransition transition = from.AddTransition(to);

            transition.hasExitTime = false;
            transition.exitTime = 0f;
            transition.duration = AnimIdleRunTransitionDuration;
            transition.hasFixedDuration = true;
            transition.canTransitionToSelf = false;

            transition.AddCondition(mode, AnimIdleRunThreshold, AnimSpeedParameter);
        }

        /// <summary>
        /// 确保"AnyState → 动作状态（Attack / Spell）"以及"动作状态 → Idle"两条过渡存在。
        ///
        /// 【为什么动作状态要有回到 Idle 的过渡】AnyState → Attack 是一次性入口（trigger），
        /// 若没有出口，控制器会永远停在 Attack 状态 —— 单位此后不再播任何其它动画。
        /// 出口用"退出时间 0.9"而不是另一个 trigger，是因为"动作播完了"这件事由片段自己知道，
        /// 让表现层再维护一个"动作结束"信号只会多一个可能与动画长度不同步的写入者。
        ///
        /// 【本阶段没有片段时会不会卡在 Attack】
        /// 没有片段的动画状态没有时长（画面上本来就什么都不显示），因此无论引擎对"退出时间"
        /// 如何求值，观感都一样；这条过渡是为**挂上片段之后**准备的（那时它按片段长度的 90% 收尾）。
        /// 万一日后发现空状态确实会滞留，给该状态挂一个占位片段即可，不需要改本工具。
        /// </summary>
        /// <param name="machine">状态机。</param>
        /// <param name="actionState">动作状态（Attack / Spell）。</param>
        /// <param name="triggerName">入口触发参数名。</param>
        /// <param name="returnState">动作结束后的归属状态（Idle）。</param>
        private static void EnsureActionTransition(
            AnimatorStateMachine machine, AnimatorState actionState, string triggerName, AnimatorState returnState)
        {
            if (actionState == null)
            {
                return;
            }

            EnsureAnyStateTransition(machine, actionState, triggerName, AnimActionEnterDuration);

            if (returnState == null || returnState == actionState)
            {
                return;
            }

            if (FindStateTransition(actionState, returnState) != null)
            {
                return;
            }

            AnimatorStateTransition transition = actionState.AddTransition(returnState);

            transition.hasExitTime = true;
            transition.exitTime = AnimActionReturnExitTime;
            transition.duration = AnimActionReturnDuration;
            transition.hasFixedDuration = true;
            transition.canTransitionToSelf = false;

            // 回归过渡刻意不带条件：它由"片段播到 90%"触发，与任何参数无关。
        }

        /// <summary>确保"AnyState → 目标状态"的触发式过渡存在（死亡 / 攻击 / 施法共用）。</summary>
        /// <param name="machine">状态机。</param>
        /// <param name="to">目标状态。</param>
        /// <param name="triggerName">触发参数名。</param>
        /// <param name="duration">过渡时长（秒）。</param>
        private static void EnsureAnyStateTransition(
            AnimatorStateMachine machine, AnimatorState to, string triggerName, float duration)
        {
            if (machine == null || to == null || FindAnyStateTransition(machine, to) != null)
            {
                return;
            }

            AnimatorStateTransition transition = machine.AddAnyStateTransition(to);

            transition.hasExitTime = false;
            transition.exitTime = 0f;
            transition.duration = duration;
            transition.hasFixedDuration = true;

            // canTransitionToSelf = false：否则"已经在 Attack 状态时再按一次攻击"会重新进入
            // Attack（表现上是动作被打断重播）。本项目的攻击节奏由 AttackInterval 决定，
            // 动画只是视觉匹配，因此不允许自己重入。
            transition.canTransitionToSelf = false;

            transition.AddCondition(AnimatorConditionMode.If, 0f, triggerName);
        }

        /// <summary>查找 from → to 的既有过渡（找不到返回 null）。</summary>
        /// <param name="from">起始状态。</param>
        /// <param name="to">目标状态。</param>
        /// <returns>过渡；不存在时返回 null。</returns>
        private static AnimatorStateTransition FindStateTransition(AnimatorState from, AnimatorState to)
        {
            AnimatorStateTransition[] transitions = from.transitions;

            for (int i = 0; i < transitions.Length; i++)
            {
                if (transitions[i] != null && transitions[i].destinationState == to)
                {
                    return transitions[i];
                }
            }

            return null;
        }

        /// <summary>查找 AnyState → to 的既有过渡（找不到返回 null）。</summary>
        /// <param name="machine">状态机。</param>
        /// <param name="to">目标状态。</param>
        /// <returns>过渡；不存在时返回 null。</returns>
        private static AnimatorStateTransition FindAnyStateTransition(AnimatorStateMachine machine, AnimatorState to)
        {
            AnimatorStateTransition[] transitions = machine.anyStateTransitions;

            for (int i = 0; i < transitions.Length; i++)
            {
                if (transitions[i] != null && transitions[i].destinationState == to)
                {
                    return transitions[i];
                }
            }

            return null;
        }

        #endregion

        #region 预制体接线

        /// <summary>
        /// 给一个单位预制体接上动画：挂 Animator（如缺）→ 注入控制器 → 挂 AnimationComponent
        /// → 注入动画引用与预留的模型槽位。
        ///
        /// 【走 LoadPrefabContents / SaveAsPrefabAsset 的理由】与 PatchPrefabTeamColor 同一理由：
        /// 直接对预制体资产上的组件做 SerializedObject 修改，在部分引擎版本下不会稳定落盘；
        /// 官方推荐路径是"加载预制体内容 → 改 → 存回"。
        ///
        /// 【幂等】组件已存在、字段已是目标值时一个字节都不写盘。
        /// </summary>
        /// <param name="prefab">目标预制体资产。</param>
        /// <param name="prefabLabel">中文标签（仅用于日志）。</param>
        /// <param name="modelPrefabPath">该单位对应的预留模型路径。</param>
        /// <param name="controller">动画控制器。</param>
        /// <param name="notes">组装说明收集器。</param>
        private static void PatchUnitPrefabAnimation(
            GameObject prefab,
            string prefabLabel,
            string modelPrefabPath,
            AnimatorController controller,
            List<string> notes)
        {
            if (prefab == null)
            {
                Debug.LogWarning($"[AutoSceneBuilder] {prefabLabel}不存在，跳过动画接线。");
                return;
            }

            string prefabPath = AssetDatabase.GetAssetPath(prefab);

            if (string.IsNullOrEmpty(prefabPath) ||
                !prefabPath.EndsWith(".prefab", System.StringComparison.OrdinalIgnoreCase))
            {
                Debug.LogWarning(
                    $"[AutoSceneBuilder] {prefabLabel}（{prefab.name}）不是磁盘上的 .prefab 资产，跳过动画接线。");
                return;
            }

            GameObject contents = PrefabUtility.LoadPrefabContents(prefabPath);

            try
            {
                List<string> changes = new List<string>();
                List<string> missingFields = new List<string>();

                // ---- ① Animator（挂根节点）----
                Animator animator = contents.GetComponent<Animator>();

                if (animator == null)
                {
                    animator = contents.AddComponent<Animator>();
                    changes.Add("新增 Animator");
                }

                // 根运动必须关：本项目的位移权威是 NavMeshAgent，
                // 开着 root motion 会让动画与寻路争夺同一个 Transform（表现为"单位被动画拽着走"或原地抖动）。
                if (animator.applyRootMotion)
                {
                    animator.applyRootMotion = false;
                    changes.Add("applyRootMotion = false（位移权威交回 NavMeshAgent）");
                }

                // 顶视角下单位在屏幕上很小，若用默认的 BasedOnRenderers 剔除，
                // 单位一离开屏幕就会被暂停动画 —— 回屏时表现为"动作突然跳了一帧"。
                if (animator.cullingMode != AnimatorCullingMode.AlwaysAnimate)
                {
                    animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                    changes.Add("cullingMode = AlwaysAnimate");
                }

                if (controller != null && animator.runtimeAnimatorController != controller)
                {
                    animator.runtimeAnimatorController = controller;
                    changes.Add($"runtimeAnimatorController = {controller.name}");
                }

                // ---- ② AnimationComponent ----
                AnimationComponent animation = contents.GetComponent<AnimationComponent>();

                if (animation == null)
                {
                    animation = contents.AddComponent<AnimationComponent>();
                    changes.Add("新增 AnimationComponent（逻辑状态 → Animator 参数）");
                }

                // ---- ③ 预留的模型槽位 ----
                // 【必须与下面的注入共用同一个 SerializedObject】两个 SerializedObject 包装同一个组件时，
                // 只有最后 ApplyModifiedProperties 的那一份会落盘 —— 前一份的写入会被静默丢弃。
                SerializedObject serialized = new SerializedObject(animation);

                GameObject modelAsset = AssetDatabase.LoadAssetAtPath<GameObject>(modelPrefabPath);

                if (modelAsset != null)
                {
                    if (SetObjectReferenceIfDifferent(serialized, "modelPrefab", modelAsset, out bool modelFieldFound))
                    {
                        changes.Add($"modelPrefab = {modelAsset.name}（预留槽位已填入）");
                    }

                    if (!modelFieldFound)
                    {
                        missingFields.Add("modelPrefab");
                    }
                }

                // ---- ④ 显式注入引用（不依赖运行期的自动解析）----
                if (SetObjectReferenceIfDifferent(serialized, "animator", animator, out bool animatorFieldFound))
                {
                    changes.Add($"animator = {animator.name}");
                }

                if (!animatorFieldFound)
                {
                    missingFields.Add("animator");
                }

                // 参数名字段必须存在：它们与控制器里的参数名一一对应，字段一旦被改名，
                // 注入就静默失效（症状是"动画组件在、控制器在，但什么都没发生"）。
                AppendMissingField(missingFields, serialized, "speedParameterName");
                AppendMissingField(missingFields, serialized, "attackTriggerName");
                AppendMissingField(missingFields, serialized, "spellTriggerName");
                AppendMissingField(missingFields, serialized, "dieTriggerName");

                if (missingFields.Count > 0)
                {
                    Debug.LogError(
                        $"[AutoSceneBuilder] {prefabPath}（{prefabLabel}）的动画接线失败：" +
                        $"AnimationComponent 上找不到字段 {string.Join("、", missingFields)}（字段可能已被改名）。" +
                        "该单位不会播放任何动画 —— 请同步修改本工具的字段名与 AnimationComponent 的声明。");
                    return;
                }

                if (changes.Count == 0)
                {
                    // 组件与全部字段都已是设计值：不写盘，保持幂等（重复执行不产生任何资产改动）。
                    notes.Add($"{prefabPath}（{prefabLabel}）动画接线已是设计值，未改动");
                    return;
                }

                serialized.ApplyModifiedProperties();

                PrefabUtility.SaveAsPrefabAsset(contents, prefabPath);
                AssetDatabase.SaveAssets();

                notes.Add($"{prefabPath}（{prefabLabel}）动画接线：{string.Join("；", changes)}");
            }
            catch (System.Exception exception)
            {
                Debug.LogError(
                    $"[AutoSceneBuilder] 为{prefabLabel} {prefabPath} 接线动画时出错，" +
                    $"该单位可能没有动画（不影响对局逻辑）：{exception.Message}");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        /// <summary>
        /// 把预留路径上的真实 3D 模型挂成预制体的子节点，并让白盒外观让位。
        ///
        /// 【为什么模型是"子节点"而不是替换根节点】README §2.1.1 的架构约束：实体根节点必须贴地、
        /// 由 NavMeshAgent 驱动，逻辑组件全部挂在根上。模型只是**外观**，挂成子节点后
        /// "换模型"就是一次纯表现层操作 —— 逻辑代码一行都不用改（这正是阶段九要验证的解耦）。
        ///
        /// 【白盒外观怎么"让位"】关掉它的 Renderer，但**绝不碰 Collider**：
        /// 索敌（Physics.OverlapSphere）与右键拾取（Physics.Raycast）都依赖碰撞体，
        /// 关掉它会让单位变成"看得见却点不中、索敌不到"——比不换模型严重得多。
        /// 英雄预制体把外观放在 Body 子节点上（带 CapsuleCollider），小兵预制体把网格与碰撞体
        /// 都挂在根节点上，因此这里两种结构都要兜住（先找 Body，找不到就退化为根节点自身）。
        ///
        /// 【幂等】已存在名为 Model 的子节点时只做一次"白盒让位"检查，不重复实例化 ——
        /// 否则每执行一次组装就多一层模型。
        /// </summary>
        /// <param name="prefab">目标预制体资产。</param>
        /// <param name="prefabLabel">中文标签（仅用于日志）。</param>
        /// <param name="modelPrefabPath">该单位对应的模型预制体路径。</param>
        /// <param name="notes">组装说明收集器。</param>
        private static void AttachReservedModelToPrefab(
            GameObject prefab, string prefabLabel, string modelPrefabPath, List<string> notes)
        {
            if (prefab == null)
            {
                return;
            }

            string prefabPath = AssetDatabase.GetAssetPath(prefab);

            if (string.IsNullOrEmpty(prefabPath) ||
                !prefabPath.EndsWith(".prefab", System.StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            GameObject modelAsset = AssetDatabase.LoadAssetAtPath<GameObject>(modelPrefabPath);

            if (modelAsset == null)
            {
                // 模型尚未导入：本阶段就是"预留槽位"，白盒外观保持原样即可，不算错误。
                return;
            }

            GameObject contents = PrefabUtility.LoadPrefabContents(prefabPath);

            try
            {
                List<string> changes = new List<string>();
                Transform existing = contents.transform.Find(ModelChildName);

                if (existing == null)
                {
                    // 优先用 PrefabUtility.InstantiatePrefab（保留与模型预制体的嵌套关系，
                    // 日后替换模型文件即可整体更新）；失败时退回普通 Instantiate，保证"有模型就一定挂得上"。
                    GameObject modelInstance = PrefabUtility.InstantiatePrefab(modelAsset, contents.transform) as GameObject;

                    if (modelInstance == null)
                    {
                        modelInstance = UnityEngine.Object.Instantiate(modelAsset, contents.transform);
                    }

                    if (modelInstance == null)
                    {
                        Debug.LogWarning(
                            $"[AutoSceneBuilder] {prefabLabel} 的模型 {modelPrefabPath} 实例化失败，" +
                            "该单位保持白盒外观（不影响任何对局逻辑）。");
                        return;
                    }

                    modelInstance.name = ModelChildName;
                    modelInstance.transform.localPosition = Vector3.zero;
                    modelInstance.transform.localRotation = Quaternion.identity;
                    modelInstance.transform.localScale = Vector3.one;

                    changes.Add($"挂载模型子节点 {ModelChildName} ← {modelAsset.name}");
                }

                // 白盒外观让位（只关 Renderer，保留 Collider）。
                GameObject whiteboxVisual = ResolveWhiteboxVisual(contents);

                if (whiteboxVisual != null)
                {
                    Renderer whiteboxRenderer = whiteboxVisual.GetComponent<Renderer>();

                    if (whiteboxRenderer != null && whiteboxRenderer.enabled)
                    {
                        whiteboxRenderer.enabled = false;
                        changes.Add($"关闭白盒外观渲染器 {whiteboxVisual.name}（保留碰撞体）");
                    }
                }

                if (changes.Count == 0)
                {
                    notes.Add($"{prefabPath}（{prefabLabel}）模型已挂载且白盒已让位，未改动");
                    return;
                }

                PrefabUtility.SaveAsPrefabAsset(contents, prefabPath);
                AssetDatabase.SaveAssets();

                notes.Add($"{prefabPath}（{prefabLabel}）：{string.Join("；", changes)}");
            }
            catch (System.Exception exception)
            {
                Debug.LogError(
                    $"[AutoSceneBuilder] 为{prefabLabel} {prefabPath} 挂载模型时出错，" +
                    $"该单位保持白盒外观（不影响对局逻辑）：{exception.Message}");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        /// <summary>
        /// 找出白盒外观所在的对象：优先名为 Body 的子节点（英雄预制体的结构），
        /// 找不到就退回预制体根节点自身（小兵预制体把网格与碰撞体都挂在根上）。
        /// 返回 null 表示没有任何可关的白盒渲染器。
        /// </summary>
        /// <param name="contents">预制体内容根对象。</param>
        /// <returns>承载白盒网格的对象；没有则返回 null。</returns>
        private static GameObject ResolveWhiteboxVisual(GameObject contents)
        {
            Transform body = contents.transform.Find("Body");

            if (body != null && body.GetComponent<Renderer>() != null)
            {
                return body.gameObject;
            }

            return contents.GetComponent<Renderer>() != null ? contents : null;
        }

        /// <summary>
        /// 创建预留的模型目录，并报告槽位状态。
        ///
        /// 【为什么工具要主动创建空目录】"预留槽位"如果只写在注释里，美术无从知道模型该放哪、
        /// 叫什么名字。把目录建出来、把约定路径写进组装日志，约定才是可执行的
        /// （导入后重新执行本菜单即自动接入，不需要任何人去 Inspector 里拖引用）。
        /// </summary>
        /// <param name="notes">组装说明收集器。</param>
        private static void EnsureReservedModelFolders(List<string> notes)
        {
            EnsureFolderRecursive(HeroModelFolder);
            EnsureFolderRecursive(MinionModelFolder);
            EnsureFolderRecursive(BuildingModelFolder);

            GameObject heroModel = AssetDatabase.LoadAssetAtPath<GameObject>(HeroModelPrefabPath);
            GameObject minionModel = AssetDatabase.LoadAssetAtPath<GameObject>(MinionModelPrefabPath);

            notes.Add(
                $"模型槽位已预留：{HeroModelPrefabPath} / {MinionModelPrefabPath}（建筑模型目录 {BuildingModelFolder}）。" +
                $"当前状态：英雄 {DescribeOptionalAsset(heroModel, HeroModelPrefabPath)} / " +
                $"小兵 {DescribeOptionalAsset(minionModel, MinionModelPrefabPath)}。" +
                "导入 3D 模型后重新执行本菜单即自动接入（本阶段不替换白盒外观）");
        }

        /// <summary>把一个字段是否存在写进缺失清单（不读值，只验证字段名是否还对得上）。</summary>
        /// <param name="missingFields">缺失清单。</param>
        /// <param name="serialized">组件包装。</param>
        /// <param name="fieldName">字段名。</param>
        private static void AppendMissingField(List<string> missingFields, SerializedObject serialized, string fieldName)
        {
            if (serialized.FindProperty(fieldName) == null)
            {
                missingFields.Add(fieldName);
            }
        }

        #endregion

        #region 收尾校验

        /// <summary>
        /// 阶段九接线**读回校验**：回磁盘上的预制体资产把"到底写进去了什么"核一遍。
        ///
        /// 【为什么必须有这一步】本项目已经两次被"字段改名 → 旧值被 Unity 静默丢弃 → 功能无声失效"
        /// 咬过（MinionPrefab 的配色、AI 英雄的技能槽）。动画接线同样是"注入失败时什么都不报"的类型：
        /// 组件在、控制器在、Inspector 里看起来一切正常，只有实机"单位一动不动"。
        /// 因此这里对两个预制体各核五项：Animator 存在、控制器已注入、根运动已关、
        /// AnimationComponent 存在、动画引用与四个参数名字段都在。
        /// </summary>
        /// <param name="heroPrefab">英雄预制体资产。</param>
        /// <param name="minionPrefab">小兵预制体资产。</param>
        /// <param name="controller">期望的动画控制器。</param>
        private static void ValidateStage9Setup(
            GameObject heroPrefab, GameObject minionPrefab, AnimatorController controller)
        {
            List<string> problems = new List<string>();

            AppendAnimatorProblems(problems, heroPrefab, "英雄预制体", controller);
            AppendAnimatorProblems(problems, minionPrefab, "小兵预制体", controller);

            // 英雄的模型是**按实例**挂的（10 个英雄各一份），预制体上不该有模型 —— 因此这里只校验小兵。
            // 英雄侧由 ValidateHeroModelAssignment 在实例化之后回场景逐个核对。
            AppendModelProblems(problems, minionPrefab, "小兵预制体", MinionModelPrefabPath);

            AppendControllerProblems(problems, controller);

            if (problems.Count == 0)
            {
                Debug.Log(
                    "[AutoSceneBuilder] 阶段九表现层接线校验通过：英雄 / 小兵预制体均已挂 Animator + AnimationComponent，" +
                    $"控制器 {UnitAnimatorControllerPath} 已注入且根运动已关闭；" +
                    "控制器含 Idle / Run / Attack / Spell / Die 五状态与 Speed / Attack / Spell / Die 四参数。\n" +
                    "  · 注意：本阶段控制器**不含动画片段**（美术资源尚未导入），" +
                    "因此运行期动画参数会正确变化、但画面上看不到动作变化 —— 这是预期行为。");
                return;
            }

            Debug.LogError(
                "[AutoSceneBuilder] 阶段九表现层接线校验失败（实机症状：单位没有任何动作变化）：\n  · " +
                string.Join("\n  · ", problems));
        }

        /// <summary>
        /// 核对模型挂载结果（仅当预留路径上确实有模型资产时才校验）。
        ///
        /// 【为什么必须读回校验】"挂载模型"是典型的**注入失败时静默降级**的操作：
        /// 实例化失败、白盒没让位、模型挂到了错误的层级，三者都不会报错，
        /// 实机症状只是"看起来还是胶囊体"或"胶囊体与模型叠在一起"。
        /// </summary>
        /// <param name="problems">问题清单。</param>
        /// <param name="prefab">目标预制体。</param>
        /// <param name="label">中文标签。</param>
        /// <param name="modelPrefabPath">预留的模型路径。</param>
        private static void AppendModelProblems(
            List<string> problems, GameObject prefab, string label, string modelPrefabPath)
        {
            if (prefab == null)
            {
                return;
            }

            GameObject modelAsset = AssetDatabase.LoadAssetAtPath<GameObject>(modelPrefabPath);

            if (modelAsset == null)
            {
                // 模型尚未导入：白盒外观是预期形态，不算问题。
                return;
            }

            Transform model = prefab.transform.Find(ModelChildName);

            if (model == null)
            {
                problems.Add(
                    $"{label}（{prefab.name}）未挂载模型子节点 {ModelChildName}（{modelPrefabPath} 已存在，" +
                    "说明挂载步骤没有生效）—— 该单位会保持白盒外观");
                return;
            }

            if (model.GetComponentInChildren<MeshRenderer>() == null &&
                model.GetComponentInChildren<SkinnedMeshRenderer>() == null)
            {
                problems.Add($"{label}.{ModelChildName} 下找不到任何渲染器（模型可能是空预制体）");
            }

            // 白盒外观必须已让位，否则会出现"胶囊体套在模型外面"的重影。
            GameObject whiteboxVisual = ResolveWhiteboxVisual(prefab);

            if (whiteboxVisual != null)
            {
                Renderer whiteboxRenderer = whiteboxVisual.GetComponent<Renderer>();

                if (whiteboxRenderer != null && whiteboxRenderer.enabled)
                {
                    problems.Add(
                        $"{label} 的白盒外观 {whiteboxVisual.name} 渲染器仍开启（会与模型重影）");
                }
            }

            // 碰撞体必须还在 —— 关掉它会让单位索敌不到、也点不中。
            if (prefab.GetComponentInChildren<Collider>() == null)
            {
                problems.Add($"{label} 及其子节点上没有 Collider（单位将无法被索敌锁定）");
            }
        }

        /// <summary>
        /// 英雄模型分配的**读回校验**（阶段九：打破"10 个盖伦"的验收判据）。
        ///
        /// 回场景里把 10 个英雄实例逐个查一遍，核三件事：
        ///   ① 每个英雄都有 Model 子节点（模型真的挂上了，而不是静默退回白盒）；
        ///   ② 10 个英雄的网格名**互不相同**（这是"各不相同"这条需求的直接判据 ——
        ///      只要有人复制粘贴把某个英雄的模型路径写重复，这里立刻会点出来）；
        ///   ③ 白盒外观渲染器已关闭（否则会出现胶囊体与模型的重影）。
        ///
        /// 【为什么必须读回场景而不是信局部变量】"我打算给谁挂什么"和"场景里实际挂的是什么"
        /// 是两件事：实例化失败、模型资产缺失、路径写错，三者都不会抛异常。
        /// </summary>
        private static void ValidateHeroModelAssignment()
        {
            List<string> problems = new List<string>();
            List<string> modelNames = new List<string>();

            HeroController[] heroes = UnityEngine.Object.FindObjectsOfType<HeroController>(true);

            for (int i = 0; i < heroes.Length; i++)
            {
                HeroController hero = heroes[i];

                if (hero == null)
                {
                    continue;
                }

                Transform model = hero.transform.Find(ModelChildName);

                if (model == null)
                {
                    problems.Add($"{hero.name} 没有 Model 子节点（模型未挂上，该英雄是白盒外观）");
                    continue;
                }

                MeshFilter filter = model.GetComponentInChildren<MeshFilter>();
                string meshName = filter != null && filter.sharedMesh != null ? filter.sharedMesh.name : "（空网格）";
                modelNames.Add($"{hero.name}={meshName}");

                GameObject whitebox = ResolveWhiteboxVisual(hero.gameObject);

                if (whitebox != null)
                {
                    Renderer whiteboxRenderer = whitebox.GetComponent<Renderer>();

                    if (whiteboxRenderer != null && whiteboxRenderer.enabled)
                    {
                        problems.Add($"{hero.name} 的白盒外观 {whitebox.name} 渲染器仍开启（会与模型重影）");
                    }
                }
            }

            // 判据 ②：模型必须互不相同
            HashSet<string> distinct = new HashSet<string>();

            for (int i = 0; i < modelNames.Count; i++)
            {
                int eq = modelNames[i].IndexOf('=');
                distinct.Add(eq >= 0 ? modelNames[i].Substring(eq + 1) : modelNames[i]);
            }

            if (heroes.Length > 0 && distinct.Count < heroes.Length)
            {
                problems.Add(
                    $"英雄里只用了 {distinct.Count} 种不同模型（应为 {heroes.Length} 种）——" +
                    "说明有英雄的模型路径重复了，战场上会出现「克隆人」");
            }

            if (problems.Count == 0)
            {
                Debug.Log(
                    $"[AutoSceneBuilder] 英雄模型分配校验通过：{heroes.Length} 个英雄各挂一份不同模型" +
                    $"（{distinct.Count} 种），白盒外观均已让位。\n  · " + string.Join("；", modelNames));
                return;
            }

            Debug.LogError(
                "[AutoSceneBuilder] 英雄模型分配校验失败：\n  · " + string.Join("\n  · ", problems));
        }

        /// <summary>核对一个预制体上的动画接线。</summary>
        /// <param name="problems">问题清单。</param>
        /// <param name="prefab">目标预制体。</param>
        /// <param name="label">中文标签。</param>
        /// <param name="controller">期望的控制器。</param>
        private static void AppendAnimatorProblems(
            List<string> problems, GameObject prefab, string label, AnimatorController controller)
        {            if (prefab == null)
            {
                problems.Add($"{label}不存在（无法校验动画接线）");
                return;
            }

            Animator animator = prefab.GetComponent<Animator>();

            if (animator == null)
            {
                problems.Add($"{label}（{prefab.name}）上没有 Animator，运行期不会有任何动画");
            }
            else
            {
                if (controller != null && animator.runtimeAnimatorController != controller)
                {
                    problems.Add(
                        $"{label}.Animator 的控制器为 " +
                        $"{(animator.runtimeAnimatorController != null ? animator.runtimeAnimatorController.name : "（空）")}，" +
                        $"应为 {controller.name}");
                }

                if (animator.applyRootMotion)
                {
                    problems.Add($"{label}.Animator 的 applyRootMotion 仍为 true（会与 NavMeshAgent 争夺位移）");
                }
            }

            AnimationComponent animation = prefab.GetComponent<AnimationComponent>();

            if (animation == null)
            {
                problems.Add($"{label}（{prefab.name}）上没有 AnimationComponent，逻辑状态不会映射到动画");
                return;
            }

            SerializedObject serialized = new SerializedObject(animation);

            // 引用字段：必须已注入，且指向预制体上那个 Animator。
            SerializedProperty animatorProperty = serialized.FindProperty("animator");

            if (animatorProperty == null)
            {
                problems.Add($"{label}：AnimationComponent 上找不到字段 animator（字段可能已被改名）");
            }
            else if (animatorProperty.objectReferenceValue == null)
            {
                problems.Add($"{label}.AnimationComponent.animator 为空（不会驱动任何动画）");
            }
            else if (animator != null && animatorProperty.objectReferenceValue != animator)
            {
                problems.Add($"{label}.AnimationComponent.animator 指向的不是预制体根节点上的 Animator");
            }

            // 参数名字段：只要存在就够（值由 AnimationComponent 的 OnValidate 兜底为默认名）。
            AppendStringFieldProblem(problems, serialized, label, "speedParameterName");
            AppendStringFieldProblem(problems, serialized, label, "attackTriggerName");
            AppendStringFieldProblem(problems, serialized, label, "spellTriggerName");
            AppendStringFieldProblem(problems, serialized, label, "dieTriggerName");
        }

        /// <summary>核对一个字符串字段是否存在且非空。</summary>
        /// <param name="problems">问题清单。</param>
        /// <param name="serialized">组件包装。</param>
        /// <param name="label">中文标签。</param>
        /// <param name="fieldName">字段名。</param>
        private static void AppendStringFieldProblem(
            List<string> problems, SerializedObject serialized, string label, string fieldName)
        {
            SerializedProperty property = serialized.FindProperty(fieldName);

            if (property == null)
            {
                problems.Add($"{label}：AnimationComponent 上找不到字段 {fieldName}（字段可能已被改名）");
                return;
            }

            if (string.IsNullOrEmpty(property.stringValue))
            {
                problems.Add($"{label}.{fieldName} 为空（该通道不会触发任何动画）");
            }
        }

        /// <summary>核对控制器本身：四个参数与五个状态必须齐全。</summary>
        /// <param name="problems">问题清单。</param>
        /// <param name="controller">目标控制器。</param>
        private static void AppendControllerProblems(List<string> problems, AnimatorController controller)
        {
            if (controller == null)
            {
                problems.Add($"动画控制器 {UnitAnimatorControllerPath} 不存在（无法生成，请检查路径可写）");
                return;
            }

            AppendParameterProblem(problems, controller, AnimSpeedParameter);
            AppendParameterProblem(problems, controller, AnimAttackTrigger);
            AppendParameterProblem(problems, controller, AnimSpellTrigger);
            AppendParameterProblem(problems, controller, AnimDieTrigger);

            AnimatorStateMachine machine = controller.layers[0].stateMachine;

            AppendStateProblem(problems, machine, AnimStateIdle);
            AppendStateProblem(problems, machine, AnimStateRun);
            AppendStateProblem(problems, machine, AnimStateAttack);
            AppendStateProblem(problems, machine, AnimStateSpell);
            AppendStateProblem(problems, machine, AnimStateDie);

            if (machine.defaultState == null)
            {
                problems.Add("控制器的默认状态为空（运行期会停在第一个状态且不报错）");
            }

            // 片段：五个状态都应当有 motion，否则该状态播出来是"静止的"（T-pose 就是这么来的）。
            AppendStateMotionProblem(problems, machine, AnimStateIdle);
            AppendStateMotionProblem(problems, machine, AnimStateRun);
            AppendStateMotionProblem(problems, machine, AnimStateAttack);
            AppendStateMotionProblem(problems, machine, AnimStateSpell);
            AppendStateMotionProblem(problems, machine, AnimStateDie);
        }

        /// <summary>核对某个状态的 motion（动画片段）是否已接上。</summary>
        /// <param name="problems">问题清单。</param>
        /// <param name="machine">状态机。</param>
        /// <param name="stateName">状态名。</param>
        private static void AppendStateMotionProblem(
            List<string> problems, AnimatorStateMachine machine, string stateName)
        {
            AnimatorState state = FindAnimatorState(machine, stateName);

            if (state == null)
            {
                // 状态缺失已由 AppendStateProblem 报过，这里不重复。
                return;
            }

            if (state.motion == null)
            {
                problems.Add($"状态 {stateName} 没有挂动画片段（该状态播出来是静止的 = T-pose 观感）");
            }
        }

        /// <summary>核对控制器上是否存在某个参数。</summary>
        /// <param name="problems">问题清单。</param>
        /// <param name="controller">控制器。</param>
        /// <param name="parameterName">参数名。</param>
        private static void AppendParameterProblem(
            List<string> problems, AnimatorController controller, string parameterName)
        {
            AnimatorControllerParameter[] parameters = controller.parameters;

            for (int i = 0; i < parameters.Length; i++)
            {
                if (string.Equals(parameters[i].name, parameterName, System.StringComparison.Ordinal))
                {
                    return;
                }
            }

            problems.Add($"控制器缺少参数 {parameterName}（AnimationComponent 写它时会静默失败）");
        }

        /// <summary>核对控制器上是否存在某个状态。</summary>
        /// <param name="problems">问题清单。</param>
        /// <param name="machine">状态机。</param>
        /// <param name="stateName">状态名。</param>
        private static void AppendStateProblem(
            List<string> problems, AnimatorStateMachine machine, string stateName)
        {
            if (FindAnimatorState(machine, stateName) == null)
            {
                problems.Add($"控制器缺少状态 {stateName}");
            }
        }

        #endregion

        #region 通用小工具

        /// <summary>
        /// 递归确保目录存在（缺失的中间层一并创建）。
        /// 【为什么需要递归】AssetDatabase.CreateFolder 只创建一级，父目录不存在时直接返回空串
        /// 且不报错 —— "目录没建出来"会一路静默到"资产保存到不存在的位置"，排查成本极高。
        /// </summary>
        /// <param name="folderPath">目标目录（形如 Assets/Art/Models/Heroes）。</param>
        /// <returns>目录最终存在返回 true。</returns>
        private static bool EnsureFolderRecursive(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath) || AssetDatabase.IsValidFolder(folderPath))
            {
                return true;
            }

            string parent = System.IO.Path.GetDirectoryName(folderPath);

            if (string.IsNullOrEmpty(parent))
            {
                return false;
            }

            parent = parent.Replace('\\', '/');

            if (!EnsureFolderRecursive(parent))
            {
                return false;
            }

            string leaf = System.IO.Path.GetFileName(folderPath);

            if (string.IsNullOrEmpty(leaf))
            {
                return false;
            }

            return !string.IsNullOrEmpty(AssetDatabase.CreateFolder(parent, leaf));
        }

        /// <summary>把"可选资产是否存在"拼成一句可读文本（仅用于日志）。</summary>
        /// <param name="asset">已加载的资产，允许为 null。</param>
        /// <param name="assetPath">资产路径。</param>
        /// <returns>存在时为资产名，缺失时为带路径的提示。</returns>
        private static string DescribeOptionalAsset(Object asset, string assetPath)
        {
            return asset != null ? asset.name : $"（未导入，槽位预留 {assetPath}）";
        }

        #endregion
    }
}
