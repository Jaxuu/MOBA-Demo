using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using MOBA.Core;
using MOBA.Gameplay;
using MOBA.Skills;
using MOBA.UI;
using MOBA.Units;

namespace MOBA.Editor
{
    /// <summary>
    /// AutoSceneBuilder 的「UI 与可视化装配」分册（阶段七，步骤 17）。
    ///
    /// 【为什么拆成 partial 分册】主文件在阶段六之后已经接近 3000 行，再塞进约 700 行 UI 装配会变得难以维护。
    /// partial 是编译期语法糖：菜单入口仍然只有一个（MOBA Demo/一键组装测试战场），
    /// 调用链、撤销组、清理逻辑全部与主文件共享，行为零变化。
    ///
    /// 【本分册产出的场景结构】
    /// <code>
    /// UIRoot                          [Canvas: Screen Space - Overlay + CanvasScaler]
    /// ├── TopBar                      [ScoreboardView + 2 个计分文本]
    /// ├── KillFeed                    [KillFeedView + 5 个播报槽位]
    /// ├── BottomBar                   [HeroHUDView + 头像/血条/蓝条 + Q/W 技能槽]
    /// ├── RespawnOverlay              [RespawnOverlayView（常驻激活）+ Panel（会被开关）]
    /// └── MatchResultView             [阶段四的结算界面，此前从未被装配进场景，本步骤补齐]
    ///
    /// WorldUIRoot                     [WorldHealthBarManager]
    /// ├── HealthBarTemplate           [World Space Canvas + WorldHealthBarView]（非激活，池模板）
    /// └── PopupCanvas                 [World Space Canvas + DamagePopupManager]
    ///     └── PopupTemplate           [Text + DamagePopupView]（非激活，池模板）
    /// </code>
    ///
    /// 【零手工拖拽的落实方式】本分册把每个视图组件需要的引用逐个用 SerializedObject 写进去；
    /// 血条与飘字【不挂在单位上】，而是由管理器订阅 EntityRegistry 统一挂载，
    /// 因此 HeroPrefab / MinionPrefab / 基地 / 塔的预制体一行都不用改。
    ///
    /// 【前置依赖】Packages/manifest.json 必须声明 com.unity.ugui。
    /// 这是编译期硬依赖：缺了它，本文件与全部 UI 脚本会直接 CS0246（这本身就是最清晰的提示）。
    /// </summary>
    public static partial class AutoSceneBuilder
    {
        #region 常量：对象名

        /// <summary>屏幕空间 HUD 的根对象名（纳入接管清单，重复执行时先删后建）。</summary>
        private const string UIRootName = "UIRoot";

        /// <summary>世界空间 UI 的根对象名（纳入接管清单）。</summary>
        private const string WorldUIRootName = "WorldUIRoot";

        private const string TopBarName = "TopBar";
        private const string ScoreboardName = "Scoreboard";
        private const string KillFeedName = "KillFeed";
        private const string KillFeedEntryPrefix = "KillFeedEntry_";
        private const string BottomBarName = "BottomBar";
        private const string PortraitName = "Portrait";
        private const string HealthBarBgName = "HealthBarBackground";
        private const string HealthBarFillName = "HealthBarFill";
        private const string HealthTextName = "HealthText";
        private const string ManaBarBgName = "ManaBarBackground";
        private const string ManaBarFillName = "ManaBarFill";
        private const string ManaTextName = "ManaText";
        private const string SkillSlotQName = "SkillSlot_Q";
        private const string SkillSlotWName = "SkillSlot_W";
        private const string SkillSlotEName = "SkillSlot_E";
        private const string SkillSlotRName = "SkillSlot_R";

        /// <summary>技能槽的边长（像素）。</summary>
        private const float SkillSlotSize = 56f;

        /// <summary>相邻技能槽的间距（像素）。Q/W/E/R 四个槽共占 4 × 56 + 3 × 8 = 248 像素。</summary>
        private const float SkillSlotSpacing = 8f;

        /// <summary>第一个技能槽相对 HUD 左下角的偏移（像素）。与血条 / 蓝条的左边界对齐。</summary>
        private static readonly Vector2 SkillSlotOrigin = new Vector2(104f, 8f);
        private const string SlotBackgroundName = "Background";
        private const string SlotIconName = "Icon";
        private const string SlotCooldownOverlayName = "CooldownOverlay";
        private const string SlotCooldownTextName = "CooldownText";
        private const string SlotCostTextName = "CostText";
        private const string SlotKeyTextName = "KeyText";
        private const string RespawnOverlayName = "RespawnOverlay";
        private const string RespawnPanelName = "Panel";
        private const string RespawnTitleName = "Title";
        private const string RespawnCountdownName = "Countdown";
        private const string MatchResultViewName = "MatchResultView";
        private const string HealthBarTemplateName = "HealthBarTemplate";
        private const string WorldHealthBarManagerName = "WorldHealthBarManager";
        private const string HealthBarBgChildName = "Background";
        private const string HealthBarFillChildName = "Fill";
        private const string HealthBarShieldChildName = "Shield";
        private const string PopupCanvasName = "PopupCanvas";
        private const string PopupTemplateName = "PopupTemplate";
        private const string MinimapName = "Minimap";
        private const string MinimapBackgroundName = "MinimapBackground";
        private const string MinimapAreaName = "MinimapArea";
        private const string MinimapMarkerTemplateName = "MinimapMarkerTemplate";

        /// <summary>占位美术资产所在目录（README 目录规范里的 Art/Textures 之外单开 Art/UI，避免与地面贴图混放）。</summary>
        private const string UIArtFolder = "Assets/Art/UI";

        /// <summary>
        /// 技能正式图标的目录（阶段九新增）。约定：<c>HeroSkillQ.png</c> ↔ <c>HeroSkillQ.asset</c>，
        /// 文件名与技能槽一一对应，因此工具可以按槽位名拼路径，不需要一张映射表。
        /// </summary>
        private const string SkillIconFolder = "Assets/Art/Textures/UI";

        /// <summary>占位白图路径。所有纯色 Image 与 CD 径向遮罩都用它，不引入任何外部美术资源。</summary>
        private const string UIWhiteSpritePath = UIArtFolder + "/UIWhite.png";

        /// <summary>
        /// 本次组装使用的占位白图（由 EnsureUIWhiteSprite 赋值）。
        /// 让所有纯色 Image 共用同一张贴图，是为了让它们能被合批到同一个 draw call：
        /// 若一部分 Image 用这张图、另一部分用 sprite = null（uGUI 会回落到内置白纹理），
        /// 就会出现两张不同的主贴图，HUD 的批次会平白翻倍。
        /// </summary>
        private static Sprite uiWhiteSprite;

        #endregion

        #region 常量：布局与配色

        /// <summary>HUD 参考分辨率（CanvasScaler 用；与 1080p 对齐，验收标准也是按 1080p 写的）。</summary>
        private static readonly Vector2 UIReferenceResolution = new Vector2(1920f, 1080f);

        private static readonly Vector2 AnchorTopLeft = new Vector2(0f, 1f);
        private static readonly Vector2 AnchorTopCenter = new Vector2(0.5f, 1f);
        private static readonly Vector2 AnchorBottomLeft = new Vector2(0f, 0f);
        private static readonly Vector2 AnchorBottomRight = new Vector2(1f, 0f);
        private static readonly Vector2 AnchorCenter = new Vector2(0.5f, 0.5f);
        private static readonly Vector2 AnchorStretchMin = Vector2.zero;
        private static readonly Vector2 AnchorStretchMax = Vector2.one;

        /// <summary>血条模板的 Canvas 尺寸（世界单位）：1.2 米宽 × 0.12 米高。</summary>
        private static readonly Vector2 HealthBarCanvasSize = new Vector2(1.2f, 0.12f);

        /// <summary>共享飘字画布的尺寸与缩放：5000 × 0.01 = 覆盖 ±25 米世界范围，字号 48 约合 0.48 米高。</summary>
        private static readonly Vector2 PopupCanvasSize = new Vector2(5000f, 5000f);
        private const float PopupCanvasScale = 0.01f;

