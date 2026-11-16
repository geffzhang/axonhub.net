#!/usr/bin/env python3
"""差分语料：把"参照实现的行为"录成可复现的输入输出对，再让新实现逐条比对。

有些行为很难用断言描述（排序、编码、错误文案、浮点格式化），
最可靠的办法是让参照实现自己跑一遍、把输出存下来。

用法:
    # 用参照实现录制基线
    python diff_corpus.py record --cmd "node ../upstream/dist/run.js" --corpus corpus/

    # 用新实现逐条比对
    python diff_corpus.py verify --cmd "dotnet run --project src/Cli --" --corpus corpus/

    python diff_corpus.py list --corpus corpus/
    python diff_corpus.py new --name leading-bom --corpus corpus/
"""

import argparse
import difflib
import glob
import json
import os
import subprocess
import sys

INPUT_SUFFIX = ".input.json"
EXPECTED_SUFFIX = ".expected.json"


def norm_text(s, strip_trailing_ws=True, json_canonical=False):
    """归一化：BOM、行尾、可选尾随空白。这三项是跨平台假阳性的主要来源。"""
    if json_canonical:
        try:
            return json.dumps(json.loads(s), ensure_ascii=False, sort_keys=True, indent=2)
        except Exception:
            pass
    s = s.lstrip("\ufeff")
    s = s.replace("\r\n", "\n").replace("\r", "\n")
    if strip_trailing_ws:
        s = "\n".join(line.rstrip() for line in s.split("\n"))
    return s.rstrip("\n")


def discover(corpus):
    cases = []
    for path in sorted(glob.glob(os.path.join(corpus, "*" + INPUT_SUFFIX))):
        name = os.path.basename(path)[: -len(INPUT_SUFFIX)]
        expected = os.path.join(corpus, name + EXPECTED_SUFFIX)
        cases.append((name, path, expected))
    return cases


def stdin_payload(input_path):
    """从语料文件取出真正喂给被测程序的字节。

    约定：若 input.json 是含 `payload` 键的对象，只把 payload 序列化后送入 stdin；
    否则整个文件原样送入。这样既能给语料写备注，又不需要被测程序理解包装格式。
    """
    with open(input_path, "rb") as f:
        raw = f.read()
    try:
        obj = json.loads(raw.decode("utf-8-sig"))
    except Exception:
        return raw
    if isinstance(obj, dict) and "payload" in obj:
        return json.dumps(obj["payload"], ensure_ascii=False).encode("utf-8")
    return raw


def run_case(cmd, case_name, input_path, corpus, timeout):
    payload = stdin_payload(input_path)
    env = dict(os.environ)
    env["CORPUS_CASE"] = case_name
    env["CORPUS_DIR"] = os.path.abspath(corpus)
    try:
        p = subprocess.run(cmd, shell=True, input=payload,
                           stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                           env=env, timeout=timeout)
        return {
            "case": case_name,
            "exit_code": p.returncode,
            "stdout": p.stdout.decode("utf-8", "replace"),
            "stderr": p.stderr.decode("utf-8", "replace"),
        }
    except subprocess.TimeoutExpired:
        return {"case": case_name, "exit_code": None,
                "stdout": "", "stderr": "TIMEOUT after {}s".format(timeout)}
    except FileNotFoundError as e:
        return {"case": case_name, "exit_code": None,
                "stdout": "", "stderr": "COMMAND NOT FOUND: {}".format(e)}


def cmd_record(args):
    cases = discover(args.corpus)
    if not cases:
        print("没有找到语料：{}/*{}".format(args.corpus, INPUT_SUFFIX))
        return 1
    written = 0
    for name, input_path, expected in cases:
        if os.path.exists(expected) and not args.update:
            print("  skip (已录制): " + name)
            continue
        result = run_case(args.cmd, name, input_path, args.corpus, args.timeout)
        if result["exit_code"] is None:
            print("  ERROR {}: {}".format(name, result["stderr"]))
            continue
        with open(expected, "w", encoding="utf-8", newline="\n") as f:
            json.dump(result, f, ensure_ascii=False, indent=2)
            f.write("\n")
        print("  recorded: {} (exit={})".format(name, result["exit_code"]))
        written += 1
    print("")
    print("完成，写入 {} 条。".format(written))
    if written:
        print("提醒：语料应由参照实现生成。若刚才是用新实现录的，这条基线不具权威性。")
    return 0


