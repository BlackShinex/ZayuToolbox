using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using IpInfo.Core;

namespace IpInfo.CoreTests
{
    /// <summary>
    /// 无界面的核心逻辑自测程序。用法：
    ///   IpInfo.CoreTests.exe [--live]
    /// 默认只运行离线测试（隐私遮罩、地址分类、本机地址枚举）；
    /// 加 --live 时额外执行一次真实的 STUN / NAT 探测。
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

            TestPrivacyMask();
            TestClassifier();
            TestLocalAddresses();
            TestVirtualAdapters();
            TestFirewallReader();
            TestIpv6InboundServer();
            TestWebView2();
            TestNatHopTracer();

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
            Console.WriteLine("[1] IP 隐私遮罩");
            CheckEqual("IPv4 遮罩 192.168.1.100", IpPrivacy.Mask("192.168.1.100"), "192.*.*.100");
            CheckEqual("IPv4 遮罩 10.0.0.5", IpPrivacy.Mask("10.0.0.5"), "10.*.*.5");
            CheckEqual(
                "IPv6 遮罩 2400:3200:1234:5678:9abc:def0:1111:2222",
                IpPrivacy.Mask("2400:3200:1234:5678:9abc:def0:1111:2222"),
                "2400:****:****:****:****:****:****:2222");
            CheckEqual("IPv6 遮罩 fe80::1%18", IpPrivacy.Mask("fe80::1%18"), "fe80:****:****:****:****:****:****:1");
            CheckEqual("自动分派 IPv4", IpPrivacy.Mask("172.16.5.9"), "172.*.*.9");
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
            Console.WriteLine("[7] 联网探测（STUN / NAT / 防火墙）");
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

        private static void TestIpv6InboundServer()
        {
            Console.WriteLine();
            Console.WriteLine("[6] IPv6 入站自检服务器（回环模式）");
            Ipv6InboundTestServer server = new Ipv6InboundTestServer();
            ManualResetEventSlim gate = new ManualResetEventSlim(false);
            string remote = null;
            server.ClientConnected += delegate(object sender, InboundTestClientEventArgs e)
            {
                remote = e.RemoteEndPoint;
                gate.Set();
            };

            try
            {
                server.Start("::1", false);
                CheckTrue("URL 为 IPv6 格式", server.Url != null && server.Url.StartsWith("http://[::1]:", StringComparison.Ordinal));

                using (TcpClient client = new TcpClient(AddressFamily.InterNetworkV6))
                {
                    client.Connect(IPAddress.IPv6Loopback, server.Port);
                    bool signaled = gate.Wait(5000);
                    CheckTrue("可接受 IPv6 入站连接", signaled, "5 秒内没有收到连接事件");
                    CheckTrue("记录入站来源地址", !string.IsNullOrEmpty(remote), "来源为空");
                    if (!string.IsNullOrEmpty(remote))
                    {
                        Console.WriteLine("    入站来源: " + remote);
                    }
                }
            }
            catch (Exception ex)
            {
                CheckTrue("入站自检服务器工作正常", false, ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                try { server.Stop(); }
                catch { }
                try { server.Dispose(); }
                catch { }
            }
        }

        private static void TestWebView2()
        {
            Console.WriteLine();
            Console.WriteLine("[7] WebView2 运行库检测");

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

        private static void TestNatHopTracer()
        {
            Console.WriteLine();
            Console.WriteLine("[8] 多层 NAT 跃点跟踪（取消路径）");

            using (CancellationTokenSource cts = new CancellationTokenSource())
            {
                cts.Cancel();
                NatHopTraceResult result = NatHopTracer.Trace(null, cts.Token);
                CheckTrue("返回跃点跟踪结果", result != null);
                if (result == null) return;

                CheckTrue("取消后标记为已停止", result.Canceled);
                CheckTrue("取消结果摘要非空", !string.IsNullOrEmpty(result.Summary));
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
