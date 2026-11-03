using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text.RegularExpressions;
using System.Threading;

namespace Zayu.Core
{
    /// <summary>
    /// 检测 Foundation Sunshine 基地版附带的显示、输入、音频和 USB 透传组件。
    /// 只做只读探测，不安装 / 卸载任何驱动。
    /// </summary>
    public static class ComponentStatusDetector
    {
        public static ComponentStatusReport Collect(Action<string> progress, CancellationToken cancellationToken)
        {
            ComponentStatusReport report = new ComponentStatusReport();
            try
            {
                Report(progress, "正在读取 Sunshine 安装信息…");
                SunshineDetectionResult sunshine = null;
                try
                {
                    sunshine = SunshineDetector.Detect();
                }
                catch
                {
                }

                SunnyService service = null;
                try
                {
                    service = new SunnyService(SunshineServiceManager.Detect());
                }
                catch
                {
                }

                string installPath = ResolveInstallPath(sunshine, service);
                report.SunshineInstallPath = installPath;

                AddSection(report, "基础环境");
                AddItem(report, "基础环境", "Sunshine",
                    sunshine == null || !sunshine.IsInstalled ? ComponentHealth.NotInstalled : ComponentHealth.Installed,
                    sunshine == null ? "—" : sunshine.Version,
                    sunshine == null ? "未检测到 Sunshine" :
                        sunshine.KindLabel + " · " + sunshine.InstallStateLabel,
                    sunshine == null ? string.Empty : sunshine.InstallPath);
                AddItem(report, "基础环境", "Sunshine 服务",
                    service == null || service.Status == null || !service.Status.IsFound ? ComponentHealth.NotInstalled : ComponentHealth.Ready,
                    string.Empty,
                    service == null || service.Status == null ? "未检测到服务"
                        : service.Status.StateLabel + " · 启动类型 " + service.Status.StartModeLabel,
                    service == null || service.Status == null ? string.Empty : service.Status.ServiceName);

                cancellationToken.ThrowIfCancellationRequested();
                Report(progress, "正在枚举 PnP 设备与驱动…");
                List<PnpDevice> devices = QueryPnpDevices();
                Dictionary<string, DriverInfo> drivers = QueryDrivers();

                cancellationToken.ThrowIfCancellationRequested();
                AddSection(report, "显示与输入");
                AddZakoDisplay(report, installPath);
                AddZakoMouse(report, installPath, devices);
                AddViGEm(report, devices, drivers);
                AddDualSense(report, installPath, devices);

                cancellationToken.ThrowIfCancellationRequested();
                AddSection(report, "音频");
                AddSteamSpeakers(report, devices);
                AddVbCable(report, devices);

                cancellationToken.ThrowIfCancellationRequested();
                AddSection(report, "USB 透传");
                AddUsbIp(report, drivers, devices);

                cancellationToken.ThrowIfCancellationRequested();
                AddSection(report, "画质增强");
                AddHdrComponent(report, installPath, "DLSS NR", Path.Combine("hdr_enhanced", "nvidia_dlssnr", "foundation_dlssnr_adapter.dll"));
                AddHdrComponent(report, installPath, "RTX Video", Path.Combine("hdr_enhanced", "nvidia_rtx_video", "foundation_rtx_video_adapter.dll"));

                report.Timestamp = DateTime.Now;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                report.Error = "组件检测失败：" + ex.GetType().Name + " " + ex.Message;
            }
            return report;
        }