def cmd_verify(args):
    cases = discover(args.corpus)
    if not cases:
        print("没有找到语料：{}/*{}".format(args.corpus, INPUT_SUFFIX))
        return 1
    missing = [n for n, _, e in cases if not os.path.exists(e)]
    if missing:
        print("缺少基线，先跑 record：" + ", ".join(missing))
        return 1

    failures = []
    for name, input_path, expected in cases:
        with open(expected, "r", encoding="utf-8") as f:
            base = json.load(f)
        cur = run_case(args.cmd, name, input_path, args.corpus, args.timeout)

        diffs = []
        if cur["exit_code"] != base.get("exit_code"):
            diffs.append("exit_code: expected {} got {}".format(base.get("exit_code"), cur["exit_code"]))

        a = norm_text(base.get("stdout", ""), args.strip_trailing_ws, args.json_canonical)
        b = norm_text(cur.get("stdout", ""), args.strip_trailing_ws, args.json_canonical)
        if a != b:
            d = list(difflib.unified_diff(a.split("\n"), b.split("\n"),
                                          fromfile="expected", tofile="actual", lineterm="", n=2))
            diffs.append("stdout 不同:\n" + "\n".join("      " + l for l in d[:args.max_diff]))

        if args.compare_stderr:
            a = norm_text(base.get("stderr", ""), args.strip_trailing_ws)
            b = norm_text(cur.get("stderr", ""), args.strip_trailing_ws)
            if a != b:
                d = list(difflib.unified_diff(a.split("\n"), b.split("\n"),
                                              fromfile="expected", tofile="actual", lineterm="", n=2))
                diffs.append("stderr 不同:\n" + "\n".join("      " + l for l in d[:args.max_diff]))

        if diffs:
            failures.append(name)
            print("FAIL " + name)
            for d in diffs:
                print("    " + d)
        else:
            print("PASS " + name)

    print("")
    print("结果：{} 条语料，{} 通过，{} 失败".format(len(cases), len(cases) - len(failures), len(failures)))
    if failures:
        print("")
        print("每一条失败都要归因：实现 bug / 语料过期 / 已登记的差异。")
        print("归因为差异的，去 diff_register.py new 立条目，并在 --test 里写上语料名。")
        return 2
    return 0


def cmd_list(args):
    cases = discover(args.corpus)
    if not cases:
        print("（无）")
        return 0
    for name, _, expected in cases:
        print("{:<40} {}".format(name, "有基线" if os.path.exists(expected) else "缺基线"))
    print("")
    print("共 {} 条".format(len(cases)))
    return 0


def cmd_new(args):
    path = os.path.join(args.corpus, args.name + INPUT_SUFFIX)
    if os.path.exists(path) and not args.force:
        print("已存在: " + path)
        return 1
    payload = {"note": "说明这条语料在守什么行为"}
    if args.input:
        try:
            payload["payload"] = json.loads(args.input)
        except Exception:
            payload["payload"] = args.input
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        json.dump(payload, f, ensure_ascii=False, indent=2)
        f.write("\n")
    print("已创建: " + path)
    print("约定：文件里的 `payload` 会被序列化后送入被测程序 stdin；没有 payload 则整个文件原样送入。")
    print("下一步：用参照实现 record 出基线，再 verify。")
    return 0


def main():
    ap = argparse.ArgumentParser(description="差分语料录制与比对")
    sub = ap.add_subparsers(dest="cmd", required=True)

    p = sub.add_parser("record", help="运行命令并把输出录成基线")
    p.add_argument("--cmd", required=True, help="运行命令，input.json 的内容从 stdin 传入")
    p.add_argument("--corpus", default="corpus")
    p.add_argument("--timeout", type=int, default=120)
    p.add_argument("--update", action="store_true", help="覆盖已有基线")
    p.set_defaults(func=cmd_record)

    p = sub.add_parser("verify", help="逐条比对（可进 CI）")
    p.add_argument("--cmd", required=True)
    p.add_argument("--corpus", default="corpus")
    p.add_argument("--timeout", type=int, default=120)
    p.add_argument("--compare-stderr", action="store_true", help="同时比对 stderr")
    p.add_argument("--no-strip-trailing-ws", dest="strip_trailing_ws", action="store_false")
    p.add_argument("--json-canonical", action="store_true", help="按 JSON 语义比较，忽略键顺序")
    p.add_argument("--max-diff", type=int, default=40, help="每条失败最多打印的行数")
    p.set_defaults(func=cmd_verify, strip_trailing_ws=True)

    p = sub.add_parser("list", help="列出语料与基线状态")
    p.add_argument("--corpus", default="corpus")
    p.set_defaults(func=cmd_list)

    p = sub.add_parser("new", help="新建一条语料")
    p.add_argument("--name", required=True)
    p.add_argument("--corpus", default="corpus")
    p.add_argument("--input", default=None, help="输入 JSON 字符串")
    p.add_argument("--force", action="store_true")
    p.set_defaults(func=cmd_new)

    args = ap.parse_args()
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
