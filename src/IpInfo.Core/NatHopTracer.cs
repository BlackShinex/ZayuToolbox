using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;

namespace IpInfo.Core
{
    /// <summary>一次 TTL 跃点探测的结果。</summary>
    public sealed class NatHop
    {
        public int Ttl { get; set; }

        /// <summary>响应的路由器地址；超时时为空字符串。</summary>
        public string Address { get; set; }

        public IpAddressScope Scope { get; set; }
        public string ScopeLabel { get; set; }

        public bool TimedOut { get; set; }
        public bool ReachedTarget { get; set; }
        public long RoundTripMilliseconds { get; set; }

        /// <summary>该跳的说明，例如“私网路由，可能位于 NAT 后”。</summary>
        public string Note { get; set; }

        /// <summary>同一 TTL 的多次探测收到的地址；分流或负载均衡时可能不止一个。</summary>
        public List<string> Addresses { get; private set; }

        public bool IsPublic
        {
            get { return Scope == IpAddressScope.Public; }
        }

        public NatHop()
        {
            Address = string.Empty;
            Scope = IpAddressScope.Unknown;
            ScopeLabel = string.Empty;
            Note = string.Empty;
            Addresses = new List<string>();
        }
    }

    /// <summary>多层 NAT 跃点跟踪的完整结果。</summary>
    public sealed class NatHopTraceResult
    {
        public List<NatHop> Hops { get; private set; }
        public string Target { get; set; }
        public int MaxHops { get; set; }
        public bool ReachedInternet { get; set; }
        public bool ReachedTarget { get; set; }
        public bool Canceled { get; set; }
        public string PublicHopAddress { get; set; }
        public int PublicHopTtl { get; set; }

        /// <summary>最后一个公网跃点，通常就是实际出口。</summary>
        public string ExitHopAddress { get; set; }
        public int ExitHopTtl { get; set; }
        public int PrivateHopCount { get; set; }
        public int CgnatHopCount { get; set; }
        public string Summary { get; set; }
        public string Error { get; set; }
        public TimeSpan Elapsed { get; set; }
        public DateTime Timestamp { get; set; }

        public NatHopTraceResult()
        {
            Hops = new List<NatHop>();
            Target = string.Empty;
            PublicHopAddress = string.Empty;
            ExitHopAddress = string.Empty;
            Summary = string.Empty;
            Error = string.Empty;
            Timestamp = DateTime.Now;
        }
    }

    /// <summary>
    /// 基于 Windows IcmpSendEcho 的 IPv4 TTL 跃点跟踪。
    /// 普通用户权限即可运行，不需要原始套接字或管理员权限。
    /// 遇到首个公网地址或目标主机后停止。
    /// </summary>
    public static class NatHopTracer
    {
        // 国内多数网络可正常 ICMP 到达；1.1.1.1 在部分地区会被拦截。
        public const string DefaultTarget = "223.5.5.5";
        public const int DefaultMaxHops = 30;
        private const int IcmpTimeoutMs = 1000;
        private const int RequestSize = 32;
        private const int ReplyBufferSize = 2048;

        private const uint IpSuccess = 0;
        private const uint IpDestNetUnreachable = 11002;
        private const uint IpDestHostUnreachable = 11003;
        private const uint IpDestProtocolUnreachable = 11004;
        private const uint IpDestPortUnreachable = 11005;
        private const uint IpNoResources = 11006;
        private const uint IpReqTimedOut = 11010;
        private const uint IpTtlExpiredTransit = 11013;
        private const uint IpTtlExpiredReassembly = 11014;
        private const uint IpGeneralFailure = 11050;

