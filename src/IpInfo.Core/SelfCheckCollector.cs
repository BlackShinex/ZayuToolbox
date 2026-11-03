using System;
using System.Collections.Generic;
using System.Globalization;
using System.Management;
using System.Text;
using System.Threading;

namespace IpInfo.Core
{
    /// <summary>
    /// 一键自检：收集系统、Sunshine、网络、连通性和组件信息，
    /// 只保留排查串流问题最常用的字段，输出为可直接展示 / 截图的表格数据。
    /// </summary>
    public static class SelfCheckCollector
    {
        public static SelfCheckReport Collect(NetworkReport networkReport, ConnectivityTestResult connectivity,
            Action<string> progress, CancellationToken cancellationToken)
        {
            SelfCheckReport report = new SelfCheckReport();
            try
            {
                Report(progress, "正在收集系统信息…");
                AddSection(report, "系统信息");
                CollectSystem(report);
                cancellationToken.ThrowIfCancellationRequested();

                Report(progress, "正在检查 Sunshine 环境…");
                AddSection(report, "Sunshine");
                CollectSunshine(report);
                cancellationToken.ThrowIfCancellationRequested();

                Report(progress, "正在整理网络信息…");
                AddSection(report, "网络");
                if (networkReport == null)
                {
                    try
                    {
                        networkReport = NetworkProbe.Collect(progress, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        AddItem(report, "网络检测", "失败：" + ex.Message);
                    }
                }

                report.NetworkReport = networkReport;
                if (networkReport != null)
                {
                    report.NetworkIncluded = true;
                    CollectNetwork(report, networkReport);
                }
                else if (!HasItem(report, "网络检测"))
                {
                    AddItem(report, "网络检测", "未获取到网络信息");
                }

                cancellationToken.ThrowIfCancellationRequested();
                Report(progress, "正在整理连通性测试结果…");
                AddSection(report, "网络连通性");
                if (connectivity == null)
                {
                    AddItem(report, "状态", "未做网络连通性测试（已跳过）");
                }
                else
                {
                    report.ConnectivityIncluded = true;
                    report.ConnectivityComplete = connectivity.Complete;
                    CollectConnectivity(report, connectivity);
                }

                cancellationToken.ThrowIfCancellationRequested();
                Report(progress, "正在检查组件…");
                AddSection(report, "组件");
                CollectWebView2(report);

                report.Timestamp = DateTime.Now;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                report.Error = "自检失败：" + ex.GetType().Name + " " + ex.Message;
            }
            return report;
        }

        private static void CollectSystem(SelfCheckReport report)
        {
            try
            {
                AddItem(report, "操作系统", GetOperatingSystem());
            }
            catch (Exception ex)
            {
                AddItem(report, "操作系统", "读取失败：" + ex.Message);
            }

            try
            {
                AddItem(report, "CPU", GetCpu());
            }
            catch (Exception ex)
            {
                AddItem(report, "CPU", "读取失败：" + ex.Message);
            }

            try
            {
                AddItem(report, "内存", GetMemory());
            }
            catch (Exception ex)
            {
                AddItem(report, "内存", "读取失败：" + ex.Message);
            }

            try
            {
                AddItem(report, "显卡", GetGpu());
            }
            catch (Exception ex)
            {
                AddItem(report, "显卡", "读取失败：" + ex.Message);
            }
        }

        private static void CollectSunshine(SelfCheckReport report)
        {
            try
            {
                SunshineDetectionResult sunshine = SunshineDetector.Detect();
                if (sunshine == null)
                {
                    AddItem(report, "版本", "未检测到 Sunshine");
                    AddItem(report, "安装状态", "未知");
                }
                else
                {
                    string version = string.IsNullOrEmpty(sunshine.Version)
                        ? sunshine.InstallStateLabel
                        : sunshine.KindLabel + " · " + sunshine.Version;
                    if (!string.IsNullOrEmpty(sunshine.AuthorName)
                        && version.IndexOf(sunshine.AuthorName, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        version += " · " + sunshine.AuthorName;
                    }
                    AddItem(report, "版本", version);
                    AddItem(report, "安装状态", sunshine.InstallStateLabel);
                }
            }
            catch (Exception ex)
            {
                AddItem(report, "版本", "检测失败：" + ex.Message);
            }

            try
            {
                SunshineServiceStatus service = SunshineServiceManager.Detect();
                if (service == null || !service.IsFound)
                {
                    AddItem(report, "服务", "未安装");
                }
                else
                {
                    AddItem(report, "服务", service.StateLabel + " · " + service.StartModeLabel);
                    if (!string.IsNullOrEmpty(service.ServiceName))
                    {
                        AddItem(report, "服务名", service.ServiceName);
                    }
                }
            }
            catch (Exception ex)
            {
                AddItem(report, "服务", "检测失败：" + ex.Message);
            }

            try
            {
                ZakoDisplayStatus zako = ZakoDisplayDetector.Detect();
                if (zako == null || zako.State == ZakoDisplayState.NotInstalled)
                {
                    AddItem(report, "虚拟显示器", "未安装");
                    return;
                }

                string display = zako.StateLabel;
                if (zako.State == ZakoDisplayState.MultipleAdapters)
                {
                    display += " · " + zako.Devices.Count + " 个适配器";
                }
                AddItem(report, "虚拟显示器", display);

                string driverVersion = zako.Adapter == null ? string.Empty : zako.Adapter.DriverVersion;
                if (string.IsNullOrEmpty(driverVersion))
                {
                    driverVersion = zako.BundledDriverVersion;
                }
                AddItem(report, "虚拟显示器驱动", string.IsNullOrEmpty(driverVersion) ? "未读取到" : driverVersion);
            }
            catch (Exception ex)
            {
                AddItem(report, "虚拟显示器", "检测失败：" + ex.Message);
            }
        }

        private static void CollectNetwork(SelfCheckReport report, NetworkReport network)
        {
            IpAddressEntry ipv4 = network.PrimaryIpv4;
            IpAddressEntry ipv6 = network.PrimaryIpv6;
            AddItem(report, "本机 IPv4", ipv4 == null ? "未检测到" : ipv4.Address + " · " + ipv4.ScopeLabel, true);
            AddItem(report, "本机 IPv6", ipv6 == null ? "未检测到" : ipv6.Address + " · " + ipv6.ScopeLabel, true);

            PublicIpInfo pub = network.PublicIp;
            if (pub == null)
            {
                AddItem(report, "公网出口 IPv4", "未获取", true);
                AddItem(report, "公网出口 IPv6", "未获取", true);
                AddItem(report, "IPv4 网络位置", "未知");
                AddItem(report, "IPv6 状态", "未知");
            }
            else
            {
                AddItem(report, "公网出口 IPv4",
                    string.IsNullOrEmpty(pub.ExternalIpv4) ? "未获取" : pub.ExternalIpv4 + " · " + PublicStatusText(pub.Ipv4Status),
                    true);
                AddItem(report, "公网出口 IPv6",
                    string.IsNullOrEmpty(pub.ExternalIpv6) ? "无" : pub.ExternalIpv6,
                    true);
                AddItem(report, "IPv4 网络位置", PublicStatusText(pub.Ipv4Status));
                AddItem(report, "IPv6 状态", pub.HasPublicIpv6 ? "公网直连" : "无公网 IPv6");
            }

            NatResult nat = network.Nat;
            if (nat == null)
            {
                AddItem(report, "NAT 类型", "未知");
            }
            else
            {
                AddItem(report, "NAT 类型", string.IsNullOrEmpty(nat.TypeLabel) ? "未知" : nat.TypeLabel);
                if (!string.IsNullOrEmpty(nat.Error))
                {
                    AddItem(report, "NAT 检测", nat.Error);
                }
            }

            if (!string.IsNullOrEmpty(network.Error))
            {
                AddItem(report, "网络检测", network.Error);
            }
        }

        private static void CollectConnectivity(SelfCheckReport report, ConnectivityTestResult connectivity)
        {
            AddItem(report, "内网 IPv4", FormatCheck(connectivity.Find("lan4")), true);
            AddItem(report, "内网 IPv6", FormatCheck(connectivity.Find("lan6")), true);
            AddItem(report, "外网 IPv4", FormatCheck(connectivity.Find("wan4")), true);
            AddItem(report, "外网 IPv6", FormatCheck(connectivity.Find("wan6")), true);
            AddItem(report, "P2P 结论",
                string.IsNullOrEmpty(connectivity.VerdictDetail)
                    ? connectivity.Verdict
                    : connectivity.Verdict + " · " + connectivity.VerdictDetail);
            if (!connectivity.Complete)
            {
                AddItem(report, "测试状态", "上次测试未完成");
            }
        }

        private static void CollectWebView2(SelfCheckReport report)
        {
            try
            {
                WebView2DetectionResult webview = WebView2Detector.Detect();
                if (webview == null)
                {
                    AddItem(report, "WebView2", "未知");
                    return;
                }

                string text = webview.StatusText;
                if (webview.Primary != null && !string.IsNullOrEmpty(webview.Primary.Version))
                {
                    text += " · " + webview.Primary.Version;
                }
                AddItem(report, "WebView2", text);
                if (!string.IsNullOrEmpty(webview.EdgeVersion))
                {
                    AddItem(report, "Edge", webview.EdgeVersion);
                }
            }
            catch (Exception ex)
            {
                AddItem(report, "WebView2", "检测失败：" + ex.Message);
            }
        }

        private static string FormatCheck(ConnectivityCheckResult check)
        {
            if (check == null) return "未测试";
            string state = CheckStateText(check.State);
            return string.IsNullOrEmpty(check.Detail) ? state : state + " · " + check.Detail;
        }

        private static string CheckStateText(ConnectivityCheckState state)
        {
            switch (state)
            {
                case ConnectivityCheckState.Pass: return "连通";
                case ConnectivityCheckState.Warn: return "可用 · 注意";
                case ConnectivityCheckState.Fail: return "不通";
                case ConnectivityCheckState.NotApplicable: return "不适用";
                case ConnectivityCheckState.Testing: return "测试中";
                default: return "未测试";
            }
        }

        private static string PublicStatusText(PublicIpStatus status)
        {
            switch (status)
            {
                case PublicIpStatus.NativePublic: return "公网直连";
                case PublicIpStatus.BehindNat: return "NAT 后";
                case PublicIpStatus.NotAvailable: return "不可用";
                default: return "未知";
            }
        }

        private static string GetOperatingSystem()
        {
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT Caption,Version,BuildNumber,OSArchitecture FROM Win32_OperatingSystem"))
                using (ManagementObjectCollection results = searcher.Get())
                {
                    foreach (ManagementObject obj in results)
                    {
                        using (obj)
                        {
                            string caption = Text(obj["Caption"]);
                            string version = Text(obj["Version"]);
                            string build = Text(obj["BuildNumber"]);
                            string arch = Text(obj["OSArchitecture"]);
                            if (!string.IsNullOrEmpty(caption))
                            {
                                string text = caption;
                                if (!string.IsNullOrEmpty(version)) text += " · " + version;
                                if (!string.IsNullOrEmpty(build)) text += " (Build " + build + ")";
                                if (!string.IsNullOrEmpty(arch)) text += " · " + arch;
                                return text;
                            }
                        }
                    }
                }
            }
            catch
            {
            }

            string fallback = Environment.OSVersion.VersionString;
            if (Environment.Is64BitOperatingSystem) fallback += " · x64";
            return fallback;
        }

        private static string GetCpu()
        {
            List<string> names = new List<string>();
            int cores = 0;
            int threads = 0;
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT Name,NumberOfCores,NumberOfLogicalProcessors FROM Win32_Processor"))
                using (ManagementObjectCollection results = searcher.Get())
                {
                    foreach (ManagementObject obj in results)
                    {
                        using (obj)
                        {
                            string name = Text(obj["Name"]);
                            if (!string.IsNullOrEmpty(name) && !names.Contains(name)) names.Add(name.Trim());
                            cores += ToInt(obj["NumberOfCores"]);
                            threads += ToInt(obj["NumberOfLogicalProcessors"]);
                        }
                    }
                }
            }
            catch
            {
            }

            if (names.Count == 0) return "未读取到";
            string text = string.Join(" / ", names.ToArray());
            if (cores > 0 || threads > 0)
            {
                text += " · ";
                if (cores > 0) text += cores + " 核";
                if (threads > 0) text += (cores > 0 ? " " : string.Empty) + threads + " 线程";
            }
            return text;
        }

