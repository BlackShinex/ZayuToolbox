using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace Zayu.Core
{
    public enum WebView2Status
    {
        Unknown = 0,
        NotInstalled = 1,
        Installed = 2,
        Broken = 3
    }

    public class WebView2Installation
    {
        public string Version { get; set; }
        public string ApplicationRoot { get; set; }
        public string VersionFolder { get; set; }
        public string Location { get; set; }
        public string ExecutablePath { get; set; }
        public string Scope { get; set; }
        public bool IsMachine { get; set; }
        public string Source { get; set; }
        public string RegistryPath { get; set; }
        public bool Healthy { get; set; }

        public WebView2Installation()
        {
            Version = string.Empty;
            ApplicationRoot = string.Empty;
            VersionFolder = string.Empty;
            Location = string.Empty;
            ExecutablePath = string.Empty;
            Scope = string.Empty;
            Source = string.Empty;
            RegistryPath = string.Empty;
        }
    }

    public class WebView2DetectionResult
    {
        public WebView2Status Status { get; set; }
        public List<WebView2Installation> Installations { get; set; }
        public WebView2Installation Primary { get; set; }
        public bool IsAdmin { get; set; }
        public string OsName { get; set; }
        public string OsDisplay { get; set; }
        public string OsLabel { get; set; }
        public string OsShort { get; set; }
        public string OsArch { get; set; }
        public string EdgeVersion { get; set; }
        public DateTime DetectedAt { get; set; }
        public List<string> Warnings { get; set; }

        public WebView2DetectionResult()
        {
            Status = WebView2Status.Unknown;
            Installations = new List<WebView2Installation>();
            OsName = string.Empty;
            OsDisplay = string.Empty;
            OsLabel = string.Empty;
            OsShort = string.Empty;
            OsArch = string.Empty;
            EdgeVersion = string.Empty;
            Warnings = new List<string>();
        }

        public string StatusText
        {
            get
            {
                switch (Status)
                {
                    case WebView2Status.Installed: return "已安装";
                    case WebView2Status.NotInstalled: return "未安装";
                    case WebView2Status.Broken: return "安装异常";
                    default: return "检测中";
                }
            }
        }

        public string ToDiagnostics()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("杂鱼串流工具箱 · WebView2 运行库检测");
            sb.AppendLine("检测时间：" + DetectedAt.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("检测结果：" + StatusText);
            if (Primary != null)
            {
                sb.AppendLine("运行库版本：" + NullSafe(Primary.Version));
                sb.AppendLine("安装范围：" + NullSafe(Primary.Scope));
                sb.AppendLine("安装状态：" + (Primary.Healthy ? "文件完整" : "文件缺失"));
                sb.AppendLine("安装目录：" + NullSafe(Primary.Location));
                sb.AppendLine("可执行文件：" + NullSafe(Primary.ExecutablePath));
                sb.AppendLine("检测来源：" + NullSafe(Primary.Source));
                if (!string.IsNullOrEmpty(Primary.RegistryPath))
                    sb.AppendLine("注册表位置：" + Primary.RegistryPath);
            }
            else
            {
                sb.AppendLine("运行库版本：未检测到");
            }
            sb.AppendLine("安装条目数：" + Installations.Count);
            for (int i = 0; i < Installations.Count; i++)
            {
                WebView2Installation item = Installations[i];
                sb.AppendLine("  [" + (i + 1) + "] " + item.Version + " | " + item.Scope + " | " + (item.Healthy ? "正常" : "文件缺失") + " | " + item.Source);
            }
            sb.AppendLine("操作系统：" + NullSafe(OsDisplay));
            sb.AppendLine("系统架构：" + NullSafe(OsArch));
            sb.AppendLine("当前权限：" + (IsAdmin ? "管理员" : "普通用户"));
            sb.AppendLine("Edge 浏览器：" + (string.IsNullOrEmpty(EdgeVersion) ? "未检测到" : EdgeVersion));
            if (Warnings.Count > 0)
            {
                sb.AppendLine("提示：");
                foreach (string warning in Warnings) sb.AppendLine("  - " + warning);
            }
            return sb.ToString();
        }

        private static string NullSafe(string value)
        {
            return string.IsNullOrEmpty(value) ? "—" : value;
        }
    }

    public static class WebView2Detector
    {
        public const string WebView2ClientGuid = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
        public const string EdgeClientGuid = "{56EB18F8-B008-4CBD-B6D2-8C97FE7E9062}";

        public static WebView2DetectionResult Detect()
        {
            WebView2DetectionResult result = new WebView2DetectionResult();
            result.DetectedAt = DateTime.Now;
            result.OsArch = GetOsArchitecture();
            result.IsAdmin = IsAdministrator();

            try
            {
                ReadOperatingSystem(result);
            }
            catch (Exception ex)
            {
                result.OsName = "Windows";
                result.OsDisplay = "Windows";
                result.OsShort = "Windows";
                result.Warnings.Add("读取系统信息失败：" + ex.Message);
            }

            try
            {
                result.EdgeVersion = TryGetEdgeVersion();
            }
            catch
            {
                result.EdgeVersion = string.Empty;
            }

            List<WebView2Installation> installs = new List<WebView2Installation>();
            try
            {
                installs.AddRange(ReadRegistryInstallations());
            }
            catch (Exception ex)
            {
                result.Warnings.Add("读取 Edge Update 注册表失败：" + ex.Message);
            }

            try
            {
                installs.AddRange(ScanInstallationFolders());
            }
            catch (Exception ex)
            {
                result.Warnings.Add("扫描安装目录失败：" + ex.Message);
            }

            result.Installations = MergeInstallations(installs);

            List<WebView2Installation> healthy = result.Installations
                .Where(i => i.Healthy)
                .OrderByDescending(i => ParseVersionSafe(i.Version))
                .ThenByDescending(i => i.IsMachine)
                .ToList();

            if (healthy.Count > 0)
            {
                result.Status = WebView2Status.Installed;
                result.Primary = healthy[0];
            }
            else if (result.Installations.Count > 0)
            {
                result.Status = WebView2Status.Broken;
                result.Primary = result.Installations
                    .OrderByDescending(i => ParseVersionSafe(i.Version))
                    .ThenByDescending(i => i.IsMachine)
                    .First();
            }
            else
            {
                result.Status = WebView2Status.NotInstalled;
                result.Primary = null;
            }

            if (result.Installations.Count > 1)
            {
                int distinct = result.Installations.Select(i => i.Version).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                if (distinct > 1)
                    result.Warnings.Add("检测到多个运行库版本，WebView2 应用通常会使用其中较新的版本。");
                if (result.Installations.Any(i => i.IsMachine) && result.Installations.Any(i => !i.IsMachine))
                    result.Warnings.Add("检测到系统级与当前用户级同时存在安装记录，这是常见情况。");
            }

            return result;
        }

        private static void ReadOperatingSystem(WebView2DetectionResult result)
        {
            string product = string.Empty;
            string display = string.Empty;
            string build = string.Empty;
            int ubr = 0;

            using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (RegistryKey key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
            {
                if (key != null)
                {
                    product = key.GetValue("ProductName") as string;
                    display = key.GetValue("DisplayVersion") as string;
                    build = key.GetValue("CurrentBuild") as string;
                    if (string.IsNullOrEmpty(build)) build = key.GetValue("CurrentBuildNumber") as string;
                    object ubrValue = key.GetValue("UBR");
                    if (ubrValue is int) ubr = (int)ubrValue;
                }
            }

            if (string.IsNullOrEmpty(product)) product = "Windows";
            if (string.IsNullOrEmpty(display)) display = Environment.OSVersion.Version.ToString();

            int buildNumber;
            if (!int.TryParse(build, out buildNumber))
            {
                buildNumber = Environment.OSVersion.Version.Build;
                build = buildNumber.ToString();
            }

            product = NormalizeWindowsProductName(product, buildNumber);

            result.OsName = product;
            result.OsLabel = product + " " + display;
            result.OsDisplay = product + " " + display + " (Build " + build + (ubr > 0 ? "." + ubr.ToString() : string.Empty) + ")";
            result.OsShort = product + " · " + (result.OsArch == "x64" || result.OsArch == "ARM64" ? "64 位" : "32 位");
        }

        /// <summary>
        /// Windows 11 often keeps the legacy registry ProductName "Windows 10" on some
        /// SKUs/builds. The NT build number is authoritative: any client build >= 22000
        /// is Windows 11 (Windows Server builds are left untouched).
        /// </summary>
        private static string NormalizeWindowsProductName(string product, int buildNumber)
        {
            if (string.IsNullOrEmpty(product)) return "Windows";
            if (buildNumber < 22000) return product;
            if (product.IndexOf("Server", StringComparison.OrdinalIgnoreCase) >= 0) return product;
            if (product.StartsWith("Windows 10", StringComparison.OrdinalIgnoreCase))
                return "Windows 11" + product.Substring("Windows 10".Length);
            return product;
        }

        private static List<WebView2Installation> ReadRegistryInstallations()
        {
            List<WebView2Installation> list = new List<WebView2Installation>();

            AddRegistryInstallation(list, RegistryHive.LocalMachine, RegistryView.Registry64,
                @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\" + WebView2ClientGuid,
                true, "HKLM\\SOFTWARE\\WOW6432Node\\Microsoft\\EdgeUpdate\\Clients");

            // A few native/ARM64 installations register the same client without the WOW6432Node prefix.
            AddRegistryInstallation(list, RegistryHive.LocalMachine, RegistryView.Registry64,
                @"SOFTWARE\Microsoft\EdgeUpdate\Clients\" + WebView2ClientGuid,
                true, "HKLM\\SOFTWARE\\Microsoft\\EdgeUpdate\\Clients");

            if (!Environment.Is64BitOperatingSystem)
            {
                AddRegistryInstallation(list, RegistryHive.LocalMachine, RegistryView.Registry32,
                    @"SOFTWARE\Microsoft\EdgeUpdate\Clients\" + WebView2ClientGuid,
                    true, "HKLM\\SOFTWARE\\Microsoft\\EdgeUpdate\\Clients");
            }

            AddRegistryInstallation(list, RegistryHive.CurrentUser, RegistryView.Registry64,
                @"Software\Microsoft\EdgeUpdate\Clients\" + WebView2ClientGuid,
                false, "HKCU\\Software\\Microsoft\\EdgeUpdate\\Clients");

            if (Environment.Is64BitOperatingSystem)
            {
                AddRegistryInstallation(list, RegistryHive.CurrentUser, RegistryView.Registry32,
                    @"Software\Microsoft\EdgeUpdate\Clients\" + WebView2ClientGuid,
                    false, "HKCU\\Software\\Microsoft\\EdgeUpdate\\Clients");
            }

            return list;
        }

        private static void AddRegistryInstallation(List<WebView2Installation> list, RegistryHive hive, RegistryView view, string subKey, bool machine, string hiveLabel)
        {
            try
            {
                using (RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view))
                using (RegistryKey key = baseKey.OpenSubKey(subKey))
                {
                    if (key == null) return;

                    string pv = key.GetValue("pv") as string;
                    Version version;
                    if (string.IsNullOrWhiteSpace(pv) || !Version.TryParse(pv, out version) || version <= new Version(0, 0, 0, 0))
                        return;

                    string location = key.GetValue("location") as string;
                    string appRoot = string.IsNullOrEmpty(location)
                        ? (machine ? GetMachineApplicationRoot() : GetUserApplicationRoot())
                        : location.TrimEnd('\\');

                    WebView2Installation item = BuildInstallation(pv, appRoot, machine, "Edge Update 注册表 (" + hiveLabel + ")", hiveLabel + "\\" + WebView2ClientGuid);
                    list.Add(item);
                }
            }
            catch (Exception)
            {
                // A registry view may be unavailable; callers try the other view and folder scan.
            }
        }

        private static WebView2Installation BuildInstallation(string version, string appRoot, bool machine, string source, string registryPath)
        {
            WebView2Installation item = new WebView2Installation();
            item.Version = version;
            item.ApplicationRoot = appRoot;
            item.Scope = machine ? "系统级（所有用户）" : "当前用户";
            item.IsMachine = machine;
            item.Source = source;
            item.RegistryPath = registryPath;

            string directExe = Path.Combine(appRoot, "msedgewebview2.exe");
            string versionFolder = Path.Combine(appRoot, version);
            string versionExe = Path.Combine(versionFolder, "msedgewebview2.exe");

            if (File.Exists(versionExe))
            {
                item.ExecutablePath = versionExe;
                item.VersionFolder = versionFolder;
                item.Location = versionFolder;
                item.Healthy = true;
            }
            else if (File.Exists(directExe))
            {
                item.ExecutablePath = directExe;
                item.VersionFolder = appRoot;
                item.Location = appRoot;
                item.Healthy = true;
            }
            else
            {
                item.ExecutablePath = versionExe;
                item.VersionFolder = versionFolder;
                item.Location = versionFolder;
                item.Healthy = false;
            }

            return item;
        }

        private static List<WebView2Installation> ScanInstallationFolders()
        {
            List<WebView2Installation> list = new List<WebView2Installation>();
            foreach (string root in GetMachineApplicationRoots())
                ScanFolder(list, root, true, "安装目录扫描 (系统级)");
            ScanFolder(list, GetUserApplicationRoot(), false, "安装目录扫描 (当前用户)");
            return list;
        }

        private static List<string> GetMachineApplicationRoots()
        {
            List<string> roots = new List<string>();
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (string.IsNullOrEmpty(programFilesX86))
                programFilesX86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
            if (!string.IsNullOrEmpty(programFilesX86))
                roots.Add(Path.Combine(programFilesX86, "Microsoft", "EdgeWebView", "Application"));

            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (string.IsNullOrEmpty(programFiles))
                programFiles = Environment.GetEnvironmentVariable("ProgramFiles");
            if (!string.IsNullOrEmpty(programFiles))
            {
                string nativeRoot = Path.Combine(programFiles, "Microsoft", "EdgeWebView", "Application");
                if (!roots.Any(r => string.Equals(r, nativeRoot, StringComparison.OrdinalIgnoreCase)))
                    roots.Add(nativeRoot);
            }

            return roots;
        }

        private static void ScanFolder(List<WebView2Installation> list, string root, bool machine, string source)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;

            string[] directories;
            try
            {
                directories = Directory.GetDirectories(root);
            }
            catch
            {
                return;
            }

            foreach (string directory in directories)
            {
                string name = Path.GetFileName(directory);
                Version version;
                if (!Version.TryParse(name, out version) || version <= new Version(0, 0, 0, 0))
                    continue;

                string exe = Path.Combine(directory, "msedgewebview2.exe");
                if (!File.Exists(exe)) continue;

                WebView2Installation item = new WebView2Installation();
                item.Version = name;
                item.ApplicationRoot = root;
                item.VersionFolder = directory;
                item.Location = directory;
                item.ExecutablePath = exe;
                item.Scope = machine ? "系统级（所有用户）" : "当前用户";
                item.IsMachine = machine;
                item.Source = source;
                item.RegistryPath = string.Empty;
                item.Healthy = true;
                list.Add(item);
            }
        }

        private static List<WebView2Installation> MergeInstallations(List<WebView2Installation> input)
        {
            List<WebView2Installation> merged = new List<WebView2Installation>();
            foreach (WebView2Installation item in input)
            {
                WebView2Installation existing = merged.FirstOrDefault(i =>
                    i.IsMachine == item.IsMachine &&
                    string.Equals(i.Version, item.Version, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                {
                    merged.Add(item);
                    continue;
                }

                // A healthy folder can outlive a stale registry path. Prefer the healthy record,
                // but keep the registry evidence for diagnostics.
                if (item.Healthy && !existing.Healthy)
                {
                    if (string.IsNullOrEmpty(item.RegistryPath))
                        item.RegistryPath = existing.RegistryPath;
                    if (!string.IsNullOrEmpty(existing.Source) && existing.Source.StartsWith("Edge Update", StringComparison.OrdinalIgnoreCase))
                        item.Source = existing.Source;
                    int index = merged.IndexOf(existing);
                    merged[index] = item;
                }
            }
            return merged
                .OrderByDescending(i => ParseVersionSafe(i.Version))
                .ThenByDescending(i => i.IsMachine)
                .ToList();
        }

        public static string GetMachineApplicationRoot()
        {
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (string.IsNullOrEmpty(programFilesX86))
                programFilesX86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
            if (string.IsNullOrEmpty(programFilesX86))
                programFilesX86 = Environment.GetEnvironmentVariable("ProgramFiles");
            return Path.Combine(programFilesX86, "Microsoft", "EdgeWebView", "Application");
        }

        public static string GetUserApplicationRoot()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "Microsoft", "EdgeWebView", "Application");
        }

        private static string TryGetEdgeVersion()
        {
            try
            {
                using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (RegistryKey key = baseKey.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\" + EdgeClientGuid))
                {
                    if (key != null)
                    {
                        string pv = key.GetValue("pv") as string;
                        if (!string.IsNullOrWhiteSpace(pv)) return pv;
                    }
                }
            }
            catch
            {
            }

            string[] candidates = new string[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft\Edge\Application\msedge.exe")
            };
            foreach (string candidate in candidates)
            {
                try
                {
                    if (File.Exists(candidate))
                    {
                        System.Diagnostics.FileVersionInfo info = System.Diagnostics.FileVersionInfo.GetVersionInfo(candidate);
                        if (!string.IsNullOrEmpty(info.FileVersion)) return info.FileVersion;
                    }
                }
                catch
                {
                }
            }
            return string.Empty;
        }

        private static bool IsAdministrator()
        {
            try
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    WindowsPrincipal principal = new WindowsPrincipal(identity);
                    return principal.IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch
            {
                return false;
            }
        }

        private static string GetOsArchitecture()
        {
            string arch = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITEW6432");
            if (string.IsNullOrEmpty(arch)) arch = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE");
            if (string.IsNullOrEmpty(arch)) arch = Environment.Is64BitOperatingSystem ? "AMD64" : "x86";

            switch (arch.ToUpperInvariant())
            {
                case "AMD64": return "x64";
                case "ARM64": return "ARM64";
                case "X86": return "x86";
                default: return arch;
            }
        }

        public static Version ParseVersionSafe(string value)
        {
            Version version;
            if (!string.IsNullOrWhiteSpace(value) && Version.TryParse(value, out version)) return version;
            return new Version(0, 0, 0, 0);
        }

        public static int CompareVersions(string left, string right)
        {
            return ParseVersionSafe(left).CompareTo(ParseVersionSafe(right));
        }
    }
}
