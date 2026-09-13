<#
E5：接收端落地时间采集（手册 §9.2）。日志路径统一用变量，避免 v0.1 的硬编码问题。
用法：.\watch-fs-events.ps1 -Root C:\LanSync\m05\sync -Log C:\LanSync\m05\e5-events.csv
按 Ctrl-C 停止。
#>
param(
  [string]$Root = 'C:\LanSync\m05\sync',
  [string]$Log  = 'C:\LanSync\m05\e5-events.csv'
)

"utc_ms,event,name" | Set-Content $Log
$fsw = New-Object System.IO.FileSystemWatcher $Root
$fsw.IncludeSubdirectories = $true
$fsw.EnableRaisingEvents   = $true

$action = {
  $ms = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
  "$ms,$($Event.SourceEventArgs.ChangeType),$($Event.SourceEventArgs.Name)" | Add-Content $using:Log
}
foreach ($ev in 'Created','Changed','Renamed','Deleted') {
  Register-ObjectEvent $fsw $ev -Action $action | Out-Null
}
"正在监听 $Root -> $Log （Ctrl-C 停止）"
while ($true) { Start-Sleep -Seconds 1 }
