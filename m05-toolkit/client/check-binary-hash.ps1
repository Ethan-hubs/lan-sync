<#
检查四台机器的 syncthing.exe 与 ZIP 是否完全一致（手册 §2.1 / §0.5 P6）。
用法：把官方 ZIP 解压到 C:\LanSync\m05\，在每台机器上跑本脚本，比对输出。
#>
param(
  [string]$BinDir = 'C:\LanSync\m05\bin',
  [string]$ZipPath = 'C:\LanSync\m05\syncthing-windows-amd64-v2.1.5.zip'
)

$exe = Join-Path $BinDir 'syncthing.exe'
if (-not (Test-Path $exe)) { Write-Error "找不到 $exe"; exit 1 }

"== syncthing.exe --version =="
& $exe --version

"`n== SHA-256 =="
[pscustomobject]@{
  Host       = $env:COMPUTERNAME
  ExeSha256  = (Get-FileHash $exe -Algorithm SHA256).Hash
  ZipSha256  = if (Test-Path $ZipPath) { (Get-FileHash $ZipPath -Algorithm SHA256).Hash } else { '(未提供 ZIP)' }
} | Format-List

"提示：四台的 ExeSha256 必须逐字符一致。不一致说明不是同一份产物 —— 改用手册 §0.5 P6 的『一台下载、复制到四台』。"
