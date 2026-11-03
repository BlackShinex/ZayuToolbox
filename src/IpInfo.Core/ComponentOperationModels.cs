using System;

namespace IpInfo.Core
{
    public enum ComponentOperationKind
    {
        Repair = 0,
        Uninstall = 1
    }

    public sealed class ComponentOperationProgress
    {
        public string Stage { get; set; }
        public int Percent { get; set; }
        public bool IsIndeterminate { get; set; }
        public long BytesReceived { get; set; }
        public long TotalBytes { get; set; }
        public string SpeedText { get; set; }
        public string EtaText { get; set; }

        public ComponentOperationProgress()
        {
            Stage = string.Empty;
            Percent = 0;
            SpeedText = string.Empty;
            EtaText = string.Empty;
        }
    }

    public sealed class ComponentOperationResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public bool RebootRequired { get; set; }

        public ComponentOperationResult()
        {
            Message = string.Empty;
        }
    }

    /// <summary>支持修复 / 卸载的组件白名单及显示信息。</summary>
    public static class ComponentOperationCatalog
    {
        public const string ZakoDisplay = "zako-display";
        public const string ZakoMouse = "zako-mouse";
        public const string DualSense = "dualsense";
        public const string SteamSpeakers = "steam-speakers";
        public const string VbCable = "vb-cable";
        public const string SunshineMicrophone = "sunshine-mic";
        public const string UsbIp = "usbip";

        public static bool CanRepair(string key)
        {
            return key == ZakoDisplay || key == ZakoMouse || key == DualSense
                || key == SteamSpeakers || key == VbCable
                || key == SunshineMicrophone || key == UsbIp;
        }

        public static bool CanUninstall(string key)
        {
            return key == ZakoDisplay || key == ZakoMouse || key == DualSense;
        }

        public static bool NeedsLocalInstaller(string key)
        {
            return key == VbCable;
        }

        public static string DisplayName(string key)
        {
            switch (key)
            {
                case ZakoDisplay: return "ZakoDisplay 虚拟显示器";
                case ZakoMouse: return "Zako 虚拟鼠标";
                case DualSense: return "DualSense / HIDMaestro 虚拟手柄";
                case SteamSpeakers: return "Steam Streaming Speakers";
                case VbCable: return "VB-CABLE";
                case SunshineMicrophone: return "Sunshine Virtual Microphone";
                case UsbIp: return "USB/IP 透传驱动";
                default: return "组件";
            }
        }

        public static string UninstallImpact(string key)
        {
            switch (key)
            {
                case ZakoDisplay:
                    return "会移除 ZakoDisplay 虚拟显示器设备和驱动包，当前显示布局可能变化，必要时需要重启。";
                case ZakoMouse:
                    return "会移除 Zako 虚拟鼠标设备和驱动包，远程鼠标功能会暂时不可用。";
                case DualSense:
                    return "会移除 DualSense 组件包；HIDMaestro 与 USB/IP 系统驱动会保留，不影响其他组件。";
                default:
                    return "该操作会修改系统组件。";
            }
        }

        public static string RepairImpact(string key)
        {
            switch (key)
            {
                case ZakoDisplay:
                case ZakoMouse:
                    return "会使用基地版 tools 目录中的 INF 重新安装驱动，过程中会请求管理员权限。";
                case DualSense:
                    return "会从基地版官方 Release 重新下载组件包，校验 SHA-256 后覆盖安装到工具目录。";
                case SteamSpeakers:
                    return "会从 Steam 自带的 Windows10 音频驱动重新安装 Steam Streaming Speakers，过程中会请求管理员权限。";
                case VbCable:
                    return "需要选择 VB-Audio 官方安装包；校验数字签名后会启动官方安装程序。";
                case SunshineMicrophone:
                    return "会检查并修复 DualSense 组件与 USB/IP 驱动依赖；虚拟麦克风会随串流设备按需创建。";
                case UsbIp:
                    return "会从 usbip-win2 官方 Release 下载 0.9.7.7 安装程序，校验 SHA-256 后启动安装。";
                default:
                    return "该操作会修复组件文件。";
            }
        }

        public static string OperationTitle(ComponentOperationKind kind)
        {
            return kind == ComponentOperationKind.Repair ? "修复组件" : "卸载组件";
        }
    }
}
