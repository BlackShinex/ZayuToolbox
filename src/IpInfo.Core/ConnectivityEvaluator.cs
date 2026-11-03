using System;
using System.Collections.Generic;
using System.Net.Sockets;

namespace IpInfo.Core
{
    /// <summary>
    /// 一次连通性测试会话：保存 PC 侧公网探测结果和手机侧页面探测结果，
    /// 并生成 2x2 检查矩阵与 P2P 结论。
    /// </summary>
    public sealed class ConnectivityTestSession
    {
        public NetworkReport Report { get; private set; }
        public ConnectivityTestOptions Options { get; private set; }

        public TcpConnectivityResult WanIpv4Probe { get; set; }
        public TcpConnectivityResult WanIpv6Probe { get; set; }

        public bool LanModeUsed { get; private set; }
        public bool WanModeUsed { get; private set; }
        public bool LanModeAttempted { get; private set; }
        public bool WanModeAttempted { get; private set; }
        public bool LanPageReceived { get; private set; }
        public bool WanPageReceived { get; private set; }

        public bool LanIpv4Reached { get; private set; }
        public string LanIpv4Remote { get; private set; }

        public bool LanIpv6Reached { get; private set; }
        public string LanIpv6Remote { get; private set; }

        public bool WanIpv6Reached { get; private set; }
        public string WanIpv6Remote { get; private set; }

        public DateTime StartedAt { get; private set; }
        public DateTime? FinishedAt { get; set; }
        public string Error { get; set; }

        public ConnectivityTestSession(NetworkReport report, ConnectivityTestOptions options)
        {
            Report = report;
            Options = options;
            StartedAt = DateTime.Now;
            LanIpv4Remote = string.Empty;
            LanIpv6Remote = string.Empty;
            WanIpv6Remote = string.Empty;
            Error = string.Empty;
        }

        /// <summary>标记某个场景的二维码已经展示给用户，用于“扫了但没连上”的失败判断。</summary>
        public void MarkAttempted(ConnectivityTestMode mode)
        {
            if (mode == ConnectivityTestMode.Lan)
            {
                LanModeAttempted = true;
            }
            else
            {
                WanModeAttempted = true;
            }
        }

        /// <summary>把服务器收到的请求合并进会话。</summary>
        public void Apply(ConnectivityProbeEventArgs args)
        {
            if (args == null) return;

            bool wan = string.Equals(args.Mode, "wan", StringComparison.Ordinal);
            if (wan)
            {
                WanModeUsed = true;
                if (args.Kind == ConnectivityProbeKind.Page)
                {
                    WanPageReceived = true;
                    // 页面能加载本身就证明公网 IPv6 入站可达。
                    if (args.IsIpv6)
                    {
                        WanIpv6Reached = true;
                        WanIpv6Remote = args.RemoteAddress;
                    }
                }
                else if (args.Kind == ConnectivityProbeKind.Ipv6Probe)
                {
                    if (!args.IsIpv6) return;
                    WanIpv6Reached = true;
                    WanIpv6Remote = args.RemoteAddress;
                }
                return;
            }

            LanModeUsed = true;
            if (args.Kind == ConnectivityProbeKind.Page)
            {
                LanPageReceived = true;
                if (args.IsIpv6)
                {
                    LanIpv6Reached = true;
                    LanIpv6Remote = args.RemoteAddress;
                }
                else
                {
                    LanIpv4Reached = true;
                    LanIpv4Remote = args.RemoteAddress;
                }
            }
            else if (args.Kind == ConnectivityProbeKind.Ipv4Probe)
            {
                if (args.IsIpv6) return;
                LanIpv4Reached = true;
                LanIpv4Remote = args.RemoteAddress;
            }
            else if (args.Kind == ConnectivityProbeKind.Ipv6Probe)
            {
                if (!args.IsIpv6) return;
                LanIpv6Reached = true;
                LanIpv6Remote = args.RemoteAddress;
            }
        }

        public ConnectivityTestResult Evaluate()
        {
            return ConnectivityEvaluator.Evaluate(this);
        }
    }

