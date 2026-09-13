#!/usr/bin/env python3
"""本地实验台：在同机起两个 Syncthing 实例，用 Adapter 走真实 REST 验证契约与 M0.5 的
"Linux 可验证子集"（双机同步 / 连接类型 / events / pause-resume / 版本恢复 / ignores / watcher 可调）。

用法：
  python3 lab.py up        # 建实例、起进程、互加设备、建共享文件夹
  python3 lab.py checks    # 跑全部检查，输出 REPORT.md 与 results.json
  python3 lab.py status    # 看两个实例健康与连接类型
  python3 lab.py down      # 停进程（保留数据，便于复跑）
  python3 lab.py clean     # 删数据（慎用）
"""
from __future__ import annotations

import argparse
import json
import os
import secrets
import shutil
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from syncthing_adapter import (  # noqa: E402
    AdapterError, FolderSpec, SyncthingAdapter, SyncthingProcess, SyncthingRest,
)

LAB = Path("/data/lan-sync/local-lab")
BIN = LAB / "bin" / "syncthing"
FOLDER_ID = "m05-lab-folder"
IGNORE_PATTERNS = ["~$*", ".~lock.*", "Thumbs.db", ".DS_Store"]

INSTANCES = {
    "A": {"gui": "127.0.0.1:8384", "sync_port": 22000},
    "B": {"gui": "127.0.0.1:8385", "sync_port": 22001},
}


def paths(name: str) -> dict:
    d = LAB / name
    return {"home": d / "home", "sync": d / "sync", "log": d / "syncthing.log",
            "key": d / "api-key.txt"}


def api_key(name: str) -> str:
    p = paths(name)["key"]
    if not p.exists():
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_text(secrets.token_hex(16))
    return p.read_text().strip()


def adapter(name: str, with_process: bool = False) -> SyncthingAdapter:
    p = paths(name)
    rest = SyncthingRest(f"http://{INSTANCES[name]['gui']}", api_key(name))
    proc = SyncthingProcess(BIN, p["home"], INSTANCES[name]["gui"], api_key(name), p["log"]) if with_process else None
    return SyncthingAdapter(rest, proc)


def my_device_id(name: str) -> str:
    return adapter(name).health()["my_id"]


def cmd_up(_: argparse.Namespace) -> None:
    for name, cfg in INSTANCES.items():
        p = paths(name)
        for k in ("home", "sync"):
            p[k].mkdir(parents=True, exist_ok=True)
        ad = adapter(name, with_process=True)
        try:
            ad.rest.get("/rest/system/ping", timeout=2)
            print(f"[{name}] 已在运行，跳过启动")
        except AdapterError:
            ad.process.start()
            print(f"[{name}] 启动中（GUI {cfg['gui']}, sync {cfg['sync_port']}）…")
            ad.wait_ready(60)
            print(f"[{name}] 就绪，Device ID = {my_device_id(name)}")

    ids = {n: my_device_id(n) for n in INSTANCES}

    # —— 私有化：关官方 discovery / relay（对应 PRD §7、R11）
    for name in INSTANCES:
        ad = adapter(name)
        ad.set_options(
            globalAnnounceEnabled=False,
            globalAnnounceServers=[],
            localAnnounceEnabled=False,          # 本机实验用显式地址，不依赖发现
            relaysEnabled=False,                  # 公共 relay 关闭（自建 relay 走 listen 地址）
            listenAddresses=[f"tcp://0.0.0.0:{INSTANCES[name]['sync_port']}",
                             f"quic://0.0.0.0:{INSTANCES[name]['sync_port']}"],
            natEnabled=False,
        )
        ad.apply_and_restart(60)
        print(f"[{name}] 已私有化：官方 discovery/relay 关闭")

    # —— 互加设备（显式地址，避免依赖发现）
    for name in INSTANCES:
        peer = "B" if name == "A" else "A"
        ad = adapter(name)
        if ids[peer] not in [d["deviceID"] for d in ad.get_config()["devices"]]:
            ad.add_device(ids[peer], f"lab-{peer}",
                          addresses=[f"tcp://127.0.0.1:{INSTANCES[peer]['sync_port']}"])
            print(f"[{name}] 已加入设备 {peer}")
    # 清掉待批准队列（本实验直接互信）
    for name in INSTANCES:
        ad = adapter(name)
        for dev in (ad.device_pending() or {}):
            ad.accept_pending(dev)
    time.sleep(1)

    # —— 建共享文件夹（staggered versioning，maxAge=30，对应 PRD §FR-3.3 / ADR A9）
    for name in INSTANCES:
        ad = adapter(name)
        existing = [f["id"] for f in ad.get_config()["folders"]]
        if FOLDER_ID in existing:
            print(f"[{name}] 文件夹已存在")
            continue
        ad.add_folder(FolderSpec(
            folder_id=FOLDER_ID, label="M0.5 lab", path=str(paths(name)["sync"]),
            device_ids=[ids[n] for n in INSTANCES], versioning_max_age_days=30,
        ))
        ad.set_ignores(FOLDER_ID, IGNORE_PATTERNS)
        print(f"[{name}] 文件夹已建（staggered/maxAge=30）+ .stignore 已设")
    print("\n完成。接着跑：python3 lab.py checks")


