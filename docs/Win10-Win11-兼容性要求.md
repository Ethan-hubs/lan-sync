# Win10 / Win11 兼容性要求（阶段一硬约束）

> 背景：2026-09-13 项目发起人明确要求"测试在 Desktop-B 的 D 盘专用区进行，**要兼容 Win10 和 Win11**"。
> 本文把这条要求翻译成**可检查、可验收**的规则，供 C# 客户端（Codex 实现）、脚本工具与安装包共同遵守。
> 实测环境：Desktop-B = Windows 11 25H2（build 26200），WinServer-A = Windows Server 2025，
> **两台都只有 Windows PowerShell 5.1**（无 pwsh 7），**两台 `LongPathsEnabled` 都是 0**。

---

## 0. 一句话

**一份二进制、一套脚本，同时跑在 Win10 与 Win11 上**——目标是 `net10.0-windows` 且最低支持到 **Win10 1809（build 17763）**，
不使用任何 Win11 专有 UI/API，不用 PS7 专有语法，不假设长路径已开启。

## 1. 平台声明（要写进产品说明，别承诺做不到的）

| 平台 | 是否支持 | 依据 |
|---|---|---|
| Windows 11（21H2 ~ 26H1，含 Home/Pro） | ✅ 全版本 | .NET 10 官方支持列表 |
| Windows 10 **LTSC / Enterprise**（1809 / 21H2 等） | ✅ | .NET 10 官方支持列表仅列这两个通道 |
| Windows 10 **家用/专业版**（如 22H2） | ⚠️ **不承诺** | 该版本本身已 EOL，.NET 10 未列入支持范围 |
| Windows Server 2016 / 2019 / 2022 / 2025 | ✅ | .NET 10 官方支持列表 |

> 结论：产品说明写「**Windows 11 全版本 + Windows 10 LTSC/Enterprise（1809 起）+ Windows Server 2016 起**」，
> 不要笼统写"支持 Windows 10/11"。

## 2. 目标框架（一条容易致命的选择）

```xml
<TargetFramework>net10.0-windows</TargetFramework>
<SupportedOSPlatformVersion>10.0.17763.0</SupportedOSPlatformVersion>   <!-- Win10 1809 -->
<UseWPF>true</UseWPF>
```

- ❌ **不要**写 `net10.0-windows10.0.22000.0`（或更高）——那是 Win11 的目标框架，会把最低系统要求直接抬到 Win11。
- ❌ 不要用 `TargetPlatformMinVersion` 替代 `SupportedOSPlatformVersion`（前者不给编译期警告）。
- ✅ 需要调用较新 API 时，用 `[SupportedOSPlatform("windows10.0.xxxxx")]` 标注 + 运行时判断，别整体抬版本。

## 3. UI 技术（WPF 是唯一同时覆盖两代的稳妥选择）

| 允许 | 禁止（Win11 专有 / 会抬高门槛） |
|---|---|
| WPF（`System.Windows.*`） | WinUI 3 / Windows App SDK |
| 托盘：`NotifyIcon`（WinForms 互操作）或 WPF 等价实现 | Mica / Acrylic 背景（`DwmSetWindowAttribute` 的 Win11 专属值） |
| 标准 `MessageBox` / 自绘窗口 | Snap Layouts 悬停按钮、Win11 圆角与任务栏集成 API |
| DPAPI 保护 API key | 依赖 Win11 通知中心的 Toast 高级特性 |

> 托盘 + 配置型界面用 WPF 完全够；**不要为了"好看"引入 Windows App SDK**，那会同时抬高 OS 门槛与安装体积。

## 4. 脚本与工具（两台机器实测只有 PS 5.1）

- 只用 **Windows PowerShell 5.1** 语法：禁 `??`、三元 `? :`、`-Parallel`、`ForEach-Object -Parallel`、
  `ConvertFrom-Json -AsHashtable`（PS7 才有）等。
