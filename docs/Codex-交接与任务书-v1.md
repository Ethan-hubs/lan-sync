# Codex 交接与任务书 v1

> 目的：把「lan-sync」项目当前的真实状态、已验证事实、硬约束、以及**可直接粘贴给 Codex 的指令**集中到一份文件里。
> 来源：2026-09-13 全程会话（PRD 六轮评审 → M0.5 本地实验 → WinServer-A 真机跨网实验 → Codex 方案复核）。
> 使用方式：① 先发「指令 A」让 Codex 对齐事实（**不写代码**）；② 确认后发「指令 B」开始迭代 1。
> 维护者：太极（Linux 侧）｜实现方：Codex（Windows 侧）

---

## 1. 一页纸项目定位

| 项 | 内容 |
|---|---|
| 目标 | 局域网优先、跨网自动经自有云中继的**文件同步工具**（自用 + 小团队） |
| 引擎路线 | **复用 Syncthing 原生引擎**（不写同步协议、不改块算法） |
| 阶段一形态 | Windows 托盘 UI → C# Adapter → Syncthing（Windows Service）→ LAN 直连 / 自建 `stdiscosrv` / 私有 `strelaysrv`（共享 token） |
| 阶段一不做 | 授权中心、账号 / OIDC、管理后台、自研 signaling、数据库、多租户、Docker Desktop |
| 阶段二 | 授权（Ed25519 凭证 + 心跳 + 吊销）；中继逐设备准入 / 计量（W1/W2，需 fork relay） |
| 公开仓库 | `github.com/Ethan-hubs/lan-sync`（public · MIT · 已脱敏，`scripts/build_public.py` 是唯一发布入口） |
| 私有项目目录 | `/data/lan-sync/`（PRD v0.9、手册 v0.3、docs/、adapter/、m05-toolkit/、local-lab/、history/、research/） |
| 交付发布位 | `https://files.example.com/`（首页卡片式下载页） |

**核心设计原则（PRD §2.7）**：凡是 Syncthing 原生已解决的问题，阶段一**不在文档里重新设计协议**；阶段一只自研三样——**产品壳 + 适配层 + 私有部署**。

---

## 2. 现状快照（2026-09-13 18:15 为准）

### 2.1 已完成并有实测证据

| 项 | 状态 | 证据位置 |
|---|---|---|
| PRD v0.9（实施基线候选 rc） | ✅ 六轮外部评审 P0 已清零 | `/data/lan-sync/局域网同步系统-产品文档-v0.9.md` |
| M0.5 操作手册 v0.3 | ✅ 含 7 处真机实测纠正 | `M0.5-技术基线PoC操作手册-v0.3.md` |
| G1 Adapter 接口约定 | ✅ 7 条职责 + 8 条上游事实 + 10 条实测注意点 | `docs/G1-Adapter接口约定.md` |
| M0.5 本地实验结论（Linux 子集） | ✅ 9/9 契约检查全绿 + 错误 token 负例通过 | `docs/M0.5-本地实验结论（Linux子集）.md` |
| M0.5 真机实验结论（Windows + 跨网） | ✅ 跨网同步跑通 + 4 项新发现 | `docs/M0.5-真机实验结论（Windows+跨网）.md` |
| Python 参考 Adapter + 可复跑 harness | ✅ | `adapter/`（`lab.py` / `net_lab.py` / `wan_lab.py` / `syncthing_adapter.py` / `exp_restore.py`） |
| M0.5 工具包 13 个文件 | ✅ | `m05-toolkit/`（server / client / tools） |
| 公共仓库 | ✅ 3 个提交，脱敏复扫 0 命中 | `public-repo/`（工作副本，remote = Ethan-hubs/lan-sync） |

### 2.2 进行中（未收口）

