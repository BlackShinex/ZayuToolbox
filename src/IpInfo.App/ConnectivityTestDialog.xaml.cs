using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using IpInfo.Core;

namespace IpInfo.App
{
    /// <summary>网络连通性测试说明窗口：显示二维码、测试地址和手机连接状态。</summary>
    public partial class ConnectivityTestDialog : Window
    {
        private readonly ConnectivityTestServer _server;
        private readonly Action<ConnectivityTestMode> _onModeShown;
        private ConnectivityTestMode _mode;
        private int _remainingSeconds = 3;
        private bool _canClose;
        private bool _syncingMode;
        private DispatcherTimer _timer;

        public ConnectivityTestDialog(ConnectivityTestServer server, Action<ConnectivityTestMode> onModeShown)
        {
            InitializeComponent();
            _server = server;
            _onModeShown = onModeShown;
            Loaded += ConnectivityTestDialog_Loaded;
            Closing += ConnectivityTestDialog_Closing;
            Closed += ConnectivityTestDialog_Closed;

            bool hasLan = server != null && !string.IsNullOrEmpty(server.LanUrl);
            bool hasWan = server != null && !string.IsNullOrEmpty(server.WanUrl);
            LanModeToggle.IsEnabled = hasLan;
            WanModeToggle.IsEnabled = hasWan;

            if (hasLan)
            {
                SelectMode(ConnectivityTestMode.Lan);
            }
            else if (hasWan)
            {
                SelectMode(ConnectivityTestMode.Wan);
            }
            else
            {
                UrlBox.Text = string.Empty;
                QrImage.Source = null;
                QrFallbackText.Visibility = Visibility.Visible;
                QrFallbackText.Text = "当前网络没有可用于连通性测试的地址。";
                StatusText.Text = "无法生成测试地址";
                ModeHintText.Text = "请先完成网络检测，确认本机有可用的 IPv4 或 IPv6 地址。";
            }
        }

        private void ConnectivityTestDialog_Loaded(object sender, RoutedEventArgs e)
        {
            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += Timer_Tick;
            _timer.Start();
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            _remainingSeconds--;
            if (_remainingSeconds > 0)
            {
                CountdownText.Text = "请阅读说明，" + _remainingSeconds + " 秒后可以关闭";
                return;
            }

            _timer.Stop();
            _timer = null;
            _canClose = true;
            CloseButton.IsEnabled = true;
            CountdownText.Text = "现在可以关闭此窗口";
        }

        private void LanModeToggle_Checked(object sender, RoutedEventArgs e)
        {
            if (_syncingMode) return;
            SelectMode(ConnectivityTestMode.Lan);
        }

