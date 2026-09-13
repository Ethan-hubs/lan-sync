#!/usr/bin/env python3
"""E5 跨网感知时延实测（Azure 实例 Z → WinServer-A 实例 W）。

流程：
  1) 在 WinServer-A 上用**计划任务**启动 watch-fs-events.ps1（脱离 SSH 会话，否则进程被杀）
  2) 在 Azure 侧按固定间隔写 N 个文件（文件名里带源端 UTC 毫秒）
  3) 等同步完成，把 W 的 CSV 取回本地
  4) 用「CSV 落地时间 − 文件名里的源时间」算每文件的感知时延，输出 P50/P95

用法：python3 wan_e5.py [文件数] [标记]
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
from wan_lab import FOLDER_ID, zp, z  # noqa: E402

WIN = Path("/data/lan-sync/local-lab/win")
TASK = "M05Watch"
WIN_LOG = r"D:\LanSync\m05\e5-events.csv"


def win(script: str | None = None, snippet: str | None = None, timeout: int = 300) -> str:
    args = ["bash", str(WIN / "win_run.sh")]
    if script:
        args += ["-f", script]
    else:
        args += ["-c", snippet or ""]
    # 注意：Windows 侧输出常混 GBK，text=True 会 UnicodeDecodeError → 按字节收再解码
    r = subprocess.run(args, capture_output=True, timeout=timeout)
    out = r.stdout.decode("utf-8", errors="replace")
    # 去掉 CLIXML 进度噪音
    out = re.sub(r"#< CLIXML.*?(?=\n[A-Za-z\u4e00-\u9fff]|$)", "", out, flags=re.S)
    out = "\n".join(l for l in out.splitlines() if not l.strip().startswith("<") and "CLIXML" not in l)
    return out.strip()


def push_watcher_script() -> None:
    """推送采集器；**必须先加 UTF-8 BOM**，否则 PS 5.1 按 ANSI/GBK 解析会崩"""
    import tempfile
    for f in ["w_e5_run.ps1"]:
        src = WIN / f
        with tempfile.NamedTemporaryFile("wb", suffix=".ps1", delete=False) as fh:
            fh.write(b"\xef\xbb\xbf" + src.read_bytes())
            tmp = fh.name
        subprocess.run(["scp", "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=no",
                        "-i", "/root/.ssh/id_ed25519", "-P", "26645", tmp,
                        f"Administrator@<YOUR_WIN_SERVER_IP>:D:/m05stage/{f}"],
                       capture_output=True, timeout=180)


def launch_watcher_detached(seconds: int, tries: int = 12):
    """用 Start-Process 起后台采集器（只需一条短 SSH 命令命中好窗口），
    再由同步通道回读结果 —— 彻底摆脱 SSH 稳定性对测量的影响。"""
    snippet = (
        "Start-Process powershell -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass',"
        "'-File','D:\\m05stage\\w_e5_run.ps1','-Seconds',"
        f"'{seconds}' -WindowStyle Hidden; \"collector_launched\""
    )
    for i in range(1, tries + 1):
        out = win(snippet=snippet)
        if "collector_launched" in out:
            print(f"    采集器已启动（第 {i} 次尝试）")
            return True
        print(f"    [{i}/{tries}] 启动失败：{out.strip()[-70:] or 'SSH 被重置'}")
        time.sleep(15)
    return False


def fetch_csv_via_sync(timeout_s: int = 120) -> str:
    """从 Z 的同步目录里读回 W 写的结果（走 Syncthing 通道，不依赖 SSH 稳定性）"""
    local = zp()["sync"] / "_m05meas" / "e5-events.csv"
    t0 = time.time()
    while time.time() - t0 < timeout_s:
        if local.exists() and local.stat().st_size > 20:
            txt = local.read_text(encoding="utf-8-sig", errors="replace")
            if len(txt.splitlines()) > 1:
                print(f"    结果已回流（{len(txt.splitlines())-1} 行）")
                return txt
        time.sleep(3)
    print("    [warn] 同步通道 120s 内未回流结果")
    return ""


def fetch_csv() -> str:
    r = subprocess.run(["scp", "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=no",
                        "-i", "/root/.ssh/id_ed25519", "-P", "26645",
                        "Administrator@<YOUR_WIN_SERVER_IP>:D:/LanSync/m05/e5-events.csv", "/tmp/wan-e5.csv"],
                       capture_output=True, timeout=180)
    if r.returncode != 0:
        print("[warn] scp CSV 失败:", r.stderr.decode("utf-8", "replace")[-200:])
    p = Path("/tmp/wan-e5.csv")
    return p.read_text(encoding="utf-8", errors="replace") if p.exists() else ""


def win(script: str | None = None, snippet: str | None = None, timeout: int = 300) -> str:
    args = ["bash", str(WIN / "win_run.sh")]
    args += ["-f", script] if script else ["-c", snippet or ""]
    r = subprocess.run(args, capture_output=True, timeout=timeout)
    out = r.stdout.decode("utf-8", errors="replace")
    out = re.sub(r"#< CLIXML.*?(?=\n[A-Za-z\u4e00-\u9fff]|$)", "", out, flags=re.S)
    return "\n".join(l for l in out.splitlines()
                      if not l.strip().startswith("<") and "CLIXML" not in l).strip()


def landed_count(prefix: str) -> str:
    return win(script=str(WIN / "w_count_e5.ps1")) if prefix == "e5wan" else ""


def main() -> None:
    n = int(sys.argv[1]) if len(sys.argv) > 1 else 20
    tag = sys.argv[2] if len(sys.argv) > 2 else time.strftime("%H%M%S")
    window = 240

    print("=== E5 跨网感知时延：Azure(Z) → WinServer-A(W) ===")
    print("[1/4] 推送采集器 + 以 Start-Process 后台启动（结果走同步通道回流，不依赖 SSH）")
    push_watcher_script()
    meas_dir = zp()["sync"] / "_m05meas"
    csv_local = meas_dir / "e5-events.csv"
    if csv_local.exists():
        csv_local.unlink()
    if not launch_watcher_detached(window):
        print("！无法启动采集器（SSH 持续被爆破打断）——稍后重试"); return
    # 等 CSV 出现，证明确实跑起来了
    t0 = time.time()
    while time.time() - t0 < 90 and not (csv_local.exists() and csv_local.stat().st_size > 20):
        time.sleep(3)
    if not csv_local.exists():
        print("！采集器未产出 CSV（未启动成功？）"); return
    print("    采集器已在运行（CSV 已通过同步通道可见）")

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

    print("[3/4] 等结果回流（90s）")
    time.sleep(90)

    print("[4/4] 计算")
    raw = fetch_csv_via_sync(120)
    rows = list(csv.DictReader(io.StringIO(raw.lstrip("\ufeff"))))
    if rows and "utc_ms" not in rows[0]:
        rows = [{k.lstrip("\ufeff"): v for k, v in r.items()} for r in rows]
    land: dict[str, int] = {}
    dispatch_lag: list[int] = []
    for r in rows:
        nm = (r.get("name") or "").strip()
        if nm.startswith(f"e5wan-{tag}-") and r.get("utc_ms"):
            t_ev = int(r["utc_ms"]); land[nm] = min(land.get(nm, t_ev), t_ev)
            if r.get("dispatch_ms"):
                dispatch_lag.append(int(r["dispatch_ms"]) - t_ev)
    lats = sorted(land[name] - src for name, src in created if name in land)
    dlag = sorted(dispatch_lag)
    out = {"tag": tag, "sent": len(created), "landed": len(lats),
           "p50_ms": lats[len(lats)//2] if lats else None,
           "p95_ms": lats[int(len(lats)*0.95)-1] if lats else None,
           "min_ms": lats[0] if lats else None, "max_ms": lats[-1] if lats else None,
           "all_ms": lats, "csv_rows": len(rows),
           "collector_dispatch_lag_p50_ms": dlag[len(dlag)//2] if dlag else None,
           "collector_dispatch_lag_max_ms": dlag[-1] if dlag else None,
           "note": "跨网 Azure→WinServer-A 直连(tcp)，fsWatcherDelayS=10（默认），采集=FileSystemWatcher($Event.TimeGenerated)"}
    Path(f"/data/lan-sync/local-lab/e5-wan-{tag}.json").write_text(json.dumps(out, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({k: v for k, v in out.items() if k != "all_ms"}, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
