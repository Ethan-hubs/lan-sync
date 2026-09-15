# C# Adapter 构建与测试

本目录的 `LanSync.Core` 与 `LanSync.Syncthing` 使用 .NET SDK **10.0.401**，目标框架为跨平台 `net10.0`。仓库根目录的 `global.json` 固定 SDK 版本；请先确认 `dotnet --list-sdks` 含 `10.0.401`。

集成测试支持两种真实 Syncthing v2.1.5 实例来源：

- 外部实例：同时设置 `LANSW_TEST_A_GUI`、`LANSW_TEST_A_APIKEY`、`LANSW_TEST_B_GUI`、`LANSW_TEST_B_APIKEY`。
- 自建实例：设置 `LANSW_SYNCTHING_BIN` 指向 v2.1.5 二进制；Windows/Linux 官方哈希按操作系统校验，也可用 `LANSW_SYNCTHING_SHA256` 覆盖期望哈希。

四个外部实例变量必须全部设置，否则测试会明确报配置错误。外部实例和自建实例均使用随机 Folder ID 与专用临时目录；测试结束会删除测试 Folder，且仅删除夹具自己新增的设备条目。未设置任何实例变量时，真实契约测试以 skip 结束，不判红；仅哈希选择单测仍会通过。

```powershell
$DotNet10 = 'D:\Tools\dotnet10\dotnet.exe'
& $DotNet10 --version       # 应输出 10.0.401
& $DotNet10 test LanSync.sln
& $DotNet10 format LanSync.sln --verify-no-changes --no-restore
```

预期结果：单元测试为 **N/N**（当前 **28/28**，含非法连接键测试）；外部实例和自建实例模式为 **11/11**；显式清空全部 `LANSW_*` 后为 **2 passed / 9 skipped**。
