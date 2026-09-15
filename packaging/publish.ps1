#requires -Version 5.1
# LanSync 发布打包预置脚本（第三批，ADR-001 §14(7)）
# setup.iss 的 [Files] 取 dotnet publish -c Release -r win-x64 输出，不再引用源码树 bin\Release。
# 本脚本把托盘 publish 产物落到 packaging\bin\tray；syncthing.exe / shawl.exe 由发布流水线
# 单独放入 packaging\bin\（本脚本不下载、不校验来源，SHA-256 比对待 install.ps1 运行时执行）。

[CmdletBinding()]
param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$OutDir = (Join-Path $PSScriptRoot 'bin\tray')
)

$ErrorActionPreference = 'Stop'

$trayProj = Join-Path $RepoRoot 'src\LanSync.Tray\LanSync.Tray.csproj'
if (-not (Test-Path $trayProj)) {
    throw "未找到托盘工程：$trayProj"
}

Push-Location $RepoRoot
try {
    & dotnet publish $trayProj -c Release -r win-x64 --self-contained false -o $OutDir
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish 失败，退出码 $LASTEXITCODE"
    }
}
finally {
    Pop-Location
}

Write-Host "托盘 publish 产物已落地：$OutDir"
Write-Host '请确认 packaging\bin\syncthing.exe 与 packaging\bin\shawl.exe 已就位，再编译 setup.iss。'