| 项 | 现状 |
|---|---|
| **E5 跨网感知时延** | t5/t6 的 89–103 s **是采集器 bug（PS 5.1 `Start-Sleep` 阻塞事件派发）造成的假数字**，已修（改用 `$Event.TimeGenerated`）；修好后 t7/t8 因 SSH/回流通道波动未取到有效样本。**独立轮询法已得出真实值 ≈ 2–3 s**（默认 `fsWatcherDelayS=10`）——还差**一次干净的成组跑**把 P50/P95 落表 |
| Windows 专属语义（E3） | 中文 / emoji / 空格单引号 / 重音 / 32 MiB / 2000 文件 ✅；大小写碰撞 ✅（会报错+卡队列）；>260 字符路径 ✅（引擎能同步，但**普通 API 枚举不到**）；**Office/WPS 锁文件未测**（WinServer-A 无 Office） |
| WinServer-A SSH 通道 | 26645 端口**长期被爆破**（`Exceeded MaxStartups` 间歇性打满），已落盘 `MaxStartups 100:30:200` / `LoginGraceTime 20` / `PasswordAuthentication no`；建议进一步收窄放行来源 IP |

### 2.3 未开始

| 项 | 说明 |
|---|---|
| **E2** ECS 并发-内存曲线 + 公网带宽上限 | 需在阿里云 ECS 上压测 |
| **E4** 真实公网 relay 的流量与成本回填 | 本机已验证 relay 路径可用（1.00× 口径），真机 relay 数据待补 |
| **E6** 五场景网络切换 P50/P95 | 需人为切网（Wi-Fi↔热点、有线↔Wi-Fi、休眠→唤醒、拔线→恢复、直连断→relay 接管） |
| **版本锁定表**（报告 §2.3） | 数字已有（见 §3），未回填进手册报告模板 |
| **Adapter 的 C# 实现** | 等 Codex 迭代 1 |

---

## 3. 硬事实（可直接引用，均实测）

### 3.1 版本锁定

| 项 | 值 |
|---|---|
| Syncthing | `v2.1.5 "Hafnium Hornet"`（go1.27.1） |
| Source tag / commit | `v2.1.5` / `2ca95cf1498104113fdfde46df4107f2450a0f71` |
| Windows 二进制 SHA-256 | `36a0f7bc372f64fa7cc4f5654fa324c0dd9f7fef2e07565e00c6e1cf73f50344`（与官方发行包逐位一致） |
| Linux 发行包 / 二进制 SHA-256 | `3d222b609f7ab2944e02748cb10488b4160d446b49e0eafc107ef2a525ab3486` / `ab0ea5f307101e5aa1b4c599164cfc2cc62bddcb8f53e1b3204fc77ac54ce07f` |
| 自建 `stdiscosrv` / `strelaysrv` | 同 tag 用 Go 1.27.1 编译，哈希见「本地实验结论 §1」 |
| 升级策略 | 阶段一不自动升级；升级前重跑「基础同步 + E3 + E5 + E6 + relay 回落」回归 |

### 3.2 上游能力边界（源码级核验，**别再按旧口径写**）

| 上游事实 | 结论 |
|---|---|
| relay 的设备级准入 | **不存在**。只有全局共享 `-token`（`cmd/strelaysrv/main.go`、`listener.go`）；Device ID 只用于身份与路由，**不是准入** |
| relay 的设备级计量 | **不存在**。`bytesProxied` 是包级全局，`/status` 零设备维度 |
| 官方 selective sync | **不存在**（只有 4 种 folder type）；替代方案＝**多 Folder 划分** |
| 块算法 | 定长块 128 KiB–16 MiB、**SHA-256**（不是 Rabin 1–8 MB / BLAKE3） |
| relay 可见内容 | **完全不可见**：文件内容、文件名、目录结构、folder ID、块哈希都看不到；可见 Device ID / IP / 时长 / 流量规模 |
| 直连与中继协议 | 直连 TCP + QUIC；relay 走 **TCP**（可映射 443 提高企业网通过率）。**"伪装 HTTPS"的说法已废止**——443 ≠ 穿透 DPI |
| 局域网发现 | 原生 Local Discovery 走 **UDP 广播/多播**（端口 21027），不是自研 mDNS |
| versioning | **默认关闭**；按设备按 Folder 配置；只归档"远端变更把本地替换/删除"的旧版本。阶段一采用 **Staggered，`maxAge = "30"`（字符串！）** |
| 冲突文件命名 | 原生 `<file>.sync-conflict-<date>-<time>-<device>.<ext>`，**磁盘上不改名**，只在 UI 翻译 |
| 恢复 API | `POST /rest/folder/versions` 只按相对路径恢复，**没有"另存"参数** |
| 重连间隔 | `reconnectionIntervalS` 默认 **20 秒**（下限 5 秒） |
| 两层准入（必须分清） | ① **同步成员准入** = 互加 Device ID + 共享 Folder（决定文件访问权）② **Relay 使用准入** = 共享 token（只管带宽）。**拿到 relay token ≠ 获得文件访问权** |

