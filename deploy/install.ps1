#requires -Version 5.1
# LanSync 引擎部署脚本（阶段一骨架，第三批收口）
# 依据：ADR-001 §5（密钥位置）、§9（服务化）、§10（安装步骤）、§14（第三批要求）
# 服务名 LanSyncEngine / 虚拟服务账号 NT SERVICE\LanSyncEngine（无口令、无需登录权限）/ shawl 包装
# 由 Inno Setup 安装器在提权上下文调用；产物与失败信息写 %ProgramData%\LanSync\logs\install.log
# 注：开机自启不在本脚本写 HKCU\Run（提权上下文会落到管理员配置单元），仅由用户在托盘菜单显式启用。

[CmdletBinding()]
param(
    [string]$SyncDir = '',
    [string]$ProgramDataDir = "$env:ProgramData\LanSync"
)

$ErrorActionPreference = 'Stop'

$ServiceName     = 'LanSyncEngine'
$EngineHome      = Join-Path $ProgramDataDir 'engine'
$ApiKeyFile      = Join-Path $ProgramDataDir 'api-key.txt'
$LogDir          = Join-Path $ProgramDataDir 'logs'
$LogFile         = Join-Path $LogDir 'install.log'
$SyncthingSha256 = '36a0f7bc372f64fa7cc4f5654fa324c0dd9f7fef2e07565e00c6e1cf73f50344'

# 本脚本由安装器放到 {app} 下运行，安装目录用 $PSScriptRoot 推断。
$InstallDir    = $PSScriptRoot
$SyncthingExe  = Join-Path $InstallDir 'bin\syncthing.exe'
$ShawlExe      = Join-Path $InstallDir 'bin\shawl.exe'

# 虚拟服务账号：SCM 按服务名自动派生，无需口令、无需「作为服务登录」、卸载删服务即回收。
$ServiceIdentity = 'NT SERVICE\{0}' -f $ServiceName

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

# §14(3)：优先查询 PATH 中的 dotnet；再扫描系统级与用户级常见安装位置。
function Test-DotnetDesktopRuntime {
    $dotnetCommand = Get-Command 'dotnet.exe' -ErrorAction SilentlyContinue
    if ($null -eq $dotnetCommand) {
        $dotnetCommand = Get-Command 'dotnet' -ErrorAction SilentlyContinue
    }

    if ($null -ne $dotnetCommand) {
        $runtimeLines = @(& $dotnetCommand.Source --list-runtimes 2>$null)
        $desktopRuntimes = @($runtimeLines | Where-Object {
            $_ -match '^Microsoft\.WindowsDesktop\.App\s+10\.'
        })
        if ($desktopRuntimes.Count -gt 0) {
            Write-Step ("通过 dotnet --list-runtimes 检测到 .NET Desktop Runtime：{0}" -f ($desktopRuntimes -join '; '))
            return
        }
    }

    $dotnetRoots = @()
    if (-not [string]::IsNullOrWhiteSpace($env:ProgramFiles)) {
        $dotnetRoots += (Join-Path $env:ProgramFiles 'dotnet')
    }
    if (-not [string]::IsNullOrWhiteSpace(${env:ProgramFiles(x86)})) {
        $dotnetRoots += (Join-Path ${env:ProgramFiles(x86)} 'dotnet')
    }
    if (-not [string]::IsNullOrWhiteSpace($env:USERPROFILE)) {
        $dotnetRoots += (Join-Path $env:USERPROFILE '.dotnet')
    }

    $found = @()
    foreach ($dotnetRoot in @($dotnetRoots | Select-Object -Unique)) {
        $desktopRoot = Join-Path $dotnetRoot 'shared\Microsoft.WindowsDesktop.App'
        if (Test-Path -LiteralPath $desktopRoot) {
            $found += @(Get-ChildItem -LiteralPath $desktopRoot -Directory -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -like '10.*' } |
                ForEach-Object { '{0} ({1})' -f $_.Name, $_.FullName })
        }
    }

    if ($found.Count -gt 0) {
        Write-Step ("通过安装目录检测到 .NET Desktop Runtime：{0}" -f ($found -join '; '))
        return
    }

    # 缺失：给出离线包路径并要求确认（阻断，不静默继续）。
    throw '未检测到 .NET Desktop Runtime 10.x。请先安装对应离线包（离线包路径待发布流水线提供，TODO）。'
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

function Register-Service {
    # shawl 包装 syncthing.exe；随后把服务账号切到虚拟服务账号 NT SERVICE\LanSyncEngine。
    & $ShawlExe add $ServiceName -- $SyncthingExe serve --home $EngineHome --no-browser --no-restart
    if ($LASTEXITCODE -ne 0) {
        throw "shawl 注册服务失败，退出码 $LASTEXITCODE"
    }
    & sc.exe config $ServiceName obj= ('NT SERVICE\{0}' -f $ServiceName) | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "sc.exe 配置服务账号失败，退出码 $LASTEXITCODE"
    }
    Write-Step "已用 shawl 注册服务 $ServiceName，账号 $ServiceIdentity"
}

function New-ApiKey {
    # API key 写 %ProgramData%\LanSync\api-key.txt；ACL 按 §5/§14(2)。
    if (-not (Test-Path $ApiKeyFile)) {
        $key = ([Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N'))
        Set-Content -Path $ApiKeyFile -Value $key -Encoding ASCII -NoNewline
    }
    # ACL：SYSTEM/Administrators 完全控制、NT SERVICE\LanSyncEngine 读、本机 Users 只读。
    & icacls $ApiKeyFile /inheritance:r | Out-Null
    & icacls $ApiKeyFile /grant 'SYSTEM:(F)' 'BUILTIN\Administrators:(F)' "${ServiceIdentity}:(R)" 'BUILTIN\Users:(R)' | Out-Null
    Write-Step "API key 已就绪并设置 ACL：$ApiKeyFile"
}

function Set-SyncDirAcl {
    param([string]$Dir)
    # §9 ②：同步目录给虚拟服务账号 Modify 权限。
    & icacls $Dir /grant "${ServiceIdentity}:(OI)(CI)M" /t | Out-Null
    Write-Step "同步目录 ACL 已授权（$Dir -> $ServiceIdentity Modify）"
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

# ===== 主流程（按 ADR §10 步骤 1–5，§14 收口）=====
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
    Register-Service
    New-ApiKey
    Set-SyncDirAcl $SyncDir
    Start-EngineService

    Write-Step '===== LanSync 安装完成 ====='
}
catch {
    Write-Step ("安装失败：{0}" -f $_.Exception.Message)
    Write-Host ("安装失败：{0}" -f $_.Exception.Message)
    exit 1
}
