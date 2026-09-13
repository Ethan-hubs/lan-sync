#!/usr/bin/env python3
'''E3：造大量小文件 + 生成 oracle 清单（手册 §7）。

用法：
  python3 make_many_files.py --root /path/sync/many --count 100000
  # 生成后：import 时间点记入报告；如果软件有漏文件，用 oracle.tsv 比对
'''
import argparse
import hashlib
import os
from pathlib import Path


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--root", required=True)
    ap.add_argument("--count", type=int, default=100_000)
    ap.add_argument("--per-dir", type=int, default=1000)
    ap.add_argument("--bytes", type=int, default=0, help="每个文件写入多少字节随机内容（0 = 只写序号）")
    args = ap.parse_args()

    root = Path(args.root)
    root.mkdir(parents=True, exist_ok=True)
    oracle = root.parent / "oracle.tsv"
    rnd = os.urandom(args.bytes) if args.bytes else b""

    with oracle.open("w", encoding="utf-8") as fh:
        for i in range(args.count):
            d = root / f"{i // args.per_dir:03d}"
            d.mkdir(exist_ok=True)
            p = d / f"f-{i:06d}.txt"
            content = (rnd + str(i).encode()) if args.bytes else str(i).encode()
            p.write_bytes(content)
            fh.write(f"{p.relative_to(root)}\\t{len(content)}\\t{hashlib.sha256(content).hexdigest()}\\n")
            if (i + 1) % 10000 == 0:
                print(f"  {i + 1}/{args.count}")
    print(f"完成。oracle 清单：{oracle}")


if __name__ == "__main__":
    main()
