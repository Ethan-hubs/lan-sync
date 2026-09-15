<#
E6：250ms 采样连接状态（手册 §10.1）。人工制造网络切换场景时跑着它。
用法：.\watch-connections.ps1 -Device <REMOTE_DEVICE_ID> -Out C:\LanSync\m05\e6-connection-log.csv
#>
param(
  [Parameter(Mandatory = $true)][string]$Device,
  [string]$ApiKeyFile = 'C:\LanSync\m05\api-key.txt',
  [string]$Gui = 'http://127.0.0.1:8384',
  [string]$Out = 'C:\LanSync\m05\e6-connection-log.csv'
)

$H = @{ 'X-API-Key' = (Get-Content $ApiKeyFile -Raw).Trim() }
"utc_ms,connected,type,address" | Set-Content $Out
"采样中 -> $Out （Ctrl-C 停止）"

while ($true) {
  try {
    $r = Invoke-RestMethod -Headers $H "$Gui/rest/system/connections"
    $c = $r.connections.$Device
    if ($c) {
      $ms = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
      "$ms,$($c.connected),$($c.type),$($c.address)" | Add-Content $Out
    }
  } catch {}
  Start-Sleep -Milliseconds 250
}