        private static readonly Color HudPanelColor = new Color(0f, 0f, 0f, 0.45f);
        private static readonly Color BarBackgroundColor = new Color(0.06f, 0.06f, 0.06f, 0.85f);
        private static readonly Color HealthBarAllyColor = new Color(0.25f, 0.85f, 0.35f, 1f);
        private static readonly Color HealthBarEnemyColor = new Color(0.9f, 0.25f, 0.25f, 1f);
        private static readonly Color ShieldBarColor = new Color(0.55f, 0.8f, 1f, 0.95f);
        private static readonly Color ManaBarColor = new Color(0.25f, 0.5f, 1f, 1f);
        private static readonly Color PortraitColor = new Color(0.3f, 0.62f, 1f, 1f);
        private static readonly Color SkillSlotColor = new Color(0.18f, 0.18f, 0.22f, 1f);
        private static readonly Color SkillIconPlaceholderColor = new Color(0.55f, 0.6f, 0.75f, 1f);
        private static readonly Color CooldownOverlayColor = new Color(0f, 0f, 0f, 0.65f);
        private static readonly Color DamagePopupColor = new Color(1f, 0.35f, 0.25f, 1f);
        private static readonly Color TextPrimaryColor = new Color(0.95f, 0.95f, 0.95f, 1f);
        private static readonly Color TextDimColor = new Color(0.8f, 0.8f, 0.8f, 1f);
        private static readonly Color RespawnTitleColor = new Color(1f, 0.35f, 0.3f, 1f);
        private static readonly Color RespawnCountdownColor = new Color(1f, 0.92f, 0.6f, 1f);

        /// <summary>
        /// 血条预生成数量。
        /// 【阶段八由 24 提到 48】PrefabPool 的契约是"池空返回 null + 告警一次，绝不 Instantiate"，
        /// 即容量就是硬上限。5v5 后同屏单位量级为 10 英雄 + 6 塔 + 2 基地 + 双兵线（每波 3 个 × 2 方），
        /// 24 条会稳定溢出，症状是"一部分单位头顶没有血条"且只报一条 Warning。
        /// </summary>
        private const int HealthBarPrewarmCount = 48;

        /// <summary>小地图标记的预生成数量。与血条同源（凡带生命组件的实体都会上图），因此同步提到 64。</summary>
        private const int MinimapMarkerPrewarmCount = 64;

        /// <summary>
        /// 小地图面板的基准宽度（像素）。
        ///
        /// 【阶段八：220 → 300】桥长从 78 米扩到 120 米后，若宽度不变，单位标记会挤在一起
        /// （英雄标记 13px 在 220px 上代表 7 米的跨度，5v5 团战时完全糊成一团）。
        /// 高度仍按战场长宽比换算，见 ResolveMinimapSize。
        /// </summary>
        private static readonly Vector2 MinimapSize = new Vector2(300f, 300f);

        /// <summary>
        /// 小地图面板的最小高度（像素）。
        ///
        /// 下限的作用：战场极端狭长时（长宽比很大），按比例算出的高度会小到只剩一条线，
        /// 标记互相重叠、完全不可读。
        /// 当前 120×40 的桥按比例得到 300 × 100 像素（比例完全正确，不会被本下限钳制）；
        /// 保留它只是为日后进一步拉长地图时兜底。
        /// </summary>
        private const float MinimapMinHeight = 70f;

        private static readonly Color MinimapBackgroundColor = new Color(0.05f, 0.07f, 0.1f, 0.75f);

        /// <summary>飘字预生成数量。阶段八同屏伤害事件密度上升，同步由 32 提到 48。</summary>
        private const int PopupPrewarmCount = 48;

        /// <summary>播报槽位数量（固定，不池化）。</summary>
        private const int KillFeedEntryCount = 5;

        #endregion

        #region 步骤 17：入口

        /// <summary>
        /// 步骤 17：装配 UI 与可视化。
        ///
        /// 调用时机：步骤 15（英雄实例化）之后、步骤 16（收尾校验）之前 —— UI 需要英雄实例作为注入源。
        /// 失败策略：本步骤的任何失败都不影响战场本身可用（战场是逻辑，UI 是表现），
        /// 因此只报错 + 记入 notes，不中止整次组装。
        /// </summary>
        /// <param name="hero">玩家英雄实例（HUD 与血条配色都依赖它）。</param>
        /// <param name="matchController">对局控制器（复活遮罩、结算界面、复活时长配置都依赖它）。</param>
        /// <param name="notes">缺口修补记录（追加进收尾日志）。</param>
        private static void BuildUIAssembly(
            HeroController hero, MatchController matchController, List<string> notes)
        {
            // ---------- 17.1 前置声明检查 ----------
            // 说明：uGUI 是【编译期】依赖（缺包时本文件与全部 UI 脚本会直接 CS0246，那是最清晰的提示）。
            // 这里额外读一次 manifest，是为了给"有人后来手动删掉那一行"这种情况留下一条可读的线索。
            EnsureUguiPackageDeclared(notes);

            // ---------- 17.2 占位美术资产（白图 + 字体） ----------
            Sprite whiteSprite = EnsureUIWhiteSprite(notes);
            Font uiFont = EnsureUIFont(notes);

            // 记录到静态字段，供 CreateImage 在调用方传 null 时统一回落（保证全 HUD 共用一张主贴图）。
            uiWhiteSprite = whiteSprite;

            // ---------- 17.3 屏幕空间 HUD ----------
            // 阵营先算出来：屏幕空间的计分板 / 播报 / 小地图与世界空间的血条都要用它决定配色。
            TeamType localTeam = hero != null ? hero.Team : TeamType.Player;

            GameObject uiRoot = CreateUIRoot(UIRootName);

            ScoreboardView scoreboard = BuildScoreboard(uiRoot.transform, uiFont);
            KillFeedView killFeed = BuildKillFeed(uiRoot.transform, uiFont);
            HeroHUDView hud = BuildHeroHUD(uiRoot.transform, uiFont, hero);

            // 小地图（D6 批准纳入本轮）：右下角，显示双方单位 / 建筑 / 英雄。
            MinimapView minimap = BuildMinimap(uiRoot.transform, localTeam, notes);

            // 复活遮罩刻意最后创建：它在层级里排在最后 → 绘制在最上层，
            // 阵亡期间不会出现"小地图 / 计分板浮在遮罩之上"的观感问题。
            RespawnOverlayView respawnOverlay = BuildRespawnOverlay(uiRoot.transform, uiFont);

            // 阶段四的结算界面：类早就写好了，但此前【没有任何工具把它装配进场景】，
            // 结果是基地被摧毁后只有一行 Console 日志、屏幕上什么都不显示。
            // 本步骤一并补齐（只创建对象 + 注入引用，不改 MatchResultView 的任何代码）。
            MatchResultView resultView = BuildMatchResultView(uiRoot.gameObject, matchController);

            // ---------- 17.4 击杀统计（挂在 Battlefield 上，与 MatchController 同物体） ----------
            MatchStatsTracker statsTracker = AttachMatchStatsTracker(matchController);

            // ---------- 17.5 世界空间 UI（血条 + 飘字） ----------
            Camera mainCamera = FindMainCamera();

            GameObject worldUIRoot = CreateUIObject(WorldUIRootName, null);
            SetupRect(worldUIRoot, AnchorCenter, AnchorCenter, AnchorCenter, Vector2.zero, Vector2.zero);

            WorldHealthBarManager barManager = BuildHealthBars(worldUIRoot.transform, whiteSprite, mainCamera, localTeam);
            DamagePopupManager popupManager = BuildDamagePopups(worldUIRoot.transform, uiFont, mainCamera);

            // ---------- 17.6 依赖注入（全部走 SerializedObject） ----------
            InjectScreenSpaceUI(scoreboard, killFeed, hud, respawnOverlay, resultView, statsTracker, matchController, localTeam);
            InjectWorldSpaceUI(barManager, popupManager, mainCamera, localTeam);

            // 复活编排需要英雄引用；复活时长走 MatchConfigData（只补空、不覆盖已有值）。
            AssignObjectReference(matchController, "playerHero", hero);
            PatchFloatIfUnset(matchController, "respawnFallbackTime", 8f);

            // 技能图标占位：只补空字段，绝不覆盖已经配好的图标（保资产 guid 与人工配置）。
            PatchSkillIcons(whiteSprite, notes);

            // ---------- 17.7 收尾校验（只提示不改场景） ----------
            ValidateUISetup(
                uiFont, whiteSprite, barManager, popupManager, hud, scoreboard, killFeed, minimap,
                respawnOverlay, resultView, statsTracker, notes);
        }

