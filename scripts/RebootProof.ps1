#requires -Version 5.1
<#
    RebootProof.ps1   v3
    =====================================================================
    回答一个问题：**本次开机以来，WPS 到底有没有被启动过？**

    v3 的关键改动：判定锚点从"基线时间"改成"开机时间 LastBootUpTime"。
    原因：如果存完基线之后、重启之前你自己点过一次 WPS，v2 会把它误判成自启。
    现在所有痕迹都跟开机时间比，重启之前的动作一律不算数。

    用法：
        .\RebootProof.ps1          # 直接判决（不需要事先存基线）
        .\RebootProof.ps1 -Save    # 可选：额外存一份快照备查
#>
[CmdletBinding()]
param(
    [switch]$Save,
    [datetime]$Since
)

$ErrorActionPreference = 'SilentlyContinue'
$Base = Split-Path -Parent $MyInvocation.MyCommand.Path
$SnapFile = Join-Path $Base 'reboot-proof-baseline.json'

function Hdr($t) { Write-Host ""; Write-Host ("===== " + $t + " =====") -ForegroundColor Cyan }
function Row($k, $v, $c = 'Gray') { Write-Host ("  {0,-38} {1}" -f $k, $v) -ForegroundColor $c }

$WpsRoot = Join-Path $env:LOCALAPPDATA 'Kingsoft\WPS Office'
$VerDir  = Get-ChildItem $WpsRoot -Directory | Where-Object { $_.Name -match '^\d+(\.\d+)+$' } |
           Sort-Object Name -Descending | Select-Object -First 1
$Office6 = Join-Path $VerDir.FullName 'office6'
$Wtoolex = Join-Path $VerDir.FullName 'wtoolex'
$O6Log   = Join-Path $env:APPDATA 'kingsoft\office6\log'
$UpLog   = Join-Path $O6Log 'update'
$GuardLog = Join-Path $env:LOCALAPPDATA 'Kingsoft\wps-guard.log'

$boot = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime
# 判定锚点：默认用开机时间；用 -Since 可指定"从某时刻起算"（跳过自己测试造成的痕迹）
$anchor = if ($PSBoundParameters.ContainsKey('Since')) { $Since } else { $boot }
$now  = Get-Date

function EpochToTime($v) {
    $n = 0L
    if (-not [int64]::TryParse("$v", [ref]$n)) { return $null }
    if ($n -lt 100000000000) {
        if ($n -lt 1000000000) { return $null }
        return [DateTimeOffset]::FromUnixTimeSeconds($n).ToLocalTime().DateTime
    }
    return [DateTimeOffset]::FromUnixTimeMilliseconds($n).ToLocalTime().DateTime
}

Write-Host ""
Write-Host "判定窗口内 WPS 是否启动过？" -ForegroundColor White
Row '开机时间' $boot.ToString('yyyy-MM-dd HH:mm:ss')
Row '当前时间' $now.ToString('yyyy-MM-dd HH:mm:ss')
Row '已运行' ("{0:hh\:mm\:ss}" -f ($now - $boot))
Row '启动模式' (Get-CimInstance Win32_ComputerSystem).BootupState
Row '判定锚点' ($anchor.ToString('HH:mm:ss') + $(if ($anchor -ne $boot) { '  （-Since 指定，开机时间 ' + $boot.ToString('HH:mm:ss') + '）' } else { '  （= 开机时间）' }))

$fail = 0
$autoAttempt = 0
$manualOpen  = 0

# ── 1. 进程 ────────────────────────────────────────────────────
Hdr "1. 当前 WPS 进程"
$procs = Get-CimInstance Win32_Process | Where-Object {
    $_.Name -match '^(wps|wpsoffice|wpscloudsvr|wpscloudlaunch|wpscenter|wpslingxi|ksolaunch|wpsupdate|updateself)$'
}
if (-not $procs) { Row '进程' '无' 'Green' }
else {
    foreach ($p in $procs) {
        $after = $p.CreationDate -gt $anchor
        Row ($p.Name + " PID " + $p.ProcessId) ($p.CreationDate.ToString('HH:mm:ss') + $(if ($after) { '  ← 锚点后启动' } else { '' })) $(if ($after) { 'Red' } else { 'Yellow' })
        if ($after) { $fail++ }
    }
}

