using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Zayu.Core
{
    /// <summary>一台已解析出 IP 的 STUN 服务器。</summary>
    public sealed class StunServer
    {
        /// <summary>主机名，例如 stun.l.google.com。</summary>
        public string Host { get; set; }

        /// <summary>端口，例如 19302。</summary>
        public int Port { get; set; }

        /// <summary>解析得到的地址（可能是 IPv4 或 IPv6）。</summary>
        public IPAddress Address { get; set; }

        /// <summary>host:port 文本。</summary>
        public string HostPort
        {
            get { return Host + ":" + Port; }
        }

        /// <summary>形如 “stun.l.google.com:19302 (74.125.250.129)” 的展示文本。</summary>
        public string Label
        {
            get
            {
                string address = Address == null ? "?" : Address.ToString();
                return Host + ":" + Port + " (" + address + ")";
            }
        }

        /// <summary>构造查询用的端点。</summary>
        public IPEndPoint GetEndPoint()
        {
            if (Address == null) return null;
            return new IPEndPoint(Address, Port);
        }
    }

    /// <summary>
    /// 公共 STUN 服务器列表与解析。DNS 失败、服务器不通都属于正常情况，
    /// 这里全部静默忽略，只返回成功解析到的条目。
    /// </summary>
    public static class StunServers
    {
        private const int DefaultPort = 3478;
        private const int DefaultDnsTimeoutMs = 1500;

        // 推荐顺序（本机实测可用性由高到低）。
        private static readonly string[] Ipv4Candidates = new string[]
        {
            "stun.miwifi.com:3478",
            "stun.l.google.com:19302",
            "stun.cloudflare.com:3478",
            "stun.nextcloud.com:3478",
            "stun.voipbuster.com:3478",
            "stun.qq.com:3478"
        };

        // IPv6 需要 AAAA 记录；下列服务器实测均有 AAAA。
        private static readonly string[] Ipv6Candidates = new string[]
        {
            "stun.cloudflare.com:3478",
            "stun.l.google.com:19302",
            "stun.nextcloud.com:3478"
        };

        /// <summary>解析某地址族下按推荐顺序排列、按 IP 去重的服务器列表。</summary>
        public static List<StunServer> Resolve(AddressFamily family)
        {
            return Resolve(family, null, DefaultDnsTimeoutMs);
        }

        /// <summary>解析服务器列表，可通过 log 输出结果摘要。</summary>
        public static List<StunServer> Resolve(AddressFamily family, Action<string> log)
        {
            return Resolve(family, log, DefaultDnsTimeoutMs);
        }

        /// <summary>解析服务器列表，并指定单次 DNS 解析的超时。</summary>
        public static List<StunServer> Resolve(AddressFamily family, Action<string> log, int dnsTimeoutMs)
        {
            string[] candidates = family == AddressFamily.InterNetworkV6 ? Ipv6Candidates : Ipv4Candidates;
            List<StunServer> result = new List<StunServer>();
            HashSet<string> seenIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < candidates.Length; i++)
            {
                StunServer server = ResolveOne(candidates[i], family, dnsTimeoutMs);
                if (server == null || server.Address == null)
                {
                    continue;
                }

                // 按解析出的公网 IP 去重：NAT 映射测试需要不同公网 IP 的服务器。
                string key = server.Address.ToString();
                if (!seenIps.Add(key))
                {
                    continue;
                }
                result.Add(server);
            }

            if (log != null && result.Count > 0)
            {
                log("已解析到 " + result.Count + " 个可用 STUN 服务器地址。");
            }
            return result;
        }

        /// <summary>解析单个 host[:port]，失败返回 null（不抛异常）。</summary>
        public static StunServer ResolveOne(string hostPort)
        {
            return ResolveOne(hostPort, AddressFamily.InterNetwork, DefaultDnsTimeoutMs);
        }

        /// <summary>解析单个 host[:port]，失败返回 null（不抛异常）。</summary>
        public static StunServer ResolveOne(string hostPort, AddressFamily family)
        {
            return ResolveOne(hostPort, family, DefaultDnsTimeoutMs);
        }

        /// <summary>解析单个 host[:port]，失败返回 null（不抛异常）。</summary>
        public static StunServer ResolveOne(string hostPort, AddressFamily family, int dnsTimeoutMs)
        {
            if (string.IsNullOrEmpty(hostPort))
            {
                return null;
            }
            if (dnsTimeoutMs <= 0)
            {
                dnsTimeoutMs = DefaultDnsTimeoutMs;
            }

            try
            {
                string host = hostPort.Trim();
                int port = DefaultPort;

                int separator = host.LastIndexOf(':');
                if (separator > 0)
                {
                    string portText = host.Substring(separator + 1);
                    int parsed;
                    if (int.TryParse(portText, out parsed) && parsed > 0 && parsed <= 65535)
                    {
                        port = parsed;
                        host = host.Substring(0, separator);
                    }
                }
                if (string.IsNullOrEmpty(host))
                {
                    return null;
                }

                IPAddress address = ResolveAddress(host, family, dnsTimeoutMs);
                if (address == null)
                {
                    return null;
                }

                StunServer server = new StunServer();
                server.Host = host;
                server.Port = port;
                server.Address = address;
                return server;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 从已解析列表中挑选不同公网 IP 的服务器；不够时返回实际能拿到的数量。
        /// </summary>
        public static List<StunServer> TakeDistinct(List<StunServer> servers, int count)
        {
            List<StunServer> result = new List<StunServer>();
            if (servers == null || count <= 0)
            {
                return result;
            }

            HashSet<string> seenIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < servers.Count && result.Count < count; i++)
            {
                StunServer server = servers[i];
                if (server == null || server.Address == null)
                {
                    continue;
                }
                if (!seenIps.Add(server.Address.ToString()))
                {
                    continue;
                }
                result.Add(server);
            }
            return result;
        }

        private static IPAddress ResolveAddress(string host, AddressFamily family, int dnsTimeoutMs)
        {
            IPAddress literal;
            if (IPAddress.TryParse(host, out literal))
            {
                return literal.AddressFamily == family ? literal : null;
            }

            Task<IPAddress[]> task = Dns.GetHostAddressesAsync(host);
            try
            {
                if (!task.Wait(dnsTimeoutMs))
                {
                    return null; // DNS 超时，放弃这台服务器。
                }
                if (task.Status != TaskStatus.RanToCompletion)
                {
                    return null;
                }

                IPAddress[] addresses = task.Result;
                if (addresses == null)
                {
                    return null;
                }
                for (int i = 0; i < addresses.Length; i++)
                {
                    IPAddress address = addresses[i];
                    if (address == null || address.AddressFamily != family)
                    {
                        continue;
                    }
                    if (family == AddressFamily.InterNetworkV6 && address.IsIPv6LinkLocal)
                    {
                        continue;
                    }
                    return address;
                }
            }
            catch
            {
                return null;
            }
            return null;
        }
    }
}