### 3.3 实测纠正的 7 条（旧手册写错的地方）

1. relay 的 `URI:` **默认就打印**（只有 `ID:` 行在 `-debug` 内）→ 取 ID 步骤已简化
2. `bytesProxied` **不是双向合计**：8 MiB 文件 → +8.02 MiB（**1.00×**）。成本 = `bytesProxied × 单价`，倍数来自**接收端数量**
3. 强制走 relay 需三件事：`relaysEnabled=true` + 去掉直连 listen + **主动重启**（已建立的连接不会因改地址而断）
4. **恢复必须 `pause(对端) → restore → resume`**：不暂停 = 对端 1–2 秒覆盖回去，而 API 返回成功
5. `versioning.params` 必须字符串 `{"maxAge":"30"}`，传数字 → 400
6. `stdiscosrv -version` 会报错（kong），查版本用 `-v`
7. `/rest/folder/versions` 返回体是**每文件一条错误的字典**，必须判空，不能只看 HTTP 200

### 3.4 真机发现（Windows + 真实跨网，Azure ↔ WinServer-A 两个不同公网 IP）

| 发现 | 内容 |
|---|---|
| 跨网同步 | ✅ 直连（`type=tcp-server`）传 33.8 MB，2000 文件 + 中文 + emoji + 重音 + 32 MiB 全部落地 |
| 感知时延 | **≈ 2–3 秒**（独立轮询法，默认 `fsWatcherDelayS=10`）→ PRD §18「3 秒默认不可达」**已修正为可达**，保留为产品目标 |
| 大小写碰撞 | Syncthing **识别并报错、拒绝覆盖**（不静默丢数据），但 **`needFiles` 永远=1，文件夹无法进入完全同步**，并会拖慢整个文件夹 → **UI 必须呈现 folder error**，Adapter 需能列错误 + 清理残留 `~syncthing~*.tmp` |
| >260 字符路径 | 引擎能同步（内部用长路径 API），但 `Get-ChildItem -Recurse` **看不到** → **Adapter/UI/报表必须用 `\\?\` 扩展路径** |
| Windows Service | **`sc create` 直接包 console 程序会启动失败**（SCM 协议）→ M1 直接复用 `Bill-Stewart/SyncthingWindowsSetup` 的服务化思路 |
| 会话隔离 | 裸进程跑 Syncthing **随 SSH 会话结束被杀** → 实证了"引擎必须作为 Windows Service"这条设计 |

---

## 4. 给人（交付给 Codex）的硬约束清单

违反即打回：

1. **不实现同步协议、不改块算法、不做授权/账号/数据库/管理后台/自研网络协议**。Adapter 只做"读写 + 转发"。
2. **不写托盘 UI**（等 E5/E6 数字），本迭代只做 Adapter。
3. 只动 `src/` `tests/`（`packaging/` `deploy/` 后续），**不要动 `docs/` `adapter/` `m05-toolkit/`**（避免同仓撞车）。
4. 集成测试**必须支持"外部提供实例"**：环境变量 `LANSW_TEST_A_GUI` / `LANSW_TEST_A_APIKEY` / `LANSW_TEST_B_GUI` / `LANSW_TEST_B_APIKEY`；未设置时自建临时实例（`LANSW_SYNCTHING_BIN`）；两者都不可用 → **skip，不许 fail**。
5. 磁盘上保留 Syncthing 原生冲突文件名，只在 UI 层翻译。
6. 忽略规则走原生 `.stignore` + `/rest/db/ignores`，UI 不暴露该文件名。
7. 版本恢复必须是事务：`pause(peer) → restore → 响应字典判空 → resume(peer)`，异常用 `finally` 兜底。
8. `versioning.params` 的值必须是字符串。
9. 不要引入 Docker Desktop；不要依赖 Python（过渡期可用它交叉验证）。**不使用现有 .NET 8.0.301 充当 SDK，必须装 .NET 10 SDK**（`net10.0` 无法由 8.x SDK 编译）。
10. 目标框架 .NET 10（LTS，EOL 2028-11-14）→ PRD 里"Windows 10/11"需注一句：**Win10 仅 LTSC/Enterprise 在支持列表内**（当前机队 Win11 Home + Server 2025 不受影响）。

---

## 5. 指令 A · 对齐（先发这一段，**不写代码**）

```text
项目：lan-sync（https://github.com/Ethan-hubs/lan-sync）。你是 C# 实现方；太极（另一个 agent，在 Linux 上）负责服务端、真机验证与文档。本轮**只做对齐，不写代码**。

