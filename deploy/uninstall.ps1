#requires -Version 5.1
# LanSync 卸载脚本（阶段一骨架，第三批收口）
# 依据：ADR-001 §9（卸载删服务即回收虚拟账号）、§10（卸载绝不删除用户同步目录）、§14(6)（卸载保身份）
# 停服务、删服务、清理程序配置；默认保留引擎身份两件套（cert.pem/key.pem，Device ID 稳定），
# /PURGE 开关可彻底清除。绝不触碰任何用户同步目录。

[CmdletBinding()]
param(
    [switch]$Purge,
    [string]$ProgramDataDir = "$env:ProgramData\LanSync"
)

$ErrorActionPreference = 'Stop'

$ServiceName    = 'LanSyncEngine'
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
    # 优先用 shawl 删除；shawl 不在则回退 sc.exe delete。虚拟账号随服务删除自动回收。
    if (Test-Path $ShawlExe) {
        & $ShawlExe remove $ServiceName
    }
    else {
        & sc.exe delete $ServiceName | Out-Null
    }
    Write-Step "已删除服务 $ServiceName"
}

function Remove-ProgramData {
    param([bool]$Purge)
    $apiKey = Join-Path $ProgramDataDir 'api-key.txt'
    $engine = Join-Path $ProgramDataDir 'engine'

    if (Test-Path $apiKey) {
        Remove-Item -Path $apiKey -Force
        Write-Step '已删除 api-key.txt'
    }

    if (Test-Path $engine) {
        if ($Purge) {
            Remove-Item -Path $engine -Recurse -Force
            Write-Step '已彻底清除 engine 目录（含身份两件套 cert.pem/key.pem）'
        }
        else {
            # 默认保留 cert.pem + key.pem（Device ID 稳定），删其余（含 config.xml，folder 定义重装后重建）。
            $keep = @('cert.pem', 'key.pem')
            Get-ChildItem -Path $engine -Force |
                Where-Object { $keep -notcontains $_.Name } |
                Remove-Item -Recurse -Force
            Write-Step '已清理 engine 配置（保留身份两件套 cert.pem/key.pem）'
        }
    }
}

try {
    New-Item -Path $LogDir -ItemType Directory -Force | Out-Null
    Write-Step '===== LanSync 卸载开始 ====='

    Stop-EngineService
    Remove-EngineService
    Remove-ProgramData $Purge

    Write-Step '===== LanSync 卸载完成（未触碰任何用户同步目录）====='
}
catch {
    Write-Step ("卸载失败：{0}" -f $_.Exception.Message)
    exit 1
}