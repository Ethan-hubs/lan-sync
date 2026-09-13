#!/usr/bin/env python3
'''E3：比对两端目录（数量 / 相对路径 / 大小 / SHA-256），输出差异清单。

用法：
  python3 verify_tree.py --left /path/A/sync --right /path/B/sync
  python3 verify_tree.py --left /path/A/sync --right /path/B/sync --skip-hash   # 大目录先比路径与大小
退出码：0 = 一致；1 = 有差异（差异明细打印出来，便于贴进报告）
'''
import argparse
import hashlib
import sys
from pathlib import Path

IGNORE_PREFIX = (".stversions", ".stfolder", ".stignore")


def walk(root: Path, do_hash: bool) -> dict[str, tuple[int, str]]:
    out: dict[str, tuple[int, str]] = {}
    for p in sorted(root.rglob("*")):
        rel = p.relative_to(root).as_posix()
        if rel.split("/")[0].startswith(IGNORE_PREFIX):
            continue
        if p.is_dir():
            continue
        try:
            size = p.stat().st_size
            digest = hashlib.sha256(p.read_bytes()).hexdigest()[:16] if do_hash else ""
        except OSError as e:                       # noqa: PERF203
            out[rel] = (-1, f"ERR:{e}")
            continue
        out[rel] = (size, digest)
    return out


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--left", required=True)
    ap.add_argument("--right", required=True)
    ap.add_argument("--skip-hash", action="store_true")
    args = ap.parse_args()

    l, r = Path(args.left), Path(args.right)
    A, B = walk(l, not args.skip_hash), walk(r, not args.skip_hash)
    only_a = sorted(set(A) - set(B))
    only_b = sorted(set(B) - set(A))
    diff = [k for k in sorted(set(A) & set(B)) if A[k] != B[k]]

    print(f"left  = {l}  文件数 {len(A)}")
    print(f"right = {r}  文件数 {len(B)}")
    print(f"只在 left: {len(only_a)}   只在 right: {len(only_b)}   大小/哈希不一致: {len(diff)}")
    for tag, items in (("ONLY-LEFT", only_a), ("ONLY-RIGHT", only_b), ("DIFF", diff)):
        for k in items[:50]:
            print(f"  [{tag}] {k}")
        if len(items) > 50:
            print(f"  ... 其余 {len(items) - 50} 条略")

    ok = not (only_a or only_b or diff)
    print("结果：" + ("一致 ✅" if ok else "存在差异 ❌（按手册 §0.6 STOP-1/2 判断是否停工）"))
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