# ── 2. 守卫壳日志（最直接的调用记录）──────────────────────────
Hdr "2. 守卫壳调用记录（ksolaunch 每次被调用都写）"
if (-not (Test-Path $GuardLog)) { Row '日志' '不存在（从未被调用）' 'Green' }
else {
    $gl = Get-Content $GuardLog | Where-Object {
        if ($_ -match '^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})') { [datetime]$matches[1] -gt $anchor } else { $false }
    }
    if (-not $gl) { Row '锚点后调用' '0 次' 'Green' }
    else {
        foreach ($l in $gl) {
            if ($l -match 'BLOCKED') { $autoAttempt++; Row '自启被拦截' $l 'Green' }
            elseif ($l -match 'from=startmenu|fromksolaunch') { $manualOpen++; Row '手动打开' $l 'Yellow' }
            else { Row '调用' $l 'Yellow' }
        }
    }
    $tail = Get-Content $GuardLog -Tail 1
    Row '（最近一条，含重启前）' $tail 'DarkGray'
}

# ── 3. WPS 自己写的启动时间戳 ──────────────────────────────────
Hdr "3. WPS 自留启动痕迹"
$pairs = @(
    @{ K = 'HKCU:\SOFTWARE\Kingsoft\Office\6.0\wpsoffice\Application Settings'; N = 'coldStartup';  L = 'coldStartup (wpsoffice)' },
    @{ K = 'HKCU:\SOFTWARE\Kingsoft\Office\6.0\qing\Application Settings';      N = 'coldStartup';  L = 'coldStartup (qing)' },
    @{ K = 'HKCU:\SOFTWARE\Kingsoft\Office\6.0\Common\updateinfo';             N = 'LastStartTime'; L = 'updateinfo.LastStartTime' }
)
foreach ($p in $pairs) {
    $v = (Get-ItemProperty $p.K -Name $p.N -ErrorAction SilentlyContinue).($p.N)
    if ($null -eq $v) { Row $p.L '（无值）' 'DarkGray'; continue }
    $t = EpochToTime $v
    if (-not $t) { Row $p.L "$v（非时间戳）" 'DarkGray'; continue }
    if ($t -gt $anchor) { Row $p.L ($t.ToString('MM-dd HH:mm:ss') + '  ← 锚点后') 'Red'; $fail++ }
    else { Row $p.L ($t.ToString('MM-dd HH:mm:ss') + '  （锚点前，不算）') 'Green' }
}

# ── 4. 日志目录写入 ────────────────────────────────────────────
Hdr "4. 日志写入时间"
$newest = Get-ChildItem $O6Log -Recurse -File -ErrorAction SilentlyContinue |
          Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($newest) {
    if ($newest.LastWriteTime -gt $anchor) { Row 'office6 日志最新写入' ($newest.LastWriteTime.ToString('MM-dd HH:mm:ss') + '  ← 锚点后') 'Red'; $fail++ }
    else { Row 'office6 日志最新写入' ($newest.LastWriteTime.ToString('MM-dd HH:mm:ss') + '  （锚点前）') 'Green' }
}
$klogs = Get-ChildItem $O6Log -Recurse -File -Filter '*_wps_*.klog' -ErrorAction SilentlyContinue |
         Where-Object { $_.LastWriteTime -gt $anchor }
Row '锚点后新增 wps 会话日志' ($klogs.Count.ToString() + ' 个') $(if ($klogs.Count -gt 0) { 'Red' } else { 'Green' })
if ($klogs.Count -gt 0) { $fail++ }

# ── 5. 更新器活动 ──────────────────────────────────────────────
Hdr "5. 更新器活动"
$up = Get-ChildItem $UpLog -Filter 'wpsupdate_*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $up) { Row '更新日志' '无' 'Green' }
else {
    $after = Get-Content $up.FullName | Where-Object {
        if ($_ -match '^(\d{4})/(\d{2})/(\d{2}) (\d{2}):(\d{2}):(\d{2})') {
            (Get-Date -Year $matches[1] -Month $matches[2] -Day $matches[3] -Hour $matches[4] -Minute $matches[5] -Second $matches[6]) -gt $anchor
        } else { $false }
    }
    if ($after) {
        Row '锚点后更新器活动' ($after.Count.ToString() + ' 行') 'Red'; $fail++
        $after | Select-Object -First 5 | ForEach-Object { Row '' ($_ -replace '\s+',' ') 'DarkYellow' }
    } else { Row '锚点后更新器活动' '0 行' 'Green' }
}

# ── 6. 安装目录有没有被铺新版本 ────────────────────────────────
Hdr "6. 安装目录"
$newVer = Get-ChildItem $WpsRoot -Directory | Where-Object { $_.CreationTime -gt $anchor }
Row '现有版本目录' ($(Get-ChildItem $WpsRoot -Directory | Where-Object { $_.Name -match '^\d+(\.\d+)+$' } | Select-Object -ExpandProperty Name) -join ',')
if ($newVer) { $newVer | ForEach-Object { Row '锚点后新建' ($_.Name + '  ' + $_.CreationTime.ToString('HH:mm:ss')) 'Red' }; $fail++ }
else { Row '锚点后新建' '无' 'Green' }

