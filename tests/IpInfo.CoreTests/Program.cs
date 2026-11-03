using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using IpInfo.Core;

namespace IpInfo.CoreTests
{
    /// <summary>
    /// 无界面的核心逻辑自测程序。用法：
    ///   IpInfo.CoreTests.exe [--live]
    ///   IpInfo.CoreTests.exe --sunshine [安装目录]
    /// 默认只运行离线测试（隐私遮罩、地址分类、本机地址枚举）；
    /// 加 --live 时额外执行一次真实的 STUN / NAT 探测；
    /// 加 --sunshine 时只输出 Sunshine 版本自检，可用于检查任意安装目录。
    /// </summary>
    internal static class Program
    {
        private static int _checks;
        private static int _failures;

        private static int Main(string[] args)
        {
            bool live = Array.IndexOf(args, "--live") >= 0;

            Console.WriteLine("=== IpInfo.Core 核心逻辑自测 ===");
            Console.WriteLine();

            int sunshineIndex = Array.IndexOf(args, "--sunshine");
            if (sunshineIndex >= 0)
            {
                string root = sunshineIndex + 1 < args.Length && !args[sunshineIndex + 1].StartsWith("--", StringComparison.Ordinal)
                    ? args[sunshineIndex + 1]
                    : string.Empty;

                SunshineDetectionResult single = string.IsNullOrEmpty(root)
                    ? SunshineDetector.Detect()
                    : SunshineDetector.Detect(root);

                Console.WriteLine(single.ToDiagnostics());
                return 0;
            }

            TestPrivacyMask();
            TestClassifier();
            TestLocalAddresses();
            TestVirtualAdapters();
            TestFirewallReader();
            TestConnectivityTestServer();
            TestIpNetworkHelper();
            TestConnectivityEvaluator();
            TestWebView2();
            TestSunshineClassifier();
            TestSunshineDetection();
            TestSunshineService();
            TestZakoDisplay();

            if (live)
            {
                RunLiveProbeTests();
            }
            else
            {
                Console.WriteLine();
                Console.WriteLine("[跳过] 联网测试（加 --live 运行 STUN / NAT 探测）");
            }

            Console.WriteLine();
            Console.WriteLine("--------------------------------------------");
            Console.WriteLine("共检查 {0} 项，失败 {1} 项。", _checks, _failures);
            Console.WriteLine("--------------------------------------------");
            return _failures == 0 ? 0 : 1;
        }

        private static void TestPrivacyMask()
        {
            Console.WriteLine("[1] IP 隐私遮罩（只遮公网）");
            CheckEqual("局域网 IPv4 不遮罩 192.168.1.100", IpPrivacy.Mask("192.168.1.100"), "192.168.1.100");
            CheckEqual("局域网 IPv4 不遮罩 10.0.0.5", IpPrivacy.Mask("10.0.0.5"), "10.0.0.5");
            CheckEqual("局域网 IPv4 不遮罩 172.16.5.9", IpPrivacy.Mask("172.16.5.9"), "172.16.5.9");
            CheckEqual("CGNAT IPv4 不遮罩", IpPrivacy.Mask("100.64.0.1"), "100.64.0.1");
            CheckEqual("公网 IPv4 遮罩 125.113.211.223", IpPrivacy.Mask("125.113.211.223"), "125.*.*.223");
            CheckEqual("公网 IPv4 遮罩 8.8.8.8", IpPrivacy.Mask("8.8.8.8"), "8.*.*.8");
            CheckEqual(
                "公网 IPv6 遮罩 2400:3200:1234:5678:9abc:def0:1111:2222",
                IpPrivacy.Mask("2400:3200:1234:5678:9abc:def0:1111:2222"),
                "2400:****:****:****:****:****:****:2222");
            CheckEqual("链路本地 IPv6 不遮罩", IpPrivacy.Mask("fe80::1%18"), "fe80::1%18");
            CheckEqual("唯一本地 IPv6 不遮罩", IpPrivacy.Mask("fd12:3456:789a::1"), "fd12:3456:789a::1");
            CheckEqual("空字符串安全", IpPrivacy.Mask(""), "");
        }

        private static void TestClassifier()
        {
            Console.WriteLine();
            Console.WriteLine("[2] IP 地址分类");
            CheckScope("192.168.1.1", IpAddressScope.Private);
            CheckScope("10.1.2.3", IpAddressScope.Private);
            CheckScope("172.16.0.1", IpAddressScope.Private);
            CheckScope("172.31.255.254", IpAddressScope.Private);
            CheckScope("172.32.0.1", IpAddressScope.Public);
            CheckScope("100.64.0.1", IpAddressScope.SharedCgnat);
            CheckScope("100.127.255.255", IpAddressScope.SharedCgnat);
            CheckScope("100.128.0.1", IpAddressScope.Public);
            CheckScope("169.254.10.20", IpAddressScope.LinkLocal);
            CheckScope("127.0.0.1", IpAddressScope.Loopback);
            CheckScope("0.0.0.0", IpAddressScope.Unspecified);
            CheckScope("224.0.0.251", IpAddressScope.Multicast);
            CheckScope("240.0.0.1", IpAddressScope.Reserved);
            CheckScope("8.8.8.8", IpAddressScope.Public);
            CheckScope("1.1.1.1", IpAddressScope.Public);

            CheckScope("fe80::1", IpAddressScope.LinkLocal);
            CheckScope("fc00::1", IpAddressScope.UniqueLocal);
            CheckScope("fd12:3456:789a::1", IpAddressScope.UniqueLocal);
            CheckScope("2001:4860:4864:5:8000::1", IpAddressScope.Public);
            CheckScope("2606:4700:4700::1111", IpAddressScope.Public);
            CheckScope("::1", IpAddressScope.Loopback);
            CheckScope("ff02::1", IpAddressScope.Multicast);
            CheckScope("2001:db8::1", IpAddressScope.Reserved);

            CheckEqual("公网标签", IpClassifier.GetScopeLabel(IpAddressScope.Public), "公网");
            CheckEqual("私有标签", IpClassifier.GetScopeLabel(IpAddressScope.Private), "私有");
            CheckEqual("非法地址", IpClassifier.GetScopeLabel(IpClassifier.Classify("not-an-ip")), "未知");
        }

