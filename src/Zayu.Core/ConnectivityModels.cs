using System;
using System.Collections.Generic;
using System.Net.Sockets;

namespace Zayu.Core
{
    /// <summary>网络连通性测试场景。</summary>
    public enum ConnectivityTestMode
    {
        Lan = 0,
        Wan = 1
    }

    /// <summary>单项连通性检查状态。</summary>
    public enum ConnectivityCheckState
    {
        /// <summary>尚未开始或尚未收到结果。</summary>
        Pending = 0,
        /// <summary>正在等待结果。</summary>
        Testing,
        /// <summary>连通性正常。</summary>
        Pass,
        /// <summary>能连通，但 P2P 条件不理想。</summary>
        Warn,
        /// <summary>不通。</summary>
        Fail,
        /// <summary>当前网络没有该协议族地址，无法测试。</summary>
        NotApplicable
    }

    /// <summary>连通性测试中收到的请求类型。</summary>
    public enum ConnectivityProbeKind
    {
        Page = 0,
        Ipv4Probe = 1,
        Ipv6Probe = 2
    }

    /// <summary>P2P 结论等级。</summary>
    public enum ConnectivityVerdictLevel
    {
        Unknown = 0,
        Good,
        Warn,
        Bad
    }

    /// <summary>单项连通性检查结果。</summary>
    public sealed class ConnectivityCheckResult
    {
        public string Key { get; set; }
        public string Title { get; set; }
        public ConnectivityCheckState State { get; set; }
        public string Detail { get; set; }
        public string Address { get; set; }
        public long LatencyMilliseconds { get; set; }
        public bool Tested { get; set; }

        public ConnectivityCheckResult()
        {
            Key = string.Empty;
            Title = string.Empty;
            State = ConnectivityCheckState.Pending;
            Detail = string.Empty;
            Address = string.Empty;
        }
    }

    /// <summary>一次网络连通性测试的完整结论。</summary>
    public sealed class ConnectivityTestResult
    {
        public List<ConnectivityCheckResult> Checks { get; private set; }
        public ConnectivityVerdictLevel Level { get; set; }
        public string Verdict { get; set; }
        public string VerdictDetail { get; set; }
        public bool P2pPossible { get; set; }
        public bool Ipv6Direct { get; set; }
        public bool Ipv4P2pLikely { get; set; }
        public bool Complete { get; set; }
        public DateTime Timestamp { get; set; }
        public string Error { get; set; }

        public ConnectivityTestResult()
        {
            Checks = new List<ConnectivityCheckResult>();
            Level = ConnectivityVerdictLevel.Unknown;
            Verdict = string.Empty;
            VerdictDetail = string.Empty;
            Timestamp = DateTime.Now;
            Error = string.Empty;
        }

        public ConnectivityCheckResult Find(string key)
        {
            if (string.IsNullOrEmpty(key) || Checks == null) return null;
            for (int i = 0; i < Checks.Count; i++)
            {
                if (string.Equals(Checks[i].Key, key, StringComparison.Ordinal))
                {
                    return Checks[i];
                }
            }
            return null;
        }
    }

    /// <summary>测试服务器收到连接时的事件参数。</summary>
    public sealed class ConnectivityProbeEventArgs : EventArgs
    {
        public ConnectivityProbeKind Kind { get; set; }

        /// <summary>该连接是否走 IPv6。</summary>
        public bool IsIpv6 { get; set; }

        /// <summary>发起连接的远端地址（不含 %scope）。</summary>
        public string RemoteAddress { get; set; }

        /// <summary>本机接受连接的地址（不含 %scope）。</summary>
        public string LocalAddress { get; set; }

        /// <summary>连接来自局域网页面还是公网页面：lan / wan。</summary>
        public string Mode { get; set; }

        /// <summary>远端地址是否属于公网。</summary>
        public bool IsPublicRemote { get; set; }

        public DateTime Timestamp { get; set; }

        public ConnectivityProbeEventArgs()
        {
            RemoteAddress = string.Empty;
            LocalAddress = string.Empty;
            Mode = string.Empty;
            Timestamp = DateTime.Now;
        }
    }

    /// <summary>测试服务器启动参数。</summary>
    public sealed class ConnectivityTestOptions
    {
        /// <summary>局域网 IPv4 地址；为空表示不监听 IPv4。</summary>
        public string LanIpv4Address { get; set; }

        public int LanIpv4PrefixLength { get; set; }

        /// <summary>局域网 IPv6 地址（公网或唯一本地）；为空表示不监听 IPv6。</summary>
        public string LanIpv6Address { get; set; }

        public int LanIpv6PrefixLength { get; set; }

        /// <summary>公网 IPv6 地址，用于公网二维码；为空表示没有公网 IPv6。</summary>
        public string PublicIpv6Address { get; set; }

        /// <summary>true 监听所有网卡；false 只监听回环地址（自动化测试用）。</summary>
        public bool BindAllInterfaces { get; set; }

        /// <summary>随机路径令牌，避免局域网内其他人猜到测试地址。</summary>
        public string Token { get; set; }

        public ConnectivityTestOptions()
        {
            LanIpv4Address = string.Empty;
            LanIpv6Address = string.Empty;
            PublicIpv6Address = string.Empty;
            Token = string.Empty;
            BindAllInterfaces = true;
        }
    }

    /// <summary>公网 TCP 探测结果，用于判断 IPv4 / IPv6 外网出口是否可用。</summary>
    public sealed class TcpConnectivityResult
    {
        public AddressFamily Family { get; set; }
        public bool Success { get; set; }
        public string Endpoint { get; set; }
        public long LatencyMilliseconds { get; set; }
        public string Error { get; set; }

        public TcpConnectivityResult()
        {
            Endpoint = string.Empty;
            Error = string.Empty;
        }
    }

    /// <summary>测试服务器异常事件参数。</summary>
    public sealed class ConnectivityErrorEventArgs : EventArgs
    {
        public string Message { get; set; }

        public ConnectivityErrorEventArgs(string message)
        {
            Message = message;
        }
    }
}
