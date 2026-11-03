using System;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Zayu.Core
{
    /// <summary>
    /// 通过 Windows 防火墙 COM 自动化接口（ProgID: HNetCfg.FwPolicy2）读取
    /// 当前活动配置文件的开关与默认入站策略，用于网络连通性 / 入站判断。
    /// 无权限 / COM 不可用时返回 IsSupported=false + Error，绝不抛出异常。
    /// </summary>
    public static class WindowsFirewallReader
    {
        // NET_FW_PROFILE_TYPE2
        private const int ProfileTypeDomain = 1;
        private const int ProfileTypePrivate = 2;
        private const int ProfileTypePublic = 4;

        private const int ErrorAccessDenied = unchecked((int)0x80070005);

        /// <summary>读取当前 Windows 防火墙状态。</summary>
        public static FirewallInfo Read()
        {
            FirewallInfo info = new FirewallInfo();
            object policy = null;

            try
            {
                Type policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
                if (policyType == null)
                {
                    info.IsSupported = false;
                    info.Error = "未找到 HNetCfg.FwPolicy2 COM 组件（Windows 防火墙服务可能未运行）。";
                    return info;
                }

                policy = Activator.CreateInstance(policyType);
                if (policy == null)
                {
                    info.IsSupported = false;
                    info.Error = "无法创建 HNetCfg.FwPolicy2 COM 对象。";
                    return info;
                }

                int profileTypes = ToInt(GetProperty(policyType, policy, "CurrentProfileTypes", null));
                FirewallProfile active = PickProfile(profileTypes);
                info.ActiveProfile = active;
                info.ProfileLabel = GetProfileLabel(active);

                int profileMask = ProfileMask(active);
                if (profileMask == 0)
                {
                    // 拿不到活动配置文件时，退化为查询全部配置的并集。
                    profileMask = profileTypes != 0
                        ? profileTypes
                        : (ProfileTypeDomain | ProfileTypePrivate | ProfileTypePublic);
                }

                info.Enabled = ToBool(GetProperty(policyType, policy, "FirewallEnabled",
                    new object[] { profileMask }));
                int inboundAction = ToInt(GetProperty(policyType, policy, "DefaultInboundAction",
                    new object[] { profileMask }));
                info.DefaultInboundBlocked = inboundAction == 0; // 0 = 阻止，1 = 允许

                info.IsSupported = true;
                info.Error = string.Empty;
                return info;
            }
            catch (Exception ex)
            {
                // COM 读取常因普通用户权限被拒绝；尝试用注册表读取防火墙开关作为降级。
                FirewallInfo fallback = TryReadFromRegistry(ex);
                if (fallback != null)
                {
                    return fallback;
                }

                info.IsSupported = false;
                info.Error = DescribeError(ex);
                return info;
            }
            finally
            {
                ReleaseComObject(policy);
            }
        }

        /// <summary>
        /// 权限不足时的降级方案：从注册表读取三个防火墙配置文件的 EnableFirewall 开关。
        /// 配置文件与默认入站策略无法读取，按 Windows 默认值（启用则阻止入站）推断。
        /// </summary>
        private static FirewallInfo TryReadFromRegistry(Exception original)
        {
            string[] profiles = new string[] { "DomainProfile", "StandardProfile", "PublicProfile" };
            bool anyRead = false;
            bool anyEnabled = false;

            try
            {
                using (RegistryKey policyKey = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy"))
                {
                    if (policyKey == null) return null;

                    for (int i = 0; i < profiles.Length; i++)
                    {
                        using (RegistryKey profileKey = policyKey.OpenSubKey(profiles[i]))
                        {
                            if (profileKey == null) continue;
                            object value = profileKey.GetValue("EnableFirewall");
                            if (value == null) continue;

                            anyRead = true;
                            try
                            {
                                int enabled = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                                if (enabled != 0) anyEnabled = true;
                            }
                            catch
                            {
                            }
                        }
                    }
                }
            }
            catch
            {
                return null;
            }

            if (!anyRead) return null;

            FirewallInfo info = new FirewallInfo();
            info.IsSupported = true;
            info.IsInferred = true;
            info.Enabled = anyEnabled;
            info.DefaultInboundBlocked = anyEnabled; // Windows 防火墙启用时默认阻止入站。
            info.ActiveProfile = FirewallProfile.Unknown;
            info.ProfileLabel = "配置文件未知";
            info.Error = "权限不足，已从注册表读取防火墙开关；配置文件与默认入站动作按 Windows 默认值推断。";
            return info;
        }

        private static object GetProperty(Type type, object instance, string name, object[] args)
        {
            return type.InvokeMember(name, BindingFlags.GetProperty, null, instance, args);
        }

        private static FirewallProfile PickProfile(int profileTypes)
        {
            if ((profileTypes & ProfileTypeDomain) != 0)
            {
                return FirewallProfile.Domain;
            }
            if ((profileTypes & ProfileTypePrivate) != 0)
            {
                return FirewallProfile.Private;
            }
            if ((profileTypes & ProfileTypePublic) != 0)
            {
                return FirewallProfile.Public;
            }
            return FirewallProfile.Unknown;
        }

        private static int ProfileMask(FirewallProfile profile)
        {
            switch (profile)
            {
                case FirewallProfile.Domain: return ProfileTypeDomain;
                case FirewallProfile.Private: return ProfileTypePrivate;
                case FirewallProfile.Public: return ProfileTypePublic;
                default: return 0;
            }
        }

        private static string GetProfileLabel(FirewallProfile profile)
        {
            switch (profile)
            {
                case FirewallProfile.Domain: return "域网络";
                case FirewallProfile.Private: return "专用网络";
                case FirewallProfile.Public: return "公用网络";
                default: return string.Empty;
            }
        }

        private static int ToInt(object value)
        {
            if (value == null)
            {
                return 0;
            }
            try
            {
                return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return 0;
            }
        }

        private static bool ToBool(object value)
        {
            if (value == null)
            {
                return false;
            }
            try
            {
                return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return false;
            }
        }

        private static string DescribeError(Exception ex)
        {
            Exception actual = ex;
            while (actual.InnerException != null)
            {
                actual = actual.InnerException;
            }

            int errorCode = 0;
            try
            {
                errorCode = Marshal.GetHRForException(actual);
            }
            catch
            {
            }

            if (actual is UnauthorizedAccessException || errorCode == ErrorAccessDenied)
            {
                return "读取 Windows 防火墙状态失败：拒绝访问（0x80070005），当前进程权限不足或 COM 访问被系统拒绝；"
                    + "请以管理员身份运行程序，或通过“Windows 安全中心”查看防火墙状态。";
            }

            return "读取 Windows 防火墙状态失败：" + actual.GetType().Name + "：" + actual.Message;
        }

        private static void ReleaseComObject(object comObject)
        {
            if (comObject == null)
            {
                return;
            }
            try
            {
                if (Marshal.IsComObject(comObject))
                {
                    Marshal.FinalReleaseComObject(comObject);
                }
            }
            catch
            {
                // 释放失败不影响结果。
            }
        }
    }
}
