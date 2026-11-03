using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace IpInfo.Core
{
    /// <summary>IP 地址归属类型。</summary>
    public enum IpAddressScope
    {
        Unknown = 0,
        Public,
        Private,
        SharedCgnat,
        UniqueLocal,
        LinkLocal,
        Loopback,
        Multicast,
        Reserved,
        Unspecified
    }

    /// <summary>一块网卡上的一个本机 IP 地址。</summary>
    public sealed class IpAddressEntry
    {
        /// <summary>规范化后的地址文本（IPv6 不带 %scope）。</summary>
        public string Address { get; set; }

        /// <summary>复制到剪贴板时使用的地址文本（链路本地 IPv6 会保留 %scope）。</summary>
        public string CopyAddress { get; set; }

        public AddressFamily Family { get; set; }

        /// <summary>网卡名称，例如“以太网”。</summary>
        public string InterfaceName { get; set; }

        /// <summary>网卡类型的中文描述，例如“以太网 / 无线网络”。</summary>
        public string InterfaceType { get; set; }

        public IpAddressScope Scope { get; set; }

        /// <summary>地址类型的短标签，例如“公网”“私有”。</summary>
        public string ScopeLabel { get; set; }

        /// <summary>同一协议族中是否为主地址（用于 UI 高亮）。</summary>
        public bool IsPrimary { get; set; }

        /// <summary>是否为虚拟网卡（Hyper-V / VMware / VirtualBox / WSL / VPN 等）。</summary>
        public bool IsVirtual { get; set; }

        /// <summary>该网卡是否配置了网关（通常代表能访问外部网络）。</summary>
        public bool HasGateway { get; set; }

        /// <summary>Windows 接口索引，用于匹配系统选路结果。</summary>
        public int InterfaceIndex { get; set; }

        /// <summary>该地址的前缀长度（子网掩码位数）；未知时为 0。</summary>
        public int PrefixLength { get; set; }

        public bool IsPublic
        {
            get { return Scope == IpAddressScope.Public; }
        }
    }

    /// <summary>公网可达性状态。</summary>
    public enum PublicIpStatus
    {
        Unknown = 0,
        /// <summary>本机地址本身就是公网地址（未经过 NAT）。</summary>
        NativePublic,
        /// <summary>本机地址是内网地址，位于 NAT 之后。</summary>
        BehindNat,
        /// <summary>没有可用的公网地址。</summary>
        NotAvailable
    }

    /// <summary>公网出口 IP 检测结果。</summary>
    public sealed class PublicIpInfo
    {
        public string ExternalIpv4 { get; set; }
        public int ExternalIpv4Port { get; set; }
        public string ExternalIpv6 { get; set; }

        public PublicIpStatus Ipv4Status { get; set; }
        public PublicIpStatus Ipv6Status { get; set; }

        /// <summary>是否至少拥有一个公网 IPv6 地址（IPv6 通常直连，不经过 NAT）。</summary>
        public bool HasPublicIpv6 { get; set; }

        /// <summary>面向用户的一句话结论。</summary>
        public string Summary { get; set; }

        /// <summary>检测失败时的错误说明。</summary>
        public string Error { get; set; }

        public PublicIpInfo()
        {
            ExternalIpv4 = string.Empty;
            ExternalIpv6 = string.Empty;
            Ipv4Status = PublicIpStatus.Unknown;
            Ipv6Status = PublicIpStatus.Unknown;
            Summary = string.Empty;
        }
    }

    /// <summary>NAT 类型。</summary>
    public enum NatType
    {
        Unknown = 0,
        /// <summary>没有 NAT，本机直接持有公网 IPv4。</summary>
        NoNat,
        FullCone,
        RestrictedCone,
        PortRestrictedCone,
        /// <summary>端点无关映射的锥型 NAT，但无法进一步区分过滤行为。</summary>
        ConeUnknown,
        /// <summary>对称型 NAT（端点相关映射）。</summary>
        Symmetric
    }

    /// <summary>NAT 映射行为（RFC 4787）。</summary>
    public enum MappingBehavior
    {
        Unknown = 0,
        EndpointIndependent,
        AddressDependent,
        EndpointDependent
    }

    /// <summary>NAT 过滤行为（RFC 4787）。</summary>
    public enum FilteringBehavior
    {
        Unknown = 0,
        EndpointIndependent,
        AddressDependent,
        AddressAndPortDependent
    }

    /// <summary>NAT 类型检测结果。</summary>
    public sealed class NatResult
    {
        public NatType Type { get; set; }

        /// <summary>中文类型名，例如“对称型 NAT（Symmetric）”。</summary>
        public string TypeLabel { get; set; }

        /// <summary>对结果的补充说明。</summary>
        public string Description { get; set; }

        public MappingBehavior Mapping { get; set; }
        public FilteringBehavior Filtering { get; set; }

        /// <summary>NAT 是否保持了本地端口号（映射端口 == 本地端口）。</summary>
        public bool PortPreserved { get; set; }

        public string ExternalIpv4 { get; set; }
        public int ExternalIpv4Port { get; set; }
        public string LocalIpv4 { get; set; }
        public int LocalPort { get; set; }

        /// <summary>实际使用的 STUN 服务器（host:port）。</summary>
        public string ServerUsed { get; set; }

        /// <summary>检测失败时的错误说明。</summary>
        public string Error { get; set; }

        public TimeSpan Elapsed { get; set; }

        /// <summary>检测步骤日志，便于排查问题。</summary>
        public List<string> Steps { get; private set; }

        public NatResult()
        {
            Type = NatType.Unknown;
            TypeLabel = "未知";
            Description = string.Empty;
            ExternalIpv4 = string.Empty;
            LocalIpv4 = string.Empty;
            ServerUsed = string.Empty;
            Error = string.Empty;
            Steps = new List<string>();
        }
    }

    /// <summary>Windows 防火墙当前配置（用于网络连通性 / 入站判断）。</summary>
    public enum FirewallProfile
    {
        Unknown = 0,
        Domain,
        Private,
        Public
    }

    /// <summary>Windows 防火墙状态。</summary>
    public sealed class FirewallInfo
    {
        /// <summary>是否成功读取到防火墙状态。</summary>
        public bool IsSupported { get; set; }

        /// <summary>当前活动配置文件下防火墙是否开启。</summary>
        public bool Enabled { get; set; }

        /// <summary>当前活动配置文件是否默认阻止入站连接。</summary>
        public bool DefaultInboundBlocked { get; set; }

        /// <summary>是否为推断值（缺少管理员权限、只能读取部分状态时）。</summary>
        public bool IsInferred { get; set; }

        public FirewallProfile ActiveProfile { get; set; }

        /// <summary>活动配置文件的中文名，例如“专用网络”。</summary>
        public string ProfileLabel { get; set; }

        /// <summary>读取失败时的错误说明。</summary>
        public string Error { get; set; }

        public FirewallInfo()
        {
            ActiveProfile = FirewallProfile.Unknown;
            ProfileLabel = string.Empty;
            Error = string.Empty;
        }
    }

    /// <summary>一次完整网络探测的结果。</summary>
    public sealed class NetworkReport
    {
        public List<IpAddressEntry> Ipv4Addresses { get; private set; }
        public List<IpAddressEntry> Ipv6Addresses { get; private set; }
        public PublicIpInfo PublicIp { get; set; }
        public NatResult Nat { get; set; }

        /// <summary>Windows 防火墙状态（网络连通性 / 入站判断辅助）。</summary>
        public FirewallInfo Firewall { get; set; }

        /// <summary>探测完成时间。</summary>
        public DateTime Timestamp { get; set; }

        /// <summary>探测总耗时。</summary>
        public TimeSpan Elapsed { get; set; }

        /// <summary>探测过程中的非致命错误。</summary>
        public string Error { get; set; }

        public NetworkReport()
        {
            Ipv4Addresses = new List<IpAddressEntry>();
            Ipv6Addresses = new List<IpAddressEntry>();
            PublicIp = new PublicIpInfo();
            Nat = new NatResult();
            Firewall = new FirewallInfo();
            Timestamp = DateTime.Now;
            Error = string.Empty;
        }

        public IpAddressEntry PrimaryIpv4
        {
            get
            {
                for (int i = 0; i < Ipv4Addresses.Count; i++)
                {
                    if (Ipv4Addresses[i].IsPrimary) return Ipv4Addresses[i];
                }
                return Ipv4Addresses.Count > 0 ? Ipv4Addresses[0] : null;
            }
        }

        public IpAddressEntry PrimaryIpv6
        {
            get
            {
                for (int i = 0; i < Ipv6Addresses.Count; i++)
                {
                    if (Ipv6Addresses[i].IsPrimary) return Ipv6Addresses[i];
                }
                return Ipv6Addresses.Count > 0 ? Ipv6Addresses[0] : null;
            }
        }

        /// <summary>拥有公网 IPv6 地址时返回它，否则返回 null。</summary>
        public string GetPublicIpv6()
        {
            if (!string.IsNullOrEmpty(PublicIp.ExternalIpv6)) return PublicIp.ExternalIpv6;
            for (int i = 0; i < Ipv6Addresses.Count; i++)
            {
                IpAddressEntry entry = Ipv6Addresses[i];
                if (entry.Scope == IpAddressScope.Public && !entry.IsVirtual)
                {
                    return entry.Address;
                }
            }
            for (int i = 0; i < Ipv6Addresses.Count; i++)
            {
                IpAddressEntry entry = Ipv6Addresses[i];
                if (entry.Scope == IpAddressScope.Public) return entry.Address;
            }
            return null;
        }
    }
}
