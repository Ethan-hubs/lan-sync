# G1 · Syncthing Adapter 接口约定（v1，2026-09-13）

> 对应《局域网同步系统-产品文档-v0.9(rc)》§2.6 准入条件 G1 与 §6.1.1 职责 7 条。
> **本约定的每一条 REST 行为都在本机真机验证过**（Syncthing v2.1.5 + 自建 stdiscosrv/strelaysrv，
> 见 `adapter/lab.py`、`adapter/net_lab.py`、`local-lab/REPORT.md`）。
> 参考实现：`adapter/syncthing_adapter.py`（Python，仅用于验证契约；产品语言未定，契约本身语言无关）。

---

## 0. 边界（先写死，防止 Adapter 变成第二个引擎）

**做**：配置读写 · 设备与文件夹管理 · 事件订阅 · 连接类型判定 · pause/resume · 版本恢复 · 进程健康检查。

**不做**：不改块算法与哈希 · 不实现同步协议 · 不实现 NAT 穿透与打洞 · 不实现授权/鉴权 ·
不自己造 discovery/relay/信令 · 不重命名冲突文件（保持原生 `.sync-conflict-*`，只在 UI 呈现层翻译）。

> 判据：凡"Syncthing 已经做完的事"，Adapter 只做**读写与转发**，不做替换（PRD §2.7）。

---

## 1. 职责 1：配置读写

| 目的 | REST | 备注 |
|---|---|---|
| 读整份配置 | `GET /rest/config` | 含 devices/folders/options |
| 读/改选项 | `GET` / `PUT /rest/config/options` | 改前先 GET 再只改差异键（PUT 是整体覆盖） |
| 是否需要重启 | `GET /rest/config/restart-required` | `{"requiresRestart": bool}` |
| 重启生效 | `POST /rest/system/restart` | 重启后需重新等到 `/rest/system/ping` 可用 |
| 默认模板 | `GET/PUT /rest/config/defaults/{folder,device,ignores}` | 新建文件夹/设备时取默认值 |

**实现要点（实测）**
1. `PUT /rest/config/options` 是**整体替换**：必须 GET → 改键 → PUT，否则会清掉未提交的字段。
2. `restart-required` 只有部分选项会置位（例如 `listenAddresses`）；设备/文件夹变更通常**不需要**重启。
3. **改地址不会断掉已有连接**：把对端地址改成 relay URI 后，原来的直连会继续用着（实测：连接类型仍为 `tcp-client`）。
   需要切换路径时必须**改 listen 地址并主动 `POST /rest/system/restart`**。

## 2. 职责 2：设备与文件夹

| 目的 | REST |
|---|---|
| 列/增设备 | `GET` / `POST /rest/config/devices` |
| 改/删设备 | `PUT` / `DELETE /rest/config/devices/{id}` |
| 待批准设备 | `GET` / `POST` / `DELETE /rest/cluster/pending/devices` |
| 列/增文件夹 | `GET` / `POST /rest/config/folders` |
| 改/删文件夹 | `GET` / `PUT` / `DELETE /rest/config/folders/{id}` |
| 读取忽略规则 | `GET /rest/db/ignores?folder=` |
| 写忽略规则 | `POST /rest/db/ignores?folder=` |
| 触发重扫 | `POST /rest/db/scan?folder=&sub=` |
| 文件夹状态 | `GET /rest/db/status?folder=` |

**实现要点（实测）**
1. **`versioning.params` 的值必须是字符串**：`{"maxAge": "2592000"}` ✅（30 天）；传数字 30 → `HTTP 400
   json: cannot unmarshal number into Go struct field ... params.maxAge of type string`
   （源码依据：`lib/config/versioningconfiguration.go` 里 `Params map[string]string`；顶层 `cleanupIntervalS` 反而是 int）。
   ⚠️ **而且这个参数的单位是秒**（staggered 版式器按秒解析，源码 `lib/versioner/staggered.go:39`，默认 31536000＝1 年）：
   写 `"30"` 只保留 **30 秒**，**保留 30 天必须写 `"2592000"`**。
2. 版本策略落地为 **staggered + 保留 30 天＝`maxAge="2592000"`**（PRD §FR-3.3 / ADR A9），归档目录 `.stversions`。
3. `.stignore` 由 Adapter 经 `/rest/db/ignores` 管理，**UI 不暴露这个文件名**（PRD §FR-1.7）。
   实测：写入 `*.labignore` 后，该模式的文件确实不再同步到对端。
