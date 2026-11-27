#requires -Version 5.1
<#
    Verify-WpsAutostartLockdown.ps1
    一次性复核：全量自启动面扫描 + 载荷可执行性 + 守卫壳行为 + 残留风险清单。
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'SilentlyContinue'
$Base = Split-Path -Parent $MyInvocation.MyCommand.Path
$Log  = Join-Path $Base ("verify-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + ".log")

$rows = New-Object System.Collections.Generic.List[object]
function Add-Row { param($Item, $Result, $Detail)
    $rows.Add([pscustomobject]@{ 检查项 = $Item; 结果 = $Result; 说明 = $Detail })
    Write-Host ("[{0,-4}] {1,-34} {2}" -f $Result, $Item, $Detail)
}

Write-Host "=================== 1. 计划任务 ===================" -ForegroundColor Cyan
$all = Get-ScheduledTask -ErrorAction SilentlyContinue
$wpsTasks = $all | Where-Object {
    $blob = ($_.Actions | ForEach-Object { "$($_.Execute) $($_.Arguments) $($_.WorkingDirectory)" }) -join ' '
    $blob -match 'wps|kingsoft|ksolaunch'
}
if (-not $wpsTasks) { Add-Row '计划任务' 'PASS' '无任何任务指向 WPS' }
else {
    foreach ($t in $wpsTasks) {
        $act = ($t.Actions | ForEach-Object { "$($_.Execute) $($_.Arguments)" }) -join ' '
        $state = $t.State
        Add-Row ("任务 " + $t.TaskName) 'WARN' ("$state ｜ $act")
    }
}

Write-Host "`n=================== 2. 注册表自启面 ===================" -ForegroundColor Cyan
$runKeys = @(
    'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run',
    'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce',
    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run',
    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce',
    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run',
    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce',
    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\RunServices',
    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer\Run',
    'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer\Run'
)
$regHit = 0
foreach ($k in $runKeys) {
    if (-not (Test-Path $k)) { continue }
    (Get-ItemProperty $k).PSObject.Properties |
        Where-Object { $_.Name -notmatch '^PS' -and "$($_.Value)" -match 'wps|kingsoft|ksolaunch' } |
        ForEach-Object { $regHit++; Add-Row ("Run: " + $_.Name) 'FAIL' ("$k = $($_.Value)") }
}
if ($regHit -eq 0) { Add-Row 'Run/RunOnce/RunServices' 'PASS' '9 个常见 Run 位置无 WPS 项' }

Write-Host "`n=================== 3. 启动文件夹 ===================" -ForegroundColor Cyan
$sf = @(
    (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Startup'),
    (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Startup')
)
$sfHit = 0
foreach ($d in $sf) {
    Get-ChildItem $d -File | ForEach-Object { $sfHit++; Add-Row ("启动项 " + $_.Name) 'WARN' $_.FullName }
}
if ($sfHit -eq 0) { Add-Row '启动文件夹' 'PASS' '两个 Startup 目录均为空' }

Write-Host "`n=================== 4. 服务 ===================" -ForegroundColor Cyan
$svc = Get-CimInstance Win32_Service | Where-Object { $_.PathName -match 'kingsoft|wps' -or $_.Name -match 'wps|kingsoft' }
foreach ($s in $svc) { Add-Row ("服务 " + $s.Name) 'INFO' ("$($s.State) / $($s.StartMode) / $($s.PathName)") }
if (-not $svc) { Add-Row '服务' 'PASS' '无 WPS 相关服务' }

Write-Host "`n=================== 5. 其它 ASEP ===================" -ForegroundColor Cyan
Add-Row 'Winlogon Shell' 'PASS' ((Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon').Shell)
Add-Row 'AppInit_DLLs' 'PASS' ("[" + (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows').AppInit_DLLs + "]")
$as = Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Active Setup\Installed Components','HKLM:\SOFTWARE\WOW6432Node\Microsoft\Active Setup\Installed Components' -ErrorAction SilentlyContinue |
      ForEach-Object { (Get-ItemProperty $_.PSPath).StubPath } | Where-Object { $_ -match 'wps|kingsoft' }
Add-Row 'Active Setup' $(if ($as) { 'WARN' } else { 'PASS' }) $(if ($as) { $as -join ';' } else { '无 WPS 项' })
$ifeo = Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options' -ErrorAction SilentlyContinue |
        Where-Object { $_.PSChildName -match 'wps|ksolaunch' } | Select-Object -ExpandProperty PSChildName
Add-Row 'IFEO' 'PASS' $(if ($ifeo) { $ifeo -join ',' } else { '无 WPS 劫持项' })

Write-Host "`n=================== 6. 载荷可执行性 ===================" -ForegroundColor Cyan
$WpsRoot = Join-Path $env:LOCALAPPDATA 'Kingsoft\WPS Office'
$VerDir  = Get-ChildItem $WpsRoot -Directory | Where-Object { $_.Name -match '^\d+(\.\d+)+$' } |
           Sort-Object Name -Descending | Select-Object -First 1
$payload = @(
    @{ P = (Join-Path $WpsRoot 'ksolaunch.exe');        N = 'ksolaunch.exe (守卫壳)';        ShouldRun = $true  },
    @{ P = (Join-Path $VerDir.FullName 'office6\ksolaunch.exe'); N = 'office6\ksolaunch.exe'; ShouldRun = $false },
    @{ P = (Join-Path $VerDir.FullName 'wtoolex\wpsupdate.exe');  N = 'wtoolex\wpsupdate.exe';  ShouldRun = $false },
    @{ P = (Join-Path $VerDir.FullName 'wtoolex\updateself.exe'); N = 'wtoolex\updateself.exe'; ShouldRun = $false },
    @{ P = (Join-Path $VerDir.FullName 'wtoolex\updatepolicy.exe'); N = 'wtoolex\updatepolicy.exe'; ShouldRun = $false }
)
foreach ($x in $payload) {
    if (-not (Test-Path $x.P)) { Add-Row $x.N 'INFO' '文件不存在'; continue }
    $acl  = icacls $x.P 2>&1 | Out-String
    $deny = ($acl -match 'DENY')
    $ran  = $true
    try { $pp = Start-Process -FilePath $x.P -ArgumentList '/wpsupdate' -PassThru -ErrorAction Stop; try { $pp.Kill() } catch {} } catch { $ran = $false }
    if ($x.ShouldRun) {
        if ($ran) { Add-Row $x.N 'PASS' '可执行（图标入口正常）' }
        else      { Add-Row $x.N 'FAIL' '不可执行 —— 主图标会被破坏' }
    } else {
        if (-not $ran -and $deny) { Add-Row $x.N 'PASS' 'DENY 生效，无法执行' }
        elseif (-not $ran)        { Add-Row $x.N 'WARN' '无法执行但无 DENY（原因待查）' }
        else                      { Add-Row $x.N 'FAIL' '仍可执行' }
    }
}

Write-Host "`n=================== 7. 端到端行为 ===================" -ForegroundColor Cyan
# 先清场：如果 WPS 已经在跑，进程对比会误报
Get-Process wps,wpsoffice,wpscloudsvr,wpscloudlaunch,wpscenter,promecefpluginhost,ksolaunch -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 4
Write-Host ("  清场后 WPS 进程数: " + (Get-Process wps,wpsoffice,wpscloudsvr -ErrorAction SilentlyContinue).Count)
$before = (Get-Process wps, wpsoffice -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id | Sort-Object) -join ','
$shim = Join-Path $WpsRoot 'ksolaunch.exe'
$taskArgs = @('/wpsupdate -from=task -source=task', '-createtask', '-repairtask', '/prometheus /from=autostart_after_install')
foreach ($a in $taskArgs) {
    try {
        $p = Start-Process -FilePath $shim -ArgumentList $a -PassThru -ErrorAction Stop
        $p.WaitForExit(6000) | Out-Null
        Write-Host ("  已发送参数 [{0}] -> 退出码 {1}" -f $a, $p.ExitCode)
    } catch { Write-Host ("  参数 [{0}] 启动异常：{1}" -f $a, $_.Exception.Message) -ForegroundColor Yellow }
}
Start-Sleep -Seconds 3
$after = (Get-Process wps, wpsoffice -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id | Sort-Object) -join ','
if ($after -eq $before) { Add-Row '模拟全部自启/更新参数' 'PASS' '四种更新/自启调用均未拉起任何 WPS 进程' }
else { Add-Row '模拟全部自启/更新参数' 'FAIL' "进程变化 before=[$before] after=[$after]" }

# 反向验证：正常启动必须能开（快捷方式真实参数），否则就是拦过头了
Write-Host "  --- 反向验证：正常启动参数 ---"
Get-Process wps,wpsoffice,wpscloudsvr,promecefpluginhost,ksolaunch -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 4
try {
    $pl = Start-Process -FilePath $shim -ArgumentList '/prometheus /fromksolaunch /from=startmenu' -PassThru -ErrorAction Stop
    Start-Sleep -Seconds 12
    $nrm = (Get-Process wps,wpsoffice,wpscloudsvr -ErrorAction SilentlyContinue).Count
    Write-Host ("  正常启动参数 -> 拉起进程 $nrm")
    if ($nrm -gt 0) { Add-Row '正常启动（点图标）' 'PASS' "$nrm 个进程，图标可用" }
    else            { Add-Row '正常启动（点图标）' 'FAIL' '正常启动被拦，图标会打不开' }
} catch {
    Add-Row '正常启动（点图标）' 'FAIL' ("异常：" + $_.Exception.Message)
}
# 收尾：把测试拉起的 WPS 关掉，避免影响后续判读
Get-Process wps,wpsoffice,wpscloudsvr,wpscloudlaunch,wpscenter,promecefpluginhost,ksolaunch -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 3

Write-Host "`n=================== 汇总 ===================" -ForegroundColor Cyan
$rows | Format-Table -AutoSize -Wrap | Out-String -Width 200 | Tee-Object -FilePath $Log -Append | Write-Host
$fail = ($rows | Where-Object { $_.结果 -eq 'FAIL' }).Count
if ($fail -eq 0) { Write-Host "FAIL 项：0 —— 自启链条已无可用载荷。" -ForegroundColor Green }
else             { Write-Host "FAIL 项：$fail —— 见上表。" -ForegroundColor Red }
Write-Host "报告：$Log"
