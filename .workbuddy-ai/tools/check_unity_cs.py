#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Unity C# 脚本静态校验工具（本机无 .NET SDK，无法编译，故用本脚本兜底）。

用法：
    python .workbuddy-ai/tools/check_unity_cs.py [--fix-bom]

六项检查：
  1. UTF-8 BOM：项目硬性约定，缺 BOM 会导致 Unity 中文注释乱码；--fix-bom 可自动补写。
  2. 括号配平：{} 与 () 数量比对（跳过注释与字符串字面量）。
  3. 命名空间归属：校验每个 .cs 的 namespace 是否为 MOBA.<目录名>（子目录并入父目录）。
  4. 双引号奇偶：数量为奇数说明字符串字面量被中途截断（中文注释里写英文双引号时极易踩到，
     例如把 "目标" 写成 \"目标\" 就会提前闭合 [Tooltip(...)] 的字符串）。
  5. 危险命名空间：MOBA.<X> 的 X 若与 UnityEngine 类型同名（如 Debug、Time、Physics），
     MOBA 下所有文件里的 X.xxx 都会解析失败并让全项目编译失败（CS0234）。本项目已踩过一次。
  6. 迭代器内的裸 return：返回 IEnumerator/IEnumerable 的方法体内若出现不带值的 `return;`，
     会报 CS1622（迭代器块内只能用 yield break 结束迭代）。本项目已踩过一次——
     写协程时顺手写了 `return;`，Unity 编译直接失败，而前五项检查全部通过、毫无察觉。
  7. 中文出现在注释与字符串之外：C# 标识符全是 ASCII，因此剥离注释与字符串后若还剩下中文字符，
     说明某个字符串字面量被提前闭合（典型写法：在 "……" 里又写了一对英文双引号）。
     这一类错误【前六项全都查不出来】：引号总数仍是偶数、括号仍然配平、命名空间也没问题，
     但编译器会报 CS1002/CS1026 等成片语法错误。第 7 项就是为补这个盲区而加。

