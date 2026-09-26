using System.Collections.Generic;
using UnityEngine;

namespace MOBA.VFX
{
    /// <summary>
    /// 白盒占位特效的材质工厂（表现层）：按颜色缓存 Standard 材质，透明与不透明各一份。
    ///
    /// 【为什么必须缓存而不是每个特效 new 一个】占位特效在对局里会生成上百个（每次施法若干个），
    /// 每个特效各 new 一份 Material 会产生上百个材质实例 —— 破坏合批、持续占用内存，
    /// 而且它们不会被自动回收（Material 是 UnityEngine.Object，不受 GC 管理）。
    /// 按颜色缓存后，整局最多只创建「用到的颜色数」个材质（当前 ≤ 8 个），全程复用。
    ///
    /// 【为什么运行时创建而不是做成 .mat 资产】白盒占位特效是临时表现（阶段九接美术后整块删除），
    /// 为它新增一批必须由工具维护、必须跟着仓库走的资产不划算；而 Shader.Find("Standard") 在本项目
    /// 使用的内置渲染管线下恒定可用（AutoSceneBuilder.EnsureColorMaterial 也是同一个前提）。
    ///
    /// 【绝不写回任何配置】本类只产出材质对象，不读也不改任何战斗数据。
    /// </summary>
    public static class WhiteboxVfxMaterials
    {
        /// <summary>不透明材质缓存（键 = 颜色）。</summary>
        private static readonly Dictionary<Color, Material> opaqueCache = new Dictionary<Color, Material>();

        /// <summary>半透明材质缓存（键 = 颜色，含 alpha）。</summary>
        private static readonly Dictionary<Color, Material> transparentCache = new Dictionary<Color, Material>();

        /// <summary>是否已就「找不到 Standard 着色器」告警过一次。</summary>
        private static bool hasReportedMissingShader;

        /// <summary>取一份半透明材质（用于护盾球 / 范围圈 / 巨柱这类需要看穿的特效）。</summary>
        /// <param name="color">颜色，alpha 决定透明度。</param>
        /// <returns>共享材质；着色器缺失时返回 null（调用方需判空并放弃生成特效）。</returns>
        public static Material GetTransparent(Color color)
        {
            return Get(color, true);
        }

        /// <summary>取一份不透明材质（用于「临时改色」这类需要覆盖原材质的场景）。</summary>
        /// <param name="color">颜色。</param>
        /// <returns>共享材质；着色器缺失时返回 null。</returns>
        public static Material GetOpaque(Color color)
        {
            return Get(color, false);
        }

        private static Material Get(Color color, bool transparent)
        {
            Dictionary<Color, Material> cache = transparent ? transparentCache : opaqueCache;

            // cached != null 的判断是必要的：材质可能在编辑器里被手工销毁（缓存会留下空槽）。
            if (cache.TryGetValue(color, out Material cached) && cached != null)
            {
                return cached;
            }

            Shader shader = Shader.Find("Standard");

            if (shader == null)
            {
                if (!hasReportedMissingShader)
                {
                    hasReportedMissingShader = true;
                    Debug.LogWarning(
                        "[WhiteboxVfxMaterials] 未找到 Standard 着色器，白盒占位特效将无法生成（不影响任何对局逻辑）。" +
                        "若项目已切换到 URP/HDRP，请把本类里的着色器名改成对应的 Lit 着色器。");
                }

                return null;
            }

            Material material = new Material(shader);
            material.color = color;

            // 去掉默认高光：顶视角下哑光颜色更实、更容易与单位区分（与工具生成材质资产时的处理一致）。
            material.SetFloat("_Glossiness", 0.05f);
            material.SetFloat("_Metallic", 0f);

            if (transparent)
            {
                // 内置 Standard 着色器的"透明模式"需要一次性改 4 个状态，缺一项都会表现为"不透明"：
                // _Mode = 3（Transparent）、混合方式 SrcAlpha/OneMinusSrcAlpha、关深度写入。
                // 关键词同理：必须开 _ALPHABLEND_ON 并关掉 _ALPHATEST_ON / _ALPHAPREMULTIPLY_ON。
                material.SetFloat("_Mode", 3f);
                material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                material.SetInt("_ZWrite", 0);
                material.DisableKeyword("_ALPHATEST_ON");
                material.EnableKeyword("_ALPHABLEND_ON");
                material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            }

            cache[color] = material;
            return material;
        }
    }
}