        private static void AddZakoDisplay(ComponentStatusReport report, string installPath)
        {
            try
            {
                ZakoDisplayStatus zako = ZakoDisplayDetector.Detect();
                if (zako == null)
                {
                    AddItem(report, "显示与输入", "ZakoDisplay 虚拟显示器",
                        ComponentHealth.Unknown, "—", "检测无结果", string.Empty);
                    return;
                }

                ComponentHealth health = HealthFromZako(zako.State);
                string version = zako.Adapter == null ? string.Empty : zako.Adapter.DriverVersion;
                if (string.IsNullOrEmpty(version)) version = zako.BundledDriverVersion;
                string detail = zako.StateLabel;
                if (zako.Adapter != null && !string.IsNullOrEmpty(zako.Adapter.Name))
                {
                    detail += " · " + zako.Adapter.Name;
                }
                if (zako.State == ZakoDisplayState.MultipleAdapters)
                {
                    detail += " · " + zako.Devices.Count + " 个适配器";
                }
                if (!string.IsNullOrEmpty(zako.Error))
                {
                    detail += " · " + zako.Error;
                }
                AddItem(report, "显示与输入", "ZakoDisplay 虚拟显示器", health, version, detail,
                    zako.Adapter == null ? string.Empty : zako.Adapter.DeviceId,
                    ComponentOperationCatalog.ZakoDisplay);
            }
            catch (Exception ex)
            {
                AddItem(report, "显示与输入", "ZakoDisplay 虚拟显示器",
                    ComponentHealth.Error, "—", "检测失败：" + ex.Message, string.Empty,
                    ComponentOperationCatalog.ZakoDisplay);
            }
        }

        private static void AddZakoMouse(ComponentStatusReport report, string installPath, List<PnpDevice> devices)
        {
            PnpDevice device = FindFirst(devices, delegate(PnpDevice d)
            {
                return Contains(d.Name, "Zako Virtual Mouse")
                    || Contains(d.DeviceId, "VID_1ACE&PID_0002")
                    || Contains(d.Name, "Zako 虚拟鼠标");
            });

            string version = GetFileVersion(Path.Combine(installPath, "tools", "vmouse", "ZakoVirtualMouse.dll"));
            if (device == null)
            {
                AddItem(report, "显示与输入", "Zako 虚拟鼠标",
                    string.IsNullOrEmpty(version) ? ComponentHealth.NotInstalled : ComponentHealth.Installed,
                    version, string.IsNullOrEmpty(version) ? "未检测到虚拟鼠标设备" : "驱动文件存在，设备未创建",
                    string.Empty, ComponentOperationCatalog.ZakoMouse);
                return;
            }

            ComponentHealth health = HealthFromPnp(device);
            AddItem(report, "显示与输入", "Zako 虚拟鼠标", health, version,
                PnpDetail(device), device.DeviceId, ComponentOperationCatalog.ZakoMouse);
        }

        private static void AddViGEm(ComponentStatusReport report, List<PnpDevice> devices,
            Dictionary<string, DriverInfo> drivers)
        {
            string driverPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "drivers", "ViGEmBus.sys");
            string version = File.Exists(driverPath) ? GetFileVersion(driverPath) : string.Empty;
            bool installed = File.Exists(driverPath);

            PnpDevice device = FindFirst(devices, delegate(PnpDevice d)
            {
                return Contains(d.Name, "Virtual Gamepad Emulation")
                    || Contains(d.Name, "ViGEm")
                    || Contains(d.DeviceId, "ViGEm");
            });
            DriverInfo service = GetDriver(drivers, "ViGEmBus");
            bool running = (device != null && HealthFromPnp(device) == ComponentHealth.Ready)
                || IsDriverRunning(service);
            bool versionOk = IsVigemVersionOk(version);

            ComponentHealth health;
            string detail;
            if (!installed)
            {
                health = ComponentHealth.NotInstalled;
                detail = "未安装 ViGEmBus";
            }
            else if (!versionOk)
            {
                health = ComponentHealth.Warning;
                detail = "版本过旧，基地版要求 v1.17 或更高";
            }
            else if (running)
            {
                health = ComponentHealth.Ready;
                detail = device == null ? "驱动运行中" : PnpDetail(device);
            }
            else
            {
                health = ComponentHealth.Warning;
                detail = "已安装但驱动未运行，可能需要重启或修复";
            }

            string deviceId = device == null ? string.Empty : device.DeviceId;
            AddItem(report, "显示与输入", "ViGEmBus 虚拟手柄驱动", health, version,
                detail, deviceId, ComponentOperationCatalog.ViGEm);
        }

        private static bool IsVigemVersionOk(string version)
        {
            if (string.IsNullOrEmpty(version)) return false;
            string[] parts = version.Split('.');
            int major;
            int minor;
            return parts.Length >= 2
                && int.TryParse(parts[0], out major)
                && int.TryParse(parts[1], out minor)
                && (major > 1 || (major == 1 && minor >= 17));
        }