必读文件（在仓库里）：
- docs/G1-Adapter接口约定.md  ← Adapter 的唯一规格（7 条职责 + 8 条上游事实 + 10 条实测注意点）
- docs/M0.5-本地实验结论（Linux子集）.md 和 docs/M0.5-真机实验结论（Windows+跨网）.md  ← 全部实测数字与 7 条纠正
- M0.5-技术基线PoC操作手册-v0.3.md
- 局域网同步系统-产品文档-v0.9.md 的 §2.4（两层准入）/§2.5（E1–E6）/§2.6（M1 准入 Gate）/§6.1.1（Adapter 职责）/§12.4（上游能力边界）
- adapter/syncthing_adapter.py（Python 参考实现）+ adapter/lab.py（C1–C9 检查定义）

必须知道的现状（别重复劳动）：
1) 版本锁定：Syncthing v2.1.5（commit 2ca95cf1498104113fdfde46df4107f2450a0f71），Windows 二进制 SHA-256 = 36a0f7bc372f64fa7cc4f5654fa324c0dd9f7fef2e07565e00c6e1cf73f50344。
2) Windows Server 2025 上已部署 Syncthing v2.1.5 到 D:\LanSync\m05（bin/home/sync 三目录），D:\m05stage\ 里有脚本：w_setup.ps1（部署+私有化+建文件夹）、w_verify.ps1（校验）、w_deep.ps1（深路径/队列/错误）、w_engine.ps1（装 Windows Service，sc create 方式已实测启动失败）、w_relay.ps1（私有 relay）、w_e5_run.ps1（E5 采集器）。
3) Azure 侧已有：本地双实例（GUI 8384/8385）、跨网实例 Z（GUI 8387）、自建 stdiscosrv/strelaysrv；Linux 上 9/9 契约检查全绿，错误 token 负例通过。
4) 真实跨网已跑通（WinServer-A ↔ Azure 两个不同公网 IP，type=tcp-server，33.8 MB 已同步）。

约束（违反会被打回）：
- 不实现同步协议、不改块算法、不做授权/账号/数据库/管理后台/自研网络协议；Adapter 只做"读写 + 转发"。
- 磁盘保留原生冲突文件名（.sync-conflict-*），只在 UI 层翻译。
- 忽略规则走原生 .stignore + /rest/db/ignores，UI 不暴露该文件名。
- versioning.params 的值必须是字符串：{"maxAge":"30"}，传数字会 400。
- 版本恢复必须是事务：pause(peer) → restore → 响应字典判空 → resume(peer)，异常用 finally 恢复。不能只看 HTTP 200。
- relaysEnabled=false 时 relay:// listen 地址完全不生效；改 listen 地址必须重启才生效。
- 不要引入 Docker Desktop；不要依赖 Python（过渡期可用于交叉验证）。
- 必须用 .NET 10 SDK（现有 .NET 8.0.301 无法 target net10.0）。

请只输出：① 你对上述规格的 10 行以内理解复盘；② 你发现的规格冲突或不清晰之处（如有）；③ 迭代 1 的落地计划（文件清单 + 测试清单）。不要提交代码。
```

---

## 6. 指令 B · 迭代 1 任务书（指令 A 确认后再发）

```text
迭代 1 目标：在 C# 中完整实现 Adapter 的前四项职责，并通过"双实例集成测试"。不写 UI。

