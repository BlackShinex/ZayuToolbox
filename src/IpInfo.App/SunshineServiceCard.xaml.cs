using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using IpInfo.Core;

namespace IpInfo.App
{
    /// <summary>Sunshine 服务状态卡片。</summary>
    public partial class SunshineServiceCard : UserControl
    {
        private const string ServiceStartArgument = "--service-start";

        private SunshineServiceStatus _status;
        private string _lastServiceError;
        private bool _busy;
        private bool _syncingToggle;
        private DispatcherTimer _copyFeedbackTimer;
        private Button _copyFeedbackButton;
        private object _copyFeedbackContent;

        public SunshineServiceCard()
        {
            InitializeComponent();
            Loaded += SunshineServiceCard_Loaded;
        }

        private async void SunshineServiceCard_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= SunshineServiceCard_Loaded;
            await RefreshAsync();
        }

        /// <summary>重新检测；供版本自检卡片联动调用。</summary>
        public async Task RefreshAsync()
        {
            if (_busy) return;

            _busy = true;
            ServiceRefreshButton.IsEnabled = false;
            ServiceStatusChip.Background = UiPalette.GrayBg;
            ServiceStatusText.Foreground = UiPalette.GrayFg;
            ServiceStatusText.Text = "检测中…";

            try
            {
                SunshineDetectionResult edition = await Task.Run(delegate
                {
                    return SunshineDetector.Detect();
                });

                if (edition == null || edition.Kind != SunshineBuildKind.BaseEdition)
                {
                    _status = null;
                    RenderLocked(edition);
                    return;
                }

                SunshineServiceStatus status = await Task.Run(delegate
                {
                    return SunshineServiceManager.Detect();
                });

                if (status == null) throw new InvalidOperationException("服务检测返回了空结果。");
                _status = status;
                Render(status);
            }
            catch (Exception ex)
            {
                ServiceHeadlineText.Text = "检测失败";
                ServiceSummaryText.Text = ex.Message;
                ServiceStatusChip.Background = UiPalette.WarnBg;
                ServiceStatusText.Foreground = UiPalette.WarnFg;
                ServiceStatusText.Text = "检测失败";
                ServiceWarningBanner.Visibility = Visibility.Visible;
                ServiceWarningText.Text = "无法读取服务状态：" + ex.Message;
                ServiceCopyButton.IsEnabled = false;
                ServiceAutoStartToggle.IsEnabled = false;
            }
            finally
            {
                _busy = false;
                ServiceRefreshButton.IsEnabled = true;
            }
        }

        /// <summary>非基地版不启用该功能，只提示当前检测到的版本。</summary>
        private void RenderLocked(SunshineDetectionResult edition)
        {
            ServiceStatusChip.Background = UiPalette.GrayBg;
            ServiceStatusText.Foreground = UiPalette.GrayFg;
            ServiceStatusText.Text = "仅基地版可用";

            ServiceHeadlineText.Text = "需要基地版 Sunshine";
            string current = edition == null || edition.Kind == SunshineBuildKind.Unknown
                ? "未检测到 Sunshine"
                : "当前检测到 " + edition.KindLabel;
            ServiceSummaryText.Text = current + "，服务检测与开机自启仅对基地版开放。";
            ServiceHintText.Text = "安装基地版后回到本页会自动启用。";

            ServiceNameText.Text = "服务名：—";
            ServiceAccountText.Text = "运行账户：—";
            ServiceProcessText.Text = "进程 PID：—";
            ServicePathText.Text = "可执行文件：—";

            ServiceAutoStartToggle.IsEnabled = false;
            ServiceCopyButton.IsEnabled = false;
            ServiceWarningBanner.Visibility = Visibility.Collapsed;
            ServiceWarningText.Text = string.Empty;
        }

