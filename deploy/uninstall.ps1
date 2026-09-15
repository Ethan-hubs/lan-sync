#requires -Version 5.1
# LanSync 卸载脚本（阶段一骨架）
# 依据：ADR-001 §9（卸载禁用不删除账号）、§10（卸载绝不删除用户同步目录）
# 仅停服务、删服务、禁账号、清理程序与配置；绝不触碰任何用户同步目录。

[CmdletBinding()]
param(
    [string]$ProgramDataDir = "$env:ProgramData\LanSync"
)

$ErrorActionPreference = 'Stop'

$ServiceName    = 'LanSyncEngine'
$ServiceAccount = 'LanSyncSvc'
$InstallDir     = $PSScriptRoot
$LogDir         = Join-Path $ProgramDataDir 'logs'
$LogFile        = Join-Path $LogDir 'uninstall.log'
$ShawlExe       = Join-Path $InstallDir 'bin\shawl.exe'

function Write-Step {
    param([string]$Message)
    $line = ('[{0}] {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Message)
    Write-Host $line
    Add-Content -Path $LogFile -Value $line -Encoding UTF8
}

function Stop-EngineService {
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($null -ne $svc -and $svc.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force
        Write-Step "已停止服务 $ServiceName"
    }
}

function Remove-EngineService {
    # 优先用 shawl 删除；shawl 不在则回退 sc.exe delete。
    if (Test-Path $ShawlExe) {
        & $ShawlExe remove $ServiceName
    }
    else {
        & sc.exe delete $ServiceName | Out-Null
    }
    Write-Step "已删除服务 $ServiceName"
}

function Disable-ServiceAccount {
    # §9 ③：卸载时禁用不删除账号。
    $user = Get-LocalUser -Name $ServiceAccount -ErrorAction SilentlyContinue
    if ($null -ne $user) {
        Disable-LocalUser -Name $ServiceAccount
        Write-Step "已禁用账号 $ServiceAccount（保留不删除）"
    }
}

function Remove-ProgramData {
    # 清理程序配置（api-key.txt、engine home）；日志目录保留以便排障。
    $apiKey = Join-Path $ProgramDataDir 'api-key.txt'
    $engine = Join-Path $ProgramDataDir 'engine'
    if (Test-Path $apiKey) { Remove-Item -Path $apiKey -Force }
    if (Test-Path $engine)  { Remove-Item -Path $engine -Recurse -Force }
    Write-Step '已清理程序配置（API key / engine home）'
}

try {
    New-Item -Path $LogDir -ItemType Directory -Force | Out-Null
    Write-Step '===== LanSync 卸载开始 ====='

    Stop-EngineService
    Remove-EngineService
    Disable-ServiceAccount
    Remove-ProgramData

    Write-Step '===== LanSync 卸载完成（未触碰任何用户同步目录）====='
}
catch {
    Write-Step ("卸载失败：{0}" -f $_.Exception.Message)
    exit 1
}