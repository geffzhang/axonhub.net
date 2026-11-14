#!/usr/bin/env python3
"""差异登记账：给每一处"不一样"一个编号、一条原因、一份测试。

差异不可能为零。问题不是"有没有差异"，而是差异有没有被记录、被解释、被测试。

用法:
    python diff_register.py new --prefix C --title "BOM 的幽灵" \\
        --what "参照实现剥掉 UTF-8 BOM，本实现不剥" \\
        --cause "两边解码器规范不同，且 BOM 可能跨 chunk" \\
        --decision "首个非空解码结果剥掉 \\\\uFEFF，永不 flush" \\
        --test "corpus/leading-bom, corpus/bom-split-across-chunks" \\
        --status resolved

    python diff_register.py lint      # 五要素缺一即报错（可进 CI）
    python diff_register.py render    # 生成 02-differences.md
    python diff_register.py list --prefix C
"""

import argparse
import datetime
import json
import os
import re
import sys

ID_RE = re.compile(r"^([A-Z]{1,4})(\d{3,})$")
STATUSES = {
    "resolved": "已解决",
    "accepted": "已接受",
    "gap": "真实缺口",
}
REQUIRED = ["title", "what", "cause", "decision", "test"]


def registry_path(d):
    return os.path.join(d, "differences.jsonl")


def load(d):
    path = registry_path(d)
    if not os.path.exists(path):
        return []
    entries = []
    with open(path, "r", encoding="utf-8") as f:
        for lineno, line in enumerate(f, 1):
            line = line.strip()
            if not line:
                continue
            try:
                entries.append(json.loads(line))
            except json.JSONDecodeError as e:
                raise SystemExit("{} 第 {} 行不是合法 JSON: {}".format(path, lineno, e))
    return entries