        private void Render(SunshineServiceStatus status)
        {
            Brush chipBackground;
            Brush chipForeground;
            if (!status.IsFound)
            {
                chipBackground = UiPalette.GrayBg;
                chipForeground = UiPalette.GrayFg;
            }
            else if (status.IsRunning)
            {
                chipBackground = UiPalette.SuccessBg;
                chipForeground = UiPalette.SuccessFg;
            }
            else if (status.State == SunshineServiceState.Stopped)
            {
                chipBackground = UiPalette.WarnBg;
                chipForeground = UiPalette.WarnFg;
            }
            else
            {
                chipBackground = UiPalette.OrangeBg;
                chipForeground = UiPalette.OrangeFg;
            }

            ServiceStatusChip.Background = chipBackground;
            ServiceStatusText.Foreground = chipForeground;
            if (!status.IsFound)
            {
                ServiceStatusText.Text = "服务未安装";
            }
            else if (status.IsRunning)
            {
                ServiceStatusText.Text = "服务运行中";
            }
            else if (status.State == SunshineServiceState.Stopped)
            {
                ServiceStatusText.Text = "服务已停止";
            }
            else
            {
                ServiceStatusText.Text = "服务" + status.StateLabel;
            }

            if (!status.IsFound)
            {
                ServiceHeadlineText.Text = "未检测到 Sunshine 服务";
                ServiceSummaryText.Text = "服务未安装，或安装时没有注册系统服务。";
                ServiceHintText.Text = "安装官方版或基地版后点击「重新检测」。";
            }
            else
            {
                ServiceHeadlineText.Text = string.IsNullOrEmpty(status.ServiceName)
                    ? "Sunshine 服务" : status.ServiceName;
                string summary = string.Empty;
                if (status.ProcessId > 0) summary = "PID " + status.ProcessId;
                if (!string.IsNullOrEmpty(status.StartName))
                {
                    summary += (summary.Length > 0 ? " · " : string.Empty) + status.StartName;
                }
                ServiceSummaryText.Text = summary.Length > 0 ? summary : "服务已安装";

                ServiceHintText.Text = "开关只控制是否开机自启；修改启动类型需要管理员权限，会单独弹出 UAC 确认。";
            }

            ServiceNameText.Text = "服务名：" + NullSafe(status.ServiceName);
            ServiceAccountText.Text = "运行账户：" + NullSafe(status.StartName);
            ServiceProcessText.Text = "进程 PID：" + (status.ProcessId > 0 ? status.ProcessId.ToString() : "—");
            ServicePathText.Text = "可执行文件：" + NullSafe(status.ImagePath);

            ServiceCopyButton.IsEnabled = true;
            ServiceAutoStartToggle.IsEnabled = status.IsFound;

            _syncingToggle = true;
            ServiceAutoStartToggle.IsChecked = status.IsAutoStart;
            _syncingToggle = false;

            List<string> warnings = new List<string>();
            if (status.Warnings != null)
            {
                for (int i = 0; i < status.Warnings.Count; i++)
                {
                    if (!string.IsNullOrEmpty(status.Warnings[i])) warnings.Add(status.Warnings[i]);
                }
            }
            if (!string.IsNullOrEmpty(status.Error)) warnings.Add(status.Error);

            if (warnings.Count > 0)
            {
                ServiceWarningBanner.Visibility = Visibility.Visible;
                ServiceWarningText.Text = string.Join("  ·  ", warnings.ToArray());
            }
            else
            {
                ServiceWarningBanner.Visibility = Visibility.Collapsed;
                ServiceWarningText.Text = string.Empty;
            }
        }

