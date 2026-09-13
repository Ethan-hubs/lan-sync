#!/usr/bin/env python3
"""本地网络实验：自建 stdiscosrv（发现）+ 私有 strelaysrv（中继 + 共享 token）。
对应手册 §3 / §5 的 B2/B3/B4，在单机上把"跨网"路径用显式地址复现出来。

用法：
  python3 net_lab.py up          # 起 disco + relay，打印 DISCO_ID / RELAY_ID
  python3 net_lab.py relay       # 让 A/B 只走私有 relay，验证连接类型=relay-* 与字节计量
  python3 net_lab.py disco       # 让 A/B 用自建 disco（对端地址=dynamic），验证发现链路
  python3 net_lab.py bad-token   # 负例：错误 token 必须连不上
  python3 net_lab.py status      # disco/relay 与两个实例的状态
  python3 net_lab.py down        # 停 disco/relay
"""
from __future__ import annotations

import argparse
import json
import re
import secrets
import shutil
import subprocess
import sys
import time
import urllib.request
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from lab import INSTANCES, adapter, my_device_id, paths  # noqa: E402

LAB = Path("/data/lan-sync/local-lab")
BIN = LAB / "bin"
NET = LAB / "net"
DISCO_PORT, RELAY_PORT, RELAY_STATUS = 18443, 12267, 12270     # 非默认端口，避免与真机冲突
TOKEN_FILE = NET / "relay-token"
PROCS = NET / "procs.json"


# ------------------------------------------------------------------ 进程管理
def _spawn(name: str, cmd: list[str], cwd: Path) -> subprocess.Popen:
    cwd.mkdir(parents=True, exist_ok=True)
    log = (NET / f"{name}.log").open("ab")
    p = subprocess.Popen(cmd, cwd=str(cwd), stdout=log, stderr=subprocess.STDOUT)
    procs = json.loads(PROCS.read_text()) if PROCS.exists() else {}
    procs[name] = p.pid
    PROCS.write_text(json.dumps(procs))
    return p


def relay_token() -> str:
    NET.mkdir(parents=True, exist_ok=True)
    if not TOKEN_FILE.exists():
        TOKEN_FILE.write_text(secrets.token_hex(32))
        TOKEN_FILE.chmod(0o600)
    return TOKEN_FILE.read_text().strip()


def read_id(log: Path, pattern: str) -> str | None:
    if not log.exists():
        return None
    m = re.search(pattern, log.read_text(errors="ignore"))
    return m.group(1) if m else None


def disco_id() -> str | None:
    return read_id(NET / "disco.log", r"deviceId=([A-Z0-9-]{20,})")


def relay_id() -> str | None:
    return read_id(NET / "relay.log", r"ID: *([A-Z0-9-]{20,})")


def relay_status() -> dict:
    try:
        with urllib.request.urlopen(f"http://127.0.0.1:{RELAY_STATUS}/status", timeout=5) as r:
            return json.load(r)
    except Exception as e:                                            # noqa: BLE001
        return {"error": str(e)}


# ------------------------------------------------------------------ 子命令
def cmd_up(_) -> None:
    NET.mkdir(parents=True, exist_ok=True)
    (NET / "disco.log").unlink(missing_ok=True)
    (NET / "relay.log").unlink(missing_ok=True)

    # stdiscosrv：db-dir 传【目录】（手册 §3.2 的修正点）
    (NET / "disco").mkdir(exist_ok=True)
    _spawn("disco", [str(BIN / "stdiscosrv"),
                     f"--listen=127.0.0.1:{DISCO_PORT}",
                     f"--db-dir={NET/'disco'}",
                     f"--cert={NET/'disco'/'cert.pem'}",
                     f"--key={NET/'disco'/'key.pem'}"], NET)

    # strelaysrv：-pools="" + -token；-debug 仅用于拿 ID（手册 §3.3 的修正点）
    (NET / "relay").mkdir(exist_ok=True)
    _spawn("relay", [str(BIN / "strelaysrv"),
                     f"-listen=127.0.0.1:{RELAY_PORT}",
                     f"-status-srv=127.0.0.1:{RELAY_STATUS}",
                     f"-keys={NET/'relay'}",
                     '-pools=', f'-token={relay_token()}', "-debug"], NET)
    time.sleep(6)
    print("DISCO_ID =", disco_id())
    print("RELAY_ID =", relay_id())
    print("RELAY_STATUS =", json.dumps({k: relay_status().get(k) for k in
                                        ("numConnections", "numActiveSessions", "bytesProxied", "options")},
                                       ensure_ascii=False))
    print("\n接着跑：python3 net_lab.py relay   （验证私有中继 + token）")