4. 加设备时给 `addresses: ["dynamic"]` 表示"靠发现/中继去找"；给显式地址（`tcp://host:port`、
   `relay://relay/?id=...&token=...`）则强制走该路径 —— 这是 E4-B 强制走 relay 的开关。

## 3. 职责 3：事件订阅

| 目的 | REST |
|---|---|
| 事件流（长轮询） | `GET /rest/events?since=&timeout=&limit=&events=`（可多次给 `events=`） |
| 磁盘事件 | `GET /rest/events/disk?since=&timeout=` |

**实现要点**：`since` 用上一次返回事件里的 `id` 续订；超时无事件返回空数组是**正常**行为（不是错误），
调用方应循环续订。实测 `LocalChangeDetected` 可稳定收到。

## 4. 职责 4：连接类型（UI 状态灯的数据源）

`GET /rest/system/connections` → `connections[<deviceID>]`，字段：
`address` `at` `clientVersion` `connected` `inBytesTotal` `isLocal` `outBytesTotal` `paused` `startedAt` `type`。

`type` 取值（官方 REST 文档）：`tcp-client` `tcp-server` `quic-client` `quic-server` `relay-client` `relay-server`。

**映射规则（Adapter 唯一需要"解释"的地方）**
```
tcp-*/quic-*  → direct（绿）
relay-*       → relay（黄）
connected=false → offline（红）；paused=true 单独标注
```
实测样例：直连时 A 侧 `tcp-client → 127.0.0.1:22001`，B 侧 `tcp-server`；
强制 relay 后 A 侧变为 `relay-server`，relay 的 `/status` 同时出现 `numActiveSessions ≥ 1`。

## 5. 职责 5：pause / resume

| 目的 | REST |
|---|---|
| 全停 / 全恢复 | `POST /rest/system/pause` / `POST /rest/system/resume` |
| 单设备 | `POST /rest/system/pause?device=<id>` / `resume?device=<id>` |
| 单文件夹 | `PUT /rest/config/folders/{id}`（`{"paused": true/false}`） |

实测：暂停后连接立即断开（`connected=false`），恢复后能自动重建。

> **产品语义**：`pause` 在 PRD 里不只是 UI 功能，它还是**版本恢复的前置步骤**（见职责 6）。

## 6. 职责 6：版本恢复（实测出的一条硬流程）

| 目的 | REST |
|---|---|
| 列可恢复版本 | `GET /rest/folder/versions?folder=&file=` |
| 执行恢复 | `POST /rest/folder/versions?folder=`，body `{"<相对路径>": "<versionTime>"}` |

**实现要点（三条，全部来自实测）**
1. **版本归档只在"远端变更替换/删除本地文件"时产生**；本机自己删/改的文件不会在本机归档
   （源码文档：versioning 只处理 *changes received from other devices*）。
2. **恢复必须先把对端连接暂停**，否则对端的较新版本会在 1–2 秒内把恢复结果覆盖回去——
   而且 `POST` 接口本身**返回成功**（不报错），属于"静默失效"。
   实测对照（`adapter/exp_restore.py`，结论存 `local-lab/exp-restore.json`）：

   | 策略 | 恢复后 B 内容 | 最终 A/B | 结论 |
   |---|---|---|---|
   | 不暂停对端 | v1（瞬间） | v2 / v2 | **恢复被覆盖，等于无效** |
   | 先 `pause` 对端再恢复 | v1 | v1 / v1 | ✅ 有效，且 v1 会正常传播回对端 |

   ⇒ Adapter 必须实现：`pause(peer) → restore → resume(peer)`，并在 UI 上把这一步说清（"恢复会覆盖对端当前版本"）。
3. `POST /rest/folder/versions` **只按原相对路径恢复**，没有"另存为新文件名"参数（PRD §FR-3.6 已据此收敛）。
   返回体是"每个文件一条错误信息"的字典，**空字典 = 全部成功**——必须检查它，不能只看 HTTP 200。

## 7. 职责 7：进程与健康

