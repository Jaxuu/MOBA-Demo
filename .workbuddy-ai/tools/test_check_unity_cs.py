"""自测 check_unity_cs.py 新增的第 6 项检查（迭代器内的裸 return）。

用法：python .workbuddy-ai/tools/test_check_unity_cs.py
全部用例通过时退出码为 0，否则为 1。

为什么要有这个自测：静态检查脚本本身也是代码，一旦它的正则写错，
要么漏报（等于没有这条检查），要么误报（把正常代码判为错误、阻塞后续开发）。
下面 6 个用例里第 3 个是最关键的假阳性防护——同一个文件里既有迭代器、
又有普通 void 方法（其中含 `return;`），必须不报错。
"""
import importlib.util
import os
import sys

TOOL = os.path.join(os.path.dirname(os.path.abspath(__file__)), "check_unity_cs.py")
spec = importlib.util.spec_from_file_location("check_unity_cs", TOOL)
mod = importlib.util.module_from_spec(spec)
spec.loader.exec_module(mod)


def run(name, source, expect_hits):
    stripped = mod.strip_comments_and_strings(source)
    hits = mod.check_iterator_bare_return(stripped, source, "Fake.cs")
    ok = len(hits) == expect_hits
    print("[%s] %s  -> 命中 %d 条（期望 %d）" % ("PASS" if ok else "FAIL", name, len(hits), expect_hits))
    for h in hits:
        print("        " + h)
    return ok


def run_cjk(name, source, expect_hits):
    """自测第 7 项检查（中文出现在注释与字符串之外）。"""
    stripped = mod.strip_comments_and_strings(source)
    hits = mod.check_cjk_outside_literals(stripped, source, "Fake.cs")
    ok = len(hits) == expect_hits
    print("[%s] %s  -> 命中 %d 条（期望 %d）" % ("PASS" if ok else "FAIL", name, len(hits), expect_hits))
    for h in hits:
        print("        " + h)
    return ok


CASES = [
    # 1. 真错：迭代器里写了裸 return（就是本次 Unity 报的 CS1622）
    ("迭代器内的裸 return", """
using System.Collections;
using UnityEngine;

namespace MOBA.Gameplay
{
    public class Fake : MonoBehaviour
    {
        private IEnumerator Loop()
        {
            yield return new WaitForSeconds(1f);
            if (true)
            {
                return;
            }
        }
    }
}
""", 1),

    # 2. 正确写法：yield break 不该被误报
    ("迭代器内用 yield break", """
using System.Collections;
using UnityEngine;

namespace MOBA.Gameplay
{
    public class Fake : MonoBehaviour
    {
        private IEnumerator Loop()
        {
            yield return new WaitForSeconds(1f);
            if (true)
            {
                yield break;
            }
        }
    }
}
""", 0),

    # 3. 同一文件里的普通 void 方法含 return; —— 绝不能误报（这是最容易踩的假阳性）
    ("同文件 void 方法的 return;", """
using System.Collections;
using UnityEngine;

namespace MOBA.Gameplay
{
    public class Fake : MonoBehaviour
    {
        private IEnumerator Loop()
        {
            yield return new WaitForSeconds(1f);
            yield break;
        }

        private void Helper(int a)
        {
            if (a > 0)
            {
                return;
            }

            Debug.Log(a);
        }
    }
}
""", 0),

    # 4. 迭代器里的 return; 藏在嵌套块中也要抓到
    ("嵌套块内的裸 return", """
using System.Collections;
using UnityEngine;

namespace MOBA.Gameplay
{
    public class Fake : MonoBehaviour
    {
        private IEnumerator Loop()
        {
            while (true)
            {
                for (int i = 0; i < 3; i++)
                {
                    if (i == 1)
                    {
                        return;
                    }

                    yield return null;
                }
            }
        }
    }
}
""", 1),

    # 5. 注释里提到 return; 不该被误报（注释已被剥离）
    ("注释中的 return;", """
using System.Collections;
using UnityEngine;

namespace MOBA.Gameplay
{
    public class Fake : MonoBehaviour
    {
        // 注意：这里不能写 return; 必须用 yield break;
        private IEnumerator Loop()
        {
            yield return null;
        }
    }
}
""", 0),

    # 6. IEnumerable<T> 泛型返回类型同样要覆盖
    ("IEnumerable<T> 泛型迭代器", """
using System.Collections.Generic;

namespace MOBA.Core
{
    public class Fake
    {
        public IEnumerable<int> Numbers()
        {
            yield return 1;
            return;
        }
    }
}
""", 1),
]