        private static void AddDualSense(ComponentStatusReport report, string installPath, List<PnpDevice> devices)
        {
            string activeRoot = Path.Combine(installPath, "tools", "sunshine-ds5-component", "active");
            string manifestPath = Path.Combine(activeRoot, "component.json");
            string compatibilityPath = Path.Combine(activeRoot, "component-compatibility.json");
            string sidecarPath = Path.Combine(activeRoot, "Sunshine.Ds5Sidecar.exe");
            string corePath = Path.Combine(activeRoot, "HIDMaestro.Core.dll");

            bool hasManifest = File.Exists(manifestPath);
            bool hasCompatibility = File.Exists(compatibilityPath);
            bool hasSidecar = File.Exists(sidecarPath);
            bool hasCore = File.Exists(corePath);
            string componentVersion = ReadJsonString(manifestPath, "component_version");
            string hidmaestroVersion = ReadJsonString(manifestPath, "hidmaestro_version");
            string usbipVersion = ReadJsonString(manifestPath, "usbip_version");
            string compatibilityVersion = ReadJsonString(compatibilityPath, "component_version");
            string compatibilityHidmaestro = ReadJsonString(compatibilityPath, "hidmaestro_version");
            string coreVersion = GetFileVersion(corePath);
            string sidecarVersion = GetFileVersion(sidecarPath);

            bool sidecarRunning = false;
            int sidecarPid = 0;
            try
            {
                sidecarPid = FirstProcessId("Sunshine.Ds5Sidecar");
                sidecarRunning = sidecarPid > 0;
            }
            catch
            {
            }
            int hidDevices = CountDevices(devices, delegate(PnpDevice d)
            {
                return Contains(d.DeviceId, "HIDMAESTRO") || Contains(d.Name, "HIDMaestro");
            });

            ComponentHealth health;
            string detail;
            if (!hasManifest && !hasSidecar)
            {
                health = ComponentHealth.NotInstalled;
                detail = "DualSense 组件未安装";
            }
            else if (!hasManifest || !hasCompatibility || !hasSidecar || !hasCore)
            {
                health = ComponentHealth.Error;
                detail = "组件文件不完整";
            }
            else if (string.IsNullOrEmpty(hidmaestroVersion)
                || string.IsNullOrEmpty(compatibilityHidmaestro)
                || !string.Equals(hidmaestroVersion, compatibilityHidmaestro, StringComparison.Ordinal)
                || !string.Equals(componentVersion, compatibilityVersion, StringComparison.Ordinal))
            {
                health = ComponentHealth.Error;
                detail = "组件清单缺少 HIDMaestro 版本或版本不匹配，Sunshine 会判为异常";
            }
            else if (!string.IsNullOrEmpty(usbipVersion)
                && VersionUpdateService.IsNewer(
                    ComponentOperationCatalog.RequiredUsbIpVersion, usbipVersion))
            {
                health = ComponentHealth.Error;
                detail = "组件声明的 USB/IP " + usbipVersion + " 低于基地版要求 "
                    + ComponentOperationCatalog.RequiredUsbIpVersion;
            }
            else if (sidecarRunning)
            {
                health = ComponentHealth.Running;
                detail = "Sidecar 运行中";
            }
            else if (hidDevices > 0)
            {
                health = ComponentHealth.Ready;
                detail = "已创建 " + hidDevices + " 个 HIDMaestro 虚拟设备";
            }
            else
            {
                health = ComponentHealth.Ready;
                detail = "组件已就绪，串流时创建虚拟手柄";
            }

            string version = componentVersion;
            if (string.IsNullOrEmpty(version)) version = sidecarVersion;
            if (!string.IsNullOrEmpty(hidmaestroVersion))
            {
                version = string.IsNullOrEmpty(version)
                    ? "HIDMaestro " + hidmaestroVersion
                    : version + " / HIDMaestro " + hidmaestroVersion;
            }
            else if (!string.IsNullOrEmpty(coreVersion))
            {
                version = string.IsNullOrEmpty(version)
                    ? "HIDMaestro " + coreVersion
                    : version + " / HIDMaestro " + coreVersion;
            }
            if (sidecarRunning) detail += " · PID " + sidecarPid;
            if (!string.IsNullOrEmpty(usbipVersion)) detail += " · USB/IP " + usbipVersion;

            AddItem(report, "显示与输入", "DualSense / HIDMaestro 虚拟手柄", health, version,
                detail, sidecarPath, ComponentOperationCatalog.DualSense);
        }

