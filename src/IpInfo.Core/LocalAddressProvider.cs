using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace IpInfo.Core
{
    /// <summary>
    /// 枚举本机所有可用网卡上的 IPv4 / IPv6 地址。
    /// 主地址选择会优先系统实际选路的物理网卡（例如以太网），
    /// 避免 VMware / Hyper-V / WSL / VPN 等虚拟网卡抢走“主地址”。
    /// </summary>
    public static class LocalAddressProvider
    {
        // GetBestInterface 的目标地址：8.8.8.8（网络字节序 0x08080808）。
        private const uint BestInterfaceDestination = 0x08080808u;

        /// <summary>名称/描述/类型中出现即判定为虚拟网卡的关键字（已转小写）。</summary>
        private static readonly string[] VirtualKeywords = new string[]
        {
            "vmware",
            "virtualbox",
            "vbox",
            "hyper-v",
            "hyperv",
            "vethernet",
            "wsl",
            "docker",
            "vmnet",
            "virtual",
            "虚拟",
            "tap",
            "tun",
            "wireguard",
            "openvpn",
            "tailscale",
            "zerotier",
            "vpn",
            "ppp",
            "tunnel",
            "bluetooth",
            "wi-fi direct",
            "wifi direct",
            "wifidirect",
            "npcap",
            "parallels",
            "qemu",
            "wintun",
            "clash",
            "mihomo"
        };

        /// <summary>已知虚拟化厂商的 MAC OUI（12 位十六进制、无分隔符）。</summary>
        private static readonly string[] VirtualMacPrefixes = new string[]
        {
            "005056", // VMware
            "000C29", // VMware
            "080027", // VirtualBox
            "00155D", // Hyper-V
            "001C42", // Parallels
            "0242"    // Docker / 常见本地管理地址
        };

        public static List<IpAddressEntry> GetIpv4Addresses()
        {
            return GetAddresses(AddressFamily.InterNetwork);
        }

        public static List<IpAddressEntry> GetIpv6Addresses()
        {
            return GetAddresses(AddressFamily.InterNetworkV6);
        }

        public static List<IpAddressEntry> GetAll()
        {
            List<IpAddressEntry> all = new List<IpAddressEntry>();
            all.AddRange(GetIpv4Addresses());
            all.AddRange(GetIpv6Addresses());
            return all;
        }

        /// <summary>
        /// 根据网卡名称、描述、类型和 MAC 判断是否为虚拟网卡（供测试与主地址选择使用）。
        /// 关键字命中、Tunnel/Ppp/Loopback 类型、已知虚拟 OUI 都会判定为虚拟。
        /// 注意：不使用 MAC 本地管理位单独判定，避免把开启“随机硬件地址”的真实 Wi-Fi 误判为虚拟网卡。
        /// </summary>
        public static bool LooksVirtualInterface(string name, string description, string interfaceType, string macAddress)
        {
            string combined = NormalizeText(name) + "|" + NormalizeText(description) + "|"
                + NormalizeText(interfaceType);
            for (int i = 0; i < VirtualKeywords.Length; i++)
            {
                if (combined.IndexOf(VirtualKeywords[i], StringComparison.Ordinal) >= 0)
                {
                    return true;
                }
            }

            string typeName = interfaceType == null ? string.Empty : interfaceType.Trim();
            if (string.Equals(typeName, "Tunnel", StringComparison.OrdinalIgnoreCase)
                || string.Equals(typeName, "Ppp", StringComparison.OrdinalIgnoreCase)
                || string.Equals(typeName, "Loopback", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string mac = NormalizeMac(macAddress);
            if (mac.Length == 12)
            {
                for (int i = 0; i < VirtualMacPrefixes.Length; i++)
                {
                    if (mac.StartsWith(VirtualMacPrefixes[i], StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static List<IpAddressEntry> GetAddresses(AddressFamily family)
        {
            List<IpAddressEntry> result = new List<IpAddressEntry>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            NetworkInterface[] interfaces;
            try
            {
                interfaces = NetworkInterface.GetAllNetworkInterfaces();
            }
            catch
            {
                return result;
            }

            uint bestInterfaceIndex = GetBestInterfaceIndex();

            for (int i = 0; i < interfaces.Length; i++)
            {
                NetworkInterface ni = interfaces[i];
                try
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;

                    NetworkInterfaceType type = ni.NetworkInterfaceType;
                    if (type == NetworkInterfaceType.Loopback) continue;
                    if (type == NetworkInterfaceType.Tunnel) continue; // Teredo / ISATAP 等隧道接口不参与地址列表

                    IPInterfaceProperties props = ni.GetIPProperties();
                    if (props == null) continue;

                    InterfaceMeta meta = BuildInterfaceMeta(ni, type, props);

                    foreach (UnicastIPAddressInformation info in props.UnicastAddresses)
                    {
                        if (info == null || info.Address == null) continue;
                        if (info.Address.AddressFamily != family) continue;
                        if (IsExcluded(info.Address)) continue;

                        IPAddress plain = new IPAddress(info.Address.GetAddressBytes());
                        string plainText = plain.ToString();
                        if (!seen.Add(plainText)) continue;

                        IpAddressScope scope = IpClassifier.Classify(plain);
                        IpAddressEntry entry = new IpAddressEntry();
                        entry.Address = plainText;
                        entry.CopyAddress = info.Address.ToString();
                        entry.Family = family;
                        entry.InterfaceName = ni.Name;
                        entry.InterfaceType = meta.TypeLabel;
                        entry.Scope = scope;
                        entry.ScopeLabel = IpClassifier.GetScopeLabel(scope);
                        entry.IsVirtual = meta.IsVirtual;
                        entry.HasGateway = meta.HasGateway;
                        entry.InterfaceIndex = meta.Index;
                        try
                        {
                            entry.PrefixLength = info.PrefixLength;
                        }
                        catch
                        {
                            entry.PrefixLength = 0;
                        }
                        result.Add(entry);
                    }
                }
                catch
                {
                    // 某些虚拟网卡在读取属性时可能抛出异常，忽略它继续枚举。
                }
            }

            if (result.Count > 0)
            {
                int primaryIndex = SelectPrimaryIndex(result, bestInterfaceIndex);
                if (primaryIndex >= 0)
                {
                    result[primaryIndex].IsPrimary = true;
                }
                result.Sort(CompareEntries);
            }

            return result;
        }

        /// <summary>
        /// 在每个协议族中挑出唯一主地址：
        /// 物理网卡优先（仅当没有物理候选时才回退虚拟网卡）→ 系统选路接口 → 有网关 →
        /// 地址类型权重（公网 &gt; 私有 &gt; CGNAT &gt; 唯一本地）→ 地址文本；
        /// 链路本地 / APIPA 仅在没有任何常规地址时才作为兜底。
        /// </summary>
        private static int SelectPrimaryIndex(List<IpAddressEntry> entries, uint bestInterfaceIndex)
        {
            if (entries.Count == 0)
            {
                return -1;
            }

            // 1) 常规候选（排除链路本地 / APIPA）。
            List<int> pool = new List<int>();
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].Scope != IpAddressScope.LinkLocal)
                {
                    pool.Add(i);
                }
            }
            if (pool.Count == 0)
            {
                // 只有链路本地地址时才回退。
                for (int i = 0; i < entries.Count; i++)
                {
                    pool.Add(i);
                }
            }

            // 2) 只要存在物理网卡候选，就完全不考虑虚拟网卡。
            bool hasPhysical = false;
            for (int i = 0; i < pool.Count; i++)
            {
                if (!entries[pool[i]].IsVirtual)
                {
                    hasPhysical = true;
                    break;
                }
            }

            int best = -1;
            for (int i = 0; i < pool.Count; i++)
            {
                int candidate = pool[i];
                if (hasPhysical && entries[candidate].IsVirtual)
                {
                    continue;
                }
                if (best < 0 || ComparePrimary(entries[candidate], entries[best], bestInterfaceIndex) < 0)
                {
                    best = candidate;
                }
            }
            return best;
        }

        private static int ComparePrimary(IpAddressEntry a, IpAddressEntry b, uint bestInterfaceIndex)
        {
            bool aBest = IsBestInterface(a, bestInterfaceIndex);
            bool bBest = IsBestInterface(b, bestInterfaceIndex);
            if (aBest != bBest)
            {
                return aBest ? -1 : 1;
            }

            if (a.IsVirtual != b.IsVirtual)
            {
                return a.IsVirtual ? 1 : -1;
            }

            if (a.HasGateway != b.HasGateway)
            {
                return a.HasGateway ? -1 : 1;
            }

            int weightA = ScopeWeight(a.Scope);
            int weightB = ScopeWeight(b.Scope);
            if (weightA != weightB)
            {
                return weightA.CompareTo(weightB);
            }

            return string.CompareOrdinal(a.Address, b.Address);
        }

        /// <summary>地址列表排序：主地址第一，物理网卡在虚拟网卡之前。</summary>
        private static int CompareEntries(IpAddressEntry a, IpAddressEntry b)
        {
            if (a.IsPrimary != b.IsPrimary)
            {
                return a.IsPrimary ? -1 : 1;
            }

            if (a.IsVirtual != b.IsVirtual)
            {
                return a.IsVirtual ? 1 : -1;
            }

            if (a.HasGateway != b.HasGateway)
            {
                return a.HasGateway ? -1 : 1;
            }

            int weightA = ScopeWeight(a.Scope);
            int weightB = ScopeWeight(b.Scope);
            if (weightA != weightB)
            {
                return weightA.CompareTo(weightB);
            }

            return string.CompareOrdinal(a.Address, b.Address);
        }

        private static bool IsBestInterface(IpAddressEntry entry, uint bestInterfaceIndex)
        {
            if (bestInterfaceIndex == 0 || entry.InterfaceIndex <= 0)
            {
                return false;
            }
            return (uint)entry.InterfaceIndex == bestInterfaceIndex;
        }

        private static InterfaceMeta BuildInterfaceMeta(NetworkInterface ni, NetworkInterfaceType type,
            IPInterfaceProperties props)
        {
            InterfaceMeta meta = new InterfaceMeta();
            meta.TypeLabel = GetInterfaceTypeLabel(type);

            string macText = string.Empty;
            try
            {
                PhysicalAddress mac = ni.GetPhysicalAddress();
                if (mac != null)
                {
                    macText = mac.ToString();
                }
            }
            catch
            {
            }

            try
            {
                meta.IsVirtual = LooksVirtualInterface(ni.Name, ni.Description, type.ToString(), macText);
            }
            catch
            {
                meta.IsVirtual = false;
            }

            try
            {
                meta.HasGateway = props.GatewayAddresses.Count > 0;
            }
            catch
            {
                meta.HasGateway = false;
            }

            meta.Index = GetInterfaceIndex(props);
            return meta;
        }

        private static int GetInterfaceIndex(IPInterfaceProperties props)
        {
            try
            {
                IPv4InterfaceProperties ipv4 = props.GetIPv4Properties();
                if (ipv4 != null && ipv4.Index > 0)
                {
                    return ipv4.Index;
                }
            }
            catch
            {
            }
            try
            {
                IPv6InterfaceProperties ipv6 = props.GetIPv6Properties();
                if (ipv6 != null && ipv6.Index > 0)
                {
                    return ipv6.Index;
                }
            }
            catch
            {
            }
            return 0;
        }

        /// <summary>
        /// 通过 iphlpapi!GetBestInterface 获取访问 8.8.8.8 时系统实际使用的接口索引。
        /// 失败时返回 0（不影响后续的物理网卡优先策略）。
        /// </summary>
        private static uint GetBestInterfaceIndex()
        {
            try
            {
                uint index;
                int error = GetBestInterface(BestInterfaceDestination, out index);
                if (error == 0)
                {
                    return index;
                }
            }
            catch
            {
                // iphlpapi.dll 不可用时忽略。
            }
            return 0;
        }

        private static bool IsExcluded(IPAddress address)
        {
            if (address.Equals(IPAddress.Any)) return true;
            if (address.Equals(IPAddress.IPv6Any)) return true;
            if (address.Equals(IPAddress.None)) return true;
            if (address.Equals(IPAddress.IPv6None)) return true;
            if (IPAddress.IsLoopback(address)) return true;
            if (address.IsIPv6Multicast) return true;
            if (address.AddressFamily == AddressFamily.InterNetwork && address.GetAddressBytes()[0] >= 224)
            {
                return true; // IPv4 组播 / 保留
            }
            return false;
        }

        private static int ScopeWeight(IpAddressScope scope)
        {
            switch (scope)
            {
                case IpAddressScope.Public: return 0;
                case IpAddressScope.Private: return 1;
                case IpAddressScope.SharedCgnat: return 2;
                case IpAddressScope.UniqueLocal: return 3;
                case IpAddressScope.Reserved: return 4;
                case IpAddressScope.LinkLocal: return 5;
                default: return 6;
            }
        }

        private static string GetInterfaceTypeLabel(NetworkInterfaceType type)
        {
            switch (type)
            {
                case NetworkInterfaceType.Ethernet: return "以太网";
                case NetworkInterfaceType.GigabitEthernet: return "以太网";
                case NetworkInterfaceType.Wireless80211: return "无线网络";
                case NetworkInterfaceType.Ppp: return "拨号网络";
                case NetworkInterfaceType.Tunnel: return "隧道";
                case NetworkInterfaceType.Loopback: return "环回";
                default: return type.ToString();
            }
        }

        private static string NormalizeText(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }
            return value.ToLowerInvariant();
        }

        private static string NormalizeMac(string macAddress)
        {
            if (string.IsNullOrEmpty(macAddress))
            {
                return string.Empty;
            }

            System.Text.StringBuilder builder = new System.Text.StringBuilder(12);
            for (int i = 0; i < macAddress.Length; i++)
            {
                char ch = macAddress[i];
                if ((ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f') || (ch >= 'A' && ch <= 'F'))
                {
                    builder.Append(ch);
                }
            }
            string mac = builder.ToString();
            if (mac.Length < 12)
            {
                return string.Empty;
            }
            if (mac.Length > 12)
            {
                mac = mac.Substring(0, 12);
            }
            return mac.ToUpperInvariant();
        }

        private sealed class InterfaceMeta
        {
            public string TypeLabel;
            public bool IsVirtual;
            public bool HasGateway;
            public int Index;
        }

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern int GetBestInterface(uint dwDestAddr, out uint pdwBestIfIndex);
    }
}