注意：本脚本只能发现"结构性"问题，无法替代编译。类型/成员是否存在仍需人工交叉核对。
"""

import io
import os
import re
import sys

SCRIPT_ROOT = os.path.dirname(os.path.abspath(__file__))
PROJECT_ROOT = os.path.abspath(os.path.join(SCRIPT_ROOT, "..", ".."))
SRC_ROOT = os.path.join(PROJECT_ROOT, "Assets", "Scripts")

# 命名空间与目录名不一致的例外（目录名 -> 实际使用的命名空间）
# 这些是项目既有的、刻意为之的决定，不是缺陷：
NAMESPACE_EXCEPTIONS = {
    # ScriptableObject 配置类统一归入 MOBA.Data（见项目记忆"代码约定"），
    # 而不叫 MOBA.ScriptableObjects —— 后者会被 Unity 自身的 UnityEngine.ScriptableObject 语境干扰阅读。
    "ScriptableObjects": "MOBA.Data",
    # Debug/ 目录必须用 MOBA.Debugging 而不是 MOBA.Debug。
    # 原因：命名空间 MOBA.Debug 会让 MOBA 下【所有】文件里的 Debug.Log 解析失败（CS0234），
    # 曾导致整个项目编译失败（波及 14 个文件、67 处调用）。详见 DANGEROUS_SEGMENTS。
    "Debug": "MOBA.Debugging",
    # 子目录并入父命名空间：Core/Interfaces -> MOBA.Core，AI/FSM 与 AI/States -> MOBA.AI。
    # 该规则已由 expected_namespace() 的"取一级目录"实现覆盖，无需在此重复登记。
}

# 禁止用作命名空间末段的标识符：它们是 UnityEngine 中会被"不加限定"书写的类型名。
# 一旦某个 MOBA.<X> 的 X 命中此表，MOBA 下所有文件里形如 X.Log / X.xxx 的写法都会先解析到
# 这个命名空间，从而报 CS0234 并使整个项目编译失败（C# 名字查找：外层命名空间成员优先于 using 导入）。
# 本项目已经因此踩过一次坑（MOBA.Debug），故设为硬性检查。
DANGEROUS_SEGMENTS = {
    "Debug", "Application", "Time", "Physics", "Mathf", "Color", "Object", "Component",
    "Transform", "Resources", "Random", "Screen", "Input", "Cursor", "Gizmos", "Handles",
    "Quality", "Audio", "PlayerPrefs", "SceneManager", "JsonUtility", "LayerMask",
    "Camera", "Material", "Animator", "GUI", "GUILayout", "Event", "Space", "ScriptableObject",
}

# 子目录并入父命名空间的规则：取离 Assets/Scripts 最近的一级目录名
def expected_namespace(rel_dir):
    if rel_dir in NAMESPACE_EXCEPTIONS:
        return NAMESPACE_EXCEPTIONS[rel_dir]
    parts = [p for p in rel_dir.replace("\\", "/").split("/") if p]
    if not parts:
        return None
    return "MOBA." + parts[0]


def strip_comments_and_strings(text):
    """把注释与字符串字面量替换为等长空白，保证括号计数不受其内容影响。

    【必须支持插值字符串的"洞"】C# 允许在 $"{...}" 的洞里再写字符串字面量，例如
        Debug.Log($"{(pass ? "全部通过" : "存在失败项")}");
    这在语法上完全合法（嵌套的双引号属于洞内的代码，不是外层字符串的结束）。
    若剥离器不识别洞，就会把洞内第一个 " 当成外层字符串的结束，此后整份文件的
    注释/字符串边界全部错位——本项目的第 7 项检查首次上线时，正是因为这一点
    把 Stage2AutoTester / AutoSceneBuilder 里合法的嵌套写法误报成了"字符串被截断"。

    实现方式：遇到 $" 进入 interp 状态；遇到未转义的 { 时把 interp 压栈、切回代码状态，
    并开始统计洞内的花括号深度；当深度回到 0 时弹栈、回到 interp 状态继续消费字符串内容。
    """
    out = []
    i, n = 0, len(text)
    state = None  # None / 'line' / 'block' / 'str' / 'char' / 'verbatim' / 'interp'
    string_stack = []  # 插值洞的返回栈：每进入一个洞压入 'interp'
    hole_brace_depth = []  # 每个洞内部的花括号深度（洞内可能再写 lambda / 初始化器）
    while i < n:
        c = text[i]
        nxt = text[i + 1] if i + 1 < n else ""
        if state is None:
            if c == "/" and nxt == "/":
                state = "line"; out.append("  "); i += 2; continue
            if c == "/" and nxt == "*":
                state = "block"; out.append("  "); i += 2; continue
            if c == "@" and nxt == '"':
                state = "verbatim"; out.append("  "); i += 2; continue
            if c == "$" and nxt == '"':
                state = "interp"; out.append("  "); i += 2; continue
            if c == '"':
                state = "str"; out.append(" "); i += 1; continue
            if c == "'":
                state = "char"; out.append(" "); i += 1; continue
            # 洞内的花括号配平：深度归零即表示洞结束，回到外层字符串。
            if c == "{" and hole_brace_depth:
                hole_brace_depth[-1] += 1
            elif c == "}" and hole_brace_depth:
                if hole_brace_depth[-1] > 0:
                    hole_brace_depth[-1] -= 1
                else:
                    hole_brace_depth.pop()
                    state = string_stack.pop()
                    out.append(" "); i += 1; continue
            out.append(c); i += 1; continue
        if state == "interp":
            if c == "\\":
                out.append("  "); i += 2; continue
            if c == '"':
                state = None; out.append(" "); i += 1; continue
            if c == "{":
                if nxt == "{":
                    out.append("  "); i += 2; continue  # {{ 是转义花括号，不构成洞
                string_stack.append("interp")
                hole_brace_depth.append(0)
                state = None
                out.append(" "); i += 1; continue
            if c == "}" and nxt == "}":
                out.append("  "); i += 2; continue  # }} 是转义花括号
            out.append("\n" if c == "\n" else " "); i += 1; continue
        if state == "line":
            if c == "\n":
                state = None; out.append("\n")
            else:
                out.append(" ")
            i += 1; continue
        if state == "block":
            if c == "*" and nxt == "/":
                state = None; out.append("  "); i += 2; continue
            out.append("\n" if c == "\n" else " "); i += 1; continue
        if state == "verbatim":
            if c == '"' and nxt == '"':
                out.append("  "); i += 2; continue
            if c == '"':
                state = None; out.append(" "); i += 1; continue
            out.append("\n" if c == "\n" else " "); i += 1; continue
        if state in ("str", "char"):
            if c == "\\":
                out.append("  "); i += 2; continue
            if (state == "str" and c == '"') or (state == "char" and c == "'"):
                state = None; out.append(" "); i += 1; continue
            out.append("\n" if c == "\n" else " "); i += 1; continue
    return "".join(out)


# 返回 IEnumerator / IEnumerable 的方法签名（含可选泛型参数）。
# 锚定"返回类型 + 方法名 + 参数列表 + 方法体左花括号"，精度足够高：
# 变量声明（`IEnumerator foo = Bar();`）因为中间隔着 `=` 不会被误匹配。
ITERATOR_METHOD_RE = re.compile(
    r"\b(?:IEnumerator|IEnumerable)\s*(?:<[^>;{}]*>)?\s+\w+\s*\([^;{}]*\)\s*\{")


def find_matching_brace(text, open_index):
    """返回与 text[open_index]（必须是 '{'）配对的 '}' 的下标；找不到返回 -1。"""
    depth = 0
    for i in range(open_index, len(text)):
        if text[i] == "{":
            depth += 1
        elif text[i] == "}":
            depth -= 1
            if depth == 0:
                return i
    return -1


def check_iterator_bare_return(stripped, text, rel):
    """
    检查"迭代器方法体内的裸 return"（CS1622）。

    规则：返回 IEnumerator / IEnumerable 的方法，其方法体要么 `yield return` 产出元素，
    要么 `return <某个迭代器对象>;`。因此方法体内出现不带值的 `return;` 一定是错的。

    为什么必须专门查这一条：它是最容易顺手写错、又最不容易被前五项检查发现的一类错误——
    写协程时按普通方法的习惯写了 `return;`，括号、引号、命名空间全部正常，
    只有真正的编译器会报 CS1622。本项目已因此浪费过一次编译周期。
    """
    problems = []

    for match in ITERATOR_METHOD_RE.finditer(stripped):
        open_index = match.end() - 1  # 回退到 '{'
        close_index = find_matching_brace(stripped, open_index)

        if close_index < 0:
            # 花括号不配平的问题由第 2 项检查负责，这里跳过避免重复报错。
            continue

        body = stripped[open_index:close_index]

        for return_match in re.finditer(r"\breturn\s*;", body):
            offset = open_index + return_match.start()
            line = text.count("\n", 0, offset) + 1
            problems.append(
                "%s:%d: 返回 IEnumerator/IEnumerable 的方法体内出现不带值的 `return;`，"
                "迭代器块内必须用 `yield break;` 结束（否则报 CS1622）" % (rel, line))

    return problems


# 中文字符（含中文标点与全角符号）。C# 的标识符全是 ASCII，因此剥离注释与字符串后
# 若还剩下这些字符，一定是某个字符串字面量被提前闭合了。
CJK_RE = re.compile(r"[\u3000-\u303f\u4e00-\u9fff\uff00-\uffef]")

# 单个文件最多报告几处"中文出现在字符串之外"。一处错误往往会连带出好几行，
# 全报出来会把真正的问题淹掉，前 3 处足以定位。
MAX_CJK_REPORTS_PER_FILE = 3


def check_cjk_outside_literals(stripped, text, rel):
    """
    检查"中文出现在注释与字符串之外"（第 7 项）。

    为什么必须单独查这一条：它是最典型的"检查全绿、编译全红"的一类错误。
    在 C# 字符串里再写一对英文双引号，例如把
        "非指向性技能的落点将退回"射线与地面平面的交点"计算"
    写进 Debug.LogWarning，结果是字符串被切成三段、中间那段变成一个中文标识符：
      · 引号总数仍是偶数（4 个）——第 4 项查不出来；
      · 括号仍然配平——第 2 项查不出来；
      · 命名空间、BOM、迭代器 return 也都正常。
    但编译器会报 CS1002 / CS1026 等成片语法错误，而错误行号指向的是"字符串那一行"，
    排查时很容易怀疑到完全无关的地方。

    判定手段：用 strip_comments_and_strings 把注释与字符串都替换成空白后，
    正文里不应该再出现任何中文字符。出现即为漏网的字符串内容。

    【必须排除预处理器指令行】`#region 状态跳转阈值` 这类写法是合法 C#——
    预处理指令后的文字是自由文本，既不是注释也不是字符串，中文出现在那里完全正常。
    若不排除，本项目里 8 个文件的 #region 中文名会全部被误报（首次上线本检查时就是这么踩的）。
    """
    problems = []
    reported_lines = set()

    # 预处理器指令所在的行号集合（1 基）。这些行整行跳过。
    preprocessor_lines = set()
    for index, raw_line in enumerate(text.split("\n")):
        if raw_line.lstrip().startswith("#"):
            preprocessor_lines.add(index + 1)

    for match in CJK_RE.finditer(stripped):
        offset = match.start()
        line = text.count("\n", 0, offset) + 1

        if line in preprocessor_lines:
            continue

        # 同一行只报一次：一处字符串被截断会让该行之后的所有中文都"越界"，
        # 逐个字符报出来会瞬间刷屏，反而看不到真正出问题的那一行。
        if line in reported_lines:
            continue

        reported_lines.add(line)
        problems.append(
            "%s:%d: 中文字符 '%s' 出现在注释与字符串之外，说明上方某个字符串字面量被提前闭合"
            "（检查该行附近是否在中文说明里误用了英文双引号，应改用「」）"
            % (rel, line, match.group()))

        if len(problems) >= MAX_CJK_REPORTS_PER_FILE:
            problems.append("%s: （本文件还有更多同类问题，已截断，请修复后重跑）" % rel)
            break

    return problems


def check_preprocessor_directives(stripped, rel):
    """检查预处理指令配对：#region / #endregion 与 #if / #endif。

    【为什么必须检查】它们不是语法元素，而是"分组标记"，但【配对是强制的】：
    少一个 #endregion 报 CS1038、少一个 #endif 报 CS1027，两者都会让【整个程序集】编译失败。
    而括号配平、引号奇偶这些检查都发现不了它 —— 实测踩坑：往一个已有 #region 的文件里插入新 region 时
    漏写了 #endregion，编辑器里只有一条 CS1038 且指向【文件最后一行】，排查时很容易去怀疑别的地方
    （本项目 2026-09-26 就因此浪费了一轮编译）。

    判据用"剥离注释与字符串后的文本"，因此注释里写的 #region 不会被误计。
    """
    problems = []
    region_stack = []
    if_stack = []

    for index, line in enumerate(stripped.splitlines(), 1):
        token = line.strip()
        if not token.startswith("#"):
            continue
        match = re.match(r"#\s*([A-Za-z_]+)", token)
        if match is None:
            continue
        directive = match.group(1)
        if directive == "region":
            region_stack.append(index)
        elif directive == "endregion":
            if region_stack:
                region_stack.pop()
            else:
                problems.append("%s: 第 %d 行的 #endregion 没有对应的 #region（CS1038）" % (rel, index))
        elif directive == "if":
            if_stack.append(index)
        elif directive == "endif":
            if if_stack:
                if_stack.pop()
            else:
                problems.append("%s: 第 %d 行的 #endif 没有对应的 #if（CS1027）" % (rel, index))

    for line_no in region_stack:
        problems.append(
            "%s: 第 %d 行的 #region 未闭合（缺 #endregion，编译器会在文件末尾报 CS1038）" % (rel, line_no))

    for line_no in if_stack:
        problems.append("%s: 第 %d 行的 #if 未闭合（缺 #endif，报 CS1027）" % (rel, line_no))

    return problems


def main():
    fix_bom = "--fix-bom" in sys.argv
    problems = []
    checked = 0

    for dirpath, _dirnames, filenames in os.walk(SRC_ROOT):
        for filename in sorted(filenames):
            if not filename.endswith(".cs"):
                continue
            checked += 1
            path = os.path.join(dirpath, filename)
            rel = os.path.relpath(path, SRC_ROOT).replace("\\", "/")

            raw = open(path, "rb").read()
            if not raw.startswith(b"\xef\xbb\xbf"):
                if fix_bom:
                    open(path, "wb").write(b"\xef\xbb\xbf" + raw)
                    raw = b"\xef\xbb\xbf" + raw
                    print("  [BOM] 已补写 %s" % rel)
                else:
                    problems.append("%s: 缺少 UTF-8 BOM（用 --fix-bom 自动补写）" % rel)

            text = raw.decode("utf-8-sig")
            stripped = strip_comments_and_strings(text)

            for open_ch, close_ch, label in (("{", "}", "花括号"), ("(", ")", "圆括号")):
                a, b = stripped.count(open_ch), stripped.count(close_ch)
                if a != b:
                    problems.append("%s: %s不配平 %d 开 / %d 闭" % (rel, label, a, b))

            # 双引号奇偶：本项目的字符串字面量都不含转义双引号，也不使用逐字字符串（@"..."），
            # 因此总数必为偶数。奇数说明某个字面量被中途截断，编译器会报出成片的语法错误。
            # 最常见的成因：在 [Tooltip("...")] 或插值字符串的中文说明里直接写了英文双引号。
            quote_count = text.count('"')
            if quote_count % 2 != 0:
                problems.append(
                    "%s: 双引号数量为奇数(%d)，字符串字面量可能被截断"
                    "（检查中文说明里是否误用了英文双引号，应改用「」）" % (rel, quote_count))

            declared = re.findall(r"^\s*namespace\s+([\w.]+)", text, re.MULTILINE)
            rel_dir = os.path.dirname(rel)
            expected = expected_namespace(rel_dir)
            if expected is not None:
                if not declared:
                    problems.append("%s: 未声明 namespace，按约定应为 %s" % (rel, expected))
                elif declared[0] != expected:
                    problems.append("%s: namespace 为 %s，按目录约定应为 %s" % (rel, declared[0], expected))

            # 危险命名空间末段检查：这是本项目踩过的真实事故（MOBA.Debug 曾让全项目编译失败）。
            for ns in declared:
                for segment in ns.split("."):
                    if segment in DANGEROUS_SEGMENTS:
                        problems.append(
                            "%s: 命名空间 %s 的段 '%s' 与 UnityEngine 类型同名，"
                            "会让所有 MOBA.* 文件里的 %s.xxx 解析失败（CS0234）——必须改名"
                            % (rel, ns, segment, segment))

            # 迭代器内的裸 return（CS1622）：同样是本项目踩过的真实事故。
            problems.extend(check_iterator_bare_return(stripped, text, rel))

            # 中文出现在注释与字符串之外：字符串被提前闭合的可靠信号（第 7 项）。
            problems.extend(check_cjk_outside_literals(stripped, text, rel))

            # 预处理指令配对（#region/#endregion、#if/#endif）：CS1038 / CS1027 的成因。
            problems.extend(check_preprocessor_directives(stripped, rel))

    print("已检查 %d 个 .cs 文件（根目录：%s）" % (checked, SRC_ROOT))
    if problems:
        print("\n发现 %d 个问题：" % len(problems))
        for p in problems:
            print("  - " + p)
        return 1

    print("结构检查全部通过（BOM / 括号配平 / 命名空间归属 / 双引号奇偶 / 危险命名空间 / 迭代器 return / 中文越界 / 预处理指令配对）。")
    print("提醒：本脚本无法替代编译，类型与成员是否存在仍需人工核对。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
