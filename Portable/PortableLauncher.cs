// 单文件启动器：exe 内嵌整个程序目录（payload.zip），首次运行解压到
// %LOCALAPPDATA%\SynJMD\<AppId>\，之后直接启动该目录中的主程序。
// 程序在自身目录读写数据并由 JmdUpdater 原地更新，因此不能使用只读的虚拟文件系统。
// 规则：
//   已安装版本 >= 内嵌版本：直接启动（在线更新后的新版本不会被旧 exe 覆盖回去）。
//   未安装、内嵌版本更新或上次解压未完成：解压到临时目录，再逐个复制到安装目录；
//     已存在的现场数据（PreservePatterns）保留不覆盖；
//     .jmd-update/installed.json 与 jmd-version.json 最后写入，未完成标记最后删除。
//   安装期间持有与 JmdUpdater 安装器相同的目录锁，两者不会同时改写目录。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle(PortableConfig.Title)]
[assembly: AssemblyProduct(PortableConfig.Title)]
[assembly: AssemblyVersion(PortableConfig.Version)]
[assembly: AssemblyFileVersion(PortableConfig.Version)]

internal static class PortableLauncher
{
    private const string VersionFile = "jmd-version.json";
    private const string InstalledRecord = ".jmd-update/installed.json";
    private const string PendingMarker = ".jmd-portable-installing";

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SynJMD");
            string installDir = Path.Combine(root, PortableConfig.AppId);
            string entry;

            using (Mutex mutex = new Mutex(false, InstallMutexName(installDir)))
            {
                bool owned = false;
                try
                {
                    try { owned = mutex.WaitOne(TimeSpan.FromMinutes(3)); }
                    catch (AbandonedMutexException) { owned = true; }
                    if (!owned) { Fail("程序正在安装或更新，请稍后再试。"); return 1; }

                    entry = ResolveInstalledEntry(installDir);
                    if (entry == null)
                    {
                        Install(root, installDir);
                        entry = Path.Combine(installDir, PortableConfig.EntryExe);
                    }
                }
                finally
                {
                    if (owned) mutex.ReleaseMutex();
                }
            }

