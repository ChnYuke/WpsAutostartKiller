#requires -Version 5.1
<#
    WPS-Autostart-Portable.ps1
    =====================================================================
    通用版：不写死任何路径 / 任务名 / 哈希，可在任意电脑上直接跑。

    能自动适应的情况：
      * 用户级安装   %LOCALAPPDATA%\Kingsoft\WPS Office\<版本>\
      * 全局安装     C:\Program Files\Kingsoft\WPS Office\<版本>\
                     C:\Program Files (x86)\Kingsoft\WPS Office\<版本>\
      * 非默认路径   以注册表 InstallRoot / App Paths 为准
      * 任意用户名   任务名形如 WpsUpdateTask_<用户名>，改为按动作扫描，不依赖名字
      * 任意 WPS 版本  原版哈希在首次运行时现场记录成基线，不依赖已知值
      * 组件缺失     wtoolex 不存在、ksolaunch 不存在，都不报错，按实际能锁的都锁

    用法：
        .\WPS-Autostart-Portable.ps1              # 默认：只检测并报告，不改动任何东西
        .\WPS-Autostart-Portable.ps1 -Apply       # 执行封锁
        .\WPS-Autostart-Portable.ps1 -Apply -DryRun   # 演练，只打印将要做什么
        .\WPS-Autostart-Portable.ps1 -Restore     # 恢复原装

    权限：
        安装目录在 %LOCALAPPDATA% 时用户级即可；
        在 Program Files 下时必须管理员（脚本会检测并提示）。
#>
[CmdletBinding()]
param(
    [switch]$Apply,
    [switch]$Restore,
    [switch]$DryRun,
    [switch]$SkipKill,
    [string]$BackupDir
)

$ErrorActionPreference = 'Continue'
$Base  = Split-Path -Parent $MyInvocation.MyCommand.Path
$Stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$Log   = Join-Path $Base ("portable-" + $Stamp + ".log")
$Me    = "$env:USERDOMAIN\$env:USERNAME"
$Fail  = 0

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

# ══════════════════════════════════════════════════════════════
# 1. 自动探测安装布局
# ══════════════════════════════════════════════════════════════
function Get-WpsLayout {
    $roots = New-Object System.Collections.Generic.List[string]

    # 来源 A：注册表 InstallRoot（最准，能覆盖非默认路径）
    foreach ($k in @(
        'HKLM:\SOFTWARE\Kingsoft\Office\6.0\common',
        'HKLM:\SOFTWARE\WOW6432Node\Kingsoft\Office\6.0\common',
        'HKCU:\SOFTWARE\Kingsoft\Office\6.0\Common',
        'HKCU:\SOFTWARE\Kingsoft\Office\6.0\common'
    )) {
        $ir = (Get-ItemProperty $k -Name InstallRoot -ErrorAction SilentlyContinue).InstallRoot
        if ($ir -and (Test-Path $ir)) { $roots.Add($ir) }
    }

    # 来源 B：App Paths\wps.exe → office6 的父目录
    foreach ($k in @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\wps.exe',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\wps.exe',
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\wps.exe'
    )) {
        $exe = (Get-ItemProperty $k -ErrorAction SilentlyContinue).'(default)'
        if ($exe -and (Test-Path $exe)) {
            $d = Split-Path $exe -Parent          # …\office6
            if ((Split-Path $d -Leaf) -ieq 'office6') { $roots.Add((Split-Path $d -Parent)) }
        }
    }

    # 来源 C：常见根目录下扫 office6\wpsoffice.exe
    $seeds = @(
        (Join-Path $env:LOCALAPPDATA 'Kingsoft'),
        (Join-Path $env:ProgramFiles 'Kingsoft'),
        (Join-Path ${env:ProgramFiles(x86)} 'Kingsoft')
    ) | Where-Object { $_ -and (Test-Path $_) }
    foreach ($s in $seeds) {
        Get-ChildItem $s -Directory -ErrorAction SilentlyContinue | ForEach-Object {
            Get-ChildItem $_.FullName -Directory -ErrorAction SilentlyContinue | ForEach-Object {
                if (Test-Path (Join-Path $_.FullName 'office6\wpsoffice.exe')) { $roots.Add($_.FullName) }
            }
        }
    }

    $seen = @{}
    $out = @()
    foreach ($r in $roots) {
        $r = (Resolve-Path $r -ErrorAction SilentlyContinue).Path
        if (-not $r -or $seen.ContainsKey($r)) { continue }
        $seen[$r] = 1

        $office6 = Join-Path $r 'office6'
        if (-not (Test-Path $office6)) { continue }

        $rootDir = Split-Path $r -Parent
        $out += [pscustomobject]@{
            VersionDir   = $r
            RootDir      = $rootDir
            Office6      = $office6
            Wtoolex      = (Join-Path $r 'wtoolex')
            LauncherRoot = (Join-Path $rootDir 'ksolaunch.exe')
            LauncherO6   = (Join-Path $office6 'ksolaunch.exe')
            Portal       = (Join-Path $office6 'wpsoffice.exe')
            WpsExe       = (Join-Path $office6 'wps.exe')
            MachineWide  = ($rootDir -match '^[A-Z]:\\Program Files')
        }
    }
    return $out
}

