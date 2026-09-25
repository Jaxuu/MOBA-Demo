using UnityEngine;
using UnityEngine.UI;

namespace MOBA.UI
{
    /// <summary>
    /// UI 字体兜底（阶段七）：保证"即使一键组装工具拿不到内置字体，界面上的文字依然可见"。
    ///
    /// 【为什么必须有它】uGUI 的 Text 在 <c>font == null</c> 时【不报任何错、只是什么都不画】。
    /// 这是本项目最忌讳的那类静默失效：界面看起来"空了一块"，Console 却一片安静。
    /// 因此除了工具在编辑期注入内置字体之外，运行期再加一道兜底。
    ///
    /// 三级回退（与工具的编辑期回退一致）：
    ///   1. <c>LegacyRuntime.ttf</c> —— Unity/Tuanjie 2022 起的内置字体名（uGUI 源码自身也用它）；
    ///   2. <c>Arial.ttf</c> —— 更老版本的内置字体名；
    ///   3. <c>Font.CreateDynamicFontFromOSFont</c> —— 直接取系统字体（Windows 上优先微软雅黑，保证中文可显示）。
    ///
    /// 【为什么用静态缓存】第 3 级创建的 Font 是运行期对象，每次都创建会持续泄漏；
    /// 静态字段保证整个进程只创建一次（域重载后重建，符合预期）。
    /// </summary>
    public static class UIFontProvider
    {
        /// <summary>缓存的字体实例。</summary>
        private static Font cachedFont;

        /// <summary>是否已经尝试过获取字体（失败也只尝试一次，避免反复刷日志）。</summary>
        private static bool hasAttempted;

        /// <summary>
        /// 取一个可用的 UI 字体。可能返回 null（极端环境下三级全部失败），调用方需自行判空。
        /// </summary>
        /// <returns>字体实例；全部回退失败时为 null。</returns>
        public static Font Get()
        {
            if (hasAttempted)
            {
                return cachedFont;
            }

            hasAttempted = true;

            cachedFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            if (cachedFont == null)
            {
                cachedFont = Resources.GetBuiltinResource<Font>("Arial.ttf");
            }

            if (cachedFont == null)
            {
                // 系统字体兜底：字体名按优先级排列，Unity 会取第一个存在的。
                // 字号只是动态字体的采样基准，实际显示大小由 Text.fontSize 决定。
                cachedFont = Font.CreateDynamicFontFromOSFont(
                    new[] { "Microsoft YaHei", "SimHei", "Arial" }, 16);

                Debug.LogWarning(
                    "[UIFontProvider] 内置字体（LegacyRuntime.ttf / Arial.ttf）都读取失败，" +
                    "已回退到系统字体。若界面文字显示为方块，说明系统字体缺少中文字形。");
            }

            return cachedFont;
        }

        /// <summary>
        /// 给一个 Text 补上字体（仅在它没有字体时动手）。
        /// 判空之后再赋值，避免每次调用都触发一次文本网格重建。
        /// </summary>
        /// <param name="text">目标文本，允许为 null。</param>
        public static void EnsureFont(Text text)
        {
            if (text == null || text.font != null)
            {
                return;
            }

            Font font = Get();

            if (font != null)
            {
                text.font = font;
            }
        }
    }
}