        #endregion

        #region 17.1 前置声明检查

        /// <summary>
        /// 检查 Packages/manifest.json 是否声明了 com.unity.ugui。
        /// 缺失时给出人话提示并记入 notes（不报错中断：此时整个编辑器程序集其实已经编译不过了，
        /// 能看到这条日志的场景是"刚改过 manifest 还没重编译"）。
        /// </summary>
        /// <param name="notes">缺口修补记录。</param>
        private static void EnsureUguiPackageDeclared(List<string> notes)
        {
            const string manifestPath = "Packages/manifest.json";

            if (!File.Exists(manifestPath))
            {
                notes.Add($"读不到 {manifestPath}，无法确认 uGUI 依赖声明");
                return;
            }

            string content = File.ReadAllText(manifestPath);

            if (content.Contains("com.unity.ugui"))
            {
                return;
            }

            Debug.LogError(
                "[AutoSceneBuilder] Packages/manifest.json 中没有声明 com.unity.ugui，" +
                "UI 相关的脚本会编译失败（CS0246）。请加入 \"com.unity.ugui\": \"1.0.0\"（编辑器内置包，离线可解析）。");

            notes.Add("manifest.json 缺少 com.unity.ugui 声明");
        }

        #endregion

        #region 17.2 占位美术资产

        /// <summary>
        /// 确保占位白图存在（4×4 纯白 PNG，导入为 Sprite）。
        ///
        /// 【为什么不用 "Image 不设 Sprite 也能显示白色方块"这个特性】那是 uGUI 依赖"贴图 UV 取到 (0,0)"
        /// 的隐式行为，对 Filled / Radial360 这类会重新计算网格的填充模式不够可靠。
        /// 生成一张真实的白图是一劳永逸的做法：所有纯色块与 CD 遮罩都用它，行为完全确定。
        ///
        /// 【为什么只创建不覆盖】占位图一旦被人手工替换成正式图（仍按 Sprite 导入），重复执行工具不应该把它冲掉。
        /// 反过来，如果同名文件不是 Sprite 导入（例如被当成普通贴图放进来），本方法会重建它 ——
        /// 那种状态下 Image 根本用不了它，留着才是真的坑。
        /// </summary>
        /// <param name="notes">缺口修补记录。</param>
        /// <returns>占位 Sprite；创建失败时为 null。</returns>
        private static Sprite EnsureUIWhiteSprite(List<string> notes)
        {
            Sprite existing = AssetDatabase.LoadAssetAtPath<Sprite>(UIWhiteSpritePath);
            if (existing != null)
            {
                return existing;
            }

            if (!Directory.Exists(UIArtFolder))
            {
                Directory.CreateDirectory(UIArtFolder);
                AssetDatabase.Refresh();
            }

            // 生成 4×4 纯白贴图并落盘。4×4 而不是 1×1：部分平台对 1×1 纹理有额外限制，
            // 且 4×4 仍会走正常的纹理压缩路径，能提前暴露压缩导致的边缘问题。
            Texture2D texture = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            Color32[] pixels = new Color32[16];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = new Color32(255, 255, 255, 255);
            }

            texture.SetPixels32(pixels);
            texture.Apply();

            byte[] png = texture.EncodeToPNG();

            // 编辑期临时创建的贴图必须立刻销毁：它只用来产出一份 PNG 字节。
            // 显式写 UnityEngine.Object，避免与任何同名类型产生歧义。
            UnityEngine.Object.DestroyImmediate(texture);

            File.WriteAllBytes(UIWhiteSpritePath, png);
            AssetDatabase.ImportAsset(UIWhiteSpritePath, ImportAssetOptions.ForceUpdate);

            // 关键：默认导入设置是 Texture，必须改成 Sprite 才能被 Image 使用。
            TextureImporter importer = AssetImporter.GetAtPath(UIWhiteSpritePath) as TextureImporter;

            if (importer != null)
            {
                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.alphaIsTransparency = true;
                importer.mipmapEnabled = false;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.filterMode = FilterMode.Bilinear;
                importer.spritePixelsPerUnit = 100f;
                importer.SaveAndReimport();
            }
            else
            {
                Debug.LogError(
                    $"[AutoSceneBuilder] 无法为 {UIWhiteSpritePath} 取到 TextureImporter，" +
                    "它可能仍按 Texture 导入，Image 将显示不出来。请手工把它的 Texture Type 改成 Sprite。");
            }

            Sprite created = AssetDatabase.LoadAssetAtPath<Sprite>(UIWhiteSpritePath);

            if (created == null)
            {
                Debug.LogError(
                    $"[AutoSceneBuilder] 占位白图 {UIWhiteSpritePath} 创建失败，" +
                    "所有纯色 Image 与技能 CD 遮罩都会不可见。");
                notes.Add("占位白图创建失败");
                return null;
            }

            notes.Add($"生成占位白图 {UIWhiteSpritePath}（4×4，导入为 Sprite）");
            return created;
        }

        /// <summary>
        /// 取内置字体，供全部 Text 使用。三级回退与运行期的 UIFontProvider 保持一致。
        ///
        /// 【为什么必须在编辑期就注入】运行期创建的 Font（CreateDynamicFontFromOSFont）
        /// 不是资产，无法被序列化进场景 —— 编辑期注入的必须是"内置资源引用"。
        /// 运行期的兜底（UIFontProvider）只是第二道保险。
        /// </summary>
        /// <param name="notes">缺口修补记录。</param>
        /// <returns>字体；三级全部失败时为 null（此时收尾校验会给出明确结论）。</returns>
        private static Font EnsureUIFont(List<string> notes)
        {
            // LegacyRuntime.ttf 是 Unity/Tuanjie 2022 起的内置字体名（uGUI 源码自身也用它）。
            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            if (font == null)
            {
                // 老版本的内置字体名，作为二级回退。
                font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            }

            if (font == null)
            {
                Debug.LogError(
                    "[AutoSceneBuilder] 内置字体（LegacyRuntime.ttf / Arial.ttf）都读取失败，" +
                    "工具无法把字体注入到 Text 上。运行期 UIFontProvider 会尝试系统字体兜底，" +
                    "但界面可能显示为方块。");
                notes.Add("内置字体读取失败（依赖运行期兜底）");
                return null;
            }

            return font;
        }

        /// <summary>
        /// 给四个技能资产的 Icon 字段补图（阶段九扩展）。
        ///
        /// 【阶段九变更：从"只补 Q/W 的占位白图"扩为"四个槽位都补，且优先用正式图标"】
        ///  · 阶段七只覆盖 Q / W 两槽（那时玩家只有两个技能），E / R 的 Icon 一直是空的
        ///    —— 阶段八扩到四槽后，HUD 上后两个技能槽一直没有图标，只是没人注意到。
        ///  · 现在按 `Assets/Art/Textures/UI/HeroSkill{Q,W,E,R}.png` 找正式图标（导入为 Sprite），
        ///    找到就注入；找不到才退回占位白图。
        ///
        /// 【优先级规则 —— 与"只补空、不覆盖"的既有原则如何共存】
        ///  · 正式图标存在时：无条件写入（这样"从零克隆仓库 → 一键组装"就能得到正确图标，
        ///    满足 README §6.1「手工拖的引用不算交付」）；
        ///  · 正式图标不存在时：沿用 `PatchObjectReferenceIfUnset`（只补空、不覆盖），
        ///    绝不把美术手工指定的图标冲成白图。
        /// </summary>
        /// <param name="whiteSprite">占位 Sprite。</param>
        /// <param name="notes">缺口修补记录。</param>
        private static void PatchSkillIcons(Sprite whiteSprite, List<string> notes)
        {
            (string skillPath, string slotLabel)[] entries =
            {
                (HeroSkillQAssetPath, "Q"),
                (HeroSkillWAssetPath, "W"),
                (HeroSkillEAssetPath, "E"),
                (HeroSkillRAssetPath, "R"),
            };

            for (int i = 0; i < entries.Length; i++)
            {
                string skillPath = entries[i].skillPath;
                string slotLabel = entries[i].slotLabel;

                SkillData skill = AssetDatabase.LoadAssetAtPath<SkillData>(skillPath);

                if (skill == null)
                {
                    continue;
                }

                // 正式图标优先：约定路径与技能槽同名（HeroSkillQ.png ↔ HeroSkillQ.asset）。
                string iconPath = SkillIconFolder + "/HeroSkill" + slotLabel + ".png";
                Sprite formalIcon = AssetDatabase.LoadAssetAtPath<Sprite>(iconPath);

                if (formalIcon != null)
                {
                    // 用"值不同才写"的写入器：图标已经正确时不产生任何资产改动，
                    // 重复执行组装不会让四个技能资产每次都变脏（幂等性要求）。
                    SerializedObject serialized = new SerializedObject(skill);

                    if (SetObjectReferenceIfDifferent(serialized, "icon", formalIcon, out bool fieldFound))
                    {
                        serialized.ApplyModifiedProperties();
                        notes.Add($"{System.IO.Path.GetFileName(skillPath)} 图标 ← {iconPath}（正式图标）");
                    }

                    if (!fieldFound)
                    {
                        // SetObjectReferenceIfDifferent 已经报过"字段可能被改名"，这里不再重复刷屏。
                        continue;
                    }

                    continue;
                }

                if (whiteSprite != null && PatchObjectReferenceIfUnset(skill, "icon", whiteSprite))
                {
                    notes.Add($"{System.IO.Path.GetFileName(skillPath)} 补写占位技能图标（{iconPath} 未导入）");
                }
            }
        }