# ══════════════════════════════════════════════════════════════
# 2. 自动探测计划任务 / 服务
# ══════════════════════════════════════════════════════════════
function Get-WpsTasks {
    Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object {
        $b = ($_.Actions | ForEach-Object { "$($_.Execute) $($_.Arguments)" }) -join ' '
        $b -match 'ksolaunch|wpsupdate|kingsoft\\.*wps|WPS Office'
    }
}

function Get-WpsServices {
    Get-CimInstance Win32_Service -ErrorAction SilentlyContinue |
        Where-Object { $_.PathName -match 'Kingsoft|\\WPS' }
}

# ══════════════════════════════════════════════════════════════
# 3. 守卫壳（与原机同款：token 精确匹配 + 图标 + 日志）
# ══════════════════════════════════════════════════════════════
$ShimSource = @'
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

internal static class WpsLaunchGuard
{
    // token 精确匹配：绝不使用子串包含（历史事故：/prometheus、from=taskbar 被误伤）
    private static readonly string[] BlockedCores =
    {
        "wpsupdate", "createtask", "repairtask", "from=task", "source=task"
    };

    private static string LogPath
    {
        get
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Kingsoft\wps-guard.log");
        }
    }

    private static void Log(string msg)
    {
        try
        {
            string p = LogPath;
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            var fi = new FileInfo(p);
            if (fi.Exists && fi.Length > 262144) fi.Delete();
            File.AppendAllText(p,
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " | " + msg + Environment.NewLine,
                Encoding.UTF8);
        }
        catch { }
    }

    private static bool IsBlocked(string argsJoined)
    {
        if (string.IsNullOrEmpty(argsJoined)) return false;
        foreach (var raw in argsJoined.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string core = raw.TrimStart('-', '/').ToLowerInvariant();
            foreach (var b in BlockedCores) if (core == b) return true;
            if (core.Contains("autostart")) return true;
        }
        return false;
    }

    private static int Main(string[] args)
    {
        var sb = new StringBuilder();
        foreach (var a in args) sb.Append(a).Append(' ');
        string joined = sb.ToString().Trim();

        if (IsBlocked(joined))
        {
            Log("BLOCKED  args=[" + joined + "]");
            return 0;
        }

        string dir = null;
        try { dir = Path.GetDirectoryName(typeof(WpsLaunchGuard).Assembly.Location); } catch { }

        string target = ResolvePortal(dir);
        if (target == null) { Log("NO-TARGET args=[" + joined + "] dir=[" + dir + "]"); return 0; }

        try
        {
            var psi = new ProcessStartInfo(target)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(target),
                Arguments = joined
            };
            Process.Start(psi);
            Log("LAUNCH   args=[" + joined + "] -> " + target);
        }
        catch (Exception ex)
        {
            Log("FAIL     args=[" + joined + "] target=[" + target + "] err=" + ex.Message);
        }
        return 0;
    }

    private static string ResolvePortal(string root)
    {
        if (string.IsNullOrEmpty(root)) return null;
        try
        {
            string best = null; Version bestVer = null;
            foreach (var d in Directory.GetDirectories(root))
            {
                var name = Path.GetFileName(d);
                if (!Regex.IsMatch(name, @"^\d+(\.\d+)+$")) continue;
                Version v; if (!Version.TryParse(name, out v)) continue;
                foreach (var c in new[] { Path.Combine(d, "office6", "wpsoffice.exe"), Path.Combine(d, "office6", "wps.exe") })
                {
                    if (!File.Exists(c)) continue;
                    if (bestVer == null || v > bestVer) { bestVer = v; best = c; }
                    break;
                }
            }
            if (best != null) return best;
            var real = Path.Combine(root, "ksolaunch.real.exe");
            if (File.Exists(real)) return real;
        }
        catch { }
        return null;
    }
}
'@

