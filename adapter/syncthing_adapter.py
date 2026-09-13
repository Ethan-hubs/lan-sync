#!/usr/bin/env python3
"""Syncthing Adapter —— 参考实现（对应 G1 接口约定的 7 条职责）。

定位：这是**契约的参考实现 + 本地验证工具**，不是最终产品代码。
职责边界严格限定为"配置 / 设备与文件夹 / events / 连接类型 / pause-resume / 版本恢复 / 健康检查"，
不实现同步协议、不改块算法、不做授权（见 PRD §2.7、§6.1.1）。

所有 REST 端点均已在 lib/api/api.go（v2.1.5）核过：
  GET  /rest/system/version|status|connections|ping|paths
  GET  /rest/config  |  /rest/config/options  |  /rest/config/restart-required
  GET/POST/PUT/DELETE /rest/config/devices[/:id]  |  /rest/config/folders[/:id]
  GET/POST /rest/db/ignores?folder=   POST /rest/db/scan?folder=
  GET  /rest/folder/versions?folder=  POST /rest/folder/versions?folder=
  POST /rest/system/pause|resume|restart|shutdown
  GET  /rest/events?since=&timeout=&events=
  GET/DELETE /rest/cluster/pending/devices
"""
from __future__ import annotations

import json
import subprocess
import time
import urllib.error
import urllib.request
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Iterator

# ---------------------------------------------------------------- 连接类型
DIRECT_TYPES = {"tcp-client", "tcp-server", "quic-client", "quic-server"}
RELAY_TYPES = {"relay-client", "relay-server"}


class AdapterError(RuntimeError):
    """REST 调用失败（携带状态码与响应体，便于排障）。"""

    def __init__(self, method: str, path: str, status: int, body: str) -> None:
        super().__init__(f"{method} {path} -> HTTP {status}: {body[:300]}")
        self.status = status


@dataclass
class FolderSpec:
    """创建共享文件夹所需的最小信息（其余走 Syncthing 默认值）。"""

    folder_id: str
    label: str
    path: str
    device_ids: list[str] = field(default_factory=list)
    folder_type: str = "sendreceive"          # sendreceive / sendonly / receiveonly / receiveencrypted
    fs_watcher_delay_s: float = 10.0          # 默认 10（源码 default:"10"），E5 会调它
    versioning_max_age_days: int = 30         # 产品语义=天；写入 Syncthing 时换算成秒（maxAge 单位是秒）
    versioning_type: str = "staggered"
    ignores: list[str] | None = None


class SyncthingRest:
    """极薄的 REST 客户端：只做请求/解析/错误归一。"""

    def __init__(self, base_url: str, api_key: str, timeout: float = 15.0) -> None:
        self.base = base_url.rstrip("/")
        self.key = api_key
        self.timeout = timeout

    def call(self, method: str, path: str, body: Any | None = None,
             timeout: float | None = None, retry: int = 2) -> Any:
        url = f"{self.base}{path}"
        data = None if body is None else json.dumps(body).encode()
        headers = {"X-API-Key": self.key}
        if data is not None:
            headers["Content-Type"] = "application/json"
        last: Exception | None = None
        for attempt in range(retry + 1):
            req = urllib.request.Request(url, data=data, headers=headers, method=method)
            try:
                with urllib.request.urlopen(req, timeout=timeout or self.timeout) as r:
                    raw = r.read()
                    if not raw:
                        return None
                    try:
                        return json.loads(raw)
                    except json.JSONDecodeError:
                        return raw.decode("utf-8", "ignore")
            except urllib.error.HTTPError as e:
                body_txt = e.read().decode("utf-8", "ignore")
                if e.code in (502, 503, 504) and attempt < retry:      # 启动瞬间偶发
                    time.sleep(0.5 * (attempt + 1))
                    last = AdapterError(method, path, e.code, body_txt)
                    continue
                raise AdapterError(method, path, e.code, body_txt) from None
            except (urllib.error.URLError, TimeoutError) as e:
                last = e
                if attempt < retry:
                    time.sleep(0.5 * (attempt + 1))
                    continue
                raise AdapterError(method, path, 0, str(e)) from None
        raise AdapterError(method, path, 0, str(last))

    def get(self, path: str, **kw: Any) -> Any:
        return self.call("GET", path, **kw)

    def post(self, path: str, body: Any | None = None, **kw: Any) -> Any:
        return self.call("POST", path, body, **kw)

    def put(self, path: str, body: Any, **kw: Any) -> Any:
        return self.call("PUT", path, body, **kw)

    def delete(self, path: str, **kw: Any) -> Any:
        return self.call("DELETE", path, **kw)