        #endregion

        #region 创建辅助

        /// <summary>创建一个 UI 物体（自带 RectTransform）并登记撤销。</summary>
        /// <param name="objectName">对象名。</param>
        /// <param name="parent">父节点，可为 null（作为根对象）。</param>
        /// <returns>创建出来的物体。</returns>
        private static GameObject CreateUIObject(string objectName, Transform parent)
        {
            // 用构造函数带 typeof(RectTransform)：先建普通 Transform 再 AddComponent&lt;RectTransform&gt;
            // 会触发引擎的组件替换流程，不如一次性建对。
            GameObject created = new GameObject(objectName, typeof(RectTransform));

            if (parent != null)
            {
                created.transform.SetParent(parent, false);
            }

            Undo.RegisterCreatedObjectUndo(created, "创建 " + objectName);
            return created;
        }

        /// <summary>设置 RectTransform 的锚点、轴心、位置与尺寸。</summary>
        private static RectTransform SetupRect(
            GameObject target,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 pivot,
            Vector2 anchoredPosition,
            Vector2 sizeDelta)
        {
            RectTransform rect = target.GetComponent<RectTransform>();
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = pivot;
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = sizeDelta;
            return rect;
        }

        /// <summary>
        /// 创建一个纯色 Image。
        /// raycastTarget 一律置 false：阶段七的 HUD 全是只读显示，没有任何点击交互，
        /// 开着它只会让 UI 事件系统每帧多做一次全屏射线检测。
        /// </summary>
        private static Image CreateImage(
            Transform parent,
            string objectName,
            Sprite sprite,
            Color color,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 pivot,
            Vector2 anchoredPosition,
            Vector2 sizeDelta)
        {
            GameObject go = CreateUIObject(objectName, parent);
            Image image = Undo.AddComponent<Image>(go);

            // 统一回落到本次组装的占位白图（理由见 uiWhiteSprite 的注释：保证单一主贴图、可合批）。
            image.sprite = sprite != null ? sprite : uiWhiteSprite;
            image.color = color;
            image.raycastTarget = false;

            SetupRect(go, anchorMin, anchorMax, pivot, anchoredPosition, sizeDelta);
            return image;
        }

        /// <summary>创建一个水平填充的 Image（血条 / 蓝条 / 护盾条用）。</summary>
        private static Image CreateHorizontalFillImage(
            Transform parent, string objectName, Sprite sprite, Color color,
            Vector2 anchoredPosition, Vector2 sizeDelta)
        {
            Image image = CreateImage(
                parent, objectName, sprite, color,
                AnchorBottomLeft, AnchorBottomLeft, AnchorBottomLeft, anchoredPosition, sizeDelta);

            image.type = Image.Type.Filled;
            // 顺序注意：fillMethod 的 setter 会把 fillOrigin 重置为 0，所以必须先设 fillMethod 再设 fillOrigin。
            image.fillMethod = Image.FillMethod.Horizontal;
            image.fillOrigin = 0; // 0 = 左端（Image.OriginHorizontal.Left）
            image.fillAmount = 1f;

            return image;
        }

        /// <summary>创建一个径向（Radial360）填充的 Image（技能 CD 遮罩用）。</summary>
        private static Image CreateRadialFillImage(
            Transform parent, string objectName, Sprite sprite, Color color)
        {
            Image image = CreateImage(
                parent, objectName, sprite, color,
                AnchorStretchMin, AnchorStretchMax, AnchorCenter, Vector2.zero, Vector2.zero);

            image.type = Image.Type.Filled;
            image.fillMethod = Image.FillMethod.Radial360;
            image.fillOrigin = (int)Image.Origin360.Top; // 从 12 点方向起
            image.fillClockwise = true;                  // 遮罩随时间顺时针消退（uGUI 默认值，也是主流 MOBA 观感）
            image.fillAmount = 1f;
            image.enabled = false;                       // 未冷却时不画遮罩

            return image;
        }

        /// <summary>
        /// 创建一个 Text。
        /// 溢出模式设为 Overflow：HUD 里的文本都是"数字可能变长"的场景（如 500/500），
        /// 一旦被 RectTransform 裁掉就会出现"数字少了一位"这种很难察觉的错误。
        /// </summary>
        private static Text CreateText(
            Transform parent,
            string objectName,
            string content,
            Font font,
            int fontSize,
            TextAnchor alignment,
            Color color,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 pivot,
            Vector2 anchoredPosition,
            Vector2 sizeDelta)
        {
            GameObject go = CreateUIObject(objectName, parent);
            Text text = Undo.AddComponent<Text>(go);

            text.font = font;
            text.fontSize = fontSize;
            text.alignment = alignment;
            text.color = color;
            text.text = content;
            text.raycastTarget = false;
            text.supportRichText = false; // 内容里不会出现富文本标记，关掉可省一次解析
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;

            SetupRect(go, anchorMin, anchorMax, pivot, anchoredPosition, sizeDelta);
            return text;
        }

        /// <summary>创建屏幕空间 HUD 的根 Canvas（Overlay + CanvasScaler）。</summary>
        /// <param name="objectName">对象名。</param>
        /// <returns>创建出来的 Canvas 物体。</returns>
        private static GameObject CreateUIRoot(string objectName)
        {
            GameObject root = CreateUIObject(objectName, null);

            Canvas canvas = Undo.AddComponent<Canvas>(root);
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 0;

            CanvasScaler scaler = Undo.AddComponent<CanvasScaler>(root);
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = UIReferenceResolution;
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            // 刻意不挂 GraphicRaycaster、也不建 EventSystem：
            // 阶段七的 HUD 没有任何点击交互（唯一的按钮在已验收的 MatchResultView 的 OnGUI 里），
            // 挂上射线检测组件只会白白增加每帧开销。
            return root;
        }

        #endregion

        #region 17.3 屏幕空间 HUD 装配

        /// <summary>计分板：顶部左侧，两行文本（我方 / 敌方）。</summary>
        private static ScoreboardView BuildScoreboard(Transform parent, Font font)
        {
            GameObject topBar = CreateUIObject(TopBarName, parent);
            SetupRect(topBar, AnchorTopLeft, AnchorTopLeft, AnchorTopLeft,
                new Vector2(24f, -16f), new Vector2(420f, 80f));

            GameObject holder = CreateUIObject(ScoreboardName, topBar.transform);
            SetupRect(holder, AnchorStretchMin, AnchorStretchMax, AnchorTopLeft, Vector2.zero, Vector2.zero);

            ScoreboardView view = Undo.AddComponent<ScoreboardView>(holder);

            Text left = CreateText(
                holder.transform, "LeftScore", "蓝方  击杀 0 · 死亡 0", font, 22,
                TextAnchor.MiddleLeft, TextPrimaryColor,
                AnchorTopLeft, AnchorTopLeft, AnchorTopLeft, new Vector2(0f, 0f), new Vector2(420f, 34f));

            Text right = CreateText(
                holder.transform, "RightScore", "红方  击杀 0 · 死亡 0", font, 22,
                TextAnchor.MiddleLeft, TextDimColor,
                AnchorTopLeft, AnchorTopLeft, AnchorTopLeft, new Vector2(0f, -38f), new Vector2(420f, 34f));

            AssignObjectReference(view, "leftScoreText", left);
            AssignObjectReference(view, "rightScoreText", right);

            return view;
        }

