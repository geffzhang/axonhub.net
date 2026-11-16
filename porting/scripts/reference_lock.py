#!/usr/bin/env python3
"""参照物指纹：锁定上游版本，检测语义漂移。

移植做一半发现参照仓库已经往前走了 29 个提交，是这类工程最常见的事故。
本脚本给出一个可进 CI 的判据。

用法:
    python reference_lock.py lock  --repo ../upstream --dest porting/reference-lock.json --note "基线 v1.4.2"
    python reference_lock.py check --repo ../upstream --dest porting/reference-lock.json
    python reference_lock.py show  --dest porting/reference-lock.json
"""

import argparse
import datetime
import hashlib
import json
import os
import subprocess
import sys

SKIP_DIRS = {".git", "node_modules", "bin", "obj", "dist", "build", "target",
             ".next", ".venv", "venv", "__pycache__", ".idea", ".vs", ".gradle"}
TEXT_EXT = {".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".cs", ".go", ".rs", ".java",
            ".kt", ".py", ".rb", ".php", ".c", ".h", ".cc", ".cpp", ".hpp", ".swift",
            ".scala", ".m", ".mm", ".sh", ".ps1", ".sql", ".proto", ".json", ".yml",
            ".yaml", ".toml", ".md", ".css", ".scss", ".html"}


