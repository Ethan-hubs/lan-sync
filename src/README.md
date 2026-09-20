# C# Adapter 构建与测试

本目录的 `LanSync.Core` 与 `LanSync.Syncthing` 使用 .NET SDK **10.0.401**，目标框架为跨平台 `net10.0`。仓库根目录的 `global.json` 固定 SDK 版本；请先确认 `dotnet --list-sdks` 含 `10.0.401`。

阶段一对外支持面：Windows 11 全版本 + Windows Server 2016 及以上；Windows 10 未验证、不承诺（`net10.0-windows10.0.17763.0` 只表示不依赖更高版本 API，不代表支持承诺）。

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

预期结果：单元测试为 **N/N**（当前 **39/39**）；外部实例和自建实例模式为 **13/13**；显式清空全部 `LANSW_*` 后为 **2 passed / 11 skipped**。

## 阶段一 Folder 默认值

`FolderSpec.DefaultFsWatcherDelaySeconds` 为 **2 秒**，`AddFolderAsync` 会把它作为 `fsWatcherDelayS` 经 REST 写入 Folder 配置。调用方可通过 `FolderSpec.FsWatcherDelaySeconds` 覆盖，阶段一建议保持在 1～2 秒。

## 连接状态可观测事件

`SubscribeConnectionChangesAsync` 返回异步事件流。默认只有设备的可观察连接类型确实发生变化时才产生 `ConnectionKindChangedEvent`，事件包含 `DeviceId`、旧/新 `ConnectionKind`、UTC 时间戳和 `IsInitialSnapshot` 标记。阶段一状态灯映射为：`Direct`=绿、`Relay`=黄、`Offline`=红、`Paused`=灰；橙色只保留给阶段二授权异常，不由 Adapter 产生。

托盘启动时推荐直接调用 `SubscribeConnectionChangesAsync(emitInitialSnapshot: true)`，先消费 `IsInitialSnapshot=true` 的当前设备状态，再持续消费同一异步流的变化事件。初始快照与后续比较由同一个订阅状态机完成，因此没有“先单独读取快照、再建立订阅”之间的竞态窗口。`emitInitialSnapshot` 默认关闭，既有“只在类型确实变化时发事件”的语义不变。

## 对端暂停能力矩阵

| 调用条件 | 恢复行为 | 返回结果 | 产品风险 |
| --- | --- | --- | --- |
| 所有共享对端都有 Adapter，且原本均未暂停 | 本事务暂停全部对端，恢复后仅恢复本事务暂停的设备 | `Succeeded=true`、`PeerPaused=true`、`DurabilityVerified=true`，`UnpausedDevices` 为空 | 提供强保证，避免在线对端覆盖恢复结果 |
| 所有共享对端都有 Adapter，但部分原本已暂停 | 原暂停设备保持不动，其余由本事务暂停/恢复 | `Succeeded=true`、`PeerPaused=false`、`DurabilityVerified=true`；原暂停设备列入 `UnpausedDevices` | 仍有暂停保障，恢复结果经过本地索引校验 |
| 未提供 Adapter，或只提供部分对端 Adapter | 缺少通道的对端跳过暂停且不抛异常；已有通道仍按事务暂停/恢复 | `Succeeded=true`、`PeerPaused=false`、`DurabilityVerified=false`；缺少通道及原暂停设备列入 `UnpausedDevices` | 本地恢复请求完成，但在线对端可能覆盖结果或产生冲突副本；UI 必须明确提示 |

字段口径固定如下：`Succeeded` 只表示本地恢复请求成功且所选归档版本已被消费，不代表结果经得起在线对端覆盖；`DurabilityVerified` 表示所有共享对端均有暂停保障且恢复版本通过本地索引校验；`PeerPaused=true` 仅表示所有共享对端都由本事务成功暂停；`UnpausedDevices` 表示本事务未施加暂停的设备，包括无控制通道和原本已暂停的设备。

`RestoreVersionAsync` 的 `peerAdapters` 参数可省略。降级只针对“没有控制通道”的设备；如果已经拿到 Adapter 但暂停请求失败，恢复仍会失败并在 `finally` 中恢复本事务已经暂停的设备。
