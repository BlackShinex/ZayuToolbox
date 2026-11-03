using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace IpInfo.Core
{
    /// <summary>
    /// 网络连通性测试服务器：同时（或按可用地址）在 IPv4 / IPv6 上监听临时 TCP 端口，
    /// 提供一个手机可打开的网页。网页会用隐藏图片分别探测 IPv4 和 IPv6，
    /// 服务器记录每个协议族的请求来源，从而判断内网 / 公网连通性。
    ///
    /// 同一局域网模式：二维码指向电脑的局域网地址，页面同时探测内网 IPv4 和 IPv6。
    /// 公网模式：二维码指向公网 IPv6，页面探测公网 IPv6 入站。
    /// </summary>
    public sealed class ConnectivityTestServer : IDisposable
    {
        private const int Ipv6OnlyOption = 27; // IPV6_V6ONLY
        private const int MaxRequestBytes = 4096;

        private Socket _ipv4Listener;
        private Socket _ipv6Listener;
        private Thread _ipv4Thread;
        private Thread _ipv6Thread;
        private volatile bool _running;
        private string _token;

        /// <summary>临时监听的端口。</summary>
        public int Port { get; private set; }

        /// <summary>局域网 IPv4 测试地址；没有 IPv4 时为 null。</summary>
        public string LanIpv4Url { get; private set; }

        /// <summary>局域网 IPv6 测试地址；没有 IPv6 时为 null。</summary>
        public string LanIpv6Url { get; private set; }

        /// <summary>局域网模式首选的测试地址（优先 IPv4）。</summary>
        public string LanUrl { get; private set; }

        /// <summary>公网 IPv6 测试地址；没有公网 IPv6 时为 null。</summary>
        public string WanUrl { get; private set; }

        /// <summary>是否正在等待手机连接。</summary>
        public bool IsRunning
        {
            get { return _running; }
        }

        /// <summary>收到网页或探测请求时触发（后台线程）。</summary>
        public event EventHandler<ConnectivityProbeEventArgs> ProbeReceived;

        /// <summary>处理连接失败时触发（后台线程）。</summary>
        public event EventHandler<ConnectivityErrorEventArgs> ServerError;

        /// <summary>启动临时监听。</summary>
        public void Start(ConnectivityTestOptions options)
        {
            if (_running)
            {
                throw new InvalidOperationException("连通性测试已经在进行中。");
            }
            if (options == null)
            {
                throw new ArgumentNullException("options");
            }

            bool needIpv4 = !string.IsNullOrEmpty(options.LanIpv4Address);
            bool needIpv6 = !string.IsNullOrEmpty(options.LanIpv6Address)
                || !string.IsNullOrEmpty(options.PublicIpv6Address);
            if (!needIpv4 && !needIpv6)
            {
                throw new ArgumentException("至少需要一个 IPv4 或 IPv6 地址。", "options");
            }

            _token = string.IsNullOrEmpty(options.Token) ? CreateToken() : options.Token;

            Socket ipv4 = null;
            Socket ipv6 = null;
            int port = 0;

            for (int attempt = 0; attempt < 6; attempt++)
            {
                try
                {
                    if (needIpv4)
                    {
                        ipv4 = CreateTcpSocket(AddressFamily.InterNetwork);
                        IPAddress bindAddress = options.BindAllInterfaces
                            ? ParseAddress(options.LanIpv4Address)
                            : IPAddress.Loopback;
                        try
                        {
                            ipv4.Bind(new IPEndPoint(bindAddress, 0));
                        }
                        catch
                        {
                            if (!options.BindAllInterfaces || bindAddress.Equals(IPAddress.Any))
                            {
                                throw;
                            }
                            // 指定网卡暂时不可用时回退到任意地址，URL 仍使用原地址。
                            ipv4.Bind(new IPEndPoint(IPAddress.Any, 0));
                        }
                        port = ((IPEndPoint)ipv4.LocalEndPoint).Port;
                    }

                    if (needIpv6)
                    {
                        ipv6 = CreateTcpSocket(AddressFamily.InterNetworkV6);
                        try
                        {
                            ipv6.SetSocketOption(SocketOptionLevel.IPv6, (SocketOptionName)Ipv6OnlyOption, 1);
                        }
                        catch
                        {
                            // 部分系统不支持显式设置 IPV6_V6ONLY。
                        }

                        IPAddress bindAddress = options.BindAllInterfaces
                            ? IPAddress.IPv6Any
                            : IPAddress.IPv6Loopback;
                        ipv6.Bind(new IPEndPoint(bindAddress, port));
                        if (!needIpv4)
                        {
                            port = ((IPEndPoint)ipv6.LocalEndPoint).Port;
                        }
                    }

                    break;
                }
                catch
                {
                    CloseQuietly(ipv4);
                    CloseQuietly(ipv6);
                    ipv4 = null;
                    ipv6 = null;
                    port = 0;
                    if (attempt == 5)
                    {
                        throw;
                    }
                    Thread.Sleep(40);
                }
            }

            try
            {
                if (ipv4 != null) ipv4.Listen(16);
                if (ipv6 != null) ipv6.Listen(16);
            }
            catch
            {
                CloseQuietly(ipv4);
                CloseQuietly(ipv6);
                throw;
            }

            _ipv4Listener = ipv4;
            _ipv6Listener = ipv6;
            Port = port;

            string host4 = options.BindAllInterfaces ? IpPrivacy.StripScopeId(options.LanIpv4Address) : "127.0.0.1";
            string host6 = options.BindAllInterfaces
                ? IpPrivacy.StripScopeId(options.LanIpv6Address)
                : "::1";

            if (needIpv4)
            {
                LanIpv4Url = "http://" + host4 + ":" + Port + "/t/" + _token + "/lan/";
            }
            if (!string.IsNullOrEmpty(options.LanIpv6Address))
            {
                LanIpv6Url = "http://[" + host6 + "]:" + Port + "/t/" + _token + "/lan/";
            }
            LanUrl = !string.IsNullOrEmpty(LanIpv4Url) ? LanIpv4Url : LanIpv6Url;

            if (!string.IsNullOrEmpty(options.PublicIpv6Address))
            {
                string publicHost = options.BindAllInterfaces
                    ? IpPrivacy.StripScopeId(options.PublicIpv6Address)
                    : "::1";
                WanUrl = "http://[" + publicHost + "]:" + Port + "/t/" + _token + "/wan/";
            }

            _running = true;

            if (ipv4 != null)
            {
                _ipv4Thread = new Thread(AcceptLoop);
                _ipv4Thread.IsBackground = true;
                _ipv4Thread.Name = "ConnectivityTestV4";
                _ipv4Thread.Start(ipv4);
            }
            if (ipv6 != null)
            {
                _ipv6Thread = new Thread(AcceptLoop);
                _ipv6Thread.IsBackground = true;
                _ipv6Thread.Name = "ConnectivityTestV6";
                _ipv6Thread.Start(ipv6);
            }
        }

        /// <summary>停止监听并释放套接字。</summary>
        public void Stop()
        {
            _running = false;

            CloseQuietly(_ipv4Listener);
            CloseQuietly(_ipv6Listener);
            _ipv4Listener = null;
            _ipv6Listener = null;

            JoinQuietly(_ipv4Thread);
            JoinQuietly(_ipv6Thread);
            _ipv4Thread = null;
            _ipv6Thread = null;
            Port = 0;
        }

        public void Dispose()
        {
            Stop();
        }

        private void AcceptLoop(object state)
        {
            Socket listener = (Socket)state;
            while (_running)
            {
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

                Socket captured = client;
                ThreadPool.QueueUserWorkItem(delegate(object item)
                {
                    HandleClient((Socket)item);
                }, captured);
            }
        }

        private void HandleClient(Socket client)
        {
            if (client == null) return;
            try
            {
                client.ReceiveTimeout = 3000;
                client.SendTimeout = 3000;

                IPEndPoint remotePoint = client.RemoteEndPoint as IPEndPoint;
                IPEndPoint localPoint = client.LocalEndPoint as IPEndPoint;
                if (remotePoint == null || localPoint == null) return;

                string path = ReadRequestPath(client);
                if (string.IsNullOrEmpty(path)) return;

                string mode;
                string action;
                if (!TryParsePath(path, out mode, out action))
                {
                    WriteResponse(client, 404, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Not Found"));
                    return;
                }

                if (string.Equals(action, "favicon", StringComparison.Ordinal))
                {
                    WriteResponse(client, 204, "text/plain", new byte[0]);
                    return;
                }

                bool isIpv6 = localPoint.AddressFamily == AddressFamily.InterNetworkV6
                    || remotePoint.AddressFamily == AddressFamily.InterNetworkV6;

                if (string.Equals(action, "page", StringComparison.Ordinal))
                {
                    byte[] body = Encoding.UTF8.GetBytes(BuildPage(mode));
                    WriteResponse(client, 200, "text/html; charset=utf-8", body);
                    RaiseProbe(ConnectivityProbeKind.Page, isIpv6, mode, remotePoint, localPoint);
                    return;
                }

                if (string.Equals(action, "p4", StringComparison.Ordinal))
                {
                    WriteResponse(client, 200, "image/gif", TransparentGif);
                    RaiseProbe(ConnectivityProbeKind.Ipv4Probe, isIpv6, mode, remotePoint, localPoint);
                    return;
                }

                if (string.Equals(action, "p6", StringComparison.Ordinal))
                {
                    WriteResponse(client, 200, "image/gif", TransparentGif);
                    RaiseProbe(ConnectivityProbeKind.Ipv6Probe, isIpv6, mode, remotePoint, localPoint);
                    return;
                }

                WriteResponse(client, 404, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Not Found"));
            }
            catch (Exception ex)
            {
                RaiseError("处理连通性测试连接失败：" + ex.Message);
            }
            finally
            {
                CloseQuietly(client);
            }
        }

        private bool TryParsePath(string rawPath, out string mode, out string action)
        {
            mode = string.Empty;
            action = string.Empty;

            int query = rawPath.IndexOf('?');
            if (query >= 0)
            {
                rawPath = rawPath.Substring(0, query);
            }

            string prefix = "/t/" + _token + "/";
            if (!rawPath.StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }

            string rest = rawPath.Substring(prefix.Length);
            if (string.Equals(rest, "favicon.ico", StringComparison.Ordinal))
            {
                action = "favicon";
                return true;
            }

            if (rest.StartsWith("lan/", StringComparison.Ordinal))
            {
                mode = "lan";
                rest = rest.Substring(4);
            }
            else if (rest.StartsWith("wan/", StringComparison.Ordinal))
            {
                mode = "wan";
                rest = rest.Substring(4);
            }
            else
            {
                return false;
            }

            if (rest.Length == 0)
            {
                action = "page";
                return true;
            }
            if (string.Equals(rest, "p4", StringComparison.Ordinal))
            {
                action = "p4";
                return true;
            }
            if (string.Equals(rest, "p6", StringComparison.Ordinal))
            {
                action = "p6";
                return true;
            }
            return false;
        }

        private string BuildPage(string mode)
        {
            bool wan = string.Equals(mode, "wan", StringComparison.Ordinal);
            StringBuilder html = new StringBuilder();
            html.Append("<!doctype html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\">");
            html.Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
            html.Append("<title>网络连通性测试</title>");
            html.Append("<style>");
            html.Append("body{margin:0;padding:28px 18px;background:#0f172a;color:#e2e8f0;");
            html.Append("font-family:-apple-system,BlinkMacSystemFont,'Segoe UI','Microsoft YaHei',sans-serif;line-height:1.7}");
            html.Append(".wrap{max-width:520px;margin:0 auto}h2{margin:0 0 4px;font-size:24px}");
            html.Append(".sub{margin:0 0 22px;color:#94a3b8;font-size:14px}");
            html.Append(".card{background:#172033;border:1px solid #263349;border-radius:16px;padding:18px 18px 6px}");
            html.Append(".row{display:flex;justify-content:space-between;align-items:center;padding:12px 0;border-bottom:1px solid #223048}");
            html.Append(".row:last-child{border-bottom:0}.label{color:#cbd5e1;font-size:15px}");
            html.Append(".state{font-size:14px;font-weight:600}.wait{color:#94a3b8}.ok{color:#4ade80}.bad{color:#f87171}");
            html.Append(".hint{margin:18px 0 0;color:#94a3b8;font-size:13px}.mode{margin:0 0 14px;color:#a5b4fc;font-size:13px}");
            html.Append("</style><script>");
            html.Append("function mark(id,ok){var e=document.getElementById(id);if(!e)return;");
            html.Append("e.textContent=ok?'已连通':'未连通';e.className='state '+(ok?'ok':'bad');}");
            html.Append("</script></head><body><div class=\"wrap\">");
            html.Append("<h2>网络连通性测试</h2>");
            html.Append("<p class=\"sub\">手机已连接测试服务器，正在验证协议族连通性。</p>");
            html.Append("<p class=\"mode\">");
            html.Append(wan ? "公网模式（移动数据）" : "同一局域网模式（Wi-Fi）");
            html.Append("</p><div class=\"card\">");

            if (wan)
            {
                AppendProbeRow(html, "v6", "公网 IPv6 入站", string.IsNullOrEmpty(WanUrl) ? null : WanUrl + "p6");
            }
            else
            {
                AppendProbeRow(html, "v4", "内网 IPv4", string.IsNullOrEmpty(LanIpv4Url) ? null : LanIpv4Url + "p4");
                AppendProbeRow(html, "v6", "内网 IPv6", string.IsNullOrEmpty(LanIpv6Url) ? null : LanIpv6Url + "p6");
            }

            html.Append("</div><p class=\"hint\">请保持本页面打开几秒，完成后回到电脑查看完整结果。</p>");
            html.Append("<p class=\"hint\">本页面仅用于本机连通性测试，不会上传任何数据。</p>");
            html.Append("</div></body></html>");
            return html.ToString();
        }

        private static void AppendProbeRow(StringBuilder html, string id, string label, string probeUrl)
        {
            html.Append("<div class=\"row\"><span class=\"label\">");
            html.Append(label);
            html.Append("</span><span id=\"");
            html.Append(id);
            html.Append("\" class=\"state wait\">测试中…</span></div>");

            if (!string.IsNullOrEmpty(probeUrl))
            {
                html.Append("<img src=\"");
                html.Append(probeUrl);
                html.Append("\" alt=\"\" style=\"display:none\" onload=\"mark('");
                html.Append(id);
                html.Append("',true)\" onerror=\"mark('");
                html.Append(id);
                html.Append("',false)\">");
            }
            else
            {
                html.Append("<script>mark('");
                html.Append(id);
                html.Append("',false)</script>");
            }
        }

        private static string ReadRequestPath(Socket client)
        {
            NetworkStream stream = new NetworkStream(client, false);
            byte[] buffer = new byte[MaxRequestBytes];
            int total = 0;
            while (total < buffer.Length)
            {
                int read = stream.Read(buffer, total, buffer.Length - total);
                if (read <= 0) break;
                total += read;
                string text = Encoding.ASCII.GetString(buffer, 0, total);
                if (text.IndexOf("\r\n\r\n", StringComparison.Ordinal) >= 0)
                {
                    string[] lines = text.Split(new string[] { "\r\n" }, StringSplitOptions.None);
                    if (lines.Length == 0) return null;
                    string[] parts = lines[0].Split(' ');
                    if (parts.Length < 2 || !string.Equals(parts[0], "GET", StringComparison.Ordinal))
                    {
                        return null;
                    }
                    return parts[1];
                }
            }
            return null;
        }

        private static void WriteResponse(Socket client, int status, string contentType, byte[] body)
        {
            if (body == null) body = new byte[0];

            string reason = status == 200 ? "OK" : (status == 204 ? "No Content" : "Not Found");
            StringBuilder header = new StringBuilder();
            header.Append("HTTP/1.1 ");
            header.Append(status);
            header.Append(' ');
            header.Append(reason);
            header.Append("\r\n");
            if (status != 204)
            {
                header.Append("Content-Type: ");
                header.Append(contentType);
                header.Append("\r\n");
            }
            header.Append("Content-Length: ");
            header.Append(body.Length);
            header.Append("\r\n");
            header.Append("Cache-Control: no-store\r\n");
            header.Append("Access-Control-Allow-Origin: *\r\n");
            header.Append("Connection: close\r\n\r\n");

            byte[] headerBytes = Encoding.ASCII.GetBytes(header.ToString());
            NetworkStream stream = new NetworkStream(client, false);
            stream.Write(headerBytes, 0, headerBytes.Length);
            if (body.Length > 0)
            {
                stream.Write(body, 0, body.Length);
            }
            stream.Flush();
        }

        private void RaiseProbe(ConnectivityProbeKind kind, bool isIpv6, string mode,
            IPEndPoint remotePoint, IPEndPoint localPoint)
        {
            EventHandler<ConnectivityProbeEventArgs> handler = ProbeReceived;
            if (handler == null) return;

            string remote = remotePoint == null ? string.Empty : remotePoint.Address.ToString();
            string local = localPoint == null ? string.Empty : localPoint.Address.ToString();
            remote = IpPrivacy.StripScopeId(remote);
            local = IpPrivacy.StripScopeId(local);

            ConnectivityProbeEventArgs args = new ConnectivityProbeEventArgs();
            args.Kind = kind;
            args.IsIpv6 = isIpv6;
            args.Mode = mode;
            args.RemoteAddress = remote;
            args.LocalAddress = local;
            args.IsPublicRemote = IpClassifier.Classify(remote) == IpAddressScope.Public;
            args.Timestamp = DateTime.Now;

            try
            {
                handler(this, args);
            }
            catch
            {
                // 事件处理器异常不应影响监听线程。
            }
        }

        private void RaiseError(string message)
        {
            EventHandler<ConnectivityErrorEventArgs> handler = ServerError;
            if (handler == null) return;
            try
            {
                handler(this, new ConnectivityErrorEventArgs(message));
            }
            catch
            {
            }
        }

        private static Socket CreateTcpSocket(AddressFamily family)
        {
            Socket socket = new Socket(family, SocketType.Stream, ProtocolType.Tcp);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
            return socket;
        }

        private static IPAddress ParseAddress(string address)
        {
            IPAddress parsed;
            if (string.IsNullOrEmpty(address)
                || !IPAddress.TryParse(IpPrivacy.StripScopeId(address), out parsed))
            {
                throw new ArgumentException("无效的 IP 地址：" + address, "address");
            }
            return parsed;
        }

        private static string CreateToken()
        {
            return Guid.NewGuid().ToString("N").Substring(0, 16);
        }

        private static void CloseQuietly(Socket socket)
        {
            if (socket == null) return;
            try { socket.Close(); }
            catch { }
        }

        private static void JoinQuietly(Thread thread)
        {
            if (thread == null) return;
            try { thread.Join(800); }
            catch { }
        }

        private static readonly byte[] TransparentGif = new byte[]
        {
            0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x01, 0x00, 0x01, 0x00,
            0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0x21,
            0xF9, 0x04, 0x01, 0x00, 0x00, 0x00, 0x00, 0x2C, 0x00, 0x00,
            0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x02, 0x02, 0x44,
            0x01, 0x00, 0x3B
        };
    }
}
