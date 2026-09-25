#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""启发式检查：跨命名空间引用类型但缺少 using（本项目最常见的 CS0246 成因）。

做法：
1. 扫描所有 .cs，收集「命名空间 -> 该命名空间内声明的类型名」；
2. 对每个文件，计算其「可见命名空间集合」= 本文件命名空间 + 其所有父级前缀 + 文件内 using；
3. 逐 token 匹配已知类型名，若该类型所属命名空间不在可见集合内，则报告可疑行。

已知局限（务必人工复核，本脚本不替代编译）：
- 不解析泛型实参、扩展方法、嵌套类型与别名 using；
- 同名类型存在于多个命名空间时会误报（例如 MOBA.AI 与其它同名类）；
- 局部变量名与类型同名时会误报。
"""
import os
import re
import sys

ROOT_DEFAULT = os.path.join("Assets", "Scripts")

TYPE_DECL = re.compile(
    r"^\s*(?:public|internal|private|protected|abstract|sealed|static|partial|\s)*"
    r"(?:class|struct|enum|interface)\s+([A-Za-z_]\w*)",
    re.MULTILINE,
)
NAMESPACE_DECL = re.compile(r"^\s*namespace\s+([\w.]+)", re.MULTILINE)
USING_DECL = re.compile(r"^\s*using\s+(?:static\s+)?([\w.]+)\s*;", re.MULTILINE)
IDENT = re.compile(r"\b([A-Z][A-Za-z0-9_]*)\b")

# 常见 BCL / Unity 类型，避免噪音
IGNORE = {
    "System", "UnityEngine", "UnityEditor", "Collections", "Generic", "String",
    "Math", "Convert", "Exception", "Action", "Func", "List", "Dictionary",
    "Vector2", "Vector3", "Quaternion", "Mathf", "Time", "Debug", "GameObject",
    "Transform", "Component", "MonoBehaviour", "ScriptableObject", "Object",
    "StringComparison", "Attribute", "Serializable", "RuntimeInitializeOnLoadMethod",
    "SubsystemRegistration", "UnityEngineObject",
}


def _blank(match):
    """把匹配到的内容整体替换为等长空白（保留换行），使后续行号仍然准确。"""
    return "".join("\n" if ch == "\n" else " " for ch in match.group(0))


# 顺序很重要：先逐字串（@"..."，内部 "" 表示一个引号），再普通串（含 $ 插值串）。
VERBATIM_STRING = re.compile(r'\$?@"(?:[^"]|"")*"')
NORMAL_STRING = re.compile(r'\$?"(?:[^"\\]|\\.)*"')


def strip_comments(text):
    """剥离注释与字符串字面量。

    字符串必须一起剥离：本项目大量使用 [Tooltip("...")] 与日志文案，
    其中的类型名（如 CombatComponent）会被误判为「跨命名空间引用缺 using」。
    代价是插值串 {expr} 中的类型引用也会被忽略 —— 属可接受的启发式损失。
    """
    text = re.sub(r"/\*.*?\*/", _blank, text, flags=re.DOTALL)
    text = re.sub(r"//[^\n]*", _blank, text)
    text = VERBATIM_STRING.sub(_blank, text)
    text = NORMAL_STRING.sub(_blank, text)
    return text


def collect(root):
    files = []
    for base, _dirs, names in os.walk(root):
        for name in names:
            if name.endswith(".cs"):
                files.append(os.path.join(base, name))
    return files


def main():
    root = sys.argv[1] if len(sys.argv) > 1 else ROOT_DEFAULT
    files = collect(root)

    type_ns = {}  # 类型名 -> 命名空间集合
    file_info = {}
    for path in files:
        raw = open(path, encoding="utf-8-sig", errors="replace").read()
        code = strip_comments(raw)
        ns_match = NAMESPACE_DECL.search(code)
        ns = ns_match.group(1) if ns_match else ""
        usings = set(USING_DECL.findall(code))
        declared = set(TYPE_DECL.findall(code))
        file_info[path] = (ns, usings, declared, code)
        for t in declared:
            type_ns.setdefault(t, set()).add(ns)

    problems = 0
    for path, (ns, usings, declared, code) in sorted(file_info.items()):
        visible = set(usings)
        visible.add("")
        if ns:
            parts = ns.split(".")
            for i in range(1, len(parts) + 1):
                visible.add("." .join(parts[:i]))
        # 把 using / namespace 行抹白而不是删掉：保持行号与源文件一致，便于直接跳转。
        body = "\n".join(
            "" if re.match(r"^\s*(using|namespace)\b", line) else line
            for line in code.splitlines()
        )
        for lineno, line in enumerate(body.splitlines(), 1):
            for token in IDENT.findall(line):
                if token in IGNORE or token in declared:
                    continue
                owners = type_ns.get(token)
                if not owners:
                    continue
                if owners & visible:
                    continue
                print(f"{path}({lineno}): 可疑 {token}（声明于 {sorted(owners)}，本文件命名空间 {ns or '<global>'}）")
                problems += 1

    print(f"\n扫描 {len(files)} 个 .cs，可疑点 {problems} 处。本脚本为启发式，需人工确认。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