function New-GuardShim {
    param([string]$SourceExe, [string]$WorkDir)
    $csc = @(
        (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
        (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $csc) { Say '未找到 csc.exe，无法编译守卫壳' 'ERR'; return $null }

    $cs  = Join-Path $WorkDir 'WpsLaunchGuard.cs'
    $ico = Join-Path $WorkDir 'wps.ico'
    $out = Join-Path $WorkDir 'ksolaunch.guard.exe'
    Set-Content $cs -Value $ShimSource -Encoding UTF8

    # 从原版 exe 提图标（失败就不带图标，不影响功能）
    if (-not (Test-Path $ico)) { try { New-IcoFromExe -Exe $SourceExe -Out $ico | Out-Null } catch { } }

    $args = @('/nologo', '/target:winexe', '/optimize+', '/platform:anycpu', "/out:$out")
    if (Test-Path $ico) { $args += "/win32icon:$ico" }
    $args += $cs
    & $csc @args 2>&1 | Out-Null
    if (Test-Path $out) { return $out }
    return $null
}

function New-IcoFromExe {
    param([string]$Exe, [string]$Out)
    Add-Type -AssemblyName System.Drawing
    if (-not ('IconGrab' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class IconGrab
{
    [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    public static extern uint PrivateExtractIcons(string f, int i, int cx, int cy, IntPtr[] h, uint[] id, uint n, uint fl);
    [DllImport("user32.dll", SetLastError=true)]
    public static extern bool DestroyIcon(IntPtr h);
}
'@
    }
    $entries = @()
    foreach ($s in 16, 32, 48, 256) {
        $h = New-Object IntPtr[] 1; $ids = New-Object uint32[] 1
        $n = [IconGrab]::PrivateExtractIcons($Exe, 0, $s, $s, $h, $ids, 1, 0)
        if ($n -lt 1 -or $h[0] -eq [IntPtr]::Zero) { continue }
        try {
            $bmp = [System.Drawing.Icon]::FromHandle($h[0]).ToBitmap()
            $rs = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
            $g = [System.Drawing.Graphics]::FromImage($rs)
            $g.InterpolationMode = 'HighQualityBicubic'; $g.DrawImage($bmp, 0, 0, $s, $s); $g.Dispose()
            $ms = New-Object System.IO.MemoryStream; $bw = New-Object System.IO.BinaryWriter($ms)
            $xor = $s * $s * 4
            $andRow = [int][Math]::Ceiling($s / 8.0); $andRow += (4 - ($andRow % 4)) % 4
            $and = $andRow * $s
            $bw.Write([uint32]40); $bw.Write([int32]$s); $bw.Write([int32]($s * 2))
            $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]0)
            $bw.Write([uint32]($xor + $and)); $bw.Write([int32]0); $bw.Write([int32]0)
            $bw.Write([uint32]0); $bw.Write([uint32]0)
            for ($y = $s - 1; $y -ge 0; $y--) { for ($x = 0; $x -lt $s; $x++) { $c = $rs.GetPixel($x, $y); $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A) } }
            $bw.Write((New-Object byte[] $and)); $bw.Flush()
            $entries += [pscustomobject]@{ Size = $s; Bytes = $ms.ToArray() }
            $bw.Dispose(); $ms.Dispose(); $rs.Dispose(); $bmp.Dispose()
        } finally { [void][IconGrab]::DestroyIcon($h[0]) }
    }
    if (-not $entries) { throw "该 exe 没有可提取的图标" }
    $fs = [System.IO.File]::Create($Out); $w = New-Object System.IO.BinaryWriter($fs)
    $w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$entries.Count)
    $off = 6 + 16 * $entries.Count
    foreach ($e in $entries) {
        $d = if ($e.Size -ge 256) { 0 } else { $e.Size }
        $w.Write([byte]$d); $w.Write([byte]$d); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$e.Bytes.Length); $w.Write([uint32]$off)
        $off += $e.Bytes.Length
    }
    foreach ($e in $entries) { $w.Write($e.Bytes) }
    $w.Flush(); $w.Dispose(); $fs.Dispose()
    return $Out
}