        private static string GetMemory()
        {
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT TotalPhysicalMemory FROM Win32_ComputerSystem"))
                using (ManagementObjectCollection results = searcher.Get())
                {
                    foreach (ManagementObject obj in results)
                    {
                        using (obj)
                        {
                            ulong bytes = ToULong(obj["TotalPhysicalMemory"]);
                            if (bytes > 0)
                            {
                                double gb = bytes / 1024.0 / 1024.0 / 1024.0;
                                return gb.ToString("0.#", CultureInfo.InvariantCulture) + " GB";
                            }
                        }
                    }
                }
            }
            catch
            {
            }
            return "未读取到";
        }

        private static string GetGpu()
        {
            List<string> gpus = new List<string>();
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT Name,DriverVersion FROM Win32_VideoController"))
                using (ManagementObjectCollection results = searcher.Get())
                {
                    foreach (ManagementObject obj in results)
                    {
                        using (obj)
                        {
                            string name = Text(obj["Name"]);
                            if (string.IsNullOrEmpty(name)) continue;
                            string driver = Text(obj["DriverVersion"]);
                            string line = name.Trim();
                            if (!string.IsNullOrEmpty(driver)) line += " · 驱动 " + driver;
                            gpus.Add(line);
                        }
                    }
                }
            }
            catch
            {
            }

