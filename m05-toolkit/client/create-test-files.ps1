<#
E5：源端造数并记录源时间（手册 §9.2）。
create 用例的文件名里带 UTC 毫秒；modify/rename/delete 需要源端事件表，用 -SourceLog 记录。
用法：
  .\create-test-files.ps1 -Op create -Count 30
  .\create-test-files.ps1 -Op modify -Count 30
#>
param(
  [ValidateSet('create','modify','rename','delete')][string]$Op = 'create',
  [int]$Count = 30,
  [string]$Root = 'C:\LanSync\m05\sync',
  [string]$SourceLog = 'C:\LanSync\m05\source-events.csv'
)

if (-not (Test-Path $SourceLog)) { "utc_ms,op,name" | Set-Content $SourceLog }

for ($i = 1; $i -le $Count; $i++) {
  $ts = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
  switch ($Op) {
    'create' {
      $name = "m05-create-$ts.txt"
      Set-Content (Join-Path $Root $name) $ts
    }
    'modify' {
      $name = "m05-mod-$ts.txt"
      Add-Content (Join-Path $Root $name) "edit $ts"
    }
    'rename' {
      $src = Join-Path $Root "m05-ren-$ts.txt"; Set-Content $src $ts
      $dst = "m05-ren2-$ts.txt"; Rename-Item $src $dst
      $name = $dst
    }
    'delete' {
      $name = "m05-del-$ts.txt"; Set-Content (Join-Path $Root $name) $ts
      Start-Sleep -Milliseconds 200; Remove-Item (Join-Path $Root $name)
    }
  }
  "$ts,$Op,$name" | Add-Content $SourceLog
  Start-Sleep -Milliseconds 300      # 拉开间隔，避免批量聚合干扰测量
  Write-Progress -Activity "E5 $Op" -Status "$i/$Count" -PercentComplete ($i * 100 / $Count)
}
"完成：$Count 次 $Op。源事件表：$SourceLog"
"配对方法：用接收端 e5-events.csv 的 utc_ms 减去源端同一 name 的 utc_ms 即感知时延。"