# 第 7 项检查（中文出现在注释与字符串之外）的用例。
# 第 1 个是真实踩到的写法：在 Debug.LogWarning 的中文说明里又写了一对英文双引号。
CJK_CASES = [
    # 1. 真错：字符串被提前闭合，中间那段中文变成了"标识符"
    ("字符串内误用英文双引号", """
using UnityEngine;

namespace MOBA.Skills
{
    public class Fake : MonoBehaviour
    {
        private void Warn()
        {
            Debug.LogWarning("落点将退回"射线与地面平面的交点"计算。");
        }
    }
}
""", 1),

    # 2. 正确写法：中文全在字符串内，且用「」代替嵌套引号 —— 绝不能误报
    ("中文只在字符串内", """
using UnityEngine;

namespace MOBA.Skills
{
    public class Fake : MonoBehaviour
    {
        private void Warn()
        {
            Debug.LogWarning("落点将退回「射线与地面平面的交点」计算。");
        }
    }
}
""", 0),

    # 3. 中文只在注释里 —— 注释已被剥离，绝不能误报
    ("中文只在注释里", """
namespace MOBA.Skills
{
    /// <summary>这里全是中文说明，不参与编译。</summary>
    public class Fake
    {
        // 行内注释也全是中文。
        private int value = 1;
    }
}
""", 0),

    # 4. 插值字符串里的中文（$"..."）同样属于字符串内容，不能误报
    ("插值字符串里的中文", """
using UnityEngine;

namespace MOBA.Skills
{
    public class Fake : MonoBehaviour
    {
        private void Log(int count)
        {
            Debug.Log($"[AreaEffectZone] 本 tick 命中 {count} 个敌方单位");
        }
    }
}
""", 0),

    # 5. 转义双引号 \\" 是合法写法，其后的中文仍在字符串内，不能误报
    ("转义双引号后的中文", """
using UnityEngine;

namespace MOBA.Skills
{
    public class Fake : MonoBehaviour
    {
        private void Log(string layerName)
        {
            Debug.LogWarning($"未找到名为 \\"{layerName}\\" 的 Layer，已跳过。");
        }
    }
}
""", 0),

    # 6. 预处理器指令后的中文是自由文本 —— 首次上线本检查时，本项目 8 个文件的 #region
    #    中文名就是被这一条误报的，因此必须有这条假阳性防护。
    ("#region 的中文名", """
namespace MOBA.Skills
{
    public class Fake
    {
        #region 状态跳转阈值（供各状态共享）

        private int threshold = 1;

        #endregion
    }
}
""", 0),

    # 7. 插值字符串的"洞"里再嵌字符串字面量 —— 这是本项目最容易被误报的写法，
    #    Stage2AutoTester / AutoSceneBuilder 里大量使用。剥离器必须识别洞的边界，
    #    否则洞内第一个引号会被当成外层字符串的结束，此后整份文件全部错位。
    ("插值洞内嵌字符串", """
using UnityEngine;

namespace MOBA.Tests
{
    public class Fake : MonoBehaviour
    {
        private void Report(bool pass, int hits)
        {
            Debug.Log(
                $"测试结论：{(pass ? "全部通过" : "存在失败项")}\\n" +
                $"  · 命中次数：{hits}（期望 3）");
        }
    }
}
""", 0),
]

if __name__ == "__main__":
    results = [run(*c) for c in CASES]

    print("")
    cjk_results = [run_cjk(*c) for c in CJK_CASES]

    total = results + cjk_results
    print("\n合计：%d / %d 通过" % (sum(total), len(total)))
    sys.exit(0 if all(total) else 1)
