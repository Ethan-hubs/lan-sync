#!/usr/bin/env python3
"""实验：版本归档与恢复的真实语义（对应 PRD §FR-3.3/§FR-3.6、手册 B5）。

问题：在双端在线的情况下恢复一个**较旧**版本，会被对端的较新版本覆盖吗？
做法：分三种策略各跑一遍，记录结果。这个结论直接决定产品里"恢复"按钮的交互与提示文案。

用法: python3 exp_restore.py
"""
from __future__ import annotations

import json
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from lab import FOLDER_ID, adapter, my_device_id, paths  # noqa: E402


def sync_wait(f: Path, expect: str, timeout: float = 60) -> bool:
    t0 = time.time()
    while time.time() - t0 < timeout:
        if f.exists() and f.read_text() == expect:
            return True
        time.sleep(0.5)
    return False


def archive_count(ad, name: str) -> int:
    try:
        return len(ad.list_versions(FOLDER_ID, name).get(name, []))
    except Exception:                                                 # noqa: BLE001
        return -1


def scenario(tag: str, *, pause_peer: bool) -> dict:
    A, B = adapter("A"), adapter("B")
    idb = my_device_id("B")
    name = f"exp-{tag}.txt"
    fa, fb = Path(paths("A")["sync"]) / name, Path(paths("B")["sync"]) / name

    out: dict = {"scenario": tag, "pause_peer": pause_peer, "steps": []}

    # 清场
    fa.unlink(missing_ok=True); fb.unlink(missing_ok=True)
    A.resume(idb)
    time.sleep(2)

    # ① v1 → 同步到 B
    fa.write_text("v1")
    out["steps"].append(("A 写 v1 并同步到 B", sync_wait(fb, "v1")))

    # ② A 改成 v2 → B 收到远端变更 → B 归档 v1
    fa.write_text("v2")
    n = 0
    for _ in range(40):
        n = archive_count(B, name)
        if n >= 1:
            break
        time.sleep(1)
    out["steps"].append(("B 归档出 v1（版本数）", n))
    sync_wait(fb, "v2")
    out["steps"].append(("B 当前内容", fb.read_text() if fb.exists() else None))

    # ③ 恢复 v1（可选：先暂停 A→B 的连接）
    if pause_peer:
        A.pause(idb); time.sleep(2)
    vers = B.list_versions(FOLDER_ID, name).get(name, [])
    if not vers:
        out["steps"].append(("B 无归档可恢复", "abort"))
        if pause_peer:
            A.resume(idb)
        return out
    vt = vers[0]["versionTime"]
    resp = B.restore_version(FOLDER_ID, name, vt)
    out["steps"].append(("restore 响应（含错误则非空）", resp or "(空=成功)"))
    time.sleep(2)
    out["steps"].append(("恢复后 B 内容", fb.read_text() if fb.exists() else None))

    # ④ 恢复连接，观察最终态与是否产生冲突副本
    if pause_peer:
        A.resume(idb)
    final = None
    for _ in range(15):
        time.sleep(2)
        final = (fa.read_text() if fa.exists() else None, fb.read_text() if fb.exists() else None)
        if final[0] == final[1] and final[0] is not None:
            break
    out["steps"].append(("最终 A/B 内容", final))
    conflicts = sorted(p.name for p in Path(paths("B")["sync"]).glob("*.sync-conflict-*"))
    out["steps"].append(("B 端冲突副本", conflicts or "无"))
    return out


def main() -> None:
    results = [scenario("online", pause_peer=False), scenario("paused", pause_peer=True)]
    print(json.dumps(results, ensure_ascii=False, indent=2))
    Path("/data/lan-sync/local-lab/exp-restore.json").write_text(
        json.dumps(results, ensure_ascii=False, indent=2), encoding="utf-8")

    print("\n=== 结论 ===")
    for r in results:
        final = dict((k, v) for k, v in r["steps"] if isinstance(k, str)).get("最终 A/B 内容")
        print(f"  策略 {r['scenario']}（暂停对端={r['pause_peer']}）→ 最终 A/B = {final}")


if __name__ == "__main__":
    main()
