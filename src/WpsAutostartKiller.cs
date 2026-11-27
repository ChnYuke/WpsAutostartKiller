// ============================================================================
//  WpsAutostartKiller.cs  —  WPS 开机自启关闭器（单文件 WinForms）
//  编译：csc /target:winexe /platform:anycpu /optimize+ /codepage:65001
//              /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.ServiceProcess.dll
//              /win32icon:assets\wps.ico /out:WpsAutostartKiller.exe WpsAutostartKiller.cs
//  自检：WpsAutostartKiller.exe --selftest       （无窗口，结果打到 stdout，退出码 0）
//
//  三条硬规则（都来自实测事故，改动前请先读）：
//    1) ACL 必须用精确权限位：文件 (X,WD,AD,WEA,WA,DE)，目录 (WD,AD)。
//       绝不能用简写 W —— 它是泛型写权限，会连带拒绝 READ_CONTROL，
//       实测后果是文件读不出来、WPS 起不来。
//    2) 守卫壳参数判定必须 token 精确匹配（按空白切分、去前导 - 或 /、逐 token 比对）。
//       绝不能用子串包含 —— 实测 /prometheus 被误拦导致图标点不动；
//       from=task 是 from=taskbar 的子串，导致任务栏图标失灵。
//    3) 替换主入口前必须先 icacls /remove:d 掉目录和文件的 DENY，否则重命名报 Access denied。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WpsAutostartKiller
{
    // ────────────────────────────────────────────────────────────────────
    //  入口
    // ────────────────────────────────────────────────────────────────────
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args != null && args.Length > 0)
            {
                string a0 = (args[0] ?? "").Trim().ToLowerInvariant();
                if (a0 == "--selftest" || a0 == "/selftest" || a0 == "-selftest")
                {
                    return SelfTest.Run();
                }
                if (a0 == "--backup-only" || a0 == "/backup-only")
                {
                    return SelfTest.RunBackupOnly();
                }
                if (a0 == "--upgrade-test" || a0 == "/upgrade-test")
                {
                    return SelfTest.RunUpgradeTest();
                }
                if (a0 == "--upgrade-state" || a0 == "/upgrade-state")
                {
                    return SelfTest.RunUpgradeState();
                }
                if (a0 == "--upgrade-disable" || a0 == "/upgrade-disable")
                {
                    return SelfTest.RunUpgradeDisable();
                }
                if (a0 == "--upgrade-restore" || a0 == "/upgrade-restore")
                {
                    return SelfTest.RunUpgradeRestore();
                }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
            {
                try { MessageBox.Show(e.Exception.Message, "未处理的异常", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                catch { }
            };
            Application.Run(new MainForm());
            return 0;
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  路径常量
    // ────────────────────────────────────────────────────────────────────
    internal static class Paths
    {
        public static string ExeDir
        {
            get
            {
                try
                {
                    return Path.GetDirectoryName(Application.ExecutablePath);
                }
                catch
                {
                    return Environment.CurrentDirectory;
                }
            }
        }

        public static string WorkDir { get { return Path.Combine(ExeDir, ".work"); } }

        public static string LogFile
        {
            get { return Path.Combine(ExeDir, "killer-" + DateTime.Now.ToString("yyyyMMdd") + ".log"); }
        }

        public static string LocalAppData
        {
            get { return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); }
        }

        public static string AppData
        {
            get { return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData); }
        }

        public static string GuardLog
        {
            get { return Path.Combine(LocalAppData, @"Kingsoft\wps-guard.log"); }
        }

        public static string ProgramFilesX86
        {
            get
            {
                string p = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
                return string.IsNullOrEmpty(p) ? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) : p;
            }
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  进程调用
    // ────────────────────────────────────────────────────────────────────
    internal static class Shell
    {
        public static int Run(string file, string args, out string stdout, out string stderr)
        {
            return Run(file, args, -1, out stdout, out stderr);
        }

        public static int Run(string file, string args, int timeoutMs, out string stdout, out string stderr)
        {
            stdout = "";
            stderr = "";
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(file, args);
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;
                try { psi.StandardOutputEncoding = Encoding.Default; } catch { }
                try { psi.StandardErrorEncoding = Encoding.Default; } catch { }

                using (Process p = Process.Start(psi))
                {
                    if (p == null) { stderr = "启动失败"; return -1; }
                    string errBuf = "";
                    Thread t = new Thread(delegate()
                    {
                        try { errBuf = p.StandardError.ReadToEnd(); }
                        catch { }
                    });
                    t.IsBackground = true;
                    t.Start();

                    try { stdout = p.StandardOutput.ReadToEnd(); }
                    catch { }

                    if (timeoutMs > 0)
                    {
                        if (!p.WaitForExit(timeoutMs))
                        {
                            try { p.Kill(); } catch { }
                            stderr = "超时";
                            return -1;
                        }
                    }
                    else
                    {
                        p.WaitForExit();
                    }
                    t.Join(3000);
                    stderr = errBuf;
                    return p.HasExited ? p.ExitCode : -1;
                }
            }
            catch (Exception ex)
            {
                stderr = ex.Message;
                return -1;
            }
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  ACL（精确权限位）
    // ────────────────────────────────────────────────────────────────────
    internal static class Acl
    {
        // 文件：禁执行 + 禁写 + 禁删，但保留读（保留 READ_CONTROL 才不会把文件读废）
        public const string FileDeny = "(X,WD,AD,WEA,WA,DE)";
        // 可执行但禁改禁删（守卫壳本身、原版备份）
        public const string FileKeepExec = "(WD,AD,WEA,WA,DE)";
        // 目录：禁止新建子项（阻断铺新版本目录）
        public const string DirDeny = "(WD,AD)";

        public static string DomainUser
        {
            get { return Environment.UserDomainName + "\\" + Environment.UserName; }
        }

        public static bool HasDeny(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path) && !Directory.Exists(path)) return false;
            string o, e;
            Shell.Run("icacls.exe", Quote(path), out o, out e);
            string all = (o ?? "") + (e ?? "");
            return all.IndexOf("DENY", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool RemoveDeny(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (!File.Exists(path) && !Directory.Exists(path)) return false;
            string o, e;
            Shell.Run("icacls.exe", Quote(path) + " /remove:d " + Quote(DomainUser), out o, out e);
            Shell.Run("icacls.exe", Quote(path) + " /remove:d " + Quote(Environment.UserName), out o, out e);
            return !HasDeny(path);
        }

        public static bool Deny(string path, string rights)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (!File.Exists(path) && !Directory.Exists(path)) return false;
            string o, e;
            // 用户标识用 DOMAIN\USERNAME，权限串整体加双引号
            Shell.Run("icacls.exe", Quote(path) + " /deny " + Quote(DomainUser + ":" + rights), out o, out e);
            return HasDeny(path);
        }

        public static string Snapshot(IEnumerable<string> paths)
        {
            StringBuilder sb = new StringBuilder();
            foreach (string p in paths)
            {
                if (string.IsNullOrEmpty(p)) continue;
                if (!File.Exists(p) && !Directory.Exists(p)) { sb.AppendLine(p + "  [不存在]"); continue; }
                string o, e;
                Shell.Run("icacls.exe", Quote(p), out o, out e);
                sb.AppendLine("--- " + p);
                sb.AppendLine((o ?? "") + (e ?? ""));
            }
            return sb.ToString();
        }

        private static string Quote(string s)
        {
            return "\"" + (s ?? "") + "\"";
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  服务
    // ────────────────────────────────────────────────────────────────────
    internal static class Svc
    {
        public const string Name = "wpscloudsvr";

        public static bool Exists
        {
            get
            {
                // 只用 sc.exe qc 判断存在性：未安装时返回 Unknown，避免依赖 ServiceController 的枚举类型名
                string m = StartMode;
                return !string.Equals(m, "Unknown", StringComparison.Ordinal);
            }
        }

        public static string State
        {
            get
            {
                try { using (ServiceController sc = new ServiceController(Name)) { return sc.Status.ToString(); } }
                catch { return "未安装"; }
            }
        }

        public static string StartMode
        {
            get
            {
                string o, e;
                Shell.Run("sc.exe", "qc " + Name, out o, out e);
                string all = ((o ?? "") + (e ?? "")).ToUpperInvariant();
                if (all.IndexOf("DISABLED", StringComparison.Ordinal) >= 0) return "Disabled";
                if (all.IndexOf("AUTO_START", StringComparison.Ordinal) >= 0) return "Auto";
                if (all.IndexOf("DEMAND_START", StringComparison.Ordinal) >= 0) return "Manual";
                return "Unknown";
            }
        }

        public static bool SetStart(string mode)
        {
            string o, e;
            Shell.Run("sc.exe", "config " + Name + " start= " + mode, out o, out e);
            return true;
        }

        public static void Stop()
        {
            string o, e;
            Shell.Run("sc.exe", "stop " + Name, out o, out e);
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  计划任务（走 schtasks，避免 COM 与本地化 header 依赖）
    // ────────────────────────────────────────────────────────────────────
    internal sealed class WpsTask
    {
        public string Name;
        public string Raw;
        public string Exec;

        public override string ToString()
        {
            return Name + (string.IsNullOrEmpty(Exec) ? "" : "  ->  " + Exec);
        }
    }

    internal static class Tasks
    {
        public static List<WpsTask> Find()
        {
            List<WpsTask> list = new List<WpsTask>();
            string o, e;
            int rc = Shell.Run("schtasks.exe", "/query /fo CSV /v /nh", out o, out e);
            if (rc != 0 || string.IsNullOrEmpty(o))
            {
                Shell.Run("schtasks.exe", "/query /fo CSV /v", out o, out e);
            }
            if (string.IsNullOrEmpty(o)) return list;

            string[] lines = o.Replace("\r", "").Split('\n');
            Dictionary<string, bool> seen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            foreach (string rawLine in lines)
            {
                string line = (rawLine ?? "").Trim();
                if (line.Length < 3) continue;
                string low = line.ToLowerInvariant();
                if (low.IndexOf("ksolaunch", StringComparison.Ordinal) < 0 &&
                    low.IndexOf("wpsupdate", StringComparison.Ordinal) < 0 &&
                    low.IndexOf("kingsoft", StringComparison.Ordinal) < 0) continue;

                Match m = Regex.Match(line, "^\"([^\"]*)\"");
                string name = m.Success ? m.Groups[1].Value : line.Split(',')[0].Trim('"');
                name = name.Trim().TrimStart('\\');
                if (name.Length == 0) continue;
                if (seen.ContainsKey(name)) continue;
                seen[name] = true;

                WpsTask t = new WpsTask();
                t.Name = name;
                t.Raw = line.Length > 400 ? line.Substring(0, 400) : line;
                Match em = Regex.Match(line, "([A-Za-z]:\\\\[^\"',]*?\\.exe)", RegexOptions.IgnoreCase);
                t.Exec = em.Success ? em.Groups[1].Value : "";
                list.Add(t);
            }
            return list;
        }

        public static bool Disable(string name)
        {
            string o, e;
            int rc = Shell.Run("schtasks.exe", "/change /tn " + Q("\\" + name) + " /disable", out o, out e);
            return rc == 0;
        }

        public static bool Delete(string name)
        {
            string o, e;
            int rc = Shell.Run("schtasks.exe", "/delete /tn " + Q("\\" + name) + " /f", out o, out e);
            return rc == 0;
        }

        public static string ExportXml(string name)
        {
            string o, e;
            int rc = Shell.Run("schtasks.exe", "/query /xml ONE /tn " + Q("\\" + name), out o, out e);
            if (rc != 0) return null;
            int i = o.IndexOf("<?xml", StringComparison.Ordinal);
            if (i < 0) return null;
            return o.Substring(i).Trim();
        }

        public static bool CreateFromXml(string name, string xmlFile)
        {
            string o, e;
            int rc = Shell.Run("schtasks.exe", "/create /tn " + Q("\\" + name) + " /xml " + Q(xmlFile) + " /f", out o, out e);
            return rc == 0;
        }

        private static string Q(string s) { return "\"" + s + "\""; }
    }

    // ────────────────────────────────────────────────────────────────────
    //  安装布局探测
    // ────────────────────────────────────────────────────────────────────
    internal sealed class WpsLayout
    {
        public string VersionDir;
        public string RootDir;
        public string Office6;
        public string Wtoolex;
        public string LauncherRoot;
        public string LauncherReal;
        public string LauncherO6;
        public string Portal;
        public string WpsExe;
        public bool MachineWide;
        public string FoundBy;

        public bool HasOffice6 { get { return DirOk(Office6); } }
        public bool HasWtoolex { get { return DirOk(Wtoolex); } }
        public bool HasLauncherRoot { get { return FileOk(LauncherRoot); } }
        public bool HasPortal { get { return FileOk(Portal); } }

        private static bool DirOk(string p) { return !string.IsNullOrEmpty(p) && Directory.Exists(p); }
        private static bool FileOk(string p) { return !string.IsNullOrEmpty(p) && File.Exists(p); }
    }

    internal static class WpsProbe
    {
        public static WpsLayout Find()
        {
            List<string> versionDirs = new List<string>();
            List<string> foundBy = new List<string>();

            // ① 注册表 InstallRoot（最准，能覆盖自定义安装路径）
            string[][] regHits = new string[][]
            {
                new string[] { "HKLM", @"SOFTWARE\Kingsoft\Office\6.0\common" },
                new string[] { "HKLM", @"SOFTWARE\WOW6432Node\Kingsoft\Office\6.0\common" },
                new string[] { "HKCU", @"SOFTWARE\Kingsoft\Office\6.0\Common" },
                new string[] { "HKCU", @"SOFTWARE\Kingsoft\Office\6.0\common" }
            };
            foreach (string[] hit in regHits)
            {
                RegistryKey root = hit[0] == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
                try
                {
                    using (RegistryKey k = root.OpenSubKey(hit[1]))
                    {
                        if (k == null) continue;
                        string ir = k.GetValue("InstallRoot") as string;
                        if (!string.IsNullOrEmpty(ir) && Directory.Exists(ir))
                        {
                            if (!versionDirs.Contains(ir)) { versionDirs.Add(ir); foundBy.Add("registry:" + hit[0] + "\\" + hit[1]); }
                        }
                    }
                }
                catch { }
            }

            // ② App Paths\wps.exe → office6 的父目录
            string[][] appPaths = new string[][]
            {
                new string[] { "HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\wps.exe" },
                new string[] { "HKLM", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\wps.exe" },
                new string[] { "HKCU", @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\wps.exe" }
            };
            foreach (string[] hit in appPaths)
            {
                RegistryKey root = hit[0] == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
                try
                {
                    using (RegistryKey k = root.OpenSubKey(hit[1]))
                    {
                        if (k == null) continue;
                        string exe = k.GetValue("") as string;
                        if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) continue;
                        string office6 = Path.GetDirectoryName(exe);
                        if (office6 != null && string.Equals(Path.GetFileName(office6), "office6", StringComparison.OrdinalIgnoreCase))
                        {
                            string vd = Path.GetDirectoryName(office6);
                            if (vd != null && !versionDirs.Contains(vd)) { versionDirs.Add(vd); foundBy.Add("apppath:" + hit[1]); }
                        }
                    }
                }
                catch { }
            }

            // ③ 兜底：扫常见根目录下的 *\office6\wpsoffice.exe
            string[] seeds = new string[]
            {
                Path.Combine(Paths.LocalAppData, "Kingsoft"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Kingsoft"),
                Path.Combine(Paths.ProgramFilesX86, "Kingsoft")
            };
            foreach (string seed in seeds)
            {
                if (!Directory.Exists(seed)) continue;
                try
                {
                    foreach (string d1 in Directory.GetDirectories(seed))
                    {
                        foreach (string d2 in Directory.GetDirectories(d1))
                        {
                            if (File.Exists(Path.Combine(d2, @"office6\wpsoffice.exe")))
                            {
                                if (!versionDirs.Contains(d2)) { versionDirs.Add(d2); foundBy.Add("scan:" + seed); }
                            }
                        }
                    }
                    // 也考虑 …\Kingsoft\office6 这种没有版本层的布局
                    foreach (string d1 in Directory.GetDirectories(seed))
                    {
                        if (string.Equals(Path.GetFileName(d1), "office6", StringComparison.OrdinalIgnoreCase))
                        {
                            string parent = Path.GetDirectoryName(d1);
                            if (parent != null && !versionDirs.Contains(parent)) { versionDirs.Add(parent); foundBy.Add("scan-flat:" + seed); }
                        }
                    }
                }
                catch { }
            }

            WpsLayout best = null;
            Version bestVer = null;
            int idx = -1;

            foreach (string vd in versionDirs)
            {
                idx++;
                string office6 = Path.Combine(vd, "office6");
                if (!Directory.Exists(office6)) continue;

                string rootDir = Path.GetDirectoryName(vd);
                WpsLayout l = new WpsLayout();
                l.VersionDir = vd;
                l.RootDir = rootDir;
                l.Office6 = office6;
                l.Wtoolex = Path.Combine(vd, "wtoolex");
                l.LauncherRoot = Path.Combine(rootDir, "ksolaunch.exe");
                l.LauncherReal = Path.Combine(rootDir, "ksolaunch.real.exe");
                l.LauncherO6 = Path.Combine(office6, "ksolaunch.exe");
                l.Portal = Path.Combine(office6, "wpsoffice.exe");
                l.WpsExe = Path.Combine(office6, "wps.exe");
                l.FoundBy = idx < foundBy.Count ? foundBy[idx] : "unknown";

                string rd = rootDir.ToLowerInvariant();
                l.MachineWide = rd.StartsWith(@"c:\program files") || rd.Contains(@"\program files");

                Version v = null;
                Version.TryParse(Path.GetFileName(vd), out v);
                if (best == null || (v != null && (bestVer == null || v > bestVer)))
                {
                    best = l;
                    bestVer = v;
                }
            }
            return best;
        }

        public static bool IsShimInstalled(WpsLayout l)
        {
            if (l == null) return false;
            if (!File.Exists(l.LauncherRoot)) return false;
            if (!File.Exists(l.LauncherReal)) return false;
            try
            {
                string h1 = Sha256(l.LauncherRoot);
                string h2 = Sha256(l.LauncherReal);
                return !string.Equals(h1, h2, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        public static string Sha256(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream fs = File.OpenRead(path))
            {
                byte[] hash = sha.ComputeHash(fs);
                StringBuilder sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  图标提取 → 多尺寸 ICO（供 /win32icon 使用）
    // ────────────────────────────────────────────────────────────────────
    internal static class IconBuilder
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint PrivateExtractIcons(string szFileName, int nIconIndex, int cxIcon, int cyIcon,
            IntPtr[] phicon, uint[] piconid, uint nIcons, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        public static bool FromExe(string exe, string icoOut)
        {
            if (!File.Exists(exe)) return false;
            int[] sizes = new int[] { 16, 32, 48, 256 };
            List<byte[]> blobs = new List<byte[]>();
            List<int> usedSizes = new List<int>();

            foreach (int s in sizes)
            {
                IntPtr[] h = new IntPtr[1];
                uint[] ids = new uint[1];
                uint n = 0;
                try { n = PrivateExtractIcons(exe, 0, s, s, h, ids, 1, 0); }
                catch { continue; }
                if (n < 1 || h[0] == IntPtr.Zero) continue;
                try
                {
                    using (Icon ic = Icon.FromHandle(h[0]))
                    using (Bitmap src = ic.ToBitmap())
                    using (Bitmap rs = new Bitmap(s, s, PixelFormat.Format32bppArgb))
                    {
                        using (Graphics g = Graphics.FromImage(rs))
                        {
                            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                            g.DrawImage(src, 0, 0, s, s);
                        }
                        blobs.Add(BuildDib(rs, s));
                        usedSizes.Add(s);
                    }
                }
                catch { }
                finally { try { DestroyIcon(h[0]); } catch { } }
            }

            if (blobs.Count == 0) return false;

            using (FileStream fs = new FileStream(icoOut, FileMode.Create, FileAccess.Write))
            using (BinaryWriter w = new BinaryWriter(fs))
            {
                w.Write((ushort)0);
                w.Write((ushort)1);
                w.Write((ushort)blobs.Count);
                int offset = 6 + 16 * blobs.Count;
                for (int i = 0; i < blobs.Count; i++)
                {
                    int d = usedSizes[i] >= 256 ? 0 : usedSizes[i];
                    w.Write((byte)d);
                    w.Write((byte)d);
                    w.Write((byte)0);
                    w.Write((byte)0);
                    w.Write((ushort)1);
                    w.Write((ushort)32);
                    w.Write((uint)blobs[i].Length);
                    w.Write((uint)offset);
                    offset += blobs[i].Length;
                }
                foreach (byte[] b in blobs) w.Write(b);
            }
            return true;
        }

        private static byte[] BuildDib(Bitmap bmp, int size)
        {
            int xorSize = size * size * 4;
            int andRow = (int)Math.Ceiling(size / 8.0);
            andRow += (4 - (andRow % 4)) % 4;
            int andSize = andRow * size;

            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write((uint)40);
                w.Write((int)size);
                w.Write((int)(size * 2));
                w.Write((ushort)1);
                w.Write((ushort)32);
                w.Write((uint)0);
                w.Write((uint)(xorSize + andSize));
                w.Write((int)0);
                w.Write((int)0);
                w.Write((uint)0);
                w.Write((uint)0);

                for (int y = size - 1; y >= 0; y--)
                {
                    for (int x = 0; x < size; x++)
                    {
                        Color c = bmp.GetPixel(x, y);
                        w.Write(c.B);
                        w.Write(c.G);
                        w.Write(c.R);
                        w.Write(c.A);
                    }
                }
                w.Write(new byte[andSize]);
                w.Flush();
                return ms.ToArray();
            }
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  守卫壳
    // ────────────────────────────────────────────────────────────────────
    internal static class Guard
    {
        // assets\WpsLaunchGuard.cs 缺失时的兜底（base64 内嵌，内容与 assets 版本一致）
        private const string GuardSourceB64 = "dXNpbmcgU3lzdGVtOwp1c2luZyBTeXN0ZW0uRGlhZ25vc3RpY3M7CnVzaW5nIFN5c3RlbS5JTzsKdXNpbmcgU3lzdGVtLlRleHQ7CnVzaW5nIFN5c3RlbS5UZXh0LlJlZ3VsYXJFeHByZXNzaW9uczsKCmludGVybmFsIHN0YXRpYyBjbGFzcyBXcHNMYXVuY2hHdWFyZAp7CiAgICAvLyDliKTlrprmlrnlvI/vvJrmjInnqbrnmb3liIfliIblj4LmlbDvvIzpgJAgdG9rZW4g57K+56Gu5q+U5a+577yI5Y675o6J5YmN5a+8IC0g5oiWIC8g5ZCO5q+U6L6D77yJ44CCCiAgICAvLyDnu53kuI3nlKgi5a2Q5Liy5YyF5ZCrIuKAlOKAlCDlt7Lnu4/ouKnov4fkuInmrKHvvJoKICAgIC8vICAgL3Byb21ldGhldXMgICAgICAgICAgICDmraPluLjlkK/liqjmoIflv5fvvIzmm77ooqvor6/mi6YKICAgIC8vICAgZnJvbT10YXNrYmFyICAgICAgICAgICDku7vliqHmoI/lm77moIfvvIzmm77ooqsgZnJvbT10YXNrIOeahOWtkOS4suWMuemFjeivr+aLpgogICAgLy8g57K+56GuIHRva2VuIOavlOWvueWPr+S7peW9u+W6lemBv+WFjei/meexu+ivr+S8pOOAggogICAgcHJpdmF0ZSBzdGF0aWMgcmVhZG9ubHkgc3RyaW5nW10gQmxvY2tlZENvcmVzID0KICAgIHsKICAgICAgICAid3BzdXBkYXRlIiwKICAgICAgICAiY3JlYXRldGFzayIsCiAgICAgICAgInJlcGFpcnRhc2siLAogICAgICAgICJmcm9tPXRhc2siLAogICAgICAgICJzb3VyY2U9dGFzayIKICAgIH07CgogICAgcHJpdmF0ZSBzdGF0aWMgYm9vbCBJc0Jsb2NrZWQoc3RyaW5nIGFyZ3NKb2luZWQpCiAgICB7CiAgICAgICAgaWYgKHN0cmluZy5Jc051bGxPckVtcHR5KGFyZ3NKb2luZWQpKSByZXR1cm4gZmFsc2U7CgogICAgICAgIGZvcmVhY2ggKHZhciByYXcgaW4gYXJnc0pvaW5lZC5TcGxpdChuZXdbXSB7ICcgJywgJ1x0JywgJ1xyJywgJ1xuJyB9LCBTdHJpbmdTcGxpdE9wdGlvbnMuUmVtb3ZlRW1wdHlFbnRyaWVzKSkKICAgICAgICB7CiAgICAgICAgICAgIHN0cmluZyBjb3JlID0gcmF3LlRyaW1TdGFydCgnLScsICcvJykuVG9Mb3dlckludmFyaWFudCgpOwoKICAgICAgICAgICAgZm9yZWFjaCAodmFyIGIgaW4gQmxvY2tlZENvcmVzKQogICAgICAgICAgICAgICAgaWYgKGNvcmUgPT0gYikgcmV0dXJuIHRydWU7CgogICAgICAgICAgICAvLyAvZnJvbT1hdXRvc3RhcnRfYWZ0ZXJfaW5zdGFsbCDov5nnsbvvvJrlronoo4Xlmajoo4XlroznmoToh6rmiJHmi4notbcKICAgICAgICAgICAgaWYgKGNvcmUuQ29udGFpbnMoImF1dG9zdGFydCIpKSByZXR1cm4gdHJ1ZTsKICAgICAgICB9CiAgICAgICAgcmV0dXJuIGZhbHNlOwogICAgfQoKICAgIHByaXZhdGUgc3RhdGljIHN0cmluZyBMb2dQYXRoCiAgICB7CiAgICAgICAgZ2V0CiAgICAgICAgewogICAgICAgICAgICByZXR1cm4gUGF0aC5Db21iaW5lKAogICAgICAgICAgICAgICAgRW52aXJvbm1lbnQuR2V0Rm9sZGVyUGF0aChFbnZpcm9ubWVudC5TcGVjaWFsRm9sZGVyLkxvY2FsQXBwbGljYXRpb25EYXRhKSwKICAgICAgICAgICAgICAgIEAiS2luZ3NvZnRcd3BzLWd1YXJkLmxvZyIpOwogICAgICAgIH0KICAgIH0KCiAgICBwcml2YXRlIHN0YXRpYyB2b2lkIExvZyhzdHJpbmcgbXNnKQogICAgewogICAgICAgIHRyeQogICAgICAgIHsKICAgICAgICAgICAgc3RyaW5nIHAgPSBMb2dQYXRoOwogICAgICAgICAgICB2YXIgZmkgPSBuZXcgRmlsZUluZm8ocCk7CiAgICAgICAgICAgIGlmIChmaS5FeGlzdHMgJiYgZmkuTGVuZ3RoID4gMjYyMTQ0KSBmaS5EZWxldGUoKTsKICAgICAgICAgICAgRmlsZS5BcHBlbmRBbGxUZXh0KHAsCiAgICAgICAgICAgICAgICBEYXRlVGltZS5Ob3cuVG9TdHJpbmcoInl5eXktTU0tZGQgSEg6bW06c3MiKSArICIgfCAiICsgbXNnICsgRW52aXJvbm1lbnQuTmV3TGluZSwKICAgICAgICAgICAgICAgIEVuY29kaW5nLlVURjgpOwogICAgICAgIH0KICAgICAgICBjYXRjaCB7IH0KICAgIH0KCiAgICBwcml2YXRlIHN0YXRpYyBpbnQgTWFpbihzdHJpbmdbXSBhcmdzKQogICAgewogICAgICAgIHZhciBzYiA9IG5ldyBTdHJpbmdCdWlsZGVyKCk7CiAgICAgICAgZm9yZWFjaCAodmFyIGEgaW4gYXJncykgc2IuQXBwZW5kKGEpLkFwcGVuZCgnICcpOwogICAgICAgIHN0cmluZyBqb2luZWQgPSBzYi5Ub1N0cmluZygpLlRyaW0oKTsKCiAgICAgICAgaWYgKElzQmxvY2tlZChqb2luZWQpKQogICAgICAgIHsKICAgICAgICAgICAgTG9nKCJCTE9DS0VEICBhcmdzPVsiICsgam9pbmVkICsgIl0iKTsKICAgICAgICAgICAgcmV0dXJuIDA7ICAgLy8g6Z2Z6buY5Lii5byD5pu05pawL+iHquWQr+ivt+axggogICAgICAgIH0KCiAgICAgICAgc3RyaW5nIGRpciA9IG51bGw7CiAgICAgICAgdHJ5IHsgZGlyID0gUGF0aC5HZXREaXJlY3RvcnlOYW1lKHR5cGVvZihXcHNMYXVuY2hHdWFyZCkuQXNzZW1ibHkuTG9jYXRpb24pOyB9CiAgICAgICAgY2F0Y2ggeyB9CgogICAgICAgIHN0cmluZyB0YXJnZXQgPSBSZXNvbHZlUG9ydGFsKGRpcik7CiAgICAgICAgaWYgKHRhcmdldCA9PSBudWxsKQogICAgICAgIHsKICAgICAgICAgICAgTG9nKCJOTy1UQVJHRVQgYXJncz1bIiArIGpvaW5lZCArICJdIGRpcj1bIiArIGRpciArICJdIik7CiAgICAgICAgICAgIHJldHVybiAwOwogICAgICAgIH0KCiAgICAgICAgdHJ5CiAgICAgICAgewogICAgICAgICAgICB2YXIgcHNpID0gbmV3IFByb2Nlc3NTdGFydEluZm8odGFyZ2V0KQogICAgICAgICAgICB7CiAgICAgICAgICAgICAgICBVc2VTaGVsbEV4ZWN1dGUgPSBmYWxzZSwKICAgICAgICAgICAgICAgIFdvcmtpbmdEaXJlY3RvcnkgPSBQYXRoLkdldERpcmVjdG9yeU5hbWUodGFyZ2V0KSwKICAgICAgICAgICAgICAgIEFyZ3VtZW50cyA9IGpvaW5lZAogICAgICAgICAgICB9OwogICAgICAgICAgICBQcm9jZXNzLlN0YXJ0KHBzaSk7CiAgICAgICAgICAgIExvZygiTEFVTkNIICAgYXJncz1bIiArIGpvaW5lZCArICJdIC0+ICIgKyB0YXJnZXQpOwogICAgICAgIH0KICAgICAgICBjYXRjaCAoRXhjZXB0aW9uIGV4KQogICAgICAgIHsKICAgICAgICAgICAgTG9nKCJGQUlMICAgICBhcmdzPVsiICsgam9pbmVkICsgIl0gdGFyZ2V0PVsiICsgdGFyZ2V0ICsgIl0gZXJyPSIgKyBleC5NZXNzYWdlKTsKICAgICAgICB9CiAgICAgICAgcmV0dXJuIDA7CiAgICB9CgogICAgcHJpdmF0ZSBzdGF0aWMgc3RyaW5nIFJlc29sdmVQb3J0YWwoc3RyaW5nIHJvb3QpCiAgICB7CiAgICAgICAgaWYgKHN0cmluZy5Jc051bGxPckVtcHR5KHJvb3QpKSByZXR1cm4gbnVsbDsKICAgICAgICB0cnkKICAgICAgICB7CiAgICAgICAgICAgIHN0cmluZyBiZXN0ID0gbnVsbDsKICAgICAgICAgICAgVmVyc2lvbiBiZXN0VmVyID0gbnVsbDsKICAgICAgICAgICAgZm9yZWFjaCAodmFyIGQgaW4gRGlyZWN0b3J5LkdldERpcmVjdG9yaWVzKHJvb3QpKQogICAgICAgICAgICB7CiAgICAgICAgICAgICAgICB2YXIgbmFtZSA9IFBhdGguR2V0RmlsZU5hbWUoZCk7CiAgICAgICAgICAgICAgICBpZiAoIVJlZ2V4LklzTWF0Y2gobmFtZSwgQCJeXGQrKFwuXGQrKSskIikpIGNvbnRpbnVlOwogICAgICAgICAgICAgICAgVmVyc2lvbiB2OwogICAgICAgICAgICAgICAgaWYgKCFWZXJzaW9uLlRyeVBhcnNlKG5hbWUsIG91dCB2KSkgY29udGludWU7CgogICAgICAgICAgICAgICAgc3RyaW5nW10gY2FuZHMgPQogICAgICAgICAgICAgICAgewogICAgICAgICAgICAgICAgICAgIFBhdGguQ29tYmluZShkLCAib2ZmaWNlNiIsICJ3cHNvZmZpY2UuZXhlIiksCiAgICAgICAgICAgICAgICAgICAgUGF0aC5Db21iaW5lKGQsICJvZmZpY2U2IiwgIndwcy5leGUiKQogICAgICAgICAgICAgICAgfTsKICAgICAgICAgICAgICAgIGZvcmVhY2ggKHZhciBjIGluIGNhbmRzKQogICAgICAgICAgICAgICAgewogICAgICAgICAgICAgICAgICAgIGlmICghRmlsZS5FeGlzdHMoYykpIGNvbnRpbnVlOwogICAgICAgICAgICAgICAgICAgIGlmIChiZXN0VmVyID09IG51bGwgfHwgdiA+IGJlc3RWZXIpIHsgYmVzdFZlciA9IHY7IGJlc3QgPSBjOyB9CiAgICAgICAgICAgICAgICAgICAgYnJlYWs7CiAgICAgICAgICAgICAgICB9CiAgICAgICAgICAgIH0KICAgICAgICAgICAgaWYgKGJlc3QgIT0gbnVsbCkgcmV0dXJuIGJlc3Q7CgogICAgICAgICAgICAvLyDmnIDlkI7kuIDmrKHlm57pgIDvvJrljp/niYjlkK/liqjlmagKICAgICAgICAgICAgdmFyIHJlYWwgPSBQYXRoLkNvbWJpbmUocm9vdCwgImtzb2xhdW5jaC5yZWFsLmV4ZSIpOwogICAgICAgICAgICBpZiAoRmlsZS5FeGlzdHMocmVhbCkpIHJldHVybiByZWFsOwogICAgICAgIH0KICAgICAgICBjYXRjaCB7IH0KICAgICAgICByZXR1cm4gbnVsbDsKICAgIH0KfQ0K";

        public static string GetSource()
        {
            // 优先用 exe 旁边的 assets 版本（便于审阅与热修）
            string p = Path.Combine(Paths.ExeDir, @"assets\WpsLaunchGuard.cs");
            try
            {
                if (File.Exists(p))
                {
                    string s = File.ReadAllText(p, Encoding.UTF8);
                    if (s != null && s.IndexOf("BlockedCores", StringComparison.Ordinal) >= 0) return s;
                }
            }
            catch { }

            try { return Encoding.UTF8.GetString(Convert.FromBase64String(GuardSourceB64)); }
            catch { return null; }
        }

        public static string FindCsc()
        {
            string windir = Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows";
            string[] cands = new string[]
            {
                Path.Combine(windir, @"Microsoft.NET\Framework64\v4.0.30319\csc.exe"),
                Path.Combine(windir, @"Microsoft.NET\Framework\v4.0.30319\csc.exe")
            };
            foreach (string c in cands) if (File.Exists(c)) return c;
            return null;
        }

        /// <summary>编译守卫壳，返回 exe 路径；失败返回 null。</summary>
        public static string Compile(string originalLauncher, string logSink)
        {
            string csc = FindCsc();
            if (csc == null) { Emit(logSink, "未找到 csc.exe，无法编译守卫壳"); return null; }

            string src = GetSource();
            if (string.IsNullOrEmpty(src)) { Emit(logSink, "守卫壳源码不可用（assets 缺失且内嵌回退失败）"); return null; }

            if (!Directory.Exists(Paths.WorkDir)) Directory.CreateDirectory(Paths.WorkDir);
            string cs = Path.Combine(Paths.WorkDir, "WpsLaunchGuard.cs");
            File.WriteAllText(cs, src, new UTF8Encoding(false));

            string ico = Path.Combine(Paths.WorkDir, "wps.ico");
            if (!File.Exists(ico))
            {
                if (!IconBuilder.FromExe(originalLauncher, ico))
                {
                    string shipped = Path.Combine(Paths.ExeDir, @"assets\wps.ico");
                    if (File.Exists(shipped)) { try { File.Copy(shipped, ico, true); } catch { } }
                }
            }

            string outExe = Path.Combine(Paths.WorkDir, "ksolaunch.guard.exe");
            try { if (File.Exists(outExe)) File.Delete(outExe); } catch { }

            string args = "/nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 \"/out:" + outExe + "\" ";
            if (File.Exists(ico)) args += "\"/win32icon:" + ico + "\" ";
            args += "\"" + cs + "\"";

            string o, e;
            Shell.Run(csc, args, out o, out e);
            if (File.Exists(outExe))
            {
                Emit(logSink, "守卫壳编译成功：" + new FileInfo(outExe).Length + " 字节" + (File.Exists(ico) ? "（含图标）" : "（无图标！）"));
                return outExe;
            }
            Emit(logSink, "守卫壳编译失败：" + (o ?? "") + (e ?? ""));
            return null;
        }

        private static void Emit(string sink, string msg)
        {
            if (sink != null) { try { File.AppendAllText(sink, msg + Environment.NewLine, Encoding.UTF8); } catch { } }
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  极简 JSON 写出（避免额外引用）
    // ────────────────────────────────────────────────────────────────────
    internal sealed class Json
    {
        private readonly StringBuilder _sb = new StringBuilder();
        private bool _first = true;

        public void Open()
        {
            _sb.Append("{");
            _first = true;
        }

        public void Close()
        {
            _sb.Append("\n}");
        }

        public void Separator()
        {
            if (!_first) _sb.Append(",");
            _first = false;
            _sb.Append("\n  ");
        }

        public void Str(string key, string val)
        {
            Separator();
            _sb.Append("\"").Append(Escape(key)).Append("\": \"").Append(Escape(val)).Append("\"");
        }

        public void Bool(string key, bool val)
        {
            Separator();
            _sb.Append("\"").Append(Escape(key)).Append("\": ").Append(val ? "true" : "false");
        }

        public void Arr(string key, IEnumerable<string> vals)
        {
            Separator();
            _sb.Append("\"").Append(Escape(key)).Append("\": [");
            bool first = true;
            if (vals != null)
            {
                foreach (string v in vals)
                {
                    if (!first) _sb.Append(", ");
                    first = false;
                    _sb.Append("\"").Append(Escape(v)).Append("\"");
                }
            }
            _sb.Append("]");
        }

        /// <summary>写出一个扁平对象：{"key": {"k1":"v1","k2":"v2"}}，pairs 为 k,v,k,v…</summary>
        public void Obj(string key, params string[] pairs)
        {
            Separator();
            _sb.Append("\"").Append(Escape(key)).Append("\": {");
            for (int i = 0; i + 1 < pairs.Length; i += 2)
            {
                if (i > 0) _sb.Append(", ");
                _sb.Append("\"").Append(Escape(pairs[i])).Append("\": \"").Append(Escape(pairs[i + 1])).Append("\"");
            }
            _sb.Append("}");
        }

        public void ObjArray(string key, IEnumerable<string[]> rows)
        {
            Separator();
            _sb.Append("\"").Append(Escape(key)).Append("\": [");
            bool first = true;
            if (rows != null)
            {
                foreach (string[] row in rows)
                {
                    if (!first) _sb.Append(", ");
                    first = false;
                    _sb.Append("{");
                    for (int i = 0; i + 1 < row.Length; i += 2)
                    {
                        if (i > 0) _sb.Append(", ");
                        _sb.Append("\"").Append(Escape(row[i])).Append("\": \"").Append(Escape(row[i + 1])).Append("\"");
                    }
                    _sb.Append("}");
                }
            }
            _sb.Append("]");
        }

        public override string ToString()
        {
            return _sb.ToString();
        }

        private static string Escape(string s)
        {
            if (s == null) return "";
            StringBuilder sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  简易 manifest 读取（只取恢复需要的字段，用正则扫，避免写完整 JSON 解析器）
    // ────────────────────────────────────────────────────────────────────
    internal sealed class Manifest
    {
        public string Path;
        public string CreatedAt;
        public string LauncherRoot;
        public string LauncherReal;
        public string OriginalHash;
        public List<string> Files = new List<string>();
        public List<string> Dirs = new List<string>();
        public List<string> TaskNames = new List<string>();
        public List<string> TaskXmls = new List<string>();
        public List<string> FileRecords = new List<string>();   // 备份时的候选文件（含当时不存在的）
        public List<RunValue> RunValues = new List<RunValue>(); // Run/RunOnce 原值，恢复时回写
        public string ServiceOriginalStartMode = "demand";

        public static Manifest Load(string jsonFile)
        {
            if (!File.Exists(jsonFile)) return null;
            string txt = File.ReadAllText(jsonFile, Encoding.UTF8);
            Manifest m = new Manifest();
            m.Path = jsonFile;
            m.CreatedAt = Grab(txt, "createdAt");
            m.LauncherRoot = Grab(txt, "launcherRoot");
            m.LauncherReal = Grab(txt, "launcherReal");
            m.OriginalHash = Grab(txt, "originalHash");
            string svc = Grab(txt, "originalStartMode");
            if (!string.IsNullOrEmpty(svc)) m.ServiceOriginalStartMode = svc;

            m.Files.AddRange(GrabArray(txt, "files"));
            m.Dirs.AddRange(GrabArray(txt, "dirs"));
            m.TaskNames.AddRange(GrabArray(txt, "taskNames"));
            m.TaskXmls.AddRange(GrabArray(txt, "taskXmls"));

            // 兼容新格式：tasks 对象数组（name/xml）
            if (m.TaskNames.Count == 0)
            {
                foreach (Dictionary<string, string> d in GrabObjArray(txt, "tasks"))
                {
                    string n = Get(d, "name");
                    string x = Get(d, "xml");
                    if (n.Length > 0) { m.TaskNames.Add(n); m.TaskXmls.Add(x); }
                }
            }

            // fileRecords：记录备份时的候选文件；即使当时不存在也要参与恢复时的解锁
            foreach (Dictionary<string, string> d in GrabObjArray(txt, "fileRecords"))
            {
                string p = Get(d, "path");
                if (p.Length > 0) m.FileRecords.Add(p);
            }

            // runValues：恢复时要回写的 Run/RunOnce 原值
            foreach (Dictionary<string, string> d in GrabObjArray(txt, "runValues"))
            {
                RunValue rv = new RunValue();
                rv.Root = Get(d, "root");
                rv.SubKey = Get(d, "subkey");
                rv.Name = Get(d, "name");
                rv.Kind = Get(d, "kind");
                rv.Value = Get(d, "value");
                rv.Matched = string.Equals(Get(d, "matched"), "true", StringComparison.OrdinalIgnoreCase);
                if (rv.Kind.Length == 0) rv.Kind = "REG_SZ";
                if (rv.Name.Length > 0) m.RunValues.Add(rv);
            }

            return m;
        }

        private static string Get(Dictionary<string, string> d, string key)
        {
            string v;
            return d.TryGetValue(key, out v) && v != null ? v : "";
        }

        private static List<Dictionary<string, string>> GrabObjArray(string json, string key)
        {
            List<Dictionary<string, string>> list = new List<Dictionary<string, string>>();
            Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\\[(.*?)\\]\\s*[,}]", RegexOptions.Singleline);
            if (!m.Success) m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\\[(.*?)\\]", RegexOptions.Singleline);
            if (!m.Success) return list;
            foreach (Match obj in Regex.Matches(m.Groups[1].Value, "\\{([^{}]*)\\}"))
            {
                Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (Match kv in Regex.Matches(obj.Groups[1].Value, "\"([^\"]+)\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\""))
                    d[kv.Groups[1].Value] = Unescape(kv.Groups[2].Value);
                if (d.Count > 0) list.Add(d);
            }
            return list;
        }

        private static string Grab(string json, string key)
        {
            Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (!m.Success) return "";
            return Unescape(m.Groups[1].Value);
        }

        private static List<string> GrabArray(string json, string key)
        {
            List<string> list = new List<string>();
            Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\\[(.*?)\\]", RegexOptions.Singleline);
            if (!m.Success) return list;
            foreach (Match item in Regex.Matches(m.Groups[1].Value, "\"((?:[^\"\\\\]|\\\\.)*)\""))
            {
                string v = Unescape(item.Groups[1].Value);
                if (!string.IsNullOrEmpty(v)) list.Add(v);
            }
            return list;
        }

        private static string Unescape(string s)
        {
            return s.Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\t", "\t");
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  开机判定
    // ────────────────────────────────────────────────────────────────────
    internal static class BootJudge
    {
        [DllImport("kernel32.dll")]
        private static extern ulong GetTickCount64();

        public static DateTime BootTime
        {
            get
            {
                try { return DateTime.Now.AddMilliseconds(-(double)GetTickCount64()); }
                catch { return DateTime.Now; }
            }
        }

        public static string ColdStartupRaw(bool qing)
        {
            string sub = qing
                ? @"SOFTWARE\Kingsoft\Office\6.0\qing\Application Settings"
                : @"SOFTWARE\Kingsoft\Office\6.0\wpsoffice\Application Settings";
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(sub))
                {
                    if (k == null) return null;
                    object v = k.GetValue("coldStartup");
                    if (v == null) return null;
                    return Convert.ToInt64(v, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
                }
            }
            catch { return null; }
        }

        public static string Describe()
        {
            StringBuilder sb = new StringBuilder();
            DateTime boot = BootTime;
            sb.AppendLine("开机时间            : " + boot.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("当前时间            : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("已运行              : " + (DateTime.Now - boot).ToString(@"hh\:mm\:ss"));
            sb.AppendLine();

            bool any = false;
            bool started = false;
            string[][] targets = new string[][]
            {
                new string[] { "coldStartup(wpsoffice)", "0" },
                new string[] { "coldStartup(qing)", "1" }
            };
            foreach (string[] t in targets)
            {
                string raw = ColdStartupRaw(t[1] == "1");
                if (string.IsNullOrEmpty(raw))
                {
                    sb.AppendLine(t[0].PadRight(22) + ": （无值，WPS 从未在本机启动过）");
                    continue;
                }
                any = true;
                long ms;
                if (!long.TryParse(raw, out ms)) { sb.AppendLine(t[0].PadRight(22) + ": " + raw + "（非时间戳）"); continue; }
                DateTime when;
                try { when = DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().DateTime; }
                catch { sb.AppendLine(t[0].PadRight(22) + ": " + raw + "（无法换算）"); continue; }

                bool after = when > boot;
                if (after) started = true;
                sb.AppendLine(t[0].PadRight(22) + ": " + when.ToString("yyyy-MM-dd HH:mm:ss") + (after ? "   ← 开机之后" : "   （开机之前）"));
            }
            sb.AppendLine();

            int procs = ProcessKiller.CountWpsProcesses();
            sb.AppendLine("WPS 进程数          : " + procs);

            if (!any && procs == 0) sb.AppendLine("判定                : 干净 —— 本机没有 WPS 启动痕迹");
            else if (started || procs > 0) sb.AppendLine("判定                : 本次开机后 WPS 启动过");
            else sb.AppendLine("判定                : 干净 —— 本次开机后 WPS 未启动");

            return sb.ToString();
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  进程
    // ────────────────────────────────────────────────────────────────────
    internal static class ProcessKiller
    {
        public static readonly string[] Names = new string[]
        {
            "wps", "wpsoffice", "wpscloudsvr", "wpscloudlaunch", "wpscenter",
            "wpslingxi", "ksolaunch", "wpsupdate", "updateself", "promecefpluginhost"
        };

        public static int CountWpsProcesses()
        {
            int n = 0;
            foreach (string name in Names)
            {
                try
                {
                    Process[] ps = Process.GetProcessesByName(name);
                    n += ps.Length;
                    foreach (Process p in ps) { try { p.Dispose(); } catch { } }
                }
                catch { }
            }
            return n;
        }

        public static int KillAll(Action<string> log)
        {
            int killed = 0;
            foreach (string name in Names)
            {
                Process[] ps;
                try { ps = Process.GetProcessesByName(name); }
                catch { continue; }
                foreach (Process p in ps)
                {
                    try
                    {
                        int id = p.Id;
                        p.Kill();
                        p.WaitForExit(3000);
                        killed++;
                        if (log != null) log("已结束进程 " + name + " (PID " + id + ")");
                    }
                    catch { }
                    finally { try { p.Dispose(); } catch { } }
                }
            }
            return killed;
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  关闭自启
    // ────────────────────────────────────────────────────────────────────
    // ────────────────────────────────────────────────────────────────────
    //  Run / RunOnce 扫描与回写（D4：删除必须可逆）
    // ────────────────────────────────────────────────────────────────────
    internal sealed class RunValue
    {
        public string Root;    // HKCU / HKLM
        public string SubKey;
        public string Name;
        public string Kind;    // REG_SZ / REG_EXPAND_SZ / REG_BINARY ...
        public string Value;
        public bool Matched;   // 是否命中 WPS 关键字（只有命中的才会被删除）

        public string Label
        {
            get { return Root + "\\" + SubKey + " :: " + Name; }
        }
    }

    internal static class RunScanner
    {
        public static readonly string[][] Locations = new string[][]
        {
            new string[] { "HKCU", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run" },
            new string[] { "HKCU", @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce" },
            new string[] { "HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run" },
            new string[] { "HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce" },
            new string[] { "HKLM", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run" },
            new string[] { "HKLM", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce" }
        };

        public static List<RunValue> Scan()
        {
            List<RunValue> list = new List<RunValue>();
            foreach (string[] loc in Locations)
            {
                RegistryKey root = loc[0] == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
                try
                {
                    using (RegistryKey k = root.OpenSubKey(loc[1], false))
                    {
                        if (k == null) continue;
                        foreach (string name in k.GetValueNames())
                        {
                            RunValue rv = new RunValue();
                            rv.Root = loc[0];
                            rv.SubKey = loc[1];
                            rv.Name = name;
                            rv.Kind = "REG_SZ";
                            rv.Value = "";
                            try
                            {
                                RegistryValueKind kind = k.GetValueKind(name);
                                rv.Kind = KindToSc(kind);
                                // 不展开环境变量，保留原始 %VAR% 形式
                                object raw = k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                                if (raw is byte[]) rv.Value = Convert.ToBase64String((byte[])raw);
                                else if (raw is string[]) rv.Value = string.Join("\u0001", (string[])raw);
                                else if (raw != null) rv.Value = raw.ToString();
                            }
                            catch { }
                            string low = (rv.Value ?? "").ToLowerInvariant();
                            string lowName = (rv.Name ?? "").ToLowerInvariant();
                            rv.Matched = low.IndexOf("wps", StringComparison.Ordinal) >= 0
                                      || low.IndexOf("kingsoft", StringComparison.Ordinal) >= 0
                                      || low.IndexOf("ksolaunch", StringComparison.Ordinal) >= 0
                                      || lowName.IndexOf("wps", StringComparison.Ordinal) >= 0
                                      || lowName.IndexOf("kingsoft", StringComparison.Ordinal) >= 0;
                            list.Add(rv);
                        }
                    }
                }
                catch { }
            }
            return list;
        }

        public static int Rewrite(List<RunValue> values, Action<string> log)
        {
            int n = 0;
            if (values == null) return 0;
            foreach (RunValue rv in values)
            {
                if (!rv.Matched) continue;
                if (string.IsNullOrEmpty(rv.Root) || string.IsNullOrEmpty(rv.SubKey) || string.IsNullOrEmpty(rv.Name)) continue;
                RegistryKey root = rv.Root == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
                try
                {
                    using (RegistryKey k = root.CreateSubKey(rv.SubKey))
                    {
                        if (k == null) continue;
                        RegistryValueKind kind = ScToKind(rv.Kind);
                        object payload;
                        if (kind == RegistryValueKind.Binary) payload = Convert.FromBase64String(rv.Value);
                        else if (kind == RegistryValueKind.MultiString) payload = rv.Value.Split('\u0001');
                        else if (kind == RegistryValueKind.DWord) payload = Convert.ToInt32(rv.Value, CultureInfo.InvariantCulture);
                        else if (kind == RegistryValueKind.QWord) payload = Convert.ToInt64(rv.Value, CultureInfo.InvariantCulture);
                        else payload = rv.Value;
                        k.SetValue(rv.Name, payload, kind);
                        n++;
                        if (log != null) log("已回写：" + rv.Label + " = " + rv.Value + "  (" + rv.Kind + ")");
                    }
                }
                catch (Exception ex)
                {
                    if (log != null) log("回写失败 " + rv.Label + "：" + ex.Message);
                }
            }
            return n;
        }

        public static string KindToSc(RegistryValueKind k)
        {
            switch (k)
            {
                case RegistryValueKind.ExpandString: return "REG_EXPAND_SZ";
                case RegistryValueKind.Binary: return "REG_BINARY";
                case RegistryValueKind.DWord: return "REG_DWORD";
                case RegistryValueKind.QWord: return "REG_QWORD";
                case RegistryValueKind.MultiString: return "REG_MULTI_SZ";
                default: return "REG_SZ";
            }
        }

        public static RegistryValueKind ScToKind(string s)
        {
            if (string.Equals(s, "REG_EXPAND_SZ", StringComparison.OrdinalIgnoreCase)) return RegistryValueKind.ExpandString;
            if (string.Equals(s, "REG_BINARY", StringComparison.OrdinalIgnoreCase)) return RegistryValueKind.Binary;
            if (string.Equals(s, "REG_DWORD", StringComparison.OrdinalIgnoreCase)) return RegistryValueKind.DWord;
            if (string.Equals(s, "REG_QWORD", StringComparison.OrdinalIgnoreCase)) return RegistryValueKind.QWord;
            if (string.Equals(s, "REG_MULTI_SZ", StringComparison.OrdinalIgnoreCase)) return RegistryValueKind.MultiString;
            return RegistryValueKind.String;
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  文件锁计数（D11：UI / selftest / 文档统一口径）
    //  M 的算法：以下候选里「实际存在」的文件数
    //      office6\ksolaunch.exe、wtoolex\{wpsupdate,updateself,updatepolicy}.exe、
    //      安装根\ksolaunch.exe（守卫壳）、安装根\ksolaunch.real.exe（原版备份）
    //  注意：守卫壳与 .real 是「可执行但禁改禁删」，同样计入 —— 否则换机器/换版本时
    //        数字会和文档对不上。
    // ────────────────────────────────────────────────────────────────────
    internal static class FileLocks
    {
        public static List<string> Candidates(WpsLayout l)
        {
            List<string> files = new List<string>();
            if (l == null) return files;
            if (!string.IsNullOrEmpty(l.LauncherO6)) files.Add(l.LauncherO6);
            if (!string.IsNullOrEmpty(l.Wtoolex) && Directory.Exists(l.Wtoolex))
            {
                files.Add(Path.Combine(l.Wtoolex, "wpsupdate.exe"));
                files.Add(Path.Combine(l.Wtoolex, "updateself.exe"));
                files.Add(Path.Combine(l.Wtoolex, "updatepolicy.exe"));
            }
            if (!string.IsNullOrEmpty(l.LauncherRoot)) files.Add(l.LauncherRoot);
            if (!string.IsNullOrEmpty(l.LauncherReal)) files.Add(l.LauncherReal);
            return files;
        }

        public static void Count(WpsLayout l, out int locked, out int total)
        {
            locked = 0;
            total = 0;
            foreach (string f in Candidates(l))
            {
                if (!File.Exists(f)) continue;
                total++;
                if (Acl.HasDeny(f)) locked++;
            }
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  禁用 / 恢复 WPS 升级（独立操作，不并入「关闭自启」）
    //
    //  原理：wpsupdate.exe / updateself.exe 运行时读取
    //        HKCU\SOFTWARE\Kingsoft\Office\6.0\Common 下的三个升级服务器地址：
    //          UpdateSvr / UpdateSecSvr / NotifySvr
    //        把地址改到 127.0.0.1:9（Discard 端口）后，连接会被立即拒绝，
    //        更新器拿不到任何升级模板，也就不会下载、不会重建计划任务。
    //
    //  为什么用端口 9：它是 RFC 863 的 Discard，本机无监听时 TCP 立刻 RST，
    //        不会像 DNS 不可达或黑洞 IP 那样卡住等待超时。
    // ────────────────────────────────────────────────────────────────────
    internal sealed class UpgradeTarget
    {
        public string Name;
        public string OriginalValue;
        public string NewValue;
        public string Kind;        // REG_SZ / REG_EXPAND_SZ …
        public bool Exists;        // 备份时该值是否存在
        public bool Applied;
        public string AppliedAt;
    }

    internal static class UpgradeChannel
    {
        public const string KeyPath = @"SOFTWARE\Kingsoft\Office\6.0\Common";
        public const string KeyDisplay = @"HKCU\SOFTWARE\Kingsoft\Office\6.0\Common";
        public const string Marker = "127.0.0.1:9";
        // 污染判定用更宽的前缀：只要 originalValue 里出现 127.0.0.1 就认为不是可用原值
        public const string PollutionMark = "127.0.0.1";
        public const string BackupFileName = "upgrade-before.json";

        public static readonly string[][] Targets = new string[][]
        {
            new string[] { "UpdateSvr",    "http://127.0.0.1:9/updateserver/update" },
            new string[] { "UpdateSecSvr", "https://127.0.0.1:9/updateserver/update" },
            new string[] { "NotifySvr",    "http://127.0.0.1:9/notifyserver/notify" }
        };

        // 状态级别：0=绿(已禁用) 1=黄(未知) 3=灰(正常)
        public static string State(out int level, out string updateSvr)
        {
            updateSvr = "";
            List<UpgradeTarget> vals = Read();
            int exist = 0, disabled = 0, normal = 0;
            foreach (UpgradeTarget t in vals)
            {
                if (string.Equals(t.Name, "UpdateSvr", StringComparison.Ordinal)) updateSvr = t.Exists ? t.OriginalValue : "";
                if (!t.Exists) continue;
                exist++;
                string v = (t.OriginalValue ?? "").ToLowerInvariant();
                if (v.IndexOf(Marker, StringComparison.Ordinal) >= 0) disabled++;
                else if (v.IndexOf("wps.cn", StringComparison.Ordinal) >= 0
                      || v.IndexOf("kingsoft", StringComparison.Ordinal) >= 0
                      || v.IndexOf("wpssvr", StringComparison.Ordinal) >= 0) normal++;
            }

            if (exist == 0) { level = 1; return "未找到配置"; }
            if (disabled == exist) { level = 0; return "已禁用"; }
            if (normal == exist) { level = 3; return "正常"; }
            level = 1; return "未知（混合状态）";
        }

        public static List<UpgradeTarget> Read()
        {
            List<UpgradeTarget> list = new List<UpgradeTarget>();
            RegistryKey key = null;
            try { key = Registry.CurrentUser.OpenSubKey(KeyPath, false); }
            catch { key = null; }

            try
            {
                foreach (string[] t in Targets)
                {
                    UpgradeTarget ut = new UpgradeTarget();
                    ut.Name = t[0];
                    ut.NewValue = t[1];
                    ut.Kind = "REG_SZ";
                    ut.Exists = false;
                    ut.OriginalValue = "";
                    if (key != null)
                    {
                        try
                        {
                            object raw = key.GetValue(ut.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                            if (raw != null)
                            {
                                ut.Exists = true;
                                ut.OriginalValue = raw.ToString();
                                ut.Kind = RunScanner.KindToSc(key.GetValueKind(ut.Name));
                            }
                        }
                        catch { }
                    }
                    list.Add(ut);
                }
            }
            finally { if (key != null) key.Close(); }
            return list;
        }

        private static bool WriteValue(string name, string value, string kind)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(KeyPath, true))
                {
                    if (k == null) return false;
                    k.SetValue(name, value, RunScanner.ScToKind(kind));
                    return true;
                }
            }
            catch { return false; }
        }

        /// <summary>读单个值的当前内容（写入后回读打印用）</summary>
        private static string ReadOne(string name)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(KeyPath, false))
                {
                    if (k == null) return "（键不存在）";
                    object raw = k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    return raw == null ? "（无值）" : raw.ToString();
                }
            }
            catch (Exception ex) { return "（读取失败：" + ex.Message + "）"; }
        }

        // ── 备份 ──
        private static string WriteBackup(List<UpgradeTarget> vals, Action<string> log)
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            Json j = new Json();
            j.Open();
            j.Str("createdAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            j.Str("user", Acl.DomainUser);
            j.Str("key", KeyDisplay);
            List<string[]> rows = new List<string[]>();
            foreach (UpgradeTarget t in vals)
            {
                if (!t.Exists) continue;
                rows.Add(new string[]
                {
                    "name", t.Name,
                    "originalValue", t.OriginalValue,
                    "newValue", t.NewValue,
                    "kind", t.Kind,
                    "appliedAt", stamp
                });
            }
            j.ObjArray("values", rows);
            j.Close();

            string json = j.ToString();

            // ① 稳定路径：exe 同级 upgrade-before.json（单独恢复时不依赖整份 manifest）
            //    D7：这是"先备份后写入"的落点，必须确认真的写成功，否则调用方要中止
            //    F2：已存在干净备份时绝不覆盖 —— 否则第二次禁用会把 127.0.0.1 当成"原值"写进去，
            //        唯一可还原的凭据就被抹掉了。
            string stable = Path.Combine(Paths.ExeDir, BackupFileName);
            string written = null;
            try
            {
                string target = stable;
                if (File.Exists(stable))
                {
                    // 污染判定必须只看 originalValue，不能扫整段文本 ——
                    // 备份里 newValue 本来就写着 127.0.0.1，扫全文会把干净备份误判成污染的。
                    bool polluted = !IsCleanBackup(ParseBackup(stable));

                    if (polluted)
                    {
                        log("检测到已有 " + BackupFileName + " 的 originalValue 含 127.0.0.1（历史污染），用本次干净值覆盖修正。");
                    }
                    else
                    {
                        target = Path.Combine(Paths.ExeDir, "upgrade-before-" + stamp + ".json");
                        log("已有干净的原值备份 " + BackupFileName + "，为防覆盖污染，本次另存为 " + Path.GetFileName(target));
                    }
                }

                File.WriteAllText(target, json, Encoding.UTF8);
                if (File.Exists(target))
                {
                    written = target;
                    FileInfo fi = new FileInfo(target);
                    log("已写备份：" + target);
                    log("           大小 " + fi.Length + " 字节，时间 " + fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                }
            }
            catch (Exception ex) { log("写 " + BackupFileName + " 失败：" + ex.Message); }

            // ② 独立备份目录：upgrade-backup-<时间>\manifest.json（带 upgradeValues）
            try
            {
                string dir = Path.Combine(Paths.ExeDir, "upgrade-backup-" + stamp);
                Directory.CreateDirectory(dir);
                Json m = new Json();
                m.Open();
                m.Str("createdAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                m.Str("user", Acl.DomainUser);
                m.Str("key", KeyDisplay);
                m.ObjArray("upgradeValues", rows);
                m.Close();
                File.WriteAllText(Path.Combine(dir, "manifest.json"), m.ToString(), Encoding.UTF8);
                File.WriteAllText(Path.Combine(dir, BackupFileName), json, Encoding.UTF8);
                log("已写备份：" + Path.Combine(dir, "manifest.json"));
            }
            catch (Exception ex) { log("写 upgrade-backup 目录失败：" + ex.Message); }

            // 本次写成功，或原本就存在一份干净可用的 stable → 都算备份就绪
            bool okAny = (written != null) || File.Exists(stable);
            return okAny ? stable : null;
        }

        // ── 禁用 ──
        public static bool Disable(Action<string> log, bool dryRun)
        {
            log("=== 禁用 WPS 升级 ===");
            log("注册表位置：" + KeyDisplay);

            List<UpgradeTarget> vals = Read();
            int exist = 0;
            foreach (UpgradeTarget t in vals) if (t.Exists) exist++;
            if (exist == 0)
            {
                log("三个升级服务器值都不存在 —— 可能 WPS 未安装或版本不同，未做任何改动。");
                return false;
            }

            // ★ F1 幂等守卫：三个值都已指向 127.0.0.1 说明已经禁过了。
            //    此时若再走一遍，Read() 拿到的是黑洞值，会被当成"原值"写进备份，
            //    把唯一能还原的凭据污染掉 —— 所以必须在这里直接返回，且不碰备份。
            bool allDisabled = true;
            foreach (UpgradeTarget t in vals)
            {
                if (!t.Exists) continue;
                if ((t.OriginalValue ?? "").IndexOf(PollutionMark, StringComparison.Ordinal) < 0) { allDisabled = false; break; }
            }
            if (allDisabled)
            {
                log("已处于禁用状态（三个值都指向 127.0.0.1），跳过。未做任何写入，也未覆盖任何备份。");
                return true;
            }

            // ★ D7：任何注册表写入之前，先把原值落到 upgrade-before.json 并确认写成功。
            //        备份失败就中止，绝不出现"值被改掉但没有备份可还原"的状态。
            if (!dryRun)
            {
                string bf = WriteBackup(vals, log);
                if (bf == null)
                {
                    log("！备份写入失败 —— 为安全起见中止，未改动任何注册表值。");
                    return false;
                }
                log("备份已就绪，开始写入注册表。");
                log("");
            }

            int applied = 0, skipped = 0;
            foreach (UpgradeTarget t in vals)
            {
                if (!t.Exists)
                {
                    log("  跳过 " + t.Name + "：该值不存在（不凭空创建）");
                    skipped++;
                    continue;
                }

                log("  " + t.Name.PadRight(13) + " 写入前 = " + t.OriginalValue + "   [" + t.Kind + "]");

                if (dryRun)
                {
                    log("  " + "".PadRight(13) + " 将写入 = " + t.NewValue + "   （演练模式，未真正写入）");
                    applied++;
                    continue;
                }

                bool wrote = WriteValue(t.Name, t.NewValue, t.Kind);
                // D10：写完再读回真实值打印，保证日志顺序与事实一致
                string actual = ReadOne(t.Name);
                log("  " + "".PadRight(13) + " 写入后 = " + (wrote ? actual : "（写入失败，当前 = " + actual + "）"));

                if (wrote)
                {
                    t.Applied = true;
                    t.AppliedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    applied++;
                }
                else log("      ！写入失败：" + t.Name);
            }

            if (dryRun) { log("演练完成：" + applied + " 个待改，跳过 " + skipped + " 个"); return true; }

            // 读回确认
            log("");
            log("读回确认：");
            List<UpgradeTarget> after = Read();
            int ok = 0;
            foreach (UpgradeTarget t in after)
            {
                if (!t.Exists) continue;
                bool good = string.Equals(t.OriginalValue, t.NewValue, StringComparison.Ordinal);
                if (good) ok++;
                log("  " + t.Name.PadRight(13) + (good ? " ✔ " : " ✘ ") + "当前 = " + t.OriginalValue);
            }
            log("共 " + applied + " 个已改，读回一致 " + ok + " 个，跳过 " + skipped + " 个");
            return ok == applied && applied > 0;
        }

        // ── 恢复 ──
        /// <summary>F3：备份是否干净 —— 任何一个 originalValue 含 127.0.0.1 就不可用</summary>
        private static bool IsCleanBackup(List<UpgradeTarget> v)
        {
            if (v == null || v.Count == 0) return false;
            foreach (UpgradeTarget t in v)
                if ((t.OriginalValue ?? "").IndexOf(PollutionMark, StringComparison.Ordinal) >= 0) return false;
            return true;
        }

        public static List<UpgradeTarget> LoadBackup(out string source)
        {
            return LoadBackup(out source, null);
        }

        public static List<UpgradeTarget> LoadBackup(out string source, Action<string> log)
        {
            source = null;

            // ① exe 同级 upgrade-before.json
            string stable = Path.Combine(Paths.ExeDir, BackupFileName);
            if (File.Exists(stable))
            {
                List<UpgradeTarget> v = ParseBackup(stable);
                if (v.Count > 0)
                {
                    if (IsCleanBackup(v)) { source = stable; return v; }
                    if (log != null) log("跳过 " + stable + "：其中的 originalValue 含 127.0.0.1，不是可用的原值备份");
                }
            }

            // ② 最新的 upgrade-backup-*\manifest.json
            try
            {
                string[] dirs = Directory.GetDirectories(Paths.ExeDir, "upgrade-backup-*");
                Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
                for (int i = dirs.Length - 1; i >= 0; i--)
                {
                    string mf = Path.Combine(dirs[i], "manifest.json");
                    if (!File.Exists(mf)) continue;
                    List<UpgradeTarget> v = ParseBackup(mf);
                    if (v.Count == 0) continue;
                    if (IsCleanBackup(v)) { source = mf; return v; }
                    if (log != null) log("跳过 " + mf + "：其中的 originalValue 含 127.0.0.1，不是可用的原值备份");
                }
            }
            catch { }

            // ③ 最新 backup-*\manifest.json 里的 upgradeValues
            try
            {
                string[] dirs = Directory.GetDirectories(Paths.ExeDir, "backup-*");
                Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
                for (int i = dirs.Length - 1; i >= 0; i--)
                {
                    string mf = Path.Combine(dirs[i], "manifest.json");
                    if (!File.Exists(mf)) continue;
                    List<UpgradeTarget> v = ParseBackup(mf);
                    if (v.Count == 0) continue;
                    if (IsCleanBackup(v)) { source = mf; return v; }
                    if (log != null) log("跳过 " + mf + "：其中的 originalValue 含 127.0.0.1，不是可用的原值备份");
                }
            }
            catch { }

            source = "（三级备份都不可用：没有找到含原始 wps.cn 地址的备份）";
            return new List<UpgradeTarget>();
        }

        private static List<UpgradeTarget> ParseBackup(string jsonFile)
        {
            List<UpgradeTarget> list = new List<UpgradeTarget>();
            string txt;
            try { txt = File.ReadAllText(jsonFile, Encoding.UTF8); }
            catch { return list; }

            // values[] 或 upgradeValues[] 都认
            Match arr = Regex.Match(txt, "\"(?:values|upgradeValues)\"\\s*:\\s*\\[(.*?)\\]\\s*[,}]", RegexOptions.Singleline);
            if (!arr.Success) arr = Regex.Match(txt, "\"(?:values|upgradeValues)\"\\s*:\\s*\\[(.*?)\\]", RegexOptions.Singleline);
            if (!arr.Success) return list;

            foreach (Match obj in Regex.Matches(arr.Groups[1].Value, "\\{([^{}]*)\\}"))
            {
                Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (Match kv in Regex.Matches(obj.Groups[1].Value, "\"([^\"]+)\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\""))
                {
                    string val = kv.Groups[2].Value
                        .Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\t", "\t");
                    d[kv.Groups[1].Value] = val;
                }
                string name;
                if (!d.TryGetValue("name", out name) || string.IsNullOrEmpty(name)) continue;
                UpgradeTarget t = new UpgradeTarget();
                t.Name = name;
                string ov;
                t.OriginalValue = d.TryGetValue("originalValue", out ov) ? ov : "";
                string kd;
                t.Kind = d.TryGetValue("kind", out kd) && !string.IsNullOrEmpty(kd) ? kd : "REG_SZ";
                t.Exists = true;
                list.Add(t);
            }
            return list;
        }

        public static bool Restore(Action<string> log)
        {
            log("=== 恢复 WPS 升级通道 ===");
            string source;
            List<UpgradeTarget> backup = LoadBackup(out source, log);
            if (backup.Count == 0)
            {
                log("没有升级配置备份，请手工恢复。");
                log("原始值应为：");
                log("  UpdateSvr    = http://update.wps.cn/updateserver/update;http://update2.wps.cn/updateserver/update");
                log("  UpdateSecSvr = https://update.wps.cn/updateserver/update;https://update2.wps.cn/updateserver/update");
                log("  NotifySvr    = http://notify.wps.cn/notifyserver/notify");
                return false;
            }
            log("恢复来源：" + source);

            List<UpgradeTarget> now = Read();
            int done = 0, refused = 0, failedWrites = 0;
            foreach (UpgradeTarget b in backup)
            {
                string current = "";
                foreach (UpgradeTarget c in now)
                    if (string.Equals(c.Name, b.Name, StringComparison.Ordinal)) current = c.OriginalValue;

                log("  " + b.Name.PadRight(13) + " 当前 = " + current);

                // ★ F4 写前断言：备份里的"原值"本身是黑洞地址就绝不写回，
                //    否则会把 127.0.0.1 当成"恢复成功"，用户以为修好了其实还是断的。
                if ((b.OriginalValue ?? "").IndexOf(PollutionMark, StringComparison.Ordinal) >= 0)
                {
                    refused++;
                    log("  " + "".PadRight(13) + " 拒绝写回 " + b.Name + "：值为 127.0.0.1，不是原地址");
                    continue;
                }

                log("  " + "".PadRight(13) + " 还原 = " + b.OriginalValue + "   [" + b.Kind + "]");

                if (WriteValue(b.Name, b.OriginalValue, b.Kind)) done++;
                else { failedWrites++; log("      ！写回失败：" + b.Name); }
            }

            log("");
            log("读回确认：");
            List<UpgradeTarget> after = Read();
            int ok = 0;
            foreach (UpgradeTarget t in after)
            {
                if (!t.Exists) continue;
                string expected = "";
                foreach (UpgradeTarget b in backup)
                    if (string.Equals(b.Name, t.Name, StringComparison.Ordinal)) expected = b.OriginalValue;
                bool good = string.Equals(t.OriginalValue, expected, StringComparison.Ordinal);
                if (good) ok++;
                log("  " + t.Name.PadRight(13) + (good ? " ✔ " : " ✘ ") + "当前 = " + t.OriginalValue);
            }
            log("共写回 " + done + " 个，读回一致 " + ok + " 个，拒绝 " + refused + " 个，写失败 " + failedWrites + " 个");
            if (refused > 0 || failedWrites > 0)
            {
                log("！有 " + (refused + failedWrites) + " 项未恢复，请检查备份来源（原值不可信时不报成功）。");
                return false;
            }
            return ok == done && done > 0;
        }

        // ── 往返自测：备份 → 改 → 读回 → 立刻还原 → 再读回 ──
        public static int RoundTripTest(Action<string> log)
        {
            log("=== 升级通道 往返自测（改完立刻还原）===");
            List<UpgradeTarget> before = Read();

            log("① 改动前实际值（只读，未改动）：");
            int exist = 0;
            foreach (UpgradeTarget t in before)
            {
                if (!t.Exists) { log("   " + t.Name.PadRight(13) + " （不存在，将跳过）"); continue; }
                exist++;
                log("   " + t.Name.PadRight(13) + " = " + t.OriginalValue + "   [" + t.Kind + "]");
            }
            if (exist == 0)
            {
                log("三个值都不存在，无法自测（WPS 未安装或版本不同）。");
                return 1;
            }

            log("");
            log("② 先落备份（必须早于任何注册表写入）：");
            // ★ D7：写入之前先把原值落盘。自测虽然会自己还原，但万一进程被杀，
            //        这份文件就是唯一能救回原值的凭据，所以必须先生成。
            string bf = WriteBackup(before, log);
            if (bf == null)
            {
                log("！备份写入失败 —— 为安全起见中止自测，未改动任何注册表值。");
                return 1;
            }
            FileInfo bfi = new FileInfo(bf);
            log("备份已就绪：" + bf);
            log("           大小 " + bfi.Length + " 字节，时间 " + bfi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss.fff") + "（早于任何写入）");

            // ③ 写入禁用值
            log("");
            log("③ 写入 127.0.0.1:9 …");
            foreach (UpgradeTarget t in before)
            {
                if (!t.Exists) continue;
                bool ok = WriteValue(t.Name, t.NewValue, t.Kind);
                log("   " + t.Name.PadRight(13) + (ok ? " 写入成功 → " : " 写入失败 → ") + t.NewValue);
            }

            // ④ 读回断言
            log("");
            log("④ 读回断言（应全部等于 127.0.0.1:9 值）：");
            bool writeOk = true;
            foreach (UpgradeTarget t in Read())
            {
                if (!t.Exists) continue;
                bool good = string.Equals(t.OriginalValue, t.NewValue, StringComparison.Ordinal);
                if (!good) writeOk = false;
                log("   " + t.Name.PadRight(13) + (good ? " ✔ " : " ✘ ") + t.OriginalValue);
            }

            // ⑤ 立刻还原
            log("");
            log("⑤ 立刻还原原值 …");
            foreach (UpgradeTarget t in before)
            {
                if (!t.Exists) continue;
                bool ok = WriteValue(t.Name, t.OriginalValue, t.Kind);
                log("   " + t.Name.PadRight(13) + (ok ? " 还原成功 ← " : " 还原失败 ← ") + t.OriginalValue);
            }

            // ⑥ 再读回断言
            log("");
            log("⑥ 读回断言（应等于原值，且 endsWith 原值）：");
            bool restoreOk = true;
            foreach (UpgradeTarget t in Read())
            {
                if (!t.Exists) continue;
                string expected = "";
                string expKind = "REG_SZ";
                foreach (UpgradeTarget b in before)
                    if (string.Equals(b.Name, t.Name, StringComparison.Ordinal)) { expected = b.OriginalValue; expKind = b.Kind; }
                bool eq = string.Equals(t.OriginalValue, expected, StringComparison.Ordinal);
                bool ends = t.OriginalValue != null && t.OriginalValue.EndsWith(expected, StringComparison.Ordinal);
                if (!eq || !ends) restoreOk = false;
                log("   " + t.Name.PadRight(13) + (eq ? " ✔ 相等" : " ✘ 不等") + " / " + (ends ? "endsWith ✔" : "endsWith ✘")
                    + "   [" + expKind + "→" + t.Kind + "]  当前 = " + t.OriginalValue);
            }

            log("");
            log("说明：" + BackupFileName + " 保留在 exe 同级，是有效备份，可用于日后单独恢复。");
            log("结论：写入断言 " + (writeOk ? "通过" : "失败") + "，还原断言 " + (restoreOk ? "通过" : "失败"));
            return (writeOk && restoreOk) ? 0 : 1;
        }
    }

    internal static class Killer
    {
        public static string BackupDir;
        // 备份阶段扫描到的 Run/RunOnce 原值；CleanRunKeys 只删这里记录过的，确保可回写
        private static List<RunValue> _runValues;

        /// <summary>只做备份（读注册表/文件/任务并写 backup-* 目录），不改动任何系统状态。供验收诊断使用。</summary>
        public static string BackupOnly(WpsLayout layout, bool isAdmin, Action<string> log)
        {
            return DoBackup(layout, isAdmin, log);
        }

        public static void Run(WpsLayout layout, bool isAdmin, Action<string> log)
        {
            int fail = 0;
            BackupDir = DoBackup(layout, isAdmin, log);

            log("");
            log("=== 1. 结束 WPS 进程 ===");
            int killed = ProcessKiller.KillAll(log);
            log("共结束 " + killed + " 个进程");

            log("");
            log("=== 2. 禁用并删除 WPS 计划任务 ===");
            List<WpsTask> tasks = Tasks.Find();
            if (tasks.Count == 0) log("未发现 WPS 计划任务");
            foreach (WpsTask t in tasks)
            {
                if (Tasks.Disable(t.Name)) log("已禁用：" + t.Name); else log("禁用失败：" + t.Name + "（继续）");
                if (Tasks.Delete(t.Name)) log("已删除：" + t.Name);
                else log("删除失败：" + t.Name + "（DACL 可能只给读权限，需管理员；已保留禁用状态）");
            }

            log("");
            log("=== 3. 清扫 Run / RunOnce ===");
            CleanRunKeys(log);

            log("");
            log("=== 4. 换守卫壳 ===");
            if (!string.IsNullOrEmpty(BackupDir))
            {
                string realExe = layout.LauncherReal;
                // 已经是守卫壳状态就跳过
                if (WpsProbe.IsShimInstalled(layout))
                {
                    log("主入口已是守卫壳，跳过替换");
                }
                else if (!File.Exists(layout.LauncherRoot))
                {
                    log("没有根级 ksolaunch.exe（该版本布局不同），跳过守卫壳");
                }
                else
                {
                    string srcForIcon = layout.LauncherRoot;
                    string shim = Guard.Compile(srcForIcon, Path.Combine(BackupDir, "guard-build.log"));
                    if (shim == null) { log("守卫壳编译失败"); fail++; }
                    else
                    {
                        // 硬规则 3：先解除目录与文件的 DENY，否则重命名/覆盖报 Access denied
                        Acl.RemoveDeny(layout.RootDir);
                        Acl.RemoveDeny(layout.LauncherRoot);
                        try
                        {
                            if (File.Exists(layout.LauncherRoot)) File.Move(layout.LauncherRoot, realExe);
                            File.Copy(shim, layout.LauncherRoot, true);
                            log("守卫壳已落位：" + layout.LauncherRoot);
                            log("原版已保留：" + realExe);
                        }
                        catch (Exception ex)
                        {
                            log("替换失败：" + ex.Message);
                            fail++;
                        }
                    }
                }
            }

            log("");
            log("=== 5. 文件级 DENY " + Acl.FileDeny + " ===");
            List<string> execFiles = new List<string>();
            execFiles.Add(layout.LauncherO6);
            if (Directory.Exists(layout.Wtoolex))
            {
                execFiles.Add(Path.Combine(layout.Wtoolex, "wpsupdate.exe"));
                execFiles.Add(Path.Combine(layout.Wtoolex, "updateself.exe"));
                execFiles.Add(Path.Combine(layout.Wtoolex, "updatepolicy.exe"));
            }
            int lockedFiles = 0;
            foreach (string f in execFiles)
            {
                if (!File.Exists(f)) continue;
                if (Acl.Deny(f, Acl.FileDeny)) { log("已锁死：" + ShortPath(f, layout)); lockedFiles++; }
                else { log("锁定失败：" + f); fail++; }
            }
            foreach (string f in new string[] { layout.LauncherRoot, layout.LauncherReal })
            {
                if (!File.Exists(f)) continue;
                if (Acl.Deny(f, Acl.FileKeepExec)) { log("可执行但禁改禁删：" + ShortPath(f, layout)); lockedFiles++; }
                else { log("锁定失败：" + f); fail++; }
            }
            log("文件锁计 " + lockedFiles + " 处");

            log("");
            log("=== 6. 目录级 DENY " + Acl.DirDeny + " ===");
            List<string> dirs = new List<string>();
            dirs.Add(layout.RootDir);
            string cloudUpd = Path.Combine(Paths.LocalAppData, @"Kingsoft\WPS Cloud Files\update");
            if (Directory.Exists(cloudUpd)) dirs.Add(cloudUpd);
            foreach (string d in dirs)
            {
                if (Acl.Deny(d, Acl.DirDeny)) log("已锁目录：" + d);
                else { log("目录锁定失败：" + d); fail++; }
            }

            log("");
            log("=== 7. 服务 wpscloudsvr ===");
            if (Svc.Exists)
            {
                if (isAdmin)
                {
                    Svc.SetStart("disabled");
                    Svc.Stop();
                    log("已设为 Disabled 并尝试停止，当前：" + Svc.State + " / " + Svc.StartMode);
                }
                else
                {
                    log("非管理员，跳过服务设置（当前 " + Svc.State + " / " + Svc.StartMode + "）");
                    log("如需一并钉死，用管理员执行：sc.exe config wpscloudsvr start= disabled");
                }
            }
            else log("未安装 wpscloudsvr 服务");

            log("");
            log("=== 8. 复核 ===");
            int left = Tasks.Find().Count;
            log("剩余 WPS 计划任务：" + left + (left == 0 ? "" : "  ← 需管理员再删一次"));
            int denyNow = 0;
            foreach (string f in execFiles) if (File.Exists(f) && Acl.HasDeny(f)) denyNow++;
            log("载荷 DENY 生效：" + denyNow + " / " + execFiles.FindAll(File.Exists).Count);
            int allLocked, allTotal;
            FileLocks.Count(layout, out allLocked, out allTotal);
            log("文件锁合计：" + allLocked + " / " + allTotal + "   （含守卫壳与原版备份，与界面口径一致）");
            log("主入口守卫壳：" + (WpsProbe.IsShimInstalled(layout) ? "是" : "否"));

            log("");
            if (fail == 0) log("完成，0 个失败项。");
            else log("完成，但有 " + fail + " 个失败项，详见上方。");
            if (!string.IsNullOrEmpty(BackupDir)) log("备份目录：" + BackupDir);
        }

        private static string DoBackup(WpsLayout layout, bool isAdmin, Action<string> log)
        {
            log("=== 0. 备份现场 ===");
            string dir = Path.Combine(Paths.ExeDir, "backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            try { Directory.CreateDirectory(dir); }
            catch (Exception ex) { log("创建备份目录失败：" + ex.Message); return null; }

            List<string> watchFiles = new List<string>();
            watchFiles.Add(layout.LauncherRoot);
            watchFiles.Add(layout.LauncherReal);
            watchFiles.Add(layout.LauncherO6);
            if (Directory.Exists(layout.Wtoolex))
            {
                watchFiles.Add(Path.Combine(layout.Wtoolex, "wpsupdate.exe"));
                watchFiles.Add(Path.Combine(layout.Wtoolex, "updateself.exe"));
                watchFiles.Add(Path.Combine(layout.Wtoolex, "updatepolicy.exe"));
            }

            // ACL 现状
            try { File.WriteAllText(Path.Combine(dir, "acl-before.txt"), Acl.Snapshot(watchFiles), Encoding.UTF8); log("已记录 ACL 现状"); }
            catch { }

            // SHA256
            StringBuilder hashes = new StringBuilder();
            hashes.AppendLine("SHA256," + "Path");
            string originalHash = "";
            foreach (string f in watchFiles)
            {
                if (!File.Exists(f)) continue;
                try
                {
                    string h = WpsProbe.Sha256(f);
                    hashes.AppendLine(h + ",\"" + f + "\"");
                    if (string.Equals(f, layout.LauncherReal, StringComparison.OrdinalIgnoreCase)) originalHash = h;
                }
                catch { }
            }
            if (originalHash.Length == 0 && File.Exists(layout.LauncherRoot) && !WpsProbe.IsShimInstalled(layout))
            {
                try { originalHash = WpsProbe.Sha256(layout.LauncherRoot); } catch { }
            }
            try { File.WriteAllText(Path.Combine(dir, "hash-before.csv"), hashes.ToString(), Encoding.UTF8); log("已记录文件 SHA256"); }
            catch { }

            // 任务 XML
            List<string> taskNames = new List<string>();
            List<string> taskXmls = new List<string>();
            foreach (WpsTask t in Tasks.Find())
            {
                string xml = Tasks.ExportXml(t.Name);
                if (string.IsNullOrEmpty(xml)) { log("任务 XML 导出失败（可能权限受限）：" + t.Name); continue; }
                string safe = Regex.Replace(t.Name, "[^A-Za-z0-9_.-]", "_");
                string file = Path.Combine(dir, safe + ".xml");
                try { File.WriteAllText(file, xml, Encoding.UTF8); taskNames.Add(t.Name); taskXmls.Add(file); log("已备份任务：" + t.Name); }
                catch { }
            }

            // 注册表
            string regFile = Path.Combine(dir, "HKCU-Kingsoft.reg");
            string o, e;
            Shell.Run("reg.exe", "export \"HKCU\\SOFTWARE\\Kingsoft\" " + "\"" + regFile + "\" /y", out o, out e);
            if (File.Exists(regFile)) log("已导出 HKCU\\SOFTWARE\\Kingsoft");

            // 服务
            string svcMode = Svc.Exists ? Svc.StartMode : "NotFound";
            string svcNow = Svc.Exists ? (Svc.State + " / " + Svc.StartMode) : "未安装";
            try { File.WriteAllText(Path.Combine(dir, "service-before.txt"), svcNow, Encoding.UTF8); } catch { }

            // Run / RunOnce 现场（D4：删除前必须先把原值记进 manifest，恢复时才写回）
            List<RunValue> runValues = RunScanner.Scan();
            _runValues = runValues;
            int runMatched = 0;
            foreach (RunValue rv in runValues) if (rv.Matched) runMatched++;
            log("扫描 Run/RunOnce 共 " + runValues.Count + " 个值，其中 WPS 相关 " + runMatched + " 个（已记入 manifest）");
            foreach (RunValue rv in runValues)
                if (rv.Matched) log("    [命中] " + rv.Label + " = " + rv.Value + "  (" + rv.Kind + ")");

            // manifest
            string originalMode = svcMode == "Disabled" ? "demand" : ModeToSc(svcMode);
            Json j = new Json();
            j.Open();
            j.Str("createdAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            j.Str("user", Acl.DomainUser);
            j.Bool("isAdmin", isAdmin);
            j.Obj("layout",
                "versionDir", layout.VersionDir,
                "rootDir", layout.RootDir,
                "office6", layout.Office6,
                "wtoolex", layout.Wtoolex,
                "machineWide", layout.MachineWide ? "true" : "false",
                "foundBy", layout.FoundBy);
            j.Str("versionDir", layout.VersionDir);
            j.Str("rootDir", layout.RootDir);
            j.Str("launcherRoot", layout.LauncherRoot);
            j.Str("launcherReal", layout.LauncherReal);
            j.Str("originalHash", originalHash);
            List<string> files = new List<string>();
            foreach (string f in watchFiles) if (File.Exists(f)) files.Add(f);
            j.Arr("files", files);

            // D3：即使备份时 ksolaunch.real.exe 还不存在（它是关闭自启过程中才产生的），
            //     也要无条件记一条，标 existedAtBackup=false，恢复时才知道要解它的锁。
            List<string[]> fileRecords = new List<string[]>();
            foreach (string f in watchFiles)
            {
                if (string.IsNullOrEmpty(f)) continue;
                fileRecords.Add(new string[] { "path", f, "existedAtBackup", File.Exists(f) ? "true" : "false" });
            }
            j.ObjArray("fileRecords", fileRecords);

            List<string> dirs = new List<string>();
            dirs.Add(layout.RootDir);
            string cloudUpd = Path.Combine(Paths.LocalAppData, @"Kingsoft\WPS Cloud Files\update");
            if (Directory.Exists(cloudUpd)) dirs.Add(cloudUpd);
            j.Arr("dirs", dirs);
            j.Arr("taskNames", taskNames);
            j.Arr("taskXmls", taskXmls);

            List<string[]> taskRecords = new List<string[]>();
            for (int i = 0; i < taskNames.Count; i++)
                taskRecords.Add(new string[] { "name", taskNames[i], "xml", taskXmls[i] });
            j.ObjArray("tasks", taskRecords);

            j.Str("service", Svc.Name);
            j.Str("originalStartMode", originalMode);
            List<string[]> svcRecords = new List<string[]>();
            svcRecords.Add(new string[] { "name", Svc.Name, "originalStartMode", originalMode, "stateAtBackup", svcMode });
            j.ObjArray("services", svcRecords);

            List<string[]> runRecords = new List<string[]>();
            foreach (RunValue rv in runValues)
            {
                runRecords.Add(new string[]
                {
                    "root", rv.Root,
                    "subkey", rv.SubKey,
                    "name", rv.Name,
                    "kind", rv.Kind,
                    "value", rv.Value,
                    "matched", rv.Matched ? "true" : "false"
                });
            }
            j.ObjArray("runValues", runRecords);

            // 升级通道现状也一并留档，便于任何时候从这里单独恢复
            List<UpgradeTarget> upVals = UpgradeChannel.Read();
            List<string[]> upRecords = new List<string[]>();
            foreach (UpgradeTarget t in upVals)
            {
                if (!t.Exists) continue;
                upRecords.Add(new string[]
                {
                    "name", t.Name,
                    "originalValue", t.OriginalValue,
                    "newValue", t.NewValue,
                    "kind", t.Kind,
                    "appliedAt", ""
                });
            }
            j.ObjArray("upgradeValues", upRecords);
            j.Close();

            try { File.WriteAllText(Path.Combine(dir, "manifest.json"), j.ToString(), Encoding.UTF8); log("已生成 manifest.json（含 fileRecords / tasks / services / runValues）"); }
            catch (Exception ex) { log("写 manifest 失败：" + ex.Message); }

            // 一致性：升级配置也在本备份目录里单独落一份，和「禁用升级」的产物对称。
            // 注意只写进 backup-<时间戳>\，不写 exe 同级，避免污染交付目录。
            if (upRecords.Count > 0)
            {
                try
                {
                    Json u = new Json();
                    u.Open();
                    u.Str("createdAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                    u.Str("user", Acl.DomainUser);
                    u.Str("key", UpgradeChannel.KeyDisplay);
                    u.ObjArray("values", upRecords);
                    u.Close();

                    string ub = Path.Combine(dir, UpgradeChannel.BackupFileName);
                    File.WriteAllText(ub, u.ToString(), Encoding.UTF8);
                    log("已记录升级配置：" + UpgradeChannel.BackupFileName + "（" + upRecords.Count + " 项，" + new FileInfo(ub).Length + " 字节）");
                }
                catch (Exception ex) { log("写 " + UpgradeChannel.BackupFileName + " 失败：" + ex.Message); }
            }
            else
            {
                // 不写空文件
                log("跳过 " + UpgradeChannel.BackupFileName + "：本次没有可记录的升级配置（WPS 未安装或值不存在）");
            }

            log("备份完成：" + dir);
            return dir;
        }

        private static string ModeToSc(string mode)
        {
            if (string.Equals(mode, "Auto", StringComparison.OrdinalIgnoreCase)) return "auto";
            if (string.Equals(mode, "Disabled", StringComparison.OrdinalIgnoreCase)) return "disabled";
            return "demand";
        }

        private static void CleanRunKeys(Action<string> log)
        {
            // D4：只删除备份阶段已经记录下来的值，确保「恢复原装」能原样写回。
            if (_runValues == null || _runValues.Count == 0)
            {
                log("备份阶段未记录 Run 值，现场重新扫描 —— 这些值不会被写进 manifest，恢复时无法回写");
                _runValues = RunScanner.Scan();
            }

            int removed = 0;
            int matched = 0;
            foreach (RunValue rv in _runValues)
            {
                if (!rv.Matched) continue;
                matched++;
                RegistryKey root = rv.Root == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
                try
                {
                    using (RegistryKey k = root.OpenSubKey(rv.SubKey, true))
                    {
                        if (k == null) { log("键不存在，跳过：" + rv.Label); continue; }
                        if (Array.IndexOf(k.GetValueNames(), rv.Name) < 0) { log("值已不在，跳过：" + rv.Label); continue; }
                        k.DeleteValue(rv.Name, false);
                        removed++;
                        log("已删除 " + rv.Label + "  [原值 " + rv.Kind + "，已记入 manifest，恢复时会回写]");
                    }
                }
                catch (Exception ex) { log("删除失败 " + rv.Label + "：" + ex.Message); }
            }
            if (matched == 0) log("Run / RunOnce 中没有 WPS 项");
            else log("共删除 " + removed + " 个值（全部已在 manifest.runValues 中留底）");
        }

        private static string ShortPath(string full, WpsLayout l)
        {
            if (full.StartsWith(l.RootDir, StringComparison.OrdinalIgnoreCase)) return full.Substring(l.RootDir.Length).TrimStart('\\');
            return full;
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  恢复原装
    // ────────────────────────────────────────────────────────────────────
    internal static class Restorer
    {
        public static string FindLatestBackup()
        {
            try
            {
                string[] dirs = Directory.GetDirectories(Paths.ExeDir, "backup-*");
                Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
                if (dirs.Length == 0) return null;
                return dirs[dirs.Length - 1];
            }
            catch { return null; }
        }

        public static void Run(string backupDir, Action<string> log)
        {
            if (string.IsNullOrEmpty(backupDir)) backupDir = FindLatestBackup();
            if (string.IsNullOrEmpty(backupDir))
            {
                log("找不到任何 backup-* 目录，无法恢复。");
                return;
            }
            log("使用备份：" + backupDir);

            Manifest m = Manifest.Load(Path.Combine(backupDir, "manifest.json"));
            if (m == null)
            {
                log("manifest.json 读取失败，只能做保守恢复。");
                m = new Manifest();
            }

            log("");
            log("=== 1. 解除 DENY ===");
            WpsLayout layout = WpsProbe.Find();

            // 候选集合 = manifest.Files（备份时存在的） ∪ manifest.FileRecords（含备份时不存在、
            // 但关闭自启过程中才产生的 ksolaunch.real.exe） ∪ 现场探测到的关键路径。
            // D3：ksolaunch.real.exe 是 File.Move 出来的，首次备份时它不存在，
            //     光靠 manifest.Files 会漏掉它，恢复后原版仍带着「禁改禁删」ACE。
            List<string> unlockTargets = new List<string>();
            foreach (string f in m.Files) if (!unlockTargets.Contains(f)) unlockTargets.Add(f);
            foreach (string f in m.FileRecords) if (!unlockTargets.Contains(f)) unlockTargets.Add(f);
            if (layout != null)
            {
                foreach (string f in new string[] { layout.LauncherRoot, layout.LauncherReal, layout.LauncherO6 }) 
                    if (!string.IsNullOrEmpty(f) && !unlockTargets.Contains(f)) unlockTargets.Add(f);
            }

            int unlocked = 0;
            foreach (string f in unlockTargets)
            {
                if (string.IsNullOrEmpty(f)) continue;
                if (!File.Exists(f))
                {
                    // 备份时不存在、现在也不存在：属正常，不刷屏
                    continue;
                }
                if (Acl.RemoveDeny(f)) { unlocked++; log("已解锁：" + f); }
                else log("解锁失败（可能本就不存在或权限受限）：" + f);
            }
            log("共解锁 " + unlocked + " 个文件");

            List<string> dirTargets = new List<string>();
            foreach (string d in m.Dirs) if (!dirTargets.Contains(d)) dirTargets.Add(d);
            if (layout != null && !string.IsNullOrEmpty(layout.RootDir) && !dirTargets.Contains(layout.RootDir))
                dirTargets.Add(layout.RootDir);
            string cloudUpd = Path.Combine(Paths.LocalAppData, @"Kingsoft\WPS Cloud Files\update");
            if (Directory.Exists(cloudUpd) && !dirTargets.Contains(cloudUpd)) dirTargets.Add(cloudUpd);

            foreach (string d in dirTargets)
            {
                if (string.IsNullOrEmpty(d) || !Directory.Exists(d)) continue;
                if (Acl.RemoveDeny(d)) log("已解锁目录：" + d);
                else log("目录解锁失败：" + d);
            }

            log("");
            log("=== 2. 还原主入口 ===");
            string launcher = m.LauncherRoot;
            string real = m.LauncherReal;
            if (string.IsNullOrEmpty(launcher) && layout != null) launcher = layout.LauncherRoot;
            if (string.IsNullOrEmpty(real) && layout != null) real = layout.LauncherReal;
            if (string.IsNullOrEmpty(real) && !string.IsNullOrEmpty(launcher))
            {
                real = launcher.Substring(0, launcher.Length - 4) + ".real.exe";
            }

            if (!string.IsNullOrEmpty(real) && File.Exists(real))
            {
                try
                {
                    // 硬规则 3：重命名前必须把目录和两个文件的 DENY 都摘掉
                    Acl.RemoveDeny(Path.GetDirectoryName(launcher));
                    Acl.RemoveDeny(launcher);
                    Acl.RemoveDeny(real);
                    if (File.Exists(launcher)) File.Delete(launcher);
                    File.Move(real, launcher);
                    log("原版 ksolaunch.exe 已复位：" + launcher);
                    if (!string.IsNullOrEmpty(m.OriginalHash))
                    {
                        string h = WpsProbe.Sha256(launcher);
                        log(string.Equals(h, m.OriginalHash, StringComparison.OrdinalIgnoreCase)
                            ? "哈希校验通过，与原始一致"
                            : "哈希不一致：当前 " + h.Substring(0, 16) + "… 期望 " + m.OriginalHash.Substring(0, 16) + "…");
                    }
                }
                catch (Exception ex) { log("还原主入口失败：" + ex.Message); }
            }
            else log("没有 ksolaunch.real.exe，主入口可能本来就是原版");

            log("");
            log("=== 3. 重建计划任务 ===");
            int restored = 0;
            for (int i = 0; i < m.TaskXmls.Count; i++)
            {
                string xml = m.TaskXmls[i];
                if (!File.Exists(xml)) continue;
                string name = i < m.TaskNames.Count ? m.TaskNames[i] : Path.GetFileNameWithoutExtension(xml);
                if (Tasks.CreateFromXml(name, xml)) { log("已重建：" + name); restored++; }
                else log("重建失败：" + name);
            }
            if (m.TaskXmls.Count == 0) log("备份里没有任务 XML");
            else log("共重建 " + restored + " 条任务");

            log("");
            log("=== 4. 服务恢复 ===");
            if (Svc.Exists)
            {
                Svc.SetStart(m.ServiceOriginalStartMode);
                log("wpscloudsvr 启动类型已设为 " + m.ServiceOriginalStartMode + "，当前：" + Svc.State + " / " + Svc.StartMode);
            }
            else log("wpscloudsvr 不存在，跳过");

            log("");
            log("=== 5. Run / RunOnce 回写（D4）===");
            if (m.RunValues.Count == 0)
            {
                log("manifest 里没有 runValues（可能是旧版备份），跳过回写");
            }
            else
            {
                int wrote = RunScanner.Rewrite(m.RunValues, log);
                log("共回写 " + wrote + " 个 Run 值");
            }

            log("");
            log("=== 6. 锁残留复核（D3）===");
            int leftover = 0;
            List<string> verify = new List<string>();
            if (!string.IsNullOrEmpty(launcher)) verify.Add(launcher);
            if (!string.IsNullOrEmpty(real) && File.Exists(real)) verify.Add(real);
            foreach (string p in verify)
            {
                if (!File.Exists(p)) continue;
                if (Acl.HasDeny(p))
                {
                    leftover++;
                    log("WARN：仍有 DENY 残留，不是干净原装 —— " + p);
                }
                else log("干净（无 DENY）：" + p);
            }
            // real 已改名成主入口后就不存在了，这里补一句说明免得误解
            if (!string.IsNullOrEmpty(real) && !File.Exists(real))
                log("说明：" + real + " 已改名为主入口，不再单独存在");
            if (leftover == 0) log("原版文件已无任何 DENY 残留，可视为真原装");
            else log("WARN：有 " + leftover + " 个文件仍带 DENY，请用管理员重跑一次恢复");

            log("");
            log("恢复完成。建议重启一次让图标缓存与任务计划完全生效。");
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  状态快照（给 UI 状态网格用）
    // ────────────────────────────────────────────────────────────────────
    internal sealed class Snapshot
    {
        public int TaskCount;
        public string ServiceText;
        public int ServiceLevel;      // 0 绿 1 黄 2 红
        public int FileLocked;
        public int FileTotal;
        public int DirLocked;
        public int DirTotal;
        public bool ShimInstalled;
        public int ProcCount;
        public int UpgradeLevel;
        public string UpgradeText;

        public static Snapshot Take(WpsLayout l)
        {
            Snapshot s = new Snapshot();
            s.TaskCount = Tasks.Find().Count;

            if (Svc.Exists)
            {
                s.ServiceText = Svc.State + " / " + Svc.StartMode;
                s.ServiceLevel = Svc.StartMode == "Disabled" ? 0 : 1;
            }
            else
            {
                s.ServiceText = "未安装";
                s.ServiceLevel = 0;
            }

            // D11：锁计数统一走 FileLocks（口径与 selftest / 使用说明 一致）
            int locked, total;
            FileLocks.Count(l, out locked, out total);
            s.FileLocked = locked;
            s.FileTotal = total;

            List<string> dirs = new List<string>();
            if (l != null) dirs.Add(l.RootDir);
            string cloudUpd = Path.Combine(Paths.LocalAppData, @"Kingsoft\WPS Cloud Files\update");
            if (Directory.Exists(cloudUpd)) dirs.Add(cloudUpd);
            foreach (string d in dirs)
            {
                if (!Directory.Exists(d)) continue;
                s.DirTotal++;
                if (Acl.HasDeny(d)) s.DirLocked++;
            }

            s.ShimInstalled = WpsProbe.IsShimInstalled(l);
            s.ProcCount = ProcessKiller.CountWpsProcesses();

            string upSvr;
            int upLevel;
            s.UpgradeText = UpgradeChannel.State(out upLevel, out upSvr);
            s.UpgradeLevel = upLevel;

            return s;
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  UI
    // ────────────────────────────────────────────────────────────────────
    internal sealed class MainForm : Form
    {
        private readonly bool _isAdmin;
        private WpsLayout _layout;

        private Label _lblVersion, _lblRoot, _lblKind, _lblAdmin;
        private Panel _banner;
        private Label[,] _dots = new Label[7, 1];
        private Label[] _rowValues = new Label[7];
        private RichTextBox _log;
        private Button _btnDetect, _btnBackup, _btnKill, _btnRestore, _btnOpenLog, _btnElevate;
        private Button _btnUpdOff, _btnUpdOn;
        private bool _busy;

        public MainForm()
        {
            _isAdmin = IsAdmin();
            InitializeUi();
            Load += delegate { Detect(); };
            FormClosing += delegate(object s, FormClosingEventArgs e) { };
        }

        private static bool IsAdmin()
        {
            try
            {
                System.Security.Principal.WindowsIdentity id = System.Security.Principal.WindowsIdentity.GetCurrent();
                System.Security.Principal.WindowsPrincipal p = new System.Security.Principal.WindowsPrincipal(id);
                return p.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        // ── 布局 ──
        private void InitializeUi()
        {
            Text = "WPS 开机自启关闭器";
            MinimumSize = new Size(980, 760);
            Size = new Size(1020, 800);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9f);
            BackColor = Color.FromArgb(246, 247, 249);

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 5;
            root.Padding = new Padding(10);
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 58));
            Controls.Add(root);

            // ── 顶部：探测卡片 ──
            GroupBox card = new GroupBox();
            card.Text = "探测结果";
            card.Dock = DockStyle.Fill;
            card.Padding = new Padding(10, 6, 10, 6);
            root.Controls.Add(card, 0, 0);

            TableLayoutPanel ct = new TableLayoutPanel();
            ct.Dock = DockStyle.Fill;
            ct.ColumnCount = 2;
            ct.RowCount = 4;
            ct.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            ct.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 4; i++) ct.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
            card.Controls.Add(ct);

            _lblVersion = MakeValueLabel();
            _lblRoot = MakeValueLabel();
            _lblKind = MakeValueLabel();
            _lblAdmin = MakeValueLabel();

            AddCardRow(ct, 0, "版本目录", _lblVersion);
            AddCardRow(ct, 1, "安装根", _lblRoot);
            AddCardRow(ct, 2, "安装类型", _lblKind);
            AddCardRow(ct, 3, "管理员", _lblAdmin);

            // ── 非管理员黄色条 ──
            _banner = new Panel();
            _banner.Dock = DockStyle.Fill;
            _banner.BackColor = Color.FromArgb(255, 244, 204);
            _banner.BorderStyle = BorderStyle.FixedSingle;
            root.Controls.Add(_banner, 0, 1);

            Label warn = new Label();
            warn.Text = "当前不是管理员：计划任务删除、HKCU 之外的注册表、服务启动类型可能受限。";
            warn.AutoSize = false;
            warn.Dock = DockStyle.Fill;
            warn.Padding = new Padding(10, 0, 0, 0);
            warn.TextAlign = ContentAlignment.MiddleLeft;
            warn.ForeColor = Color.FromArgb(120, 80, 0);
            _banner.Controls.Add(warn);

            _btnElevate = new Button();
            _btnElevate.Text = "以管理员重启";
            _btnElevate.Width = 120;
            _btnElevate.Dock = DockStyle.Right;
            _btnElevate.Click += delegate { Elevate(); };
            _banner.Controls.Add(_btnElevate);

            // ── 中部：状态网格 ──
            GroupBox grid = new GroupBox();
            grid.Text = "状态";
            grid.Dock = DockStyle.Fill;
            grid.Padding = new Padding(10, 6, 10, 6);
            root.Controls.Add(grid, 0, 2);

            TableLayoutPanel gt = new TableLayoutPanel();
            gt.Dock = DockStyle.Fill;
            gt.ColumnCount = 3;
            gt.RowCount = 7;
            gt.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 26));
            gt.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
            gt.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 7; i++) gt.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / 7f));
            grid.Controls.Add(gt);

            string[] names = new string[]
            {
                "计划任务数", "wpscloudsvr", "文件锁", "目录锁", "主入口", "WPS 进程数", "升级通道"
            };
            for (int i = 0; i < names.Length; i++)
            {
                Label dot = new Label();
                dot.Text = "●";
                dot.Dock = DockStyle.Fill;
                dot.TextAlign = ContentAlignment.MiddleCenter;
                dot.ForeColor = Color.Gray;
                gt.Controls.Add(dot, 0, i);

                Label nm = new Label();
                nm.Text = names[i];
                nm.Dock = DockStyle.Fill;
                nm.TextAlign = ContentAlignment.MiddleLeft;
                gt.Controls.Add(nm, 1, i);

                Label val = new Label();
                val.Text = "—";
                val.Dock = DockStyle.Fill;
                val.TextAlign = ContentAlignment.MiddleLeft;
                gt.Controls.Add(val, 2, i);

                _dots[i, 0] = dot;
                _rowValues[i] = val;
            }

            // ── 按钮行 ──
            FlowLayoutPanel bar = new FlowLayoutPanel();
            bar.Dock = DockStyle.Fill;
            bar.FlowDirection = FlowDirection.LeftToRight;
            bar.WrapContents = false;
            bar.Padding = new Padding(0, 8, 0, 0);
            root.Controls.Add(bar, 0, 3);

            _btnDetect = MakeButton("检测", 100, delegate { Detect(); });
            _btnBackup = MakeButton("备份", 100, delegate { BackupOnly(); });
            _btnKill = MakeButton("关闭自启", 120, delegate { KillAsync(); });
            _btnRestore = MakeButton("恢复原装", 120, delegate { RestoreAsync(); });
            _btnUpdOff = MakeButton("禁用升级", 110, delegate { UpgradeDisableAsync(); });
            _btnUpdOn = MakeButton("恢复升级", 110, delegate { UpgradeRestoreAsync(); });
            _btnOpenLog = MakeButton("打开日志目录", 130, delegate { OpenLogDir(); });

            bar.Controls.Add(_btnDetect);
            bar.Controls.Add(_btnBackup);
            bar.Controls.Add(_btnKill);
            bar.Controls.Add(_btnRestore);
            bar.Controls.Add(_btnUpdOff);
            bar.Controls.Add(_btnUpdOn);
            bar.Controls.Add(_btnOpenLog);

            // ── 底部：日志 ──
            GroupBox logBox = new GroupBox();
            logBox.Text = "日志";
            logBox.Dock = DockStyle.Fill;
            logBox.Padding = new Padding(10, 6, 10, 6);
            root.Controls.Add(logBox, 0, 4);

            _log = new RichTextBox();
            _log.Dock = DockStyle.Fill;
            _log.ReadOnly = true;
            _log.BackColor = Color.FromArgb(30, 32, 36);
            _log.ForeColor = Color.Gainsboro;
            _log.Font = new Font("Consolas", 9f);
            _log.WordWrap = true;
            _log.BorderStyle = BorderStyle.None;
            logBox.Controls.Add(_log);

            _banner.Visible = !_isAdmin;
        }

        private static Label MakeValueLabel()
        {
            Label l = new Label();
            l.Dock = DockStyle.Fill;
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.AutoEllipsis = true;
            l.Text = "—";
            return l;
        }

        private static void AddCardRow(TableLayoutPanel t, int row, string caption, Label value)
        {
            Label c = new Label();
            c.Text = caption;
            c.Dock = DockStyle.Fill;
            c.TextAlign = ContentAlignment.MiddleLeft;
            c.ForeColor = Color.FromArgb(90, 96, 104);
            t.Controls.Add(c, 0, row);
            t.Controls.Add(value, 1, row);
        }

        private Button MakeButton(string text, int width, EventHandler onClick)
        {
            Button b = new Button();
            b.Text = text;
            b.Width = width;
            b.Height = 34;
            b.Margin = new Padding(0, 0, 8, 0);
            b.FlatStyle = FlatStyle.System;
            b.Click += onClick;
            return b;
        }

        // ── 日志 ──
        private void LogLine(string msg)
        {
            LogLine(msg, Color.Gainsboro);
        }

        private void LogLine(string msg, Color color)
        {
            if (InvokeRequired)
            {
                try { BeginInvoke((Action)delegate { LogLine(msg, color); }); } catch { }
                return;
            }
            try
            {
                _log.SelectionStart = _log.TextLength;
                _log.SelectionLength = 0;
                _log.SelectionColor = color;
                _log.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + msg + "\r\n");
                _log.SelectionColor = _log.ForeColor;
                _log.SelectionStart = _log.TextLength;
                _log.ScrollToCaret();
            }
            catch { }

            try
            {
                File.AppendAllText(Paths.LogFile,
                    "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + msg + "\r\n", Encoding.UTF8);
            }
            catch { }
        }

        private void SetBusy(bool busy)
        {
            if (InvokeRequired) { try { BeginInvoke((Action)delegate { SetBusy(busy); }); } catch { } return; }
            _busy = busy;
            _btnDetect.Enabled = !busy;
            _btnBackup.Enabled = !busy;
            _btnKill.Enabled = !busy;
            _btnRestore.Enabled = !busy;
            if (_btnUpdOff != null) _btnUpdOff.Enabled = !busy;
            if (_btnUpdOn != null) _btnUpdOn.Enabled = !busy;
            Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
        }

        private void RunAsync(string title, Action work)
        {
            if (_busy) { LogLine("正在执行其它操作，请稍候。", Color.Khaki); return; }
            SetBusy(true);
            LogLine("");
            LogLine(">>> " + title, Color.DeepSkyBlue);
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { work(); }
                catch (Exception ex) { LogLine("异常：" + ex.Message, Color.Salmon); }
                finally
                {
                    try { BeginInvoke((Action)delegate { SetBusy(false); RefreshStatus(); }); }
                    catch { }
                }
            });
        }

        // ── 动作 ──
        private void Detect()
        {
            RunAsync("检测安装布局与状态", delegate
            {
                _layout = WpsProbe.Find();
                if (_layout == null)
                {
                    LogLine("没有找到 WPS 安装。", Color.Salmon);
                    BeginInvoke((Action)delegate
                    {
                        _lblVersion.Text = "未找到 WPS";
                        _lblVersion.ForeColor = Color.Firebrick;
                        _lblRoot.Text = "—";
                        _lblKind.Text = "—";
                        _lblKind.ForeColor = Color.Gray;
                    });
                    return;
                }

                LogLine("版本目录 : " + _layout.VersionDir);
                LogLine("安装根   : " + _layout.RootDir);
                LogLine("office6  : " + (_layout.HasOffice6 ? "存在" : "缺失"));
                LogLine("wtoolex  : " + (_layout.HasWtoolex ? "存在" : "缺失（该版本可能没有更新器目录）"));
                LogLine("根启动器 : " + (_layout.HasLauncherRoot ? "存在" : "缺失（该布局无根级 ksolaunch）"));
                LogLine("门户     : " + (_layout.HasPortal ? "存在" : "缺失"));
                LogLine("探测来源 : " + _layout.FoundBy);
                LogLine("安装类型 : " + (_layout.MachineWide ? "全局安装（需管理员）" : "用户级安装"));
                LogLine("管理员   : " + (_isAdmin ? "是" : "否"));
                LogLine(WpsProbe.IsShimInstalled(_layout) ? "主入口   : 守卫壳（已封锁）" : "主入口   : 原版程序（未封锁）");

                if (_layout.MachineWide && !_isAdmin)
                    LogLine("注意：WPS 装在 Program Files，必须用管理员身份运行本程序才能改动。", Color.Khaki);

                string kind = _layout.MachineWide ? "全局安装（Program Files）" : "用户级安装（LocalAppData）";
                Color kindColor = _layout.MachineWide ? Color.DarkOrange : Color.SeaGreen;

                BeginInvoke((Action)delegate
                {
                    _lblVersion.Text = Path.GetFileName(_layout.VersionDir) + "   (" + _layout.VersionDir + ")";
                    _lblVersion.ForeColor = Color.FromArgb(30, 60, 110);
                    _lblRoot.Text = _layout.RootDir;
                    _lblKind.Text = kind;
                    _lblKind.ForeColor = kindColor;
                    _lblAdmin.Text = _isAdmin ? "是（可完整处置）" : "否（部分操作受限）";
                    _lblAdmin.ForeColor = _isAdmin ? Color.SeaGreen : Color.DarkOrange;
                });
            });
        }

        private void RefreshStatus()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                Snapshot s = Snapshot.Take(_layout);
                BeginInvoke((Action)delegate { ApplyStatus(s); });
            });
        }

        private void ApplyStatus(Snapshot s)
        {
            Set(0, s.TaskCount == 0 ? 0 : 2, s.TaskCount + " 条");
            Set(1, s.ServiceLevel, s.ServiceText);
            Set(2, s.FileTotal == 0 ? 1 : (s.FileLocked == s.FileTotal ? 0 : 2), s.FileLocked + " / " + s.FileTotal);
            Set(3, s.DirTotal == 0 ? 1 : (s.DirLocked == s.DirTotal ? 0 : 2), s.DirLocked + " / " + s.DirTotal);
            Set(4, s.ShimInstalled ? 0 : 1, s.ShimInstalled ? "守卫壳（已封锁）" : "原版程序（未封锁）");
            Set(5, s.ProcCount == 0 ? 0 : 1, s.ProcCount + " 个");
            Set(6, s.UpgradeLevel, s.UpgradeText);
        }

        private void Set(int row, int level, string text)
        {
            Color c;
            if (level == 0) c = Color.SeaGreen;
            else if (level == 3) c = Color.DimGray;      // 灰：正常（未干预）
            else if (level == 1) c = Color.Goldenrod;
            else c = Color.Firebrick;
            _dots[row, 0].ForeColor = c;
            _rowValues[row].Text = text;
            _rowValues[row].ForeColor = level == 2 ? Color.Firebrick : (level == 3 ? Color.DimGray : Color.Black);
        }

        private void BackupOnly()
        {
            if (_layout == null) { LogLine("请先点『检测』。", Color.Khaki); return; }
            RunAsync("备份现场", delegate
            {
                string dir = Path.Combine(Paths.ExeDir, "backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                Directory.CreateDirectory(dir);
                LogLine("备份目录：" + dir);
                List<WpsTask> ts = Tasks.Find();
                foreach (WpsTask t in ts)
                {
                    string xml = Tasks.ExportXml(t.Name);
                    if (string.IsNullOrEmpty(xml)) { LogLine("导出失败：" + t.Name); continue; }
                    string safe = Regex.Replace(t.Name, "[^A-Za-z0-9_.-]", "_");
                    File.WriteAllText(Path.Combine(dir, safe + ".xml"), xml, Encoding.UTF8);
                    LogLine("已导出任务：" + t.Name);
                }
                LogLine("任务数：" + ts.Count);
                LogLine("提示：完整备份（含 manifest 与哈希）会在点『关闭自启』时自动生成。");
            });
        }

        private void KillAsync()
        {
            if (_layout == null) { LogLine("请先点『检测』。", Color.Khaki); return; }
            string msg = "将执行以下动作：\r\n\r\n" +
                         "1) 结束所有 WPS 进程（未保存内容会丢失）\r\n" +
                         "2) 禁用并删除 WPS 计划任务\r\n" +
                         "3) 清扫 Run / RunOnce 中的 WPS 项\r\n" +
                         "4) 把 ksolaunch.exe 换成守卫壳（正常点图标仍可打开）\r\n" +
                         "5) 对 5 处载荷施加文件 ACL，对安装根施加目录 ACL\r\n" +
                         "6) 服务 wpscloudsvr 设为 Disabled" + (_isAdmin ? "" : "（非管理员会跳过）") + "\r\n\r\n" +
                         "全过程可回滚（『恢复原装』）。确认继续？";
            if (MessageBox.Show(this, msg, "确认关闭自启", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
            {
                LogLine("已取消。", Color.Khaki);
                return;
            }
            bool admin = _isAdmin;
            WpsLayout l = _layout;
            RunAsync("关闭自启", delegate { Killer.Run(l, admin, LogLine); });
        }

        private void RestoreAsync()
        {
            string latest = Restorer.FindLatestBackup();
            string msg = "将从备份恢复原装：\r\n\r\n" +
                         (latest == null ? "（没有找到 backup-* 目录，只能做保守恢复）" : "备份：" + latest) + "\r\n\r\n" +
                         "1) 解除所有 DENY ACL\r\n" +
                         "2) 删除守卫壳，把 ksolaunch.real.exe 改回 ksolaunch.exe\r\n" +
                         "3) 按备份 XML 重建计划任务\r\n" +
                         "4) wpscloudsvr 启动类型恢复\r\n\r\n" +
                         "恢复后 WPS 的自动更新与自启会全部回归。确认继续？";
            if (MessageBox.Show(this, msg, "确认恢复原装", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
            {
                LogLine("已取消。", Color.Khaki);
                return;
            }
            RunAsync("恢复原装", delegate { Restorer.Run(latest, LogLine); });
        }

        // ── 升级通道（独立操作，与「关闭自启」互不影响）──
        private void UpgradeDisableAsync()
        {
            string msg = "将把 WPS 的升级服务器地址改为本机不可达地址：\r\n\r\n" +
                         "HKCU\\SOFTWARE\\Kingsoft\\Office\\6.0\\Common\r\n" +
                         "  UpdateSvr    -> http://127.0.0.1:9/updateserver/update\r\n" +
                         "  UpdateSecSvr -> https://127.0.0.1:9/updateserver/update\r\n" +
                         "  NotifySvr    -> http://127.0.0.1:9/notifyserver/notify\r\n\r\n" +
                         "原值会先写入 upgrade-before.json，可随时点『恢复升级』还原。\r\n" +
                         "本操作与『关闭自启』互相独立，可单独执行、单独回滚。\r\n\r\n" +
                         "确认继续？";
            if (MessageBox.Show(this, msg, "确认禁用 WPS 升级", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
            {
                LogLine("已取消。", Color.Khaki);
                return;
            }
            RunAsync("禁用 WPS 升级", delegate
            {
                LogLine("──────── 升级通道 · 禁用 ────────", Color.Plum);
                bool ok = UpgradeChannel.Disable(delegate(string s) { LogLine(s); }, false);
                LogLine(ok ? "禁用升级完成。" : "禁用升级未完全成功，详见上方。", ok ? Color.LightGreen : Color.Salmon);
                LogLine("────────────────────────────────", Color.Plum);
            });
        }

        private void UpgradeRestoreAsync()
        {
            string msg = "将从备份还原 WPS 升级服务器地址：\r\n\r\n" +
                         "优先读 exe 同级的 upgrade-before.json，\r\n" +
                         "没有则读 upgrade-backup-*\\manifest.json 或最新 backup-*\\manifest.json 的 upgradeValues。\r\n\r\n" +
                         "确认继续？";
            if (MessageBox.Show(this, msg, "确认恢复 WPS 升级", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
            {
                LogLine("已取消。", Color.Khaki);
                return;
            }
            RunAsync("恢复 WPS 升级", delegate
            {
                LogLine("──────── 升级通道 · 恢复 ────────", Color.Plum);
                bool ok = UpgradeChannel.Restore(delegate(string s) { LogLine(s); });
                LogLine(ok ? "恢复升级完成。" : "恢复升级未完成，详见上方。", ok ? Color.LightGreen : Color.Salmon);
                LogLine("────────────────────────────────", Color.Plum);
            });
        }

        private void OpenLogDir()
        {
            try
            {
                if (File.Exists(Paths.LogFile)) LogLine("日志文件：" + Paths.LogFile);
                Process.Start("explorer.exe", "\"" + Paths.ExeDir + "\"");
            }
            catch (Exception ex) { LogLine("打开目录失败：" + ex.Message, Color.Salmon); }
        }

        private void Elevate()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath);
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                Process.Start(psi);
                Close();
            }
            catch (Exception ex)
            {
                LogLine("提权失败：" + ex.Message, Color.Salmon);
            }
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  自检（无窗口）
    // ────────────────────────────────────────────────────────────────────
    internal static class SelfTest
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);

        public static int Run()
        {
            try { AttachConsole(-1); } catch { }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("=== WpsAutostartKiller --selftest ===");
            sb.AppendLine("时间        : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("exe         : " + Application.ExecutablePath);
            sb.AppendLine("管理员      : " + (IsAdmin() ? "是" : "否"));
            sb.AppendLine();

            WpsLayout l = null;
            try { l = WpsProbe.Find(); }
            catch (Exception ex) { sb.AppendLine("探测异常: " + ex.Message); }

            if (l == null)
            {
                sb.AppendLine("探测结果    : 未找到 WPS 安装");
            }
            else
            {
                sb.AppendLine("[探测]");
                sb.AppendLine("版本目录    : " + l.VersionDir);
                sb.AppendLine("安装根      : " + l.RootDir);
                sb.AppendLine("office6     : " + (l.HasOffice6 ? "存在" : "缺失"));
                sb.AppendLine("wtoolex     : " + (l.HasWtoolex ? "存在" : "缺失"));
                sb.AppendLine("根启动器    : " + (l.HasLauncherRoot ? "存在" : "缺失"));
                sb.AppendLine("门户        : " + (l.HasPortal ? "存在" : "缺失"));
                sb.AppendLine("探测来源    : " + l.FoundBy);
                sb.AppendLine("安装类型    : " + (l.MachineWide ? "全局安装" : "用户级安装"));
            }
            sb.AppendLine();

            sb.AppendLine("[状态]");
            List<WpsTask> tasks = Tasks.Find();
            sb.AppendLine("计划任务数  : " + tasks.Count);
            foreach (WpsTask t in tasks) sb.AppendLine("    - " + t.Name + (string.IsNullOrEmpty(t.Exec) ? "" : "  ->  " + t.Exec));
            sb.AppendLine("wpscloudsvr : " + (Svc.Exists ? (Svc.State + " / " + Svc.StartMode) : "未安装"));

            int upLevel;
            string upSvr;
            string upState = UpgradeChannel.State(out upLevel, out upSvr);
            string upExcerpt = (upSvr == null ? "" : upSvr);
            if (upExcerpt.Length > 60) upExcerpt = upExcerpt.Substring(0, 60) + "…";
            if (upExcerpt.Length == 0) upExcerpt = "（无）";
            sb.AppendLine("升级通道    : " + upState + "  UpdateSvr=" + upExcerpt);

            if (l != null)
            {
                // D11：与 UI 状态网格、使用说明统一口径 —— 守卫壳与原版备份也计入
                foreach (string f in FileLocks.Candidates(l))
                {
                    if (!File.Exists(f)) continue;
                    // D9：根级 ksolaunch.exe 与 office6\ksolaunch.exe 同名，带上一级目录区分
                    string parent = Path.GetFileName(Path.GetDirectoryName(f));
                    string label = Path.GetFileName(f) + " (" + parent + ")";
                    sb.AppendLine(string.Format("锁 {0,-34}: {1}", label, Acl.HasDeny(f) ? "DENY 在位" : "未锁"));
                }
                int locked, total;
                FileLocks.Count(l, out locked, out total);
                sb.AppendLine("文件锁      : " + locked + " / " + total + "   （M=实际存在的载荷+守卫壳+原版备份）");
                sb.AppendLine("主入口      : " + (WpsProbe.IsShimInstalled(l) ? "守卫壳（已封锁）" : "原版程序（未封锁）"));
            }

            sb.AppendLine("WPS 进程数  : " + ProcessKiller.CountWpsProcesses());
            sb.AppendLine();

            sb.AppendLine("[开机判定]");
            try { sb.Append(BootJudge.Describe()); }
            catch (Exception ex) { sb.AppendLine("判定异常: " + ex.Message); }

            sb.AppendLine();
            sb.AppendLine("[结论]");
            bool lockedOk = l != null && WpsProbe.IsShimInstalled(l) && tasks.Count == 0;
            if (l == null) sb.AppendLine("无法判定：未找到 WPS");
            else if (lockedOk) sb.AppendLine("已封锁 —— 主入口是守卫壳、计划任务已清空");
            else sb.AppendLine("未封锁或部分封锁 —— 主入口=" + (WpsProbe.IsShimInstalled(l) ? "守卫壳" : "原版") + "，任务=" + tasks.Count);

            sb.AppendLine();
            sb.AppendLine("[环境]");
            sb.AppendLine("csc         : " + (Guard.FindCsc() ?? "未找到"));
            sb.AppendLine("守卫壳源码  : " + (Guard.GetSource() != null ? "可用" : "不可用"));
            sb.AppendLine("工作目录    : " + Paths.WorkDir);
            sb.AppendLine("日志        : " + Paths.LogFile);

            string text = sb.ToString();

            // 先落盘：无论控制台是否可写，自动化都能从文件断言
            string savedTo = null;
            try
            {
                savedTo = Path.Combine(Paths.ExeDir, "selftest-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
                File.WriteAllText(savedTo, text, Encoding.UTF8);
            }
            catch { savedTo = null; }

            if (savedTo != null) text += Environment.NewLine + "selftest 输出已保存: " + savedTo + Environment.NewLine;

            // GUI 子系统程序没有控制台时 Console.Out 是空写入器，必须直接写标准输出句柄
            try
            {
                using (Stream stdout = Console.OpenStandardOutput())
                {
                    if (stdout != null)
                    {
                        using (StreamWriter sw = new StreamWriter(stdout, new UTF8Encoding(false)))
                        {
                            sw.Write(text);
                            sw.Flush();
                        }
                    }
                }
            }
            catch { }

            return 0;
        }

        private static bool IsAdmin()
        {
            try
            {
                System.Security.Principal.WindowsIdentity id = System.Security.Principal.WindowsIdentity.GetCurrent();
                System.Security.Principal.WindowsPrincipal p = new System.Security.Principal.WindowsPrincipal(id);
                return p.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        /// <summary>
        /// --backup-only：只跑备份流程（读注册表/文件/任务 + 写 backup-* 目录），
        /// 不杀进程、不改 ACL、不动服务，可安全地在验收机上执行，用于检查 manifest 字段。
        /// </summary>
        public static int RunBackupOnly()
        {
            try { AttachConsole(-1); } catch { }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("=== WpsAutostartKiller --backup-only（只备份，不改系统）===");
            sb.AppendLine("时间   : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("管理员 : " + (IsAdmin() ? "是" : "否"));

            WpsLayout l = WpsProbe.Find();
            if (l == null)
            {
                sb.AppendLine("未找到 WPS 安装，退出。");
                WriteStdout(sb.ToString());
                return 1;
            }

            List<string> steps = new List<string>();
            string dir = Killer.BackupOnly(l, IsAdmin(), delegate(string s) { steps.Add(s); });

            sb.AppendLine("备份目录: " + (dir == null ? "（失败）" : dir));
            sb.AppendLine();
            sb.AppendLine("[备份过程]");
            foreach (string s in steps) sb.AppendLine("  " + s);

            if (!string.IsNullOrEmpty(dir))
            {
                string mf = Path.Combine(dir, "manifest.json");
                sb.AppendLine();
                sb.AppendLine("[manifest 字段检查]");
                if (File.Exists(mf))
                {
                    string json = File.ReadAllText(mf, Encoding.UTF8);
                    string[] fields = new string[]
                    {
                        "createdAt", "user", "layout", "versionDir", "rootDir",
                        "launcherRoot", "launcherReal", "originalHash",
                        "files", "fileRecords", "dirs",
                        "tasks", "taskNames", "taskXmls",
                        "service", "services", "runValues"
                    };
                    foreach (string f in fields)
                    {
                        bool hit = json.IndexOf("\"" + f + "\"", StringComparison.Ordinal) >= 0;
                        sb.AppendLine(string.Format("  {0,-16}: {1}", f, hit ? "有" : "缺失"));
                    }
                    sb.AppendLine();
                    sb.AppendLine("[manifest.json 原文]");
                    sb.AppendLine(json);
                }
                else sb.AppendLine("  manifest.json 不存在！");
            }

            WriteStdout(sb.ToString());
            return 0;
        }

        private static void WriteStdout(string text)
        {
            try
            {
                using (Stream stdout = Console.OpenStandardOutput())
                {
                    if (stdout == null) return;
                    using (StreamWriter sw = new StreamWriter(stdout, new UTF8Encoding(false)))
                    {
                        sw.Write(text);
                        sw.Flush();
                    }
                }
            }
            catch { }
        }

        // ── 升级通道相关 CLI ──

        public static int RunUpgradeTest()
        {
            try { AttachConsole(-1); } catch { }
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("=== WpsAutostartKiller --upgrade-test ===");
            sb.AppendLine("时间   : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("说明   : 备份 → 改 → 读回 → 立刻还原 → 再读回，全程打印对照");
            sb.AppendLine();
            int rc = 1;
            try
            {
                rc = UpgradeChannel.RoundTripTest(delegate(string s) { sb.AppendLine(s); });
            }
            catch (Exception ex) { sb.AppendLine("异常：" + ex.Message); rc = 1; }
            WriteStdout(sb.ToString());
            return rc;
        }

        public static int RunUpgradeState()
        {
            try { AttachConsole(-1); } catch { }
            StringBuilder sb = new StringBuilder();
            int level;
            string svr;
            string state = UpgradeChannel.State(out level, out svr);
            sb.AppendLine("升级通道 : " + state + "   [级别 " + level + "]");
            sb.AppendLine("注册表   : " + UpgradeChannel.KeyDisplay);
            foreach (UpgradeTarget t in UpgradeChannel.Read())
            {
                sb.AppendLine(string.Format("  {0,-13}: {1}", t.Name, t.Exists ? (t.OriginalValue + "   [" + t.Kind + "]") : "（不存在）"));
            }
            WriteStdout(sb.ToString());
            return 0;
        }

        public static int RunUpgradeDisable()
        {
            try { AttachConsole(-1); } catch { }
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("=== WpsAutostartKiller --upgrade-disable ===");
            sb.AppendLine();
            bool ok = false;
            try { ok = UpgradeChannel.Disable(delegate(string s) { sb.AppendLine(s); }, false); }
            catch (Exception ex) { sb.AppendLine("异常：" + ex.Message); }
            WriteStdout(sb.ToString());
            return ok ? 0 : 1;
        }

        public static int RunUpgradeRestore()
        {
            try { AttachConsole(-1); } catch { }
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("=== WpsAutostartKiller --upgrade-restore ===");
            sb.AppendLine();
            bool ok = false;
            try { ok = UpgradeChannel.Restore(delegate(string s) { sb.AppendLine(s); }); }
            catch (Exception ex) { sb.AppendLine("异常：" + ex.Message); }
            WriteStdout(sb.ToString());
            return ok ? 0 : 1;
        }
    }
}
