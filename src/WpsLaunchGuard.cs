using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

internal static class WpsLaunchGuard
{
    // 判定方式：按空白切分参数，逐 token 精确比对（去掉前导 - 或 / 后比较）。
    // 绝不用"子串包含"—— 已经踩过三次：
    //   /prometheus            正常启动标志，曾被误拦
    //   from=taskbar           任务栏图标，曾被 from=task 的子串匹配误拦
    // 精确 token 比对可以彻底避免这类误伤。
    private static readonly string[] BlockedCores =
    {
        "wpsupdate",
        "createtask",
        "repairtask",
        "from=task",
        "source=task"
    };

    private static bool IsBlocked(string argsJoined)
    {
        if (string.IsNullOrEmpty(argsJoined)) return false;

        foreach (var raw in argsJoined.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string core = raw.TrimStart('-', '/').ToLowerInvariant();

            foreach (var b in BlockedCores)
                if (core == b) return true;

            // /from=autostart_after_install 这类：安装器装完的自我拉起
            if (core.Contains("autostart")) return true;
        }
        return false;
    }

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
            var fi = new FileInfo(p);
            if (fi.Exists && fi.Length > 262144) fi.Delete();
            File.AppendAllText(p,
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " | " + msg + Environment.NewLine,
                Encoding.UTF8);
        }
        catch { }
    }

    private static int Main(string[] args)
    {
        var sb = new StringBuilder();
        foreach (var a in args) sb.Append(a).Append(' ');
        string joined = sb.ToString().Trim();

        if (IsBlocked(joined))
        {
            Log("BLOCKED  args=[" + joined + "]");
            return 0;   // 静默丢弃更新/自启请求
        }

        string dir = null;
        try { dir = Path.GetDirectoryName(typeof(WpsLaunchGuard).Assembly.Location); }
        catch { }

        string target = ResolvePortal(dir);
        if (target == null)
        {
            Log("NO-TARGET args=[" + joined + "] dir=[" + dir + "]");
            return 0;
        }

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
            string best = null;
            Version bestVer = null;
            foreach (var d in Directory.GetDirectories(root))
            {
                var name = Path.GetFileName(d);
                if (!Regex.IsMatch(name, @"^\d+(\.\d+)+$")) continue;
                Version v;
                if (!Version.TryParse(name, out v)) continue;

                string[] cands =
                {
                    Path.Combine(d, "office6", "wpsoffice.exe"),
                    Path.Combine(d, "office6", "wps.exe")
                };
                foreach (var c in cands)
                {
                    if (!File.Exists(c)) continue;
                    if (bestVer == null || v > bestVer) { bestVer = v; best = c; }
                    break;
                }
            }
            if (best != null) return best;

            // 最后一次回退：原版启动器
            var real = Path.Combine(root, "ksolaunch.real.exe");
            if (File.Exists(real)) return real;
        }
        catch { }
        return null;
    }
}
