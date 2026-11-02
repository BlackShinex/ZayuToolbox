using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace IpInfo.Core
{
    /// <summary>IPv6 入站自检客户端事件参数。</summary>
    public sealed class InboundTestClientEventArgs : EventArgs
    {
        /// <summary>发起入站连接的远端地址。</summary>
        public string RemoteEndPoint { get; private set; }

        public InboundTestClientEventArgs(string remoteEndPoint)
        {
            RemoteEndPoint = remoteEndPoint;
        }
    }

    /// <summary>
    /// IPv6 入站自检服务器：在 IPv6 任意地址上临时监听一个 TCP 端口，
    /// 用手机（移动网络）访问打印出的 URL，若收到连接就说明外部入站可达。
    /// IPv6 没有 NAT，是否能入站主要取决于光猫/路由器的 IPv6 防火墙与 Windows 防火墙。
    /// </summary>
    public sealed class Ipv6InboundTestServer : IDisposable
    {
        private const int Ipv6OnlyOption = 27; // IPV6_V6ONLY

        private Socket _listener;
        private Thread _thread;
        private volatile bool _running;

        /// <summary>临时监听的端口。</summary>
        public int Port { get; private set; }

        /// <summary>供外部设备访问的完整 URL，例如 http://[240e:...:1]:52345/。</summary>
        public string Url { get; private set; }

        /// <summary>是否正在等待外部连接。</summary>
        public bool IsRunning
        {
            get { return _running; }
        }

        /// <summary>收到外部入站连接时触发（在后台线程上触发，UI 需自行切回主线程）。</summary>
        public event EventHandler<InboundTestClientEventArgs> ClientConnected;

        /// <summary>处理连接过程中发生错误时触发（在后台线程上触发）。</summary>
        public event EventHandler<InboundTestClientEventArgs> ServerError;

        /// <summary>启动临时监听。</summary>
        /// <param name="publicIpv6Address">本机公网 IPv6 地址，用于拼接 URL。</param>
        public void Start(string publicIpv6Address)
        {
            Start(publicIpv6Address, true);
        }

        /// <summary>启动临时监听。</summary>
        /// <param name="publicIpv6Address">本机公网 IPv6 地址，用于拼接 URL。</param>
        /// <param name="bindAllInterfaces">true 监听所有 IPv6 网卡（供外部设备访问）；false 只监听回环地址（单元测试用）。</param>
        public void Start(string publicIpv6Address, bool bindAllInterfaces)
        {
            if (_running)
            {
                throw new InvalidOperationException("入站自检已经在进行中。");
            }

            IPAddress parsed;
            if (string.IsNullOrEmpty(publicIpv6Address)
                || !IPAddress.TryParse(IpPrivacy.StripScopeId(publicIpv6Address), out parsed)
                || parsed.AddressFamily != AddressFamily.InterNetworkV6)
            {
                throw new ArgumentException("需要一个有效的公网 IPv6 地址。", "publicIpv6Address");
            }

            string addressText = IpPrivacy.StripScopeId(publicIpv6Address);
            Socket listener = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                listener.SetSocketOption(SocketOptionLevel.IPv6, (SocketOptionName)Ipv6OnlyOption, 1);
            }
            catch
            {
                // 部分系统不支持显式设置 IPV6_V6ONLY，忽略即可。
            }

            IPAddress bindAddress = bindAllInterfaces ? IPAddress.IPv6Any : IPAddress.IPv6Loopback;
            listener.Bind(new IPEndPoint(bindAddress, 0));
            listener.Listen(8);

            _listener = listener;
            Port = ((IPEndPoint)listener.LocalEndPoint).Port;
            Url = "http://[" + addressText + "]:" + Port + "/";
            _running = true;

            _thread = new Thread(AcceptLoop);
            _thread.IsBackground = true;
            _thread.Name = "Ipv6InboundTest";
            _thread.Start();
        }

        /// <summary>停止监听并释放套接字。</summary>
        public void Stop()
        {
            _running = false;

            Socket listener = _listener;
            _listener = null;
            if (listener != null)
            {
                try { listener.Close(); }
                catch { }
            }

            Thread thread = _thread;
            _thread = null;
            if (thread != null)
            {
                try { thread.Join(800); }
                catch { }
            }
        }

        public void Dispose()
        {
            Stop();
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                Socket listener = _listener;
                if (listener == null) return;

                Socket client = null;
                try
                {
                    client = listener.Accept();
                }
                catch
                {
                    if (!_running) return;
                    continue;
                }

                try
                {
                    HandleClient(client);
                }
                catch (Exception ex)
                {
                    Raise(ServerError, "处理入站连接失败：" + ex.Message);
                }
                finally
                {
                    if (client != null)
                    {
                        try { client.Close(); }
                        catch { }
                    }
                }
            }
        }

        private void HandleClient(Socket client)
        {
            string remote = client.RemoteEndPoint != null
                ? client.RemoteEndPoint.ToString()
                : "未知地址";

            byte[] response = BuildResponse(remote);
            try
            {
                client.SendTimeout = 3000;
                client.Send(response);
            }
            catch
            {
                // 浏览器可能提前断开，不影响“连接已到达”的判定。
            }

            Raise(ClientConnected, remote);
        }

        private static byte[] BuildResponse(string remote)
        {
            string html =
                "<!doctype html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\">"
                + "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">"
                + "<title>IPv6 入站连接成功</title></head>"
                + "<body style=\"font-family:-apple-system,Segoe UI,Microsoft YaHei,sans-serif;"
                + "background:#0f172a;color:#e2e8f0;padding:28px;line-height:1.7\">"
                + "<h2 style=\"color:#4ade80\">IPv6 入站连接成功</h2>"
                + "<p>来自 <b>" + WebEscape(remote) + "</b> 的连接已经到达本机。</p>"
                + "<p>说明当前网络的 IPv6 入站路径是通的（Windows 防火墙与光猫/路由器均未拦截该端口）。</p>"
                + "<p style=\"color:#94a3b8;font-size:14px\">可以回到 IpInfo 窗口查看检测结果。</p>"
                + "</body></html>";

            byte[] body = Encoding.UTF8.GetBytes(html);
            string header = "HTTP/1.1 200 OK\r\n"
                + "Content-Type: text/html; charset=utf-8\r\n"
                + "Content-Length: " + body.Length + "\r\n"
                + "Cache-Control: no-store\r\n"
                + "Connection: close\r\n\r\n";
            byte[] headerBytes = Encoding.ASCII.GetBytes(header);

            byte[] response = new byte[headerBytes.Length + body.Length];
            Buffer.BlockCopy(headerBytes, 0, response, 0, headerBytes.Length);
            Buffer.BlockCopy(body, 0, response, headerBytes.Length, body.Length);
            return response;
        }

        private static string WebEscape(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }

        private void Raise(EventHandler<InboundTestClientEventArgs> handler, string remote)
        {
            if (handler == null) return;
            try
            {
                handler(this, new InboundTestClientEventArgs(remote));
            }
            catch
            {
                // 事件处理器异常不应影响监听线程。
            }
        }
    }
}