class SyncthingProcess:
    """职责 7 的一半：进程启停与守护（Windows 上是 Service，这里用子进程等价物）。"""

    def __init__(self, binary: str | Path, home: str | Path, gui_address: str,
                 api_key: str, log_file: str | Path) -> None:
        self.binary = str(binary)
        self.home = str(home)
        self.gui_address = gui_address
        self.api_key = api_key
        self.log_file = Path(log_file)
        self.proc: subprocess.Popen | None = None

    def start(self) -> None:
        cmd = [
            self.binary, "serve",
            "--home", self.home,
            "--no-browser",
            "--no-upgrade",
            "--gui-address", self.gui_address,
            "--gui-apikey", self.api_key,
        ]
        self.log_file.parent.mkdir(parents=True, exist_ok=True)
        self.log = self.log_file.open("ab")
        self.proc = subprocess.Popen(cmd, stdout=self.log, stderr=subprocess.STDOUT)

    def stop(self, timeout: float = 15.0) -> None:
        if not self.proc:
            return
        self.proc.terminate()
        try:
            self.proc.wait(timeout)
        except subprocess.TimeoutExpired:
            self.proc.kill()
        finally:
            self.log.close()
            self.proc = None

    def alive(self) -> bool:
        return bool(self.proc and self.proc.poll() is None)


