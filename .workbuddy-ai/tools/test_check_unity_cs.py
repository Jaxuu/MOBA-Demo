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

if __name__ == "__main__":
    results = [run(*c) for c in CASES]
    print("\n合计：%d / %d 通过" % (sum(results), len(results)))
    sys.exit(0 if all(results) else 1)