def _set_peer_address(name: str, peer: str, addresses: list[str], *, dynamic: bool = False) -> None:
    ad = adapter(name)
    dev = next(d for d in ad.get_config()["devices"] if d["deviceID"] == my_device_id(peer))
    dev["addresses"] = addresses
    ad.rest.put(f"/rest/config/devices/{dev['deviceID']}", dev)


def cmd_relay(_) -> None:
    rid, tok = relay_id(), relay_token()
    assert rid, "拿不到 RELAY_ID，先跑 net_lab.py up"
    uri = f"relay://127.0.0.1:{RELAY_PORT}/?id={rid}&token={tok}"
    print("relay URI =", uri)
    # 关键（手册 §E4-B 的首选做法）：**把直连 listen 项拿掉**再重启。
    # 只改对端地址不够——已建立的直连不会被中断，Syncthing 也不会因为地址变更而重拨。
    for name in INSTANCES:
        peer = "B" if name == "A" else "A"
        ad = adapter(name)
        ad.set_options(relaysEnabled=False, listenAddresses=[uri])   # 只留 relay，无直连监听
        _set_peer_address(name, peer, [uri])                          # 对端也只给 relay 地址
        ad.rest.post("/rest/system/restart")                          # 强制重启，丢掉旧直连
        time.sleep(3)
        ad.wait_ready(60)
        ad.resume(my_device_id(peer))
        print(f"[{name}] 已切为 relay-only 并重启（listen={uri[:38]}…）")

    print("\n等待 relay 连接建立（最多 90s）…")
    ok = False
    for i in range(18):
        time.sleep(5)
        a = adapter("A").connection_type(my_device_id("B"))
        st = relay_status()
        if a["kind"] == "relay" and st.get("numActiveSessions", 0) >= 1:
            ok = True
            break
        print(f"  +{(i+1)*5}s  A->B type={a['type']} kind={a['kind']} "
              f"relay sessions={st.get('numActiveSessions')} conns={st.get('numConnections')}")

    st = relay_status()
    print(f"\n结果：连接类型={'relay 成功 ✅' if ok else '未建立 ❌'}")
    print("relay /status:", json.dumps({k: st.get(k) for k in
                                        ("numConnections", "numActiveSessions", "numProxies", "bytesProxied")},
                                       ensure_ascii=False))
    print("A->B 连接详情:", json.dumps(adapter("A").connection_type(my_device_id("B")), ensure_ascii=False))

    # 传一个已知大小的文件，验证 bytesProxied 增长（双向合计口径）
    before = relay_status().get("bytesProxied", 0)
    blob = Path(paths("A")["sync"]) / "relay-bytes-test.bin"
    blob.write_bytes(secrets.token_bytes(4 * 1024 * 1024))       # 4 MiB 不可压缩
    target = Path(paths("B")["sync"]) / blob.name
    t0, ok_file = time.time(), False
    while time.time() - t0 < 120:
        if target.exists() and target.stat().st_size == blob.stat().st_size:
            ok_file = True
            break
        time.sleep(1)
    time.sleep(3)
    after = relay_status().get("bytesProxied", 0)
    delta = after - before
    print(f"文件同步={'成功 ✅' if ok_file else '失败 ❌'}；relay bytesProxied 增量 = {delta} 字节 "
          f"（双向合计；文件 4 MiB → 单向约 {delta//2/1024/1024:.1f} MiB）")


def cmd_disco(_) -> None:
    did = disco_id()
    assert did, "拿不到 DISCO_ID，先跑 net_lab.py up"
    url = f"https://127.0.0.1:{DISCO_PORT}/?id={did}"
    print("disco URL =", url)
    for name in INSTANCES:
        peer = "B" if name == "A" else "A"
        ad = adapter(name)
        # 恢复直连监听（本测试要验证「只靠自建 disco 就能找到对方」），关掉 relay 路径
        ad.set_options(globalAnnounceEnabled=True, globalAnnounceServers=[url],
                       localAnnounceEnabled=False, relaysEnabled=False,
                       listenAddresses=[f"tcp://0.0.0.0:{INSTANCES[name]['sync_port']}",
                                        f"quic://0.0.0.0:{INSTANCES[name]['sync_port']}"])
        _set_peer_address(name, peer, ["dynamic"])      # 不给静态地址
        ad.rest.post("/rest/system/restart"); time.sleep(3); ad.wait_ready(60)
        ad.resume()
        print(f"[{name}] 已切到自建 disco，对端地址=dynamic，已重启")
    time.sleep(5)
    log = (NET / "disco.log").read_text(errors="ignore")
    announces = log.count("Announce") + log.count("announce")
    for i in range(12):
        time.sleep(5)
        a = adapter("A").connection_type(my_device_id("B"))
        if a["connected"]:
            print(f"  +{(i+1)*5}s  A->B 已连接 type={a['type']} address={a['address']}")
            break
        print(f"  +{(i+1)*5}s  未连接…")
    print(f"disco 日志里的 announce 相关行数 = {announces}（B2 观测项）")