    /// <summary>
    /// 根据本机地址和公网探测结果，为一次测试挑选 LAN IPv4 / LAN IPv6 / 公网 IPv6 地址。
    /// </summary>
    public static class ConnectivityTestTargets
    {
        public static ConnectivityTestOptions Create(NetworkReport report, bool bindAllInterfaces,
            string token)
        {
            ConnectivityTestOptions options = new ConnectivityTestOptions();
            options.BindAllInterfaces = bindAllInterfaces;
            options.Token = string.IsNullOrEmpty(token) ? CreateToken() : token;

            if (!bindAllInterfaces)
            {
                options.LanIpv4Address = "127.0.0.1";
                options.LanIpv4PrefixLength = 8;
                options.LanIpv6Address = "::1";
                options.LanIpv6PrefixLength = 128;
                options.PublicIpv6Address = "::1";
                return options;
            }

            if (report == null)
            {
                return options;
            }

            IpAddressEntry lan4 = FindLanIpv4(report);
            if (lan4 != null)
            {
                options.LanIpv4Address = lan4.Address;
                options.LanIpv4PrefixLength = lan4.PrefixLength;
            }

            IpAddressEntry lan6 = FindLanIpv6(report);
            if (lan6 != null)
            {
                options.LanIpv6Address = lan6.Address;
                options.LanIpv6PrefixLength = lan6.PrefixLength;
            }

            string public6 = report.GetPublicIpv6();
            if (!string.IsNullOrEmpty(public6))
            {
                options.PublicIpv6Address = IpPrivacy.StripScopeId(public6);
            }

            return options;
        }

        private static IpAddressEntry FindLanIpv4(NetworkReport report)
        {
            IpAddressEntry best = null;
            for (int i = 0; i < report.Ipv4Addresses.Count; i++)
            {
                IpAddressEntry entry = report.Ipv4Addresses[i];
                if (entry == null) continue;
                if (entry.Scope == IpAddressScope.LinkLocal) continue;
                if (entry.Scope == IpAddressScope.Loopback || entry.Scope == IpAddressScope.Multicast) continue;

                if (best == null || ScoreIpv4(entry) < ScoreIpv4(best))
                {
                    best = entry;
                }
            }
            return best;
        }

        private static int ScoreIpv4(IpAddressEntry entry)
        {
            int score = 0;
            if (entry.IsVirtual) score += 20;
            if (!entry.HasGateway) score += 8;
            if (entry.Scope == IpAddressScope.Public) score += 2;
            if (entry.Scope == IpAddressScope.SharedCgnat) score += 4;
            if (!entry.IsPrimary) score += 1;
            return score;
        }

        private static IpAddressEntry FindLanIpv6(NetworkReport report)
        {
            IpAddressEntry best = null;
            for (int i = 0; i < report.Ipv6Addresses.Count; i++)
            {
                IpAddressEntry entry = report.Ipv6Addresses[i];
                if (entry == null) continue;
                if (entry.Scope != IpAddressScope.Public && entry.Scope != IpAddressScope.UniqueLocal) continue;

                if (best == null || ScoreIpv6(entry) < ScoreIpv6(best))
                {
                    best = entry;
                }
            }
            return best;
        }

        private static int ScoreIpv6(IpAddressEntry entry)
        {
            int score = 0;
            if (entry.IsVirtual) score += 20;
            if (!entry.HasGateway) score += 8;
            if (entry.Scope == IpAddressScope.UniqueLocal) score += 4;
            if (!entry.IsPrimary) score += 1;
            return score;
        }

        private static string CreateToken()
        {
            return Guid.NewGuid().ToString("N").Substring(0, 16);
        }
    }

