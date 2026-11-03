using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
        private static readonly Brush BannerRedDot = UiPalette.Brush("#EF4444");

        private NetworkReport _report;
        private bool _revealed;
        private bool _syncingReveal;
        private bool _busy;
        private readonly TaskCompletionSource<bool> _initialRefresh = new TaskCompletionSource<bool>();
        private CancellationTokenSource _cts;
        private DispatcherTimer _copyFeedbackTimer;
        private Button _lastCopyButton;
        private object _lastCopyContent;
        private ConnectivityTestServer _connectivityServer;
        private ConnectivityTestDialog _connectivityDialog;
        private ConnectivityTestSession _connectivitySession;
        private CancellationTokenSource _connectivityProbeCts;
        private DispatcherTimer _connectivityTimer;
        private bool _connectivityBusy;
        private DateTime? _lanModeShownAt;
        private DateTime? _wanModeShownAt;
        private ConnectivityTestResult _lastConnectivityResult;
        private SelfCheckReport _selfCheckReport;
        private bool _selfCheckBusy;
        private bool _selfCheckRevealed;
        private bool _syncingSelfCheckReveal;
        private CancellationTokenSource _selfCheckCts;
        private ComponentStatusReport _componentStatusReport;
        private bool _componentBusy;
        private string _componentInstallPath;
        private CancellationTokenSource _componentCts;
        private WebView2DetectionResult _webView2Result;
        private bool _webView2Busy;
        private WebView2InstallDialog _webView2InstallDialog;

        public MainWindow()
        {
            InitializeComponent();
            RevealToggle.Content = "显示 IP";
            RevealToggle.Foreground = UiPalette.IndigoFg;
            UpdatePageVisibility(NavList.SelectedIndex);
            ResetSelfCheckState();
            ResetComponentStatusState();

            if (VersionCardView != null) VersionCardView.Detected += SunshineVersionCard_Detected;

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

        /// <summary>版本自检刷新后，服务与虚拟显示器卡片跟着刷新（含基地版门槛变化）。</summary>
        private async void SunshineVersionCard_Detected(object sender, EventArgs e)
        {
            await Task.WhenAll(ServiceCardView.RefreshAsync(), ZakoCardView.RefreshAsync());
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

            if (NavList.SelectedIndex == 1 && IsLoaded
                && _componentStatusReport == null && !_componentBusy)
            {
                AutoRunComponentStatus();
            }
        }

        private async void AutoRunComponentStatus()
        {
            await RunComponentStatusAsync();
        }

        private void UpdatePageVisibility(int index)
        {
            if (NetworkScrollViewer == null
                || SunshineScrollViewer == null
                || WebView2ScrollViewer == null
                || SelfCheckScrollViewer == null
                || ComponentStatusScrollViewer == null)
            {
                return;
            }

            SunshineScrollViewer.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
            ComponentStatusScrollViewer.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
            WebView2ScrollViewer.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
            NetworkScrollViewer.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
            SelfCheckScrollViewer.Visibility = index == 4 ? Visibility.Visible : Visibility.Collapsed;

            ScrollViewer selected = index == 0 ? SunshineScrollViewer
                : index == 1 ? ComponentStatusScrollViewer
                : index == 2 ? WebView2ScrollViewer
                : index == 3 ? NetworkScrollViewer
                : SelfCheckScrollViewer;
            if (selected != null)
            {
                selected.Opacity = 1;
                if (IsLoaded)
                {
                    DoubleAnimation fade = new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(180));
                    fade.EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut };
                    selected.BeginAnimation(UIElement.OpacityProperty, fade);
                }
            }
            UpdatePageFooter(index);
        }

        private void UpdatePageFooter(int index)
        {
            if (TimestampText == null) return;
            if (index == 1 && _componentStatusReport != null)
            {
                TimestampText.Text = "组件检测时间 "
                    + _componentStatusReport.Timestamp.ToString("yyyy-MM-dd HH:mm:ss");
            }
            else if (index == 3 && _report != null)
            {
                TimestampText.Text = "检测时间 "
                    + _report.Timestamp.ToString("yyyy-MM-dd HH:mm:ss");
            }
        }

        /// <summary>选择左侧导航页面，供截图模式与自动化使用。</summary>
        public void SelectPage(string pageKey)
        {
            if (NavList == null || string.IsNullOrEmpty(pageKey)) return;
            int index = 0;
            switch (pageKey.ToLowerInvariant())
            {
                case "components": index = 1; break;
                case "webview2": index = 2; break;
                case "network": index = 3; break;
                case "selfcheck": index = 4; break;
                default: index = 0; break;
            }
            NavList.SelectedIndex = index;
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
            ShowNetworkIdleState();
            _initialRefresh.TrySetResult(true);
            await RefreshWebView2Async();
        }

        /// <summary>网络检测改为手动触发，启动时只展示未检测状态。</summary>
        private void ShowNetworkIdleState()
        {
            SummaryBanner.Background = UiPalette.GrayBg;
            SummaryDot.Background = UiPalette.MutedFg;
            SummaryText.Text = "尚未检测，点击「开始检测」开始";
            SummaryText.Foreground = UiPalette.GrayFg;
            RefreshButton.Content = "开始检测";

            Ipv4Summary.Text = "尚未检测";
            Ipv6Summary.Text = "尚未检测";
            Ipv4CountText.Text = "0 个";
            Ipv6CountText.Text = "0 个";
            Ipv4List.ItemsSource = null;
            Ipv6List.ItemsSource = null;
            Ipv4Empty.Visibility = Visibility.Collapsed;
            Ipv6Empty.Visibility = Visibility.Collapsed;

            ExternalIpv4Text.Text = "--";
            ExternalIpv6Text.Text = "--";
            Ipv4StatusText.Text = "未知";
            Ipv6StatusText.Text = "未知";
            Ipv4StatusChip.Background = UiPalette.GrayBg;
            Ipv6StatusChip.Background = UiPalette.GrayBg;
            Ipv4StatusText.Foreground = UiPalette.GrayFg;
            Ipv6StatusText.Foreground = UiPalette.GrayFg;
            ExternalIpv4CopyButton.IsEnabled = false;
            ExternalIpv6CopyButton.IsEnabled = false;

            NatTypeText.Text = "尚未检测";
            NatTypeText.Foreground = UiPalette.MutedFg;
            NatDescriptionText.Text = "点击「重新检测」后开始分析当前网络的 NAT 行为。";
            NatDetailMappingText.Text = "映射行为：--";
            NatDetailPortText.Text = "端口保持：--";
            NatDetailServerText.Text = "STUN 服务器：--";
            NatDetailExpander.Visibility = Visibility.Collapsed;
            StepsList.ItemsSource = null;
            HideError();

            FirewallText.Text = "尚未检测";
            ResetConnectivityState();
            TimestampText.Text = "检测时间 --";
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshAsync();
        }

        private async Task RefreshAsync()
        {
            if (_busy) return;

            await RefreshWebView2Async();

            StopConnectivityTest(false);

            _busy = true;
            RefreshButton.IsEnabled = false;
            ConnectivityStartButton.IsEnabled = false;
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
                RefreshButton.Content = "重新检测";
                ConnectivityStartButton.IsEnabled = HasConnectivityTargets(_report);
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
            RenderConnectivityAvailability(report);

            TimestampText.Text = "检测时间 " + report.Timestamp.ToString("yyyy-MM-dd HH:mm:ss")
                + "  ·  耗时 " + report.Elapsed.TotalSeconds.ToString("0.0") + " 秒";
        }

        private void ConnectivityStartButton_Click(object sender, RoutedEventArgs e)
        {
            StartConnectivityTest();
        }

        private void ConnectivityStopButton_Click(object sender, RoutedEventArgs e)
        {
            StopConnectivityTest(true);
            ConnectivityStatusText.Text = "测试已停止，临时端口已关闭。";
            ConnectivityStatusText.Foreground = UiPalette.MutedFg;
        }

        private void StartConnectivityTest()
        {
            if (_connectivityBusy) return;
            if (_report == null)
            {
                ConnectivityStatusText.Text = "请先完成网络检测，再启动连通性测试。";
                ConnectivityStatusText.Foreground = UiPalette.WarnFg;
                return;
            }

            StopConnectivityTest(false);

            try
            {
                bool bindAllInterfaces = !string.Equals(
                    Environment.GetEnvironmentVariable("IPINFO_CONNECTIVITY_BIND_LOOPBACK"),
                    "1", StringComparison.Ordinal);
                ConnectivityTestOptions options = ConnectivityTestTargets.Create(
                    _report, bindAllInterfaces, null);
                if (string.IsNullOrEmpty(options.LanIpv4Address)
                    && string.IsNullOrEmpty(options.LanIpv6Address)
                    && string.IsNullOrEmpty(options.PublicIpv6Address))
                {
                    ConnectivityStatusText.Text = "没有可用于连通性测试的 IPv4 / IPv6 地址。";
                    ConnectivityStatusText.Foreground = UiPalette.WarnFg;
                    return;
                }

                ConnectivityTestServer server = new ConnectivityTestServer();
                server.ProbeReceived += ConnectivityServer_ProbeReceived;
                server.ServerError += ConnectivityServer_ServerError;
                server.Start(options);

                _connectivityServer = server;
                _connectivitySession = new ConnectivityTestSession(_report, options);
                _connectivityBusy = true;
                _lanModeShownAt = null;
                _wanModeShownAt = null;
                ConnectivityStartButton.IsEnabled = false;
                ConnectivityStartButton.Content = "等待手机连接…";
                ConnectivityStopButton.Visibility = Visibility.Visible;
                ConnectivityStatusText.Text = "测试已启动：同一 Wi-Fi 选内网二维码，移动数据选公网二维码。";
                ConnectivityStatusText.Foreground = UiPalette.WarnFg;

                RenderConnectivityResult(_connectivitySession.Evaluate());
                StartConnectivityProbes();
                OpenConnectivityDialog(server);
                StartConnectivityTimeout();
            }
            catch (Exception ex)
            {
                StopConnectivityTest(false);
                ConnectivityStatusText.Text = "启动连通性测试失败：" + ex.Message;
                ConnectivityStatusText.Foreground = UiPalette.WarnFg;
            }
        }

        private void StartConnectivityProbes()
        {
            _connectivityProbeCts = new CancellationTokenSource();
            CancellationToken token = _connectivityProbeCts.Token;
            Task.Run(delegate
            {
                TcpConnectivityResult ipv4 = ConnectivityProbe.ProbeIpv4(null, token);
                TcpConnectivityResult ipv6 = ConnectivityProbe.ProbeIpv6(null, token);
                try
                {
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        ApplyConnectivityProbes(ipv4, ipv6);
                    }));
                }
                catch
                {
                }
            }, token);
        }

        private void ApplyConnectivityProbes(TcpConnectivityResult ipv4, TcpConnectivityResult ipv6)
        {
            if (_connectivitySession == null || _connectivityServer == null) return;
            _connectivitySession.WanIpv4Probe = ipv4;
            _connectivitySession.WanIpv6Probe = ipv6;
            RenderConnectivityResult(_connectivitySession.Evaluate());
        }

        private void OpenConnectivityDialog(ConnectivityTestServer server)
        {
            ConnectivityTestDialog dialog = new ConnectivityTestDialog(server, RecordConnectivityModeShown);
            dialog.Owner = this;
            _connectivityDialog = dialog;
            dialog.Closed += delegate
            {
                if (ReferenceEquals(_connectivityDialog, dialog))
                {
                    _connectivityDialog = null;
                }
                StopConnectivityTest(true);
            };
            dialog.Show();
        }

        private void RecordConnectivityModeShown(ConnectivityTestMode mode)
        {
            if (mode == ConnectivityTestMode.Lan)
            {
                if (!_lanModeShownAt.HasValue) _lanModeShownAt = DateTime.Now;
            }
            else
            {
                if (!_wanModeShownAt.HasValue) _wanModeShownAt = DateTime.Now;
            }
        }

        /// <summary>二维码展示了足够久但仍没有收到连接，按“已尝试但未连通”处理。</summary>
        private void MarkConnectivityAttempts()
        {
            if (_connectivitySession == null) return;

            DateTime now = DateTime.Now;
            TimeSpan threshold = TimeSpan.FromSeconds(8);
            if (_lanModeShownAt.HasValue
                && now - _lanModeShownAt.Value >= threshold
                && !_connectivitySession.LanModeUsed
                && !_connectivitySession.LanIpv4Reached
                && !_connectivitySession.LanIpv6Reached)
            {
                _connectivitySession.MarkAttempted(ConnectivityTestMode.Lan);
            }

            if (_wanModeShownAt.HasValue
                && now - _wanModeShownAt.Value >= threshold
                && !_connectivitySession.WanModeUsed
                && !_connectivitySession.WanIpv6Reached)
            {
                _connectivitySession.MarkAttempted(ConnectivityTestMode.Wan);
            }
        }

        private void StartConnectivityTimeout()
        {
            _connectivityTimer = new DispatcherTimer();
            _connectivityTimer.Interval = TimeSpan.FromSeconds(90);
            _connectivityTimer.Tick += delegate
            {
                if (_connectivityTimer != null)
                {
                    _connectivityTimer.Stop();
                    _connectivityTimer = null;
                }
                if (_connectivitySession != null)
                {
                    _connectivitySession.FinishedAt = DateTime.Now;
                    RenderConnectivityResult(_connectivitySession.Evaluate());
                }
                StopConnectivityTest(true);
                ConnectivityStatusText.Text = "测试超时，临时端口已自动关闭。";
                ConnectivityStatusText.Foreground = UiPalette.MutedFg;
            };
            _connectivityTimer.Start();
        }

        private void ConnectivityServer_ProbeReceived(object sender, ConnectivityProbeEventArgs e)
        {
            ConnectivityProbeEventArgs args = e;
            try
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    HandleConnectivityProbe(args);
                }));
            }
            catch
            {
            }
        }

        private void ConnectivityServer_ServerError(object sender, ConnectivityErrorEventArgs e)
        {
            string message = e == null ? string.Empty : e.Message;
            try
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (_connectivitySession != null)
                    {
                        _connectivitySession.Error = message;
                    }
                    ConnectivityStatusText.Text = MaskConnectivityText(message);
                    ConnectivityStatusText.Foreground = UiPalette.WarnFg;
                }));
            }
            catch
            {
            }
        }

        private void HandleConnectivityProbe(ConnectivityProbeEventArgs e)
        {
            if (_connectivitySession == null || e == null) return;
            _connectivitySession.Apply(e);
            ConnectivityTestResult result = _connectivitySession.Evaluate();
            RenderConnectivityResult(result);
            if (_connectivityDialog != null)
            {
                _connectivityDialog.NotifyProbe(e);
            }
        }

        private void StopConnectivityTest(bool preserveStatus)
        {
            if (_connectivityTimer != null)
            {
                _connectivityTimer.Stop();
                _connectivityTimer = null;
            }

            if (_connectivityProbeCts != null)
            {
                try { _connectivityProbeCts.Cancel(); }
                catch { }
                try { _connectivityProbeCts.Dispose(); }
                catch { }
                _connectivityProbeCts = null;
            }

            ConnectivityTestServer server = _connectivityServer;
            _connectivityServer = null;
            if (server != null)
            {
                server.ProbeReceived -= ConnectivityServer_ProbeReceived;
                server.ServerError -= ConnectivityServer_ServerError;
                try { server.Stop(); }
                catch { }
                try { server.Dispose(); }
                catch { }
            }

            ConnectivityTestDialog dialog = _connectivityDialog;
            _connectivityDialog = null;
            if (dialog != null)
            {
                try { dialog.ForceClose(); }
                catch { }
            }

            _connectivityBusy = false;
            ConnectivityStopButton.Visibility = Visibility.Collapsed;
            ConnectivityStartButton.Content = "启动测试并显示二维码";
            ConnectivityStartButton.IsEnabled = HasConnectivityTargets(_report);

            if (_connectivitySession != null)
            {
                MarkConnectivityAttempts();
                _connectivitySession.FinishedAt = DateTime.Now;
                RenderConnectivityResult(_connectivitySession.Evaluate());
            }
            if (!preserveStatus)
            {
                _connectivitySession = null;
                ResetConnectivityState();
            }
        }

        private void RenderConnectivityAvailability(NetworkReport report)
        {
            if (_connectivityServer != null) return;

            bool hasTargets = HasConnectivityTargets(report);
            ConnectivityStartButton.IsEnabled = hasTargets;
            if (!hasTargets)
            {
                ConnectivityStatusText.Text = "未检测到可用的 IPv4 / IPv6 地址，无法启动连通性测试。";
                ConnectivityStatusText.Foreground = UiPalette.MutedFg;
            }
            else
            {
                ConnectivityStatusText.Text = "点击「启动测试并显示二维码」，用手机扫码验证内网 / 公网连通性。";
                ConnectivityStatusText.Foreground = UiPalette.MutedFg;
            }
        }

        private static bool HasConnectivityTargets(NetworkReport report)
        {
            if (report == null) return false;
            ConnectivityTestOptions options = ConnectivityTestTargets.Create(report, true, null);
            return !string.IsNullOrEmpty(options.LanIpv4Address)
                || !string.IsNullOrEmpty(options.LanIpv6Address)
                || !string.IsNullOrEmpty(options.PublicIpv6Address);
        }

        private void ResetConnectivityState()
        {
            ConnectivityList.ItemsSource = BuildConnectivityRows(null);
            ConnectivityStatusText.Text = "尚未测试。点击「启动测试并显示二维码」，用手机扫码。";
            ConnectivityStatusText.Foreground = UiPalette.MutedFg;
            ConnectivityStopButton.Visibility = Visibility.Collapsed;
            ConnectivityStartButton.Content = "启动测试并显示二维码";
            ConnectivityStartButton.IsEnabled = HasConnectivityTargets(_report);
            SetConnectivityVerdict("尚未测试",
                "同时验证内网 / 外网的 IPv4 与 IPv6 连通性，再判断能否建立有效 P2P 连接。",
                ConnectivityVerdictLevel.Unknown);
        }

        private void RenderConnectivityResult(ConnectivityTestResult result)
        {
            _lastConnectivityResult = result;
            ConnectivityList.ItemsSource = BuildConnectivityRows(result);

            string status;
            if (result == null)
            {
                status = "尚未测试。";
            }
            else if (result.Complete)
            {
                status = result.Level == ConnectivityVerdictLevel.Bad
                    ? "测试完成：未发现可用的 P2P 路径。"
                    : "测试完成。";
            }
            else if (_connectivitySession != null && _connectivitySession.FinishedAt.HasValue)
            {
                status = "测试已停止，未完成全部场景。";
            }
            else
            {
                status = "测试进行中，等待手机扫码或等待公网探测完成…";
            }
            ConnectivityStatusText.Text = status;
            ConnectivityStatusText.Foreground = result != null && result.Level == ConnectivityVerdictLevel.Bad
                ? BannerRedFg
                : (result != null && result.Level == ConnectivityVerdictLevel.Good
                    ? BannerGreenFg
                    : UiPalette.MutedFg);

            if (result != null)
            {
                SetConnectivityVerdict(MaskConnectivityText(result.Verdict),
                    MaskConnectivityText(result.VerdictDetail), result.Level);
            }
        }

        private List<ConnectivityRow> BuildConnectivityRows(ConnectivityTestResult result)
        {
            List<ConnectivityRow> rows = new List<ConnectivityRow>();
            AddConnectivityRow(rows, result, "lan4", "内网 IPv4", "同一 Wi-Fi 扫码后验证。");
            AddConnectivityRow(rows, result, "lan6", "内网 IPv6", "同一 Wi-Fi 扫码后验证。");
            AddConnectivityRow(rows, result, "wan4", "外网 IPv4", "本机主动探测公网 TCP 出口。");
            AddConnectivityRow(rows, result, "wan6", "外网 IPv6", "移动数据扫码后验证公网入站。");
            return rows;
        }

        private void AddConnectivityRow(List<ConnectivityRow> rows,
            ConnectivityTestResult result, string key, string title, string defaultDetail)
        {
            ConnectivityCheckResult check = result == null ? null : result.Find(key);
            ConnectivityRow row = new ConnectivityRow();
            row.Title = title;

            if (check == null)
            {
                row.Detail = defaultDetail;
                row.StatusText = "待测试";
                row.StatusBackground = UiPalette.GrayBg;
                row.StatusForeground = UiPalette.GrayFg;
                rows.Add(row);
                return;
            }

            row.Detail = string.IsNullOrEmpty(check.Detail) ? "—" : MaskConnectivityText(check.Detail);
            switch (check.State)
            {
                case ConnectivityCheckState.Pass:
                    row.StatusText = "连通";
                    row.StatusBackground = UiPalette.SuccessBg;
                    row.StatusForeground = UiPalette.SuccessFg;
                    break;
                case ConnectivityCheckState.Warn:
                    row.StatusText = "可用 · 注意";
                    row.StatusBackground = UiPalette.WarnBg;
                    row.StatusForeground = UiPalette.WarnFg;
                    break;
                case ConnectivityCheckState.Fail:
                    row.StatusText = "不通";
                    row.StatusBackground = BannerRedBg;
                    row.StatusForeground = BannerRedFg;
                    break;
                case ConnectivityCheckState.Testing:
                    row.StatusText = "测试中";
                    row.StatusBackground = UiPalette.IndigoBg;
                    row.StatusForeground = UiPalette.IndigoFg;
                    break;
                case ConnectivityCheckState.NotApplicable:
                    row.StatusText = "不适用";
                    row.StatusBackground = UiPalette.GrayBg;
                    row.StatusForeground = UiPalette.GrayFg;
                    break;
                default:
                    row.StatusText = "待测试";
                    row.StatusBackground = UiPalette.GrayBg;
                    row.StatusForeground = UiPalette.GrayFg;
                    break;
            }
            rows.Add(row);
        }

        /// <summary>未显示 IP 时，把连通性说明中的来源 IP / 测试端点一并遮罩。</summary>
        private string MaskConnectivityText(string text)
        {
            if (_revealed) return text;
            return MaskIpText(text);
        }

        private static string MaskIpText(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            text = Regex.Replace(text, @"\b(?:\d{1,3}\.){3}\d{1,3}\b",
                delegate(Match match) { return IpPrivacy.Mask(match.Value); });
            text = Regex.Replace(text,
                @"(?<![0-9A-Fa-f])(?:[0-9A-Fa-f]{0,4}:){2,}[0-9A-Fa-f]{0,4}(?![0-9A-Fa-f])",
                delegate(Match match) { return IpPrivacy.Mask(match.Value); });
            return text;
        }

        private void SetConnectivityVerdict(string verdict, string detail, ConnectivityVerdictLevel level)
        {
            Brush background;
            Brush foreground;
            Brush dot;
            switch (level)
            {
                case ConnectivityVerdictLevel.Good:
                    background = BannerGreenBg;
                    foreground = BannerGreenFg;
                    dot = BannerGreenDot;
                    break;
                case ConnectivityVerdictLevel.Warn:
                    background = BannerAmberBg;
                    foreground = BannerAmberFg;
                    dot = BannerAmberDot;
                    break;
                case ConnectivityVerdictLevel.Bad:
                    background = BannerRedBg;
                    foreground = BannerRedFg;
                    dot = BannerRedDot;
                    break;
                default:
                    background = BannerIndigoBg;
                    foreground = BannerIndigoFg;
                    dot = BannerIndigoDot;
                    break;
            }

            ConnectivityVerdictBanner.Background = background;
            ConnectivityVerdictText.Foreground = foreground;
            ConnectivityVerdictDot.Background = dot;
            ConnectivityVerdictText.Text = string.IsNullOrEmpty(detail)
                ? (verdict ?? string.Empty)
                : (verdict ?? string.Empty) + "\n" + detail;
        }
        private async void SelfCheckStartButton_Click(object sender, RoutedEventArgs e)
        {
            await RunSelfCheckAsync();
        }

        private async Task RunSelfCheckAsync()
        {
            if (_selfCheckBusy) return;
            if (_busy)
            {
                SelfCheckStatusText.Text = "网络检测正在进行中，请稍后再试。";
                SelfCheckStatusText.Foreground = UiPalette.WarnFg;
                return;
            }

            _selfCheckBusy = true;
            _selfCheckCts = new CancellationTokenSource();
            CancellationToken token = _selfCheckCts.Token;
            DateTime started = DateTime.Now;

            SelfCheckStartButton.IsEnabled = false;
            SelfCheckStartButton.Content = "正在自检…";
            SelfCheckCopyButton.IsEnabled = false;
            SelfCheckProgressBar.Visibility = Visibility.Visible;
            SelfCheckStatusText.Text = "正在收集诊断信息…";
            SelfCheckStatusText.Foreground = UiPalette.MutedFg;

            IProgress<string> progress = new Progress<string>(delegate(string message)
            {
                SelfCheckStatusText.Text = message;
            });

            try
            {
                ConnectivityTestResult connectivity = _lastConnectivityResult;
                SelfCheckReport report = await Task.Run(delegate
                {
                    return SelfCheckCollector.Collect(_report, connectivity,
                        new Action<string>(progress.Report), token);
                }, token);

                if (report == null) throw new InvalidOperationException("自检返回了空报告。");

                _selfCheckReport = report;
                if (_report == null && report.NetworkReport != null)
                {
                    _report = report.NetworkReport;
                    RenderReport(_report);
                }

                SelfCheckMetaText.Text = "生成时间 " + report.Timestamp.ToString("yyyy-MM-dd HH:mm:ss")
                    + " · 网络数据：" + (report.NetworkIncluded ? "已包含" : "未获取")
                    + " · 连通性：" + (report.ConnectivityIncluded
                        ? (report.ConnectivityComplete ? "已包含上次测试" : "包含上次未完成测试")
                        : "未测试（已跳过）");
                SelfCheckEmptyText.Visibility = Visibility.Collapsed;
                RenderSelfCheckRows();
                SelfCheckCopyButton.IsEnabled = true;

                TimeSpan elapsed = DateTime.Now - started;
                if (string.IsNullOrEmpty(report.Error))
                {
                    SelfCheckStatusText.Text = "自检完成 · 用时 " + elapsed.TotalSeconds.ToString("0.0") + " 秒。";
                    SelfCheckStatusText.Foreground = UiPalette.SuccessFg;
                }
                else
                {
                    SelfCheckStatusText.Text = report.Error;
                    SelfCheckStatusText.Foreground = UiPalette.WarnFg;
                }
            }
            catch (OperationCanceledException)
            {
                SelfCheckStatusText.Text = "自检已取消。";
                SelfCheckStatusText.Foreground = UiPalette.MutedFg;
            }
            catch (Exception ex)
            {
                SelfCheckStatusText.Text = "自检失败：" + ex.Message;
                SelfCheckStatusText.Foreground = UiPalette.WarnFg;
            }
            finally
            {
                _selfCheckBusy = false;
                SelfCheckProgressBar.Visibility = Visibility.Collapsed;
                SelfCheckStartButton.IsEnabled = true;
                SelfCheckStartButton.Content = _selfCheckReport == null ? "开始自检" : "重新自检";
                if (_selfCheckCts != null)
                {
                    _selfCheckCts.Dispose();
                    _selfCheckCts = null;
                }
            }
        }

        private void SelfCheckRevealToggle_Checked(object sender, RoutedEventArgs e)
        {
            if (_syncingSelfCheckReveal) return;
            _selfCheckRevealed = true;
            SyncSelfCheckRevealToggle();
            RenderSelfCheckRows();
        }

        private void SelfCheckRevealToggle_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_syncingSelfCheckReveal) return;
            _selfCheckRevealed = false;
            SyncSelfCheckRevealToggle();
            RenderSelfCheckRows();
        }

        private void SyncSelfCheckRevealToggle()
        {
            if (SelfCheckRevealToggle == null) return;
            SelfCheckRevealToggle.Content = _selfCheckRevealed ? "隐藏 IP" : "显示 IP";
            SelfCheckRevealToggle.Foreground = UiPalette.IndigoFg;
            if (SelfCheckRevealToggle.IsChecked != _selfCheckRevealed)
            {
                _syncingSelfCheckReveal = true;
                SelfCheckRevealToggle.IsChecked = _selfCheckRevealed;
                _syncingSelfCheckReveal = false;
            }
        }

        private void RenderSelfCheckRows()
        {
            if (_selfCheckReport == null) return;

            SelfCheckRowsList.ItemsSource = BuildSelfCheckRows(_selfCheckReport, _selfCheckRevealed);
            SelfCheckPrivacyText.Text = _selfCheckRevealed ? "公网 IP 未打码" : "公网 IP 已打码";
        }

        private static List<SelfCheckRow> BuildSelfCheckRows(SelfCheckReport report, bool revealed)
        {
            List<SelfCheckRow> rows = new List<SelfCheckRow>();
            if (report == null || report.Items == null) return rows;

            for (int i = 0; i < report.Items.Count; i++)
            {
                SelfCheckItem item = report.Items[i];
                SelfCheckRow row = new SelfCheckRow();
                row.Label = item.Label;
                row.IsSection = item.IsSection;
                if (!item.IsSection)
                {
                    row.Value = item.Sensitive && !revealed ? MaskIpText(item.Value) : item.Value;
                }
                rows.Add(row);
            }
            return rows;
        }

        private void SelfCheckCopyButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selfCheckReport == null) return;

            try
            {
                BitmapSource bitmap = RenderSelfCheckCardBitmap();
                if (bitmap == null)
                {
                    ClipboardHelper.ShowCopyFailure(SelfCheckCopyButton);
                    return;
                }

                if (CopyImageToClipboard(bitmap))
                {
                    SelfCheckCopyButton.Content = "已复制截图";
                    DispatcherTimer reset = new DispatcherTimer();
                    reset.Interval = TimeSpan.FromMilliseconds(1500);
                    reset.Tick += delegate
                    {
                        reset.Stop();
                        SelfCheckCopyButton.Content = "复制为截图";
                    };
                    reset.Start();
                }
                else
                {
                    ClipboardHelper.ShowCopyFailure(SelfCheckCopyButton);
                }
            }
            catch
            {
                ClipboardHelper.ShowCopyFailure(SelfCheckCopyButton);
            }
        }

        private static bool CopyImageToClipboard(BitmapSource image)
        {
            for (int attempt = 0; attempt < 6; attempt++)
            {
                try
                {
                    Clipboard.SetImage(image);
                    return true;
                }
                catch
                {
                    Thread.Sleep(60);
                }
            }
            return false;
        }

        private BitmapSource RenderSelfCheckCardBitmap()
        {
            if (_selfCheckReport == null) return null;

            const double cardWidth = 900.0;
            const double padding = 24.0;
            const double labelWidth = 150.0;
            const double columnGap = 12.0;
            const double dpi = 144.0;
            double valueWidth = cardWidth - padding * 2 - labelWidth - columnGap;
            double pixelsPerDip = dpi / 96.0;

            Brush labelBrush = UiPalette.Brush("#475569");
            Brush valueBrush = UiPalette.Brush("#0F172A");
            Brush sectionBrush = UiPalette.Brush("#EEF2FF");
            Brush sectionTextBrush = UiPalette.Brush("#3730A3");
            Brush mutedBrush = UiPalette.Brush("#6E687F");
            Brush borderBrush = UiPalette.Brush("#E2E8F0");
            Pen borderPen = new Pen(borderBrush, 1);

            FormattedText title = CreateSelfCheckText("杂鱼串流工具箱 · 一键自检报告",
                18, valueBrush, true, cardWidth - padding * 2 - 160, pixelsPerDip);
            FormattedText meta = CreateSelfCheckText(SelfCheckMetaText.Text,
                11.5, mutedBrush, false, cardWidth - padding * 2 - 160, pixelsPerDip);
            FormattedText privacy = CreateSelfCheckText(SelfCheckPrivacyText.Text,
                10.5, UiPalette.Brush("#4338CA"), false, 200, pixelsPerDip);
            FormattedText footer = CreateSelfCheckText(SelfCheckFooterText.Text,
                10.5, mutedBrush, false, cardWidth - padding * 2, pixelsPerDip);

            List<SelfCheckRow> rows = BuildSelfCheckRows(_selfCheckReport, _selfCheckRevealed);
            List<SelfCheckDrawRow> drawRows = new List<SelfCheckDrawRow>();
            double contentHeight = padding;
            contentHeight += title.Height + 4;
            contentHeight += meta.Height + 14;

            for (int i = 0; i < rows.Count; i++)
            {
                SelfCheckRow row = rows[i];
                SelfCheckDrawRow draw = new SelfCheckDrawRow();
                draw.Row = row;
                if (row.IsSection)
                {
                    draw.Label = CreateSelfCheckText(row.Label, 12, sectionTextBrush, true,
                        cardWidth - padding * 2, pixelsPerDip);
                    draw.Height = 28;
                }
                else
                {
                    draw.Label = CreateSelfCheckText(row.Label, 11.5, labelBrush, false,
                        labelWidth, pixelsPerDip);
                    draw.Value = CreateSelfCheckText(row.Value, 11.5, valueBrush, false,
                        valueWidth, pixelsPerDip);
                    draw.Height = Math.Max(18, Math.Max(draw.Label.Height, draw.Value.Height)) + 10;
                }
                contentHeight += draw.Height + 2;
                drawRows.Add(draw);
            }

            contentHeight += 10;
            contentHeight += footer.Height;
            contentHeight += padding;
            if (contentHeight < 200) contentHeight = 200;

            DrawingVisual visual = new DrawingVisual();
            using (DrawingContext context = visual.RenderOpen())
            {
                context.DrawRectangle(Brushes.White, null, new Rect(0, 0, cardWidth, contentHeight));
                context.DrawRoundedRectangle(Brushes.White, borderPen,
                    new Rect(0.5, 0.5, cardWidth - 1, contentHeight - 1), 16, 16);

                double y = padding;
                context.DrawText(title, new Point(padding, y));
                y += title.Height + 4;
                context.DrawText(meta, new Point(padding, y));

                double badgeWidth = privacy.Width + 16;
                double badgeHeight = 22;
                double badgeX = cardWidth - padding - badgeWidth;
                context.DrawRoundedRectangle(sectionBrush, null,
                    new Rect(badgeX, padding, badgeWidth, badgeHeight), 7, 7);
                context.DrawText(privacy, new Point(badgeX + 8, padding + (badgeHeight - privacy.Height) / 2));

                y += meta.Height + 14;
                context.DrawLine(new Pen(borderBrush, 1),
                    new Point(padding, y), new Point(cardWidth - padding, y));
                y += 6;

                for (int i = 0; i < drawRows.Count; i++)
                {
                    SelfCheckDrawRow draw = drawRows[i];
                    if (draw.Row.IsSection)
                    {
                        context.DrawRoundedRectangle(sectionBrush, null,
                            new Rect(padding, y, cardWidth - padding * 2, draw.Height), 6, 6);
                        context.DrawText(draw.Label,
                            new Point(padding + 8, y + (draw.Height - draw.Label.Height) / 2));
                    }
                    else
                    {
                        context.DrawText(draw.Label, new Point(padding, y + 2));
                        context.DrawText(draw.Value, new Point(padding + labelWidth + columnGap, y + 2));
                    }
                    y += draw.Height + 2;
                }

                y += 10;
                context.DrawText(footer, new Point(padding, y));
            }

            RenderTargetBitmap bitmap = new RenderTargetBitmap(
                (int)Math.Ceiling(cardWidth * pixelsPerDip),
                (int)Math.Ceiling(contentHeight * pixelsPerDip),
                dpi, dpi, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }

        private static FormattedText CreateSelfCheckText(string text, double size, Brush brush,
            bool semibold, double maxWidth, double pixelsPerDip)
        {
            Typeface typeface = new Typeface(App.AppFontFamily,
                FontStyles.Normal,
                semibold ? FontWeights.SemiBold : FontWeights.Normal,
                FontStretches.Normal);
            FormattedText formatted = new FormattedText(
                string.IsNullOrEmpty(text) ? string.Empty : text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                typeface,
                size,
                brush,
                pixelsPerDip);
            formatted.MaxTextWidth = Math.Max(1, maxWidth);
            return formatted;
        }

        private sealed class SelfCheckDrawRow
        {
            public SelfCheckRow Row;
            public FormattedText Label;
            public FormattedText Value;
            public double Height;
        }

        private void ResetSelfCheckState()
        {
            _selfCheckReport = null;
            SelfCheckRowsList.ItemsSource = null;
            SelfCheckEmptyText.Visibility = Visibility.Visible;
            SelfCheckCopyButton.IsEnabled = false;
            SelfCheckStartButton.IsEnabled = true;
            SelfCheckStartButton.Content = "开始自检";
            SelfCheckMetaText.Text = "尚未生成报告";
            SelfCheckStatusText.Text = "尚未自检，点击「开始自检」生成诊断报告。";
            SelfCheckStatusText.Foreground = UiPalette.MutedFg;
            SelfCheckProgressBar.Visibility = Visibility.Collapsed;
            SyncSelfCheckRevealToggle();
            SelfCheckPrivacyText.Text = _selfCheckRevealed ? "公网 IP 未打码" : "公网 IP 已打码";
        }

        private void ComponentRepairButton_Click(object sender, RoutedEventArgs e)
        {
            ComponentStatusRow row = GetComponentRow(sender);
            if (row == null || !row.CanRepair || string.IsNullOrEmpty(row.Key)) return;

            MessageBoxResult confirm = MessageBox.Show(
                "将修复 " + row.Name + "。\n\n"
                + ComponentOperationCatalog.RepairImpact(row.Key) + "\n\n继续吗？",
                "修复组件",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question);
            if (confirm != MessageBoxResult.OK) return;

            StartComponentOperation(row, ComponentOperationKind.Repair);
        }

        private void ComponentUninstallButton_Click(object sender, RoutedEventArgs e)
        {
            ComponentStatusRow row = GetComponentRow(sender);
            if (row == null || !row.CanUninstall || string.IsNullOrEmpty(row.Key)) return;

            MessageBoxResult confirm = MessageBox.Show(
                "将卸载 " + row.Name + "。\n\n"
                + ComponentOperationCatalog.UninstallImpact(row.Key) + "\n\n此操作会修改系统组件，确认继续吗？",
                "卸载组件",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.OK) return;

            StartComponentOperation(row, ComponentOperationKind.Uninstall);
        }

        private static ComponentStatusRow GetComponentRow(object sender)
        {
            Button button = sender as Button;
            return button == null ? null : button.DataContext as ComponentStatusRow;
        }

        private async void StartComponentOperation(ComponentStatusRow row, ComponentOperationKind kind)
        {
            if (row == null) return;
            ComponentOperationDialog dialog = new ComponentOperationDialog(
                row.Key, row.Name, row.DeviceId, kind, _componentInstallPath);
            dialog.Owner = this;
            dialog.ShowDialog();
            await RunComponentStatusAsync();
        }

        private async void ComponentRefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await RunComponentStatusAsync();
        }

        private async Task RunComponentStatusAsync()
        {
            if (_componentBusy) return;

            _componentBusy = true;
            _componentCts = new CancellationTokenSource();
            CancellationToken token = _componentCts.Token;
            DateTime started = DateTime.Now;

            ComponentRefreshButton.IsEnabled = false;
            ComponentCopyButton.IsEnabled = false;
            ComponentProgressBar.Visibility = Visibility.Visible;
            ComponentStatusText.Text = "正在检测组件…";
            ComponentStatusText.Foreground = UiPalette.MutedFg;

            IProgress<string> progress = new Progress<string>(delegate(string message)
            {
                ComponentStatusText.Text = message;
            });

            try
            {
                ComponentStatusReport report = await Task.Run(delegate
                {
                    return ComponentStatusDetector.Collect(new Action<string>(progress.Report), token);
                }, token);

                if (report == null) throw new InvalidOperationException("组件检测返回了空结果。");
                _componentStatusReport = report;
                _componentInstallPath = report.SunshineInstallPath;
                RenderComponentStatusReport(report);

                TimeSpan elapsed = DateTime.Now - started;
                ComponentStatusText.Text = string.IsNullOrEmpty(report.Error)
                    ? "组件检测完成 · 用时 " + elapsed.TotalSeconds.ToString("0.0") + " 秒。"
                    : report.Error;
                ComponentStatusText.Foreground = string.IsNullOrEmpty(report.Error)
                    ? UiPalette.SuccessFg : UiPalette.WarnFg;
            }
            catch (OperationCanceledException)
            {
                ComponentStatusText.Text = "组件检测已取消。";
                ComponentStatusText.Foreground = UiPalette.MutedFg;
            }
            catch (Exception ex)
            {
                ComponentStatusText.Text = "组件检测失败：" + ex.Message;
                ComponentStatusText.Foreground = UiPalette.WarnFg;
            }
            finally
            {
                _componentBusy = false;
                ComponentProgressBar.Visibility = Visibility.Collapsed;
                ComponentRefreshButton.IsEnabled = true;
                if (_componentCts != null)
                {
                    _componentCts.Dispose();
                    _componentCts = null;
                }
            }
        }

        private void RenderComponentStatusReport(ComponentStatusReport report)
        {
            if (report == null) return;

            ComponentStatusList.ItemsSource = BuildComponentStatusRows(report);
            ComponentCopyButton.IsEnabled = true;

            int ready = 0;
            int missing = 0;
            int errors = 0;
            for (int i = 0; i < report.Items.Count; i++)
            {
                ComponentStatusItem item = report.Items[i];
                if (item.IsSection) continue;
                if (item.Health == ComponentHealth.Ready || item.Health == ComponentHealth.Running
                    || item.Health == ComponentHealth.Installed)
                {
                    ready++;
                }
                else if (item.Health == ComponentHealth.NotInstalled)
                {
                    missing++;
                }
                else if (item.Health == ComponentHealth.Error)
                {
                    errors++;
                }
            }

            ComponentSummaryText.Text = "检测时间 " + report.Timestamp.ToString("yyyy-MM-dd HH:mm:ss")
                + " · 已就绪 " + ready + " 项 · 未安装 " + missing + " 项 · 异常 " + errors + " 项";
            ComponentToolsButton.IsEnabled = !string.IsNullOrEmpty(_componentInstallPath)
                && Directory.Exists(Path.Combine(_componentInstallPath, "tools"));
            UpdatePageFooter(NavList.SelectedIndex);
        }

        private static List<ComponentStatusRow> BuildComponentStatusRows(ComponentStatusReport report)
        {
            List<ComponentStatusRow> rows = new List<ComponentStatusRow>();
            if (report == null || report.Items == null) return rows;

            for (int i = 0; i < report.Items.Count; i++)
            {
                ComponentStatusItem item = report.Items[i];
                ComponentStatusRow row = new ComponentStatusRow();
                row.Name = item.Name;
                row.IsSection = item.IsSection;
                if (!item.IsSection)
                {
                    row.StatusText = item.StatusText;
                    row.Version = item.Version;
                    row.Detail = item.Detail;
                    row.DeviceId = item.DeviceId;
                    row.Key = item.Key;
                    row.CanRepair = item.CanRepair;
                    row.CanUninstall = item.CanUninstall;
                    row.HasActions = item.CanRepair || item.CanUninstall;
                    Brush background;
                    Brush foreground;
                    GetComponentStatusColors(item.Health, out background, out foreground);
                    row.StatusBackground = background;
                    row.StatusForeground = foreground;
                }
                rows.Add(row);
            }
            return rows;
        }

        private static void GetComponentStatusColors(ComponentHealth health, out Brush background, out Brush foreground)
        {
            switch (health)
            {
                case ComponentHealth.Running:
                case ComponentHealth.Ready:
                    background = UiPalette.SuccessBg;
                    foreground = UiPalette.SuccessFg;
                    break;
                case ComponentHealth.Installed:
                    background = UiPalette.IndigoBg;
                    foreground = UiPalette.IndigoFg;
                    break;
                case ComponentHealth.Warning:
                    background = UiPalette.WarnBg;
                    foreground = UiPalette.WarnFg;
                    break;
                case ComponentHealth.Error:
                    background = BannerRedBg;
                    foreground = BannerRedFg;
                    break;
                default:
                    background = UiPalette.GrayBg;
                    foreground = UiPalette.GrayFg;
                    break;
            }
        }

        private void ComponentCopyButton_Click(object sender, RoutedEventArgs e)
        {
            if (_componentStatusReport == null) return;

            string text = BuildComponentDiagnostics(_componentStatusReport);
            if (CopyToClipboard(text))
            {
                ShowCopyFeedback((Button)sender);
            }
            else
            {
                ClipboardHelper.ShowCopyFailure((Button)sender);
            }
        }

        private static string BuildComponentDiagnostics(ComponentStatusReport report)
        {
            List<string> lines = new List<string>();
            lines.Add("杂鱼串流工具箱 · 基地版组件状态");
            lines.Add("检测时间：" + report.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"));
            if (!string.IsNullOrEmpty(report.SunshineInstallPath))
            {
                lines.Add("Sunshine 路径：" + report.SunshineInstallPath);
            }
            for (int i = 0; i < report.Items.Count; i++)
            {
                ComponentStatusItem item = report.Items[i];
                if (item.IsSection)
                {
                    lines.Add("");
                    lines.Add("[" + item.Name + "]");
                }
                else
                {
                    lines.Add(item.Name + "：" + item.StatusText
                        + " · 版本 " + (string.IsNullOrEmpty(item.Version) ? "—" : item.Version)
                        + (string.IsNullOrEmpty(item.Detail) ? string.Empty : " · " + item.Detail)
                        + (string.IsNullOrEmpty(item.DeviceId) ? string.Empty : " · " + item.DeviceId));
                }
            }
            if (!string.IsNullOrEmpty(report.Error)) lines.Add("错误：" + report.Error);
            return string.Join(Environment.NewLine, lines.ToArray());
        }

        private void ComponentDeviceManagerButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo("devmgmt.msc") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show("打开设备管理器失败：" + ex.Message,
                    "杂鱼串流工具箱", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ComponentToolsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (string.IsNullOrEmpty(_componentInstallPath)) return;
                string tools = Path.Combine(_componentInstallPath, "tools");
                if (!Directory.Exists(tools))
                {
                    MessageBox.Show("没有找到 Sunshine tools 目录。",
                        "杂鱼串流工具箱", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                WebView2InstallerService.OpenFolder(tools);
            }
            catch (Exception ex)
            {
                MessageBox.Show("打开工具目录失败：" + ex.Message,
                    "杂鱼串流工具箱", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ResetComponentStatusState()
        {
            _componentStatusReport = null;
            _componentInstallPath = null;
            ComponentStatusList.ItemsSource = null;
            ComponentCopyButton.IsEnabled = false;
            ComponentToolsButton.IsEnabled = false;
            ComponentRefreshButton.IsEnabled = true;
            ComponentSummaryText.Text = "尚未检测";
            ComponentStatusText.Text = "尚未检测，点击「刷新状态」开始。";
            ComponentStatusText.Foreground = UiPalette.MutedFg;
            ComponentProgressBar.Visibility = Visibility.Collapsed;
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
                    FirewallText.Text = "Windows 防火墙：读取完整状态需要管理员权限，可继续用网络连通性测试验证。";
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

        private void MainWindow_Closed(object sender, EventArgs e)
        {
            if (_selfCheckCts != null)
            {
                try { _selfCheckCts.Cancel(); }
                catch { }
            }
            if (_componentCts != null)
            {
                try { _componentCts.Cancel(); }
                catch { }
            }
            StopConnectivityTest(false);
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
                ClipboardHelper.ShowCopyFailure((Button)sender);
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

        private void SunshineOfficialSiteButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                WebView2InstallerService.OpenUrl("https://www.alkaidlab.cn/");
            }
            catch (Exception ex)
            {
                MessageBox.Show("打开 Sunshine 基地版官网失败：" + ex.Message,
                    "杂鱼串流工具箱", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            if (_syncingReveal) return;
            SetRevealed(true);
        }

        private void RevealToggle_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_syncingReveal) return;
            SetRevealed(false);
        }

        private void SetRevealed(bool revealed)
        {
            _revealed = revealed;
            SyncRevealToggle(RevealToggle, revealed);
            if (_report != null) RenderReport(_report);
            if (_connectivitySession != null) RenderConnectivityResult(_connectivitySession.Evaluate());
        }

        private void SyncRevealToggle(ToggleButton toggle, bool revealed)
        {
            if (toggle == null) return;

            toggle.Content = revealed ? "隐藏 IP" : "显示 IP";
            toggle.Foreground = UiPalette.IndigoFg;
            if (toggle.IsChecked != revealed)
            {
                _syncingReveal = true;
                toggle.IsChecked = revealed;
                _syncingReveal = false;
            }
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
                ClipboardHelper.ShowCopyFailure(button);
            }
        }

        private static bool CopyToClipboard(string text)
        {
            return ClipboardHelper.TrySetText(text);
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

        /// <summary>离屏生成组件修复 / 下载进度窗口 PNG，供文档 / 验证使用。</summary>
        public async Task RenderComponentOperationPreviewAsync(string path)
        {
            await _initialRefresh.Task;
            ComponentOperationDialog dialog = new ComponentOperationDialog(
                ComponentOperationCatalog.DualSense,
                ComponentOperationCatalog.DisplayName(ComponentOperationCatalog.DualSense),
                string.Empty,
                ComponentOperationKind.Repair,
                _componentInstallPath,
                true);
            dialog.Owner = this;
            dialog.Show();
            await Task.Delay(400);
            dialog.SavePreviewPng(path);
            dialog.Close();
            Close();
        }

        /// <summary>离屏生成组件状态页 PNG，供文档 / 自动验证使用。</summary>
        public async Task RenderComponentStatusSnapshotAsync(string path)
        {
            await _initialRefresh.Task;
            SelectPage("components");
            if (!_componentBusy)
            {
                await RunComponentStatusAsync();
            }
            else
            {
                for (int i = 0; i < 150 && _componentBusy; i++)
                {
                    await Task.Delay(200);
                }
            }
            await Task.Delay(500);
            RenderToPng(path);
            Close();
        }

        /// <summary>离屏生成一键自检报告 PNG，供文档 / 自动验证使用。</summary>
        public async Task RenderSelfCheckSnapshotAsync(string path)
        {
            await _initialRefresh.Task;
            SelectPage("selfcheck");
            await RunSelfCheckAsync();
            await Task.Delay(300);

            if (_selfCheckReport == null)
            {
                throw new InvalidOperationException("一键自检没有生成报告。");
            }

            BitmapSource image = RenderSelfCheckCardBitmap();
            if (image == null)
            {
                throw new InvalidOperationException("一键自检报告尺寸无效。");
            }

            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            PngBitmapEncoder encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (FileStream stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                encoder.Save(stream);
            }

            Close();
        }

        /// <summary>离屏截图（自动验证用）：等待首次检测完成后渲染窗口为 PNG。</summary>
        public async Task RenderSnapshotAsync(string path, bool reveal, bool full)
        {
            if (reveal && !_revealed) RevealToggle.IsChecked = true;

            await _initialRefresh.Task;

            // 截图模式下网络检测不再自动执行，这里补跑一次，保证截图有数据。
            if (_report == null)
            {
                await RefreshAsync();
            }

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

            StopConnectivityTest(false);
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
            Typeface typeface = new Typeface(App.AppFontFamily,
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