| 目的 | 做法 |
|---|---|
| 启动 | `syncthing serve --home <dir> --no-browser --no-upgrade --gui-address <addr> --gui-apikey <key>` |
| 就绪探测 | 轮询 `GET /rest/system/ping`（不要用端口探测） |
| 健康快照 | `GET /rest/system/version` + `GET /rest/system/status` + `restart-required` |
| 日志 | `GET /rest/system/log.txt?since=` |
| 文件夹级错误 | `GET /rest/folder/errors?folder=` |
| 关闭 | `POST /rest/system/shutdown`（或用进程信号） |

**实现要点**：Windows 上进程形态是 **Service**（PRD §6.9 P0）；Adapter 只负责"健康检查 + 必要时重启 + 上报状态"，
不把引擎塞进托盘进程里（托盘只做前端展示，不持有文件句柄）。

---

## 8. 本约定依赖的"上游事实"清单（都有出处，改动上游前先看这张表）

| # | 事实 | 出处 | 对 Adapter 的影响 |
|---|---|---|---|
| F1 | `versioning.params` 值必须是字符串 | 源码 + 实测 400 报错 | 生成配置时的类型 |
| F2 | 恢复会被对端覆盖，除非先暂停对端 | 实测 | 恢复流程必须三步 |
| F3 | `relaysEnabled=false` 时，`relay://` listen 地址**不生效** | 实测（置 true 后 relay 监听才起来） | E4-B 与私有部署必须置 true |
| F4 | 已建立的连接不会因地址变更而中断，需重启 | 实测 | 切换网络路径的流程 |
| F5 | `bytesProxied ≈ 转发的单向字节量`（实测 8 MiB 文件 → 8.02 MiB，1.00×） | 实测 | 成本 ≈ bytesProxied × 单价，**不要除以 2** |
| F6 | `fsWatcherDelayS` 默认 10、可经 REST 调到 1（源码下限 0.01） | 源码 + 实测 | E5 时延调优 |
| F7 | 连接 `type` 六种取值与 `kind` 映射 | 官方 REST 文档 | 状态灯 |
| F8 | `/rest/folder/versions` 无"另存"参数 | 官方文档 + 实测 | 恢复交互只做"恢复/取消" |
| F9 | 忽略规则走 `/rest/db/ignores`，被忽略的文件确实不同步 | 实测 | UI 不暴露 `.stignore` |
| F10 | relay 的 `ID:` 只在 `-debug` 下打印；**URI 默认就会打印** | 源码（`main.go:192` 在 `if debug` 内；`:270` 无条件） | M0.5 手册 §3.3 的取 ID 步骤 |
| F11 | **`versioning.params.maxAge` 的单位是秒**（staggered；默认 31536000＝1 年）。`simple`/`trashcan` 用的是 `cleanoutDays`（单位＝**天**） | 源码 `lib/versioner/staggered.go:39`、`simple.go:95`、`trashcan.go:67` | 保留 30 天必须写 `"2592000"`；写 `"30"` 只有 30 秒 |

---

## 9. 验证方式（可复跑）

```bash
cd /data/lan-sync/adapter
python3 lab.py up && python3 lab.py checks      # 9 项契约检查（含版本恢复三步流程）
python3 exp_restore.py                          # 恢复语义对照实验
python3 net_lab.py up && python3 net_lab.py relay   # 自建 disco + 私有 relay + token
python3 net_lab.py bad-token                    # 负例：错误 token 必须连不上
python3 net_lab.py status / down
```

产物：`local-lab/REPORT.md`（检查结果表）、`local-lab/results.json`、`local-lab/exp-restore.json`。

## 10. 仍未验证 / 待你机器就位的部分

| 项 | 为什么本机测不了 |
|---|---|
| Windows 文件监听行为（Office 锁文件、USR Journal、长路径） | 需要真 Windows |
| 真实跨网 relay（NAT 打洞、公网中继） | 本机是同机回环，只验证了"relay 路径可用" |
| E2 的 ECS 并发-内存曲线与公网带宽上限 | 需要云主机 |
| E5 的真实 P50/P95（本机预演：p50 ≈ 10s，正好等于默认 `fsWatcherDelayS=10`） | 需要真机 + 真实网络 |
| E6 网络切换 | 需要真实网络切换场景 |
| relay 的按设备准入/计量（W1/W2） | 上游本就没有，属改造工作项 |
