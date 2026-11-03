using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace Zayu.Core
{
    /// <summary>Sunshine 服务的运行状态。</summary>
    public enum SunshineServiceState
    {
        Unknown = 0,
        NotFound,
        Stopped,
        Running,
        StartPending,
        StopPending,
        Paused,
        Other
    }

    /// <summary>服务启动类型。</summary>
    public enum SunshineStartMode
    {
        Unknown = 0,
        Boot,
        System,
        Automatic,
        Manual,
        Disabled
    }

    /// <summary>一次服务状态检测的结果。</summary>
    public sealed class SunshineServiceStatus
    {
        public bool IsFound { get; set; }

        public string ServiceName { get; set; }
        public string DisplayName { get; set; }

        public SunshineServiceState State { get; set; }
        public SunshineStartMode StartMode { get; set; }

        /// <summary>服务二进制路径（已去掉引号和参数）。</summary>
        public string ImagePath { get; set; }

        /// <summary>运行账户，例如 LocalSystem。</summary>
        public string StartName { get; set; }

        public int ProcessId { get; set; }

        /// <summary>找到多个候选服务时记录全部服务名。</summary>
        public List<string> Candidates { get; set; }

        public string Error { get; set; }
        public List<string> Warnings { get; set; }
        public DateTime DetectedAt { get; set; }

        public SunshineServiceStatus()
        {
            ServiceName = string.Empty;
            DisplayName = string.Empty;
            ImagePath = string.Empty;
            StartName = string.Empty;
            Candidates = new List<string>();
            Error = string.Empty;
            Warnings = new List<string>();
            DetectedAt = DateTime.Now;
        }

        public bool IsRunning
        {
            get { return State == SunshineServiceState.Running; }
        }

        public string StateLabel
        {
            get
            {
                switch (State)
                {
                    case SunshineServiceState.Running: return "运行中";
                    case SunshineServiceState.Stopped: return "已停止";
                    case SunshineServiceState.StartPending: return "正在启动";
                    case SunshineServiceState.StopPending: return "正在停止";
                    case SunshineServiceState.Paused: return "已暂停";
                    case SunshineServiceState.NotFound: return "未安装";
                    case SunshineServiceState.Other: return "状态异常";
                    default: return "未知";
                }
            }
        }

        public string StartModeLabel
        {
            get { return SunshineServiceManager.StartModeText(StartMode); }
        }

        /// <summary>是否处于开机自启（自动）状态。</summary>
        public bool IsAutoStart
        {
            get { return StartMode == SunshineStartMode.Automatic; }
        }

        public string Summary
        {
            get
            {
                if (!IsFound) return "未检测到 Sunshine 服务";
                return StateLabel + " · " + StartModeLabel;
            }
        }

        public string ToDiagnostics()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine("杂鱼串流工具箱 · Sunshine 服务状态");
            sb.AppendLine("检测结果：" + Summary);
            sb.AppendLine("服务名：" + NullSafe(ServiceName));
            if (!string.IsNullOrEmpty(DisplayName) && !string.Equals(DisplayName, ServiceName, StringComparison.Ordinal))
                sb.AppendLine("显示名：" + DisplayName);
            sb.AppendLine("启动类型：" + StartModeLabel);
            sb.AppendLine("可执行文件：" + NullSafe(ImagePath));
            sb.AppendLine("运行账户：" + NullSafe(StartName));
            if (ProcessId > 0) sb.AppendLine("进程 PID：" + ProcessId);
            if (Candidates.Count > 1)
                sb.AppendLine("其他候选服务：" + string.Join("、", Candidates.ToArray()));
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

    /// <summary>修改服务启动类型的结果。</summary>
    public sealed class SunshineServiceChangeResult
    {
        public bool Success { get; set; }

        /// <summary>权限不足，需要以管理员身份重试。</summary>
        public bool NeedElevation { get; set; }

        public SunshineStartMode NewStartMode { get; set; }
        public string Error { get; set; }

        public SunshineServiceChangeResult()
        {
            NewStartMode = SunshineStartMode.Unknown;
            Error = string.Empty;
        }
    }

    /// <summary>
    /// 只读查询 Sunshine 服务状态；修改启动类型需要管理员权限，调用方负责提权与确认。
    /// </summary>
    public static class SunshineServiceManager
    {
        public const string DefaultServiceName = "SunshineService";

        private const uint ScManagerConnect = 0x0001;
        private const uint ServiceQueryStatus = 0x0004;
        private const uint ServiceChangeConfig = 0x0002;
        private const uint ServiceNoChange = 0xFFFFFFFF;
        private const int ScStatusProcessInfo = 0;

        /// <summary>按服务名找，找不到再按二进制路径或服务名里的 sunshine 兜底。</summary>
        public static SunshineServiceStatus Detect()
        {
            SunshineServiceStatus status = new SunshineServiceStatus();

            try
            {
                string serviceName = FindServiceName(status);
                if (string.IsNullOrEmpty(serviceName))
                {
                    status.State = SunshineServiceState.NotFound;
                    return status;
                }

                status.IsFound = true;
                status.ServiceName = serviceName;
                ReadServiceConfiguration(serviceName, status);
                ReadServiceState(serviceName, status);
            }
            catch (Exception ex)
            {
                status.Error = "读取服务状态失败：" + ex.Message;
            }

            return status;
        }

        /// <summary>把服务启动类型设为指定值。调用方需确保进程已提权。</summary>
        public static SunshineServiceChangeResult SetStartMode(SunshineStartMode mode)
        {
            SunshineServiceChangeResult result = new SunshineServiceChangeResult();
            result.NewStartMode = mode;

            try
            {
                string serviceName = FindServiceName(new SunshineServiceStatus());
                if (string.IsNullOrEmpty(serviceName))
                {
                    result.Error = "没有找到 Sunshine 服务，无法修改启动类型。";
                    return result;
                }

                uint startType;
                if (!TryGetStartType(mode, out startType))
                {
                    result.Error = "不支持的启动类型：" + mode;
                    return result;
                }

                IntPtr manager = OpenSCManager(null, null, ScManagerConnect);
                if (manager == IntPtr.Zero)
                {
                    result.Error = "无法连接服务管理器（错误码 " + Marshal.GetLastWin32Error() + "）。";
                    result.NeedElevation = true;
                    return result;
                }

                try
                {
                    IntPtr service = OpenService(manager, serviceName, ServiceChangeConfig);
                    if (service == IntPtr.Zero)
                    {
                        int error = Marshal.GetLastWin32Error();
                        result.NeedElevation = error == 5;
                        result.Error = error == 5
                            ? "权限不足，需要管理员权限。"
                            : "无法打开服务 " + serviceName + "（错误码 " + error + "）。";
                        return result;
                    }

                    try
                    {
                        bool changed = ChangeServiceConfig(service, ServiceNoChange, startType, ServiceNoChange,
                            null, null, IntPtr.Zero, null, null, null, null);
                        if (changed)
                        {
                            result.Success = true;
                            return result;
                        }

                        int error = Marshal.GetLastWin32Error();
                        result.NeedElevation = error == 5;
                        result.Error = error == 5
                            ? "权限不足，需要管理员权限。"
                            : "修改服务启动类型失败（错误码 " + error + "）。";
                    }
                    finally
                    {
                        CloseServiceHandle(service);
                    }
                }
                finally
                {
                    CloseServiceHandle(manager);
                }
            }
            catch (Exception ex)
            {
                result.Error = "修改服务启动类型失败：" + ex.Message;
            }

            return result;
        }

        /// <summary>解析命令行里的启动类型，例如 auto / automatic / manual / disabled。</summary>
        public static bool TryParseStartMode(string text, out SunshineStartMode mode)
        {
            mode = SunshineStartMode.Unknown;
            if (string.IsNullOrEmpty(text)) return false;

            string value = text.Trim().ToLowerInvariant();
            if (value == "auto" || value == "automatic" || value == "自动")
            {
                mode = SunshineStartMode.Automatic;
                return true;
            }
            if (value == "manual" || value == "手动")
            {
                mode = SunshineStartMode.Manual;
                return true;
            }
            if (value == "disabled" || value == "disable" || value == "禁用")
            {
                mode = SunshineStartMode.Disabled;
                return true;
            }
            return false;
        }

        public static string StartModeText(SunshineStartMode mode)
        {
            switch (mode)
            {
                case SunshineStartMode.Boot: return "系统启动（boot）";
                case SunshineStartMode.System: return "系统启动";
                case SunshineStartMode.Automatic: return "自动";
                case SunshineStartMode.Manual: return "手动";
                case SunshineStartMode.Disabled: return "已禁用";
                default: return "未知";
            }
        }

        /// <summary>当前进程是否具备管理员权限。</summary>
        public static bool IsElevated()
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

        private static string FindServiceName(SunshineServiceStatus status)
        {
            if (ServiceExists(DefaultServiceName)) return DefaultServiceName;

            List<string> candidates = new List<string>();
            try
            {
                using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (RegistryKey services = baseKey.OpenSubKey(@"SYSTEM\CurrentControlSet\Services"))
                {
                    if (services == null) return string.Empty;

                    foreach (string name in services.GetSubKeyNames())
                    {
                        try
                        {
                            using (RegistryKey key = services.OpenSubKey(name))
                            {
                                if (key == null) continue;
                                string imagePath = key.GetValue("ImagePath") as string;
                                string displayName = key.GetValue("DisplayName") as string;

                                bool looksLikeSunshine = ContainsIgnoreCase(name, "sunshine")
                                    || ContainsIgnoreCase(imagePath, "sunshine")
                                    || ContainsIgnoreCase(displayName, "sunshine");

                                if (looksLikeSunshine) candidates.Add(name);
                            }
                        }
                        catch
                        {
                        }
                    }
                }
            }
            catch
            {
            }

            if (candidates.Count == 0) return string.Empty;

            if (status != null)
            {
                for (int i = 0; i < candidates.Count; i++) status.Candidates.Add(candidates[i]);
                if (candidates.Count > 1)
                {
                    status.Warnings.Add("检测到多个候选服务，已使用第一个：" + candidates[0]);
                }
            }
            return candidates[0];
        }

        private static bool ServiceExists(string name)
        {
            IntPtr manager = OpenSCManager(null, null, ScManagerConnect);
            if (manager == IntPtr.Zero) return false;

            try
            {
                IntPtr service = OpenService(manager, name, ServiceQueryStatus);
                if (service == IntPtr.Zero) return false;
                CloseServiceHandle(service);
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                CloseServiceHandle(manager);
            }
        }

        private static void ReadServiceConfiguration(string serviceName, SunshineServiceStatus status)
        {
            try
            {
                using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (RegistryKey key = baseKey.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + serviceName))
                {
                    if (key == null) return;

                    status.DisplayName = (key.GetValue("DisplayName") as string) ?? serviceName;
                    status.ImagePath = ExtractExecutablePath(key.GetValue("ImagePath") as string);
                    status.StartName = (key.GetValue("ObjectName") as string) ?? string.Empty;

                    object start = key.GetValue("Start");
                    if (start is int) status.StartMode = ConvertStartMode((int)start);
                }
            }
            catch (Exception ex)
            {
                status.Warnings.Add("读取服务配置失败：" + ex.Message);
            }
        }

        private static void ReadServiceState(string serviceName, SunshineServiceStatus status)
        {
            IntPtr manager = OpenSCManager(null, null, ScManagerConnect);
            if (manager == IntPtr.Zero)
            {
                status.Warnings.Add("无法连接服务管理器，只能根据注册表判断（错误码 " + Marshal.GetLastWin32Error() + "）。");
                return;
            }

            try
            {
                IntPtr service = OpenService(manager, serviceName, ServiceQueryStatus);
                if (service == IntPtr.Zero)
                {
                    status.Warnings.Add("无法打开服务（错误码 " + Marshal.GetLastWin32Error() + "）。");
                    return;
                }

                try
                {
                    int size = Marshal.SizeOf(typeof(ServiceStatusProcess));
                    IntPtr buffer = Marshal.AllocHGlobal(size);
                    try
                    {
                        int needed;
                        if (!QueryServiceStatusEx(service, ScStatusProcessInfo, buffer, size, out needed))
                        {
                            status.Warnings.Add("查询服务状态失败（错误码 " + Marshal.GetLastWin32Error() + "）。");
                            return;
                        }

                        ServiceStatusProcess info = (ServiceStatusProcess)Marshal.PtrToStructure(buffer, typeof(ServiceStatusProcess));
                        status.State = ConvertState(info.CurrentState);
                        status.ProcessId = (int)info.ProcessId;
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }
                }
                finally
                {
                    CloseServiceHandle(service);
                }
            }
            finally
            {
                CloseServiceHandle(manager);
            }
        }

        private static SunshineServiceState ConvertState(uint state)
        {
            switch (state)
            {
                case 1: return SunshineServiceState.Stopped;
                case 2: return SunshineServiceState.StartPending;
                case 3: return SunshineServiceState.StopPending;
                case 4: return SunshineServiceState.Running;
                case 7: return SunshineServiceState.Paused;
                default: return SunshineServiceState.Other;
            }
        }

        private static SunshineStartMode ConvertStartMode(int start)
        {
            switch (start)
            {
                case 0: return SunshineStartMode.Boot;
                case 1: return SunshineStartMode.System;
                case 2: return SunshineStartMode.Automatic;
                case 3: return SunshineStartMode.Manual;
                case 4: return SunshineStartMode.Disabled;
                default: return SunshineStartMode.Unknown;
            }
        }

        private static bool TryGetStartType(SunshineStartMode mode, out uint startType)
        {
            switch (mode)
            {
                case SunshineStartMode.Automatic: startType = 2; return true;
                case SunshineStartMode.Manual: startType = 3; return true;
                case SunshineStartMode.Disabled: startType = 4; return true;
                default: startType = 0; return false;
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

        private static bool ContainsIgnoreCase(string value, string token)
        {
            return !string.IsNullOrEmpty(value)
                && value.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ServiceStatusProcess
        {
            public uint ServiceType;
            public uint CurrentState;
            public uint ControlsAccepted;
            public uint Win32ExitCode;
            public uint ServiceSpecificExitCode;
            public uint CheckPoint;
            public uint WaitHint;
            public uint ProcessId;
            public uint ServiceFlags;
        }

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr OpenSCManager(string machineName, string databaseName, uint access);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr OpenService(IntPtr scManager, string serviceName, uint access);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool QueryServiceStatusEx(
            IntPtr service, int infoLevel, IntPtr buffer, int bufferSize, out int bytesNeeded);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool ChangeServiceConfig(
            IntPtr service, uint serviceType, uint startType, uint errorControl,
            string binaryPathName, string loadOrderGroup, IntPtr tagId, string dependencies,
            string serviceStartName, string password, string displayName);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool CloseServiceHandle(IntPtr handle);
    }
}
