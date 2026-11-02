using System;
using System.Collections.Generic;
using System.Management;
using Microsoft.Win32;

namespace IpInfo.Core
{
    /// <summary>Zako 虚拟显示器的整体状态。</summary>
    public enum ZakoDisplayState
    {
        Unknown = 0,
        /// <summary>没有找到 Zako 显示设备。</summary>
        NotInstalled,
        /// <summary>适配器工作正常。</summary>
        Healthy,
        /// <summary>设备被禁用。</summary>
        Disabled,
        /// <summary>驱动缺失或安装失败。</summary>
        DriverMissing,
        /// <summary>其他设备错误。</summary>
        Error
    }

    /// <summary>一个 Zako 相关设备节点。</summary>
    public sealed class ZakoDeviceInfo
    {
        public string Name { get; set; }
        public string DeviceClass { get; set; }
        public string Status { get; set; }
        public string DeviceId { get; set; }
        public int ProblemCode { get; set; }
        public string ProblemText { get; set; }
        public string Summary { get; set; }

        /// <summary>驱动版本，来自 Win32_PnPSignedDriver。</summary>
        public string DriverVersion { get; set; }
        public string DriverDate { get; set; }
        public string DriverProvider { get; set; }
        public string InfName { get; set; }

        public ZakoDeviceInfo()
        {
            Name = string.Empty;
            DeviceClass = string.Empty;
            Status = string.Empty;
            DeviceId = string.Empty;
            ProblemText = string.Empty;
            Summary = string.Empty;
            DriverVersion = string.Empty;
            DriverDate = string.Empty;
            DriverProvider = string.Empty;
            InfName = string.Empty;
        }
    }

    /// <summary>Windows 版本与 Zako 虚拟显示器状态。</summary>
    public sealed class ZakoDisplayStatus
    {
        public string OsName { get; set; }
        public string OsDisplay { get; set; }
        public string OsArch { get; set; }

        public ZakoDisplayState State { get; set; }

        /// <summary>显示适配器节点；未安装时为空。</summary>
        public ZakoDeviceInfo Adapter { get; set; }

        public List<ZakoDeviceInfo> Devices { get; set; }
        public List<string> Warnings { get; set; }

        public string Error { get; set; }
        public DateTime DetectedAt { get; set; }

        public ZakoDisplayStatus()
        {
            OsName = string.Empty;
            OsDisplay = string.Empty;
            OsArch = string.Empty;
            State = ZakoDisplayState.Unknown;
            Devices = new List<ZakoDeviceInfo>();
            Warnings = new List<string>();
            Error = string.Empty;
            DetectedAt = DateTime.Now;
        }

        public bool IsHealthy
        {
            get { return State == ZakoDisplayState.Healthy; }
        }

        public string StateLabel
        {
            get
            {
                switch (State)
                {
                    case ZakoDisplayState.Healthy: return "正常";
                    case ZakoDisplayState.NotInstalled: return "未安装";
                    case ZakoDisplayState.Disabled: return "已禁用";
                    case ZakoDisplayState.DriverMissing: return "驱动缺失";
                    case ZakoDisplayState.Error: return "异常";
                    default: return "未知";
                }
            }
        }

        public string Summary
        {
            get
            {
                if (State == ZakoDisplayState.NotInstalled)
                    return "未检测到 Zako 虚拟显示器";

                string adapter = Adapter == null || string.IsNullOrEmpty(Adapter.Name) ? "Zako 显示适配器" : Adapter.Name;
                return StateLabel + " · " + adapter;
            }
        }

        public string ToDiagnostics()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine("杂鱼串流工具箱 · Zako 虚拟显示器检测");
            sb.AppendLine("检测结果：" + Summary);
            sb.AppendLine("操作系统：" + NullSafe(OsDisplay));
            sb.AppendLine("系统架构：" + NullSafe(OsArch));

            if (Adapter != null)
            {
                sb.AppendLine("显示适配器：" + NullSafe(Adapter.Name));
                sb.AppendLine("设备状态：" + NullSafe(Adapter.Status) + " · " + NullSafe(Adapter.ProblemText));
                if (!string.IsNullOrEmpty(Adapter.DriverVersion))
                {
                    sb.AppendLine("驱动版本：" + Adapter.DriverVersion
                        + (string.IsNullOrEmpty(Adapter.DriverDate) ? string.Empty : "（" + Adapter.DriverDate + "）")
                        + (string.IsNullOrEmpty(Adapter.DriverProvider) ? string.Empty : " · " + Adapter.DriverProvider));
                }
                if (!string.IsNullOrEmpty(Adapter.DeviceId)) sb.AppendLine("设备 ID：" + Adapter.DeviceId);
            }

