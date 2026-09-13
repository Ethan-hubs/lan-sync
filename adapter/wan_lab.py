#!/usr/bin/env python3
"""跨网实验（真实 WAN）：Azure 侧实例 "Z" ↔ WinServer-A 侧实例 "W"。

与本地实验台（lab.py / net_lab.py）完全隔离，用独立 home / 端口 / 文件夹 ID。

用法：
  python3 wan_lab.py up                 # 起 Z（私有化、建文件夹），打印 Z 的 Device ID
  python3 wan_lab.py peer <W_DEVICE>    # 给 Z 加上 W 的地址（默认 tcp://WinServer-A:22000）
  python3 wan_lab.py relay-url <uri>    # 切到"只走 relay"（给 Z 与 W 用同一台私有 relay）
  python3 wan_lab.py status             # 连接类型/kind、字节数
  python3 wan_lab.py latency [N]        # E5 风格的跨网感知时延（默认 20 次）
  python3 wan_lab.py e3 [case]          # 生成 Windows 专属语义用例（中文/emoji/长路径/大小写/大文件）
  python3 wan_lab.py where              # 打印 Z 的 home/sync 路径
  python3 wan_lab.py down               # 停 Z
"""
from __future__ import annotations

import argparse
import json
import secrets
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from syncthing_adapter import (  # noqa: E402
    AdapterError, FolderSpec, SyncthingAdapter, SyncthingProcess, SyncthingRest,
)

LAB = Path("/data/lan-sync/local-lab")
BIN = LAB / "bin" / "syncthing"
Z = {"gui": "127.0.0.1:8387", "sync_port": 22003}
ROOT = LAB / "Z"
FOLDER_ID = "m05-wan-lab"
WIN_IP = "<YOUR_WIN_SERVER_IP>"
WIN_SYNC_PORT = 22000


def zp() -> dict:
    return {"home": ROOT / "home", "sync": ROOT / "sync", "log": ROOT / "syncthing.log",
            "key": ROOT / "api-key.txt"}


def zkey() -> str:
    p = zp()["key"]
    if not p.exists():
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_text(secrets.token_hex(16))
    return p.read_text().strip()


def z(with_process: bool = True) -> SyncthingAdapter:
    p = zp()
    return SyncthingAdapter(SyncthingRest(f"http://{Z['gui']}", zkey()),
                            SyncthingProcess(BIN, p["home"], Z["gui"], zkey(), p["log"]) if with_process else None)


def zid() -> str:
    return z(False).health()["my_id"]


def cmd_up(_) -> None:
    for k in ("home", "sync"):
        zp()[k].mkdir(parents=True, exist_ok=True)
    ad = z()
    try:
        ad.rest.get("/rest/system/ping", timeout=2)
        print("[Z] 已在运行")
    except AdapterError:
        ad.process.start()
        ad.wait_ready(60)
        print("[Z] 已启动")
    print("Z_DEVICE_ID =", zid())

    ad.set_options(globalAnnounceEnabled=False, globalAnnounceServers=[],
                   localAnnounceEnabled=False, relaysEnabled=False, natEnabled=False,
                   listenAddresses=[f"tcp://0.0.0.0:{Z['sync_port']}",
                                    f"quic://0.0.0.0:{Z['sync_port']}"])
    ad.apply_and_restart(60)
    print("[Z] 已私有化（官方 discovery/relay 关闭，直连监听 %d）" % Z["sync_port"])

    existing = [f["id"] for f in ad.get_config()["folders"]]
    if FOLDER_ID not in existing:
        ad.add_folder(FolderSpec(folder_id=FOLDER_ID, label="M0.5 WAN lab",
                                 path=str(zp()["sync"]), device_ids=[zid()],
                                 versioning_max_age_days=30))
        ad.set_ignores(FOLDER_ID, ["~$*", ".~lock.*", "Thumbs.db", ".DS_Store"])
        print(f"[Z] 文件夹已建：{FOLDER_ID}（staggered maxAge=30）+ ignores")
    else:
        print("[Z] 文件夹已存在")
    print("\n下一步：在 WinServer-A 上跑 w_setup.ps1（带 Z_DEVICE_ID），然后 `peer <W_DEVICE_ID>`")


