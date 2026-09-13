<#
E4-B：强制走 relay。
v0.2 首选做法（比防火墙干净）：临时把 Sync Protocol Listen Addresses 里的直连项去掉，
只保留 relay URI；测完用 -Action Restore 还原。

用法：
  .\force-relay.ps1 -Action Force      # 备份原 listen 地址 -> 去掉 tcp/quic 直连项
  .\force-relay.ps1 -Action Restore    # 从备份还原
  .\force-relay.ps1 -Action Show       # 只看当前值
#>
param(
  [ValidateSet('Force','Restore','Show')][string]$Action = 'Show',
  [string]$ApiKeyFile = 'C:\LanSync\m05\api-key.txt',
  [string]$Gui = 'http://127.0.0.1:8384',
  [string]$BackupFile = 'C:\LanSync\m05\listen-addresses.bak'
)

$H = @{ 'X-API-Key' = (Get-Content $ApiKeyFile -Raw).Trim() }
$cfg = Invoke-RestMethod -Headers $H "$Gui/rest/config/options"
$current = @($cfg.listenAddresses)

switch ($Action) {
  'Show' { $current | ForEach-Object { $_ } }

  'Force' {
    $current | Set-Content $BackupFile
    $keep = $current | Where-Object { $_ -notmatch '^(tcp|quic)://' }
    if ($keep.Count -eq 0) { Write-Error "去掉直连项后没有剩下任何 listen 地址，请确认配置里已有 relay:// 条目"; exit 1 }
    $cfg.listenAddresses = $keep
    Invoke-RestMethod -Method Put -Headers $H -ContentType 'application/json' -Body ($cfg | ConvertTo-Json -Depth 20) "$Gui/rest/config/options"
    "已改为："; $keep | ForEach-Object { "  $_" }
    "确认 /rest/system/connections 的 type 变成 relay-* 后再开始传输。测完务必执行 -Action Restore。"
  }

  'Restore' {
    if (-not (Test-Path $BackupFile)) { Write-Error "没有备份文件 $BackupFile"; exit 1 }
    $cfg.listenAddresses = @(Get-Content $BackupFile)
    Invoke-RestMethod -Method Put -Headers $H -ContentType 'application/json' -Body ($cfg | ConvertTo-Json -Depth 20) "$Gui/rest/config/options"
    "已还原："; $cfg.listenAddresses | ForEach-Object { "  $_" }
    "备选方案（必须验证网络层真的不通时）：防火墙 Block 22000/TCP+UDP，用后立即 Remove-NetFirewallRule。"
  }
}