            if (Devices.Count > 0)
            {
                sb.AppendLine("设备列表：");
                for (int i = 0; i < Devices.Count; i++)
                {
                    ZakoDeviceInfo device = Devices[i];
                    sb.AppendLine("  - " + device.Name + " · " + device.DeviceClass + " · "
                        + device.Status + " · " + device.ProblemText);
                }
            }

            if (Warnings.Count > 0)
            {
                sb.AppendLine("提示：");
                for (int i = 0; i < Warnings.Count; i++) sb.AppendLine("  - " + Warnings[i]);
            }
            if (!string.IsNullOrEmpty(Error)) sb.AppendLine("错误：" + Error);
            return sb.ToString();
        }

        private static string NullSafe(string value)
        {
            return string.IsNullOrEmpty(value) ? "—" : value;
        }
    }

    /// <summary>
    /// 只读检测 Windows 版本与 Zako 虚拟显示器状态。
    /// 设备信息通过 WMI 的 Win32_PnPEntity 读取，失败时降级到注册表枚举。
    /// </summary>
    public static class ZakoDisplayDetector
    {
        public static ZakoDisplayStatus Detect()
        {
            ZakoDisplayStatus status = new ZakoDisplayStatus();

            try
            {
                ReadOperatingSystem(status);
            }
            catch (Exception ex)
            {
                status.Warnings.Add("读取系统版本失败：" + ex.Message);
            }

            try
            {
                ReadDevices(status);
            }
            catch (Exception ex)
            {
                status.Error = "读取设备信息失败：" + ex.Message;
            }

            return status;
        }

        private static void ReadOperatingSystem(ZakoDisplayStatus status)
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

            string normalized = NormalizeWindowsName(product, buildNumber);

            status.OsName = normalized;
            status.OsArch = Environment.Is64BitOperatingSystem ? "x64" : "x86";
            status.OsDisplay = normalized + " " + display + " (Build " + build
                + (ubr > 0 ? "." + ubr.ToString() : string.Empty) + ") · " + status.OsArch;
        }

        private static string NormalizeWindowsName(string product, int buildNumber)
        {
            if (string.IsNullOrEmpty(product)) return "Windows";
            if (product.IndexOf("Server", StringComparison.OrdinalIgnoreCase) >= 0) return product;
            if (buildNumber >= 22000 && product.IndexOf("Windows 11", StringComparison.OrdinalIgnoreCase) < 0)
                return product.Replace("Windows 10", "Windows 11");
            return product;
        }

        private static void ReadDevices(ZakoDisplayStatus status)
        {
            try
            {
                ReadDevicesFromWmi(status);
            }
            catch (Exception ex)
            {
                status.Warnings.Add("WMI 查询失败，改用注册表枚举：" + ex.Message);
                ReadDevicesFromRegistry(status);
            }

            if (status.Devices.Count == 0)
            {
                status.State = ZakoDisplayState.NotInstalled;
                return;
            }

            ReadDriverInfo(status);

            ZakoDeviceInfo adapter = null;
            for (int i = 0; i < status.Devices.Count; i++)
            {
                ZakoDeviceInfo device = status.Devices[i];
                if (string.Equals(device.DeviceClass, "Display", StringComparison.OrdinalIgnoreCase))
                {
                    adapter = device;
                    break;
                }
            }

            status.Adapter = adapter;
            if (adapter == null)
            {
                status.State = ZakoDisplayState.NotInstalled;
                return;
            }

            switch (adapter.ProblemCode)
            {
                case 0:
                    status.State = ZakoDisplayState.Healthy;
                    break;
                case 22:
                    status.State = ZakoDisplayState.Disabled;
                    break;
                case 28:
                case 18:
                case 31:
                    status.State = ZakoDisplayState.DriverMissing;
                    break;
                default:
                    status.State = ZakoDisplayState.Error;
                    break;
            }

            for (int i = 0; i < status.Devices.Count; i++)
            {
                ZakoDeviceInfo device = status.Devices[i];
                if (device.ProblemCode == 45)
                {
                    status.Warnings.Add("设备「" + device.Name + "」当前未连接（Code 45），虚拟显示器空闲时属于正常现象。");
                }
            }
        }

        /// <summary>
        /// 驱动信息从注册表读：设备的 Driver 值指向 Control\Class 下的键，
        /// 比 WMI 的 Win32_PnPSignedDriver 快一个数量级（后者要十几秒）。
        /// </summary>
        private static void ReadDriverInfo(ZakoDisplayStatus status)
        {
            for (int i = 0; i < status.Devices.Count; i++)
            {
                ZakoDeviceInfo device = status.Devices[i];
                if (string.IsNullOrEmpty(device.DeviceId)) continue;

                try
                {
                    ReadDriverInfo(device);
                }
                catch
                {
                }
            }
        }

        private static void ReadDriverInfo(ZakoDeviceInfo device)
        {
            string driverKey;
            using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (RegistryKey deviceKey = baseKey.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + device.DeviceId))
            {
                if (deviceKey == null) return;
                driverKey = deviceKey.GetValue("Driver") as string;
            }

            if (string.IsNullOrEmpty(driverKey)) return;

            using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (RegistryKey classKey = baseKey.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\" + driverKey))
            {
                if (classKey == null) return;

                device.DriverVersion = (classKey.GetValue("DriverVersion") as string) ?? string.Empty;
                device.DriverProvider = (classKey.GetValue("ProviderName") as string) ?? string.Empty;
                device.InfName = (classKey.GetValue("InfPath") as string) ?? string.Empty;

                byte[] dateData = classKey.GetValue("DriverDateData") as byte[];
                if (dateData != null && dateData.Length == 8)
                {
                    long fileTime = BitConverter.ToInt64(dateData, 0);
                    if (fileTime > 0)
                    {
                        device.DriverDate = DateTime.FromFileTimeUtc(fileTime).ToLocalTime().ToString("yyyy-MM-dd");
                    }
                }
            }
        }

        private static void ReadDevicesFromWmi(ZakoDisplayStatus status)
        {
            string query = "SELECT Name, PNPClass, Status, ConfigManagerErrorCode, DeviceID FROM Win32_PnPEntity";
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(query))
            {
                foreach (ManagementBaseObject item in searcher.Get())
                {
                    string name = item["Name"] as string;
                    if (string.IsNullOrEmpty(name) || name.IndexOf("zako", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    ZakoDeviceInfo device = new ZakoDeviceInfo();
                    device.Name = name;
                    device.DeviceClass = (item["PNPClass"] as string) ?? string.Empty;
                    device.Status = (item["Status"] as string) ?? string.Empty;
                    device.DeviceId = (item["DeviceID"] as string) ?? string.Empty;

                    object problem = item["ConfigManagerErrorCode"];
                    device.ProblemCode = problem is uint ? (int)(uint)problem
                        : (problem is int ? (int)problem : 0);
                    device.ProblemText = DescribeProblem(device.ProblemCode);
                    device.Summary = device.Name + " · " + device.DeviceClass + " · " + device.ProblemText;

                    status.Devices.Add(device);
                }
            }
        }

        private static void ReadDevicesFromRegistry(ZakoDisplayStatus status)
        {
            try
            {
                using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (RegistryKey enumKey = baseKey.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum"))
                {
                    if (enumKey == null) return;

                    foreach (string bus in enumKey.GetSubKeyNames())
                    {
                        using (RegistryKey busKey = enumKey.OpenSubKey(bus))
                        {
                            if (busKey == null) continue;

                            foreach (string instance in busKey.GetSubKeyNames())
                            {
                                using (RegistryKey instanceKey = busKey.OpenSubKey(instance))
                                {
                                    if (instanceKey == null) continue;

                                    string description = instanceKey.GetValue("DeviceDesc") as string;
                                    if (string.IsNullOrEmpty(description)
                                        || description.IndexOf("zako", StringComparison.OrdinalIgnoreCase) < 0)
                                    {
                                        continue;
                                    }

                                    ZakoDeviceInfo device = new ZakoDeviceInfo();
                                    device.Name = description.Substring(description.LastIndexOf(';') + 1);
                                    device.DeviceClass = bus;
                                    device.DeviceId = bus + "\\" + instance;
                                    device.Status = "未知";
                                    device.ProblemText = "注册表枚举无法读取问题码";
                                    device.Summary = device.Name + " · " + device.DeviceClass;
                                    status.Devices.Add(device);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                status.Warnings.Add("注册表枚举失败：" + ex.Message);
            }
        }

        private static string DescribeProblem(int code)
        {
            switch (code)
            {
                case 0: return "工作正常";
                case 10: return "无法启动";
                case 12: return "资源不足";
                case 18: return "需要重新安装驱动";
                case 21: return "系统正在移除设备";
                case 22: return "已禁用";
                case 24: return "设备不存在";
                case 28: return "驱动未安装";
                case 31: return "驱动加载失败";
                case 43: return "设备上报故障";
                case 45: return "当前未连接";
                default: return "问题码 " + code;
            }
        }
    }
}
