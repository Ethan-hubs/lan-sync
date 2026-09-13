#!/usr/bin/env python3
'''采样 relay 的 /status，输出 CSV（手册 §6 E2 / §11）。

要点（v0.2 修正）：bytesProxied 是【双向】合计，而阿里云只对出网计费。
本脚本同时给出：
  - bytes_proxied        累计双向字节（原生字段）
  - throughput_bidir     双向吞吐 Mbps（差分/间隔）
  - throughput_uni_est   单向估算 Mbps（= 双向/2，仅近似，报告里要注明是估算）
  - outbound_est         出网字节估算（= 累计双向/2，用于 §13 成本口径的近似）

用法：
  python3 relay-status-watch.py --csv status.csv --interval 10 --minutes 30
'''
import argparse
import csv
import json
import time
import urllib.request
from datetime import datetime, timezone

URL = "http://127.0.0.1:22070/status"


def fetch(url: str) -> dict:
    with urllib.request.urlopen(url, timeout=5) as r:
        return json.load(r)


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--url", default=URL)
    ap.add_argument("--csv", default="status.csv")
    ap.add_argument("--interval", type=float, default=10)
    ap.add_argument("--minutes", type=float, default=0, help="0 = 一直采到 Ctrl-C")
    args = ap.parse_args()

    fields = ["ts", "uptime_s", "num_conns", "num_sessions", "num_proxies", "pending_keys",
              "bytes_proxied", "d_bytes_bidir", "throughput_bidir_mbps", "throughput_uni_est_mbps",
              "outbound_est_bytes", "kbps10s1m5m15m30m60m", "global_rate", "per_session_rate"]
    t_end = time.time() + args.minutes * 60 if args.minutes else None
    prev = None
    with open(args.csv, "w", newline="", encoding="utf-8") as fh:
        w = csv.DictWriter(fh, fieldnames=fields)
        w.writeheader()
        while t_end is None or time.time() < t_end:
            try:
                s = fetch(args.url)
            except Exception as e:                                    # noqa: BLE001
                print(f"[warn] 取不到 /status: {e}")
                time.sleep(args.interval)
                continue
            now = time.time()
            bp = int(s.get("bytesProxied", 0))
            d = 0 if prev is None else bp - prev
            dt = args.interval if prev is not None else 0
            mbps = (d * 8 / dt / 1e6) if dt else 0.0
            opts = s.get("options", {}) or {}
            row = {
                "ts": datetime.now(timezone.utc).astimezone().isoformat(timespec="seconds"),
                "uptime_s": s.get("uptimeSeconds"),
                "num_conns": s.get("numConnections"),
                "num_sessions": s.get("numActiveSessions"),
                "num_proxies": s.get("numProxies"),
                "pending_keys": s.get("numPendingSessionKeys"),
                "bytes_proxied": bp,
                "d_bytes_bidir": d,
                "throughput_bidir_mbps": round(mbps, 4),
                "throughput_uni_est_mbps": round(mbps / 2, 4),
                "outbound_est_bytes": bp // 2,
                "kbps10s1m5m15m30m60m": "|".join(str(x) for x in (s.get("kbps10s1m5m15m30m60m") or [])),
                "global_rate": opts.get("global-rate"),
                "per_session_rate": opts.get("per-session-rate"),
            }
            w.writerow(row)
            fh.flush()
            print(f"{row['ts']}  sessions={row['num_sessions']} conns={row['num_conns']} "
                  f"bidir={row['throughput_bidir_mbps']}Mbps uni_est={row['throughput_uni_est_mbps']}Mbps")
            prev = bp
            time.sleep(args.interval)


if __name__ == "__main__":
    main()
