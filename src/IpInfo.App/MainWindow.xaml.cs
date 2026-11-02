using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IpInfo.Core;

namespace IpInfo.App
{
    public partial class MainWindow : Window
    {
        private static readonly Brush BannerAmberBg = UiPalette.Brush("#FFF7ED");
        private static readonly Brush BannerAmberFg = UiPalette.Brush("#B45309");
        private static readonly Brush BannerAmberDot = UiPalette.Brush("#F59E0B");
        private static readonly Brush BannerGreenBg = UiPalette.Brush("#F0FDF4");
        private static readonly Brush BannerGreenFg = UiPalette.Brush("#15803D");
        private static readonly Brush BannerGreenDot = UiPalette.Brush("#22C55E");
        private static readonly Brush BannerIndigoBg = UiPalette.Brush("#EEF2FF");
        private static readonly Brush BannerIndigoFg = UiPalette.Brush("#3730A3");
        private static readonly Brush BannerIndigoDot = UiPalette.Brush("#6366F1");
        private static readonly Brush BannerRedBg = UiPalette.Brush("#FEF2F2");
        private static readonly Brush BannerRedFg = UiPalette.Brush("#B91C1C");

        private NetworkReport _report;
        private bool _revealed;
        private bool _busy;
        private readonly TaskCompletionSource<bool> _initialRefresh = new TaskCompletionSource<bool>();
        private CancellationTokenSource _cts;
        private DispatcherTimer _copyFeedbackTimer;
        private Button _lastCopyButton;
        private object _lastCopyContent;
        private Ipv6InboundTestServer _inboundServer;
        private string _inboundPublicIpv6;
        private InboundTestDialog _inboundDialog;
        private WebView2DetectionResult _webView2Result;
        private bool _webView2Busy;
        private WebView2InstallDialog _webView2InstallDialog;
        private NatHopTraceResult _natTraceResult;
        private CancellationTokenSource _natTraceCts;
        private bool _natTraceRunning;

        public MainWindow()
        {
            InitializeComponent();
            RevealToggle.Content = "显示 IP";
            RevealToggle.Foreground = Brushes.White;

            Version appVersion = typeof(MainWindow).Assembly.GetName().Version;
            VersionText.Text = "杂鱼串流工具箱 v" + appVersion.Major + "." + appVersion.Minor
                + " · 作者 BlackCat · 数据仅在本机处理";

            Loaded += MainWindow_Loaded;
            Closed += MainWindow_Closed;
            DpiChanged += MainWindow_DpiChanged;
        }

        private void MainWindow_DpiChanged(object sender, DpiChangedEventArgs e)
        {
            FitToWorkArea();
        }

        /// <summary>
        /// 内层 ScrollViewer 到达顶部/底部后，把滚轮事件交给外层页面，
        /// 避免鼠标停在跃点列表或检测详情上时整页滚动“卡住”。
        /// </summary>
        private void NestedScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            ScrollViewer inner = sender as ScrollViewer;
            if (inner == null) return;