        private void LanModeToggle_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_syncingMode) return;
            if (_mode == ConnectivityTestMode.Lan)
            {
                _syncingMode = true;
                LanModeToggle.IsChecked = true;
                _syncingMode = false;
            }
        }

        private void WanModeToggle_Checked(object sender, RoutedEventArgs e)
        {
            if (_syncingMode) return;
            SelectMode(ConnectivityTestMode.Wan);
        }

        private void WanModeToggle_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_syncingMode) return;
            if (_mode == ConnectivityTestMode.Wan)
            {
                _syncingMode = true;
                WanModeToggle.IsChecked = true;
                _syncingMode = false;
            }
        }

        private void SelectMode(ConnectivityTestMode mode)
        {
            _mode = mode;
            if (_onModeShown != null)
            {
                _onModeShown(mode);
            }
            _syncingMode = true;
            LanModeToggle.IsChecked = mode == ConnectivityTestMode.Lan;
            WanModeToggle.IsChecked = mode == ConnectivityTestMode.Wan;
            _syncingMode = false;

            bool lan = mode == ConnectivityTestMode.Lan;
            string url = lan
                ? (_server == null ? null : _server.LanUrl)
                : (_server == null ? null : _server.WanUrl);

            UrlBox.Text = url ?? string.Empty;
            if (lan)
            {
                ModeHintText.Text = "手机连接与电脑相同的 Wi-Fi，用相机扫描二维码。网页会同时测试内网 IPv4 与内网 IPv6。";
                InstructionsText.Text = "步骤：1. 手机连接本机同一 Wi-Fi；2. 扫描上方二维码；3. 保持页面打开几秒，回到电脑查看结果。";
            }
            else
            {
                ModeHintText.Text = "关闭手机 Wi-Fi，使用 4G/5G 移动数据扫描二维码。网页会测试公网 IPv6 能否被外部主动连入。";
                InstructionsText.Text = "步骤：1. 关闭手机 Wi-Fi；2. 打开 4G/5G 移动数据；3. 扫描上方二维码；4. 保持页面打开几秒，回到电脑查看结果。";
            }

            if (string.IsNullOrEmpty(url))
            {
                QrImage.Source = null;
                QrFallbackText.Visibility = Visibility.Visible;
                QrFallbackText.Text = lan
                    ? "当前网络没有可用的内网测试地址。"
                    : "当前网络没有公网 IPv6，无法使用移动数据模式。";
                StatusText.Text = "当前场景不可用";
                StatusText.Foreground = UiPalette.WarnFg;
                return;
            }

            try
            {
                QrImage.Source = QrCodeRenderer.Create(url, 6);
                QrImage.Visibility = Visibility.Visible;
                QrFallbackText.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                QrImage.Source = null;
                QrFallbackText.Visibility = Visibility.Visible;
                QrFallbackText.Text = "二维码生成失败：" + ex.Message + " 请复制地址手动打开。";
            }

            StatusText.Text = "等待手机连接…";
            StatusText.Foreground = UiPalette.MutedFg;
        }

        private void CopyButton_Click(object sender, RoutedEventArgs e)
        {
            if (!ClipboardHelper.TrySetText(UrlBox.Text))
            {
                ClipboardHelper.ShowCopyFailure(CopyButton);
                return;
            }

            CopyButton.Content = "已复制";
            DispatcherTimer reset = new DispatcherTimer();
            reset.Interval = TimeSpan.FromMilliseconds(1200);
            reset.Tick += delegate
            {
                reset.Stop();
                CopyButton.Content = "复制地址";
            };
            reset.Start();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            if (_canClose) Close();
        }

        private void ConnectivityTestDialog_Closing(object sender, CancelEventArgs e)
        {
            if (!_canClose)
            {
                e.Cancel = true;
                CountdownText.Text = "请先阅读说明，" + Math.Max(1, _remainingSeconds) + " 秒后可关闭";
            }
        }

        private void ConnectivityTestDialog_Closed(object sender, EventArgs e)
        {
            if (_timer != null)
            {
                _timer.Stop();
                _timer = null;
            }
        }

        /// <summary>主窗口停止测试时强制关闭，绕过 3 秒阅读倒计时。</summary>
        public void ForceClose()
        {
            _canClose = true;
            if (_timer != null)
            {
                _timer.Stop();
                _timer = null;
            }
            Close();
        }

        /// <summary>收到手机请求后更新窗口状态（由主窗口在 UI 线程调用）。</summary>
        public void NotifyProbe(ConnectivityProbeEventArgs args)
        {
            if (args == null) return;

            if (args.Kind == ConnectivityProbeKind.Page)
            {
                StatusText.Text = "手机已连接，正在检测 "
                    + (string.Equals(args.Mode, "wan", StringComparison.Ordinal) ? "公网 IPv6" : "内网 IPv4 / IPv6")
                    + "…";
                StatusText.Foreground = UiPalette.WarnFg;
            }
            else if (args.Kind == ConnectivityProbeKind.Ipv4Probe)
            {
                StatusText.Text = "已收到内网 IPv4 探测请求。";
                StatusText.Foreground = UiPalette.SuccessFg;
            }
            else if (args.Kind == ConnectivityProbeKind.Ipv6Probe)
            {
                StatusText.Text = string.Equals(args.Mode, "wan", StringComparison.Ordinal)
                    ? "已收到公网 IPv6 探测请求。"
                    : "已收到内网 IPv6 探测请求。";
                StatusText.Foreground = UiPalette.SuccessFg;
            }

            ProbeText.Text = "手机来源：" + IpPrivacy.Mask(
                string.IsNullOrEmpty(args.RemoteAddress) ? "未知" : args.RemoteAddress)
                + " · 结果已同步到主窗口。";
        }
    }
}
