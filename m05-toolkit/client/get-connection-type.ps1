<#
读连接类型与字节数（手册 §4.1 / B4 / E4-A）。
type 取值已核对（官方 REST 文档）：tcp-client tcp-server quic-client quic-server relay-client relay-server
#>
param(
  [string]$ApiKeyFile = 'C:\LanSync\m05\api-key.txt',
  [string]$Gui = 'http://127.0.0.1:8384'
)

$H = @{ 'X-API-Key' = (Get-Content $ApiKeyFile -Raw).Trim() }
$r = Invoke-RestMethod -Headers $H "$Gui/rest/system/connections"

$r.connections.PSObject.Properties | ForEach-Object {
  [pscustomobject]@{
    Device    = $_.Name
    Connected = $_.Value.connected
    Type      = $_.Value.type
    Address   = $_.Value.address
    InBytes   = $_.Value.inBytesTotal
    OutBytes  = $_.Value.outBytesTotal      # 成本口径用这个（出网）
  }
} | Sort-Object Type | Format-Table -AutoSize

"提示：E4-A 里按 Type 分组统计 direct（tcp-*/quic-*）与 relay（relay-*）即可得观测直连率 p。"
