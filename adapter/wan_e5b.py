#!/usr/bin/env python3
"""E5（v2）：跨网感知时延 —— 会话内采集 + 结果经同步目录回流 + 重试。

为什么这么绕（都是实测踩出来的）：
  1) 裸 Start-Process 起的采集器会随 SSH 会话结束被杀；
  2) 计划任务 Register-ScheduledTask 启动失败（LastTaskResult=1）；
  3) 所以采集器必须跑在一个**长活会话**里：110s 的 Start-Sleep（但用短睡循环，见 w_e5_run.ps1）；
  4) 输出写到**正在同步的目录**，结果自动回流到 Azure，SSH 中途被爆破打断也不影响取数。

用法：python3 wan_e5b.py [文件数] [tag]
"""
from __future__ import annotations

import csv
import io
import json
import re
import statistics
import subprocess
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from wan_e5 import push_watcher_script, fetch_csv_via_sync, WIN  # noqa: E402
from wan_lab import zp  # noqa: E402

MEAS_LOCAL = lambda: zp()["sync"] / "_m05meas" / "e5-events.csv"   # noqa: E731


def start_session(seconds: int):
    cmd = ["ssh", "-o", "BatchMode=yes", "-o", "ConnectTimeout=15", "-o", "StrictHostKeyChecking=no",
           "-i", "/root/.ssh/id_ed25519", "-p", "26645", "Administrator@<YOUR_WIN_SERVER_IP>",
           f"powershell -NoProfile -ExecutionPolicy Bypass -File D:\\\\m05stage\\\\w_e5_run.ps1 -Seconds {seconds}"]
    return subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)


def main() -> None:
    n = int(sys.argv[1]) if len(sys.argv) > 1 else 20
    tag = sys.argv[2] if len(sys.argv) > 2 else time.strftime("%H%M%S")
    window = 40 + n * 2 + 60

    print("=== E5 跨网感知时延（会话内采集 + 同步回流）===")
    push_watcher_script()
    local_csv = MEAS_LOCAL()
    if local_csv.exists():
        local_csv.unlink()

    print(f"[1/4] 起采集会话（窗口 {window}s），最多重试 8 次")
    proc = None
    for attempt in range(1, 9):
        proc = start_session(window)
        t0 = time.time()
        while time.time() - t0 < 25:
            time.sleep(3)
            if local_csv.exists() and local_csv.stat().st_size > 20:
                print(f"    采集器已在运行（第 {attempt} 次尝试，{int(time.time()-t0)}s 后 CSV 可见）")
                break
        else:
            print(f"    [{attempt}/8] 采集器没起来，重试")
            try:
                proc.kill()
            except Exception:
                pass
            proc = None
            continue
        break
    if proc is None:
        print("！8 次都没起来（SSH 持续被爆破打断）"); return

    print(f"[2/4] 从 Azure 写 {n} 个文件（间隔 1.5s）")
    sync_dir = zp()["sync"]
    created: list[tuple[str, int]] = []
    for i in range(n):
        ts = int(time.time() * 1000)
        name = f"e5wan-{tag}-{ts}-{i:02d}.txt"
        (sync_dir / name).write_text(str(ts), encoding="utf-8")
        created.append((name, ts))
        time.sleep(1.5)
    print(f"    已写 {len(created)} 个")

    print("[3/4] 等采集窗口结束 + 结果回流")
    try:
        proc.wait(timeout=window + 120)
        print("    会话输出尾:", (proc.stdout.read().decode('utf-8', 'replace') or '').strip()[-200:])
    except subprocess.TimeoutExpired:
        proc.kill()
        print("    会话超时（结果仍可从同步通道取）")
    time.sleep(20)

    print("[4/4] 计算")
    raw = fetch_csv_via_sync(150)
    rows = list(csv.DictReader(io.StringIO(raw.lstrip("\ufeff"))))
    if rows and "utc_ms" not in rows[0]:
        rows = [{k.lstrip("\ufeff"): v for k, v in r.items()} for r in rows]
    land: dict[str, int] = {}
    lag: list[int] = []
    for r in rows:
        nm = (r.get("name") or "").strip()
        if nm.startswith(f"e5wan-{tag}-") and r.get("utc_ms"):
            t_ev = int(r["utc_ms"])
            land[nm] = min(land.get(nm, t_ev), t_ev)
            if r.get("dispatch_ms"):
                lag.append(int(r["dispatch_ms"]) - t_ev)
    lat = sorted(land[nm] - src for nm, src in created if nm in land)
    lag.sort()
    out = {"tag": tag, "sent": len(created), "landed": len(lat),
           "p50_ms": lat[len(lat)//2] if lat else None,
           "p95_ms": lat[int(len(lat)*0.95)-1] if lat else None,
           "min_ms": lat[0] if lat else None, "max_ms": lat[-1] if lat else None,
           "all_ms": lat, "csv_rows": len(rows),
           "collector_dispatch_lag_p50_ms": lag[len(lag)//2] if lag else None,
           "collector_dispatch_lag_max_ms": lag[-1] if lag else None,
           "note": "跨网 Azure→WinServer-A 直连(tcp)，fsWatcherDelayS=10，采集=FileSystemWatcher($Event.TimeGenerated)"}
    Path(f"/data/lan-sync/local-lab/e5-wan-{tag}.json").write_text(json.dumps(out, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({k: v for k, v in out.items() if k != "all_ms"}, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