class SyncthingAdapter:
    """G1 的 7 条职责（配置 / 设备与文件夹 / events / 连接类型 / pause-resume / 版本恢复 / 健康）。"""

    def __init__(self, rest: SyncthingRest, process: SyncthingProcess | None = None) -> None:
        self.rest = rest
        self.process = process

    # ---------------------------------------------------------- 职责 7：健康
    def wait_ready(self, timeout: float = 60.0, poll: float = 0.5) -> dict:
        deadline = time.time() + timeout
        while time.time() < deadline:
            try:
                return self.rest.get("/rest/system/ping", timeout=2)
            except AdapterError:
                time.sleep(poll)
        raise TimeoutError(f"Syncthing 在 {timeout}s 内未就绪（GUI 未响应）")

    def health(self) -> dict:
        version = self.rest.get("/rest/system/version")
        status = self.rest.get("/rest/system/status")
        restart_required = self.rest.get("/rest/config/restart-required")
        return {
            "version": version.get("version"),
            "my_id": status.get("myID"),
            "uptime_s": status.get("uptime"),
            "goroutines": status.get("goroutines"),
            "restart_required": restart_required.get("requiresRestart", False),
            "process_alive": self.process.alive() if self.process else None,
        }

    # ---------------------------------------------------- 职责 1：配置读写
    def get_config(self) -> dict:
        return self.rest.get("/rest/config")

    def get_options(self) -> dict:
        return self.rest.get("/rest/config/options")

    def set_options(self, **options: Any) -> dict:
        """只改传入的键（例如 listenAddresses / globalAnnounceServers / relaysEnabled）。"""
        cur = self.get_options()
        changed = {k: v for k, v in options.items() if cur.get(k) != v}
        if changed:
            cur.update(changed)
            self.rest.put("/rest/config/options", cur)
        return {"changed": changed, "restart_required": self.restart_required()}

    def restart_required(self) -> bool:
        return bool(self.rest.get("/rest/config/restart-required").get("requiresRestart"))

    def apply_and_restart(self, timeout: float = 60.0) -> None:
        """配置改动若需要重启才生效，在这里统一处理（等待重新就绪）。"""
        if self.restart_required():
            self.rest.post("/rest/system/restart")
            time.sleep(2)
            self.wait_ready(timeout)

    # ------------------------------------------------ 职责 2：设备与文件夹
    def add_device(self, device_id: str, name: str, *, addresses: list[str] | None = None,
                   paused: bool = False, introducer: bool = False) -> dict:
        if not device_id or device_id.count("-") != 7:
            raise ValueError(f"Device ID 形如 7 段短横线分隔：{device_id!r}")
        body = {
            "deviceID": device_id,
            "name": name,
            "addresses": addresses or ["dynamic"],
            "paused": paused,
            "introducer": introducer,
            "compression": "metadata",
        }
        return self.rest.post("/rest/config/devices", body)

    def remove_device(self, device_id: str) -> None:
        self.rest.delete(f"/rest/config/devices/{device_id}")

    def device_pending(self) -> list[dict]:
        """尚未被接受的待批准设备（成员准入的第二道确认）。"""
        return self.rest.get("/rest/cluster/pending/devices") or {}

    def accept_pending(self, device_id: str, name: str = "") -> Any:
        return self.rest.post(f"/rest/cluster/pending/devices?device={device_id}&name={name or device_id}")

    def add_folder(self, spec: FolderSpec) -> dict:
        body = {
            "id": spec.folder_id,
            "label": spec.label,
            "path": spec.path,
            "type": spec.folder_type,
            "devices": [{"deviceID": d, "introducedBy": "", "encryptionPassword": ""}
                        for d in spec.device_ids],
            "rescanIntervalS": 3600,
            "fsWatcherEnabled": True,
            "fsWatcherDelayS": spec.fs_watcher_delay_s,
            # ⚠️ 实测（v2.1.5）：versioning.params 的值必须是**字符串**
            #    错法：{"maxAge": 30} → HTTP 400 cannot unmarshal number into ... of type string
            "versioning": {
                "type": spec.versioning_type,
                # ⚠️ maxAge 单位是**秒**不是天（源码 lib/versioner/staggered.go:39，默认 31536000=1 年）
                "params": {"maxAge": str(int(spec.versioning_max_age_days) * 86400), "cleanupIntervalS": "3600"},
                "cleanupIntervalS": 3600,
            },
        }
        return self.rest.post("/rest/config/folders", body)

    def get_folder(self, folder_id: str) -> dict:
        return self.rest.get(f"/rest/config/folders/{folder_id}")

    def update_folder(self, folder_id: str, patch: dict) -> dict:
        """读-改-写，避免 REST PUT 整体覆盖时丢字段。"""
        cur = self.get_folder(folder_id)
        cur.update(patch)
        return self.rest.put(f"/rest/config/folders/{folder_id}", cur)

    def folder_status(self, folder_id: str) -> dict:
        return self.rest.get(f"/rest/db/status?folder={folder_id}")

    def rescan(self, folder_id: str, sub: str = "") -> Any:
        return self.rest.post(f"/rest/db/scan?folder={folder_id}" + (f"&sub={sub}" if sub else ""))

    def get_ignores(self, folder_id: str) -> list[str]:
        return self.rest.get(f"/rest/db/ignores?folder={folder_id}").get("ignore", [])

    def set_ignores(self, folder_id: str, patterns: list[str]) -> Any:
        """对应 PRD §FR-1.7：用原生 .stignore，由 Adapter 经 REST 管理，UI 不暴露文件名。"""
        return self.rest.post(f"/rest/db/ignores?folder={folder_id}", {"ignore": patterns})

    # ------------------------------------------------------ 职责 3：events
    def events(self, *, events: list[str] | None = None, since: int = 0,
               timeout: float = 60.0, limit: int = 100) -> Iterator[dict]:
        """长轮询事件流；超时/断流由调用方决定是否续订（返回值为完整事件，含 id）。"""
        q = [f"since={since}", f"timeout={int(timeout)}", f"limit={limit}"]
        if events:
            q += [f"events={e}" for e in events]
        data = self.rest.get("/rest/events?" + "&".join(q), timeout=timeout + 10)
        for ev in data or []:
            yield ev

    def wait_events(self, predicate, *, events: list[str] | None = None,
                    timeout: float = 60.0, since: int = 0) -> dict:
        """等到 predicate(ev) 为真；返回该事件。用于测试与 UI 状态流转。"""
        deadline = time.time() + timeout
        last_id = since
        while time.time() < deadline:
            got = False
            for ev in self.events(events=events, since=last_id, timeout=min(30, timeout)):
                got = True
                last_id = ev.get("id", last_id)
                if predicate(ev):
                    return ev
            if not got:
                time.sleep(0.5)
        raise TimeoutError("等待事件超时")

    def disk_events(self, since: int = 0, timeout: float = 30.0) -> list[dict]:
        return self.rest.get(f"/rest/events/disk?since={since}&timeout={int(timeout)}")

    # ------------------------------------------------- 职责 4：连接类型
    def connections(self) -> dict:
        return self.rest.get("/rest/system/connections").get("connections", {})

    def connection_type(self, device_id: str) -> dict:
        c = self.connections().get(device_id, {})
        t = c.get("type", "")
        return {
            "device": device_id,
            "connected": bool(c.get("connected")),
            "type": t,
            "kind": "direct" if t in DIRECT_TYPES else "relay" if t in RELAY_TYPES else "none",
            "address": c.get("address"),
            "in_bytes": c.get("inBytesTotal"),
            "out_bytes": c.get("outBytesTotal"),   # 出网口径（成本用这个）
        }

    def connection_summary(self) -> dict:
        direct = relay = none = 0
        for dev in self.connections().values():
            t = dev.get("type", "")
            if t in DIRECT_TYPES:
                direct += 1
            elif t in RELAY_TYPES:
                relay += 1
            else:
                none += 1
        return {"direct": direct, "relay": relay, "none": none}

    def wait_for_connection(self, device_id: str, *, timeout: float = 60.0,
                            want: str | None = None) -> dict:
        """等连接建立（可选：等特定 kind，如 'relay'）。返回连接详情。"""
        deadline = time.time() + timeout
        last = {}
        while time.time() < deadline:
            try:
                last = self.connection_type(device_id)
            except AdapterError:
                time.sleep(0.5)
                continue
            if last["connected"] and (want is None or last["kind"] == want):
                return last
            time.sleep(0.5)
        raise TimeoutError(f"等待 {device_id} 连接{'(' + want + ')' if want else ''}超时；最后状态={last}")

    # ---------------------------------------------- 职责 5：pause / resume
    def pause(self, device_id: str | None = None) -> Any:
        return self.rest.post("/rest/system/pause" + (f"?device={device_id}" if device_id else ""))

    def resume(self, device_id: str | None = None) -> Any:
        return self.rest.post("/rest/system/resume" + (f"?device={device_id}" if device_id else ""))

    def folder_pause(self, folder_id: str) -> Any:
        return self.update_folder(folder_id, {"paused": True})

    def folder_resume(self, folder_id: str) -> Any:
        return self.update_folder(folder_id, {"paused": False})

    # --------------------------------------------------- 职责 6：版本恢复
    def list_versions(self, folder_id: str, path: str | None = None) -> dict[str, list[dict]]:
        """GET /rest/folder/versions：本设备可恢复的版本（key = 相对路径）。"""
        q = f"/rest/folder/versions?folder={folder_id}"
        if path:
            q += f"&file={path}"
        return self.rest.get(q) or {}

    def restore_version(self, folder_id: str, path: str, version_time: str) -> dict:
        """POST /rest/folder/versions：恢复回**原相对路径**（原生无"另存"参数，见 PRD §FR-3.6）。"""
        return self.rest.post(f"/rest/folder/versions?folder={folder_id}", {path: version_time}) or {}

    # --------------------------------------------------- 其它：错误与日志
    def folder_errors(self, folder_id: str | None = None) -> list[dict]:
        q = f"?folder={folder_id}" if folder_id else ""
        return self.rest.get(f"/rest/folder/errors{q}") or []

    def recent_log(self, since: str = "") -> str:
        return self.rest.get(f"/rest/system/log.txt?since={since}")