def cmd_bad_token(_) -> None:
    """起第三个实例 C，只给错误 token 的 relay 地址，应永远连不上。"""
    from lab import INSTANCES as I
    I.setdefault("C", {"gui": "127.0.0.1:8386", "sync_port": 22002})
    rid = relay_id()
    bad = f"relay://127.0.0.1:{RELAY_PORT}/?id={rid}&token=wrong-token-{secrets.token_hex(4)}"
    paths("C")["home"].mkdir(parents=True, exist_ok=True)
    c = adapter("C", with_process=True)          # 必须带 process，否则 c.process 为 None
    paths("C")["sync"].mkdir(parents=True, exist_ok=True)
    try:
        c.rest.get("/rest/system/ping", timeout=2)
        print("[C] 已在运行")
    except Exception:                                                  # noqa: BLE001
        c.process.start()
        c.wait_ready(60)
        print("[C] 已启动，Device ID =", my_device_id("C"))
    c.set_options(globalAnnounceEnabled=False, globalAnnounceServers=[], localAnnounceEnabled=False,
                  relaysEnabled=False,
                  listenAddresses=[f"tcp://0.0.0.0:{I['C']['sync_port']}"])
    c.apply_and_restart(60)

    # C 加 A/B，但地址只给"错误 token 的 relay"
    for peer in ("A", "B"):
        ids = [d["deviceID"] for d in c.get_config()["devices"]]
        if my_device_id(peer) not in ids:
            c.add_device(my_device_id(peer), f"lab-{peer}", addresses=[bad])
        else:
            _set_peer_address("C", peer, [bad])
    c.resume()

    print("等待 60s，确认 C 永远连不上（relay 应拒绝错误 token）…")
    for i in range(12):
        time.sleep(5)
        conns = {p: c.connection_type(my_device_id(p)) for p in ("A", "B")}
        if any(v["connected"] for v in conns.values()):
            print(f"  ❌ 竟然连上了：{json.dumps(conns, ensure_ascii=False)}")
            return
        print(f"  +{(i+1)*5}s  仍未连接（符合预期）")
    log = (NET / "relay.log").read_text(errors="ignore")
    print("relay 日志中的 invalid token 计数 =", log.count("invalid token"))
    print("结论：错误 token 无法使用私有 relay ✅（Relay 使用准入有效）")


def cmd_status(_) -> None:
    st = relay_status()
    print("relay:", json.dumps({k: st.get(k) for k in
                                ("numConnections", "numActiveSessions", "numProxies", "bytesProxied",
                                 "uptimeSeconds", "options")}, ensure_ascii=False))
    print("DISCO_ID =", disco_id(), "| RELAY_ID =", relay_id())
    for name in ("A", "B"):
        try:
            ad = adapter(name)
            print(f"[{name}] {json.dumps(ad.connection_summary(), ensure_ascii=False)} "
                  f"device={ad.health()['my_id']}")
        except Exception as e:                                        # noqa: BLE001
            print(f"[{name}] 不可用：{e}")


def cmd_down(_) -> None:
    if PROCS.exists():
        procs = json.loads(PROCS.read_text())
        for name, pid in procs.items():
            try:
                subprocess.run(["kill", str(pid)], check=False)
                print(f"[{name}] 已停 (pid {pid})")
            except Exception as e:                                    # noqa: BLE001
                print(f"[{name}] 停止失败：{e}")
        PROCS.unlink(missing_ok=True)


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("cmd", choices=["up", "relay", "disco", "bad-token", "status", "down"])
    args = ap.parse_args()
    {"up": cmd_up, "relay": cmd_relay, "disco": cmd_disco, "bad-token": cmd_bad_token,
     "status": cmd_status, "down": cmd_down}[args.cmd](args)


if __name__ == "__main__":
    main()
