#requires -Version 5.1
<#
    Revert-WpsAutostartLockdown.ps1   ——  恢复 WPS 原装状态
    =====================================================================
    把本方案做的所有改动逐条还原，让 WPS 回到"从未被干预过"的样子：

      R1 结束所有 WPS 进程（否则文件被占用，改不动）
      R2 删除全部 DENY ACE（5 个载荷文件 + 2 个目录）
      R3 把守卫壳换回原版 ksolaunch.exe（ksolaunch.real.exe 重命名回来）
      R4 用备份 XML 重新注册两个计划任务
      R5 wpscloudsvr 服务恢复为「手动」（需要管理员，做不到会提示）
      R6 逐项核对：哈希 == 原始、无 DENY、任务在册、服务手动

    用法：
        .\Revert-WpsAutostartLockdown.ps1              # 真还原
        .\Revert-WpsAutostartLockdown.ps1 -VerifyOnly  # 只看状态，不动手

    注意：还原后 WPS 的所有行为（自动更新、重建自启任务、开机自启）
          都会回到原样。这是"原装"的定义。
#>
[CmdletBinding()]
param(
    [switch]$VerifyOnly,
    [string]$User = "$env:USERDOMAIN\$env:USERNAME"
)

$ErrorActionPreference = 'Continue'
$Base = Split-Path -Parent $MyInvocation.MyCommand.Path
$Stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$Log  = Join-Path $Base ("revert-$Stamp.log")
$script:Fail = 0

function Say {
    param([string]$M, [string]$L = 'INFO')
    $c = @{ OK = 'Green'; WARN = 'Yellow'; ERR = 'Red'; STEP = 'Cyan'; INFO = 'Gray' }[$L]
    if (-not $c) { $c = 'Gray' }
    $line = "[{0}] [{1}] {2}" -f (Get-Date -Format 'HH:mm:ss'), $L, $M
    Write-Host $line -ForegroundColor $c
    Add-Content $Log $line -Encoding UTF8
}
function Step($t) { Write-Host ""; Say ("=" * 6 + " " + $t) 'STEP' }

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
            [Security.Principal.WindowsBuiltInRole]::Administrator)

# ── 定位 ────────────────────────────────────────────────────────
$WpsRoot = Join-Path $env:LOCALAPPDATA 'Kingsoft\WPS Office'
$CloudUp = Join-Path $env:LOCALAPPDATA 'Kingsoft\WPS Cloud Files\update'
$VerDir  = Get-ChildItem $WpsRoot -Directory | Where-Object { $_.Name -match '^\d+(\.\d+)+$' } |
           Sort-Object Name -Descending | Select-Object -First 1
$Office6 = Join-Path $VerDir.FullName 'office6'
$Wtoolex = Join-Path $VerDir.FullName 'wtoolex'
$RootL   = Join-Path $WpsRoot 'ksolaunch.exe'
$RealL   = Join-Path $WpsRoot 'ksolaunch.real.exe'

$Backup = Get-ChildItem $Base -Directory -Filter 'backup-*' |
          Sort-Object Name -Descending | Select-Object -First 1 -ExpandProperty FullName
$ExpectHash = '6A1E302155B25504DA98A6C9F0A67D837AFD850EDCD7729122B27C4DDB0D1557'

Say ("模式     : " + $(if ($VerifyOnly) { '仅核对' } else { '执行还原' }))
Say ("WPS 目录 : " + $WpsRoot)
Say ("备份目录 : " + $(if ($Backup) { $Backup } else { '未找到！' }))
Say ("管理员   : " + $isAdmin)

if (-not $Backup) { Say "找不到 backup-* 备份目录，无法重建计划任务" 'ERR'; $script:Fail++ }

