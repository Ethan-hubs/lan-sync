#requires -Version 5.1
# LanSync 引擎部署脚本（阶段一骨架）
# 依据：ADR-001 §5（密钥位置）、§9（服务化）、§10（安装步骤）
# 服务名 LanSyncEngine / 专用账号 LanSyncSvc（非 SYSTEM、非交互登录）/ shawl 包装
# 由 Inno Setup 安装器在提权上下文调用；产物与失败信息写 %ProgramData%\LanSync\logs\install.log

[CmdletBinding()]
param(
    [string]$SyncDir = '',
    [string]$ProgramDataDir = "$env:ProgramData\LanSync"
)

$ErrorActionPreference = 'Stop'

$ServiceName     = 'LanSyncEngine'
$ServiceAccount  = 'LanSyncSvc'
$EngineHome      = Join-Path $ProgramDataDir 'engine'
$ApiKeyFile      = Join-Path $ProgramDataDir 'api-key.txt'
$LogDir          = Join-Path $ProgramDataDir 'logs'
$LogFile         = Join-Path $LogDir 'install.log'
$SyncthingSha256 = '36a0f7bc372f64fa7cc4f5654fa324c0dd9f7fef2e07565e00c6e1cf73f50344'

# 本脚本由安装器放到 {app} 下运行，安装目录用 $PSScriptRoot 推断。
$InstallDir    = $PSScriptRoot
$SyncthingExe  = Join-Path $InstallDir 'bin\syncthing.exe'
$ShawlExe      = Join-Path $InstallDir 'bin\shawl.exe'

function Write-Step {
    param([string]$Message)
    $line = ('[{0}] {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Message)
    Write-Host $line
    Add-Content -Path $LogFile -Value $line -Encoding UTF8
}

function Test-IsAdmin {
    $identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Resolve-SyncDir {
    param([string]$Candidate)
    if ([string]::IsNullOrWhiteSpace($Candidate)) {
        return (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'LanSync')
    }
    return $Candidate.Trim()
}

# §9 依赖安装：.NET Desktop Runtime 检测（托盘 exe 需要）；缺失时提示并提供离线包路径。
function Test-DotnetDesktopRuntime {
    # 简化探测：注册表 NDP 只覆盖 .NET Framework 4.x，.NET 10 Desktop Runtime 的精确检测待补。
    Write-Step '依赖检测：.NET Desktop Runtime 检测为骨架占位，需按实际版本补齐。'
}

function Assert-SyncthingHash {
    param([string]$Path)
    $actual = (Get-FileHash -Path $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    $expect = $SyncthingSha256.ToLowerInvariant()
    if ($actual -ne $expect) {
        throw "syncthing.exe SHA-256 不匹配：期望 $expect，实际 $actual"
    }
    Write-Step "syncthing.exe SHA-256 校验通过：$actual"
}

function New-ServiceAccount {
    # 专用本地账号（非 SYSTEM、非交互登录）。密码随机生成，卸载时只禁用不删除。
    $exists = Get-LocalUser -Name $ServiceAccount -ErrorAction SilentlyContinue
    if ($null -eq $exists) {
        $password = ([Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N'))
        New-LocalUser -Name $ServiceAccount -Password (ConvertTo-SecureString $password -AsPlainText -Force) `
            -Description 'LanSync 引擎专用服务账号' -PasswordNeverExpires | Out-Null
        Write-Step "已创建本地账号 $ServiceAccount"
    }
    else {
        Write-Step "本地账号 $ServiceAccount 已存在，跳过创建"
    }
}

function Grant-LogonAsService {
    # 授予「作为服务登录」权限。骨架先经 secedit 导入；也可用 ntrights.exe / LsaAddAccountRights。
    Write-Step "授予 $ServiceAccount 「作为服务登录」为骨架占位（secedit / ntrights，待接入）。"
}

function Register-Service {
    # shawl 包装 syncthing.exe；服务账号切到 LanSyncSvc（下一条 TODO）。
    & $ShawlExe add $ServiceName -- $SyncthingExe serve --home $EngineHome --no-browser --no-restart
    if ($LASTEXITCODE -ne 0) {
        throw "shawl 注册服务失败，退出码 $LASTEXITCODE"
    }
    # TODO：sc.exe config LanSyncEngine obj= ".\LanSyncSvc" password= ... 切服务账号。
    Write-Step "已用 shawl 注册服务 $ServiceName"
}

function New-ApiKey {
    # API key 写 %ProgramData%\LanSync\api-key.txt；ACL 按 §5。
    if (-not (Test-Path $ApiKeyFile)) {
        $key = ([Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N'))
        Set-Content -Path $ApiKeyFile -Value $key -Encoding ASCII -NoNewline
    }
    Write-Step "API key 已就绪：$ApiKeyFile"
    # TODO：icacls 设置 ACL（SYSTEM/Administrators/LanSyncSvc 完全控制，Users 只读）。
}

function Set-SyncDirAcl {
    param([string]$Dir)
    # §9 ②：同步目录给账号 Modify 权限。
    & icacls $Dir /grant "${ServiceAccount}:(OI)(CI)M" /t | Out-Null
    Write-Step "同步目录 ACL 已授权（$Dir -> $ServiceAccount Modify）"
}

function Start-EngineService {
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($null -eq $svc) {
        throw "未找到服务 $ServiceName"
    }
    if ($svc.Status -ne 'Running') {
        Start-Service -Name $ServiceName
    }
    Write-Step "服务 $ServiceName 已启动"
}

function Enable-Autostart {
    # 托盘开机自启：HKCU Run。
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    New-Item -Path $runKey -Force | Out-Null
    $trayExe = Join-Path $InstallDir 'LanSync.Tray.exe'
    Set-ItemProperty -Path $runKey -Name 'LanSync' -Value ('"{0}"' -f $trayExe)
    Write-Step '已写入 HKCU Run 开机自启'
}

# ===== 主流程（按 ADR §10 步骤 1–5）=====
try {
    New-Item -Path $LogDir -ItemType Directory -Force | Out-Null
    New-Item -Path $EngineHome -ItemType Directory -Force | Out-Null
    Write-Step '===== LanSync 安装开始 ====='

    if (-not (Test-IsAdmin)) {
        throw '需要管理员权限运行本脚本。'
    }

    $SyncDir = Resolve-SyncDir $SyncDir

    Test-DotnetDesktopRuntime
    Assert-SyncthingHash $SyncthingExe
    New-ServiceAccount
    Grant-LogonAsService
    Register-Service
    New-ApiKey
    Set-SyncDirAcl $SyncDir
    Start-EngineService
    Enable-Autostart

    Write-Step '===== LanSync 安装完成 ====='
}
catch {
    Write-Step ("安装失败：{0}" -f $_.Exception.Message)
    Write-Host ("安装失败：{0}" -f $_.Exception.Message)
    exit 1
}