        public static NatHopTraceResult Trace(Action<string> progress, CancellationToken cancellationToken)
        {
            NatHopTraceResult result = new NatHopTraceResult();
            result.Target = DefaultTarget;
            result.MaxHops = DefaultMaxHops;

            Stopwatch total = Stopwatch.StartNew();
            IntPtr handle = IntPtr.Zero;
            try
            {
                IPAddress target;
                if (!IPAddress.TryParse(DefaultTarget, out target))
                {
                    result.Error = "跃点跟踪目标地址无效。";
                    return result;
                }

                handle = IcmpCreateFile();
                if (handle == IntPtr.Zero || handle == new IntPtr(-1))
                {
                    result.Error = "无法创建 ICMP 句柄（可能被系统策略或安全软件阻止）。";
                    return result;
                }

                byte[] request = BuildRequestData();
                uint destination = BitConverter.ToUInt32(target.GetAddressBytes(), 0);

                int consecutiveTimeouts = 0;
                for (int ttl = 1; ttl <= DefaultMaxHops; ttl++)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        result.Canceled = true;
                        result.Summary = "跃点跟踪已停止。";
                        break;
                    }

                    Report(progress, "正在探测第 " + ttl + " 跳…");
                    NatHop hop = ProbeHop(handle, destination, request, ttl, !result.ReachedInternet);
                    result.Hops.Add(hop);

                    if (hop.TimedOut)
                    {
                        consecutiveTimeouts++;
                        Report(progress, "第 " + ttl + " 跳：无响应。");
                        if (result.ReachedInternet && consecutiveTimeouts >= 2)
                        {
                            // 进入公网后连续两跳没有响应就收尾，不再追到目标主机。
                            break;
                        }
                        continue;
                    }

                    consecutiveTimeouts = 0;
                    Report(progress, "第 " + ttl + " 跳：" + hop.Address + " · " + hop.ScopeLabel);

                    if (hop.ReachedTarget)
                    {
                        result.ReachedTarget = true;
                        result.ReachedInternet = true;
                        result.ExitHopAddress = hop.Address;
                        result.ExitHopTtl = hop.Ttl;
                        break;
                    }

                    if (hop.IsPublic)
                    {
                        if (!result.ReachedInternet)
                        {
                            result.PublicHopAddress = hop.Address;
                            result.PublicHopTtl = hop.Ttl;
                        }
                        result.ReachedInternet = true;
                        result.ExitHopAddress = hop.Address;
                        result.ExitHopTtl = hop.Ttl;
                    }
                }
            }
            catch (Exception ex)
            {
                result.Error = "跃点跟踪失败：" + ex.GetType().Name + " " + ex.Message;
            }
            finally
            {
                if (handle != IntPtr.Zero && handle != new IntPtr(-1))
                {
                    try { IcmpCloseHandle(handle); }
                    catch { }
                }
                total.Stop();
                result.Elapsed = total.Elapsed;
                result.Timestamp = DateTime.Now;
                for (int i = 0; i < result.Hops.Count; i++)
                {
                    NatHop hop = result.Hops[i];
                    if (hop.Scope == IpAddressScope.Private) result.PrivateHopCount++;
                    if (hop.Scope == IpAddressScope.SharedCgnat) result.CgnatHopCount++;
                }
                if (string.IsNullOrEmpty(result.Summary))
                {
                    ComposeSummary(result);
                }
            }
            return result;
        }

        /// <summary>
        /// 单跳探测：私网段超时补测一次，公网跳点额外再采一次（用于发现分流/负载均衡造成的多出口）。
        /// 公网段不补测，靠连续两跳无响应判断路径结束，避免收尾多等一个超时周期。
        /// </summary>
        private static NatHop ProbeHop(IntPtr handle, uint destination, byte[] request, int ttl, bool retryOnTimeout)
        {
            NatHop hop = ProbeHopOnce(handle, destination, request, ttl);
            if (hop.TimedOut && retryOnTimeout)
            {
                NatHop retry = ProbeHopOnce(handle, destination, request, ttl);
                return retry.TimedOut ? hop : retry;
            }
            if (hop.TimedOut) return hop;

            hop.Addresses.Add(hop.Address);
            if (!hop.IsPublic) return hop;

            NatHop extra = ProbeHopOnce(handle, destination, request, ttl);
            if (extra.TimedOut || string.Equals(extra.Address, hop.Address, StringComparison.Ordinal)) return hop;

            if (!hop.Addresses.Contains(extra.Address)) hop.Addresses.Add(extra.Address);
            string note = "同跳还有 " + extra.Address + "（分流或负载均衡）";
            hop.Note = string.IsNullOrEmpty(hop.Note) ? note : hop.Note + "；" + note;
            return hop;
        }

        private static NatHop ProbeHopOnce(IntPtr handle, uint destination, byte[] request, int ttl)
        {
            NatHop hop = new NatHop();
            hop.Ttl = ttl;

            IP_OPTION_INFORMATION options = new IP_OPTION_INFORMATION();
            options.Ttl = (byte)ttl;

            byte[] replyBuffer = new byte[ReplyBufferSize];
            Stopwatch stopwatch = Stopwatch.StartNew();
            uint count = IcmpSendEcho(handle, destination, request, (ushort)request.Length,
                ref options, replyBuffer, (uint)replyBuffer.Length, IcmpTimeoutMs);
            stopwatch.Stop();
            hop.RoundTripMilliseconds = stopwatch.ElapsedMilliseconds;

            if (count == 0)
            {
                int error = Marshal.GetLastWin32Error();
                if (error == (int)IpReqTimedOut || error == 0)
                {
                    hop.TimedOut = true;
                    hop.Note = "无响应（可能被防火墙或 ICMP 策略过滤）";
                }
                else
                {
                    hop.TimedOut = true;
                    hop.Note = "ICMP 请求失败（错误码 " + error + "）";
                }
                return hop;
            }

            ICMP_ECHO_REPLY reply = ReadReply(replyBuffer);
            IPAddress address = new IPAddress(BitConverter.GetBytes(reply.Address));
            hop.Address = address.ToString();
            hop.Scope = IpClassifier.Classify(address);
            hop.ScopeLabel = IpClassifier.GetScopeLabel(hop.Scope);
            hop.RoundTripMilliseconds = reply.RoundTripTime > 0
                ? reply.RoundTripTime
                : stopwatch.ElapsedMilliseconds;

            if (reply.Status == IpSuccess)
            {
                hop.ReachedTarget = true;
                hop.Note = "已到达目标主机";
            }
            else if (reply.Status == IpTtlExpiredTransit || reply.Status == IpTtlExpiredReassembly)
            {
                hop.Note = DescribeScopeNote(hop.Scope);
            }
            else
            {
                hop.Note = DescribeStatus(reply.Status) + "；" + DescribeScopeNote(hop.Scope);
            }

            return hop;
        }

        private static ICMP_ECHO_REPLY ReadReply(byte[] replyBuffer)
        {
            GCHandle pinned = GCHandle.Alloc(replyBuffer, GCHandleType.Pinned);
            try
            {
                return (ICMP_ECHO_REPLY)Marshal.PtrToStructure(
                    pinned.AddrOfPinnedObject(), typeof(ICMP_ECHO_REPLY));
            }
            finally
            {
                pinned.Free();
            }
        }

        private static byte[] BuildRequestData()
        {
            byte[] request = new byte[RequestSize];
            for (int i = 0; i < request.Length; i++)
            {
                request[i] = (byte)i;
            }
            return request;
        }

        private static void ComposeSummary(NatHopTraceResult result)
        {
            int responsive = 0;
            for (int i = 0; i < result.Hops.Count; i++)
            {
                if (!result.Hops[i].TimedOut) responsive++;
            }

            if (result.ReachedTarget)
            {
                result.Summary = "已到达目标 " + result.Target + "（第 " + result.Hops.Count
                    + " 跳，耗时 " + result.Elapsed.TotalMilliseconds.ToString("0") + " ms）。共经过 "
                    + result.PrivateHopCount + " 个私网跃点、"
                    + result.CgnatHopCount + " 个 CGNAT 跃点。";
                return;
            }

            if (result.ReachedInternet)
            {
                string exitAddress = string.IsNullOrEmpty(result.ExitHopAddress)
                    ? result.PublicHopAddress
                    : result.ExitHopAddress;
                int exitTtl = result.ExitHopTtl > 0 ? result.ExitHopTtl : result.PublicHopTtl;

                string summary = "出口 " + exitAddress + "（第 " + exitTtl + " 跳）";
                if (!string.IsNullOrEmpty(result.PublicHopAddress) && result.PublicHopTtl != exitTtl)
                {
                    summary += "，首个公网跃点 " + result.PublicHopAddress + "（第 " + result.PublicHopTtl + " 跳）";
                }
                summary += "；之前有 " + result.PrivateHopCount + " 个私网跃点、"
                    + result.CgnatHopCount + " 个 CGNAT 跃点，耗时 "
                    + result.Elapsed.TotalMilliseconds.ToString("0") + " ms。";
                result.Summary = summary;
                return;
            }

            if (result.Canceled)
            {
                result.Summary = "跃点跟踪已停止。";
                return;
            }

            if (responsive == 0)
            {
                result.Summary = "未收到任何跃点响应。可能是 ICMP 被拦截，或当前网络不允许该探测。";
                return;
            }

            result.Summary = "在 " + result.MaxHops + " 跳内未发现公网地址；路径可能更长，或公网路由器不返回 ICMP。";
        }

        private static string DescribeScopeNote(IpAddressScope scope)
        {
            switch (scope)
            {
                case IpAddressScope.Public:
                    return "公网地址，已到达互联网出口";
                case IpAddressScope.SharedCgnat:
                    return "运营商级 NAT（CGNAT）地址，可能位于多层 NAT 中";
                case IpAddressScope.Private:
                    return "私网地址，可能是内网路由或 NAT 网关";
                case IpAddressScope.UniqueLocal:
                    return "唯一本地地址（IPv6 私网）";
                case IpAddressScope.LinkLocal:
                    return "链路本地地址";
                default:
                    return "非公网地址";
            }
        }

        private static string DescribeStatus(uint status)
        {
            switch (status)
            {
                case IpDestNetUnreachable: return "目标网络不可达";
                case IpDestHostUnreachable: return "目标主机不可达";
                case IpDestProtocolUnreachable: return "协议不可达";
                case IpDestPortUnreachable: return "端口不可达";
                case IpNoResources: return "路由器资源不足";
                case IpGeneralFailure: return "ICMP 一般故障";
                default: return "ICMP 状态 " + status;
            }
        }

        private static void Report(Action<string> progress, string message)
        {
            if (progress != null) progress(message);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IP_OPTION_INFORMATION
        {
            public byte Ttl;
            public byte Tos;
            public byte Flags;
            public byte OptionsSize;
            public IntPtr OptionsData;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ICMP_ECHO_REPLY
        {
            public uint Address;
            public uint Status;
            public uint RoundTripTime;
            public ushort DataSize;
            public ushort Reserved;
            public IntPtr Data;
            public IP_OPTION_INFORMATION Options;
        }

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern IntPtr IcmpCreateFile();

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern bool IcmpCloseHandle(IntPtr icmpHandle);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint IcmpSendEcho(
            IntPtr icmpHandle,
            uint destinationAddress,
            byte[] requestData,
            ushort requestSize,
            ref IP_OPTION_INFORMATION requestOptions,
            byte[] replyBuffer,
            uint replySize,
            uint timeout);
    }
}