# ── R1 结束进程 ────────────────────────────────────────────────
Step "R1 结束 WPS 进程"
if ($VerifyOnly) { Say "跳过" 'WARN' }
else {
    $names = 'wps','wpsoffice','wpscloudsvr','wpscloudlaunch','wpscenter','wpslingxi','ksolaunch','wpsupdate','updateself','promecefpluginhost','kwpsconvert','photolaunch'
    $killed = 0
    Get-Process $names -ErrorAction SilentlyContinue | ForEach-Object {
        try { Stop-Process -Id $_.Id -Force -ErrorAction Stop; $killed++ } catch {}
    }
    Say ("已结束 $killed 个进程") 'OK'
}

# ── R2 移除 DENY ───────────────────────────────────────────────
Step "R2 移除全部 DENY ACE"
$targets = @(
    $RootL, $RealL,
    (Join-Path $Office6 'ksolaunch.exe'),
    (Join-Path $Wtoolex 'wpsupdate.exe'),
    (Join-Path $Wtoolex 'updateself.exe'),
    (Join-Path $Wtoolex 'updatepolicy.exe'),
    $WpsRoot,
    $CloudUp
) | Where-Object { $_ -and (Test-Path $_) }

if ($VerifyOnly) { Say "跳过" 'WARN' }
else {
    # 父目录必须先解锁，否则后面重命名会被拒
    foreach ($p in @($WpsRoot, $CloudUp) | Where-Object { Test-Path $_ }) {
        icacls $p /remove:d "$User" 2>&1 | Out-Null
        icacls $p /remove:d "$env:USERNAME" 2>&1 | Out-Null
    }
    foreach ($p in $targets) {
        icacls $p /remove:d "$User" 2>&1 | Out-Null
        icacls $p /remove:d "$env:USERNAME" 2>&1 | Out-Null
    }
    foreach ($p in $targets) {
        $acl = icacls $p 2>&1 | Out-String
        if ($acl -match 'DENY') { Say ("仍有 DENY：" + $p) 'ERR'; $script:Fail++ }
        else { Say ("已解锁：" + (Split-Path $p -Leaf) + "  [" + (Split-Path $p -Parent | Split-Path -Leaf) + "]") 'OK' }
    }
}

# ── R3 换回原版 ksolaunch.exe ──────────────────────────────────
Step "R3 恢复原版 ksolaunch.exe"
if ($VerifyOnly) { Say "跳过" 'WARN' }
elseif (-not (Test-Path $RealL)) {
    Say "ksolaunch.real.exe 不存在（可能已是原装）" 'WARN'
}
else {
    try {
        if (Test-Path $RootL) { Remove-Item $RootL -Force -ErrorAction Stop }
        Rename-Item -Path $RealL -NewName 'ksolaunch.exe' -ErrorAction Stop
        Say "守卫壳已移除，原版 ksolaunch.exe 已复位" 'OK'
    } catch {
        Say ("恢复失败：" + $_.Exception.Message + "（若报拒绝访问，先确认 R2 已解锁父目录）") 'ERR'
        $script:Fail++
    }
}

# ── R4 重新注册计划任务 ────────────────────────────────────────
Step "R4 重新注册两个计划任务"
if ($VerifyOnly) { Say "跳过" 'WARN' }
elseif ($Backup) {
    $svc = Get-Service Schedule -ErrorAction SilentlyContinue
    if ($svc.Status -ne 'Running') {
        Say ("任务计划服务未运行（" + $svc.Status + "），尝试启动") 'WARN'
        try { Start-Service Schedule -ErrorAction Stop; Start-Sleep 2 } catch { Say ("启动失败：" + $_.Exception.Message) 'ERR' }
    }
    foreach ($xml in Get-ChildItem $Backup -Filter '*.xml') {
        $name = $xml.BaseName
        try {
            Register-ScheduledTask -Xml (Get-Content $xml.FullName -Raw) -TaskName $name -TaskPath '\' -Force -ErrorAction Stop | Out-Null
            Say ("已重建：" + $name) 'OK'
        } catch {
            # 有些任务的 DACL 只给普通用户读权限，覆盖注册会被拒；只要任务本来就在，原装状态就是满足的
            $exist = Get-ScheduledTask -TaskName $name -TaskPath '\' -ErrorAction SilentlyContinue
            if ($exist) {
                Say ("重建 " + $name + " 被拒（DACL 限制），但任务已存在 [" + $exist.State + "]，视为已恢复") 'WARN'
            } else {
                Say ("重建 " + $name + " 失败：" + $_.Exception.Message) 'ERR'
                $script:Fail++
            }
        }
    }
}