        private static void AddSteamSpeakers(ComponentStatusReport report, List<PnpDevice> devices)
        {
            List<PnpDevice> matches = FindAll(devices, delegate(PnpDevice d)
            {
                return Contains(d.Name, "Steam Streaming Speakers");
            });
            if (matches.Count == 0)
            {
                AddItem(report, "音频", "Steam Streaming Speakers（Steam 虚拟声卡）",
                    ComponentHealth.NotInstalled, "—", "负责串流音频输出与主机端静音；未检测到虚拟声卡", string.Empty, ComponentOperationCatalog.SteamSpeakers);
                return;
            }

            ComponentHealth health = BestHealth(matches);
            string detail = "负责串流音频输出与主机端静音 · " + DescribeDevices(matches);
            AddItem(report, "音频", "Steam Streaming Speakers（Steam 虚拟声卡）",
                health, "—", detail, matches[0].DeviceId, ComponentOperationCatalog.SteamSpeakers);
        }

        private static void AddVbCable(ComponentStatusReport report, List<PnpDevice> devices)
        {
            List<PnpDevice> matches = FindAll(devices, delegate(PnpDevice d)
            {
                return Contains(d.Name, "VB-Audio")
                    || Contains(d.Manufacturer, "VB-Audio")
                    || Contains(d.Name, "VB-CABLE")
                    || Contains(d.Name, "CABLE Input")
                    || Contains(d.Name, "CABLE Output");
            });
            if (matches.Count == 0)
            {
                AddItem(report, "音频", "VB-CABLE（虚拟麦克风）",
                    ComponentHealth.NotInstalled, "—", "负责把串流设备麦克风声音发送到主机；未检测到 VB-Audio Virtual Cable", string.Empty, ComponentOperationCatalog.VbCable);
                return;
            }

            ComponentHealth health = BestHealth(matches);
            AddItem(report, "音频", "VB-CABLE（虚拟麦克风）",
                health, "—", "负责把串流设备麦克风声音发送到主机 · " + DescribeDevices(matches), matches[0].DeviceId, ComponentOperationCatalog.VbCable);
        }

        private static void AddSunshineMicrophone(ComponentStatusReport report, List<PnpDevice> devices,
            Dictionary<string, DriverInfo> drivers)
        {
            List<PnpDevice> matches = FindAll(devices, delegate(PnpDevice d)
            {
                return Contains(d.Name, "Sunshine Virtual Microphone")
                    || Contains(d.Name, "Sunshine 虚拟麦克风");
            });

            DriverInfo ude = GetDriver(drivers, "usbip2_ude");
            DriverInfo filter = GetDriver(drivers, "usbip2_filter");
            bool usbipReady = IsDriverRunning(ude) && IsDriverRunning(filter);

            if (matches.Count == 0)
            {
                AddItem(report, "音频", "Sunshine Virtual Microphone（USB/IP 虚拟麦克风）",
                    usbipReady ? ComponentHealth.NotInstalled : ComponentHealth.Warning,
                    "—",
                    usbipReady
                        ? "不是独立驱动；串流时由虚拟设备宿主创建。当前 USB/IP 0.9.8.1 已就绪，开始串流后会自动出现"
                        : "不是独立驱动；需要 DualSense 组件与 USB/IP 0.9.8.1，串流时由虚拟设备宿主创建",
                    string.Empty);
                return;
            }

            AddItem(report, "音频", "Sunshine Virtual Microphone（USB/IP 虚拟麦克风）",
                BestHealth(matches), "—", "把串流客户端麦克风作为 Windows 录音设备 · " + DescribeDevices(matches), matches[0].DeviceId);
        }