交付：
1) 解决方案与工程结构（严格按此，勿增删顶层目录）：
   src/LanSync.Core（领域模型：DeviceId、FolderSpec、ConnectionKind、SyncState、VersionEntry）
   src/LanSync.Syncthing（REST 客户端 + Adapter 实现）
   tests/LanSync.Syncthing.Tests（单元测试，用 HttpMessageHandler 打桩）
   tests/LanSync.IntegrationTests（真实例契约测试）
2) Adapter 前四项职责：
   ① health：/rest/system/ping、/version、/status、/rest/config/restart-required
   ② 配置：GET→改→PUT 的安全更新（禁止整体覆盖丢字段）+ 需要重启时 POST /rest/system/restart 后重新等 ping
   ③ 设备/文件夹/忽略：/rest/config/devices[/:id]、/rest/config/folders[/:id]、/rest/db/ignores
   ④ 连接状态映射：tcp-*/quic-* → Direct，relay-* → Relay，未连接 → Offline（枚举就这三个 + Unknown）
3) 集成测试必须"外部提供实例"，用环境变量注入，不许硬编码端口：
   LANSW_TEST_A_GUI / LANSW_TEST_A_APIKEY / LANSW_TEST_B_GUI / LANSW_TEST_B_APIKEY
   未设置则自建两个临时实例（LANSW_SYNCTHING_BIN 指向二进制）；两者都不可用时 skip 而不是 fail。
   这条是硬要求：太极会在 Linux 上用真实实例跑你的测试做独立复核。
4) 测试覆盖 C1–C6（对齐 adapter/lab.py 的同名检查，编号别改）：
   C1 REST 可读配置、C2 连接类型可读、C3 双机同步、C4 events、C5 pause/resume、C6 ignores 经 REST 生效
5) 硬性验收：`dotnet test` 全绿（单元 + 集成），且集成测试在两个真实 Syncthing v2.1.5 实例上通过。

提交拆分（用这些 message）：
build: initialize .NET 10 solution
feat(adapter): add REST client and health checks
feat(adapter): add safe configuration updates
feat(adapter): add devices folders and ignores
feat(adapter): map direct relay and offline states
test(adapter): port C1-C6 contract checks

不要动 docs/、adapter/、m05-toolkit/（那些是太极的，避免同仓冲突）。不要写托盘 UI、不要写授权、不要引入数据库。
跑完把 `dotnet test` 的原始输出贴出来。
```

**顺手让 Codex 在 WinServer-A 本地跑（不用 SSH，绕开爆破窗口）**：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File D:\m05stage\w_engine.ps1   # 装 Windows Service（M1 必做项；已知 sc create 方式会失败，需包装器）
powershell -NoProfile -ExecutionPolicy Bypass -File D:\m05stage\w_verify.ps1   # 报告实例状态/连接类型/同步目录
```

---

## 7. 分工协议（避免同仓撞车）

| 谁 | 负责 | 目录 |
|---|---|---|
| **Codex** | C# 实现、测试、托盘、打包 | `src/` `tests/` `packaging/` `deploy/` |
| **太极** | ECS 服务端、真机跨网验证、G2 流程、文档与 Gate 状态、**独立复核 Codex 的代码** | `docs/` `m05-toolkit/` `adapter/`（Python 参考）+ 根文档 |

**独立复核怎么落地**：太极在 Linux 上有完整实验环境（A/B 双实例 + C 负例 + Z 跨网 + 自建 disco/relay，均在运行）。只要 Codex 的集成测试支持"外部提供实例"（环境变量传 GUI 地址 + API key），太极就能 `git clone` 下来在真实例上跑它的 C# 测试——这就是一轮真正的第三方验证。

---

## 8. 下一步（三条并行）

1. **Codex**：发指令 A → 确认 → 发指令 B（迭代 1）
2. **太极**：E5 干净成组跑（修好采集器 + 结果走同步通道回流）+ E6 五场景网络切换
3. **太极**：ECS 上起 `stdiscosrv` + 私有 `strelaysrv`（二进制已备好）→ 补 E2/E4 真机数字

跑完把数字回填手册 §12 → 冻结 **PRD v1.0** → 写托盘 UI（依赖 E5/E6 数字）。
