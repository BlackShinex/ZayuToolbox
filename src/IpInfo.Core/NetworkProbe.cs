using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace IpInfo.Core
{
    /// <summary>
    /// 网络探测入口。负责组合“本机地址枚举 + 公网 IP 查询 + NAT 类型检测”，
    /// 返回一份完整的 <see cref="NetworkReport"/>。所有网络操作都在调用线程同步执行，
    /// UI 层应放到后台线程中调用。
    /// </summary>
    public static class NetworkProbe
    {
        private const int Ipv6BudgetMs = 3000;
        private const int Ipv6DnsTimeoutMs = 800;
        private const int Ipv6QueryTimeoutMs = 1200;

        /// <summary>
        /// 执行一次完整探测。
        /// </summary>
        /// <param name="progress">进度回调，可为 null。会在探测线程上被调用。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>完整探测结果，不应为 null；网络失败时通过报告中的 Error / Nat.Error 说明。</returns>
        public static NetworkReport Collect(Action<string> progress, CancellationToken cancellationToken)
        {
            NetworkReport report = new NetworkReport();
            Stopwatch stopwatch = Stopwatch.StartNew();
            List<string> errors = new List<string>();

            try
            {
                // ---- 1. 枚举本机地址 ----
                Report(progress, "正在枚举网卡…");
                report.Ipv4Addresses.AddRange(LocalAddressProvider.GetIpv4Addresses());
                report.Ipv6Addresses.AddRange(LocalAddressProvider.GetIpv6Addresses());
                if (report.Ipv4Addresses.Count == 0 && report.Ipv6Addresses.Count == 0)
                {
                    errors.Add("未枚举到可用的本机 IP 地址。");
                }

                cancellationToken.ThrowIfCancellationRequested();

                // ---- 2. Windows 防火墙状态（失败不影响主报告） ----
                Report(progress, "正在读取防火墙状态…");
                try
                {
                    FirewallInfo firewall = WindowsFirewallReader.Read();
                    report.Firewall = firewall != null ? firewall : new FirewallInfo();
                }
                catch (Exception ex)
                {
                    FirewallInfo firewall = new FirewallInfo();
                    firewall.IsSupported = false;
                    firewall.Error = "读取 Windows 防火墙状态失败：" + ex.GetType().Name + " " + ex.Message;
                    report.Firewall = firewall;
                }

                cancellationToken.ThrowIfCancellationRequested();

                // ---- 4. 公网 IPv4 + NAT 类型 ----
                Report(progress, "正在查询公网 IP（STUN）…");
                NatResult nat;
                try
                {
                    nat = NatDetector.Detect(progress, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    nat = new NatResult();
                    nat.Error = "NAT 检测失败：" + ex.GetType().Name + " " + ex.Message;
                }
                report.Nat = nat;
                FillIpv4Info(report, nat);
                if (!string.IsNullOrEmpty(nat.Error))
                {
                    errors.Add(nat.Error);
                }

                cancellationToken.ThrowIfCancellationRequested();

                // ---- 5. 公网 IPv6（失败静默） ----
                Report(progress, "正在检测 IPv6 公网地址…");
                FillIpv6Info(report, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // 取消是控制流，原样交给 UI 处理。
                throw;
            }
            catch (Exception ex)
            {
                errors.Add("探测异常：" + ex.GetType().Name + " " + ex.Message);
            }
            finally
            {
                stopwatch.Stop();
                report.Timestamp = DateTime.Now;
                report.Elapsed = stopwatch.Elapsed;

                string errorText = string.Empty;
                if (errors.Count > 0)
                {
                    errorText = string.Join("；", errors.ToArray());
                }
                report.Error = errorText;

                try
                {
                    ComposeSummary(report);
                }
                catch
                {
                    // 摘要生成失败不应影响报告本身。
                }

                if (report.PublicIp != null && string.IsNullOrEmpty(report.PublicIp.Error))
                {
                    report.PublicIp.Error = errorText;
                }
            }

            return report;
        }

        private static void FillIpv4Info(NetworkReport report, NatResult nat)
        {
            PublicIpInfo pub = report.PublicIp;
            if (pub == null)
            {
                return;
            }

            if (nat != null && !string.IsNullOrEmpty(nat.ExternalIpv4))
            {
                pub.ExternalIpv4 = nat.ExternalIpv4;
                pub.ExternalIpv4Port = nat.ExternalIpv4Port;
                if (ContainsAddress(report.Ipv4Addresses, nat.ExternalIpv4))
                {
                    pub.Ipv4Status = PublicIpStatus.NativePublic;
                }
                else
                {
                    pub.Ipv4Status = PublicIpStatus.BehindNat;
                }
                return;
            }

            // STUN 不可用时，按主 IPv4 的分类推断。
            IpAddressEntry primary = report.PrimaryIpv4;
            if (primary == null)
            {
                pub.Ipv4Status = PublicIpStatus.NotAvailable;
                return;
            }

            switch (primary.Scope)
            {
                case IpAddressScope.Public:
                    pub.Ipv4Status = PublicIpStatus.NativePublic;
                    break;
                case IpAddressScope.Private:
                case IpAddressScope.SharedCgnat:
                    pub.Ipv4Status = PublicIpStatus.BehindNat;
                    break;
                default:
                    pub.Ipv4Status = PublicIpStatus.NotAvailable;
                    break;
            }
        }

        private static void FillIpv6Info(NetworkReport report, CancellationToken cancellationToken)
        {
            PublicIpInfo pub = report.PublicIp;
            if (pub == null)
            {
                return;
            }

            string localPublicIpv6 = FindLocalPublicIpv6(report);
            string stunExternalIpv6 = ProbeIpv6External(cancellationToken);

            if (!string.IsNullOrEmpty(stunExternalIpv6))
            {
                pub.ExternalIpv6 = stunExternalIpv6;
                pub.HasPublicIpv6 = true;
                pub.Ipv6Status = PublicIpStatus.NativePublic;
                return;
            }

            if (!string.IsNullOrEmpty(localPublicIpv6))
            {
                pub.ExternalIpv6 = localPublicIpv6;
                pub.HasPublicIpv6 = true;
                pub.Ipv6Status = PublicIpStatus.NativePublic;
                return;
            }

            pub.HasPublicIpv6 = false;
            pub.Ipv6Status = PublicIpStatus.NotAvailable;
        }

        /// <summary>
        /// 用 IPv6 STUN 服务器获取公网 IPv6（总耗时预算 ≤ 3 秒，失败静默）。
        /// </summary>
        private static string ProbeIpv6External(CancellationToken cancellationToken)
        {
            Stopwatch budget = Stopwatch.StartNew();
            List<StunServer> servers;
            try
            {
                servers = StunServers.Resolve(AddressFamily.InterNetworkV6, null, Ipv6DnsTimeoutMs);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return null;
            }

            if (servers.Count == 0 || budget.ElapsedMilliseconds >= Ipv6BudgetMs)
            {
                return null;
            }

            StunProbe probe = null;
            try
            {
                probe = new StunProbe(AddressFamily.InterNetworkV6);
                for (int i = 0; i < servers.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    long remaining = Ipv6BudgetMs - budget.ElapsedMilliseconds;
                    if (remaining < 150)
                    {
                        break;
                    }

                    int timeout = (int)Math.Min(remaining, Ipv6QueryTimeoutMs);
                    StunResponse response = probe.Query(servers[i].GetEndPoint(), timeout,
                        StunChangeRequest.None, cancellationToken);
                    if (response != null && response.Success && response.Mapped != null
                        && response.Mapped.AddressFamily == AddressFamily.InterNetworkV6)
                    {
                        string text = response.Mapped.Address.ToString();
                        if (!string.IsNullOrEmpty(text))
                        {
                            return text;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // IPv6 探测失败不需要打扰用户。
            }
            finally
            {
                if (probe != null)
                {
                    probe.Dispose();
                }
            }
            return null;
        }

        private static string FindLocalPublicIpv6(NetworkReport report)
        {
            for (int i = 0; i < report.Ipv6Addresses.Count; i++)
            {
                IpAddressEntry entry = report.Ipv6Addresses[i];
                if (entry != null && entry.Scope == IpAddressScope.Public
                    && !string.IsNullOrEmpty(entry.Address))
                {
                    return entry.Address;
                }
            }
            return null;
        }

        private static bool ContainsAddress(List<IpAddressEntry> addresses, string address)
        {
            if (string.IsNullOrEmpty(address))
            {
                return false;
            }
            for (int i = 0; i < addresses.Count; i++)
            {
                IpAddressEntry entry = addresses[i];
                if (entry != null && string.Equals(entry.Address, address, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static void ComposeSummary(NetworkReport report)
        {
            PublicIpInfo pub = report.PublicIp;
            if (pub == null)
            {
                return;
            }

            StringBuilder summary = new StringBuilder();
            bool hasExternalIpv4 = !string.IsNullOrEmpty(pub.ExternalIpv4);

            if (hasExternalIpv4)
            {
                if (pub.Ipv4Status == PublicIpStatus.NativePublic)
                {
                    summary.Append("当前 IPv4 为公网地址（直连）。");
                    if (pub.HasPublicIpv6)
                    {
                        summary.Append("；IPv6 为公网直连。");
                    }
                }
                else
                {
                    summary.Append("IPv4 位于 NAT 后（出口 ");
                    summary.Append(pub.ExternalIpv4);
                    summary.Append("）；");
                    summary.Append(pub.HasPublicIpv6 ? "IPv6 为公网直连。" : "未检测到公网 IPv6。");
                }
            }
            else if (pub.Ipv4Status == PublicIpStatus.BehindNat)
            {
                summary.Append("无法获取公网出口（STUN 不可用）；");

                IpAddressEntry primary = report.PrimaryIpv4;
                if (primary != null && primary.Scope == IpAddressScope.SharedCgnat)
                {
                    summary.Append("本机 IPv4 为运营商级 NAT（CGNAT）地址，可能位于多层 NAT 后（推断）。");
                }
                else
                {
                    summary.Append("本机 IPv4 为私有地址，可能位于 NAT 后（推断）。");
                }

                if (pub.HasPublicIpv6)
                {
                    summary.Append("IPv6 为公网直连。");
                }
            }
            else if (pub.Ipv4Status == PublicIpStatus.NativePublic)
            {
                summary.Append("无法获取公网出口（STUN 不可用）；本机 IPv4 为公网地址，可能直连。");
                if (pub.HasPublicIpv6)
                {
                    summary.Append("IPv6 为公网直连。");
                }
            }
            else
            {
                summary.Append("无法获取公网出口（STUN 不可用）；未检测到可用的 IPv4 地址。");
                if (pub.HasPublicIpv6)
                {
                    summary.Append("IPv6 为公网直连。");
                }
            }

            pub.Summary = summary.ToString();
        }

        private static void Report(Action<string> progress, string message)
        {
            if (progress != null)
            {
                progress(message);
            }
        }
    }
}