        private static void AddUsbIp(ComponentStatusReport report, Dictionary<string, DriverInfo> drivers,
            List<PnpDevice> devices)
        {
            DriverInfo ude = GetDriver(drivers, "usbip2_ude");
            DriverInfo filter = GetDriver(drivers, "usbip2_filter");
            bool udeRunning = IsDriverRunning(ude);
            bool filterRunning = IsDriverRunning(filter);

            List<PnpDevice> hostDevices = FindAll(devices, delegate(PnpDevice d)
            {
                return Contains(d.DeviceId, "USBIP_WIN2")
                    || Contains(d.DeviceId, "HIDMAESTRO_UDE")
                    || Contains(d.Name, "USB/IP");
            });

            ComponentHealth health;
            string detail;
            if (!udeRunning && !filterRunning)
            {
                health = ComponentHealth.NotInstalled;
                detail = "usbip2_ude / usbip2_filter 未运行";
            }
            else if (udeRunning && filterRunning && hostDevices.Count > 0)
            {
                health = ComponentHealth.Running;
                detail = "USB/IP 驱动运行中 · " + hostDevices.Count + " 个主机设备";
            }
            else
            {
                health = ComponentHealth.Warning;
                detail = "驱动状态：ude=" + DriverStateText(ude) + "，filter=" + DriverStateText(filter);
            }

            string version = DriverVersion(ude);
            if (health == ComponentHealth.Ready && !string.IsNullOrEmpty(version)
                && VersionUpdateService.IsNewer(ComponentOperationCatalog.RequiredUsbIpVersion, version))
            {
                health = ComponentHealth.Warning;
                detail += " · 基地版要求 " + ComponentOperationCatalog.RequiredUsbIpVersion
                    + "，建议修复";
            }
            AddItem(report, "USB 透传", "USB/IP 透传驱动", health, version, detail,
                hostDevices.Count == 0 ? string.Empty : hostDevices[0].DeviceId, ComponentOperationCatalog.UsbIp);
        }

        private static void AddHdrComponent(ComponentStatusReport report, string installPath,
            string name, string relativePath)
        {
            string path = Path.Combine(installPath, "tools", relativePath);
            if (!File.Exists(path))
            {
                AddItem(report, "画质增强", name + " 增强组件",
                    ComponentHealth.NotInstalled, "—", "未安装", path);
                return;
            }

            AddItem(report, "画质增强", name + " 增强组件",
                ComponentHealth.Installed, GetFileVersion(path), "组件文件已安装", path);
        }

        private static string ResolveInstallPath(SunshineDetectionResult sunshine, SunnyService service)
        {
            if (sunshine != null && !string.IsNullOrEmpty(sunshine.InstallPath))
            {
                return sunshine.InstallPath.TrimEnd('\\');
            }
            if (service != null && service.Status != null && !string.IsNullOrEmpty(service.Status.ImagePath))
            {
                string tools = Path.GetDirectoryName(service.Status.ImagePath);
                if (!string.IsNullOrEmpty(tools))
                {
                    string root = Path.GetDirectoryName(tools);
                    if (!string.IsNullOrEmpty(root)) return root.TrimEnd('\\');
                }
            }
            return string.Empty;
        }

        private static ComponentHealth HealthFromZako(ZakoDisplayState state)
        {
            switch (state)
            {
                case ZakoDisplayState.Healthy: return ComponentHealth.Ready;
                case ZakoDisplayState.NotInstalled: return ComponentHealth.NotInstalled;
                case ZakoDisplayState.Disabled:
                case ZakoDisplayState.DriverMissing:
                case ZakoDisplayState.Error:
                case ZakoDisplayState.MultipleAdapters: return ComponentHealth.Error;
                default: return ComponentHealth.Unknown;
            }
        }

        private static ComponentHealth HealthFromPnp(PnpDevice device)
        {
            if (device == null) return ComponentHealth.Unknown;
            if (device.ErrorCode != 0) return ComponentHealth.Error;
            if (string.Equals(device.Status, "OK", StringComparison.OrdinalIgnoreCase))
            {
                return ComponentHealth.Ready;
            }
            if (string.Equals(device.Status, "Error", StringComparison.OrdinalIgnoreCase))
            {
                return ComponentHealth.Error;
            }
            return ComponentHealth.Warning;
        }

        private static ComponentHealth BestHealth(List<PnpDevice> devices)
        {
            ComponentHealth best = ComponentHealth.NotInstalled;
            for (int i = 0; i < devices.Count; i++)
            {
                ComponentHealth health = HealthFromPnp(devices[i]);
                if (health == ComponentHealth.Ready) return ComponentHealth.Ready;
                if (health == ComponentHealth.Error) best = ComponentHealth.Error;
                else if (best != ComponentHealth.Error && health == ComponentHealth.Warning)
                {
                    best = ComponentHealth.Warning;
                }
            }
            return best;
        }

