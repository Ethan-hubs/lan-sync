# lan-sync

**局域网优先、跨网自动回落自有中继的文件同步系统**——面向 3–5 人的小团队自用场景。

同一网段内设备点对点直连、跑满内网带宽；不在同一网段时自动经**自己部署的** Syncthing 中继
（`strelaysrv`）加密中转；全程端到端加密，中继只转发密文。

> 本仓库是**设计与工具仓库**：产品需求文档 + M0.5 技术基线验证手册 + 配套测量/部署工具包。
> 不含产品实现代码（托盘 UI / 适配层）——那些在 M0.5 验证通过、技术基线冻结之后才开始写。

## 文档

| 文件 | 内容 |
|---|---|
| [`docs/局域网同步系统-产品文档-v0.9.md`](docs/局域网同步系统-产品文档-v0.9.md) | 产品需求文档（rc）：场景 / 功能需求 / 架构 / 成本模型 / 里程碑 / 风险 / 验收标准，附录含开源生态调研、评审往复、同步数据表现、内部数据说明、ADR |
| [`docs/M0.5-技术基线PoC操作手册-v0.3.md`](docs/M0.5-技术基线PoC操作手册-v0.3.md) | 照单可跑的基线验证手册：五项基础能力 + 六个实验 + 版本锁定表 + 冻结 Gate（v0.3 已按真机实验校准） |
| [`docs/G1-Adapter接口约定.md`](docs/G1-Adapter接口约定.md) | **Adapter 接口契约**：7 条职责、每条 REST 调用、8 条上游事实、10 条实测注意点 |
| [`docs/M0.5-本地实验结论（Linux子集）.md`](docs/M0.5-本地实验结论（Linux子集）.md) | 本地实验结论：版本锁定表、B1–B5/C1–C9 结果、7 处假设纠正、成本口径实测数字 |

## 技术路线（一句话）

复用 **Syncthing**（MPL-2.0）做同步引擎，自建发现服务 `stdiscosrv` 与私有中继 `strelaysrv`，
自研只留三样：**产品壳（托盘 UI）+ 适配层 + 私有部署**。
凡 Syncthing 原生已解决的问题，本项目不重新设计协议。

```
Windows 托盘 UI（自研产品壳）
      ↓
Syncthing Adapter（自研适配层）
      ↓
Syncthing Headless / Windows Service（原生引擎）
      ├─ 局域网：原生 Local Discovery + TCP/QUIC 直连
      ├─ 跨网：自建 stdiscosrv（发现）
      └─ 直连失败：自建私有 strelaysrv（TCP，可映射 443，共享 -token）
```

## 参考实现与验证 harness `adapter/`

| 文件 | 内容 |
|---|---|
| `syncthing_adapter.py` | **契约的参考实现**（Python）：配置读写 / 设备与文件夹 / events / 连接类型 / pause-resume / 版本恢复 / 健康检查 |
| `lab.py` | 本地实验台：同机起两个 Syncthing 实例并跑 9 项契约检查（含版本恢复三步流程） |
| `net_lab.py` | 网络实验：自建 `stdiscosrv` + 私有 `strelaysrv`（共享 token），验证发现链路、强制 relay、错误 token 负例 |
| `exp_restore.py` | 恢复语义对照实验（"不暂停对端 = 被覆盖"的实测证据） |

```bash
cd adapter
python3 lab.py up && python3 lab.py checks      # 9 项契约检查
python3 net_lab.py up && python3 net_lab.py relay   # 私有 relay + token
python3 exp_restore.py                          # 恢复语义对照
```

## 工具包 `m05-toolkit/`

| 目录 | 内容 |
|---|---|
| `server/` | `docker-compose.yml`（锁版本 tag、`--db-dir` 传目录、token 从文件读、status 只绑本机）、`deploy.sh` 一键部署、`get-relay-id.sh`（默认日志不含 relay ID，需一次性 `-debug`）、`relay-status-watch.py`（`/status` 采样，**同时给出双向与出网单向口径**） |
| `client/` | PowerShell：二进制 hash 一致性校验、读连接类型、**临时移除直连 listen 强制走 relay**（比防火墙干净）、E5 落地时延采集、E6 连接切换 250ms 采样、造数 |
| `tools/` | E3 造 10 万文件 + oracle、两端目录比对（退出码可判）、M0.5 报告骨架生成 |

```bash
# 服务端
cd m05-toolkit/server && cp .env.example .env && ./deploy.sh && python3 relay-status-watch.py --csv status.csv
# 文件语义（E3）
python3 m05-toolkit/tools/make_many_files.py --root ./sync/many --count 100000
python3 m05-toolkit/tools/verify_tree.py --left ./A/sync --right ./B/sync
# 报告骨架
python3 m05-toolkit/tools/gen_report.py --out M05-报告.md
```

## 几个上游事实（都做过源码级核对，写进文档避免返工）

| 事实 | 影响 |
|---|---|
| `strelaysrv` 只有**全局共享 `-token`**，没有按设备白名单/黑名单 | 按设备 ACL 要改中继代码，属独立工作项 |
| `bytesProxied` 是**双向合计**，而云厂商只对出网计费 | 直接拿它算流量费会**高估约 2 倍** |
| relay 的 `ID:` 与 URI 打印在 `if debug` / `if natEnabled` 分支内 | 默认日志拿不到，需一次性 `-debug` |
| 文件版本（versioning）**默认关闭**，且只归档「远端变更导致本地被替换/删除」 | 恢复能力按设备、按 Folder 计算 |
| `fsWatcherDelayS` 默认 **10 秒**；`reconnectionIntervalS` 默认 **20 秒**（下限 5） | 直接影响感知时延与切换 SLA |
| relay 转发载荷**完全不透明**（relay 协议文档全文无 "folder" 一词） | 中继看不到内容、文件名、folder ID、块哈希 |
| **`bytesProxied` ≈ 转发的单向字节量**（实测 8 MiB 文件 → 8.02 MiB，1.00×） | 成本 = `bytesProxied` × 单价；倍数来自**接收端数量**，不是 2 |
| **恢复必须先暂停对端**，否则对端较新版本会静默覆盖回去（API 仍返回成功） | 恢复流程 = `pause(peer) → restore → resume` |
| `relaysEnabled=false` 时 `relay://` listen 地址**不生效**；已建立的连接不会因地址变更而断 | 切换网络路径要"改 listen + 重启" |

## 许可

- 本仓库内容：MIT（见 [`LICENSE`](LICENSE)）。
- 依赖的上游组件：Syncthing 为 **MPL-2.0**（本仓库不修改其源码，仅按官方方式部署与调用）。

## 状态

设计已收敛为**实施基线候选（rc）**；下一步按手册跑 M0.5，把实测数字回填文档后再冻结为 v1.0。
