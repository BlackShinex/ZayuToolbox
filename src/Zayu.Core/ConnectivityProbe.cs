using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Zayu.Core
{
    /// <summary>
    /// 从本机主动连接公共 TCP 端口，判断 IPv4 / IPv6 外网出口是否可用。
    /// 只建立 TCP 连接，不发送业务数据，也不依赖 DNS。
    /// </summary>
    public static class ConnectivityProbe
    {
        private const int ConnectTimeoutMs = 1500;

        private static readonly ProbeTarget[] Ipv4Targets = new ProbeTarget[]
        {
            new ProbeTarget("223.5.5.5", 443, "223.5.5.5:443"),
            new ProbeTarget("119.29.29.29", 443, "119.29.29.29:443"),
            new ProbeTarget("1.1.1.1", 443, "1.1.1.1:443")
        };

        private static readonly ProbeTarget[] Ipv6Targets = new ProbeTarget[]
        {
            new ProbeTarget("2400:3200::1", 443, "[2400:3200::1]:443"),
            new ProbeTarget("2400:3200:baba::1", 443, "[2400:3200:baba::1]:443"),
            new ProbeTarget("2606:4700:4700::1111", 443, "[2606:4700:4700::1111]:443")
        };

        public static TcpConnectivityResult ProbeIpv4(Action<string> progress, CancellationToken cancellationToken)
        {
            return Probe(AddressFamily.InterNetwork, progress, cancellationToken);
        }

        public static TcpConnectivityResult ProbeIpv6(Action<string> progress, CancellationToken cancellationToken)
        {
            return Probe(AddressFamily.InterNetworkV6, progress, cancellationToken);
        }

        public static TcpConnectivityResult Probe(AddressFamily family, Action<string> progress,
            CancellationToken cancellationToken)
        {
            TcpConnectivityResult result = new TcpConnectivityResult();
            result.Family = family;

            ProbeTarget[] targets = family == AddressFamily.InterNetworkV6 ? Ipv6Targets : Ipv4Targets;
            string lastError = string.Empty;
            for (int i = 0; i < targets.Length; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    result.Error = "已取消";
                    return result;
                }

                ProbeTarget target = targets[i];
                if (progress != null)
                {
                    progress("正在测试 " + target.Label + "…");
                }

                string error;
                long latency;
                if (TryConnect(target, cancellationToken, out latency, out error))
                {
                    result.Success = true;
                    result.Endpoint = target.Label;
                    result.LatencyMilliseconds = latency;
                    return result;
                }

                if (!string.IsNullOrEmpty(error))
                {
                    lastError = target.Label + "：" + error;
                }
            }

            result.Error = string.IsNullOrEmpty(lastError) ? "所有测试地址均不可达。" : lastError;
            return result;
        }

        private static bool TryConnect(ProbeTarget target, CancellationToken cancellationToken,
            out long latencyMilliseconds, out string error)
        {
            latencyMilliseconds = 0;
            error = string.Empty;

            IPAddress address;
            if (!IPAddress.TryParse(target.Address, out address))
            {
                error = "地址无效";
                return false;
            }

            Socket socket = null;
            Stopwatch stopwatch = Stopwatch.StartNew();
            try
            {
                socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                IAsyncResult pending = socket.BeginConnect(new IPEndPoint(address, target.Port), null, null);
                if (!pending.AsyncWaitHandle.WaitOne(ConnectTimeoutMs))
                {
                    stopwatch.Stop();
                    error = "连接超时（" + ConnectTimeoutMs + " ms）";
                    return false;
                }

                socket.EndConnect(pending);
                stopwatch.Stop();
                latencyMilliseconds = stopwatch.ElapsedMilliseconds;
                return true;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                error = ex.GetType().Name + " " + ex.Message;
                return false;
            }
            finally
            {
                if (socket != null)
                {
                    try { socket.Close(); }
                    catch { }
                }
            }
        }

        private sealed class ProbeTarget
        {
            public readonly string Address;
            public readonly int Port;
            public readonly string Label;

            public ProbeTarget(string address, int port, string label)
            {
                Address = address;
                Port = port;
                Label = label;
            }
        }
    }
}
