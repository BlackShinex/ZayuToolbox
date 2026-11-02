using System;
using System.Net;
using System.Net.Sockets;

namespace IpInfo.Core
{
    /// <summary>判断 IP 地址属于公网、私有、链路本地等哪一类。</summary>
    public static class IpClassifier
    {
        public static IpAddressScope Classify(IPAddress address)
        {
            if (address == null) return IpAddressScope.Unknown;
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                return ClassifyIpv4(address.GetAddressBytes());
            }
            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                return ClassifyIpv6(address);
            }
            return IpAddressScope.Unknown;
        }

        public static IpAddressScope Classify(string address)
        {
            IPAddress ip;
            if (!IPAddress.TryParse(address, out ip)) return IpAddressScope.Unknown;
            return Classify(ip);
        }

        public static bool IsPublic(IPAddress address)
        {
            return Classify(address) == IpAddressScope.Public;
        }

        public static string GetScopeLabel(IpAddressScope scope)
        {
            switch (scope)
            {
                case IpAddressScope.Public: return "公网";
                case IpAddressScope.Private: return "私有";
                case IpAddressScope.SharedCgnat: return "CGNAT";
                case IpAddressScope.UniqueLocal: return "唯一本地";
                case IpAddressScope.LinkLocal: return "链路本地";
                case IpAddressScope.Loopback: return "环回";
                case IpAddressScope.Multicast: return "组播";
                case IpAddressScope.Reserved: return "保留";
                case IpAddressScope.Unspecified: return "未指定";
                default: return "未知";
            }
        }

        private static IpAddressScope ClassifyIpv4(byte[] b)
        {
            if (b.Length != 4) return IpAddressScope.Unknown;

            // 0.0.0.0 与 127.0.0.0/8
            if (b[0] == 0 && b[1] == 0 && b[2] == 0 && b[3] == 0) return IpAddressScope.Unspecified;
            if (b[0] == 127) return IpAddressScope.Loopback;

            // 10.0.0.0/8
            if (b[0] == 10) return IpAddressScope.Private;

            // 172.16.0.0/12
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return IpAddressScope.Private;

            // 192.168.0.0/16
            if (b[0] == 192 && b[1] == 168) return IpAddressScope.Private;

            // 100.64.0.0/10 运营商级 NAT
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return IpAddressScope.SharedCgnat;

            // 169.254.0.0/16 链路本地
            if (b[0] == 169 && b[1] == 254) return IpAddressScope.LinkLocal;

            // 224.0.0.0/4 组播
            if (b[0] >= 224 && b[0] <= 239) return IpAddressScope.Multicast;

            // 240.0.0.0/4 保留（含 255.255.255.255）
            if (b[0] >= 240) return IpAddressScope.Reserved;

            // 文档 / 测试 / 基准测试等保留网段
            if (b[0] == 192 && b[1] == 0 && b[2] == 2) return IpAddressScope.Reserved;
            if (b[0] == 198 && b[1] == 51 && b[2] == 100) return IpAddressScope.Reserved;
            if (b[0] == 203 && b[1] == 0 && b[2] == 113) return IpAddressScope.Reserved;
            if (b[0] == 198 && (b[1] == 18 || b[1] == 19)) return IpAddressScope.Reserved;
            if (b[0] == 192 && b[1] == 88 && b[2] == 99) return IpAddressScope.Reserved;

            // 0.0.0.0/8 除 0.0.0.0 外均为“本网络”保留地址
            if (b[0] == 0) return IpAddressScope.Reserved;

            return IpAddressScope.Public;
        }

        private static IpAddressScope ClassifyIpv6(IPAddress address)
        {
            if (address.Equals(IPAddress.IPv6Loopback)) return IpAddressScope.Loopback;
            if (address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6None))
            {
                return IpAddressScope.Unspecified;
            }

            byte[] b = address.GetAddressBytes();
            if (b.Length != 16) return IpAddressScope.Unknown;

            // fe80::/10 链路本地
            if (b[0] == 0xFE && (b[1] & 0xC0) == 0x80) return IpAddressScope.LinkLocal;

            // fc00::/7 唯一本地地址（IPv6 私有地址）
            if ((b[0] & 0xFE) == 0xFC) return IpAddressScope.UniqueLocal;

            // ff00::/8 组播
            if (b[0] == 0xFF) return IpAddressScope.Multicast;

            // 2000::/3 全球单播
            if ((b[0] & 0xE0) == 0x20)
            {
                // 2001:db8::/32 文档地址
                if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8)
                {
                    return IpAddressScope.Reserved;
                }
                return IpAddressScope.Public;
            }

            // ::/128、::1/128 已在上面处理，其余为保留 / 过渡地址
            return IpAddressScope.Reserved;
        }
    }
}