# ══════════════════════════════════════════════════════════════
# 4. 主流程
# ══════════════════════════════════════════════════════════════
Say ("模式: " + $(if ($Restore) { '恢复原装' } elseif ($Apply) { '执行封锁' } else { '仅检测' }) + $(if ($DryRun) { ' (演练)' } else { '' }))
Say ("用户: $Me    管理员: $isAdmin")

Step "探测 WPS 安装布局"
$layouts = @(Get-WpsLayout)
if (-not $layouts) { Say '没有找到任何 WPS 安装，退出' 'ERR'; exit 1 }
foreach ($l in $layouts) {
    Say ("版本目录 : " + $l.VersionDir) 'OK'
    Say ("  安装根   : " + $l.RootDir + $(if ($l.MachineWide) { '   [全局安装]' } else { '   [用户级安装]' }))
    Say ("  office6  : " + (Test-Path $l.Office6))
    Say ("  wtoolex  : " + (Test-Path $l.Wtoolex))
    Say ("  根启动器 : " + (Test-Path $l.LauncherRoot))
    Say ("  门户     : " + (Test-Path $l.Portal))
    if ($l.MachineWide -and -not $isAdmin) {
        Say "该安装位于 Program Files，必须管理员运行本脚本" 'ERR'
        $Fail++
    }
}

Step "探测计划任务"
$tasks = @(Get-WpsTasks)
if ($tasks) { $tasks | ForEach-Object { Say ("  " + $_.TaskPath + $_.TaskName + "  [" + $_.State + "]  " + (($_.Actions | ForEach-Object { $_.Execute }) -join '|')) 'WARN' } }
else { Say "  无 WPS 计划任务" 'OK' }

Step "探测服务"
$svcs = @(Get-WpsServices)
if ($svcs) { $svcs | ForEach-Object { Say ("  " + $_.Name + "  " + $_.State + " / " + $_.StartMode + "  " + $_.PathName) 'WARN' } }
else { Say "  无 WPS 服务" 'OK' }