        private static string PnpDetail(PnpDevice device)
        {
            if (device == null) return string.Empty;
            string detail = string.IsNullOrEmpty(device.Status) ? "状态未知" : device.Status;
            if (device.ErrorCode != 0) detail += " · 错误代码 " + device.ErrorCode;
            if (!string.IsNullOrEmpty(device.Manufacturer)) detail += " · " + device.Manufacturer;
            return detail;
        }

        private static string DescribeDevices(List<PnpDevice> devices)
        {
            List<string> names = new List<string>();
            for (int i = 0; i < devices.Count && names.Count < 4; i++)
            {
                if (!string.IsNullOrEmpty(devices[i].Name)) names.Add(devices[i].Name);
            }
            string text = string.Join(" / ", names.ToArray());
            if (devices.Count > names.Count) text += " 等 " + devices.Count + " 项";
            return text;
        }

        private static List<PnpDevice> QueryPnpDevices()
        {
            List<PnpDevice> result = new List<PnpDevice>();
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT Name,Manufacturer,PNPDeviceID,Status,ConfigManagerErrorCode FROM Win32_PnPEntity"))
                using (ManagementObjectCollection items = searcher.Get())
                {
                    foreach (ManagementObject obj in items)
                    {
                        using (obj)
                        {
                            PnpDevice device = new PnpDevice();
                            device.Name = Text(obj["Name"]);
                            device.Manufacturer = Text(obj["Manufacturer"]);
                            device.DeviceId = Text(obj["PNPDeviceID"]);
                            device.Status = Text(obj["Status"]);
                            device.ErrorCode = ToInt(obj["ConfigManagerErrorCode"]);
                            if (IsRelevant(device)) result.Add(device);
                        }
                    }
                }
            }
            catch
            {
            }
            return result;
        }

        private static bool IsRelevant(PnpDevice device)
        {
            return Contains(device.Name, "Zako")
                || Contains(device.Name, "Steam Streaming")
                || Contains(device.Name, "VB-Audio")
                || Contains(device.Name, "CABLE ")
                || Contains(device.Name, "Sunshine Virtual")
                || Contains(device.Name, "Virtual Gamepad Emulation")
                || Contains(device.DeviceId, "HIDMAESTRO")
                || Contains(device.DeviceId, "USBIP")
                || Contains(device.DeviceId, "VID_1ACE&PID_0002");
        }

        private static Dictionary<string, DriverInfo> QueryDrivers()
        {
            Dictionary<string, DriverInfo> result = new Dictionary<string, DriverInfo>(
                StringComparer.OrdinalIgnoreCase);
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT Name,DisplayName,State,StartMode,PathName FROM Win32_SystemDriver"))
                using (ManagementObjectCollection items = searcher.Get())
                {
                    foreach (ManagementObject obj in items)
                    {
                        using (obj)
                        {
                            string name = Text(obj["Name"]);
                            if (string.IsNullOrEmpty(name)) continue;
                            DriverInfo info = new DriverInfo();
                            info.Name = name;
                            info.DisplayName = Text(obj["DisplayName"]);
                            info.State = Text(obj["State"]);
                            info.StartMode = Text(obj["StartMode"]);
                            info.PathName = Text(obj["PathName"]);
                            result[name] = info;
                        }
                    }
                }
            }
            catch
            {
            }
            return result;
        }

        private static List<PnpDevice> FindAll(List<PnpDevice> devices, Predicate<PnpDevice> predicate)
        {
            List<PnpDevice> result = new List<PnpDevice>();
            for (int i = 0; i < devices.Count; i++)
            {
                if (predicate(devices[i])) result.Add(devices[i]);
            }
            return result;
        }

        private static PnpDevice FindFirst(List<PnpDevice> devices, Predicate<PnpDevice> predicate)
        {
            for (int i = 0; i < devices.Count; i++)
            {
                if (predicate(devices[i])) return devices[i];
            }
            return null;
        }

        private static int CountDevices(List<PnpDevice> devices, Predicate<PnpDevice> predicate)
        {
            int count = 0;
            for (int i = 0; i < devices.Count; i++)
            {
                if (predicate(devices[i])) count++;
            }
            return count;
        }

        private static DriverInfo GetDriver(Dictionary<string, DriverInfo> drivers, string name)
        {
            DriverInfo info;
            return drivers.TryGetValue(name, out info) ? info : null;
        }

        private static bool IsDriverRunning(DriverInfo info)
        {
            return info != null && string.Equals(info.State, "Running", StringComparison.OrdinalIgnoreCase);
        }

        private static string DriverStateText(DriverInfo info)
        {
            if (info == null) return "未安装";
            return (string.IsNullOrEmpty(info.State) ? "未知" : info.State)
                + " / " + (string.IsNullOrEmpty(info.StartMode) ? "未知" : info.StartMode);
        }

        private static string DriverVersion(DriverInfo info)
        {
            if (info == null || string.IsNullOrEmpty(info.PathName)) return "—";
            string path = Environment.ExpandEnvironmentVariables(info.PathName.Trim('"'));
            return GetFileVersion(path);
        }

        private static string GetFileVersion(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return string.Empty;
                FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
                return string.IsNullOrEmpty(info.FileVersion) ? string.Empty : info.FileVersion;
            }
            catch
            {
                return "—";
            }
        }

        private static string ReadJsonString(string path, string key)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return string.Empty;
                string text = File.ReadAllText(path);
                Match match = Regex.Match(text, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"([^\"]*)\"");
                return match.Success ? match.Groups[1].Value : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static int FirstProcessId(string name)
        {
            try
            {
                Process[] processes = Process.GetProcessesByName(name);
                return processes.Length == 0 ? 0 : processes[0].Id;
            }
            catch
            {
                return 0;
            }
        }

        private static bool Contains(string text, string value)
        {
            return !string.IsNullOrEmpty(text)
                && text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string Text(object value)
        {
            return value == null ? string.Empty : value.ToString().Trim();
        }

        private static int ToInt(object value)
        {
            if (value == null) return 0;
            try
            {
                return Convert.ToInt32(value);
            }
            catch
            {
                return 0;
            }
        }

        private static void AddSection(ComponentStatusReport report, string category)
        {
            ComponentStatusItem item = new ComponentStatusItem();
            item.Category = category;
            item.Name = category;
            item.IsSection = true;
            item.StatusText = string.Empty;
            report.Items.Add(item);
        }

        private static void AddItem(ComponentStatusReport report, string category, string name,
            ComponentHealth health, string version, string detail, string deviceId, string key = null)
        {
            ComponentStatusItem item = new ComponentStatusItem();
            item.Category = category;
            item.Name = name;
            item.Health = health;
            item.StatusText = HealthText(health);
            item.Version = version ?? string.Empty;
            item.Detail = detail ?? string.Empty;
            item.DeviceId = deviceId ?? string.Empty;
            item.Key = key ?? string.Empty;
            if (!string.IsNullOrEmpty(item.Key))
            {
                item.CanRepair = ComponentOperationCatalog.CanRepair(item.Key);
                item.CanUninstall = ComponentOperationCatalog.CanUninstall(item.Key);
            }
            report.Items.Add(item);
        }

        private static string HealthText(ComponentHealth health)
        {
            switch (health)
            {
                case ComponentHealth.NotInstalled: return "未安装";
                case ComponentHealth.Installed: return "已安装";
                case ComponentHealth.Ready: return "就绪";
                case ComponentHealth.Running: return "运行中";
                case ComponentHealth.Warning: return "注意";
                case ComponentHealth.Error: return "异常";
                default: return "未知";
            }
        }

        private static void Report(Action<string> progress, string message)
        {
            if (progress != null) progress(message);
        }

        private sealed class PnpDevice
        {
            public string Name;
            public string Manufacturer;
            public string DeviceId;
            public string Status;
            public int ErrorCode;
        }

        private sealed class DriverInfo
        {
            public string Name;
            public string DisplayName;
            public string State;
            public string StartMode;
            public string PathName;
        }

        /// <summary>服务状态的轻量包装，避免 SunshineServiceManager 返回 null 时重复解引用。</summary>
        private sealed class SunnyService
        {
            public SunshineServiceStatus Status;

            public SunnyService(SunshineServiceStatus status)
            {
                Status = status;
            }
        }
    }
}
