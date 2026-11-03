using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Zayu.Core
{
    /// <summary>Sunshine 的发行渠道。</summary>
    public enum SunshineBuildKind
    {
        Unknown = 0,
        /// <summary>LizardByte 上游正式版。</summary>
        Official,
        /// <summary>基地版（杂鱼定制版）。</summary>
        BaseEdition,
        /// <summary>阿西西版（Axixi 二次分发）。</summary>
        Axixi
    }

    /// <summary>版本判断的置信度。</summary>
    public enum SunshineConfidence
    {
        None = 0,
        Low,
        Medium,
        High
    }

    /// <summary>Sunshine 在本机的安装状态。</summary>
    public enum SunshineInstallState
    {
        Unknown = 0,
        /// <summary>服务或安装记录存在，且能找到 Sunshine.exe。</summary>
        Installed,
        /// <summary>已卸载，但安装目录里还留着文件。</summary>
        Leftover,
        /// <summary>安装记录还在，但文件已经没了。</summary>
        Uninstalled,
        /// <summary>没有安装记录（只在指定目录扫描时使用）。</summary>
        DirectoryScan,
        NotInstalled
    }

    /// <summary>从安装目录和注册表提取出的原始特征，用于版本判定。</summary>
    public sealed class SunshineSignature
    {
        public string FileVersion { get; set; }
        public string ProductVersion { get; set; }
        public string CompanyName { get; set; }
        public string ProductName { get; set; }
        public string FileDescription { get; set; }

        /// <summary>Inno Setup / MSI / 未知。</summary>
        public string InstallerKind { get; set; }

        public string UninstallDisplayName { get; set; }
        public string UninstallVersion { get; set; }
        public string UninstallPublisher { get; set; }

        /// <summary>命中的文本标记，例如 axixi、杂鱼。</summary>
        public List<string> MatchedMarkers { get; set; }

        /// <summary>命中的指纹文件（安装目录下的相对路径）。</summary>
        public List<string> MatchedFiles { get; set; }

        public SunshineSignature()
        {
            FileVersion = string.Empty;
            ProductVersion = string.Empty;
            CompanyName = string.Empty;
            ProductName = string.Empty;
            FileDescription = string.Empty;
            InstallerKind = string.Empty;
            UninstallDisplayName = string.Empty;
            UninstallVersion = string.Empty;
            UninstallPublisher = string.Empty;
            MatchedMarkers = new List<string>();
            MatchedFiles = new List<string>();
        }
    }

    /// <summary>版本判定的结论。</summary>
    public sealed class SunshineVerdict
    {
        public SunshineBuildKind Kind { get; set; }
        public SunshineConfidence Confidence { get; set; }

        /// <summary>得出结论的依据，按来源逐条记录。</summary>
        public List<string> Reasons { get; private set; }

        public SunshineVerdict()
        {
            Kind = SunshineBuildKind.Unknown;
            Confidence = SunshineConfidence.None;
            Reasons = new List<string>();
        }
    }

    /// <summary>Sunshine 版本自检结果。</summary>
    public sealed class SunshineDetectionResult
    {
        public SunshineBuildKind Kind { get; set; }
        public SunshineConfidence Confidence { get; set; }
        public SunshineInstallState InstallState { get; set; }

        /// <summary>是否算作“已安装”：有服务或安装记录，且能找到程序文件。</summary>
        public bool IsInstalled
        {
            get { return InstallState == SunshineInstallState.Installed; }
        }

        /// <summary>安装状态的中文描述。</summary>
        public string InstallStateLabel
        {
            get
            {
                switch (InstallState)
                {
                    case SunshineInstallState.Installed: return "已安装";
                    case SunshineInstallState.Leftover: return "未安装（残留文件）";
                    case SunshineInstallState.Uninstalled: return "已卸载（注册表残留）";
                    case SunshineInstallState.DirectoryScan: return "目录扫描";
                    case SunshineInstallState.NotInstalled: return "未安装";
                    default: return "未知";
                }
            }
        }

        public string Version { get; set; }
        public string ProductVersion { get; set; }
        public string CompanyName { get; set; }
        public string ProductName { get; set; }
        public string FileDescription { get; set; }

        public string InstallPath { get; set; }
        public string ExePath { get; set; }

        public string InstallerKind { get; set; }
        public string UninstallDisplayName { get; set; }
        public string UninstallVersion { get; set; }
        public string UninstallPublisher { get; set; }

        public string ServiceName { get; set; }
        public string ServicePath { get; set; }
        public string ServiceStartMode { get; set; }

        /// <summary>服务实际指向的目录是否与安装目录一致。</summary>
        public bool ServicePathMatchesInstall { get; set; }

        public List<string> MatchedFiles { get; set; }
        public List<string> MatchedMarkers { get; set; }

        /// <summary>判定依据，按来源逐条记录。</summary>
        public List<string> Evidence { get; set; }

        public List<string> Warnings { get; set; }

        public DateTime DetectedAt { get; set; }

        /// <summary>未知构建时才计算，便于把新版本登记进白名单。</summary>
        public string ExeSha256 { get; set; }

        /// <summary>未知构建时的安装目录顶层条目，用于人工判断渠道。</summary>
        public List<string> TopLevelEntries { get; set; }

        /// <summary>检测过程中的致命错误，通常为空。</summary>
        public string Error { get; set; }

        public SunshineDetectionResult()
        {
            Kind = SunshineBuildKind.Unknown;
            Confidence = SunshineConfidence.None;
            InstallState = SunshineInstallState.Unknown;
            Version = string.Empty;
            ProductVersion = string.Empty;
            CompanyName = string.Empty;
            ProductName = string.Empty;
            FileDescription = string.Empty;
            InstallPath = string.Empty;
            ExePath = string.Empty;
            InstallerKind = string.Empty;
            UninstallDisplayName = string.Empty;
            UninstallVersion = string.Empty;
            UninstallPublisher = string.Empty;
            ServiceName = string.Empty;
            ServicePath = string.Empty;
            ServiceStartMode = string.Empty;
            MatchedFiles = new List<string>();
            MatchedMarkers = new List<string>();
            Evidence = new List<string>();
            Warnings = new List<string>();
            TopLevelEntries = new List<string>();
            DetectedAt = DateTime.Now;
            ExeSha256 = string.Empty;
            Error = string.Empty;
        }

        /// <summary>渠道名称，用于界面展示。</summary>
        public string KindLabel
        {
            get
            {
                switch (Kind)
                {
                    case SunshineBuildKind.Official: return "官方版";
                    case SunshineBuildKind.BaseEdition: return "基地版";
                    case SunshineBuildKind.Axixi: return "阿西西版";
                    default: return "未知构建";
                }
            }
        }

        /// <summary>
        /// 渠道维护者。基地版由 AlkaidLab 维护，不能直接沿用 exe 里上游的 CompanyName=LizardByte。
        /// </summary>
        public string AuthorName
        {
            get
            {
                switch (Kind)
                {
                    case SunshineBuildKind.BaseEdition: return "AlkaidLab";
                    case SunshineBuildKind.Official: return "LizardByte";
                    case SunshineBuildKind.Axixi: return "Axixi";
                    default:
                        return string.IsNullOrEmpty(CompanyName) ? "未知" : CompanyName;
                }
            }
        }

        /// <summary>是否为第三方二次分发版本。</summary>
        public bool IsUnofficial
        {
            get { return Kind == SunshineBuildKind.Axixi; }
        }

        public string ConfidenceLabel
        {
            get
            {
                switch (Confidence)
                {
                    case SunshineConfidence.High: return "高";
                    case SunshineConfidence.Medium: return "中";
                    case SunshineConfidence.Low: return "低";
                    default: return "无";
                }
            }
        }

        /// <summary>一行结论，例如“基地版 · v2026.1031.121458.杂鱼（高）”。</summary>
        public string Summary
        {
            get
            {
                string version = string.IsNullOrEmpty(Version) ? "版本未知" : Version;
                return KindLabel + " · " + version + "（" + ConfidenceLabel + "置信）";
            }
        }

        public string ToDiagnostics()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("杂鱼串流工具箱 · Sunshine 版本自检");
            sb.AppendLine("检测结果：" + KindLabel + "（" + ConfidenceLabel + "置信）");
            sb.AppendLine("安装状态：" + InstallStateLabel);
            sb.AppendLine("版本号：" + NullSafe(Version));
            if (!string.IsNullOrEmpty(ProductVersion) && !string.Equals(ProductVersion, Version, StringComparison.OrdinalIgnoreCase))
                sb.AppendLine("产品版本：" + ProductVersion);
            sb.AppendLine("作者：" + NullSafe(AuthorName));
            sb.AppendLine("版本资源 CompanyName：" + NullSafe(CompanyName));
            sb.AppendLine("安装来源：" + NullSafe(InstallerKind));
            if (!string.IsNullOrEmpty(UninstallDisplayName))
                sb.AppendLine("卸载项：" + UninstallDisplayName);
            if (!string.IsNullOrEmpty(UninstallPublisher))
                sb.AppendLine("安装包发布者：" + UninstallPublisher);
            sb.AppendLine("安装路径：" + NullSafe(InstallPath));
            sb.AppendLine("可执行文件：" + NullSafe(ExePath));
            if (!string.IsNullOrEmpty(ServiceName))
            {
                sb.AppendLine("服务：" + ServiceName + " · " + NullSafe(ServiceStartMode)
                    + " · " + NullSafe(ServicePath));
                sb.AppendLine("服务与安装目录一致：" + (ServicePathMatchesInstall ? "是" : "否"));
            }
            if (MatchedFiles.Count > 0)
                sb.AppendLine("指纹文件：" + string.Join("、", MatchedFiles.ToArray()));
            if (MatchedMarkers.Count > 0)
                sb.AppendLine("文本标记：" + string.Join("、", MatchedMarkers.ToArray()));
            if (!string.IsNullOrEmpty(ExeSha256))
                sb.AppendLine("EXE SHA256：" + ExeSha256);
            if (TopLevelEntries.Count > 0)
                sb.AppendLine("目录顶层：" + string.Join("、", TopLevelEntries.ToArray()));
            if (Evidence.Count > 0)
            {
                sb.AppendLine("判断依据：");
                for (int i = 0; i < Evidence.Count; i++) sb.AppendLine("  - " + Evidence[i]);
            }
            if (Warnings.Count > 0)
            {
                sb.AppendLine("提示：");
                for (int i = 0; i < Warnings.Count; i++) sb.AppendLine("  - " + Warnings[i]);
            }
            if (!string.IsNullOrEmpty(Error))
                sb.AppendLine("错误：" + Error);
            return sb.ToString();
        }

        private static string NullSafe(string value)
        {
            return string.IsNullOrEmpty(value) ? "—" : value;
        }
    }

    /// <summary>
    /// 只读检测本机 Sunshine 的发行渠道与版本。
    /// 不修改任何文件、注册表或服务，也不需要管理员权限。
    /// </summary>
    public static class SunshineDetector
    {
        public const string DefaultInstallDirectory = @"C:\Program Files\Sunshine";
        public const string ServiceName = "SunshineService";

        /// <summary>基地版各版本共有的组件。目录以反斜杠结尾，按前缀匹配。</summary>
        private static readonly string[] BaseEditionFiles = new string[]
        {
            @"assets\gui\sunshine-gui.exe",
            @"tools\qiin-tabtip.exe",
            @"tools\device-toggler.exe",
            @"tools\ddprobe.exe",
            @"tools\vdd\",
            @"tools\vmouse\",
            @"tools\vdd\ZakoVDD.inf",
            @"tools\vmouse\ZakoVirtualMouse.inf"
        };

        /// <summary>阿西西版各版本共有的组件。目录以反斜杠结尾，按前缀匹配。</summary>
        private static readonly string[] AxixiFiles = new string[]
        {
            @"drivers\sudovda\",
            @"drivers\sudovda\sudovda.inf",
            @"drivers\sudovda\install.bat",
            @"tools\sunshine-hidmaestro-host.exe"
        };

        private static readonly string[] TextMarkers = new string[] { "axixi", "阿西西", "杂鱼" };

        /// <summary>检测本机安装的 Sunshine。</summary>
        public static SunshineDetectionResult Detect()
        {
            return DetectCore(string.Empty, true);
        }

        /// <summary>只分析指定目录（用于便携版、解包目录或测试样本），不读取注册表。</summary>
        public static SunshineDetectionResult Detect(string installRoot)
        {
            return DetectCore(installRoot, false);
        }

        /// <summary>根据提取出的特征判断发行渠道。纯函数，便于测试。</summary>
        public static SunshineVerdict Classify(SunshineSignature signature)
        {
            SunshineVerdict verdict = new SunshineVerdict();
            if (signature == null) return verdict;

            string company = NullToEmpty(signature.CompanyName);
            string version = NullToEmpty(signature.FileVersion);
            if (string.IsNullOrEmpty(version)) version = NullToEmpty(signature.ProductVersion);

            int axixiSignals = 0;
            if (ContainsIgnoreCase(company, "third party publisher") || ContainsIgnoreCase(company, "axixi"))
            {
                axixiSignals++;
                verdict.Reasons.Add("版本资源 CompanyName=" + company);
            }
            if (HasMarker(signature, "axixi") || HasMarker(signature, "阿西西"))
            {
                axixiSignals++;
                verdict.Reasons.Add("安装目录中的文件内容命中 axixi / 阿西西 文案");
            }
            if (HasAnyFile(signature, AxixiFiles))
            {
                axixiSignals++;
                verdict.Reasons.Add("安装目录中存在阿西西专属组件（SudoVDA / HIDMaestro）");
            }
            if (ContainsIgnoreCase(NullToEmpty(signature.UninstallDisplayName), "axixi")
                || ContainsIgnoreCase(NullToEmpty(signature.UninstallDisplayName), "阿西西"))
            {
                axixiSignals++;
                verdict.Reasons.Add("卸载项名称=" + signature.UninstallDisplayName);
            }
            if (axixiSignals > 0)
            {
                verdict.Kind = SunshineBuildKind.Axixi;
                verdict.Confidence = axixiSignals >= 2 ? SunshineConfidence.High : SunshineConfidence.Medium;
                return verdict;
            }

            int baseSignals = 0;
            if (HasMarker(signature, "杂鱼"))
            {
                baseSignals++;
                verdict.Reasons.Add("安装目录中的 Web 资源命中“杂鱼”文案");
            }
            if (ContainsIgnoreCase(version, "杂鱼") || ContainsIgnoreCase(version, "基地"))
            {
                baseSignals++;
                verdict.Reasons.Add("版本号带中文渠道后缀：" + version);
            }
            if (HasAnyFile(signature, BaseEditionFiles))
            {
                baseSignals++;
                verdict.Reasons.Add("安装目录中存在基地版专属组件（Zako 虚拟显示器 / sunshine-gui / 基地工具）");
            }
            if (ContainsIgnoreCase(NullToEmpty(signature.UninstallPublisher), "qiin"))
            {
                baseSignals++;
                verdict.Reasons.Add("安装包发布者=" + signature.UninstallPublisher);
            }
            if (ContainsIgnoreCase(NullToEmpty(signature.UninstallDisplayName), "杂鱼")
                || ContainsIgnoreCase(NullToEmpty(signature.UninstallDisplayName), "基地"))
            {
                baseSignals++;
                verdict.Reasons.Add("卸载项名称=" + signature.UninstallDisplayName);
            }
            if (baseSignals > 0)
            {
                verdict.Kind = SunshineBuildKind.BaseEdition;
                verdict.Confidence = baseSignals >= 2 ? SunshineConfidence.High : SunshineConfidence.Medium;
                return verdict;
            }

            bool officialCompany = ContainsIgnoreCase(company, "lizardbyte");
            bool semanticVersion = Regex.IsMatch(version, @"^v?\d+\.\d+\.\d+$");
            if (officialCompany && semanticVersion)
            {
                verdict.Kind = SunshineBuildKind.Official;
                verdict.Reasons.Add("版本资源 CompanyName=LizardByte，版本号 " + version);
                bool signedSource = ContainsIgnoreCase(NullToEmpty(signature.InstallerKind), "msi");
                verdict.Confidence = signedSource ? SunshineConfidence.High : SunshineConfidence.Medium;
                return verdict;
            }

            verdict.Kind = SunshineBuildKind.Unknown;
            verdict.Confidence = SunshineConfidence.Low;
            if (string.IsNullOrEmpty(version))
                verdict.Reasons.Add("未读取到版本信息");
            else
                verdict.Reasons.Add("版本号 " + version + " 不属于已知发行渠道");
            return verdict;
        }

        /// <summary>根据服务、安装记录和程序文件判断安装状态。纯函数，便于测试。</summary>
        public static SunshineInstallState DetermineInstallState(bool hasService, bool hasUninstallEntry, bool hasExecutable)
        {
            if (hasExecutable && (hasService || hasUninstallEntry)) return SunshineInstallState.Installed;
            if (hasService || hasUninstallEntry) return SunshineInstallState.Uninstalled;
            if (hasExecutable) return SunshineInstallState.Leftover;
            return SunshineInstallState.NotInstalled;
        }

        private static SunshineDetectionResult DetectCore(string installRoot, bool useRegistry)
        {
            SunshineDetectionResult result = new SunshineDetectionResult();
            UninstallEntry entry = null;
            ServiceInfo service = null;

            if (useRegistry)
            {
                try
                {
                    entry = PickUninstallEntry(ReadUninstallEntries(result.Warnings));
                }
                catch (Exception ex)
                {
                    result.Warnings.Add("读取卸载注册表失败：" + ex.Message);
                }

                try
                {
                    service = ReadServiceInfo();
                }
                catch (Exception ex)
                {
                    result.Warnings.Add("读取 Sunshine 服务失败：" + ex.Message);
                }
            }

            string processDirectory = useRegistry ? TryGetRunningDirectory() : string.Empty;
            string serviceDirectory = service == null ? string.Empty : GetDirectorySafe(service.ExecutablePath);
            string installLocation = entry == null ? string.Empty : entry.InstallLocation;
            string iconDirectory = entry == null ? string.Empty : GetDirectorySafe(entry.DisplayIcon);

            string root = ResolveInstallDirectory(new string[]
            {
                installRoot,
                installLocation,
                iconDirectory,
                serviceDirectory,
                processDirectory,
                DefaultInstallDirectory
            });

            result.InstallPath = root;

            if (!string.IsNullOrEmpty(root) && !Directory.Exists(root))
                result.Warnings.Add("安装目录不存在：" + root);

            string exePath = FindSunshineExe(root);
            result.ExePath = exePath;

            result.InstallState = useRegistry
                ? DetermineInstallState(service != null, entry != null, !string.IsNullOrEmpty(exePath))
                : SunshineInstallState.DirectoryScan;

            if (result.InstallState == SunshineInstallState.Leftover)
            {
                result.Warnings.Add("没有检测到 Sunshine 服务和安装记录，只发现残留文件：" + exePath
                    + "。如果确认已经卸载，可以删除该目录，避免影响下一次安装。");
            }
            else if (result.InstallState == SunshineInstallState.Uninstalled)
            {
                result.Warnings.Add("安装记录仍在，但安装目录里找不到 Sunshine.exe，可能已被手动删除。");
            }

            SunshineSignature signature = new SunshineSignature();
            signature.InstallerKind = DetectInstallerKind(root, entry, useRegistry);

            if (!string.IsNullOrEmpty(exePath))
            {
                ReadVersionInfo(exePath, result, signature, result.Warnings);
            }
            else if (entry != null)
            {
                result.Warnings.Add("未找到 Sunshine.exe，只能根据安装记录判断。");
            }

            if (entry != null)
            {
                result.UninstallDisplayName = entry.DisplayName;
                result.UninstallVersion = entry.DisplayVersion;
                result.UninstallPublisher = entry.Publisher;
                signature.UninstallDisplayName = entry.DisplayName;
                signature.UninstallVersion = entry.DisplayVersion;
                signature.UninstallPublisher = entry.Publisher;
            }

            result.InstallerKind = signature.InstallerKind;

            try
            {
                ScanFingerprints(root, signature, result.Evidence, result.Warnings);
            }
            catch (Exception ex)
            {
                result.Warnings.Add("扫描安装目录失败：" + ex.Message);
            }

            result.MatchedFiles = signature.MatchedFiles;
            result.MatchedMarkers = signature.MatchedMarkers;

            SunshineVerdict verdict = Classify(signature);
            result.Kind = verdict.Kind;
            result.Confidence = verdict.Confidence;
            for (int i = 0; i < verdict.Reasons.Count; i++) result.Evidence.Insert(0, verdict.Reasons[verdict.Reasons.Count - 1 - i]);

            if (result.Kind == SunshineBuildKind.Unknown && !string.IsNullOrEmpty(exePath))
            {
                result.ExeSha256 = ComputeSha256(exePath);
                result.TopLevelEntries = ListTopLevelEntries(root);
            }

            if (service != null)
            {
                result.ServiceName = service.Name;
                result.ServicePath = service.ExecutablePath;
                result.ServiceStartMode = service.StartMode;
                result.ServicePathMatchesInstall = IsUnderDirectory(service.ExecutablePath, root);
                if (!result.ServicePathMatchesInstall)
                {
                    result.Warnings.Add("服务实际指向 " + GetDirectorySafe(service.ExecutablePath)
                        + "，不在安装目录 " + root + " 下。");
                }
            }

            if (IsMeaningfulVersion(result.UninstallVersion) && !string.IsNullOrEmpty(result.Version))
            {
                string registryVersion = result.UninstallVersion.TrimStart('v', 'V');
                string fileVersion = result.Version.TrimStart('v', 'V');
                if (!string.Equals(registryVersion, fileVersion, StringComparison.OrdinalIgnoreCase))
                {
                    result.Warnings.Add("安装记录版本 " + result.UninstallVersion
                        + " 与磁盘上的 Sunshine.exe 版本 " + result.Version + " 不一致，可能被覆盖安装或替换过文件。");
                }
            }

            return result;
        }

        private static void ReadVersionInfo(string exePath, SunshineDetectionResult result, SunshineSignature signature, List<string> warnings)
        {
            try
            {
                FileVersionInfo info = FileVersionInfo.GetVersionInfo(exePath);
                result.Version = NormalizeText(NullToEmpty(info.FileVersion)).Trim();
                result.ProductVersion = NormalizeText(NullToEmpty(info.ProductVersion)).Trim();
                result.CompanyName = NormalizeText(NullToEmpty(info.CompanyName)).Trim();
                result.ProductName = NormalizeText(NullToEmpty(info.ProductName)).Trim();
                result.FileDescription = NormalizeText(NullToEmpty(info.FileDescription)).Trim();

                signature.FileVersion = result.Version;
                signature.ProductVersion = result.ProductVersion;
                signature.CompanyName = result.CompanyName;
                signature.ProductName = result.ProductName;
                signature.FileDescription = result.FileDescription;
            }
            catch (Exception ex)
            {
                warnings.Add("读取 Sunshine.exe 版本资源失败：" + ex.Message);
            }
        }

        private static string DetectInstallerKind(string root, UninstallEntry entry, bool useRegistry)
        {
            if (entry != null && !string.IsNullOrEmpty(entry.InstallerKind) && !string.Equals(entry.InstallerKind, "未知", StringComparison.Ordinal))
                return entry.InstallerKind;

            if (!string.IsNullOrEmpty(root))
            {
                if (File.Exists(Path.Combine(root, "unins000.exe"))) return "Inno Setup";
                if (File.Exists(Path.Combine(root, "Uninstall.exe"))) return "Uninstall.exe";
            }

            return useRegistry ? "未登记" : "未检测";
        }

        private static void ScanFingerprints(string root, SunshineSignature signature, List<string> evidence, List<string> warnings)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;

            foreach (string relative in BaseEditionFiles) AddIfExists(root, relative, signature);
            foreach (string relative in AxixiFiles) AddIfExists(root, relative, signature);

            string webDirectory = Path.Combine(root, @"assets\web");
            if (!Directory.Exists(webDirectory)) return;

            string[] preferred = new string[]
            {
                "index.html",
                @"featured.html",
                @"assets\locale\zh.json"
            };

            List<string> scanned = new List<string>();
            foreach (string relative in preferred)
            {
                string path = Path.Combine(webDirectory, relative);
                if (File.Exists(path))
                {
                    ScanFileForMarkers(path, webDirectory, signature, evidence, warnings);
                    scanned.Add(path);
                }
            }

            int scannedCount = scanned.Count;
            foreach (string path in SafeEnumerateFiles(webDirectory))
            {
                if (scannedCount >= 80) break;
                if (scanned.Contains(path)) continue;
                ScanFileForMarkers(path, webDirectory, signature, evidence, warnings);
                scanned.Add(path);
                scannedCount++;
            }
        }

        private static void AddIfExists(string root, string relative, SunshineSignature signature)
        {
            try
            {
                bool isDirectory = relative.EndsWith("\\", StringComparison.Ordinal);
                string path = Path.Combine(root, relative.TrimEnd('\\'));
                bool exists = isDirectory ? Directory.Exists(path) : File.Exists(path);
                if (exists && !signature.MatchedFiles.Contains(relative)) signature.MatchedFiles.Add(relative);
            }
            catch
            {
            }
        }

        private static void ScanFileForMarkers(string path, string baseDirectory, SunshineSignature signature, List<string> evidence, List<string> warnings)
        {
            try
            {
                FileInfo info = new FileInfo(path);
                if (info.Length <= 0 || info.Length > 4 * 1024 * 1024) return;

                byte[] data = ReadPrefix(path, 512 * 1024);
                if (data == null || data.Length == 0) return;

                for (int i = 0; i < TextMarkers.Length; i++)
                {
                    string marker = TextMarkers[i];
                    if (!ContainsToken(data, marker)) continue;
                    if (!signature.MatchedMarkers.Contains(marker)) signature.MatchedMarkers.Add(marker);

                    string relative;
                    try
                    {
                        relative = path.Substring(baseDirectory.Length).TrimStart('\\');
                    }
                    catch
                    {
                        relative = path;
                    }
                    evidence.Add("文本标记“" + marker + "”出现在 assets\\web\\" + relative);
                }
            }
            catch
            {
            }
        }

        private static byte[] ReadPrefix(string path, int maxBytes)
        {
            try
            {
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    int length = (int)Math.Min(stream.Length, maxBytes);
                    byte[] buffer = new byte[length];
                    int read = 0;
                    while (read < length)
                    {
                        int chunk = stream.Read(buffer, read, length - read);
                        if (chunk <= 0) break;
                        read += chunk;
                    }
                    if (read == length) return buffer;
                    byte[] trimmed = new byte[read];
                    Array.Copy(buffer, trimmed, read);
                    return trimmed;
                }
            }
            catch
            {
                return null;
            }
        }

        private static bool ContainsToken(byte[] data, string token)
        {
            if (data == null || data.Length == 0 || string.IsNullOrEmpty(token)) return false;

            byte[] utf8 = Encoding.UTF8.GetBytes(token);
            if (IsAscii(token))
            {
                if (IndexOfAsciiIgnoreCase(data, utf8) >= 0) return true;
            }
            else
            {
                if (IndexOf(data, utf8) >= 0) return true;
                try
                {
                    if (IndexOf(data, Encoding.GetEncoding(936).GetBytes(token)) >= 0) return true;
                }
                catch
                {
                }
            }
            return false;
        }

        private static bool IsAscii(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] > 127) return false;
            }
            return true;
        }

        private static bool ContainsCjk(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            for (int i = 0; i < text.Length; i++)
            {
                char value = text[i];
                if (value >= 0x4E00 && value <= 0x9FFF) return true;
            }
            return false;
        }

        /// <summary>
        /// 版本资源里的中文会根据系统码页被解码成乱码：在 936 下“杂鱼”会变成“鏉傞奔”，
        /// 在 1252 下会变成 æ\u009d\u201aé±¼。这里按候选码页还原成字节再按 UTF-8 解码，
        /// 只有确实还原出中文时才采用，避免破坏正常字符串。
        /// </summary>
        public static string NormalizeText(string value)
        {
            if (string.IsNullOrEmpty(value) || IsAscii(value)) return value;

            int[] codePages = new int[] { 936, 1252, 28591 };
            for (int i = 0; i < codePages.Length; i++)
            {
                try
                {
                    Encoding encoder = Encoding.GetEncoding(codePages[i], EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
                    byte[] bytes = encoder.GetBytes(value);
                    string decoded = new UTF8Encoding(false, true).GetString(bytes);
                    if (!string.IsNullOrEmpty(decoded)
                        && !string.Equals(decoded, value, StringComparison.Ordinal)
                        && ContainsCjk(decoded))
                    {
                        return decoded;
                    }
                }
                catch
                {
                }
            }
            return value;
        }

        private static bool IsUnderDirectory(string filePath, string directory)
        {
            if (string.IsNullOrEmpty(filePath) || string.IsNullOrEmpty(directory)) return false;

            string normalizedFile = NormalizeDirectory(filePath);
            string normalizedDirectory = NormalizeDirectory(directory);
            if (string.IsNullOrEmpty(normalizedFile) || string.IsNullOrEmpty(normalizedDirectory)) return false;

            return normalizedFile.StartsWith(normalizedDirectory + "\\", StringComparison.OrdinalIgnoreCase);
        }

        private static int IndexOfAsciiIgnoreCase(byte[] haystack, byte[] needle)
        {
            if (needle.Length == 0 || haystack.Length < needle.Length) return -1;
            int limit = haystack.Length - needle.Length;
            for (int i = 0; i <= limit; i++)
            {
                int j = 0;
                while (j < needle.Length)
                {
                    int a = haystack[i + j];
                    int b = needle[j];
                    if (a >= 'A' && a <= 'Z') a += 32;
                    if (b >= 'A' && b <= 'Z') b += 32;
                    if (a != b) break;
                    j++;
                }
                if (j == needle.Length) return i;
            }
            return -1;
        }

        private static int IndexOf(byte[] haystack, byte[] needle)
        {
            if (needle.Length == 0 || haystack.Length < needle.Length) return -1;
            int limit = haystack.Length - needle.Length;
            for (int i = 0; i <= limit; i++)
            {
                int j = 0;
                while (j < needle.Length && haystack[i + j] == needle[j]) j++;
                if (j == needle.Length) return i;
            }
            return -1;
        }

        private static string FindSunshineExe(string root)
        {
            if (string.IsNullOrEmpty(root)) return string.Empty;

            string direct = Path.Combine(root, "Sunshine.exe");
            if (File.Exists(direct)) return direct;

            try
            {
                foreach (string path in Directory.GetFiles(root, "*.exe"))
                {
                    if (Path.GetFileName(path).IndexOf("sunshine", StringComparison.OrdinalIgnoreCase) >= 0
                        && Path.GetFileName(path).IndexOf("svc", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        return path;
                    }
                }
            }
            catch
            {
            }
            return string.Empty;
        }

        private static string ComputeSha256(string path)
        {
            try
            {
                using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    byte[] hash = sha.ComputeHash(stream);
                    StringBuilder sb = new StringBuilder(hash.Length * 2);
                    for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("X2"));
                    return sb.ToString();
                }
            }
            catch
            {
                return string.Empty;
            }
        }

        private static List<string> ListTopLevelEntries(string root)
        {
            List<string> entries = new List<string>();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return entries;

            try
            {
                foreach (string directory in Directory.GetDirectories(root))
                {
                    if (entries.Count >= 24) break;
                    entries.Add(Path.GetFileName(directory) + @"\");
                }
                foreach (string file in Directory.GetFiles(root))
                {
                    if (entries.Count >= 24) break;
                    entries.Add(Path.GetFileName(file));
                }
            }
            catch
            {
            }
            return entries;
        }

        /// <summary>
        /// 找一个确实包含 Sunshine.exe 的目录。旧版安装器会把服务放在 tools 子目录、
        /// 卸载项又不写 InstallLocation，所以候选目录找不到 exe 时逐级向上找。
        /// </summary>
        private static string ResolveInstallDirectory(string[] candidates)
        {
            string fallback = string.Empty;

            for (int i = 0; i < candidates.Length; i++)
            {
                string candidate = TrimDirectory(candidates[i]);
                if (string.IsNullOrEmpty(candidate)) continue;
                if (string.IsNullOrEmpty(fallback) && Directory.Exists(candidate)) fallback = candidate;

                string current = candidate;
                for (int level = 0; level < 3 && !string.IsNullOrEmpty(current); level++)
                {
                    if (!string.IsNullOrEmpty(FindSunshineExe(current))) return current;
                    current = GetDirectorySafe(current);
                }
            }

            return fallback;
        }

        private static string TrimDirectory(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            return path.Trim().Trim('"');
        }

        /// <summary>0.0.0 之类的占位版本号视为不可用。</summary>
        private static bool IsMeaningfulVersion(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            string trimmed = value.Trim().TrimStart('v', 'V');
            for (int i = 0; i < trimmed.Length; i++)
            {
                char c = trimmed[i];
                if (c != '0' && c != '.') return true;
            }
            return false;
        }

        private static string NormalizeDirectory(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            try
            {
                return Path.GetFullPath(path).TrimEnd('\\', '/');
            }
            catch
            {
                return path.TrimEnd('\\', '/');
            }
        }

        private static string GetDirectorySafe(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            try
            {
                return Path.GetDirectoryName(path) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string TryGetRunningDirectory()
        {
            try
            {
                Process[] processes = Process.GetProcessesByName("Sunshine");
                for (int i = 0; i < processes.Length; i++)
                {
                    try
                    {
                        string path = processes[i].MainModule == null ? string.Empty : processes[i].MainModule.FileName;
                        if (!string.IsNullOrEmpty(path) && File.Exists(path))
                        {
                            return Path.GetDirectoryName(path) ?? string.Empty;
                        }
                    }
                    catch
                    {
                    }
                    finally
                    {
                        processes[i].Dispose();
                    }
                }
            }
            catch
            {
            }
            return string.Empty;
        }

        private static IEnumerable<string> SafeEnumerateFiles(string directory)
        {
            List<string> files = new List<string>();
            try
            {
                files.AddRange(Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories));
            }
            catch
            {
            }
            return files;
        }

        private static ServiceInfo ReadServiceInfo()
        {
            ServiceInfo info = ReadServiceByKey(ServiceName);
            if (info != null) return info;

            try
            {
                using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (RegistryKey services = baseKey.OpenSubKey(@"SYSTEM\CurrentControlSet\Services"))
                {
                    if (services == null) return null;
                    foreach (string name in services.GetSubKeyNames())
                    {
                        ServiceInfo candidate = ReadServiceByKey(name);
                        if (candidate == null) continue;
                        if (candidate.ExecutablePath.IndexOf("sunshine", StringComparison.OrdinalIgnoreCase) >= 0)
                            return candidate;
                    }
                }
            }
            catch
            {
            }
            return null;
        }

        private static ServiceInfo ReadServiceByKey(string name)
        {
            try
            {
                using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (RegistryKey key = baseKey.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name))
                {
                    if (key == null) return null;

                    string imagePath = key.GetValue("ImagePath") as string;
                    if (string.IsNullOrEmpty(imagePath)) return null;
                    if (name.IndexOf("sunshine", StringComparison.OrdinalIgnoreCase) < 0
                        && imagePath.IndexOf("sunshine", StringComparison.OrdinalIgnoreCase) < 0)
                        return null;

                    ServiceInfo info = new ServiceInfo();
                    info.Name = name;
                    info.DisplayName = (key.GetValue("DisplayName") as string) ?? name;
                    info.ExecutablePath = ExtractExecutablePath(imagePath);

                    object start = key.GetValue("Start");
                    info.StartMode = start is int ? DescribeStart((int)start) : "未知";
                    return info;
                }
            }
            catch
            {
                return null;
            }
        }

        private static string DescribeStart(int start)
        {
            switch (start)
            {
                case 0:
                case 1:
                    return "系统启动";
                case 2:
                    return "自动";
                case 3:
                    return "手动";
                case 4:
                    return "已禁用";
                default:
                    return "未知";
            }
        }

        private static string ExtractExecutablePath(string imagePath)
        {
            if (string.IsNullOrEmpty(imagePath)) return string.Empty;
            string trimmed = imagePath.Trim();

            if (trimmed.StartsWith("\"", StringComparison.Ordinal))
            {
                int end = trimmed.IndexOf('"', 1);
                if (end > 1) return trimmed.Substring(1, end - 1);
                return trimmed.Trim('"');
            }

            int exeIndex = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exeIndex >= 0) return trimmed.Substring(0, exeIndex + 4);
            return trimmed;
        }

        private static List<UninstallEntry> ReadUninstallEntries(List<string> warnings)
        {
            List<UninstallEntry> entries = new List<UninstallEntry>();
            const string uninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

            RegistryHive[] hives = new RegistryHive[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser };
            RegistryView[] views = new RegistryView[] { RegistryView.Registry64, RegistryView.Registry32 };

            foreach (RegistryHive hive in hives)
            {
                foreach (RegistryView view in views)
                {
                    if (hive == RegistryHive.CurrentUser && view == RegistryView.Registry32) continue;

                    try
                    {
                        using (RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view))
                        using (RegistryKey uninstall = baseKey.OpenSubKey(uninstallPath))
                        {
                            if (uninstall == null) continue;

                            foreach (string name in uninstall.GetSubKeyNames())
                            {
                                try
                                {
                                    using (RegistryKey key = uninstall.OpenSubKey(name))
                                    {
                                        if (key == null) continue;

                                        string displayName = key.GetValue("DisplayName") as string;
                                        string installLocation = key.GetValue("InstallLocation") as string;
                                        string displayIcon = key.GetValue("DisplayIcon") as string;

                                        if (!LooksLikeSunshine(displayName) && !LooksLikeSunshine(installLocation) && !LooksLikeSunshine(displayIcon))
                                            continue;

                                        UninstallEntry entry = new UninstallEntry();
                                        entry.DisplayName = NullToEmpty(displayName);
                                        entry.DisplayVersion = NullToEmpty(key.GetValue("DisplayVersion") as string);
                                        entry.Publisher = NullToEmpty(key.GetValue("Publisher") as string);
                                        entry.InstallLocation = NullToEmpty(installLocation);
                                        entry.DisplayIcon = NullToEmpty(displayIcon);
                                        entry.InstallerKind = DetectInstallerKindFromKey(name, key);
                                        entry.RegistryPath = hive + "\\" + view + "\\" + uninstallPath + "\\" + name;
                                        entries.Add(entry);
                                    }
                                }
                                catch
                                {
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        warnings.Add("读取 " + hive + " 卸载项失败：" + ex.Message);
                    }
                }
            }

            return entries;
        }

        private static UninstallEntry PickUninstallEntry(List<UninstallEntry> entries)
        {
            if (entries == null || entries.Count == 0) return null;

            for (int i = 0; i < entries.Count; i++)
            {
                string location = entries[i].InstallLocation.Trim().Trim('"');
                if (!string.IsNullOrEmpty(location) && Directory.Exists(location)) return entries[i];
            }

            for (int i = 0; i < entries.Count; i++)
            {
                if (LooksLikeSunshine(entries[i].DisplayName)) return entries[i];
            }

            return entries[0];
        }

        private static string DetectInstallerKindFromKey(string name, RegistryKey key)
        {
            if (!string.IsNullOrEmpty(name) && name.EndsWith("_is1", StringComparison.OrdinalIgnoreCase))
                return "Inno Setup";

            object windowsInstaller = key.GetValue("WindowsInstaller");
            if (windowsInstaller is int && (int)windowsInstaller == 1) return "MSI";

            string uninstallString = key.GetValue("UninstallString") as string;
            if (!string.IsNullOrEmpty(uninstallString) && uninstallString.IndexOf("unins000", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Inno Setup";

            return "未知";
        }

        private static bool LooksLikeSunshine(string value)
        {
            return !string.IsNullOrEmpty(value)
                && value.IndexOf("sunshine", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool HasMarker(SunshineSignature signature, string marker)
        {
            if (signature == null || signature.MatchedMarkers == null) return false;
            for (int i = 0; i < signature.MatchedMarkers.Count; i++)
            {
                if (string.Equals(signature.MatchedMarkers[i], marker, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static bool HasAnyFile(SunshineSignature signature, string[] candidates)
        {
            if (signature == null || signature.MatchedFiles == null) return false;
            for (int i = 0; i < candidates.Length; i++)
            {
                for (int j = 0; j < signature.MatchedFiles.Count; j++)
                {
                    if (string.Equals(signature.MatchedFiles[j], candidates[i], StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            return false;
        }

        private static bool ContainsIgnoreCase(string value, string token)
        {
            return !string.IsNullOrEmpty(value)
                && value.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string NullToEmpty(string value)
        {
            return value ?? string.Empty;
        }

        private sealed class UninstallEntry
        {
            public string DisplayName;
            public string DisplayVersion;
            public string Publisher;
            public string InstallLocation;
            public string DisplayIcon;
            public string InstallerKind;
            public string RegistryPath;
        }

        private sealed class ServiceInfo
        {
            public string Name;
            public string DisplayName;
            public string ExecutablePath;
            public string StartMode;
        }
    }
}