# ── 恢复模式 ──
if ($Restore) {
    Step "恢复原装"
    if (-not $BackupDir) {
        $BackupDir = Get-ChildItem $Base -Directory -Filter 'portable-backup-*' | Sort-Object Name -Descending |
                     Select-Object -First 1 -ExpandProperty FullName
    }
    if (-not $BackupDir -or -not (Test-Path $BackupDir)) { Say "找不到备份目录" 'ERR'; exit 1 }
    Say ("使用备份: " + $BackupDir)
    $man = Get-Content (Join-Path $BackupDir 'manifest.json') -Raw | ConvertFrom-Json

    foreach ($f in $man.Files) {
        if (-not $DryRun) {
            icacls $f /remove:d "$Me" 2>&1 | Out-Null
            icacls $f /remove:d "$env:USERNAME" 2>&1 | Out-Null
        }
        Say ("解除锁定: " + $f) 'OK'
    }
    if ($man.Launcher -and (Test-Path ($man.Launcher + '.real.exe'))) {
        $real = $man.Launcher + '.real.exe'
        if (-not $DryRun) {
            icacls $man.Launcher /remove:d "$Me" 2>&1 | Out-Null
            Remove-Item $man.Launcher -Force -ErrorAction SilentlyContinue
            Rename-Item $real -NewName (Split-Path $man.Launcher -Leaf) -ErrorAction SilentlyContinue
        }
        Say "原版启动器已复位" 'OK'
    }
    foreach ($x in Get-ChildItem $BackupDir -Filter '*.xml' -ErrorAction SilentlyContinue) {
        if ($DryRun) { Say ("[演练] 重建任务 " + $x.BaseName) 'WARN'; continue }
        try { Register-ScheduledTask -Xml (Get-Content $x.FullName -Raw) -TaskName $x.BaseName -Force -ErrorAction Stop | Out-Null; Say ("重建任务: " + $x.BaseName) 'OK' }
        catch { Say ("重建任务 " + $x.BaseName + " 失败: " + $_.Exception.Message) 'WARN' }
    }
    foreach ($s in $man.Services) {
        if ($DryRun) { continue }
        sc.exe config $s.Name start= $s.OriginalStartMode 2>&1 | Out-Null
        Say ("服务 " + $s.Name + " 恢复为 " + $s.OriginalStartMode) 'OK'
    }
    Write-Host ""; Say "恢复完成。建议重启一次。" 'OK'
    exit 0
}

# ── 仅检测 ──
if (-not $Apply) {
    Step "检测结论"
    Say "以上是探测结果。要执行封锁，加 -Apply 参数；要演练，加 -Apply -DryRun。" 'OK'
    Say ("完整日志: " + $Log)
    exit 0
}

# ── 执行封锁 ──
$WorkDir = Join-Path $Base '.work'
New-Item -ItemType Directory -Path $WorkDir -Force | Out-Null
$BackupDir = Join-Path $Base ("portable-backup-" + $Stamp)
New-Item -ItemType Directory -Path $BackupDir -Force | Out-Null

Step "0. 备份现场"
$manifest = [pscustomobject]@{
    CreatedAt = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    User      = $Me
    Files     = @()
    Launcher  = $null
    OriginalHash = $null
    Tasks     = @()
    Services  = @()
}
foreach ($l in $layouts) {
    $cands = @(
        $l.LauncherRoot, ($l.LauncherRoot -replace '\.exe$', '.real.exe'),
        $l.LauncherO6,
        (Join-Path $l.Wtoolex 'wpsupdate.exe'),
        (Join-Path $l.Wtoolex 'updateself.exe'),
        (Join-Path $l.Wtoolex 'updatepolicy.exe'),
        $l.RootDir
    ) | Where-Object { $_ -and (Test-Path $_) }
    $manifest.Files = @($manifest.Files) + $cands
    # 首次运行才记录"原版"哈希；real 已存在说明之前锁过，直接用 real 的值
    if (Test-Path $l.LauncherRoot) {
        if (Test-Path ($l.LauncherRoot -replace '\.exe$', '.real.exe')) {
            $manifest.OriginalHash = (Get-FileHash ($l.LauncherRoot -replace '\.exe$', '.real.exe') -Algorithm SHA256).Hash
            $manifest.Launcher = $null    # 已是锁定状态，无需再换
        } else {
            $manifest.OriginalHash = (Get-FileHash $l.LauncherRoot -Algorithm SHA256).Hash
            $manifest.Launcher = $l.LauncherRoot
        }
    }
}
$manifest.Files | ForEach-Object { Say ("  备份关注: " + $_) 'INFO' }
icacls ($manifest.Files) 2>&1 | Out-File (Join-Path $BackupDir 'acl-before.txt') -Encoding UTF8
if ($manifest.OriginalHash) { Say ("  原版哈希基线: " + $manifest.OriginalHash) 'OK' }