def cmd_status(_: argparse.Namespace) -> None:
    for name in INSTANCES:
        try:
            ad = adapter(name)
            h = ad.health()
            print(f"[{name}] version={h['version']} my_id={h['my_id']} "
                  f"uptime={h['uptime_s']}s 连接={ad.connection_summary()}")
        except Exception as e:                                        # noqa: BLE001
            print(f"[{name}] 不可用：{e}")


def cmd_down(_: argparse.Namespace) -> None:
    for name in INSTANCES:
        ad = adapter(name, with_process=True)
        ad.process.stop()
        print(f"[{name}] 已停")


def cmd_clean(_: argparse.Namespace) -> None:
    for name in INSTANCES:
        shutil.rmtree(paths(name)["home"].parent, ignore_errors=True)
        print(f"[{name}] 数据已删")


# ----------------------------------------------------------------- 检查项
def run_checks() -> dict:
    A, B = adapter("A"), adapter("B")
    ida, idb = my_device_id("A"), my_device_id("B")
    res: dict = {"env": {"syncthing": A.health()["version"], "adapter": "reference-impl"},
                 "checks": {}}

    def record(key: str, ok: bool, detail: dict | str) -> None:
        res["checks"][key] = {"ok": bool(ok), "detail": detail}
        print(f"  [{'PASS' if ok else 'FAIL'}] {key}: {detail if isinstance(detail, str) else json.dumps(detail, ensure_ascii=False)[:160]}")

    # C1 REST 可控性（职责 1/7）
    try:
        cfg = A.get_config()
        opts = A.get_options()
        record("C1 REST 可读配置", "devices" in cfg and "folders" in cfg,
               {"devices": len(cfg["devices"]), "folders": len(cfg["folders"]),
                "listen": opts.get("listenAddresses")})
    except Exception as e:                                            # noqa: BLE001
        record("C1 REST 可读配置", False, str(e))

    # C2 连接建立 + 连接类型可读（职责 4）
    try:
        ra = A.wait_for_connection(idb, timeout=60)
        rb = B.wait_for_connection(ida, timeout=60)
        record("C2 连接类型可读", ra["kind"] in ("direct", "relay") and rb["kind"] in ("direct", "relay"),
               {"A->B": {k: ra[k] for k in ("type", "kind", "address")},
                "B->A": {k: rb[k] for k in ("type", "kind", "address")}})
    except Exception as e:                                            # noqa: BLE001
        record("C2 连接类型可读", False, str(e))

    # C3 双机同步（B1）
    try:
        stamp = time.strftime("%Y%m%d-%H%M%S")
        f = Path(paths("A")["sync"]) / f"hello-{stamp}.txt"
        payload = f"M0.5 lab {stamp}"
        f.write_text(payload)
        deadline, target = time.time() + 60, Path(paths("B")["sync"]) / f.name
        while time.time() < deadline and not target.exists():
            time.sleep(0.5)
        ok = target.exists() and target.read_text() == payload
        record("C3 双机同步(同机直连)", ok, {"file": f.name, "landed": target.exists()})
    except Exception as e:                                            # noqa: BLE001
        record("C3 双机同步(同机直连)", False, str(e))

    # C4 events 订阅（职责 3）
    try:
        got: list[str] = []
        t0 = time.time()
        f2 = Path(paths("A")["sync"]) / "event-probe.txt"
        f2.write_text("probe")
        for ev in A.events(events=["LocalChangeDetected", "ItemStarted", "StateChanged"],
                           since=0, timeout=20):
            got.append(ev.get("type", ""))
            if "LocalChangeDetected" in got and time.time() - t0 > 3:
                break
        record("C4 events 可用", "LocalChangeDetected" in got, {"seen": sorted(set(got))[:6]})
    except Exception as e:                                            # noqa: BLE001
        record("C4 events 可用", False, str(e))

    # C5 pause / resume（职责 5）
    try:
        A.pause(); time.sleep(2)
        paused = not A.connection_type(idb)["connected"]
        A.resume(); time.sleep(3)
        resumed = A.wait_for_connection(idb, timeout=60)["connected"]
        record("C5 pause/resume", paused and resumed, {"paused_broke_conn": paused, "resumed": resumed})
    except Exception as e:                                            # noqa: BLE001
        record("C5 pause/resume", False, str(e))

    # C6 .stignore 经 REST 生效（职责 2 / FR-1.7）
    try:
        A.set_ignores(FOLDER_ID, IGNORE_PATTERNS + ["*.labignore"])
        back = A.get_ignores(FOLDER_ID)
        probe = Path(paths("A")["sync"]) / "should-not-sync.labignore"
        probe.write_text("ignored")
        time.sleep(8)
        reached = (Path(paths("B")["sync"]) / probe.name).exists()
        record("C6 ignores 经 REST 生效", "*.labignore" in back and not reached,
               {"patterns": back, "ignored_file_synced": reached})
        probe.unlink(missing_ok=True)
    except Exception as e:                                            # noqa: BLE001
        record("C6 ignores 经 REST 生效", False, str(e))

    # C7 版本归档 + 恢复（B5 / 职责 6 / FR-3.3 / FR-3.6）
    # ⚠️ 实测结论（见 exp_restore.py）：**恢复前必须先暂停对端连接**，
    #    否则对端的最新版本会在 1–2 秒内把恢复结果覆盖回去（restore 接口本身返回成功）。
    try:
        # 每次用全新文件名，避开上一次运行留下的同名文件与归档（否则会误判"没有归档"）
        name = f"restore-test-{time.strftime('%H%M%S')}.txt"
        fa, fb = Path(paths("A")["sync"]) / name, Path(paths("B")["sync"]) / name
        A.resume(idb); time.sleep(2)

        fa.write_text("v1")                      # ① 建立基线并同步
        for _ in range(60):
            if fb.exists() and fb.read_text() == "v1":
                break
            time.sleep(0.5)
        time.sleep(2)                            # 让两端状态向量稳定
        fa.write_text("v2")                      # ② A 改 → B 归档 v1
        versions: dict = {}
        for _ in range(60):
            versions = B.list_versions(FOLDER_ID, name)
            if versions.get(name):
                break
            time.sleep(1)
        if not versions.get(name):
            record("C7 版本归档+恢复", False, f"B 未产生归档：{versions}")
        else:
            A.pause(idb); time.sleep(2)           # ③ 关键：先暂停对端
            resp = B.restore_version(FOLDER_ID, name, versions[name][0]["versionTime"])
            ok = False
            for _ in range(40):
                if fb.exists() and fb.read_text() == "v1":
                    ok = True
                    break
                time.sleep(0.5)
            A.resume(idb)                         # ④ 恢复连接，v1 会传播到 A
            propagated = False
            for _ in range(30):
                if fa.exists() and fa.read_text() == "v1":
                    propagated = True
                    break
                time.sleep(1)
            record("C7 版本归档+恢复（含暂停对端流程）", ok and propagated,
                   {"versions_seen": len(versions[name]), "restored_to_v1": ok,
                    "propagated_back_to_A": propagated, "restore_response": resp or "(空=成功)",
                    "procedure": "pause(peer) → restore → resume"})
    except Exception as e:                                            # noqa: BLE001
        record("C7 版本归档+恢复（含暂停对端流程）", False, str(e))

    # C8 watcher 时延可调（E5 的前置：确认 fsWatcherDelayS 可通过 REST 调整并生效）
    try:
        before = A.get_folder(FOLDER_ID)["fsWatcherDelayS"]
        A.update_folder(FOLDER_ID, {"fsWatcherDelayS": 1})
        after = A.get_folder(FOLDER_ID)["fsWatcherDelayS"]
        record("C8 fsWatcherDelayS 可调", abs(after - 1) < 0.01,
               {"before": before, "after": after, "note": "E5 会据此测 P50/P95"})
        A.update_folder(FOLDER_ID, {"fsWatcherDelayS": 10})
    except Exception as e:                                            # noqa: BLE001
        record("C8 fsWatcherDelayS 可调", False, str(e))

    # C9 端到端时延快速采样（E5 的本地预演；30 次太慢，这里 10 次）
    try:
        lats = []
        for i in range(10):
            ts = int(time.time() * 1000)
            fn = f"lat-{ts}-{i}.txt"
            (Path(paths("A")["sync"]) / fn).write_text(str(ts))
            tgt = Path(paths("B")["sync"]) / fn
            t0 = time.time()
            while time.time() - t0 < 30 and not tgt.exists():
                time.sleep(0.05)
            if tgt.exists():
                lats.append(round(time.time() - t0, 2))
        lats.sort()
        record("C9 时延预演(10 次)", len(lats) >= 8,
               {"n": len(lats), "p50": lats[len(lats)//2] if lats else None, "max": lats[-1] if lats else None,
                "note": "同机 localhost，不代表真实局域网；E5 要在真机上测"})
    except Exception as e:                                            # noqa: BLE001
        record("C9 时延预演(10 次)", False, str(e))

    return res


def cmd_checks(_: argparse.Namespace) -> None:
    print("=== 本地实验台检查（Linux 可验证子集）===")
    res = run_checks()
    ok = sum(1 for c in res["checks"].values() if c["ok"])
    total = len(res["checks"])
    res["summary"] = {"pass": ok, "total": total}
    (LAB / "results.json").write_text(json.dumps(res, ensure_ascii=False, indent=2), encoding="utf-8")
    lines = [f"# 本地实验台结果（{time.strftime('%Y-%m-%d %H:%M:%S')}）", "",
             f"- Syncthing：{res['env']['syncthing']}", f"- 通过：**{ok}/{total}**", "",
             "| 检查 | 结果 | 详情 |", "|---|---|---|"]
    for k, v in res["checks"].items():
        detail = v["detail"] if isinstance(v["detail"], str) else json.dumps(v["detail"], ensure_ascii=False)
        lines.append(f"| {k} | {'✅' if v['ok'] else '❌'} | {detail[:220]} |")
    (LAB / "REPORT.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(f"\n通过 {ok}/{total}；明细：{LAB/'REPORT.md'} / {LAB/'results.json'}")


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("cmd", choices=["up", "checks", "status", "down", "clean"])
    args = ap.parse_args()
    {"up": cmd_up, "checks": cmd_checks, "status": cmd_status,
     "down": cmd_down, "clean": cmd_clean}[args.cmd](args)


if __name__ == "__main__":
    main()