def git(repo, *args, check=True):
    p = subprocess.run(["git", "-C", repo] + list(args),
                       stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    if check and p.returncode != 0:
        raise RuntimeError("git {} failed: {}".format(" ".join(args),
                                                      p.stdout.decode("utf-8", "replace").strip()))
    return p.returncode, p.stdout.decode("utf-8", "replace").strip()


def is_git(repo):
    rc, _ = git(repo, "rev-parse", "--is-inside-work-tree", check=False)
    return rc == 0


def identity_git(repo):
    _, head = git(repo, "rev-parse", "HEAD")
    _, tree = git(repo, "rev-parse", "HEAD^{tree}")
    _, subject = git(repo, "log", "-1", "--pretty=%ci %s")
    _, count = git(repo, "rev-list", "--count", "HEAD")
    _, dirty = git(repo, "status", "--porcelain", check=False)
    _, files = git(repo, "ls-files")
    file_list = files.splitlines()
    return {
        "vcs": "git",
        "commit": head,
        "tree_hash": tree,
        "commit_count": int(count or 0) or 0,
        "head_subject": subject,
        "dirty_files": len(dirty.splitlines()),
        "file_count": len(file_list),
        "files": file_list,
    }


def identity_fs(repo):
    """非 git 仓库：对 (相对路径, 大小, 内容) 取指纹。"""
    files = []
    h = hashlib.sha256()
    loc = 0
    for root, dirs, names in os.walk(repo):
        dirs[:] = sorted(d for d in dirs if d not in SKIP_DIRS and not d.startswith("."))
        for name in sorted(names):
            if name.startswith("."):
                continue
            full = os.path.join(root, name)
            rel = os.path.relpath(full, repo).replace("\\", "/")
            try:
                size = os.path.getsize(full)
            except OSError:
                continue
            files.append(rel)
            h.update("{}:{}:".format(rel, size).encode("utf-8"))
            if os.path.splitext(name)[1].lower() in TEXT_EXT:
                try:
                    with open(full, "rb") as f:
                        chunk = f.read()
                    h.update(chunk)
                    loc += chunk.count(b"\n") + (0 if chunk.endswith(b"\n") else 1)
                except OSError:
                    pass
    return {
        "vcs": "filesystem",
        "commit": None,
        "tree_hash": h.hexdigest(),
        "commit_count": None,
        "head_subject": None,
        "dirty_files": 0,
        "file_count": len(files),
        "loc": loc,
        "files": files,
    }


def compute(repo):
    repo = os.path.abspath(repo)
    if not os.path.isdir(repo):
        raise SystemExit("不是有效目录: " + repo)
    ident = identity_git(repo) if is_git(repo) else identity_fs(repo)
    ident["repo"] = repo
    ident["locked_at"] = datetime.datetime.now().isoformat(timespec="seconds")
    return ident


def cmd_lock(args):
    ident = compute(args.repo)
    if args.note:
        ident["note"] = args.note
    dest = args.dest
    d = os.path.dirname(os.path.abspath(dest))
    if d:
        os.makedirs(d, exist_ok=True)

    old = None
    if os.path.exists(dest):
        try:
            with open(dest, "r", encoding="utf-8") as f:
                old = json.load(f)
        except Exception:
            old = None

    files = ident.pop("files", [])
    with open(dest, "w", encoding="utf-8", newline="\n") as f:
        json.dump(ident, f, ensure_ascii=False, indent=2)
        f.write("\n")

    print("已锁定参照物 -> " + dest)
    print("  repo        : " + ident["repo"])
    print("  vcs         : " + str(ident["vcs"]))
    if ident["commit"]:
        print("  commit      : " + ident["commit"])
        print("  head        : " + (ident["head_subject"] or ""))
        print("  commits     : " + str(ident["commit_count"]))
        print("  dirty files : " + str(ident["dirty_files"]))
    print("  tree hash   : " + ident["tree_hash"])
    print("  file count  : " + str(ident["file_count"]))
    if ident.get("loc"):
        print("  loc         : " + str(ident["loc"]))
    if old and old.get("commit") and old.get("commit") != ident["commit"]:
        print("")
        print("注意：本次锁定覆盖了旧基线 {} -> {}".format(old["commit"][:10], (ident["commit"] or "")[:10]))
        print("      请把这次切换的原因和影响范围写进 00-reference.md 的漂移记录。")
    if ident["dirty_files"]:
        print("")
        print("警告：参照物工作区有 {} 个未提交改动。基线应基于干净的工作区，否则指纹不可复现。"
              .format(ident["dirty_files"]))
    return 0


def cmd_check(args):
    dest = args.dest
    if not os.path.exists(dest):
        raise SystemExit("找不到锁定文件: {}（先跑 lock）".format(dest))
    with open(dest, "r", encoding="utf-8") as f:
        locked = json.load(f)

    cur = compute(args.repo)
    cur_files = cur.pop("files", [])

    problems = []
    ok = True

    if locked.get("tree_hash") != cur.get("tree_hash"):
        ok = False
        print("[DRIFT] 树指纹不同")
        print("  locked: " + str(locked.get("tree_hash")))
        print("  now   : " + str(cur.get("tree_hash")))
    if locked.get("commit") and cur.get("commit"):
        if locked["commit"] != cur["commit"]:
            ok = False
            print("[DRIFT] commit 不同")
            print("  locked: " + locked["commit"] + "  " + str(locked.get("head_subject") or ""))
            print("  now   : " + cur["commit"] + "  " + str(cur.get("head_subject") or ""))
            rc, cnt = git(args.repo, "rev-list", "--left-right", "--count",
                          "{}...HEAD".format(locked["commit"]), check=False)
            if rc == 0 and cnt:
                behind, ahead = (cnt.split() + ["?", "?"])[:2]
                print("  上游在锁定点之后新增 {} 个提交（本实现落后 {} 个）".format(ahead, ahead))
                rc2, names = git(args.repo, "diff", "--name-only",
                                 "{}..HEAD".format(locked["commit"]), check=False)
                if rc2 == 0 and names:
                    lst = names.splitlines()
                    print("  受影响文件 {} 个：".format(len(lst)))
                    for n in lst[:20]:
                        print("    - " + n)
                    if len(lst) > 20:
                        print("    ... 还有 {} 个".format(len(lst) - 20))
                problems.append("commit")
            else:
                print("  ! 锁定 commit 在当前仓库中已不存在（可能被 rebase / force-push），建议重新 lock")
                problems.append("missing-commit")
    if locked.get("file_count") != cur.get("file_count"):
        ok = False
        print("[DRIFT] 文件数不同: locked={} now={}".format(locked.get("file_count"), cur.get("file_count")))
        lf, cf = set(locked.get("files") or []), set(cur_files)
        if lf and cf:
            added, removed = sorted(cf - lf), sorted(lf - cf)
            for n in added[:10]:
                print("    + " + n)
            for n in removed[:10]:
                print("    - " + n)
    if cur.get("dirty_files"):
        print("[WARN] 参照物工作区有 {} 个未提交改动，检查结果不可信".format(cur["dirty_files"]))

    if ok:
        print("OK 参照物未漂移（{}）".format(locked.get("commit") or locked.get("tree_hash", "")[:16]))
        return 0

    print("")
    print("结论：参照物已漂移。按纪律处理：")
    print("  1) 明确本次跟进到哪个 commit")
    print("  2) 列出受影响的语义范围（上面列出的文件是起点）")
    print("  3) 逐项回审受影响的实现，统一混用之处")
    print("  4) 处理完再 lock，并在 00-reference.md 记录漂移")
    return 2


def cmd_show(args):
    with open(args.dest, "r", encoding="utf-8") as f:
        data = json.load(f)
    data.pop("files", None)
    print(json.dumps(data, ensure_ascii=False, indent=2))
    return 0


def main():
    ap = argparse.ArgumentParser(description="参照物指纹锁")
    sub = ap.add_subparsers(dest="cmd", required=True)

    p = sub.add_parser("lock", help="锁定当前参照物版本")
    p.add_argument("--repo", required=True)
    p.add_argument("--dest", default="porting/reference-lock.json")
    p.add_argument("--note", default=None)
    p.set_defaults(func=cmd_lock)

    p = sub.add_parser("check", help="检测漂移（可进 CI）")
    p.add_argument("--repo", required=True)
    p.add_argument("--dest", default="porting/reference-lock.json")
    p.set_defaults(func=cmd_check)

    p = sub.add_parser("show", help="查看锁定内容")
    p.add_argument("--dest", default="porting/reference-lock.json")
    p.set_defaults(func=cmd_show)

    args = ap.parse_args()
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