        private async void ServiceAutoStartToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_syncingToggle || _status == null || !_status.IsFound) return;

            bool enable = ServiceAutoStartToggle.IsChecked == true;
            SunshineStartMode target = enable ? SunshineStartMode.Automatic : SunshineStartMode.Manual;

            string action = enable ? "设为开机自启（自动启动）" : "改为手动启动";
            MessageBoxResult confirm = MessageBox.Show(
                "将把 " + _status.ServiceName + " " + action + "。\n\n"
                + "修改服务启动类型需要管理员权限，确认后会弹出 UAC 提示；取消 UAC 则不会修改。",
                "修改服务启动类型",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question);
            if (confirm != MessageBoxResult.OK)
            {
                SyncToggle(_status.IsAutoStart);
                return;
            }

            ServiceAutoStartToggle.IsEnabled = false;
            ServiceRefreshButton.IsEnabled = false;

            bool ok = await Task.Run(delegate
            {
                return TryChangeStartMode(target);
            });

            await RefreshAsync();

            if (!ok && !string.IsNullOrEmpty(_lastServiceError))
            {
                ServiceWarningBanner.Visibility = Visibility.Visible;
                ServiceWarningText.Text = _lastServiceError;
            }
        }

        private bool TryChangeStartMode(SunshineStartMode mode)
        {
            _lastServiceError = string.Empty;

            if (SunshineServiceManager.IsElevated())
            {
                SunshineServiceChangeResult direct = SunshineServiceManager.SetStartMode(mode);
                if (!direct.Success) _lastServiceError = direct.Error;
                return direct.Success;
            }

            try
            {
                ProcessStartInfo info = new ProcessStartInfo();
                info.FileName = Process.GetCurrentProcess().MainModule.FileName;
                info.Arguments = ServiceStartArgument + " " + ToArgument(mode);
                info.UseShellExecute = true;
                info.Verb = "runas";
                info.WindowStyle = ProcessWindowStyle.Hidden;

                using (Process process = Process.Start(info))
                {
                    if (process == null)
                    {
                        _lastServiceError = "无法启动提权进程。";
                        return false;
                    }

                    process.WaitForExit(30000);
                    if (!process.HasExited)
                    {
                        _lastServiceError = "提权进程超时未返回。";
                        return false;
                    }

                    if (process.ExitCode == 0) return true;

                    _lastServiceError = process.ExitCode == 2
                        ? "权限不足，修改未生效。"
                        : "修改失败（退出码 " + process.ExitCode + "）。";
                    return false;
                }
            }
            catch (Win32Exception ex)
            {
                _lastServiceError = ex.NativeErrorCode == 1223 ? "已取消 UAC 授权。" : "提权失败：" + ex.Message;
                return false;
            }
            catch (Exception ex)
            {
                _lastServiceError = "提权失败：" + ex.Message;
                return false;
            }
        }

        private static string ToArgument(SunshineStartMode mode)
        {
            switch (mode)
            {
                case SunshineStartMode.Automatic: return "auto";
                case SunshineStartMode.Manual: return "manual";
                case SunshineStartMode.Disabled: return "disabled";
                default: return "manual";
            }
        }

        private void SyncToggle(bool isAutoStart)
        {
            _syncingToggle = true;
            ServiceAutoStartToggle.IsChecked = isAutoStart;
            _syncingToggle = false;
        }

        private async void ServiceRefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshAsync();
        }

        private void ServiceCopyButton_Click(object sender, RoutedEventArgs e)
        {
            if (_status == null) return;

            if (CopyToClipboard(_status.ToDiagnostics()))
            {
                ShowCopyFeedback((Button)sender);
            }
            else
            {
                ClipboardHelper.ShowCopyFailure((Button)sender);
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
                if (_copyFeedbackButton != null) _copyFeedbackButton.Content = _copyFeedbackContent;
            }

            _copyFeedbackButton = button;
            _copyFeedbackContent = button.Content;
            button.Content = "已复制";

            _copyFeedbackTimer = new DispatcherTimer();
            _copyFeedbackTimer.Interval = TimeSpan.FromMilliseconds(1500);
            _copyFeedbackTimer.Tick += delegate
            {
                _copyFeedbackTimer.Stop();
                if (_copyFeedbackButton != null) _copyFeedbackButton.Content = _copyFeedbackContent;
                _copyFeedbackButton = null;
                _copyFeedbackContent = null;
            };
            _copyFeedbackTimer.Start();
        }

        private static string NullSafe(string value)
        {
            return string.IsNullOrEmpty(value) ? "—" : value;
        }
    }
}