        /// <summary>击杀播报：顶部居中，固定 5 个槽位（不池化，循环复用最省）。</summary>
        private static KillFeedView BuildKillFeed(Transform parent, Font font)
        {
            GameObject holder = CreateUIObject(KillFeedName, parent);

            // 高度 = 槽位数 × 行距，保证 5 条同时存在时不会互相压住。
            const float lineHeight = 34f;
            SetupRect(holder, AnchorTopCenter, AnchorTopCenter, AnchorTopCenter,
                new Vector2(0f, -96f), new Vector2(760f, lineHeight * KillFeedEntryCount));

            KillFeedView view = Undo.AddComponent<KillFeedView>(holder);

            UnityEngine.Object[] entries = new UnityEngine.Object[KillFeedEntryCount];

            for (int i = 0; i < KillFeedEntryCount; i++)
            {
                Text entry = CreateText(
                    holder.transform,
                    KillFeedEntryPrefix + i,
                    string.Empty,
                    font,
                    22,
                    TextAnchor.MiddleCenter,
                    TextPrimaryColor,
                    AnchorTopCenter,
                    AnchorTopCenter,
                    AnchorTopCenter,
                    new Vector2(0f, -lineHeight * i),
                    new Vector2(760f, lineHeight - 4f));

                entries[i] = entry;
            }

            AssignObjectArray(view, "entryTexts", entries);
            return view;
        }

        /// <summary>玩家 HUD：左下角，头像 + 血条 + 蓝条 + Q/W 技能槽。</summary>
        private static HeroHUDView BuildHeroHUD(Transform parent, Font font, HeroController hero)
        {
            GameObject bottomBar = CreateUIObject(BottomBarName, parent);
            SetupRect(bottomBar, AnchorBottomLeft, AnchorBottomLeft, AnchorBottomLeft,
                new Vector2(24f, 24f), new Vector2(560f, 150f));

            HeroHUDView view = Undo.AddComponent<HeroHUDView>(bottomBar);

            // ---------- 头像 ----------
            Image portrait = CreateImage(
                bottomBar.transform, PortraitName, null, PortraitColor,
                AnchorBottomLeft, AnchorBottomLeft, AnchorBottomLeft,
                new Vector2(0f, 26f), new Vector2(96f, 96f));

            // ---------- 生命条 ----------
            CreateImage(
                bottomBar.transform, HealthBarBgName, null, BarBackgroundColor,
                AnchorBottomLeft, AnchorBottomLeft, AnchorBottomLeft,
                new Vector2(104f, 104f), new Vector2(400f, 24f));

            Image healthFill = CreateHorizontalFillImage(
                bottomBar.transform, HealthBarFillName, null, HealthBarAllyColor,
                new Vector2(104f, 104f), new Vector2(400f, 24f));

            Text healthText = CreateText(
                bottomBar.transform, HealthTextName, "0/0", font, 16,
                TextAnchor.MiddleRight, TextPrimaryColor,
                AnchorBottomLeft, AnchorBottomLeft, AnchorBottomLeft,
                new Vector2(104f, 104f), new Vector2(396f, 24f));

            // ---------- 法力条 ----------
            CreateImage(
                bottomBar.transform, ManaBarBgName, null, BarBackgroundColor,
                AnchorBottomLeft, AnchorBottomLeft, AnchorBottomLeft,
                new Vector2(104f, 76f), new Vector2(400f, 16f));

            Image manaFill = CreateHorizontalFillImage(
                bottomBar.transform, ManaBarFillName, null, ManaBarColor,
                new Vector2(104f, 76f), new Vector2(400f, 16f));

            Text manaText = CreateText(
                bottomBar.transform, ManaTextName, "0/0", font, 14,
                TextAnchor.MiddleRight, TextPrimaryColor,
                AnchorBottomLeft, AnchorBottomLeft, AnchorBottomLeft,
                new Vector2(104f, 76f), new Vector2(396f, 16f));

            // ---------- 技能槽（阶段八：2 槽 → 4 槽，与 Q / W / E / R 四键一一对应）----------
            // 按 (int)SkillSlot 的顺序排布，并把它们组成数组注入给 HUD ——
            // HUD 只按数组下标驱动，因此"槽位数量"这件事在视图侧只有一个来源。
            SkillSlotView[] skillSlots = new SkillSlotView[4];

            for (int i = 0; i < skillSlots.Length; i++)
            {
                SkillSlot slot = (SkillSlot)i;
                Vector2 position = new Vector2(
                    SkillSlotOrigin.x + i * (SkillSlotSize + SkillSlotSpacing), SkillSlotOrigin.y);

                skillSlots[i] = BuildSkillSlot(
                    bottomBar.transform, ResolveSkillSlotObjectName(slot), slot, font, position);
            }

            AssignObjectReference(view, "hero", hero);
            AssignObjectReference(view, "portraitImage", portrait);
            AssignObjectReference(view, "healthFill", healthFill);
            AssignObjectReference(view, "manaFill", manaFill);
            AssignObjectReference(view, "healthText", healthText);
            AssignObjectReference(view, "manaText", manaText);
            AssignObjectArray(view, "skillSlots", skillSlots);

            return view;
        }

        /// <summary>
        /// 槽位 → 技能槽子对象的固定名字。
        /// 用 switch 而不是枚举名拼接：对象名要满足本工具"按固定名字清理 / 校验"的约定，
        /// 枚举一旦被重命名，界面对象名不该跟着漂移。
        /// </summary>
        /// <param name="slot">技能槽位。</param>
        /// <returns>该槽位的子对象名。</returns>
        private static string ResolveSkillSlotObjectName(SkillSlot slot)
        {
            switch (slot)
            {
                case SkillSlot.Q: return SkillSlotQName;
                case SkillSlot.W: return SkillSlotWName;
                case SkillSlot.E: return SkillSlotEName;
                case SkillSlot.R: return SkillSlotRName;
                default: return "SkillSlot_Unknown";
            }
        }

        /// <summary>单个技能槽：底框 + 图标 + CD 径向遮罩 + 秒数 + 蓝耗 + 按键提示。</summary>
        private static SkillSlotView BuildSkillSlot(
            Transform parent, string objectName, SkillSlot slot, Font font, Vector2 anchoredPosition)
        {
            GameObject holder = CreateUIObject(objectName, parent);
            SetupRect(holder, AnchorBottomLeft, AnchorBottomLeft, AnchorBottomLeft,
                anchoredPosition, new Vector2(SkillSlotSize, SkillSlotSize));

            SkillSlotView view = Undo.AddComponent<SkillSlotView>(holder);

            CreateImage(
                holder.transform, SlotBackgroundName, null, SkillSlotColor,
                AnchorStretchMin, AnchorStretchMax, AnchorCenter, Vector2.zero, Vector2.zero);

            Image icon = CreateImage(
                holder.transform, SlotIconName, null, SkillIconPlaceholderColor,
                AnchorStretchMin, AnchorStretchMax, AnchorCenter, Vector2.zero, new Vector2(-8f, -8f));

            Image overlay = CreateRadialFillImage(
                holder.transform, SlotCooldownOverlayName, null, CooldownOverlayColor);

            Text cooldownText = CreateText(
                holder.transform, SlotCooldownTextName, string.Empty, font, 24,
                TextAnchor.MiddleCenter, TextPrimaryColor,
                AnchorStretchMin, AnchorStretchMax, AnchorCenter, Vector2.zero, Vector2.zero);

            Text costText = CreateText(
                holder.transform, SlotCostTextName, string.Empty, font, 14,
                TextAnchor.LowerCenter, TextDimColor,
                new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f),
                new Vector2(0f, 2f), new Vector2(0f, 18f));

