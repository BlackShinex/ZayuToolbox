using System;
using System.Net;
using System.Net.Sockets;

namespace Zayu.Core
{
    /// <summary>
    /// IP 地址隐私遮罩：只遮罩公网 IP，局域网 / 私有 / 链路本地 / ULA 地址保持原样。
    /// 这样本机内网地址可以直接用于排查，公网出口仍默认隐藏。
    /// </summary>
    public static class IpPrivacy
    {
        /// <summary>按地址类型自动遮罩：仅公网 IPv4 / IPv6 打码。</summary>
        public static string Mask(string address)
        {
            if (string.IsNullOrEmpty(address)) return string.Empty;

            string plain = StripScopeId(address);
            IPAddress ip;
            if (!IPAddress.TryParse(plain, out ip)) return address;
            if (IpClassifier.Classify(ip) != IpAddressScope.Public) return address;

            if (ip.AddressFamily == AddressFamily.InterNetworkV6) return MaskIpv6(address);
            return MaskIpv4(address);
        }

        /// <summary>IPv4 遮罩，例如 125.113.211.223 → 125.*.*.223。</summary>
        public static string MaskIpv4(string address)
        {
            if (string.IsNullOrEmpty(address)) return string.Empty;
            string[] parts = address.Split('.');
            if (parts.Length != 4) return address;
            return parts[0] + ".*.*." + parts[3];
        }

        /// <summary>
        /// IPv6 遮罩，例如 2400:3200:1234:5678:9abc:def0:1111:2222
        /// → 2400:****:****:****:****:****:****:2222。
        /// </summary>
        public static string MaskIpv6(string address)
        {
            if (string.IsNullOrEmpty(address)) return string.Empty;

            string plain = address;
            int percent = plain.IndexOf('%');
            if (percent >= 0) plain = plain.Substring(0, percent);

            IPAddress ip;
            if (!IPAddress.TryParse(plain, out ip) || ip.AddressFamily != AddressFamily.InterNetworkV6)
            {
                return address;
            }

            byte[] bytes = ip.GetAddressBytes();
            if (bytes.Length != 16) return address;

            string first = ((bytes[0] << 8) | bytes[1]).ToString("x");
            string last = ((bytes[14] << 8) | bytes[15]).ToString("x");
            return first + ":****:****:****:****:****:****:" + last;
        }

        /// <summary>去掉 IPv6 的 %scope 后缀，用于显示。</summary>
        public static string StripScopeId(string address)
        {
            if (string.IsNullOrEmpty(address)) return string.Empty;
            int percent = address.IndexOf('%');
            return percent >= 0 ? address.Substring(0, percent) : address;
        }
    }
}