# ── 7. 锁是否还在 ──────────────────────────────────────────────
Hdr "7. 锁与文件"
$orig = @{
    '6A1E302155B25504DA98A6C9F0A67D837AFD850EDCD7729122B27C4DDB0D1557' = '原版'
    '83F359DE635F0297189B2B5C78BB55AA4E99661BFC793C0EF48344AE24A69F64' = '原版'
    '2D6A959453930EF1C91EDA7C00B98412EEB70225B800F8088CA4F701924CAC50' = '原版'
}
$items = @(
    @{ P = (Join-Path $WpsRoot 'ksolaunch.exe');          N = 'ksolaunch.exe (主入口)'; Guard = $true  },
    @{ P = (Join-Path $WpsRoot 'ksolaunch.real.exe');     N = 'ksolaunch.real.exe';     Guard = $false },
    @{ P = (Join-Path $Office6 'ksolaunch.exe');          N = 'office6\ksolaunch.exe';  Guard = $false },
    @{ P = (Join-Path $Wtoolex 'wpsupdate.exe');          N = 'wpsupdate.exe';          Guard = $false },
    @{ P = (Join-Path $Wtoolex 'updateself.exe');         N = 'updateself.exe';         Guard = $false }
) | Where-Object { Test-Path $_.P }
foreach ($i in $items) {
    $h = (Get-FileHash $i.P -Algorithm SHA256).Hash
    $deny = ((icacls $i.P 2>&1 | Out-String) -match 'DENY')
    $isOrig = $orig.ContainsKey($h)
    if (-not $deny) { Row $i.N 'DENY 丢失' 'Red'; $fail++ }
    elseif ($i.Guard) {
        if ($isOrig) { Row $i.N '主入口被换回原版（自启会复活）' 'Red'; $fail++ }
        else { Row $i.N '守卫壳在位 / 已上锁' 'Green' }
    }
    else {
        if ($isOrig) { Row $i.N '原版未被篡改 / 已上锁' 'Green' }
        else { Row $i.N '被替换成未知二进制' 'Red'; $fail++ }
    }
}
$t = Get-ScheduledTask | Where-Object { (($_.Actions | ForEach-Object { "$($_.Execute) $($_.Arguments)" }) -join ' ') -match 'wps|kingsoft|ksolaunch' }
Row '计划任务' ($(if ($t) { ($t | ForEach-Object { $_.TaskName }) -join ',' } else { '0 条' })) $(if ($t) { 'Red' } else { 'Green' })
$svc = Get-CimInstance Win32_Service -Filter "Name='wpscloudsvr'"
Row 'wpscloudsvr' ($svc.State + ' / ' + $svc.StartMode) $(if ($svc.StartMode -eq 'Disabled') { 'Green' } else { 'Yellow' })

# ── 判决 ───────────────────────────────────────────────────────
Write-Host ""
Write-Host ("=" * 62)
if ($fail -eq 0) {
    Write-Host "判决：干净。判定窗口内 WPS 从未被启动。" -ForegroundColor Green
    if ($autoAttempt -gt 0) { Write-Host ("      期间有 " + $autoAttempt + " 次自启/更新调用被守卫壳拦截（正常现象，说明拦截在工作）") -ForegroundColor Green }
    if ($manualOpen  -gt 0) { Write-Host ("      期间有 " + $manualOpen + " 次你手动打开（不影响结论）") -ForegroundColor Yellow }
    Write-Host ("      开机时间 " + $boot.ToString('HH:mm:ss') + "，已运行 " + ("{0:hh\:mm\:ss}" -f ($now - $boot))) -ForegroundColor DarkGray
} else {
    Write-Host ("判决：窗口内发现 " + $fail + " 项开机后活动，见红色项。") -ForegroundColor Red
    Write-Host "      把本窗口内容发我，按痕迹继续定位。" -ForegroundColor Red
}

if ($Save) {
    $snap = [pscustomobject]@{
        SavedAt  = $now.ToString('yyyy-MM-dd HH:mm:ss')
        BootTime = $boot.ToString('yyyy-MM-dd HH:mm:ss')
        判决     = if ($fail -eq 0) { 'PASS' } else { "FAIL($fail)" }
    }
    $snap | ConvertTo-Json | Set-Content $SnapFile -Encoding UTF8
    Write-Host ("快照已存：" + $SnapFile) -ForegroundColor DarkGray
}