            bool canScrollUp = inner.VerticalOffset > 0.5;
            bool canScrollDown = inner.VerticalOffset < inner.ScrollableHeight - 0.5;
            if ((e.Delta > 0 && !canScrollUp) || (e.Delta < 0 && !canScrollDown))
            {
                ScrollViewer outer = FindAncestorScrollViewer(inner);
                if (outer == null) return;

                e.Handled = true;
                double before = outer.VerticalOffset;
                MouseWheelEventArgs forwarded = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta);
                forwarded.RoutedEvent = UIElement.MouseWheelEvent;
                outer.RaiseEvent(forwarded);
                if (Math.Abs(outer.VerticalOffset - before) < 0.5)
                {
                    double offset = before + (e.Delta > 0 ? -48 : 48);
                    outer.ScrollToVerticalOffset(Math.Max(0, offset));
                }
            }
        }

        private static ScrollViewer FindAncestorScrollViewer(DependencyObject element)
        {
            DependencyObject current = element;
            while (current != null)
            {
                ScrollViewer viewer = current as ScrollViewer;
                if (viewer != null) return viewer;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (NavList == null) return;
            UpdatePageVisibility(NavList.SelectedIndex);
        }

        private void UpdatePageVisibility(int index)
        {
            if (NetworkScrollViewer == null
                || NatHopScrollViewer == null
                || SunshineScrollViewer == null
                || WebView2ScrollViewer == null)
            {
                return;
            }

            NetworkScrollViewer.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
            NatHopScrollViewer.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
            SunshineScrollViewer.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
            WebView2ScrollViewer.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>在 125% / 150% 等高分屏缩放下，保证窗口不会超出当前屏幕可用区域。</summary>
        private void FitToWorkArea()
        {
            Rect area = GetCurrentMonitorWorkArea();
            if (area.Width <= 0 || area.Height <= 0) return;

            const double margin = 24;
            double maxWidth = Math.Max(MinWidth, area.Width - margin);
            double maxHeight = Math.Max(MinHeight, area.Height - margin);

            if (Width > maxWidth) Width = maxWidth;
            if (Height > maxHeight) Height = maxHeight;
            if (Left < area.Left + margin) Left = area.Left + margin;
            if (Top < area.Top + margin) Top = area.Top + margin;
            if (Left + Width > area.Right - margin) Left = Math.Max(area.Left + margin, area.Right - margin - Width);
            if (Top + Height > area.Bottom - margin) Top = Math.Max(area.Top + margin, area.Bottom - margin - Height);
        }

        private Rect GetCurrentMonitorWorkArea()
        {
            try
            {
                IntPtr handle = new WindowInteropHelper(this).Handle;
                if (handle != IntPtr.Zero)
                {
                    IntPtr monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
                    if (monitor != IntPtr.Zero)
                    {
                        MonitorInfo info = new MonitorInfo();
                        info.cbSize = Marshal.SizeOf(typeof(MonitorInfo));
                        if (GetMonitorInfo(monitor, ref info))
                        {
                            DpiScale dpi = VisualTreeHelper.GetDpi(this);
                            double scaleX = dpi.DpiScaleX <= 0 ? 1.0 : dpi.DpiScaleX;
                            double scaleY = dpi.DpiScaleY <= 0 ? 1.0 : dpi.DpiScaleY;
                            return new Rect(
                                info.rcWork.Left / scaleX,
                                info.rcWork.Top / scaleY,
                                (info.rcWork.Right - info.rcWork.Left) / scaleX,
                                (info.rcWork.Bottom - info.rcWork.Top) / scaleY);
                        }
                    }
                }
            }
            catch
            {
            }
            return SystemParameters.WorkArea;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            public int cbSize;
            public NativeRect rcMonitor;
            public NativeRect rcWork;
            public uint dwFlags;
        }

        private const uint MonitorDefaultToNearest = 2;

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= MainWindow_Loaded;
            FitToWorkArea();
            await RefreshAsync();
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshAsync();
        }

        private async Task RefreshAsync()
        {
            if (_busy) return;

            await RefreshWebView2Async();

            if (_inboundServer != null)
            {
                StopInboundTest(false);
            }
            CancelNatTrace();

            _busy = true;
            RefreshButton.IsEnabled = false;
            DetectProgressBar.Visibility = Visibility.Visible;
            ProgressText.Visibility = Visibility.Visible;
            ProgressText.Text = "正在检测…";
            HideError();

            _cts = new CancellationTokenSource();
            CancellationToken token = _cts.Token;

            IProgress<string> progress = new Progress<string>(delegate(string message)
            {
                ProgressText.Text = message;
            });

            try
            {
                NetworkReport report = await Task.Run(delegate
                {
                    return NetworkProbe.Collect(new Action<string>(progress.Report), token);
                }, token);

                if (report == null) throw new InvalidOperationException("网络探测返回了空结果。");

                _report = report;
                RenderReport(report);

                if (!string.IsNullOrEmpty(report.Error))
                {
                    ShowError(report.Error);
                }
                else if (!string.IsNullOrEmpty(report.Nat.Error))
                {
                    ShowError(report.Nat.Error);
                }
            }
            catch (OperationCanceledException)
            {
                // 用户切换/关闭窗口，忽略。
            }
            catch (Exception ex)
            {
                ShowError("检测失败：" + ex.Message);
                RenderFailureFallback(ex);
            }
            finally
            {
                _busy = false;
                RefreshButton.IsEnabled = true;
                DetectProgressBar.Visibility = Visibility.Collapsed;
                ProgressText.Visibility = Visibility.Collapsed;
                _initialRefresh.TrySetResult(true);

                if (_cts != null)
                {
                    _cts.Dispose();
                    _cts = null;
                }
            }
        }

        private void RenderReport(NetworkReport report)
        {
            if (report == null) return;

            // 本机 IPv4 / IPv6 列表
            Ipv4List.ItemsSource = BuildRows(report.Ipv4Addresses, false);
            Ipv4CountText.Text = report.Ipv4Addresses.Count + " 个";
            Ipv4Summary.Text = BuildAddressSummary(report.Ipv4Addresses);
            Ipv4List.Visibility = report.Ipv4Addresses.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            Ipv4Empty.Visibility = report.Ipv4Addresses.Count > 0 ? Visibility.Collapsed : Visibility.Visible;

            Ipv6List.ItemsSource = BuildRows(report.Ipv6Addresses, true);
            Ipv6CountText.Text = report.Ipv6Addresses.Count + " 个";
            Ipv6Summary.Text = BuildAddressSummary(report.Ipv6Addresses);
            Ipv6List.Visibility = report.Ipv6Addresses.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            Ipv6Empty.Visibility = report.Ipv6Addresses.Count > 0 ? Visibility.Collapsed : Visibility.Visible;

            // 公网出口
            PublicIpInfo pub = report.PublicIp;
            ExternalIpv4Text.Text = DisplayIp(pub.ExternalIpv4, "未获取");
            ExternalIpv4CopyButton.Tag = pub.ExternalIpv4 ?? string.Empty;
            ExternalIpv4CopyButton.IsEnabled = !string.IsNullOrEmpty(pub.ExternalIpv4);
            ApplyStatusChip(Ipv4StatusChip, Ipv4StatusText, pub.Ipv4Status);

            ExternalIpv6Text.Text = DisplayIp(pub.ExternalIpv6, "未检测到");
            ExternalIpv6CopyButton.Tag = pub.ExternalIpv6 ?? string.Empty;
            ExternalIpv6CopyButton.IsEnabled = !string.IsNullOrEmpty(pub.ExternalIpv6);
            ApplyStatusChip(Ipv6StatusChip, Ipv6StatusText, pub.Ipv6Status);

            SummaryText.Text = MaskKnownIps(
                string.IsNullOrEmpty(pub.Summary) ? "未获取到网络状态" : pub.Summary, report);
            ApplySummaryStyle(pub);

            // NAT 类型
            NatResult nat = report.Nat;
            NatTypeText.Text = nat.TypeLabel;
            NatTypeText.Foreground = UiPalette.GetNatTypeBrush(nat.Type);
            NatDescriptionText.Text = string.IsNullOrEmpty(nat.Description)
                ? "—"
                : MaskKnownIps(nat.Description, report);
            NatDetailMappingText.Text = "映射行为：" + GetMappingLabel(nat.Mapping) + "  ·  过滤行为：" + GetFilteringLabel(nat.Filtering);
            string portText;
            if (nat.LocalPort > 0)
            {
                portText = "（本地 " + nat.LocalPort + " → 外部 " + nat.ExternalIpv4Port + "）";
            }
            else
            {
                portText = string.Empty;
            }
            NatDetailPortText.Text = nat.LocalPort > 0
                ? "端口保持：" + (nat.PortPreserved ? "是" : "否") + portText
                : "端口保持：未知";
            NatDetailServerText.Text = string.IsNullOrEmpty(nat.ServerUsed)
                ? "STUN 服务器：--"
                : "STUN 服务器：" + nat.ServerUsed;

            List<string> steps = new List<string>();
            if (nat.Steps != null)
            {
                for (int i = 0; i < nat.Steps.Count; i++)
                {
                    steps.Add(MaskKnownIps(nat.Steps[i], report));
                }
            }
            StepsList.ItemsSource = steps;
            NatDetailExpander.Visibility = steps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            RenderFirewall(report);
            RenderInboundAvailability(report);

            TimestampText.Text = "检测时间 " + report.Timestamp.ToString("yyyy-MM-dd HH:mm:ss")
                + "  ·  耗时 " + report.Elapsed.TotalSeconds.ToString("0.0") + " 秒";
        }

        private async void NatTraceStartButton_Click(object sender, RoutedEventArgs e)
        {
            if (_natTraceRunning) return;

            _natTraceRunning = true;
            _natTraceCts = new CancellationTokenSource();
            CancellationToken token = _natTraceCts.Token;
            _natTraceResult = null;
            NatHopList.ItemsSource = null;
            NatTraceStartButton.IsEnabled = false;
            NatTraceStartButton.Content = "跟踪中…";
            NatTraceStopButton.IsEnabled = true;
            NatTraceStopButton.Visibility = Visibility.Visible;
            NatTraceProgressBar.Visibility = Visibility.Visible;
            SetNatTraceBanner(BannerIndigoBg, BannerIndigoFg);
            NatTraceSummaryText.Text = "正在准备跃点跟踪…";

            try
            {
                IProgress<string> progress = new Progress<string>(delegate(string message)
                {
                    NatTraceSummaryText.Text = MaskNatTraceText(message);
                });
                NatHopTraceResult result = await Task.Run(delegate
                {
                    return NatHopTracer.Trace(new Action<string>(progress.Report), token);
                }, token);

                if (result == null) throw new InvalidOperationException("跃点跟踪返回了空结果。");
                _natTraceResult = result;
                RenderNatTrace(result);
            }
            catch (OperationCanceledException)
            {
                NatTraceSummaryText.Text = "跃点跟踪已停止。";
                SetNatTraceBanner(BannerIndigoBg, BannerIndigoFg);
            }
            catch (Exception ex)
            {
                NatTraceSummaryText.Text = "跃点跟踪失败：" + ex.Message;
                SetNatTraceBanner(BannerRedBg, BannerRedFg);
            }
            finally
            {
                _natTraceRunning = false;
                NatTraceProgressBar.Visibility = Visibility.Collapsed;
                NatTraceStopButton.Visibility = Visibility.Collapsed;
                NatTraceStopButton.IsEnabled = true;
                NatTraceStartButton.IsEnabled = true;
                NatTraceStartButton.Content = _natTraceResult != null ? "重新跟踪" : "开始跟踪";
                if (_natTraceCts != null)
                {
                    _natTraceCts.Dispose();
                    _natTraceCts = null;
                }
            }
        }

        private void NatTraceStopButton_Click(object sender, RoutedEventArgs e)
        {
            if (_natTraceCts != null)
            {
                _natTraceCts.Cancel();
                NatTraceStopButton.IsEnabled = false;
                NatTraceSummaryText.Text = "正在停止跃点跟踪…";
            }
        }

        private void RenderNatTrace(NatHopTraceResult result)
        {
            List<NatHopRow> rows = new List<NatHopRow>();
            if (result.Hops != null)
            {
                for (int i = 0; i < result.Hops.Count; i++)
                {
                    NatHop hop = result.Hops[i];
                    NatHopRow row = new NatHopRow();
                    row.TtlText = hop.Ttl.ToString();
                    row.AddressText = hop.TimedOut
                        ? "无响应"
                        : (_revealed ? hop.Address : IpPrivacy.Mask(hop.Address));
                    row.ScopeLabel = hop.TimedOut
                        ? "超时"
                        : (string.IsNullOrEmpty(hop.ScopeLabel) ? "未知" : hop.ScopeLabel);
                    row.RoundTripText = hop.TimedOut ? "—" : hop.RoundTripMilliseconds + " ms";
                    row.Note = hop.Note;
                    if (hop.TimedOut)
                    {
                        row.TagBackground = UiPalette.GrayBg;
                        row.TagForeground = UiPalette.GrayFg;
                    }
                    else
                    {
                        Brush background;
                        Brush foreground;
                        UiPalette.GetScopeColors(hop.Scope, out background, out foreground);
                        row.TagBackground = background;
                        row.TagForeground = foreground;
                    }
                    rows.Add(row);
                }
            }
            NatHopList.ItemsSource = rows;

            if (!string.IsNullOrEmpty(result.Error))
            {
                NatTraceSummaryText.Text = MaskNatTraceText(result.Error);
                SetNatTraceBanner(BannerRedBg, BannerRedFg);
            }
            else if (result.Canceled)
            {
                NatTraceSummaryText.Text = MaskNatTraceText(string.IsNullOrEmpty(result.Summary)
                    ? "跃点跟踪已停止。"
                    : result.Summary);
                SetNatTraceBanner(BannerIndigoBg, BannerIndigoFg);
            }
            else if (result.ReachedInternet)
            {
                NatTraceSummaryText.Text = MaskNatTraceText(result.Summary);
                SetNatTraceBanner(BannerGreenBg, BannerGreenFg);
            }
            else
            {
                NatTraceSummaryText.Text = MaskNatTraceText(result.Summary);
                SetNatTraceBanner(BannerAmberBg, BannerAmberFg);
            }
        }

        private void SetNatTraceBanner(Brush background, Brush foreground)
        {
            NatTraceSummaryBanner.Background = background;
            NatTraceSummaryText.Foreground = foreground;
        }

        private string MaskNatTraceText(string text)
        {
            if (_revealed || string.IsNullOrEmpty(text)) return text;
            return Regex.Replace(text, @"\b(?:\d{1,3}\.){3}\d{1,3}\b",
                delegate(Match match) { return IpPrivacy.Mask(match.Value); });
        }

        private void CancelNatTrace()
        {
            if (_natTraceCts != null)
            {
                _natTraceCts.Cancel();
            }
        }

        private List<IpRow> BuildRows(List<IpAddressEntry> entries, bool ipv6)
        {
            List<IpRow> rows = new List<IpRow>();
            if (entries == null) return rows;

            for (int i = 0; i < entries.Count; i++)
            {
                IpAddressEntry entry = entries[i];
                Brush tagBackground;
                Brush tagForeground;
                UiPalette.GetScopeColors(entry.Scope, out tagBackground, out tagForeground);

                IpRow row = new IpRow();
                row.DisplayAddress = _revealed ? entry.Address : IpPrivacy.Mask(entry.Address);
                row.CopyAddress = entry.CopyAddress;

                string adapter = entry.InterfaceName;
                if (!string.IsNullOrEmpty(entry.InterfaceType)
                    && !string.Equals(entry.InterfaceName, entry.InterfaceType, StringComparison.Ordinal))
                {
                    adapter = adapter + " · " + entry.InterfaceType;
                }
                if (entry.IsVirtual)
                {
                    adapter = adapter + " · 虚拟网卡";
                }
                row.AdapterText = adapter + (entry.IsPrimary ? " · 当前主地址" : string.Empty);
                row.ScopeLabel = entry.ScopeLabel;
                row.TagBackground = tagBackground;
                row.TagForeground = tagForeground;
                row.AddressFontSize = ipv6 ? 13.5 : 16;
                row.PrimaryVisibility = entry.IsPrimary ? Visibility.Visible : Visibility.Collapsed;
                rows.Add(row);
            }
            return rows;
        }

        private string DisplayIp(string address, string emptyText)
        {
            if (string.IsNullOrEmpty(address)) return emptyText;
            return _revealed ? address : IpPrivacy.Mask(address);
        }

        /// <summary>未显示完整 IP 时，把文本中出现的本机/公网 IP 一并遮罩。</summary>
        private string MaskKnownIps(string text, NetworkReport report)
        {
            if (_revealed || string.IsNullOrEmpty(text) || report == null) return text;

            List<string> ips = new List<string>();
            AddEntryAddresses(ips, report.Ipv4Addresses);
            AddEntryAddresses(ips, report.Ipv6Addresses);
            if (report.PublicIp != null)
            {
                AddIp(ips, report.PublicIp.ExternalIpv4);
                AddIp(ips, report.PublicIp.ExternalIpv6);
            }
            if (report.Nat != null)
            {
                AddIp(ips, report.Nat.ExternalIpv4);
            }

            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> unique = new List<string>();
            for (int i = 0; i < ips.Count; i++)
            {
                if (!string.IsNullOrEmpty(ips[i]) && seen.Add(ips[i]))
                {
                    unique.Add(ips[i]);
                }
            }
            unique.Sort(delegate(string a, string b) { return b.Length.CompareTo(a.Length); });

            string result = text;
            for (int i = 0; i < unique.Count; i++)
            {
                string ip = unique[i];
                if (result.IndexOf(ip, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    result = Regex.Replace(result, Regex.Escape(ip), IpPrivacy.Mask(ip), RegexOptions.IgnoreCase);
                }
            }
            return result;
        }

        private static void AddEntryAddresses(List<string> ips, List<IpAddressEntry> entries)
        {
            if (entries == null) return;
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i] == null) continue;
                AddIp(ips, entries[i].Address);
                AddIp(ips, entries[i].CopyAddress);
            }
        }

        private static void AddIp(List<string> ips, string address)
        {
            if (!string.IsNullOrEmpty(address)) ips.Add(address);
        }

        private static string BuildAddressSummary(List<IpAddressEntry> entries)
        {
            if (entries == null || entries.Count == 0) return "未检测到地址";
            int publicCount = 0;
            int virtualCount = 0;
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].Scope == IpAddressScope.Public) publicCount++;
                if (entries[i].IsVirtual) virtualCount++;
            }

            string summary;
            if (publicCount > 0)
            {
                summary = "共 " + entries.Count + " 个 · 公网 " + publicCount + " 个";
            }
            else
            {
                summary = "共 " + entries.Count + " 个 · " + entries[0].ScopeLabel + "地址";
            }
            if (virtualCount > 0)
            {
                summary = summary + " · 含虚拟网卡 " + virtualCount + " 个";
            }
            return summary;
        }

        private static void ApplyStatusChip(Border chip, TextBlock text, PublicIpStatus status)
        {
            Brush background;
            Brush foreground;
            UiPalette.GetPublicStatusColors(status, out background, out foreground);
            chip.Background = background;
            text.Foreground = foreground;
            text.Text = UiPalette.GetPublicStatusText(status);
        }

        private void ApplySummaryStyle(PublicIpInfo pub)
        {
            if (pub.Ipv4Status == PublicIpStatus.BehindNat)
            {
                SummaryBanner.Background = BannerAmberBg;
                SummaryText.Foreground = BannerAmberFg;
                SummaryDot.Background = BannerAmberDot;
            }
            else if (pub.Ipv4Status == PublicIpStatus.NativePublic)
            {
                SummaryBanner.Background = BannerGreenBg;
                SummaryText.Foreground = BannerGreenFg;
                SummaryDot.Background = BannerGreenDot;
            }
            else
            {
                SummaryBanner.Background = BannerIndigoBg;
                SummaryText.Foreground = BannerIndigoFg;
                SummaryDot.Background = BannerIndigoDot;
            }
        }

        private static string GetMappingLabel(MappingBehavior mapping)
        {
            switch (mapping)
            {
                case MappingBehavior.EndpointIndependent: return "端点无关（Endpoint-Independent）";
                case MappingBehavior.AddressDependent: return "地址相关（Address-Dependent）";
                case MappingBehavior.EndpointDependent: return "端点相关（Endpoint-Dependent）";
                default: return "未知";
            }
        }

        private static string GetFilteringLabel(FilteringBehavior filtering)
        {
            switch (filtering)
            {
                case FilteringBehavior.EndpointIndependent: return "端点无关";
                case FilteringBehavior.AddressDependent: return "地址相关";
                case FilteringBehavior.AddressAndPortDependent: return "地址与端口相关";
                default: return "未知";
            }
        }

        private void RenderFirewall(NetworkReport report)
        {
            FirewallInfo firewall = report.Firewall;
            if (firewall == null || !firewall.IsSupported)
            {
                string error = firewall != null ? (firewall.Error ?? string.Empty) : string.Empty;
                if (error.IndexOf("0x80070005", StringComparison.OrdinalIgnoreCase) >= 0
                    || error.IndexOf("拒绝访问", StringComparison.Ordinal) >= 0
                    || error.IndexOf("权限", StringComparison.Ordinal) >= 0)
                {
                    FirewallText.Text = "Windows 防火墙：读取完整状态需要管理员权限，可继续用入站自检验证。";
                }
                else
                {
                    FirewallText.Text = "Windows 防火墙：无法读取状态。";
                }
                FirewallText.Foreground = UiPalette.MutedFg;
                return;
            }

            string profile = string.IsNullOrEmpty(firewall.ProfileLabel) ? "当前网络" : firewall.ProfileLabel;
            if (firewall.IsInferred)
            {
                if (firewall.Enabled)
                {
                    FirewallText.Text = "Windows 防火墙：已启用（默认阻止入站，推断；完整状态需管理员权限）";
                    FirewallText.Foreground = UiPalette.WarnFg;
                }
                else
                {
                    FirewallText.Text = "Windows 防火墙：未启用（推断）";
                    FirewallText.Foreground = UiPalette.GrayFg;
                }
                return;
            }
            if (!firewall.Enabled)
            {
                FirewallText.Text = "Windows 防火墙：已关闭（" + profile + "）";
                FirewallText.Foreground = UiPalette.GrayFg;
            }
            else if (firewall.DefaultInboundBlocked)
            {
                FirewallText.Text = "Windows 防火墙：开启（" + profile + "）· 默认阻止入站，可能拦截外部连接";
                FirewallText.Foreground = UiPalette.WarnFg;
            }
            else
            {
                FirewallText.Text = "Windows 防火墙：开启（" + profile + "）· 默认允许入站";
                FirewallText.Foreground = UiPalette.SuccessFg;
            }
        }

        private void RenderInboundAvailability(NetworkReport report)
        {
            _inboundPublicIpv6 = report.GetPublicIpv6();
            bool hasIpv6 = !string.IsNullOrEmpty(_inboundPublicIpv6);

            InboundStartButton.IsEnabled = hasIpv6;
            InboundStartButton.Content = "启动入站自检";
            InboundUrlPanel.Visibility = Visibility.Collapsed;
            InboundCopyButton.Visibility = Visibility.Collapsed;
            InboundStopButton.Visibility = Visibility.Collapsed;
            InboundUrlText.Text = "--";

            if (!hasIpv6)
            {
                InboundStatusText.Text = "未检测到公网 IPv6，无法进行入站自检。";
                InboundStatusText.Foreground = UiPalette.MutedFg;
                InboundHintText.Text = "提示：需要本机拥有公网 IPv6 地址才能测试入站。";
            }
            else
            {
                InboundStatusText.Text = "尚未启动自检";
                InboundStatusText.Foreground = UiPalette.MutedFg;
                InboundHintText.Text = "提示：自检会在本机临时打开一个 TCP 端口，检测结束后自动关闭。";
            }
        }

        private void InboundStartButton_Click(object sender, RoutedEventArgs e)
        {
            if (_inboundServer != null && _inboundServer.IsRunning)
            {
                StopInboundTest(false);
                return;
            }
            if (string.IsNullOrEmpty(_inboundPublicIpv6))
            {
                InboundStatusText.Text = "未检测到公网 IPv6，无法进行入站自检。";
                InboundStatusText.Foreground = UiPalette.MutedFg;
                return;
            }

            try
            {
                Ipv6InboundTestServer server = new Ipv6InboundTestServer();
                server.ClientConnected += InboundServer_ClientConnected;
                server.ServerError += InboundServer_ServerError;
                // 自动化测试时可通过环境变量只监听回环地址，避免弹出防火墙授权窗口。
                bool bindAllInterfaces = !string.Equals(
                    Environment.GetEnvironmentVariable("IPINFO_INBOUND_BIND_LOOPBACK"),
                    "1", StringComparison.Ordinal);
                server.Start(_inboundPublicIpv6, bindAllInterfaces);

                _inboundServer = server;

                InboundUrlText.Text = server.Url;
                InboundUrlPanel.Visibility = Visibility.Visible;
                InboundCopyButton.Visibility = Visibility.Visible;
                InboundStopButton.Visibility = Visibility.Visible;
                InboundStartButton.IsEnabled = false;
                InboundStartButton.Content = "等待连接…";
                InboundStatusText.Text = "正在等待外部连接… 请在手机上打开上方地址（监听 TCP " + server.Port + "）。";
                InboundStatusText.Foreground = UiPalette.WarnFg;
                InboundHintText.Text = "如弹出 Windows 防火墙提示，请选择「允许访问」；检测结束后点击「停止」。";

                InboundTestDialog dialog = new InboundTestDialog(server.Url);
                dialog.Owner = this;
                _inboundDialog = dialog;
                dialog.Closed += delegate { _inboundDialog = null; };
                dialog.Show();
            }
            catch (Exception ex)
            {
                InboundStatusText.Text = "启动自检失败：" + ex.Message;
                InboundStatusText.Foreground = UiPalette.WarnFg;
            }
        }

        private void InboundCopyButton_Click(object sender, RoutedEventArgs e)
        {
            string url = InboundUrlText.Text;
            if (string.IsNullOrEmpty(url) || url == "--") return;
            if (CopyToClipboard(url))
            {
                ShowCopyFeedback((Button)sender);
            }
            else
            {
                ShowError("复制失败：剪贴板可能被其他程序占用。");
            }
        }

        private void InboundStopButton_Click(object sender, RoutedEventArgs e)
        {
            StopInboundTest(false);
        }

        private void InboundServer_ClientConnected(object sender, InboundTestClientEventArgs e)
        {
            string remote = e.RemoteEndPoint;
            Dispatcher.BeginInvoke(new Action(delegate { HandleInboundConnected(remote); }));
        }

        private void InboundServer_ServerError(object sender, InboundTestClientEventArgs e)
        {
            string message = e.RemoteEndPoint;
            Dispatcher.BeginInvoke(new Action(delegate
            {
                InboundStatusText.Text = message;
                InboundStatusText.Foreground = UiPalette.WarnFg;
            }));
        }

        private void HandleInboundConnected(string remote)
        {
            InboundStatusText.Text = "检测成功！已收到来自 " + remote + " 的入站连接，IPv6 入站可达。";
            InboundStatusText.Foreground = UiPalette.SuccessFg;
            if (_inboundDialog != null)
            {
                _inboundDialog.NotifyConnected(remote);
            }

            StopInboundTest(true);
            InboundStartButton.Content = "重新自检";
            InboundStartButton.IsEnabled = true;
            InboundHintText.Text = "入站路径已验证；该端口未被 Windows 防火墙或光猫/路由器的 IPv6 防火墙拦截。";
        }

        private void StopInboundTest(bool preserveStatus)
        {
            Ipv6InboundTestServer server = _inboundServer;
            _inboundServer = null;

            if (server != null)
            {
                server.ClientConnected -= InboundServer_ClientConnected;
                server.ServerError -= InboundServer_ServerError;
                try { server.Stop(); }
                catch { }
                try { server.Dispose(); }
                catch { }
            }

            InboundStopButton.Visibility = Visibility.Collapsed;
            InboundStartButton.IsEnabled = !string.IsNullOrEmpty(_inboundPublicIpv6);

            if (!preserveStatus)
            {
                InboundUrlPanel.Visibility = Visibility.Collapsed;
                InboundCopyButton.Visibility = Visibility.Collapsed;
                InboundUrlText.Text = "--";
                InboundStartButton.Content = "启动入站自检";
                if (string.IsNullOrEmpty(_inboundPublicIpv6))
                {
                InboundStatusText.Text = "未检测到公网 IPv6，无法进行入站自检。";
                }
                else
                {
                    InboundStatusText.Text = "尚未启动自检";
                }
                InboundStatusText.Foreground = UiPalette.MutedFg;
                InboundHintText.Text = "提示：自检会在本机临时打开一个 TCP 端口，检测结束后自动关闭。";
            }
        }

        private void MainWindow_Closed(object sender, EventArgs e)
        {
            CancelNatTrace();
            StopInboundTest(false);
        }

        private async Task RefreshWebView2Async()
        {
            if (_webView2Busy) return;

            _webView2Busy = true;
            WebView2RefreshButton.IsEnabled = false;
            WebView2StatusChip.Background = UiPalette.GrayBg;
            WebView2StatusText.Foreground = UiPalette.GrayFg;
            WebView2StatusText.Text = "检测中…";

            try
            {
                WebView2DetectionResult result = await Task.Run(delegate
                {
                    return WebView2Detector.Detect();
                });

                if (result == null) throw new InvalidOperationException("WebView2 检测返回了空结果。");
                _webView2Result = result;
                RenderWebView2(result);
            }
            catch (Exception ex)
            {
                WebView2HeadlineText.Text = "检测失败";
                WebView2SummaryText.Text = ex.Message;
                WebView2StatusText.Text = "检测失败";
                WebView2StatusChip.Background = UiPalette.WarnBg;
                WebView2StatusText.Foreground = UiPalette.WarnFg;
                WebView2WarningBanner.Visibility = Visibility.Visible;
                WebView2WarningText.Text = "无法读取 WebView2 状态：" + ex.Message;
                WebView2InstallButton.IsEnabled = false;
                WebView2CopyButton.IsEnabled = false;
                WebView2FolderButton.IsEnabled = false;
            }
            finally
            {
                _webView2Busy = false;
                WebView2RefreshButton.IsEnabled = true;
            }
        }

        private void RenderWebView2(WebView2DetectionResult result)
        {
            WebView2Installation primary = result.Primary;

            Brush statusBg;
            Brush statusFg;
            switch (result.Status)
            {
                case WebView2Status.Installed:
                    statusBg = UiPalette.SuccessBg;
                    statusFg = UiPalette.SuccessFg;
                    break;
                case WebView2Status.NotInstalled:
                    statusBg = UiPalette.WarnBg;
                    statusFg = UiPalette.WarnFg;
                    break;
                case WebView2Status.Broken:
                    statusBg = UiPalette.Brush("#FEF2F2");
                    statusFg = UiPalette.Brush("#B91C1C");
                    break;
                default:
                    statusBg = UiPalette.GrayBg;
                    statusFg = UiPalette.GrayFg;
                    break;
            }
            WebView2StatusChip.Background = statusBg;
            WebView2StatusText.Foreground = statusFg;
            WebView2StatusText.Text = result.StatusText;

            string version = primary == null ? string.Empty : primary.Version;
            WebView2HeadlineText.Text = result.StatusText
                + (string.IsNullOrEmpty(version) ? string.Empty : " · " + version);

            if (result.Status == WebView2Status.Installed && primary != null)
            {
                WebView2SummaryText.Text = primary.Scope + " · " + result.OsArch + " · 文件完整";
                WebView2InstallButton.Content = "修复 / 重新安装";
                WebView2HintText.Text = "如应用仍提示缺少 WebView2，可尝试修复安装；安装包从 Microsoft 官方服务器下载并校验数字签名。";
            }
            else if (result.Status == WebView2Status.Broken && primary != null)
            {
                WebView2SummaryText.Text = "检测到安装记录，但 msedgewebview2.exe 缺失，建议修复安装。";
                WebView2InstallButton.Content = "修复安装";
                WebView2HintText.Text = "注册表存在安装记录但文件缺失，通常由手动删除目录或安装中断引起。";
            }
            else if (result.Status == WebView2Status.NotInstalled)
            {
                WebView2SummaryText.Text = "未检测到 Evergreen 运行库，可以从 Microsoft 官方服务器安装。";
                WebView2InstallButton.Content = "立即安装 WebView2";
                WebView2HintText.Text = "安装包从 Microsoft 官方服务器下载，并校验 Microsoft 数字签名后静默安装。";
            }
            else
            {
                WebView2SummaryText.Text = "当前状态未知，请点击「重新检测」。";
                WebView2InstallButton.Content = "安装 / 修复 WebView2";
                WebView2HintText.Text = "WebView2 检测用于排查应用启动时的运行库缺失问题。";
            }

            WebView2VersionText.Text = "版本：" + NullSafe(primary == null ? null : primary.Version);
            WebView2ScopeText.Text = "范围：" + NullSafe(primary == null ? null : primary.Scope);
            WebView2ArchText.Text = "架构：" + NullSafe(result.OsArch);
            WebView2SourceText.Text = "来源：" + NullSafe(primary == null ? null : primary.Source);
            WebView2PathText.Text = "路径：" + NullSafe(primary == null ? null : primary.Location);
            WebView2EdgeText.Text = "Edge 浏览器：" + (string.IsNullOrEmpty(result.EdgeVersion) ? "未检测到" : result.EdgeVersion);
            WebView2PermissionText.Text = "权限：" + (result.IsAdmin ? "管理员" : "普通用户");

            WebView2InstallButton.IsEnabled = true;
            WebView2CopyButton.IsEnabled = true;
            WebView2FolderButton.IsEnabled = primary != null
                && !string.IsNullOrEmpty(primary.Location)
                && Directory.Exists(primary.Location);

            List<string> warnings = new List<string>();
            if (result.Warnings != null)
            {
                for (int i = 0; i < result.Warnings.Count; i++)
                {
                    if (!string.IsNullOrEmpty(result.Warnings[i])) warnings.Add(result.Warnings[i]);
                }
            }
            if (warnings.Count > 0)
            {
                WebView2WarningBanner.Visibility = Visibility.Visible;
                WebView2WarningText.Text = string.Join("  ·  ", warnings.ToArray());
            }
            else
            {
                WebView2WarningBanner.Visibility = Visibility.Collapsed;
                WebView2WarningText.Text = string.Empty;
            }
        }

        private async void WebView2RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshWebView2Async();
        }

        private void WebView2InstallButton_Click(object sender, RoutedEventArgs e)
        {
            if (_webView2Result == null) return;
            if (_webView2InstallDialog != null)
            {
                _webView2InstallDialog.Activate();
                return;
            }

            WebView2InstallDialog dialog = new WebView2InstallDialog(_webView2Result);
            dialog.Owner = this;
            _webView2InstallDialog = dialog;
            WebView2InstallButton.IsEnabled = false;
            dialog.Closed += async delegate
            {
                _webView2InstallDialog = null;
                WebView2InstallButton.IsEnabled = true;
                await RefreshWebView2Async();
            };
            dialog.Show();
        }

        private void WebView2CopyButton_Click(object sender, RoutedEventArgs e)
        {
            if (_webView2Result == null) return;
            string text = _webView2Result.ToDiagnostics();
            if (CopyToClipboard(text))
            {
                ShowCopyFeedback((Button)sender);
            }
            else
            {
                ShowError("复制失败：剪贴板可能被其他程序占用。");
            }
        }

        private void WebView2FolderButton_Click(object sender, RoutedEventArgs e)
        {
            if (_webView2Result == null || _webView2Result.Primary == null) return;
            try
            {
                WebView2InstallerService.OpenFolder(_webView2Result.Primary.Location);
            }
            catch (Exception ex)
            {
                ShowError("打开安装目录失败：" + ex.Message);
            }
        }

        private void WebView2OfficialButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                WebView2InstallerService.OpenUrl(WebView2InstallerService.OfficialDownloadPage);
            }
            catch (Exception ex)
            {
                ShowError("打开官方下载页失败：" + ex.Message);
            }
        }

        private static string NullSafe(string value)
        {
            return string.IsNullOrEmpty(value) ? "—" : value;
        }

        private void RevealToggle_Checked(object sender, RoutedEventArgs e)
        {
            _revealed = true;
            RevealToggle.Content = "隐藏 IP";
            RevealToggle.Foreground = UiPalette.IndigoFg;
            if (_report != null) RenderReport(_report);
            if (_natTraceResult != null) RenderNatTrace(_natTraceResult);
        }

        private void RevealToggle_Unchecked(object sender, RoutedEventArgs e)
        {
            _revealed = false;
            RevealToggle.Content = "显示 IP";
            RevealToggle.Foreground = Brushes.White;
            if (_report != null) RenderReport(_report);
            if (_natTraceResult != null) RenderNatTrace(_natTraceResult);
        }

        private void CopyIpButton_Click(object sender, RoutedEventArgs e)
        {
            Button button = sender as Button;
            if (button == null) return;

            string text = button.Tag as string;
            if (string.IsNullOrEmpty(text))
            {
                ShowError("没有可复制的 IP 地址。");
                return;
            }

            if (CopyToClipboard(text))
            {
                ShowCopyFeedback(button);
            }
            else
            {
                ShowError("复制失败：剪贴板可能被其他程序占用，请稍后重试。");
            }
        }

        private static bool CopyToClipboard(string text)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    Clipboard.SetText(text);
                    return true;
                }
                catch
                {
                    Thread.Sleep(40);
                }
            }
            return false;
        }

        private void ShowCopyFeedback(Button button)
        {
            if (_copyFeedbackTimer != null)
            {
                _copyFeedbackTimer.Stop();
                _copyFeedbackTimer = null;
            }
            if (_lastCopyButton != null)
            {
                _lastCopyButton.Content = _lastCopyContent;
            }

            _lastCopyButton = button;
            _lastCopyContent = button.Content;
            button.Content = "已复制";

            DispatcherTimer timer = new DispatcherTimer();
            timer.Interval = TimeSpan.FromMilliseconds(1200);
            timer.Tick += delegate
            {
                timer.Stop();
                if (_lastCopyButton != null)
                {
                    _lastCopyButton.Content = _lastCopyContent;
                }
                _lastCopyButton = null;
                _lastCopyContent = null;
                _copyFeedbackTimer = null;
            };
            _copyFeedbackTimer = timer;
            timer.Start();
        }

        private void ShowError(string message)
        {
            ErrorText.Text = MaskKnownIps(message, _report);
            ErrorBanner.Visibility = Visibility.Visible;
        }

        private void HideError()
        {
            ErrorBanner.Visibility = Visibility.Collapsed;
            ErrorText.Text = string.Empty;
        }

        /// <summary>联网检测整体失败时，至少把本机地址展示出来。</summary>
        private void RenderFailureFallback(Exception ex)
        {
            NetworkReport fallback = new NetworkReport();
            try
            {
                fallback.Ipv4Addresses.AddRange(LocalAddressProvider.GetIpv4Addresses());
                fallback.Ipv6Addresses.AddRange(LocalAddressProvider.GetIpv6Addresses());
            }
            catch
            {
            }

            fallback.Timestamp = DateTime.Now;
            fallback.Error = "检测失败：" + ex.Message;
            fallback.PublicIp.Summary = "公网出口检测失败，仅显示本机地址。请检查网络连接，或确认 UDP / STUN 未被防火墙拦截。";
            fallback.Nat.TypeLabel = "检测失败";
            fallback.Nat.Description = "无法完成 STUN 检测，NAT 类型未知。";
            fallback.Nat.Error = "检测失败：" + ex.Message;
            try
            {
                fallback.Firewall = WindowsFirewallReader.Read();
            }
            catch
            {
            }

            _report = fallback;
            RenderReport(fallback);
        }

        /// <summary>离屏截图（自动验证用）：等待首次检测完成后渲染窗口为 PNG。</summary>
        public async Task RenderSnapshotAsync(string path, bool reveal, bool full)
        {
            if (reveal && !_revealed) RevealToggle.IsChecked = true;

            await _initialRefresh.Task;
            await Task.Delay(500);

            if (full)
            {
                RenderFullPageToPng(path);
            }
            else
            {
                UpdateLayout();
                RenderToPng(path);
            }

            StopInboundTest(false);
            Close();
        }

        /// <summary>
        /// 渲染完整页面（不裁剪 ScrollViewer）：分别渲染标题栏、内容区和底部状态栏，
        /// 再用 DrawingVisual 拼成一张长图，用于文档 / README 截图。
        /// </summary>
        private void RenderFullPageToPng(string path)
        {
            FrameworkElement contentElement = NetworkScrollViewer.Content as FrameworkElement;
            if (contentElement == null)
            {
                UpdateLayout();
                RenderToPng(path);
                return;
            }

            NetworkScrollViewer.ScrollToTop();
            NetworkScrollViewer.ScrollToHorizontalOffset(0);
            UpdateLayout();

            const double dpi = 144.0;
            double scale = dpi / 96.0;
            double width = ActualWidth;
            double headerHeight = HeaderBorder.ActualHeight;
            double footerHeight = FooterBorder.ActualHeight;
            double paddingTop = NetworkScrollViewer.Padding.Top;
            double paddingBottom = NetworkScrollViewer.Padding.Bottom;
            double paddingLeft = NetworkScrollViewer.Padding.Left;
            double contentWidth = contentElement.ActualWidth;
            double contentHeight = contentElement.ActualHeight;
            double pageHeight = headerHeight + paddingTop + contentHeight + paddingBottom + footerHeight;

            int pixelWidth = (int)Math.Ceiling(width * scale);
            int pixelHeight = (int)Math.Ceiling(pageHeight * scale);

            RenderTargetBitmap headerBitmap = RenderElement(HeaderBorder, width, headerHeight, dpi);
            RenderTargetBitmap contentBitmap = RenderElement(contentElement, contentWidth, contentHeight, dpi);

            DrawingVisual visual = new DrawingVisual();
            using (DrawingContext context = visual.RenderOpen())
            {
                Brush background = Background;
                if (background != null)
                {
                    context.DrawRectangle(background, null, new Rect(0, 0, width, pageHeight));
                }
                context.DrawImage(headerBitmap, new Rect(0, 0, width, headerHeight));
                context.DrawImage(contentBitmap, new Rect(
                    paddingLeft,
                    headerHeight + paddingTop,
                    contentWidth,
                    contentHeight));

                // 底部状态栏：直接用 DrawingContext 绘制，避免单独渲染 Border 时得到空白图。
                double footerTop = headerHeight + paddingTop + contentHeight + paddingBottom;
                context.DrawRectangle(Brushes.White, null, new Rect(0, footerTop, width, footerHeight));
                Pen borderPen = new Pen(UiPalette.Brush("#E8ECF4"), 1);
                context.DrawLine(borderPen, new Point(0, footerTop), new Point(width, footerTop));

                FormattedText leftText = CreateFooterText(TimestampText.Text);
                context.DrawText(leftText, new Point(24, footerTop + (footerHeight - leftText.Height) / 2));

                FormattedText rightText = CreateFooterText(VersionText.Text);
                context.DrawText(rightText, new Point(
                    width - 24 - rightText.Width,
                    footerTop + (footerHeight - rightText.Height) / 2));
            }

            RenderTargetBitmap bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, dpi, dpi, PixelFormats.Pbgra32);
            bitmap.Render(visual);

            PngBitmapEncoder encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            using (FileStream stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                encoder.Save(stream);
            }
        }

        private static RenderTargetBitmap RenderElement(FrameworkElement element, double width, double height, double dpi)
        {
            double scale = dpi / 96.0;
            int pixelWidth = Math.Max(1, (int)Math.Ceiling(width * scale));
            int pixelHeight = Math.Max(1, (int)Math.Ceiling(height * scale));
            RenderTargetBitmap bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, dpi, dpi, PixelFormats.Pbgra32);
            bitmap.Render(element);
            return bitmap;
        }

        private static FormattedText CreateFooterText(string text)
        {
            Typeface typeface = new Typeface(new FontFamily("Microsoft YaHei UI"),
                FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            return new FormattedText(
                string.IsNullOrEmpty(text) ? string.Empty : text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                typeface,
                11.5,
                UiPalette.MutedFg,
                1.5);
        }


        private void RenderToPng(string path)
        {
            double width = ActualWidth;
            double height = ActualHeight;
            if (width <= 0) width = Width;
            if (height <= 0) height = Height;

            const double dpi = 144.0;
            double scale = dpi / 96.0;
            int pixelWidth = (int)Math.Ceiling(width * scale);
            int pixelHeight = (int)Math.Ceiling(height * scale);

            RenderTargetBitmap bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, dpi, dpi, PixelFormats.Pbgra32);
            bitmap.Render(this);

            PngBitmapEncoder encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            using (FileStream stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                encoder.Save(stream);
            }
        }
    }
}