$taskXmls = @()
foreach ($t in $tasks) {
    try {
        $xml = Export-ScheduledTask -TaskName $t.TaskName -TaskPath $t.TaskPath -ErrorAction Stop
        if ($xml) { Set-Content (Join-Path $BackupDir ($t.TaskName + '.xml')) $xml -Encoding UTF8; $taskXmls += $t.TaskName; Say ("  备份任务: " + $t.TaskName) 'OK' }
    } catch { Say ("  备份任务 " + $t.TaskName + " 失败（可能是权限限制）: " + $_.Exception.Message) 'WARN' }
}
$manifest.Services = @($svcs | ForEach-Object { [pscustomobject]@{ Name = $_.Name; OriginalStartMode = $(switch ($_.StartMode) { 'Auto' { 'auto' } 'Manual' { 'demand' } 'Disabled' { 'disabled' } default { 'demand' } }) } })
$manifest | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $BackupDir 'manifest.json') -Encoding UTF8

if ($DryRun) {
    Step "演练结束"
    Say "以上为将要执行的动作。去掉 -DryRun 即真正执行。" 'OK'
    exit 0
}

Step "1. 结束 WPS 进程"
if ($SkipKill) { Say "已跳过" 'WARN' }
else {
    $names = @('wps','wpsoffice','wpscloudsvr','wpscloudlaunch','wpscenter','wpslingxi','ksolaunch','wpsupdate','updateself','promecefpluginhost')
    $n = 0
    Get-Process -Name $names -ErrorAction SilentlyContinue | ForEach-Object { try { Stop-Process -Id $_.Id -Force -ErrorAction Stop; $n++ } catch {} }
    Say ("  已结束 $n 个进程") 'OK'
}

Step "2. 禁用并删除 WPS 计划任务"
foreach ($t in $tasks) {
    try { Disable-ScheduledTask -TaskName $t.TaskName -TaskPath $t.TaskPath -ErrorAction Stop | Out-Null; Say ("  已禁用: " + $t.TaskName) 'OK' } catch { Say ("  禁用 " + $t.TaskName + " 失败: " + $_.Exception.Message) 'WARN' }
    try { Unregister-ScheduledTask -TaskName $t.TaskName -TaskPath $t.TaskPath -Confirm:$false -ErrorAction Stop; Say ("  已删除: " + $t.TaskName) 'OK' } catch { Say ("  删除 " + $t.TaskName + " 需管理员或 DACL 受限，已保留禁用状态") 'WARN' }
}

Step "3. 清扫 Run / RunOnce / 启动文件夹"
foreach ($k in @(
    'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run',
    'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce',
    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run',
    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce',
    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run'
)) {
    if (-not (Test-Path $k)) { continue }
    (Get-ItemProperty $k).PSObject.Properties |
        Where-Object { $_.Name -notmatch '^PS' -and "$($_.Value)" -match 'wps|kingsoft|ksolaunch' } |
        ForEach-Object {
            try { Remove-ItemProperty $k -Name $_.Name -Force -ErrorAction Stop; Say ("  已删 " + $k + " :: " + $_.Name) 'OK' } catch { Say ("  删除 " + $_.Name + " 失败") 'WARN' }
        }
}
Say "  完成" 'OK'