            if (gpus.Count == 0) return "未读取到";
            return string.Join(Environment.NewLine, gpus.ToArray());
        }

        private static string Text(object value)
        {
            if (value == null) return string.Empty;
            return value.ToString().Trim();
        }

        private static int ToInt(object value)
        {
            if (value == null) return 0;
            try
            {
                return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return 0;
            }
        }

        private static ulong ToULong(object value)
        {
            if (value == null) return 0;
            try
            {
                return Convert.ToUInt64(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return 0;
            }
        }

        private static void AddSection(SelfCheckReport report, string title)
        {
            AddItem(report, title, string.Empty, false, true);
        }

        private static void AddItem(SelfCheckReport report, string label, string value)
        {
            AddItem(report, label, value, false, false);
        }

        private static void AddItem(SelfCheckReport report, string label, string value, bool sensitive)
        {
            AddItem(report, label, value, sensitive, false);
        }

        private static void AddItem(SelfCheckReport report, string label, string value, bool sensitive, bool isSection)
        {
            SelfCheckItem item = new SelfCheckItem();
            item.Label = label ?? string.Empty;
            item.Value = value ?? string.Empty;
            item.Sensitive = sensitive;
            item.IsSection = isSection;
            report.Items.Add(item);
        }

        private static bool HasItem(SelfCheckReport report, string label)
        {
            for (int i = 0; i < report.Items.Count; i++)
            {
                if (string.Equals(report.Items[i].Label, label, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private static void Report(Action<string> progress, string message)
        {
            if (progress != null) progress(message);
        }
    }
}