            Text keyText = CreateText(
                holder.transform, SlotKeyTextName, string.Empty, font, 14,
                TextAnchor.UpperLeft, TextDimColor,
                AnchorTopLeft, AnchorTopLeft, AnchorTopLeft,
                new Vector2(4f, -2f), new Vector2(24f, 20f));

            // 枚举字段必须写 intValue（SerializedProperty 对枚举只认底层整数）。
            AssignEnum(view, "slot", (int)slot);
            AssignObjectReference(view, "iconImage", icon);
            AssignObjectReference(view, "cooldownOverlay", overlay);
            AssignObjectReference(view, "cooldownText", cooldownText);
            AssignObjectReference(view, "costText", costText);
            AssignObjectReference(view, "keyText", keyText);

            return view;
        }

        /// <summary>复活倒计时遮罩：全屏面板（默认隐藏）+ 标题 + 倒计时。</summary>
        private static RespawnOverlayView BuildRespawnOverlay(Transform parent, Font font)
        {
            GameObject holder = CreateUIObject(RespawnOverlayName, parent);
            SetupRect(holder, AnchorStretchMin, AnchorStretchMax, AnchorCenter, Vector2.zero, Vector2.zero);

            // 视图挂在常驻激活的 holder 上，只开关子节点 Panel ——
            // 若视图挂在 Panel 上，Panel 一关 Update 就停了，"复活完成后关掉面板"永远等不到执行。
            RespawnOverlayView view = Undo.AddComponent<RespawnOverlayView>(holder);

            GameObject panel = CreateUIObject(RespawnPanelName, holder.transform);
            SetupRect(panel, AnchorStretchMin, AnchorStretchMax, AnchorCenter, Vector2.zero, Vector2.zero);

            Image panelImage = Undo.AddComponent<Image>(panel);
            panelImage.sprite = null;
            panelImage.color = HudPanelColor;
            panelImage.raycastTarget = false;

            Text title = CreateText(
                panel.transform, RespawnTitleName, "已阵亡", font, 64,
                TextAnchor.MiddleCenter, RespawnTitleColor,
                AnchorCenter, AnchorCenter, AnchorCenter,
                new Vector2(0f, 70f), new Vector2(700f, 90f));

            Text countdown = CreateText(
                panel.transform, RespawnCountdownName, string.Empty, font, 96,
                TextAnchor.MiddleCenter, RespawnCountdownColor,
                AnchorCenter, AnchorCenter, AnchorCenter,
                new Vector2(0f, -40f), new Vector2(400f, 140f));

            AssignObjectReference(view, "panelRoot", panel);
            AssignObjectReference(view, "titleText", title);
            AssignObjectReference(view, "countdownText", countdown);

            // 默认隐藏：遮罩只在阵亡期间出现，不能一进游戏就压暗整个画面。
            panel.SetActive(false);

            return view;
        }

        /// <summary>
        /// 小地图（D6 批准纳入本轮）：右下角一块按战场长宽比取形的区域 + 池化的单位标记。
        ///
        /// 世界 → 小地图的映射范围取自【地面覆盖范围】（与 NavMesh 烘焙用的是同一份包围盒），
        /// 因此小地图的边界与"实际可走的地面"严格一致 —— 不会出现"看着还在小地图边缘、人却已经跑出去了"。
        /// </summary>
        /// <param name="parent">屏幕空间 HUD 的根节点。</param>
        /// <param name="localTeam">"我方"阵营（标记配色用）。</param>
        /// <param name="notes">缺口修补记录。</param>
        /// <returns>小地图视图组件。</returns>
        private static MinimapView BuildMinimap(Transform parent, TeamType localTeam, List<string> notes)
        {
            // 世界映射范围 = 地面覆盖范围。与 NavMesh 烘焙共用同一个包围盒计算方法，
            // 保证"小地图上能看到的范围"与"实际能走的地面"是同一份数据。
            Bounds groundBounds = ComputeRequiredGroundBounds();

            // 【阶段八：小地图按战场长宽比取形】原实现固定 220×220 正方形。桥梁地形是 120×40（3:1），
            // 若仍画成正方形，世界 X 与 Z 会被分别压进两个方向，标记在图上被纵向"压扁"——
            // 位置虽然仍与真实坐标一一对应（不会错位），但玩家读不出"桥有多长"，形状也与战场完全不符。
            // 这里按地面长宽比换算高度（宽度固定），并夹一个下限避免极端比例下退化成一条线。
            Vector2 minimapSize = ResolveMinimapSize(groundBounds);

            GameObject panel = CreateUIObject(MinimapName, parent);
            SetupRect(panel, AnchorBottomRight, AnchorBottomRight, AnchorBottomRight,
                new Vector2(-24f, 24f), minimapSize);

            CreateImage(
                panel.transform, MinimapBackgroundName, null, MinimapBackgroundColor,
                AnchorStretchMin, AnchorStretchMax, AnchorCenter, Vector2.zero, Vector2.zero);

            // 映射区：向内缩 12 像素，让标记不压到边框上。标记的父节点就是它，轴心在中心。
            GameObject area = CreateUIObject(MinimapAreaName, panel.transform);
            SetupRect(area, AnchorStretchMin, AnchorStretchMax, AnchorCenter, Vector2.zero, new Vector2(-12f, -12f));

            MinimapView view = Undo.AddComponent<MinimapView>(panel);

            // 标记模板（非激活）：一个纯色 Image，尺寸与颜色在绑定单位时按类型覆写。
            GameObject template = CreateUIObject(MinimapMarkerTemplateName, area.transform);
            SetupRect(template, AnchorCenter, AnchorCenter, AnchorCenter, Vector2.zero, new Vector2(7f, 7f));

            Image icon = Undo.AddComponent<Image>(template);
            icon.sprite = uiWhiteSprite;
            icon.color = Color.white;
            icon.raycastTarget = false;

            template.SetActive(false);

            AssignObjectReference(view, "markerTemplate", icon);
            AssignObjectReference(view, "markerHost", area.transform);
            AssignObjectReference(view, "mapRect", area.GetComponent<RectTransform>());
            AssignEnum(view, "localTeam", (int)localTeam);
            AssignInt(view, "prewarmCount", MinimapMarkerPrewarmCount);
            AssignVector2(view, "worldCenter", new Vector2(groundBounds.center.x, groundBounds.center.z));
            AssignVector2(view, "worldSize", new Vector2(groundBounds.size.x, groundBounds.size.z));

            notes.Add(
                $"小地图已装配（映射范围 {groundBounds.size.x:F0}×{groundBounds.size.z:F0} 米，" +
                $"面板 {minimapSize.x:F0}×{minimapSize.y:F0} 像素（按战场长宽比取形），" +
                $"中心 ({groundBounds.center.x:F0}, {groundBounds.center.z:F0})，标记池 {MinimapMarkerPrewarmCount}）");

            return view;
        }

        /// <summary>
        /// 按战场长宽比换算小地图面板尺寸（宽度固定为 MinimapSize.x，高度按比例算）。
        ///
        /// 【为什么必须按比例】MinimapView 的映射是"世界 X 归一化 → 小地图横向、世界 Z 归一化 → 小地图纵向"，
        /// 两个方向各用各的尺寸。若面板是正方形而战场是 120×40 的长桥，
        /// 纵向就会被压缩到 1/3 —— 位置仍然一一对应（不会错位），但形状失真、玩家读不出地形。
        /// 让面板与战场同比例后，小地图才真正是战场的缩略图。
        ///
        /// 高度夹在 [MinimapMinHeight, MinimapSize.y]：下限保证可读性，上限保证 HUD 布局不被向上侵占。
        /// </summary>
        /// <param name="groundBounds">地面水平包围盒（与 NavMesh 烘焙同源）。</param>
        /// <returns>小地图面板尺寸（像素）。</returns>
        private static Vector2 ResolveMinimapSize(Bounds groundBounds)
        {
            float width = Mathf.Max(1f, MinimapSize.x);
            float sizeX = Mathf.Max(0.01f, groundBounds.size.x);
            float sizeZ = Mathf.Max(0.01f, groundBounds.size.z);

            float height = Mathf.Clamp(width * (sizeZ / sizeX), MinimapMinHeight, MinimapSize.y);
            return new Vector2(width, height);
        }

        /// <summary>阶段四的对局结算界面（OnGUI 实现，本步骤只负责把它装配进场景并注入引用）。</summary>
        private static MatchResultView BuildMatchResultView(GameObject host, MatchController matchController)
        {
            GameObject holder = CreateUIObject(MatchResultViewName, host.transform);
            SetupRect(holder, AnchorStretchMin, AnchorStretchMax, AnchorCenter, Vector2.zero, Vector2.zero);

            MatchResultView view = Undo.AddComponent<MatchResultView>(holder);
            AssignObjectReference(view, "matchController", matchController);

            return view;
        }

        /// <summary>把击杀统计挂到 Battlefield 上（与 MatchController 同物体，语义上同属"对局编排"）。</summary>
        private static MatchStatsTracker AttachMatchStatsTracker(MatchController matchController)
        {
            if (matchController == null)
            {
                Debug.LogError("[AutoSceneBuilder] 没有 MatchController，无法挂载击杀统计。");
                return null;
            }

            GameObject host = matchController.gameObject;
            MatchStatsTracker existing = host.GetComponent<MatchStatsTracker>();

            if (existing != null)
            {
                return existing;
            }

            return Undo.AddComponent<MatchStatsTracker>(host);
        }

        #endregion

        #region 17.5 世界空间 UI 装配

        /// <summary>头顶血条：管理器 + 非激活的模板（池按模板预生成实例）。</summary>
        private static WorldHealthBarManager BuildHealthBars(
            Transform parent, Sprite sprite, Camera camera, TeamType localTeam)
        {
            GameObject managerHolder = CreateUIObject(WorldHealthBarManagerName, parent);
            SetupRect(managerHolder, AnchorStretchMin, AnchorStretchMax, AnchorCenter, Vector2.zero, Vector2.zero);

            WorldHealthBarManager manager = Undo.AddComponent<WorldHealthBarManager>(managerHolder);

            // ---------- 模板：一个 World Space Canvas + 三条水平填充条 ----------
            GameObject template = CreateUIObject(HealthBarTemplateName, managerHolder.transform);
            SetupRect(template, AnchorCenter, AnchorCenter, AnchorCenter, Vector2.zero, HealthBarCanvasSize);

            Canvas canvas = Undo.AddComponent<Canvas>(template);
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = camera;
            canvas.sortingOrder = 1;

            WorldHealthBarView view = Undo.AddComponent<WorldHealthBarView>(template);

            CreateImage(
                template.transform, HealthBarBgChildName, sprite, BarBackgroundColor,
                AnchorStretchMin, AnchorStretchMax, AnchorCenter, Vector2.zero, Vector2.zero);

            Image fill = CreateImage(
                template.transform, HealthBarFillChildName, sprite, HealthBarAllyColor,
                AnchorStretchMin, AnchorStretchMax, AnchorCenter, Vector2.zero, Vector2.zero);

            fill.type = Image.Type.Filled;
            fill.fillMethod = Image.FillMethod.Horizontal;
            fill.fillOrigin = 0;
            fill.fillAmount = 1f;

            Image shield = CreateImage(
                template.transform, HealthBarShieldChildName, sprite, ShieldBarColor,
                AnchorStretchMin, AnchorStretchMax, AnchorCenter, Vector2.zero, Vector2.zero);

            shield.type = Image.Type.Filled;
            shield.fillMethod = Image.FillMethod.Horizontal;
            shield.fillOrigin = 0;
            shield.fillAmount = 0f;
            shield.enabled = false; // 没有护盾时不画

            AssignObjectReference(view, "barCanvas", canvas);
            AssignObjectReference(view, "fillImage", fill);
            AssignObjectReference(view, "shieldImage", shield);

            // 满血也显示血条（阶段八实机修复）。
            // 【为什么必须由工具显式写入而不是靠字段默认值】本字段此前是 true（满血隐藏），而场景里的
            // 血条模板是【已存在的资产】—— 它的序列化数据里存着 true，改 C# 默认值对它毫无影响。
            // 不显式写入的话，症状（英雄刚复活/满血时完全没有血条）在重新组装后照旧，
            // 且 Console 一片安静 —— 与 PatchMinionPrefabTuning 踩过的坑完全同源。
            AssignBool(view, "hideWhenFull", false);

            // 模板必须非激活：它只是池的样板，不该出现在画面里。
            template.SetActive(false);

            AssignObjectReference(manager, "barTemplate", view);
            AssignObjectReference(manager, "poolHost", managerHolder.transform);
            AssignObjectReference(manager, "targetCamera", camera);
            AssignEnum(manager, "localTeam", (int)localTeam);
            AssignInt(manager, "prewarmCount", HealthBarPrewarmCount);

            return manager;
        }

        /// <summary>伤害飘字：共享世界画布 + 管理器 + 非激活的模板。</summary>
        private static DamagePopupManager BuildDamagePopups(Transform parent, Font font, Camera camera)
        {
            // ---------- 共享世界画布 ----------
            GameObject canvasHolder = CreateUIObject(PopupCanvasName, parent);
            SetupRect(canvasHolder, AnchorCenter, AnchorCenter, AnchorCenter, Vector2.zero, PopupCanvasSize);
            canvasHolder.transform.localScale = new Vector3(PopupCanvasScale, PopupCanvasScale, PopupCanvasScale);

            Canvas canvas = Undo.AddComponent<Canvas>(canvasHolder);
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = camera;
            canvas.sortingOrder = 2;

            DamagePopupManager manager = Undo.AddComponent<DamagePopupManager>(canvasHolder);

            // ---------- 模板 ----------
            GameObject template = CreateUIObject(PopupTemplateName, canvasHolder.transform);
            SetupRect(template, AnchorCenter, AnchorCenter, AnchorCenter, Vector2.zero, new Vector2(300f, 100f));

            DamagePopupView view = Undo.AddComponent<DamagePopupView>(template);

            Text label = CreateText(
                template.transform, "Label", string.Empty, font, 48,
                TextAnchor.MiddleCenter, DamagePopupColor,
                AnchorStretchMin, AnchorStretchMax, AnchorCenter, Vector2.zero, Vector2.zero);

            AssignObjectReference(view, "label", label);

            // 模板非激活：池按它预生成实例。
            template.SetActive(false);

            AssignObjectReference(manager, "popupTemplate", view);
            AssignObjectReference(manager, "poolHost", canvasHolder.transform);
            AssignObjectReference(manager, "popupCanvasRoot", canvasHolder.transform);
            AssignObjectReference(manager, "targetCamera", camera);
            AssignInt(manager, "prewarmCount", PopupPrewarmCount);

            return manager;
        }

        #endregion

        #region 17.6 依赖注入

        /// <summary>注入屏幕空间 HUD 的跨对象引用（阵营、事件源、对局控制器）。</summary>
        private static void InjectScreenSpaceUI(
            ScoreboardView scoreboard,
            KillFeedView killFeed,
            HeroHUDView hud,
            RespawnOverlayView respawnOverlay,
            MatchResultView resultView,
            MatchStatsTracker statsTracker,
            MatchController matchController,
            TeamType localTeam)
        {
            // 计分板与播报的数据源都是 MatchStatsTracker（唯一计数器）。
            AssignObjectReference(scoreboard, "statsTracker", statsTracker);
            AssignEnum(scoreboard, "localTeam", (int)localTeam);

            AssignObjectReference(killFeed, "statsTracker", statsTracker);
            AssignEnum(killFeed, "localTeam", (int)localTeam);

            // 复活遮罩与结算界面都以 MatchController 为唯一状态源。
            AssignObjectReference(respawnOverlay, "matchController", matchController);
            AssignObjectReference(resultView, "matchController", matchController);

            // HUD 的英雄引用在 BuildHeroHUD 里已经注入；这里只做一次存在性确认。
            if (hud == null)
            {
                Debug.LogError("[AutoSceneBuilder] HeroHUDView 未创建，玩家 HUD 不会显示。");
            }
        }

        /// <summary>注入世界空间 UI 的跨对象引用。</summary>
        private static void InjectWorldSpaceUI(
            WorldHealthBarManager barManager,
            DamagePopupManager popupManager,
            Camera camera,
            TeamType localTeam)
        {
            // 血条管理器：模板、池宿主、相机、阵营都在 BuildHealthBars 里注入，这里只补阵营的二次确认。
            AssignEnum(barManager, "localTeam", (int)localTeam);

            // 飘字管理器：模板与池宿主在 BuildDamagePopups 里注入，这里补相机（若上一步拿不到主相机会是 null）。
            if (camera != null)
            {
                AssignObjectReference(popupManager, "targetCamera", camera);
            }
        }

        #endregion

        #region 17.7 收尾校验

        /// <summary>
        /// 写入 Vector2 型序列化字段（小地图的世界映射范围用）。
        /// 主文件的辅助方法只覆盖了 float / int / string / 枚举 / 对象引用，这里补上 Vector2。
        /// </summary>
        private static bool AssignVector2(UnityEngine.Object target, string fieldName, Vector2 value)
        {
            if (target == null)
            {
                return false;
            }

            SerializedObject serialized = new SerializedObject(target);
            SerializedProperty property = serialized.FindProperty(fieldName);

            if (property == null)
            {
                Debug.LogError(
                    $"[AutoSceneBuilder] 在 {target.GetType().Name} 上找不到序列化字段「{fieldName}」，" +
                    "注入失败（字段可能已被改名），请同步修改本工具的字段名常量。");
                return false;
            }

            if (property.propertyType != SerializedPropertyType.Vector2)
            {
                Debug.LogError(
                    $"[AutoSceneBuilder] {target.GetType().Name}.{fieldName} 不是 Vector2 字段，注入失败。");
                return false;
            }

            property.vector2Value = value;
            serialized.ApplyModifiedProperties();
            return true;
        }

        /// <summary>
        /// UI 装配的收尾校验：给出"这次装配到底成不成"的确定性结论，只提示、不改场景。
        ///
        /// 【为什么必须显式校验字体与占位图】uGUI 的 Text 在 font 为 null 时、Image 在 sprite 为 null 时
        /// 都【不报错、只是不显示】。不把这两项查出来，使用者只会看到"界面上什么都没有"而毫无线索。
        /// </summary>
        private static void ValidateUISetup(
            Font uiFont,
            Sprite whiteSprite,
            WorldHealthBarManager barManager,
            DamagePopupManager popupManager,
            HeroHUDView hud,
            ScoreboardView scoreboard,
            KillFeedView killFeed,
            MinimapView minimap,
            RespawnOverlayView respawnOverlay,
            MatchResultView resultView,
            MatchStatsTracker statsTracker,
            List<string> notes)
        {
            List<string> problems = new List<string>();

            // ---------- 全局资源 ----------
            if (uiFont == null)
            {
                problems.Add("内置字体读取失败：所有 Text 都会不可见（uGUI 不报错，只是不画）");
            }

            if (whiteSprite == null)
            {
                problems.Add("占位白图缺失：所有纯色 Image 与技能 CD 径向遮罩都会不可见");
            }

            // ---------- 关键引用 ----------
            CheckObjectReference(problems, barManager, "barTemplate", "血条管理器.模板");
            CheckObjectReference(problems, barManager, "poolHost", "血条管理器.池宿主");
            CheckObjectReference(problems, barManager, "targetCamera", "血条管理器.相机");

            CheckObjectReference(problems, popupManager, "popupTemplate", "飘字管理器.模板");
            CheckObjectReference(problems, popupManager, "poolHost", "飘字管理器.池宿主");
            CheckObjectReference(problems, popupManager, "popupCanvasRoot", "飘字管理器.共享画布");
            CheckObjectReference(problems, popupManager, "targetCamera", "飘字管理器.相机");

            CheckObjectReference(problems, hud, "hero", "玩家 HUD.英雄");
            CheckObjectReference(problems, hud, "healthFill", "玩家 HUD.血条");
            CheckObjectReference(problems, hud, "manaFill", "玩家 HUD.蓝条");

            // 【阶段八：Q / W 两个字段已升级为 skillSlots 四元素数组】
            // 装配侧（BuildHeroHUD）会按 (int)SkillSlot 循环建 4 个槽并整体写入数组，
            // 校验侧必须跟着改 —— 否则每次组装都会刷出两条"字段 qSlot 不存在"的假错误，
            // 而这类假错误比"没有校验"更糟：它会训练人忽略 Console，而本项目整套流程都依赖 Console 暴露真问题。
            CheckArrayField(problems, hud, "skillSlots", "玩家 HUD.技能槽数组");

            CheckObjectReference(problems, scoreboard, "statsTracker", "计分板.统计源");
            CheckObjectReference(problems, killFeed, "statsTracker", "播报.统计源");
            CheckArrayField(problems, killFeed, "entryTexts", "播报.槽位数组");

            CheckObjectReference(problems, minimap, "markerTemplate", "小地图.标记模板");
            CheckObjectReference(problems, minimap, "markerHost", "小地图.标记父节点");
            CheckObjectReference(problems, minimap, "mapRect", "小地图.映射区");

            CheckObjectReference(problems, respawnOverlay, "matchController", "复活遮罩.对局控制器");
            CheckObjectReference(problems, respawnOverlay, "panelRoot", "复活遮罩.面板");

            CheckObjectReference(problems, resultView, "matchController", "结算界面.对局控制器");

            if (statsTracker == null)
            {
                problems.Add("击杀统计未挂载：播报与计分板都不会有数据");
            }

            // ---------- 输出结论 ----------
            if (problems.Count == 0)
            {
                notes.Add("UI 装配校验通过（字体/占位图/全部视图引用均已就位）");
                return;
            }

            Debug.LogError(
                "[AutoSceneBuilder] UI 装配存在 " + problems.Count + " 处问题，" +
                "界面上会表现为「什么都没有」或局部缺失：\n  · " + string.Join("\n  · ", problems));

            notes.Add($"UI 装配校验发现 {problems.Count} 处问题（详见 Console）");
        }

        /// <summary>读一个对象引用字段并检查是否为空（只读，不做任何修改）。</summary>
        private static void CheckObjectReference(
            List<string> problems, UnityEngine.Object target, string fieldName, string label)
        {
            if (target == null)
            {
                problems.Add($"{label}：组件不存在");
                return;
            }

            SerializedObject serialized = new SerializedObject(target);
            SerializedProperty property = serialized.FindProperty(fieldName);

            if (property == null)
            {
                problems.Add($"{label}：字段 {fieldName} 不存在（可能已改名，需同步修改工具）");
                return;
            }

            if (property.propertyType != SerializedPropertyType.ObjectReference)
            {
                problems.Add($"{label}：字段 {fieldName} 不是对象引用类型");
                return;
            }

            if (property.objectReferenceValue == null)
            {
                problems.Add($"{label}：字段 {fieldName} 未注入");
            }
        }

        /// <summary>读一个数组字段并检查是否非空（只读）。</summary>
        /// <summary>
        /// 读一个对象引用数组字段并检查是否为空（只读，不做任何修改）。
        ///
        /// 【为什么要逐元素检查】"数组有长度"不等于"有内容"：技能槽数组里混一个 null，
        /// 界面上就少一个技能槽（uGUI 不报错，只是那一格什么都不显示），
        /// 而只查 arraySize 的校验会给出"通过"的结论 —— 这正是本方法要堵的盲区。
        /// </summary>
        private static void CheckArrayField(
            List<string> problems, UnityEngine.Object target, string fieldName, string label)
        {
            if (target == null)
            {
                problems.Add($"{label}：组件不存在");
                return;
            }

            SerializedObject serialized = new SerializedObject(target);
            SerializedProperty property = serialized.FindProperty(fieldName);

            if (property == null)
            {
                problems.Add($"{label}：字段 {fieldName} 不存在（可能已改名，需同步修改工具）");
                return;
            }

            if (!property.isArray || property.arraySize == 0)
            {
                problems.Add($"{label}：字段 {fieldName} 为空数组");
                return;
            }

            for (int i = 0; i < property.arraySize; i++)
            {
                if (property.GetArrayElementAtIndex(i).objectReferenceValue == null)
                {
                    problems.Add($"{label}：字段 {fieldName} 的第 {i} 个元素未注入（界面上会少一格）");
                }
            }
        }

        #endregion
    }
}