def cmd_peer(args) -> None:
    ad = z(False)
    dev_id = args.device
    addr = args.address or f"tcp://{WIN_IP}:{WIN_SYNC_PORT}"
    cfg = ad.get_config()
    found = next((d for d in cfg["devices"] if d["deviceID"] == dev_id), None)
    if found:
        found["addresses"] = [addr]
        ad.rest.put(f"/rest/config/devices/{dev_id}", found)
        print(f"[Z] 对端地址已更新为 {addr}")
    else:
        ad.add_device(dev_id, "win-w", addresses=[addr])
        print(f"[Z] 已加入对端 win-w -> {addr}")
    # 把 W 加进文件夹的设备列表
    f = ad.get_folder(FOLDER_ID)
    ids = [d["deviceID"] for d in f["devices"]]
    if dev_id not in ids:
        ad.update_folder(FOLDER_ID, {"devices": f["devices"] + [{"deviceID": dev_id}]})
        print("[Z] 已把 W 加入文件夹设备列表")
    ad.resume()
    for dev in (ad.device_pending() or {}):
        ad.accept_pending(dev)
        print(f"[Z] accepted pending {dev}")


def cmd_relay_url(args) -> None:
    ad = z(False)
    uri = args.uri
    if uri.lower() == "direct":
        ad.set_options(relaysEnabled=False, listenAddresses=[f"tcp://0.0.0.0:{Z['sync_port']}",
                                                             f"quic://0.0.0.0:{Z['sync_port']}"])
        print("[Z] 已切回直连监听")
    else:
        # relay-only：去掉直连监听 + relaysEnabled=true（实测：false 时 relay 地址不生效）
        ad.set_options(relaysEnabled=True, listenAddresses=[uri])
        cfg = ad.get_config()
        for d in cfg["devices"]:
            if d.get("name") == "win-w":
                d["addresses"] = [uri]
                ad.rest.put(f"/rest/config/devices/{d['deviceID']}", d)
        print("[Z] 已切为 relay-only")
    ad.rest.post("/rest/system/restart")
    time.sleep(3)
    ad.wait_ready(60)
    ad.resume()
    print("[Z] 已重启并恢复")


def cmd_status(_) -> None:
    ad = z(False)
    h = ad.health()
    print(f"[Z] version={h['version']} device={h['my_id']} uptime={h['uptime_s']}s")
    cfg = ad.get_config()
    for d in cfg["devices"]:
        if d["deviceID"] == h["my_id"]:
            continue
        c = ad.connection_type(d["deviceID"])
        print(f"    对端 {d.get('name')}  addresses={d['addresses']}  "
              f"connected={c['connected']} type={c['type'] or '(无)'} kind={c['kind']} "
              f"in={c['in_bytes']} out={c['out_bytes']}")


def cmd_latency(args) -> None:
    """跨网感知时延：源端写文件并记录时间，看对端何时收到（对端由 WinServer-A 侧脚本回报）。

    本脚本只能测"源端 → 被确认同步完成"的时间：用 /rest/db/completion 或事件更准；
    这里用最朴素也最可信的口径：写文件后轮询 folder 状态里的 lastFile 时间不可行，
    因此采用「写文件 → 等本端 scan 完成 → 记录远端确认」需要一个对端 watcher。
    折中：用 events 里的 ItemFinished/RemoteIndexUpdated 估算，并把原始事件流落盘备查。
    """
    n = args.n
    ad = z(False)
    ad.rest.post(f"/rest/db/scan?folder={FOLDER_ID}")
    samples = []
    print(f"[Z] 生成 {n} 个文件并测量（默认 fsWatcherDelayS=10）…")
    for i in range(n):
        ts = int(time.time() * 1000)
        name = f"wan-lat-{ts}-{i}.txt"
        (zp()["sync"] / name).write_text(str(ts))
        t0 = time.time()
        done = None
        for ev in ad.events(events=["ItemFinished", "RemoteIndexUpdated", "StateChanged"], since=0, timeout=25):
            if ev.get("type") == "ItemFinished" and (ev.get("data", {}).get("item") or "").endswith(name):
                done = time.time() - t0
                break
        samples.append({"file": name, "local_finish_s": round(done, 2) if done else None})
        print(f"  {name}  本端完成 {samples[-1]['local_finish_s']}s")
        time.sleep(1)
    out = LAB / "wan-latency.json"
    out.write_text(json.dumps(samples, ensure_ascii=False, indent=2), encoding="utf-8")
    got = [s["local_finish_s"] for s in samples if s["local_finish_s"] is not None]
    if got:
        got.sort()
        print(f"[Z] 本端完成时间 p50={got[len(got)//2]}s max={got[-1]}s（{len(got)}/{n} 有效）")
    print(f"[Z] 明细：{out}")
    print("注意：真正的「对端可见时延」需要在 WinServer-A 侧用 FileSystemWatcher 记录落地时间，")
    print("      请配合 m05-toolkit/client/watch-fs-events.ps1 使用（本脚本只给本端口径）。")


