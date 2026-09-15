# M0.5 工具包

配套《M0.5 技术基线 PoC 操作手册 v0.2》与《局域网同步系统-产品文档 v0.10(rc)》。

**定位**：把手册里"手敲几十条命令"的部分变成可复跑的工具。**不包含任何产品代码**
（不写 Syncthing Adapter 实现、不写托盘 UI、不写授权）——那些要等 M0.5 出数字、v1.0 冻结之后。

## 目录

```
server/    ECS 侧：docker-compose + 部署脚本 + relay /status 采集器
client/    Windows 侧：hash 校验、连接类型读取、强制走 relay、E5/E6 采集脚本
tools/     E3 造数与比对、M0.5 报告骨架生成
```

## 快速开始

### 1) ECS 侧（自建 discovery + 私有 relay）

```bash
cd server
cp .env.example .env          # 填 ECS 公网地址、镜像 tag
./deploy.sh                   # 生成 token、构建镜像、起两个容器、打印 disco deviceId
./get-relay-id.sh             # 抓 relay 自身 Device ID（手册 §3.3 的修正点）
python3 relay-status-watch.py --csv status.csv --interval 10   # 采样 /status
```

`deploy.sh` 会做**手册 §3 的全部关键动作**：锁版本 tag、`--db-dir` 传目录、`-pools="" -token=` 私有化、
status 端口只绑 `127.0.0.1`、`-keys` 显式指向卷内。跑完记得按手册 §3.4 收紧安全组来源 IP。

### 2) Windows 侧

```powershell
cd client
.\check-binary-hash.ps1                  # 四机二进制一致性（手册 §2.1 / §0.5 P6）
.\get-connection-type.ps1                # 读连接类型（B4 / E4-A）
.\force-relay.ps1 -Action Force          # 临时移除直连 listen，强制走 relay（E4-B）
.\force-relay.ps1 -Action Restore        # 测完恢复（务必执行）
.\watch-fs-events.ps1                    # E5：接收端落地时间采集
.\create-test-files.ps1 -Count 30 -Op create   # E5：源端造数 + 记录源时间
.\watch-connections.ps1                  # E6：250ms 采样连接状态
```

### 3) 文件语义与报告

```bash
python3 tools/make_many_files.py --root /path/to/sync/many --count 100000
python3 tools/verify_tree.py --left /path/to/A/sync --right /path/to/B/sync      # E3 比对
python3 tools/gen_report.py --out M05-报告.md                                    # 报告骨架（含版本锁定表）
```

## 纪律（沿用手册）

1. **专用测试目录**，不拿真实工作资料做破坏性测试。
2. 见到手册 §0.6 的五条 STOP 条件（静默丢文件 / 大小写覆盖 / relay OOM 且数据面异常 /
   连上官方节点 / 误用真实目录）**立即停并保留现场**。
3. `.env`、token、证书、日志、CSV 都在 `.gitignore` 里，**不要提交**。
4. `bytesProxied` 是**双向**合计；阿里云只对出网计费——算钱用出网单向（`relay-status-watch.py` 会同时给出两列）。