def save(d, entries):
    path = registry_path(d)
    os.makedirs(d, exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        for e in entries:
            f.write(json.dumps(e, ensure_ascii=False) + "\n")


def next_id(entries, prefix):
    mx = 0
    for e in entries:
        m = ID_RE.match(e.get("id", ""))
        if m and m.group(1) == prefix:
            mx = max(mx, int(m.group(2)))
    return "{}{:03d}".format(prefix, mx + 1)


def cmd_new(args):
    prefix = args.prefix.upper()
    if not re.match(r"^[A-Z]{1,4}$", prefix):
        raise SystemExit("前缀只能是 1-4 个大写字母，例如 C / T / E")
    if args.status not in STATUSES:
        raise SystemExit("status 只能是：" + " / ".join(STATUSES))

    entries = load(args.dir)
    new_id = args.id or next_id(entries, prefix)

    for e in entries:
        if e.get("id") == new_id:
            raise SystemExit("编号已存在: " + new_id)

    entry = {
        "id": new_id,
        "title": args.title,
        "what": args.what or "",
        "cause": args.cause or "",
        "decision": args.decision or "",
        "test": args.test or "",
        "status": args.status,
        "created": datetime.date.today().isoformat(),
    }
    entries.append(entry)
    entries.sort(key=lambda e: (e.get("id", "")[:1], len(e.get("id", "")), e.get("id", "")))
    save(args.dir, entries)

    print("已登记 {} {}".format(new_id, entry["title"]))
    missing = [k for k in REQUIRED if not entry[k]]
    if missing:
        print("")
        print("注意：以下要素仍为空，lint 会失败 —— " + ", ".join(missing))
        print("空着的差异条目等于没有条目。补齐后跑 lint。")
    else:
        print("五要素齐全。")
    print("")
    print("下一步：python {} render --dir {}".format(os.path.basename(__file__), args.dir))
    return 0


def cmd_update(args):
    entries = load(args.dir)
    target = None
    for e in entries:
        if e.get("id", "").upper() == args.id.upper():
            target = e
            break
    if target is None:
        raise SystemExit("找不到编号: " + args.id)

    changed = []
    for k in ["title", "what", "cause", "decision", "test", "status"]:
        v = getattr(args, k, None)
        if v is not None:
            if k == "status" and v not in STATUSES:
                raise SystemExit("status 只能是：" + " / ".join(STATUSES))
            target[k] = v
            changed.append(k)

    if not changed:
        print("没有指定要改的字段。")
        return 1

    save(args.dir, entries)
    print("已更新 {}：{}".format(target["id"], ", ".join(changed)))
    missing = [k for k in REQUIRED if not str(target.get(k, "")).strip()]
    if missing:
        print("仍缺要素：" + ", ".join(missing))
    return 0


def cmd_lint(args):
    entries = load(args.dir)
    if not entries:
        print("差异登记簿为空。如果工程已经开始，这通常意味着差异没被记录。")
        return 0 if args.allow_empty else 1

    problems = []
    seen = {}
    for e in entries:
        eid = e.get("id", "")
        if not ID_RE.match(eid):
            problems.append("{}: 编号格式非法（应为 前缀+3位以上数字，如 C001）".format(eid or "<无编号>"))
        if eid in seen:
            problems.append("{}: 编号重复".format(eid))
        seen[eid] = True
        for k in REQUIRED:
            if not str(e.get(k, "")).strip():
                problems.append("{}: 缺少要素 `{}`".format(eid, k))
        if e.get("status") not in STATUSES:
            problems.append("{}: status 非法（{}），只能是 {}".format(
                eid, e.get("status"), "/".join(STATUSES)))
        if e.get("status") == "gap" and len(str(e.get("decision", ""))) < 10:
            problems.append("{}: 标为真实缺口时，decision 必须说明为什么暂时做不到".format(eid))

    print("共 {} 条差异".format(len(entries)))
    by_status = {}
    for e in entries:
        by_status[e.get("status")] = by_status.get(e.get("status"), 0) + 1
    for k, v in sorted(by_status.items()):
        print("  {}: {}".format(STATUSES.get(k, k), v))

    if problems:
        print("")
        for p in problems:
            print("  [NG] " + p)
        print("")
        print("lint 失败：{} 处".format(len(problems)))
        return 1
    print("")
    print("lint 通过。")
    return 0


def render_table(entries):
    if not entries:
        return "（暂无登记）\n"
    lines = []
    lines.append("| 编号 | 标题 | 现象 | 原因 | 决策 | 测试 | 状态 |")
    lines.append("|---|---|---|---|---|---|---|")
    for e in entries:
        def cell(k):
            v = str(e.get(k, "")).replace("\n", " ").replace("|", "\\|")
            return v if v else "—"
        lines.append("| `{}` | {} | {} | {} | {} | {} | {} |".format(
            e.get("id", ""), cell("title"), cell("what"), cell("cause"),
            cell("decision"), cell("test"), STATUSES.get(e.get("status"), e.get("status", ""))))
    lines.append("")
    return "\n".join(lines)


def cmd_render(args):
    entries = load(args.dir)
    entries.sort(key=lambda e: (e.get("id", "")[:1], len(e.get("id", "")), e.get("id", "")))
    table = render_table(entries)

    target = args.out
    if target is None:
        target = os.path.join(args.dir, "02-differences.md")

    if os.path.exists(target):
        with open(target, "r", encoding="utf-8") as f:
            old = f.read()
        start, end = "<!-- RENDER:START -->", "<!-- RENDER:END -->"
        if start in old and end in old:
            head, rest = old.split(start, 1)
            _, tail = rest.split(end, 1)
            content = head + start + "\n" + table + end + tail
        else:
            content = (old.rstrip("\n") + "\n\n" + start + "\n" + table + end + "\n")
    else:
        content = "# 差异登记账（第二本账）\n\n<!-- RENDER:START -->\n" + table + "<!-- RENDER:END -->\n"

    d = os.path.dirname(os.path.abspath(target))
    if d:
        os.makedirs(d, exist_ok=True)
    with open(target, "w", encoding="utf-8", newline="\n") as f:
        f.write(content)
    print("已生成 {}（{} 条差异）".format(target, len(entries)))
    return 0


def cmd_list(args):
    entries = load(args.dir)
    if args.prefix:
        entries = [e for e in entries if e.get("id", "").upper().startswith(args.prefix.upper())]
    if not entries:
        print("（无）")
        return 0
    for e in sorted(entries, key=lambda x: x.get("id", "")):
        print("{:<8} {:<12} {}".format(e.get("id", ""),
                                       STATUSES.get(e.get("status"), e.get("status", "")),
                                       e.get("title", "")))
    print("")
    print("共 {} 条".format(len(entries)))
    return 0


def main():
    ap = argparse.ArgumentParser(description="差异登记账")
    sub = ap.add_subparsers(dest="cmd", required=True)

    p = sub.add_parser("new", help="登记一条差异")
    p.add_argument("--dir", default="porting", help="台账目录（默认 porting）")
    p.add_argument("--prefix", required=True, help="C / T / E 或自定义前缀")
    p.add_argument("--title", required=True)
    p.add_argument("--what", default="", help="现象：具体到输入输出，可复现")
    p.add_argument("--cause", default="", help="原因：语言/运行时/标准库/平台根因")
    p.add_argument("--decision", default="", help="决策：已解决 / 已接受 / 真实缺口，附理由")
    p.add_argument("--test", default="", help="锁定该差异的测试或语料路径")
    p.add_argument("--status", default="resolved", choices=list(STATUSES))
    p.add_argument("--id", default=None, help="手工指定编号（默认自动递增）")
    p.set_defaults(func=cmd_new)

    p = sub.add_parser("update", help="补齐或修改一条差异")
    p.add_argument("--dir", default="porting", help="台账目录（默认 porting）")
    p.add_argument("--id", required=True)
    p.add_argument("--title", default=None)
    p.add_argument("--what", default=None)
    p.add_argument("--cause", default=None)
    p.add_argument("--decision", default=None)
    p.add_argument("--test", default=None)
    p.add_argument("--status", default=None, choices=list(STATUSES))
    p.set_defaults(func=cmd_update)

    p = sub.add_parser("lint", help="校验五要素（可进 CI）")
    p.add_argument("--dir", default="porting", help="台账目录（默认 porting）")
    p.add_argument("--allow-empty", action="store_true", help="允许登记簿为空")
    p.set_defaults(func=cmd_lint)

    p = sub.add_parser("render", help="生成 markdown 表")
    p.add_argument("--dir", default="porting", help="台账目录（默认 porting）")
    p.add_argument("--out", default=None)
    p.set_defaults(func=cmd_render)

    p = sub.add_parser("list", help="列出差异")
    p.add_argument("--dir", default="porting", help="台账目录（默认 porting）")
    p.add_argument("--prefix", default=None)
    p.set_defaults(func=cmd_list)

    args = ap.parse_args()
    if not hasattr(args, "dir"):
        args.dir = "porting"
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