def cmd_e3(args) -> None:
    """生成 Windows 专属语义用例；对端（WinServer-A）用 verify 脚本核对。"""
    sync = zp()["sync"]
    case = (args.case or "all").lower()
    made = []

    def touch(name: str, content: str = "x") -> None:
        p = sync / name
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_text(content, encoding="utf-8")
        made.append(str(p.relative_to(sync)))

    if case in ("all", "names"):
        touch("中文测试-报表.xlsx", "cn")
        touch("项目😀-01.txt", "emoji")
        touch("名前-with space and 'quote'.txt", "mixed")
        touch("dash-ümlaut-ñ.txt", "latin")
    if case in ("all", "longpath"):
        # >260 字符完整路径：C:\LanSync\m05\sync\... + 深目录
        deep = "/".join(["d" * 30] * 8)
        touch(f"{deep}/deep-file.txt", "long")
    if case in ("all", "case"):
        touch("CaseTest.txt", "lower-name")
        touch("CaseTest.TXT", "upper-ext")     # Windows 上会与上一条同名（NTFS 不区分大小写）
    if case in ("all", "big"):
        big = sync / "big-32m.bin"
        big.write_bytes(secrets.token_bytes(32 * 1024 * 1024))
        made.append("big-32m.bin (32 MiB)")
    if case in ("all", "many"):
        many = sync / "many"
        for i in range(2000):
            d = many / f"{i // 200:03d}"
            d.mkdir(parents=True, exist_ok=True)
            (d / f"f-{i:05d}.txt").write_text(str(i))
        made.append(f"many/ (2000 文件)")

    print(f"[Z] 已生成 {len(made)} 项：")
    for m in made:
        print("   ", m)
    print("[Z] 触发重扫…")
    z(False).rest.post(f"/rest/db/scan?folder={FOLDER_ID}")
    print("[Z] 完成。请在 WinServer-A 上跑 w_verify.ps1 核对落地情况。")


def cmd_where(_) -> None:
    print("Z home:", zp()["home"])
    print("Z sync:", zp()["sync"])
    print("Z gui :", Z["gui"], " Z device:", zid())


def cmd_down(_) -> None:
    z().process.stop()
    print("[Z] 已停")


def main() -> None:
    ap = argparse.ArgumentParser()
    sub = ap.add_subparsers(dest="cmd", required=True)
    sub.add_parser("up").set_defaults(fn=cmd_up)
    p = sub.add_parser("peer")
    p.add_argument("device")
    p.add_argument("--address", default="")
    p.set_defaults(fn=cmd_peer)
    p = sub.add_parser("relay-url")
    p.add_argument("uri")
    p.set_defaults(fn=cmd_relay_url)
    sub.add_parser("status").set_defaults(fn=cmd_status)
    p = sub.add_parser("latency")
    p.add_argument("n", nargs="?", type=int, default=20)
    p.set_defaults(fn=cmd_latency)
    p = sub.add_parser("e3")
    p.add_argument("case", nargs="?", default="all")
    p.set_defaults(fn=cmd_e3)
    sub.add_parser("where").set_defaults(fn=cmd_where)
    sub.add_parser("down").set_defaults(fn=cmd_down)
    args = ap.parse_args()
    args.fn(args)


if __name__ == "__main__":
    main()