# ── R5 服务启动类型 ────────────────────────────────────────────
Step "R5 wpscloudsvr 恢复为「手动」"
if ($VerifyOnly) { Say "跳过" 'WARN' }
else {
    $s = Get-CimInstance Win32_Service -Filter "Name='wpscloudsvr'" -ErrorAction SilentlyContinue
    if ($s) {
        if ($s.StartMode -eq 'Manual') { Say "已是手动启动" 'OK' }
        elseif ($isAdmin) {
            sc.exe config wpscloudsvr start= demand 2>&1 | ForEach-Object { Say ("  " + $_) 'INFO' }
            $s2 = Get-CimInstance Win32_Service -Filter "Name='wpscloudsvr'"
            Say ("当前：" + $s2.StartMode) $(if ($s2.StartMode -eq 'Manual') { 'OK' } else { 'ERR' })
        } else {
            Say "需要管理员权限，当前不是管理员。请用管理员 PowerShell 执行：" 'WARN'
            Say "    sc.exe config wpscloudsvr start= demand" 'WARN'
        }
    } else { Say "服务不存在" 'WARN' }
}

# ── R6 核对 ────────────────────────────────────────────────────
Step "R6 原装状态核对"
$h = (Get-FileHash $RootL -Algorithm SHA256 -ErrorAction SilentlyContinue).Hash
if ($h -eq $ExpectHash) { Say ("ksolaunch.exe 哈希 == 原始（" + $h.Substring(0,16) + "…）") 'OK' }
else { Say ("ksolaunch.exe 哈希 = " + $h + "  ≠ 原始 " + $ExpectHash) 'ERR'; $script:Fail++ }

Say "— 所有文件应无 DENY —"
foreach ($p in $targets) {
    $acl = icacls $p 2>&1 | Out-String
    Say ("  " + (Split-Path $p -Leaf).PadRight(26) + $(if ($acl -match 'DENY') { '仍被锁' } else { '干净' })) $(if ($acl -match 'DENY') { 'ERR' } else { 'OK' })
}
if ((icacls $WpsRoot 2>&1 | Out-String) -match 'DENY') { Say "  安装根目录仍被锁" 'ERR'; $script:Fail++ } else { Say "  安装根目录           干净" 'OK' }

Say "— 计划任务 —"
$tasks = Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object {
    (($_.Actions | ForEach-Object { "$($_.Execute) $($_.Arguments)" }) -join ' ') -match 'wps|kingsoft|ksolaunch'
}
if ($tasks) { $tasks | ForEach-Object { Say ("  " + $_.TaskPath + $_.TaskName + "  [" + $_.State + "]") 'OK' } }
else { Say "  无 WPS 任务（原装应有 2 条）" 'WARN' }

Say "— 服务 —"
$s3 = Get-CimInstance Win32_Service -Filter "Name='wpscloudsvr'" -ErrorAction SilentlyContinue
if ($s3) { Say ("  wpscloudsvr : " + $s3.State + " / " + $s3.StartMode) $(if ($s3.StartMode -eq 'Manual') { 'OK' } else { 'WARN' }) }

Write-Host ""
if ($script:Fail -eq 0) { Say "原装状态已恢复，WPS 的所有默认行为（含自动更新、重建自启任务）全部回归。" 'OK' }
else { Say ("完成，但有 " + $script:Fail + " 项未达标，见上。") 'WARN' }
Say ("日志：" + $Log)