- **含非 ASCII 的 .ps1 必须带 UTF-8 BOM**（PS 5.1 否则按 ANSI/GBK 解析 → `字符串缺少终止符`，整脚本不执行）。
- 经 `cmd.exe` 的 `-EncodedCommand` 有 **8191 字符命令行上限** → 长脚本一律 **scp + `-File`**。
- 采集/计时脚本注意：`Register-ObjectEvent` 的处理器在主线程 `Start-Sleep` 期间**不会派发**，
  计时要用 `$Event.TimeGenerated` + 短睡循环（详见 `windows-server-admin` 技能）。

## 5. 文件系统（两台机器 `LongPathsEnabled=0`）

- 读/写/枚举**必须走扩展长度路径**：.NET 侧用 `\\?\` 前缀或 `app.manifest` 声明 `longPathAware`；
  C# 里配合 `AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false)`（框架默认已关）。
- **实测教训**：281 字符路径的文件 Syncthing **能同步**，但 `Get-ChildItem -Recurse` **看不到**——
  所以 Adapter 的"扫描/统计/展示"必须用长路径感知 API，否则会出现"文件在、界面不显示"。
- 大小写：NTFS 不敏感 → 仅大小写不同的两个文件无法共存；Syncthing 会报
  `uses different upper or lowercase characters than local` 并让文件夹长期 `needFiles=1`。
  UI 必须把这类 folder error 显式呈现 + 提供清理入口。

## 6. 服务化与安装（两代系统都要能装）

- 引擎必须由**服务包装器**托管（`sc create` 直接包 console 程序会 `CouldNotStartService`）：
  上游 Syncthing 生态用的是 **shawl**；亦可用 NSSM / WinSW。
- 服务账号非 SYSTEM 时：授予"Log on as a service"权限 + 同步目录 `Modify` 权限
  （`icacls "<dir>" /grant "<acct>:(OI)(CI)M" /t`）。
- 安装包：**Inno Setup 或 MSI**；安装/升级/卸载都要在 Win10 与 Win11 各跑一遍。
- 卸载**绝不删除用户同步目录**。

## 7. 测试矩阵与当前缺口（诚实标注）

| 环境 | 机器 | 状态 |
|---|---|---|
| Windows 11 25H2 | Desktop-B（台式机，Office 已装；测试区 `D:\LanSync\m05`） | ✅ 已接入 |
| Windows 11 25H2（家庭） | huawei（NoteBook-A D-16） | ⏳ 未接入 |
| Windows Server 2025 | WinServer-A（`D:\LanSync\m05`） | ✅ 已接入（无 Office） |
| **Windows 10**（LTSC/Enterprise 或 22H2） | **机队内暂无** | ❌ **缺真机** |

> **当前 Win10 兼容性靠"规范约束 + 静态检查"保证，没有 Win10 真机验证过**——
> 这条必须写在风险清单里。补法二选一：① 找一台 Win10（虚拟机也行）跑一遍安装+同步；
> ② 至少做一次"目标框架/API/脚本语法"的静态审查（见 §8）。

## 8. 可自动化检查的清单（建议接进 CI，Codex 迭代 1 就能加）

```bash
# ① 目标框架与最低 OS（防"抬门槛"）
grep -rn "TargetFramework" --include=*.csproj . | grep -v "net10.0-windows$" && echo "FAIL: 目标框架不是 net10.0-windows"
grep -rn "SupportedOSPlatformVersion" --include=*.csproj . | grep -v "10.0.17763.0" && echo "FAIL: 最低 OS 不是 Win10 1809"

# ② Win11 专有依赖（防误引入）
grep -rniE "WindowsAppSDK|Microsoft\.Windows\.AppSDK|Mica|SnapLayout" --include=*.csproj --include=*.cs . && echo "FAIL: 引入 Win11 专有组件"

# ③ PS7 专有语法（脚本目录）
grep -rnE '\?\?|\? *\$|\| *ForEach-Object *-Parallel|-AsHashtable' --include=*.ps1 . && echo "FAIL: PS7 专有语法"

# ④ 脚本 BOM（含中文的 .ps1 必须带 BOM）
for f in $(grep -rlP '[^\x00-\x7F]' --include=*.ps1 .); do
  head -c3 "$f" | od -An -tx1 | grep -q "ef bb bf" || echo "FAIL: $f 缺 UTF-8 BOM"
done
```
