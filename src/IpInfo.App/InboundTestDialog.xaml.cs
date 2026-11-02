using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;

namespace IpInfo.App
{
    public partial class InboundTestDialog : Window
    {
        private int _remainingSeconds = 3;
        private bool _canClose;
        private DispatcherTimer _timer;

        public InboundTestDialog(string url)
        {
            InitializeComponent();
            UrlBox.Text = url;
            Loaded += InboundTestDialog_Loaded;
            Closing += InboundTestDialog_Closing;
            Closed += InboundTestDialog_Closed;
        }

        private void InboundTestDialog_Loaded(object sender, RoutedEventArgs e)
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

        private void CopyButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(UrlBox.Text);
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
            catch
            {
                MessageBox.Show(this, "复制失败：剪贴板可能被其他程序占用。", "IPv6 入站自检",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            if (_canClose) Close();
        }

        private void InboundTestDialog_Closing(object sender, CancelEventArgs e)
        {
            if (!_canClose)
            {
                e.Cancel = true;
                CountdownText.Text = "请先阅读说明，" + Math.Max(1, _remainingSeconds) + " 秒后可关闭";
            }
        }

        private void InboundTestDialog_Closed(object sender, EventArgs e)
        {
            if (_timer != null)
            {
                _timer.Stop();
                _timer = null;
            }
        }

        /// <summary>检测到入站连接时更新窗口内的状态（由主窗口在 UI 线程调用）。</summary>
        public void NotifyConnected(string remoteEndPoint)
        {
            StatusText.Text = "检测成功！已收到来自 " + remoteEndPoint + " 的入站连接。";
            StatusText.Foreground = UiPalette.SuccessFg;
        }
    }
}