    /// <summary>把测试会话转换成界面展示用的 2x2 检查和 P2P 结论。</summary>
    public static class ConnectivityEvaluator
    {
        public static ConnectivityTestResult Evaluate(ConnectivityTestSession session)
        {
            ConnectivityTestResult result = new ConnectivityTestResult();
            if (session == null)
            {
                result.Error = "测试会话为空。";
                result.Verdict = "连通性测试失败。";
                result.Level = ConnectivityVerdictLevel.Bad;
                return result;
            }

            ConnectivityTestOptions options = session.Options ?? new ConnectivityTestOptions();
            NetworkReport report = session.Report;
            bool hasLan4 = !string.IsNullOrEmpty(options.LanIpv4Address);
            bool hasLan6 = !string.IsNullOrEmpty(options.LanIpv6Address);
            bool hasWan6 = !string.IsNullOrEmpty(options.PublicIpv6Address)
                && IpClassifier.Classify(options.PublicIpv6Address) == IpAddressScope.Public;

            ConnectivityCheckResult lan4 = new ConnectivityCheckResult();
            lan4.Key = "lan4";
            lan4.Title = "内网 IPv4";
            if (!hasLan4)
            {
                lan4.State = ConnectivityCheckState.NotApplicable;
                lan4.Detail = "未检测到可用的局域网 IPv4 地址。";
            }
            else if (session.LanIpv4Reached)
            {
                lan4.State = ConnectivityCheckState.Pass;
                lan4.Tested = true;
                lan4.Address = session.LanIpv4Remote;
                lan4.Detail = "手机来源 " + DisplayRemote(session.LanIpv4Remote) + " · 局域网 IPv4 可达";
            }
            else if (session.LanModeUsed)
            {
                lan4.State = ConnectivityCheckState.Fail;
                lan4.Tested = true;
                lan4.Address = options.LanIpv4Address;
                lan4.Detail = "已打开局域网页面，但没有收到 IPv4 探测请求。";
            }
            else if (session.LanModeAttempted)
            {
                lan4.State = ConnectivityCheckState.Fail;
                lan4.Tested = true;
                lan4.Address = options.LanIpv4Address;
                lan4.Detail = "已显示局域网二维码，但没有收到手机连接；可能不在同一 Wi-Fi、开启 AP 隔离或防火墙拦截。";
            }
            else
            {
                lan4.State = ConnectivityCheckState.Pending;
                lan4.Address = options.LanIpv4Address;
                lan4.Detail = "等待手机在同一 Wi-Fi 下扫码测试。";
            }
            result.Checks.Add(lan4);

            ConnectivityCheckResult lan6 = new ConnectivityCheckResult();
            lan6.Key = "lan6";
            lan6.Title = "内网 IPv6";
            if (!hasLan6)
            {
                lan6.State = ConnectivityCheckState.NotApplicable;
                lan6.Detail = "未检测到可用的局域网 IPv6 地址。";
            }
            else if (session.LanIpv6Reached)
            {
                lan6.State = ConnectivityCheckState.Pass;
                lan6.Tested = true;
                lan6.Address = session.LanIpv6Remote;
                bool sameSubnet = IpNetworkHelper.SameSubnet(session.LanIpv6Remote,
                    options.LanIpv6Address, options.LanIpv6PrefixLength);
                lan6.Detail = "手机来源 " + DisplayRemote(session.LanIpv6Remote)
                    + (sameSubnet ? " · 与本机同一 IPv6 网段" : " · IPv6 可达");
            }
            else if (session.LanModeUsed)
            {
                lan6.State = ConnectivityCheckState.Fail;
                lan6.Tested = true;
                lan6.Address = options.LanIpv6Address;
                lan6.Detail = "已打开局域网页面，但没有收到 IPv6 探测请求。";
            }
            else if (session.LanModeAttempted)
            {
                lan6.State = ConnectivityCheckState.Fail;
                lan6.Tested = true;
                lan6.Address = options.LanIpv6Address;
                lan6.Detail = "已显示局域网二维码，但没有收到手机连接；手机可能未启用 IPv6 或不在同一 Wi-Fi。";
            }
            else
            {
                lan6.State = ConnectivityCheckState.Pending;
                lan6.Address = options.LanIpv6Address;
                lan6.Detail = "等待手机在同一 Wi-Fi 下扫码测试。";
            }
            result.Checks.Add(lan6);

            ConnectivityCheckResult wan4 = new ConnectivityCheckResult();
            wan4.Key = "wan4";
            wan4.Title = "外网 IPv4";
            if (session.WanIpv4Probe == null)
            {
                wan4.State = ConnectivityCheckState.Testing;
                wan4.Detail = "正在测试公网 IPv4 出口…";
            }
            else if (session.WanIpv4Probe.Success)
            {
                wan4.Tested = true;
                wan4.Address = session.WanIpv4Probe.Endpoint;
                wan4.LatencyMilliseconds = session.WanIpv4Probe.LatencyMilliseconds;
                NatType natType = report != null && report.Nat != null ? report.Nat.Type : NatType.Unknown;
                string natLabel = report != null && report.Nat != null ? report.Nat.TypeLabel : "未知";
                string public4 = report != null && report.Nat != null ? report.Nat.ExternalIpv4 : string.Empty;
                bool cgnat = report != null && report.PrimaryIpv4 != null
                    && report.PrimaryIpv4.Scope == IpAddressScope.SharedCgnat;

                if (cgnat)
                {
                    wan4.State = ConnectivityCheckState.Warn;
                    wan4.Detail = "公网出口可用（" + session.WanIpv4Probe.LatencyMilliseconds
                        + " ms），但本机 IPv4 为运营商级 NAT（CGNAT），P2P 可能失败，优先尝试 IPv6。";
                }
                else if (natType == NatType.Symmetric)
                {
                    wan4.State = ConnectivityCheckState.Warn;
                    wan4.Detail = "公网出口可用（" + session.WanIpv4Probe.LatencyMilliseconds
                        + " ms），但 NAT 类型为对称型，P2P 可能需要中继。";
                }
                else if (natType == NatType.NoNat || natType == NatType.FullCone
                    || natType == NatType.RestrictedCone || natType == NatType.PortRestrictedCone)
                {
                    wan4.State = ConnectivityCheckState.Pass;
                    wan4.Detail = "公网出口可用（" + session.WanIpv4Probe.LatencyMilliseconds
                        + " ms），NAT 类型 " + natLabel + "，支持 P2P 打洞。";
                }
                else if (natType == NatType.ConeUnknown)
                {
                    wan4.State = ConnectivityCheckState.Pass;
                    wan4.Detail = "公网出口可用（" + session.WanIpv4Probe.LatencyMilliseconds
                        + " ms），NAT 映射为锥型（端点无关），P2P 打洞通常可行。";
                }
                else
                {
                    wan4.State = ConnectivityCheckState.Warn;
                    wan4.Detail = "公网出口可用（" + session.WanIpv4Probe.LatencyMilliseconds
                        + " ms），但 NAT 类型未知，P2P 是否可用需实际验证。";
                }

                if (string.IsNullOrEmpty(public4))
                {
                    wan4.State = ConnectivityCheckState.Warn;
                    wan4.Detail = "公网 TCP 出口可用，但未能通过 STUN 获取公网 IPv4 映射。";
                }
            }
            else
            {
                wan4.State = ConnectivityCheckState.Fail;
                wan4.Tested = true;
                wan4.Detail = "无法连接公网 IPv4：" + (string.IsNullOrEmpty(session.WanIpv4Probe.Error)
                    ? "未知错误" : session.WanIpv4Probe.Error);
            }
            result.Checks.Add(wan4);

            ConnectivityCheckResult wan6 = new ConnectivityCheckResult();
            wan6.Key = "wan6";
            wan6.Title = "外网 IPv6";
            if (!hasWan6)
            {
                wan6.State = ConnectivityCheckState.NotApplicable;
                wan6.Detail = "未检测到公网 IPv6 地址。";
            }
            else if (session.WanIpv6Reached)
            {
                wan6.State = ConnectivityCheckState.Pass;
                wan6.Tested = true;
                wan6.Address = session.WanIpv6Remote;
                wan6.Detail = "手机来源 " + DisplayRemote(session.WanIpv6Remote) + " · 公网 IPv6 入站可达";
            }
            else if (session.WanModeUsed)
            {
                wan6.State = ConnectivityCheckState.Fail;
                wan6.Tested = true;
                wan6.Address = options.PublicIpv6Address;
                wan6.Detail = "已用移动数据测试，但没有收到公网 IPv6 连接；可能被光猫/路由器 IPv6 防火墙或 Windows 防火墙拦截。";
            }
            else if (session.WanModeAttempted)
            {
                wan6.State = ConnectivityCheckState.Fail;
                wan6.Tested = true;
                wan6.Address = options.PublicIpv6Address;
                wan6.Detail = "已显示公网二维码，但没有收到手机连接；公网 IPv6 入站可能被防火墙或运营商拦截。";
            }
            else
            {
                wan6.State = ConnectivityCheckState.Pending;
                wan6.Address = options.PublicIpv6Address;
                wan6.Detail = "等待手机使用移动数据扫码测试。";
            }
            result.Checks.Add(wan6);

            bool wan4Done = session.WanIpv4Probe != null;
            bool phoneTestDone = session.LanModeUsed || session.WanModeUsed
                || session.LanModeAttempted || session.WanModeAttempted;
            bool wan6Done = !hasWan6 || session.WanIpv6Reached
                || session.WanModeUsed || session.WanModeAttempted;
            result.Complete = wan4Done && phoneTestDone && wan6Done;

            ComposeVerdict(result, session, lan4, lan6, wan4, wan6, hasWan6);
            result.Timestamp = DateTime.Now;
            return result;
        }