        private static void TestLocalAddresses()
        {
            Console.WriteLine();
            Console.WriteLine("[3] 本机地址枚举");
            List<IpAddressEntry> v4 = LocalAddressProvider.GetIpv4Addresses();
            List<IpAddressEntry> v6 = LocalAddressProvider.GetIpv6Addresses();

            Console.WriteLine("    IPv4: " + Describe(v4));
            Console.WriteLine("    IPv6: " + Describe(v6));

            CheckTrue("至少检测到一个 IPv4 地址", v4.Count > 0, "可能是网卡全部断开");
            CheckTrue("IPv4 主地址非空", v4.Count > 0 && v4[0].IsPrimary, "主地址标记缺失");
            CheckTrue("IPv4 地址文本非空", v4.Count == 0 || AllHaveText(v4), "存在空地址");
            CheckTrue("IPv6 地址文本非空", v6.Count == 0 || AllHaveText(v6), "存在空地址");
            CheckTrue("IPv6 不含环回地址", !Contains(v6, "::1"), "不应出现 ::1");
            CheckTrue("IPv4 不含环回地址", !Contains(v4, "127.0.0.1"), "不应出现 127.0.0.1");
        }

        private static bool Contains(List<IpAddressEntry> list, string address)
        {
            foreach (IpAddressEntry e in list)
            {
                if (string.Equals(e.Address, address, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static bool AllHaveText(List<IpAddressEntry> list)
        {
            foreach (IpAddressEntry e in list)
            {
                if (string.IsNullOrEmpty(e.Address)) return false;
                if (string.IsNullOrEmpty(e.ScopeLabel)) return false;
                if (string.IsNullOrEmpty(e.InterfaceName)) return false;
            }
            return true;
        }

        private static string Describe(List<IpAddressEntry> list)
        {
            if (list.Count == 0) return "（无）";
            List<string> parts = new List<string>();
            foreach (IpAddressEntry e in list)
            {
                parts.Add(e.Address + " [" + e.ScopeLabel + ", " + e.InterfaceName + (e.IsPrimary ? ", 主" : "") + "]");
            }
            return string.Join(" | ", parts.ToArray());
        }

        private static void RunLiveProbeTests()
        {
            Console.WriteLine();
            Console.WriteLine("[14] 联网探测（STUN / NAT / 防火墙）");
            try
            {
                NetworkReport report = NetworkProbe.Collect(delegate(string message)
                {
                    Console.WriteLine("    ... " + message);
                }, System.Threading.CancellationToken.None);

                CheckTrue("公网出口 IPv4 已获取", !string.IsNullOrEmpty(report.PublicIp.ExternalIpv4), report.Nat.Error);
                CheckTrue("NAT 类型已判定", report.Nat.Type != NatType.Unknown || !string.IsNullOrEmpty(report.Nat.Error), "未知结果");
                CheckTrue("包含检测步骤日志", report.Nat.Steps.Count > 0, "无日志");
                CheckTrue("报告包含防火墙状态", report.Firewall != null, "为空");
                CheckTrue("GetPublicIpv6 可返回公网 IPv6", !string.IsNullOrEmpty(report.GetPublicIpv6()));

                Console.WriteLine("    公网 IPv4: " + report.PublicIp.ExternalIpv4);
                Console.WriteLine("    公网 IPv6: " + (string.IsNullOrEmpty(report.PublicIp.ExternalIpv6) ? "（无）" : report.PublicIp.ExternalIpv6));
                Console.WriteLine("    NAT 类型: " + report.Nat.TypeLabel);
                Console.WriteLine("    防火墙: " + DescribeFirewall(report.Firewall));
                Console.WriteLine("    说明: " + report.PublicIp.Summary);
            }
            catch (Exception ex)
            {
                CheckTrue("联网探测未抛出异常", false, ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void TestVirtualAdapters()
        {
            Console.WriteLine();
            Console.WriteLine("[4] 虚拟网卡识别与主地址选择");
            CheckTrue("识别 VMware 网卡", LocalAddressProvider.LooksVirtualInterface(
                "VMware Network Adapter VMnet8", "VMware Virtual Ethernet Adapter for VMnet8",
                "Ethernet", "00:50:56:C0:00:08"), "未识别");
            CheckTrue("识别 Hyper-V / WSL 网卡", LocalAddressProvider.LooksVirtualInterface(
                "vEthernet (WSL)", "Hyper-V Virtual Ethernet Adapter",
                "Ethernet", "00:15:5D:12:34:56"), "未识别");
            CheckTrue("识别 VirtualBox 网卡", LocalAddressProvider.LooksVirtualInterface(
                "VirtualBox Host-Only Network", "VirtualBox Host-Only Ethernet Adapter",
                "Ethernet", "0A:00:27:00:00:01"), "未识别");
            CheckTrue("识别 Tailscale 网卡", LocalAddressProvider.LooksVirtualInterface(
                "Tailscale", "Tailscale Tunnel", "Ethernet", ""), "未识别");
            CheckTrue("识别蓝牙网络连接", LocalAddressProvider.LooksVirtualInterface(
                "蓝牙网络连接", "Bluetooth Device (Personal Area Network)",
                "Ethernet", "00:11:22:33:44:55"), "未识别");
            CheckTrue("不误判真实有线网卡", !LocalAddressProvider.LooksVirtualInterface(
                "以太网", "Realtek PCIe GbE Family Controller",
                "Ethernet", "00:1A:2B:3C:4D:5E"), "被误判为虚拟网卡");
            CheckTrue("不误判真实无线网卡", !LocalAddressProvider.LooksVirtualInterface(
                "WLAN", "Intel(R) Wi-Fi 6E AX211 160MHz",
                "Wireless80211", "A0:36:BC:12:34:56"), "被误判为虚拟网卡");
            CheckTrue("不误判随机 MAC 的真实无线网卡", !LocalAddressProvider.LooksVirtualInterface(
                "WLAN", "Intel(R) Wi-Fi 6E AX211 160MHz",
                "Wireless80211", "02:11:22:33:44:55"), "随机硬件地址导致误判");

            List<IpAddressEntry> v4 = LocalAddressProvider.GetIpv4Addresses();
            IpAddressEntry primary4 = null;
            for (int i = 0; i < v4.Count; i++)
            {
                if (v4[i].IsPrimary) { primary4 = v4[i]; break; }
            }
            CheckTrue("IPv4 主地址存在", primary4 != null);

            bool hasPhysicalCandidate = false;
            for (int i = 0; i < v4.Count; i++)
            {
                if (!v4[i].IsVirtual && v4[i].Scope != IpAddressScope.LinkLocal)
                {
                    hasPhysicalCandidate = true;
                    break;
                }
            }
            CheckTrue("IPv4 主地址不是虚拟网卡（存在物理候选时）",
                !hasPhysicalCandidate || primary4 == null || !primary4.IsVirtual,
                primary4 == null ? "" : primary4.InterfaceName + "（CI/VM 环境可能只有虚拟网卡）");
            CheckTrue("IPv4 主地址有接口索引", primary4 == null || primary4.InterfaceIndex >= 0);
        }

        private static void TestFirewallReader()
        {
            Console.WriteLine();
            Console.WriteLine("[5] Windows 防火墙状态");
            FirewallInfo info = WindowsFirewallReader.Read();
            CheckTrue("返回防火墙状态对象", info != null);
            if (info != null)
            {
                Console.WriteLine("    " + DescribeFirewall(info));
            }
        }

        private static void TestConnectivityTestServer()
        {
            Console.WriteLine();
            Console.WriteLine("[6] 网络连通性测试服务器（回环模式）");
            ConnectivityTestServer server = new ConnectivityTestServer();
            List<ConnectivityProbeEventArgs> events = new List<ConnectivityProbeEventArgs>();
            server.ProbeReceived += delegate(object sender, ConnectivityProbeEventArgs e)
            {
                lock (events) { events.Add(e); }
            };

            try
            {
                ConnectivityTestOptions options = new ConnectivityTestOptions();
                options.LanIpv4Address = "127.0.0.1";
                options.LanIpv4PrefixLength = 8;
                options.LanIpv6Address = "::1";
                options.LanIpv6PrefixLength = 128;
                options.PublicIpv6Address = "::1";
                options.BindAllInterfaces = false;
                options.Token = "testtoken";
                server.Start(options);

                CheckTrue("已分配临时端口", server.Port > 0);
                CheckTrue("IPv4 测试地址格式正确",
                    server.LanIpv4Url != null && server.LanIpv4Url.StartsWith("http://127.0.0.1:", StringComparison.Ordinal));
                CheckTrue("IPv6 测试地址格式正确",
                    server.LanIpv6Url != null && server.LanIpv6Url.StartsWith("http://[::1]:", StringComparison.Ordinal));
                CheckTrue("公网测试地址格式正确",
                    server.WanUrl != null && server.WanUrl.StartsWith("http://[::1]:", StringComparison.Ordinal));

                string contentType;
                string page = HttpGet(server.LanUrl, out contentType);
                CheckTrue("局域网页面返回 HTML", page.IndexOf("内网 IPv4", StringComparison.Ordinal) >= 0
                    && page.IndexOf("内网 IPv6", StringComparison.Ordinal) >= 0);
                CheckTrue("页面包含 IPv4 探测链接", page.IndexOf("/lan/p4", StringComparison.Ordinal) >= 0);
                CheckTrue("页面包含 IPv6 探测链接", page.IndexOf("/lan/p6", StringComparison.Ordinal) >= 0);

                CheckTrue("IPv4 探测返回图片", HttpGetStatus(server.LanIpv4Url + "p4") == 200);
                CheckTrue("IPv6 探测返回图片", HttpGetStatus(server.LanIpv6Url + "p6") == 200);

                string wanPage = HttpGet(server.WanUrl, out contentType);
                CheckTrue("公网页面只测试公网 IPv6", wanPage.IndexOf("公网 IPv6", StringComparison.Ordinal) >= 0
                    && wanPage.IndexOf("内网 IPv4", StringComparison.Ordinal) < 0);
                CheckTrue("公网 IPv6 探测返回图片", HttpGetStatus(server.WanUrl + "p6") == 200);

                Thread.Sleep(500);
                lock (events)
                {
                    bool gotPage = false;
                    bool gotIpv4 = false;
                    bool gotIpv6 = false;
                    bool gotWanIpv6 = false;
                    for (int i = 0; i < events.Count; i++)
                    {
                        ConnectivityProbeEventArgs item = events[i];
                        if (item.Kind == ConnectivityProbeKind.Page && item.Mode == "lan") gotPage = true;
                        if (item.Kind == ConnectivityProbeKind.Ipv4Probe && item.Mode == "lan" && !item.IsIpv6) gotIpv4 = true;
                        if (item.Kind == ConnectivityProbeKind.Ipv6Probe && item.Mode == "lan" && item.IsIpv6) gotIpv6 = true;
                        if (item.Kind == ConnectivityProbeKind.Ipv6Probe && item.Mode == "wan" && item.IsIpv6) gotWanIpv6 = true;
                    }
                    CheckTrue("记录局域网页面请求", gotPage);
                    CheckTrue("记录内网 IPv4 探测", gotIpv4);
                    CheckTrue("记录内网 IPv6 探测", gotIpv6);
                    CheckTrue("记录公网 IPv6 探测", gotWanIpv6);
                }
            }
            catch (Exception ex)
            {
                CheckTrue("连通性测试服务器工作正常", false, ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                try { server.Stop(); }
                catch { }
                try { server.Dispose(); }
                catch { }
            }
        }

        private static void TestIpNetworkHelper()
        {
            Console.WriteLine();
            Console.WriteLine("[7] 网段判断");
            CheckTrue("IPv4 同 /24 网段",
                IpNetworkHelper.SameSubnet("192.168.1.23", "192.168.1.10", 24));
            CheckTrue("IPv4 不同 /24 网段",
                !IpNetworkHelper.SameSubnet("192.168.2.23", "192.168.1.10", 24));
            CheckTrue("IPv4 /16 网段",
                IpNetworkHelper.SameSubnet("172.16.5.9", "172.16.1.1", 16));
            CheckTrue("IPv6 同 /64 网段",
                IpNetworkHelper.SameSubnet("240e:1:2:3::23", "240e:1:2:3::10", 64));
            CheckTrue("IPv6 不同 /64 网段",
                !IpNetworkHelper.SameSubnet("240e:1:2:4::23", "240e:1:2:3::10", 64));
            CheckTrue("地址族不同返回 false",
                !IpNetworkHelper.SameSubnet("192.168.1.1", "240e::1", 24));
        }

        private static void TestConnectivityEvaluator()
        {
            Console.WriteLine();
            Console.WriteLine("[8] 连通性结论（P2P 判断）");

            NetworkReport report = new NetworkReport();
            report.Nat.Type = NatType.FullCone;
            report.Nat.TypeLabel = "全锥型 NAT";
            report.Nat.ExternalIpv4 = "203.0.113.9";

            ConnectivityTestOptions options = new ConnectivityTestOptions();
            options.LanIpv4Address = "192.168.1.10";
            options.LanIpv4PrefixLength = 24;
            options.LanIpv6Address = "240e:1:2:3::10";
            options.LanIpv6PrefixLength = 64;
            options.PublicIpv6Address = "240e:1:2:3::10";

            ConnectivityTestSession session = new ConnectivityTestSession(report, options);
            session.WanIpv4Probe = new TcpConnectivityResult();
            session.WanIpv4Probe.Success = true;
            session.WanIpv4Probe.Endpoint = "223.5.5.5:443";
            session.WanIpv4Probe.LatencyMilliseconds = 30;

            session.Apply(MakeProbe(ConnectivityProbeKind.Page, false, "lan", "192.168.1.23"));
            session.Apply(MakeProbe(ConnectivityProbeKind.Ipv6Probe, true, "lan", "240e:1:2:3::23"));
            session.Apply(MakeProbe(ConnectivityProbeKind.Page, true, "wan", "240e:9:9:9::99"));

            ConnectivityTestResult allPass = session.Evaluate();
            CheckTrue("IPv6 直连优先判定为 P2P 可用",
                allPass.Level == ConnectivityVerdictLevel.Good && allPass.P2pPossible && allPass.Ipv6Direct,
                allPass.Level + " / " + allPass.Verdict);
            CheckTrue("测试完成", allPass.Complete);
            CheckTrue("四项检查齐全", allPass.Checks.Count == 4);
            CheckTrue("内网 IPv4 通过",
                allPass.Find("lan4") != null && allPass.Find("lan4").State == ConnectivityCheckState.Pass);
            CheckTrue("内网 IPv6 通过",
                allPass.Find("lan6") != null && allPass.Find("lan6").State == ConnectivityCheckState.Pass);
            CheckTrue("外网 IPv4 通过",
                allPass.Find("wan4") != null && allPass.Find("wan4").State == ConnectivityCheckState.Pass);
            CheckTrue("外网 IPv6 通过",
                allPass.Find("wan6") != null && allPass.Find("wan6").State == ConnectivityCheckState.Pass);

            report.Nat.Type = NatType.Symmetric;
            report.Nat.TypeLabel = "对称型 NAT";
            ConnectivityTestSession symmetric = new ConnectivityTestSession(report, options);
            symmetric.WanIpv4Probe = new TcpConnectivityResult();
            symmetric.WanIpv4Probe.Success = true;
            symmetric.WanIpv4Probe.Endpoint = "223.5.5.5:443";
            symmetric.Apply(MakeProbe(ConnectivityProbeKind.Page, false, "lan", "192.168.1.23"));
            ConnectivityTestResult symmetricResult = symmetric.Evaluate();
            CheckTrue("对称 NAT 判为需要注意",
                symmetricResult.Level == ConnectivityVerdictLevel.Warn && !symmetricResult.P2pPossible,
                symmetricResult.Level + " / " + symmetricResult.Verdict);

            NetworkReport noIpv6 = new NetworkReport();
            ConnectivityTestOptions noIpv6Options = new ConnectivityTestOptions();
            noIpv6Options.LanIpv4Address = "192.168.1.10";
            noIpv6Options.LanIpv4PrefixLength = 24;
            ConnectivityTestSession failed = new ConnectivityTestSession(noIpv6, noIpv6Options);
            failed.WanIpv4Probe = new TcpConnectivityResult();
            failed.WanIpv4Probe.Success = false;
            failed.WanIpv4Probe.Error = "连接超时";
            failed.Apply(MakeProbe(ConnectivityProbeKind.Page, false, "lan", "192.168.1.23"));
            ConnectivityTestResult failedResult = failed.Evaluate();
            CheckTrue("IPv4 / IPv6 都不通时判定无法 P2P",
                failedResult.Level == ConnectivityVerdictLevel.Bad && !failedResult.P2pPossible,
                failedResult.Level + " / " + failedResult.Verdict);

            ConnectivityTestSession attempted = new ConnectivityTestSession(noIpv6, noIpv6Options);
            attempted.WanIpv4Probe = new TcpConnectivityResult();
            attempted.WanIpv4Probe.Success = false;
            attempted.WanIpv4Probe.Error = "连接超时";
            attempted.MarkAttempted(ConnectivityTestMode.Lan);
            ConnectivityTestResult attemptedResult = attempted.Evaluate();
            CheckTrue("展示了二维码但没收到连接时判定内网失败",
                attemptedResult.Find("lan4") != null
                && attemptedResult.Find("lan4").State == ConnectivityCheckState.Fail);
            CheckTrue("未收到连接也能完成 P2P 判定", attemptedResult.Complete);

            NetworkReport cgnatReport = new NetworkReport();
            cgnatReport.Nat.Type = NatType.FullCone;
            cgnatReport.Nat.TypeLabel = "全锥型 NAT";
            cgnatReport.Nat.ExternalIpv4 = "203.0.113.9";
            IpAddressEntry cgnatEntry = new IpAddressEntry();
            cgnatEntry.Address = "100.64.0.10";
            cgnatEntry.Scope = IpAddressScope.SharedCgnat;
            cgnatEntry.IsPrimary = true;
            cgnatReport.Ipv4Addresses.Add(cgnatEntry);
            ConnectivityTestSession cgnatSession = new ConnectivityTestSession(cgnatReport, noIpv6Options);
            cgnatSession.WanIpv4Probe = new TcpConnectivityResult();
            cgnatSession.WanIpv4Probe.Success = true;
            cgnatSession.WanIpv4Probe.Endpoint = "223.5.5.5:443";
            cgnatSession.WanIpv4Probe.LatencyMilliseconds = 20;
            cgnatSession.Apply(MakeProbe(ConnectivityProbeKind.Page, false, "lan", "192.168.1.23"));
            ConnectivityTestResult cgnatResult = cgnatSession.Evaluate();
            CheckTrue("CGNAT 环境提示 P2P 可能失败",
                cgnatResult.Find("wan4") != null
                && cgnatResult.Find("wan4").State == ConnectivityCheckState.Warn
                && cgnatResult.Level == ConnectivityVerdictLevel.Warn,
                cgnatResult.Verdict);
        }

        private static ConnectivityProbeEventArgs MakeProbe(ConnectivityProbeKind kind, bool ipv6,
            string mode, string remote)
        {
            ConnectivityProbeEventArgs args = new ConnectivityProbeEventArgs();
            args.Kind = kind;
            args.IsIpv6 = ipv6;
            args.Mode = mode;
            args.RemoteAddress = remote;
            args.LocalAddress = remote;
            args.Timestamp = DateTime.Now;
            return args;
        }

        private static string HttpGet(string url, out string contentType)
        {
            contentType = string.Empty;
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Proxy = null;
            request.Timeout = 5000;
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            {
                contentType = response.ContentType;
                using (Stream stream = response.GetResponseStream())
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                {
                    return reader.ReadToEnd();
                }
            }
        }

        private static int HttpGetStatus(string url)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Proxy = null;
            request.Timeout = 5000;
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            {
                return (int)response.StatusCode;
            }
        }

        private static void TestWebView2()
        {
            Console.WriteLine();
            Console.WriteLine("[9] WebView2 运行库检测");

            WebView2DetectionResult result = WebView2Detector.Detect();
            CheckTrue("返回 WebView2 检测结果", result != null);
            if (result == null) return;

            CheckTrue("WebView2 状态已识别", result.Status != WebView2Status.Unknown);
            CheckTrue("WebView2 状态文本非空", !string.IsNullOrEmpty(result.StatusText));
            CheckTrue("WebView2 安装列表已初始化", result.Installations != null);
            CheckTrue("WebView2 系统架构非空", !string.IsNullOrEmpty(result.OsArch));

            string diagnostics = result.ToDiagnostics();
            CheckTrue("WebView2 诊断信息包含标题", diagnostics.IndexOf("WebView2", StringComparison.Ordinal) >= 0);
            CheckTrue("WebView2 诊断信息包含检测结果", diagnostics.IndexOf("检测结果", StringComparison.Ordinal) >= 0);
            CheckTrue("WebView2 版本比较正确",
                WebView2Detector.CompareVersions("1.2.3.4", "1.2.3.5") < 0);

            if (result.Primary != null)
            {
                CheckTrue("WebView2 主安装版本非空", !string.IsNullOrEmpty(result.Primary.Version));
                Console.WriteLine("    检测到 WebView2：" + result.Primary.Version + " · "
                    + result.Primary.Scope + " · " + result.Primary.Location);
            }
            else
            {
                Console.WriteLine("    未检测到 WebView2 运行库（未安装或仅 Fixed Version）");
            }

            string edgeExe = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                @"Microsoft\Edge\Application\msedge.exe");
            if (File.Exists(edgeExe))
            {
                WebView2SignatureVerification signature = WebView2InstallerService.VerifySignature(edgeExe);
                CheckTrue("Edge 可执行文件数字签名有效", signature.Valid,
                    string.IsNullOrEmpty(signature.Error) ? string.Empty : signature.Error);
            }
            else
            {
                Console.WriteLine("    [跳过] 未找到 msedge.exe，跳过数字签名正向校验");
            }

            string notepad = Path.Combine(Environment.SystemDirectory, "notepad.exe");
            if (File.Exists(notepad))
            {
                bool canceled = false;
                using (CancellationTokenSource cts = new CancellationTokenSource())
                {
                    cts.Cancel();
                    try
                    {
                        new WebView2InstallerService().RunInstallerAsync(
                            notepad, false, null, cts.Token).GetAwaiter().GetResult();
                    }
                    catch (OperationCanceledException)
                    {
                        canceled = true;
                    }
                }
                CheckTrue("WebView2 安装前取消会生效", canceled);
            }
            else
            {
                Console.WriteLine("    [跳过] 未找到 notepad.exe，跳过安装取消测试");
            }
        }

        private static void TestSunshineClassifier()
        {
            Console.WriteLine();
            Console.WriteLine("[10] Sunshine 版本判定（离线）");

            SunshineSignature official = new SunshineSignature();
            official.CompanyName = "LizardByte";
            official.FileVersion = "0.23.1";
            official.InstallerKind = "MSI";
            SunshineVerdict officialVerdict = SunshineDetector.Classify(official);
            CheckTrue("官方版：渠道判定", officialVerdict.Kind == SunshineBuildKind.Official, officialVerdict.Kind.ToString());
            CheckTrue("官方版：高置信", officialVerdict.Confidence == SunshineConfidence.High, officialVerdict.Confidence.ToString());

            SunshineSignature baseEdition = new SunshineSignature();
            baseEdition.CompanyName = "LizardByte";
            baseEdition.FileVersion = "v2026.1031.121458.杂鱼";
            baseEdition.UninstallPublisher = "qiin2333";
            baseEdition.MatchedMarkers.Add("杂鱼");
            baseEdition.MatchedFiles.Add(@"tools\vdd\ZakoVDD.inf");
            SunshineVerdict baseVerdict = SunshineDetector.Classify(baseEdition);
            CheckTrue("基地版：渠道判定", baseVerdict.Kind == SunshineBuildKind.BaseEdition, baseVerdict.Kind.ToString());
            CheckTrue("基地版：高置信", baseVerdict.Confidence == SunshineConfidence.High, baseVerdict.Confidence.ToString());

            SunshineDetectionResult baseAuthor = new SunshineDetectionResult();
            baseAuthor.Kind = SunshineBuildKind.BaseEdition;
            baseAuthor.CompanyName = "LizardByte";
            CheckEqual("基地版：作者显示为 AlkaidLab", baseAuthor.AuthorName, "AlkaidLab");
            SunshineDetectionResult officialAuthor = new SunshineDetectionResult();
            officialAuthor.Kind = SunshineBuildKind.Official;
            officialAuthor.CompanyName = "LizardByte";
            CheckEqual("官方版：作者显示为 LizardByte", officialAuthor.AuthorName, "LizardByte");

            SunshineSignature earlyBase = new SunshineSignature();
            earlyBase.CompanyName = "LizardByte";
            earlyBase.FileVersion = "2024.0715.211805.7ff3168e.杂鱼";
            earlyBase.InstallerKind = "Uninstall.exe";
            earlyBase.MatchedFiles.Add(@"assets\gui\sunshine-gui.exe");
            SunshineVerdict earlyBaseVerdict = SunshineDetector.Classify(earlyBase);
            CheckTrue("初版基地：渠道判定", earlyBaseVerdict.Kind == SunshineBuildKind.BaseEdition, earlyBaseVerdict.Kind.ToString());
            CheckTrue("初版基地：高置信", earlyBaseVerdict.Confidence == SunshineConfidence.High, earlyBaseVerdict.Confidence.ToString());

            CheckEqual("版本号乱码修复（936）",
                SunshineDetector.NormalizeText("2024.0715.211805.7ff3168e.\u93c9\u509e\u5954"),
                "2024.0715.211805.7ff3168e.杂鱼");
            CheckEqual("版本号乱码修复（1252）",
                SunshineDetector.NormalizeText("v2026.1031.121458.\u00e6\u009d\u201a\u00e9\u00b1\u00bc"),
                "v2026.1031.121458.杂鱼");
            CheckEqual("正常中文不被改写", SunshineDetector.NormalizeText("杂鱼"), "杂鱼");
            CheckEqual("纯英文版本号不被改写", SunshineDetector.NormalizeText("0.23.1"), "0.23.1");

            SunshineSignature futureBase = new SunshineSignature();
            futureBase.CompanyName = "LizardByte";
            futureBase.FileVersion = "v2027.0203.091500.0badc0de.杂鱼";
            SunshineVerdict futureBaseVerdict = SunshineDetector.Classify(futureBase);
            CheckTrue("未来基地版本：仅凭中文版本后缀即可判定",
                futureBaseVerdict.Kind == SunshineBuildKind.BaseEdition && futureBaseVerdict.Confidence == SunshineConfidence.Medium,
                futureBaseVerdict.Kind + " / " + futureBaseVerdict.Confidence);

            SunshineSignature namedBase = new SunshineSignature();
            namedBase.UninstallDisplayName = "Foundation Sunshine 基地版 v3";
            SunshineVerdict namedBaseVerdict = SunshineDetector.Classify(namedBase);
            CheckTrue("基地版：卸载项名称命中", namedBaseVerdict.Kind == SunshineBuildKind.BaseEdition, namedBaseVerdict.Kind.ToString());

            SunshineSignature axixiOld = new SunshineSignature();
            axixiOld.CompanyName = "Third Party Publisher";
            axixiOld.FileVersion = "1.0.0";
            axixiOld.MatchedFiles.Add(@"drivers\sudovda\");
            SunshineVerdict axixiOldVerdict = SunshineDetector.Classify(axixiOld);
            CheckTrue("阿西西旧版：目录指纹生效", axixiOldVerdict.Kind == SunshineBuildKind.Axixi, axixiOldVerdict.Kind.ToString());
            CheckTrue("阿西西旧版：高置信", axixiOldVerdict.Confidence == SunshineConfidence.High, axixiOldVerdict.Confidence.ToString());

            SunshineSignature axixiNamed = new SunshineSignature();
            axixiNamed.UninstallDisplayName = "阿西西 Sunshine";
            SunshineVerdict axixiNamedVerdict = SunshineDetector.Classify(axixiNamed);
            CheckTrue("阿西西版：卸载项名称命中", axixiNamedVerdict.Kind == SunshineBuildKind.Axixi, axixiNamedVerdict.Kind.ToString());

            SunshineSignature futureOfficial = new SunshineSignature();
            futureOfficial.CompanyName = "LizardByte";
            futureOfficial.FileVersion = "1.0.0";
            SunshineVerdict futureOfficialVerdict = SunshineDetector.Classify(futureOfficial);
            CheckTrue("官方版升级到 1.x 仍判为官方", futureOfficialVerdict.Kind == SunshineBuildKind.Official, futureOfficialVerdict.Kind.ToString());

            CheckTrue("安装状态：服务+文件=已安装",
                SunshineDetector.DetermineInstallState(true, false, true) == SunshineInstallState.Installed);
            CheckTrue("安装状态：安装记录+文件=已安装",
                SunshineDetector.DetermineInstallState(false, true, true) == SunshineInstallState.Installed);
            CheckTrue("安装状态：只有文件=残留",
                SunshineDetector.DetermineInstallState(false, false, true) == SunshineInstallState.Leftover);
            CheckTrue("安装状态：只有安装记录=已卸载",
                SunshineDetector.DetermineInstallState(false, true, false) == SunshineInstallState.Uninstalled);
            CheckTrue("安装状态：服务指向的文件已消失=已卸载",
                SunshineDetector.DetermineInstallState(true, false, false) == SunshineInstallState.Uninstalled);
            CheckTrue("安装状态：什么都没有=未安装",
                SunshineDetector.DetermineInstallState(false, false, false) == SunshineInstallState.NotInstalled);

            SunshineSignature axixi = new SunshineSignature();
            axixi.CompanyName = "Third Party Publisher";
            axixi.FileVersion = "1.1.0";
            axixi.MatchedMarkers.Add("axixi");
            axixi.MatchedFiles.Add(@"drivers\sudovda\sudovda.inf");
            SunshineVerdict axixiVerdict = SunshineDetector.Classify(axixi);
            CheckTrue("阿西西版：渠道判定", axixiVerdict.Kind == SunshineBuildKind.Axixi, axixiVerdict.Kind.ToString());
            CheckTrue("阿西西版：高置信", axixiVerdict.Confidence == SunshineConfidence.High, axixiVerdict.Confidence.ToString());

            SunshineSignature axixiWeak = new SunshineSignature();
            axixiWeak.CompanyName = "Third Party Publisher";
            axixiWeak.FileVersion = "1.0.0";
            SunshineVerdict weakVerdict = SunshineDetector.Classify(axixiWeak);
            CheckTrue("阿西西版：只有厂商字段时为中置信",
                weakVerdict.Kind == SunshineBuildKind.Axixi && weakVerdict.Confidence == SunshineConfidence.Medium,
                weakVerdict.Kind + " / " + weakVerdict.Confidence);

            SunshineSignature baseOnly = new SunshineSignature();
            baseOnly.CompanyName = "LizardByte";
            baseOnly.FileVersion = "v2026.1031.121458.杂鱼";
            SunshineVerdict baseOnlyVerdict = SunshineDetector.Classify(baseOnly);
            CheckTrue("基地版不会被误判为阿西西", baseOnlyVerdict.Kind == SunshineBuildKind.BaseEdition, baseOnlyVerdict.Kind.ToString());

            SunshineVerdict unknownVerdict = SunshineDetector.Classify(new SunshineSignature());
            CheckTrue("无信息时为未知构建", unknownVerdict.Kind == SunshineBuildKind.Unknown, unknownVerdict.Kind.ToString());
            CheckTrue("未知构建置信度为低", unknownVerdict.Confidence == SunshineConfidence.Low, unknownVerdict.Confidence.ToString());
            CheckTrue("判定结果附带依据", officialVerdict.Reasons.Count > 0);
        }

        private static void TestSunshineDetection()
        {
            Console.WriteLine();
            Console.WriteLine("[11] Sunshine 版本自检（本机）");

            SunshineDetectionResult result = SunshineDetector.Detect();
            CheckTrue("返回 Sunshine 检测结果", result != null);
            if (result == null) return;

            CheckTrue("Sunshine 摘要非空", !string.IsNullOrEmpty(result.Summary));
            CheckTrue("Sunshine 诊断信息包含标题",
                result.ToDiagnostics().IndexOf("Sunshine", StringComparison.Ordinal) >= 0);
            CheckTrue("Sunshine 渠道标签非空", !string.IsNullOrEmpty(result.KindLabel));
            CheckTrue("Sunshine 判断依据非空", result.Evidence.Count > 0);

            if (string.IsNullOrEmpty(result.ExePath))
            {
                Console.WriteLine("    [跳过] 本机未安装 Sunshine");
                return;
            }

            CheckTrue("Sunshine 版本号非空", !string.IsNullOrEmpty(result.Version));
            CheckTrue("Sunshine 渠道已识别", result.Kind != SunshineBuildKind.Unknown, result.Kind.ToString());
            Console.WriteLine("    " + result.Summary + " | 安装路径 " + result.InstallPath);
            if (!string.IsNullOrEmpty(result.ServicePath))
                Console.WriteLine("    服务 " + result.ServiceName + " · " + result.ServiceStartMode + " · " + result.ServicePath);

            if (!string.IsNullOrEmpty(result.InstallPath) && Directory.Exists(result.InstallPath))
            {
                SunshineDetectionResult byDirectory = SunshineDetector.Detect(result.InstallPath);
                CheckTrue("按目录检测与整机检测渠道一致",
                    byDirectory.Kind == result.Kind,
                    "整机 " + result.Kind + "，目录 " + byDirectory.Kind);
                CheckTrue("按目录检测能读到版本号", !string.IsNullOrEmpty(byDirectory.Version));
            }
        }

        private static void TestSunshineService()
        {
            Console.WriteLine();
            Console.WriteLine("[12] Sunshine 服务状态");

            SunshineServiceStatus status = SunshineServiceManager.Detect();
            CheckTrue("返回服务检测结果", status != null);
            if (status == null) return;

            CheckTrue("服务状态标签非空", !string.IsNullOrEmpty(status.StateLabel));
            CheckTrue("服务摘要非空", !string.IsNullOrEmpty(status.Summary));
            CheckTrue("服务诊断信息包含标题",
                status.ToDiagnostics().IndexOf("Sunshine 服务", StringComparison.Ordinal) >= 0);

            if (!status.IsFound)
            {
                CheckTrue("未安装时状态为未安装", status.State == SunshineServiceState.NotFound, status.State.ToString());
                Console.WriteLine("    [跳过] 本机未注册 Sunshine 服务");
            }
            else
            {
                CheckTrue("已找到服务时状态已识别", status.State != SunshineServiceState.Unknown, status.State.ToString());
                CheckTrue("已找到服务时启动类型已识别", status.StartMode != SunshineStartMode.Unknown, status.StartMode.ToString());
                Console.WriteLine("    " + status.Summary + " | " + status.ServiceName + " | " + status.ImagePath);
            }

            SunshineStartMode mode;
            CheckTrue("解析启动类型 auto",
                SunshineServiceManager.TryParseStartMode("auto", out mode) && mode == SunshineStartMode.Automatic,
                mode.ToString());
            CheckTrue("解析启动类型 manual",
                SunshineServiceManager.TryParseStartMode("manual", out mode) && mode == SunshineStartMode.Manual,
                mode.ToString());
            CheckTrue("解析启动类型 disabled",
                SunshineServiceManager.TryParseStartMode("disabled", out mode) && mode == SunshineStartMode.Disabled,
                mode.ToString());
            CheckTrue("拒绝未知启动类型", !SunshineServiceManager.TryParseStartMode("bogus", out mode));
            CheckEqual("启动类型文案", SunshineServiceManager.StartModeText(SunshineStartMode.Automatic), "自动");
        }

        private static void TestZakoDisplay()
        {
            Console.WriteLine();
            Console.WriteLine("[13] Zako 虚拟显示器");

            ZakoDisplayStatus status = ZakoDisplayDetector.Detect();
            CheckTrue("返回 Zako 检测结果", status != null);
            if (status == null) return;

            CheckTrue("系统版本已读取", !string.IsNullOrEmpty(status.OsDisplay));
            CheckTrue("系统架构已读取", !string.IsNullOrEmpty(status.OsArch));
            CheckTrue("Zako 状态已识别", status.State != ZakoDisplayState.Unknown, status.State.ToString());
            CheckTrue("Zako 状态标签非空", !string.IsNullOrEmpty(status.StateLabel));
            CheckTrue("Zako 摘要非空", !string.IsNullOrEmpty(status.Summary));
            CheckTrue("Zako 诊断信息包含标题",
                status.ToDiagnostics().IndexOf("Zako", StringComparison.Ordinal) >= 0);

            List<ZakoDeviceInfo> one = new List<ZakoDeviceInfo>();
            ZakoDeviceInfo healthy = new ZakoDeviceInfo();
            healthy.DeviceClass = "Display";
            healthy.ProblemCode = 0;
            one.Add(healthy);
            CheckTrue("单适配器正常判定",
                ZakoDisplayDetector.EvaluateState(one) == ZakoDisplayState.Healthy);

            ZakoDeviceInfo disabled = new ZakoDeviceInfo();
            disabled.DeviceClass = "Display";
            disabled.ProblemCode = 22;
            CheckTrue("单适配器禁用判定",
                ZakoDisplayDetector.EvaluateState(new List<ZakoDeviceInfo> { disabled }) == ZakoDisplayState.Disabled);

            List<ZakoDeviceInfo> two = new List<ZakoDeviceInfo>();
            two.Add(healthy);
            two.Add(healthy);
            CheckTrue("两个适配器判为异常",
                ZakoDisplayDetector.EvaluateState(two) == ZakoDisplayState.MultipleAdapters);
            CheckTrue("空列表判为未安装",
                ZakoDisplayDetector.EvaluateState(new List<ZakoDeviceInfo>()) == ZakoDisplayState.NotInstalled);

            if (status.State == ZakoDisplayState.NotInstalled)
            {
                Console.WriteLine("    [跳过] 本机未安装 Zako 虚拟显示器");
            }
            else
            {
                CheckTrue("已安装时能找到适配器", status.Adapter != null, "适配器为空");
                if (status.Adapter != null && !string.IsNullOrEmpty(status.Adapter.DriverVersion))
                {
                    CheckTrue("读到驱动版本", !string.IsNullOrEmpty(status.Adapter.DriverVersion));
                    CheckTrue("驱动日期格式规范",
                        string.IsNullOrEmpty(status.Adapter.DriverDate)
                            || (status.Adapter.DriverDate.Length == 10 && status.Adapter.DriverDate[4] == '-'),
                        status.Adapter.DriverDate);
                }
                Console.WriteLine("    " + status.Summary + " | 设备数 " + status.Devices.Count
                    + " | 驱动 " + (status.Adapter == null ? "—" : status.Adapter.DriverVersion));

                if (!string.IsNullOrEmpty(status.BundledDriverVersion))
                {
                    CheckTrue("读到安装包驱动版本", status.BundledDriverVersion.IndexOf('.') > 0, status.BundledDriverVersion);
                    CheckTrue("安装包驱动与本机驱动已对照",
                        status.DriverVersionMatch != ZakoDriverVersionMatch.Unknown,
                        status.DriverVersionMatch.ToString());
                    Console.WriteLine("    安装包驱动 " + status.BundledDriverVersion
                        + " · " + status.DriverVersionMatchLabel);
                }
            }
        }

        private static string DescribeFirewall(FirewallInfo info)
        {
            if (info == null) return "（无）";
            if (!info.IsSupported) return "无法读取" + (string.IsNullOrEmpty(info.Error) ? "" : "：" + info.Error);
            return "开启=" + info.Enabled + "，默认阻止入站=" + info.DefaultInboundBlocked
                + "，配置文件=" + info.ProfileLabel + "，推断=" + info.IsInferred;
        }

        private static void CheckScope(string address, IpAddressScope expected)
        {
            IpAddressScope actual = IpClassifier.Classify(address);
            CheckTrue("分类 " + address + " → " + IpClassifier.GetScopeLabel(expected),
                actual == expected, "实际为 " + actual);
        }

        private static void CheckEqual(string name, string actual, string expected)
        {
            bool ok = string.Equals(actual, expected, StringComparison.Ordinal);
            CheckTrue(name, ok, "期望 [" + expected + "]，实际 [" + actual + "]");
        }

        private static void CheckTrue(string name, bool condition)
        {
            CheckTrue(name, condition, "");
        }

        private static void CheckTrue(string name, bool condition, string detail)
        {
            _checks++;
            if (condition)
            {
                Console.WriteLine("    [PASS] " + name);
            }
            else
            {
                _failures++;
                Console.WriteLine("    [FAIL] " + name + (string.IsNullOrEmpty(detail) ? "" : "：" + detail));
            }
        }
    }
}
