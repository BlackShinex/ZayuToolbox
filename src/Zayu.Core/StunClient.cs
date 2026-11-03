using System;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Zayu.Core
{
    /// <summary>
    /// STUN CHANGE-REQUEST 属性（type 0x0003）的请求标志，
    /// 用于 RFC 3489 / RFC 5780 的 NAT 过滤行为测试。
    /// </summary>
    public enum StunChangeRequest
    {
        /// <summary>不附加 CHANGE-REQUEST 属性。</summary>
        None = 0,

        /// <summary>请求服务器换一个端口回复（0x02）。</summary>
        ChangePort = 2,

        /// <summary>请求服务器换一个 IP 回复（0x04）。</summary>
        ChangeIp = 4,

        /// <summary>请求服务器同时换 IP 与端口回复（0x06）。</summary>
        ChangeIpAndPort = 6
    }

    /// <summary>一次 STUN Binding 查询的结果。</summary>
    public sealed class StunResponse
    {
        /// <summary>是否成功拿到映射地址或（过滤测试中的）合法响应。</summary>
        public bool Success { get; set; }

        /// <summary>失败原因（中文）。成功时为空字符串。</summary>
        public string Error { get; set; }

        /// <summary>服务器返回的公网映射地址（XOR-MAPPED-ADDRESS / MAPPED-ADDRESS）。</summary>
        public IPEndPoint Mapped { get; set; }

        /// <summary>实际回包的对端地址，用于判断 CHANGE-REQUEST 是否被遵守。</summary>
        public IPEndPoint ResponseSource { get; set; }

        public StunResponse()
        {
            Error = string.Empty;
        }
    }

    /// <summary>
    /// 一个自带 UDP 套接字的 RFC 5389 STUN Binding 客户端（自行实现，无第三方依赖）。
    /// 同一个实例的所有 Query 共用一个本地端口，因此可以用于 RFC 4787 的映射行为测试。
    /// 该类不是线程安全的：请在同一线程中顺序调用。
    /// </summary>
    public sealed class StunProbe : IDisposable
    {
        private const int HeaderSize = 20;
        private const int MaxPacketSize = 2048;
        private const int TransactionIdSize = 12;
        private const uint MagicCookieValue = 0x2112A442u;
        private const int MagicCookieXorPort = 0x2112;

        // 用于关闭 Windows 上 UDP 的 ICMP 端口不可达反馈（WSAECONNRESET），
        // 否则向不可用服务器发送请求后再收包会抛 10054。
        private const int SioUdpConnReset = -1744830452; // 0x9800000C

        private readonly Socket _socket;
        private readonly AddressFamily _family;
        private bool _disposed;

        /// <summary>创建一个绑定到任意端口（0.0.0.0:0 / [::]:0）的 UDP 探测套接字。</summary>
        public StunProbe(AddressFamily family)
        {
            if (family != AddressFamily.InterNetwork && family != AddressFamily.InterNetworkV6)
            {
                throw new ArgumentException("StunProbe 只支持 IPv4 或 IPv6 地址族。", "family");
            }

            _family = family;
            _socket = new Socket(family, SocketType.Dgram, ProtocolType.Udp);
            IPAddress any = family == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any;
            _socket.Bind(new IPEndPoint(any, 0));
            TryDisableUdpConnectionReset(_socket);
        }

        /// <summary>该探测套接字使用的地址族。</summary>
        public AddressFamily Family
        {
            get { return _family; }
        }

        /// <summary>本地绑定端点。</summary>
        public IPEndPoint LocalEndPoint
        {
            get { return _socket.LocalEndPoint as IPEndPoint; }
        }

        /// <summary>本地 UDP 端口（映射测试的对照值）。</summary>
        public int LocalPort
        {
            get
            {
                IPEndPoint endpoint = _socket.LocalEndPoint as IPEndPoint;
                return endpoint == null ? 0 : endpoint.Port;
            }
        }

        /// <summary>发送普通 Binding 请求并等待映射地址。</summary>
        public StunResponse Query(IPEndPoint server, int timeoutMs)
        {
            return QueryCore(server, timeoutMs, StunChangeRequest.None, CancellationToken.None);
        }

        /// <summary>发送带 CHANGE-REQUEST 的 Binding 请求（用于 NAT 过滤行为测试）。</summary>
        public StunResponse Query(IPEndPoint server, int timeoutMs, StunChangeRequest change)
        {
            return QueryCore(server, timeoutMs, change, CancellationToken.None);
        }

        /// <summary>发送带 CHANGE-REQUEST 的 Binding 请求，并支持取消。</summary>
        public StunResponse Query(IPEndPoint server, int timeoutMs, StunChangeRequest change, CancellationToken cancellationToken)
        {
            return QueryCore(server, timeoutMs, change, cancellationToken);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                _socket.Close();
            }
            catch
            {
                // 释放阶段忽略异常。
            }
        }

        private StunResponse QueryCore(IPEndPoint server, int timeoutMs, StunChangeRequest change,
            CancellationToken cancellationToken)
        {
            StunResponse response = new StunResponse();
            if (server == null)
            {
                response.Error = "STUN 服务器地址为空。";
                return response;
            }
            if (_disposed)
            {
                response.Error = "STUN 探测套接字已释放。";
                return response;
            }
            if (server.AddressFamily != _family)
            {
                response.Error = "地址族不匹配：套接字为 " + _family + "，服务器为 " + server.AddressFamily + "。";
                return response;
            }
            if (timeoutMs <= 0)
            {
                timeoutMs = 1000;
            }

            try
            {
                byte[] transactionId = new byte[TransactionIdSize];
                using (RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider())
                {
                    rng.GetBytes(transactionId);
                }
                byte[] request = BuildBindingRequest(transactionId, change);

                cancellationToken.ThrowIfCancellationRequested();
                _socket.SendTimeout = timeoutMs;
                _socket.SendTo(request, server);

                DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                byte[] buffer = new byte[MaxPacketSize];
                string lastError = string.Empty;

                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    int remaining = (int)Math.Ceiling((deadline - DateTime.UtcNow).TotalMilliseconds);
                    if (remaining <= 0)
                    {
                        break;
                    }

                    _socket.ReceiveTimeout = remaining;
                    EndPoint remote = new IPEndPoint(
                        _family == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0);

                    int received;
                    try
                    {
                        received = _socket.ReceiveFrom(buffer, ref remote);
                    }
                    catch (SocketException ex)
                    {
                        if (IsTimeoutError(ex))
                        {
                            // 本轮接收超时，交给 while 条件统一判断总超时。
                            break;
                        }
                        if (IsTransientIcmpError(ex))
                        {
                            // 上一次发送触发的 ICMP 反馈（端口不可达等），它并不说明当前请求失败；
                            // 稍等后继续等到总超时。
                            Thread.Sleep(5);
                            continue;
                        }
                        lastError = DescribeSocketError(ex);
                        break;
                    }

                    if (received < HeaderSize)
                    {
                        continue;
                    }
                    if (ReadUInt32(buffer, 4) != MagicCookieValue)
                    {
                        continue; // 不是 STUN 报文，忽略。
                    }
                    if (!TransactionIdMatches(buffer, transactionId))
                    {
                        continue; // 过期或串包的响应，继续等待自己的响应。
                    }

                    int messageType = (buffer[0] << 8) | buffer[1];
                    int bodyLength = (buffer[2] << 8) | buffer[3];
                    int usableLength = Math.Min(received, HeaderSize + bodyLength);
                    IPEndPoint source = remote as IPEndPoint;
                    response.ResponseSource = source;

                    if (messageType == 0x0111)
                    {
                        response.Success = false;
                        response.Error = ParseErrorCode(buffer, usableLength);
                        return response;
                    }
                    if (messageType != 0x0101)
                    {
                        lastError = "非预期的 STUN 响应类型 0x" + messageType.ToString("X4") + "。";
                        continue;
                    }

                    IPEndPoint mapped;
                    if (TryParseMappedAddress(buffer, usableLength, transactionId, out mapped))
                    {
                        response.Mapped = mapped;
                        response.Success = true;
                        response.Error = string.Empty;
                        return response;
                    }

                    lastError = "STUN 响应中缺少 MAPPED-ADDRESS / XOR-MAPPED-ADDRESS 属性。";
                    break;
                }

                response.Success = false;
                if (!string.IsNullOrEmpty(lastError))
                {
                    response.Error = lastError;
                }
                else
                {
                    response.Error = "STUN 服务器在 " + timeoutMs + " 毫秒内没有响应。";
                }
                return response;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                response.Success = false;
                response.Error = "STUN 请求失败：" + ex.GetType().Name + " " + ex.Message;
                return response;
            }
        }

        private static byte[] BuildBindingRequest(byte[] transactionId, StunChangeRequest change)
        {
            bool hasChange = change != StunChangeRequest.None;
            byte[] request = new byte[hasChange ? HeaderSize + 8 : HeaderSize];

            request[0] = 0x00;
            request[1] = 0x01; // Binding Request (0x0001)

            int attributeLength = hasChange ? 8 : 0;
            request[2] = (byte)((attributeLength >> 8) & 0xFF);
            request[3] = (byte)(attributeLength & 0xFF);

            // magic cookie 0x2112A442
            request[4] = 0x21;
            request[5] = 0x12;
            request[6] = 0xA4;
            request[7] = 0x42;

            Buffer.BlockCopy(transactionId, 0, request, 8, TransactionIdSize);

            if (hasChange)
            {
                // CHANGE-REQUEST 属性：type 0x0003，length 4，value = 0x0000000X
                request[20] = 0x00;
                request[21] = 0x03;
                request[22] = 0x00;
                request[23] = 0x04;
                request[24] = 0x00;
                request[25] = 0x00;
                request[26] = 0x00;
                request[27] = (byte)((int)change);
            }

            return request;
        }

        private static bool TryParseMappedAddress(byte[] packet, int length, byte[] transactionId,
            out IPEndPoint mapped)
        {
            mapped = null;
            IPEndPoint legacy = null;
            int offset = HeaderSize;

            while (offset + 4 <= length)
            {
                int attributeType = (packet[offset] << 8) | packet[offset + 1];
                int attributeLength = (packet[offset + 2] << 8) | packet[offset + 3];
                int valueOffset = offset + 4;

                if (valueOffset + attributeLength > length)
                {
                    break; // 报文被截断。
                }

                IPEndPoint endpoint;
                if (attributeType == 0x0020)
                {
                    // XOR-MAPPED-ADDRESS（RFC 5389）
                    if (TryDecodeAddress(packet, valueOffset, attributeLength, transactionId, true, out endpoint))
                    {
                        mapped = endpoint;
                        return true;
                    }
                }
                else if (attributeType == 0x0001)
                {
                    // 旧式 MAPPED-ADDRESS（RFC 3489），作为兜底。
                    if (legacy == null
                        && TryDecodeAddress(packet, valueOffset, attributeLength, transactionId, false, out endpoint))
                    {
                        legacy = endpoint;
                    }
                }

                // 属性长度必须按 4 字节对齐。
                offset = valueOffset + ((attributeLength + 3) & ~3);
            }

            if (legacy != null)
            {
                mapped = legacy;
                return true;
            }
            return false;
        }

        private static bool TryDecodeAddress(byte[] packet, int valueOffset, int attributeLength,
            byte[] transactionId, bool xored, out IPEndPoint endpoint)
        {
            endpoint = null;
            if (attributeLength < 8)
            {
                return false;
            }

            int family = packet[valueOffset + 1];
            int port = (packet[valueOffset + 2] << 8) | packet[valueOffset + 3];
            if (xored)
            {
                port = port ^ MagicCookieXorPort;
            }
            if (port <= 0 || port > 65535)
            {
                return false;
            }

            if (family == 0x01) // IPv4
            {
                byte[] addressBytes = new byte[4];
                for (int i = 0; i < 4; i++)
                {
                    byte value = packet[valueOffset + 4 + i];
                    if (xored)
                    {
                        value = (byte)(value ^ packet[4 + i]); // 与 magic cookie 对应字节异或
                    }
                    addressBytes[i] = value;
                }
                endpoint = new IPEndPoint(new IPAddress(addressBytes), port);
                return true;
            }

            if (family == 0x02) // IPv6
            {
                if (attributeLength < 20)
                {
                    return false;
                }
                byte[] addressBytes = new byte[16];
                for (int i = 0; i < 16; i++)
                {
                    byte value = packet[valueOffset + 4 + i];
                    if (xored)
                    {
                        // 前 4 字节与 magic cookie 异或，后 12 字节与 transaction id 异或。
                        byte key = i < 4 ? packet[4 + i] : transactionId[i - 4];
                        value = (byte)(value ^ key);
                    }
                    addressBytes[i] = value;
                }
                endpoint = new IPEndPoint(new IPAddress(addressBytes), port);
                return true;
            }

            return false;
        }

        private static string ParseErrorCode(byte[] packet, int length)
        {
            int offset = HeaderSize;
            while (offset + 4 <= length)
            {
                int attributeType = (packet[offset] << 8) | packet[offset + 1];
                int attributeLength = (packet[offset + 2] << 8) | packet[offset + 3];
                int valueOffset = offset + 4;
                if (valueOffset + attributeLength > length)
                {
                    break;
                }

                if (attributeType == 0x0009 && attributeLength >= 4)
                {
                    int errorClass = packet[valueOffset + 2] & 0x07;
                    int errorNumber = packet[valueOffset + 3];
                    int code = errorClass * 100 + errorNumber;
                    string reason = string.Empty;
                    if (attributeLength > 4)
                    {
                        reason = Encoding.UTF8.GetString(packet, valueOffset + 4, attributeLength - 4);
                    }
                    if (string.IsNullOrEmpty(reason))
                    {
                        return "STUN 错误响应 " + code + "。";
                    }
                    return "STUN 错误响应 " + code + "：" + reason.Trim();
                }

                offset = valueOffset + ((attributeLength + 3) & ~3);
            }
            return "STUN 错误响应。";
        }

        private static bool TransactionIdMatches(byte[] packet, byte[] transactionId)
        {
            for (int i = 0; i < TransactionIdSize; i++)
            {
                if (packet[8 + i] != transactionId[i])
                {
                    return false;
                }
            }
            return true;
        }

        private static uint ReadUInt32(byte[] buffer, int offset)
        {
            return ((uint)buffer[offset] << 24)
                | ((uint)buffer[offset + 1] << 16)
                | ((uint)buffer[offset + 2] << 8)
                | (uint)buffer[offset + 3];
        }

        private static bool IsTimeoutError(SocketException ex)
        {
            return ex.SocketErrorCode == SocketError.TimedOut
                || ex.SocketErrorCode == SocketError.WouldBlock;
        }

        private static bool IsTransientIcmpError(SocketException ex)
        {
            return ex.SocketErrorCode == SocketError.ConnectionReset
                || ex.SocketErrorCode == SocketError.ConnectionRefused
                || ex.SocketErrorCode == SocketError.HostUnreachable
                || ex.SocketErrorCode == SocketError.NetworkUnreachable
                || ex.SocketErrorCode == SocketError.HostDown
                || ex.SocketErrorCode == SocketError.NetworkDown;
        }

        private static string DescribeSocketError(SocketException ex)
        {
            switch (ex.SocketErrorCode)
            {
                case SocketError.ConnectionReset:
                    return "STUN 服务器端口不可达（ICMP 端口不可达）。";
                case SocketError.ConnectionRefused:
                    return "STUN 服务器拒绝连接。";
                case SocketError.HostUnreachable:
                case SocketError.NetworkUnreachable:
                case SocketError.HostDown:
                case SocketError.NetworkDown:
                    return "网络不可达，无法连接 STUN 服务器。";
                default:
                    return "STUN 套接字错误：" + ex.SocketErrorCode + "（" + ex.Message + "）。";
            }
        }

        private static void TryDisableUdpConnectionReset(Socket socket)
        {
            try
            {
                socket.IOControl(SioUdpConnReset, new byte[] { 0, 0, 0, 0 }, null);
            }
            catch
            {
                // 非 Windows 或权限不足时忽略；上层仍会处理 SocketException。
            }
        }
    }
}