        private static void ComposeVerdict(ConnectivityTestResult result, ConnectivityTestSession session,
            ConnectivityCheckResult lan4, ConnectivityCheckResult lan6,
            ConnectivityCheckResult wan4, ConnectivityCheckResult wan6, bool hasWan6)
        {
            if (!string.IsNullOrEmpty(session.Error))
            {
                result.Level = ConnectivityVerdictLevel.Bad;
                result.Verdict = "连通性测试失败。";
                result.VerdictDetail = session.Error;
                return;
            }

            if (session.WanIpv4Probe == null)
            {
                result.Level = ConnectivityVerdictLevel.Unknown;
                result.Verdict = "正在测试公网出口…";
                result.VerdictDetail = "完成公网 IPv4 探测后，再用手机扫码验证内网 / 公网连通性。";
                return;
            }

            if (!session.LanModeUsed && !session.WanModeUsed
                && !session.LanModeAttempted && !session.WanModeAttempted)
            {
                result.Level = ConnectivityVerdictLevel.Unknown;
                result.Verdict = "等待手机扫码。";
                result.VerdictDetail = "请用手机扫描二维码：同一 Wi-Fi 选内网模式，移动数据选公网模式。";
                return;
            }

            if (hasWan6 && session.WanIpv6Reached)
            {
                result.Level = ConnectivityVerdictLevel.Good;
                result.Verdict = "IPv6 公网可直连，P2P 可用。";
                result.VerdictDetail = "外网 IPv6 入站已验证；若对端也支持 IPv6，可直接建立 P2P，无需 NAT 穿透。";
                result.P2pPossible = true;
                result.Ipv6Direct = true;
                return;
            }

            if (wan4.State == ConnectivityCheckState.Pass)
            {
                if (hasWan6 && !session.WanModeUsed && !session.WanModeAttempted)
                {
                    result.Level = ConnectivityVerdictLevel.Warn;
                    result.Verdict = "IPv4 公网出口可用，但公网 IPv6 尚未验证。";
                    result.VerdictDetail = "建议用移动数据扫码完成 IPv6 入站测试；若只使用 IPv4，P2P 仍取决于 NAT 打洞。";
                    result.P2pPossible = true;
                    result.Ipv4P2pLikely = true;
                    return;
                }

                if (hasWan6 && (session.WanModeUsed || session.WanModeAttempted))
                {
                    result.Level = ConnectivityVerdictLevel.Warn;
                    result.Verdict = "IPv4 公网出口可用，但 IPv6 入站未通过。";
                    result.VerdictDetail = "可尝试 IPv4 NAT 打洞；若对端只支持 IPv6，则无法直连。";
                    result.P2pPossible = true;
                    result.Ipv4P2pLikely = true;
                    return;
                }

                result.Level = ConnectivityVerdictLevel.Good;
                result.Verdict = "IPv4 公网出口可用，P2P 可能建立。";
                result.VerdictDetail = "NAT 类型允许打洞；实际能否连上还取决于对端网络和路由器实现。";
                result.P2pPossible = true;
                result.Ipv4P2pLikely = true;
                return;
            }

            if (wan4.State == ConnectivityCheckState.Warn && session.WanIpv4Probe != null
                && session.WanIpv4Probe.Success)
            {
                result.Level = ConnectivityVerdictLevel.Warn;
                result.Verdict = "IPv4 出口可用，但 P2P 条件不理想。";
                result.VerdictDetail = "对称 NAT 或 NAT 类型未知时，打洞可能失败，通常需要中继服务器。";
                result.P2pPossible = false;
                result.Ipv4P2pLikely = false;
                return;
            }

            // 到这里说明 IPv4 外网失败。
            if (!hasWan6)
            {
                result.Level = ConnectivityVerdictLevel.Bad;
                result.Verdict = "无法建立有效 P2P 连接。";
                result.VerdictDetail = "IPv4 外网不可用，且没有公网 IPv6；设备之间缺少可用的直连路径。";
                return;
            }

            if ((session.WanModeUsed || session.WanModeAttempted)
                && wan6.State == ConnectivityCheckState.Fail)
            {
                result.Level = ConnectivityVerdictLevel.Bad;
                result.Verdict = "无法建立有效 P2P 连接。";
                result.VerdictDetail = "IPv4 外网不可用，IPv6 入站也被拦截；建议检查防火墙、光猫 IPv6 防火墙或更换网络。";
                return;
            }

            result.Level = ConnectivityVerdictLevel.Warn;
            result.Verdict = "IPv4 外网不可用，IPv6 尚未验证。";
            result.VerdictDetail = "请用移动数据扫码完成公网 IPv6 入站测试后再判断 P2P。";
        }

        private static string DisplayRemote(string address)
        {
            if (string.IsNullOrEmpty(address)) return "未知地址";
            return address;
        }
    }
}