Step "4. 换守卫壳（仅当存在根启动器）"
foreach ($l in $layouts) {
    if ($manifest.Launcher -ne $l.LauncherRoot) { Say ("  " + $l.LauncherRoot + " 已是锁定状态或不存在，跳过") 'INFO'; continue }
    $shim = New-GuardShim -SourceExe $l.LauncherRoot -WorkDir $WorkDir
    if (-not $shim) { Say "  守卫壳编译失败，跳过" 'ERR'; $Fail++; continue }
    icacls $l.RootDir /remove:d "$Me" 2>&1 | Out-Null
    icacls $l.LauncherRoot /remove:d "$Me" 2>&1 | Out-Null
    $real = $l.LauncherRoot -replace '\.exe$', '.real.exe'
    Move-Item $l.LauncherRoot $real -Force -ErrorAction SilentlyContinue
    Copy-Item $shim $l.LauncherRoot -Force -ErrorAction SilentlyContinue
    Say ("  守卫壳已落位: " + $l.LauncherRoot) 'OK'
}

Step "5. 文件级 DENY (X,WD,AD,WEA,WA,DE)"
$lockExec = @()
foreach ($l in $layouts) {
    $lockExec += $l.LauncherO6
    $lockExec += @('wpsupdate.exe','updateself.exe','updatepolicy.exe') | ForEach-Object { Join-Path $l.Wtoolex $_ }
}
$lockExec = $lockExec | Where-Object { $_ -and (Test-Path $_) }
foreach ($f in $lockExec) {
    icacls $f /deny "${Me}:(X,WD,AD,WEA,WA,DE)" 2>&1 | Out-Null
    $okay = (icacls $f 2>&1 | Out-String) -match 'DENY'
    Say ("  " + (Split-Path $f -Leaf) + " @ " + (Split-Path (Split-Path $f -Parent) -Leaf)) $(if ($okay) { 'OK' } else { 'ERR' })
    if (-not $okay) { $Fail++ }
}
foreach ($l in $layouts) {
    foreach ($f in @($l.LauncherRoot, ($l.LauncherRoot -replace '\.exe$', '.real.exe'))) {
        if (-not (Test-Path $f)) { continue }
        icacls $f /deny "${Me}:(WD,AD,WEA,WA,DE)" 2>&1 | Out-Null
        Say ("  " + (Split-Path $f -Leaf) + " 可执行但禁改禁删") 'OK'
    }
}

Step "6. 目录级 DENY (WD,AD)"
foreach ($l in $layouts) {
    foreach ($d in @($l.RootDir, (Join-Path $env:LOCALAPPDATA 'Kingsoft\WPS Cloud Files\update')) | Where-Object { $_ -and (Test-Path $_) }) {
        icacls $d /deny "${Me}:(WD,AD)" 2>&1 | Out-Null
        Say ("  " + $d) 'OK'
    }
}

Step "7. 服务启动类型改为 Disabled"
foreach ($s in $svcs) {
    if ($isAdmin) {
        sc.exe config $s.Name start= disabled 2>&1 | Out-Null
        sc.exe stop $s.Name 2>&1 | Out-Null
        Say ("  " + $s.Name + " -> Disabled") 'OK'
    } else {
        Say ("  " + $s.Name + " 需要管理员，跳过（手动: sc.exe config " + $s.Name + " start= disabled）") 'WARN'
    }
}

Step "8. 复核"
$leftTasks = @(Get-WpsTasks)
Say ("  剩余 WPS 任务: " + $leftTasks.Count) $(if ($leftTasks.Count -eq 0) { 'OK' } else { 'WARN' })
$leftDeny = 0
foreach ($f in $lockExec) { if ((icacls $f 2>&1 | Out-String) -match 'DENY') { $leftDeny++ } }
Say ("  载荷锁: $leftDeny / " + $lockExec.Count) $(if ($leftDeny -eq $lockExec.Count) { 'OK' } else { 'ERR' })

Write-Host ""
if ($Fail -eq 0) { Say "完成，0 失败项。" 'OK' } else { Say ("完成，$Fail 个失败项，见日志。") 'WARN' }
Say ("备份: " + $BackupDir)
Say ("日志: " + $Log)
Say "回滚: .\WPS-Autostart-Portable.ps1 -Restore"