            if (!File.Exists(entry)) { Fail("找不到主程序：" + entry); return 1; }
            ProcessStartInfo psi = new ProcessStartInfo(entry, JoinArgs(args));
            psi.WorkingDirectory = installDir;
            psi.UseShellExecute = false;
            Process.Start(psi);
            return 0;
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
            return 1;
        }
    }

    // 已安装且不比内嵌包旧时返回可启动的入口；需要（重新）安装时返回 null。
    private static string ResolveInstalledEntry(string installDir)
    {
        if (!Directory.Exists(installDir) || File.Exists(Path.Combine(installDir, PendingMarker))) return null;
        Version installed = ReadInstalledVersion(installDir);
        if (installed == null || Normalize(installed) < Normalize(new Version(PortableConfig.Version))) return null;

        string entry = Path.Combine(installDir, PortableConfig.EntryExe);
        if (File.Exists(entry)) return entry;
        // 在线更新可能已把主程序改名：按更新器记录的入口启动，不用旧包覆盖新版本。
        string renamed = ReadRecordedEntry(installDir);
        if (renamed != null && File.Exists(renamed)) return renamed;
        return null;
    }

    private static Version ReadInstalledVersion(string installDir)
    {
        try
        {
            string json = File.ReadAllText(Path.Combine(installDir, VersionFile));
            if (!Regex.IsMatch(json, "\"AppId\"\\s*:\\s*\"" + Regex.Escape(PortableConfig.AppId) + "\"", RegexOptions.IgnoreCase)) return null;
            Match m = Regex.Match(json, "\"Version\"\\s*:\\s*\"([0-9]+(?:\\.[0-9]+){1,3})\"");
            return m.Success ? new Version(m.Groups[1].Value) : null;
        }
        catch { return null; }
    }

    // .jmd-update/installed.json 的 Release.Payload 是 base64 的发布清单，其中 EntryPoint 为当前入口。
    private static string ReadRecordedEntry(string installDir)
    {
        try
        {
            string record = File.ReadAllText(Path.Combine(installDir, InstalledRecord.Replace('/', Path.DirectorySeparatorChar)));
            Match p = Regex.Match(record, "\"Payload\"\\s*:\\s*\"([A-Za-z0-9+/=]+)\"");
            if (!p.Success) return null;
            string manifest = Encoding.UTF8.GetString(Convert.FromBase64String(p.Groups[1].Value));
            Match e = Regex.Match(manifest, "\"EntryPoint\"\\s*:\\s*\"([^\"]+)\"");
            if (!e.Success) return null;
            string full = Path.GetFullPath(Path.Combine(installDir, e.Groups[1].Value.Replace('/', Path.DirectorySeparatorChar)));
            string baseFull = Path.GetFullPath(installDir + Path.DirectorySeparatorChar);
            if (!full.StartsWith(baseFull, StringComparison.OrdinalIgnoreCase) ||
                !full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
            return full;
        }
        catch { return null; }
    }

    private static Version Normalize(Version v)
    {
        return new Version(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
    }

    private static void Install(string root, string installDir)
    {
        Directory.CreateDirectory(root);
        string staging = Path.Combine(root, "." + PortableConfig.AppId + ".staging-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (Stream payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"))
            {
                if (payload == null) throw new InvalidDataException("启动器内缺少程序包。");
                using (ZipArchive zip = new ZipArchive(payload, ZipArchiveMode.Read))
                    ExtractSafely(zip, staging);
            }

            Directory.CreateDirectory(installDir);
            string marker = Path.Combine(installDir, PendingMarker);
            File.WriteAllText(marker, PortableConfig.Version);

            List<string> last = new List<string>();
            foreach (string file in Directory.GetFiles(staging, "*", SearchOption.AllDirectories))
            {
                string relative = file.Substring(staging.Length + 1).Replace('\\', '/');
                if (relative.Equals(InstalledRecord, StringComparison.OrdinalIgnoreCase) ||
                    relative.Equals(VersionFile, StringComparison.OrdinalIgnoreCase)) continue;
                Place(file, relative, installDir);
            }
            foreach (string relative in new[] { InstalledRecord, VersionFile })
            {
                string file = Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(file)) Place(file, relative, installDir);
            }
            File.Delete(marker);
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
        }
    }

    private static void Place(string source, string relative, string installDir)
    {
        string target = Path.Combine(installDir, relative.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(target) && IsPreserved(relative)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(target));
        // 先完整写到临时文件再换名：中途中断不会留下“存在但不完整”的数据文件被当作现场数据保留。
        string temp = target + ".jmdtmp";
        try
        {
            File.Copy(source, temp, true);
            if (File.Exists(target))
            {
                File.SetAttributes(target, FileAttributes.Normal);
                File.Delete(target);
            }
            File.Move(temp, target);
        }
        catch (Exception ex)
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            if (ex is IOException || ex is UnauthorizedAccessException)
                throw new IOException("无法写入 " + target + "\n请先关闭正在运行的 " + PortableConfig.Title + " 后再试。\n\n" + ex.Message, ex);
            throw;
        }
    }

    private static void ExtractSafely(ZipArchive zip, string staging)
    {
        string baseFull = Path.GetFullPath(staging + Path.DirectorySeparatorChar);
        foreach (ZipArchiveEntry e in zip.Entries)
        {
            if (String.IsNullOrEmpty(e.Name)) continue; // 目录项
            string dest = Path.GetFullPath(Path.Combine(staging, e.FullName));
            if (!dest.StartsWith(baseFull, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("程序包含有非法路径：" + e.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(dest));
            e.ExtractToFile(dest, true);
        }
    }

    // 与 JmdUpdater PreservePaths 同一语义：精确文件、目录前缀、*.扩展名（任意层级）。
    private static bool IsPreserved(string relative)
    {
        foreach (string raw in PortableConfig.PreservePatterns)
        {
            string p = raw.Replace('\\', '/').Trim('/');
            if (p.Length == 0) continue;
            if (p.StartsWith("*."))
            {
                if (relative.EndsWith(p.Substring(1), StringComparison.OrdinalIgnoreCase)) return true;
            }
            else if (relative.Equals(p, StringComparison.OrdinalIgnoreCase) ||
                     relative.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    // 与 JmdUpdater.InstallerService.InstallMutexName 保持一致。
    private static string InstallMutexName(string installDirectory)
    {
        string normalized = Path.GetFullPath(installDirectory).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
        using (SHA256 hash = SHA256.Create())
            return "Local\\JmdUpdater.Install." + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(normalized))).Replace("-", "").ToLowerInvariant();
    }

    private static string JoinArgs(string[] args)
    {
        List<string> parts = new List<string>();
        foreach (string a in args)
        {
            if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) { parts.Add(a); continue; }
            // Windows 命令行转义：引号前的反斜杠加倍再转义引号；结尾反斜杠加倍
            string escaped = Regex.Replace(a, "(\\\\*)\"", "$1$1\\\"");
            escaped = Regex.Replace(escaped, "(\\\\+)$", "$1$1");
            parts.Add("\"" + escaped + "\"");
        }
        return String.Join(" ", parts.ToArray());
    }

    private static void Fail(string message)
    {
        MessageBox.Show(message, PortableConfig.Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
