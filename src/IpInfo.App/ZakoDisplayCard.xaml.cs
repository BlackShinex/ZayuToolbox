using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using IpInfo.Core;

namespace IpInfo.App
{
    /// <summary>Zako 虚拟显示器状态卡片。</summary>
    public partial class ZakoDisplayCard : UserControl
    {
        private ZakoDisplayStatus _status;
        private bool _busy;
        private DispatcherTimer _copyFeedbackTimer;
        private Button _copyFeedbackButton;
        private object _copyFeedbackContent;

        public ZakoDisplayCard()
        {
            InitializeComponent();
            Loaded += ZakoDisplayCard_Loaded;
        }

        private async void ZakoDisplayCard_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= ZakoDisplayCard_Loaded;
            await RefreshAsync();
        }

        /// <summary>重新检测；供版本自检卡片联动调用。</summary>
        public async Task RefreshAsync()
        {
            if (_busy) return;

            _busy = true;
            ZakoRefreshButton.IsEnabled = false;
            ZakoStatusChip.Background = UiPalette.GrayBg;
            ZakoStatusText.Foreground = UiPalette.GrayFg;
            ZakoStatusText.Text = "检测中…";

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

                string installPath = edition.InstallPath;
                ZakoDisplayStatus status = await Task.Run(delegate
                {
                    return ZakoDisplayDetector.Detect(installPath);
                });

                if (status == null) throw new InvalidOperationException("Zako 检测返回了空结果。");
                _status = status;
                Render(status);
            }
            catch (Exception ex)
            {
                ZakoHeadlineText.Text = "检测失败";
                ZakoSummaryText.Text = ex.Message;
                ZakoStatusChip.Background = UiPalette.WarnBg;
                ZakoStatusText.Foreground = UiPalette.WarnFg;
                ZakoStatusText.Text = "检测失败";
                ZakoWarningBanner.Visibility = Visibility.Visible;
                ZakoWarningText.Text = "无法读取 Zako 设备状态：" + ex.Message;
                ZakoCopyButton.IsEnabled = false;
            }
            finally
            {
                _busy = false;
                ZakoRefreshButton.IsEnabled = true;
            }
        }

        /// <summary>非基地版不启用该功能。</summary>
        private void RenderLocked(SunshineDetectionResult edition)
        {
            ZakoStatusChip.Background = UiPalette.GrayBg;
            ZakoStatusText.Foreground = UiPalette.GrayFg;
            ZakoStatusText.Text = "仅基地版可用";

            ZakoHeadlineText.Text = "需要基地版 Sunshine";
            string current = edition == null || edition.Kind == SunshineBuildKind.Unknown
                ? "未检测到 Sunshine"
                : "当前检测到 " + edition.KindLabel;
            ZakoSummaryText.Text = current + "，Zako 虚拟显示器检测仅对基地版开放。";
            ZakoHintText.Text = "安装基地版后回到本页会自动启用。";

            ZakoOsText.Text = "系统：—";
            ZakoAdapterText.Text = "适配器：—";
            ZakoProblemText.Text = "设备状态：—";
            ZakoDriverText.Text = "驱动版本：—";
            ZakoBundledText.Text = "安装包驱动：—";
            ZakoDeviceCountText.Text = "Zako 设备数：—";
            ZakoDeviceListText.Text = string.Empty;

            ZakoCopyButton.IsEnabled = false;
            ZakoWarningBanner.Visibility = Visibility.Collapsed;
            ZakoWarningText.Text = string.Empty;
        }

        private void Render(ZakoDisplayStatus status)
        {
            Brush chipBackground;
            Brush chipForeground;
            switch (status.State)
            {
                case ZakoDisplayState.Healthy:
                    chipBackground = UiPalette.SuccessBg;
                    chipForeground = UiPalette.SuccessFg;
                    break;
                case ZakoDisplayState.NotInstalled:
                    chipBackground = UiPalette.GrayBg;
                    chipForeground = UiPalette.GrayFg;
                    break;
                case ZakoDisplayState.Disabled:
                    chipBackground = UiPalette.WarnBg;
                    chipForeground = UiPalette.WarnFg;
                    break;
                default:
                    chipBackground = UiPalette.OrangeBg;
                    chipForeground = UiPalette.OrangeFg;
                    break;
            }

            ZakoStatusChip.Background = chipBackground;
            ZakoStatusText.Foreground = chipForeground;
            ZakoStatusText.Text = status.StateLabel;

            ZakoHeadlineText.Text = status.Summary;
            ZakoSummaryText.Text = string.IsNullOrEmpty(status.OsDisplay) ? "系统版本未知" : status.OsDisplay;

            if (status.State == ZakoDisplayState.NotInstalled)
            {
                ZakoHintText.Text = string.IsNullOrEmpty(status.BundledDriverVersion)
                    ? "未找到 Zako 显示适配器，安装包目录里也没有驱动文件。"
                    : "未找到 Zako 显示适配器，但安装包自带驱动 " + status.BundledDriverVersion + "，重新安装基地版即可恢复。";
            }
            else if (status.State == ZakoDisplayState.MultipleAdapters)
            {
                ZakoHintText.Text = "检测到多个 Zako 显示适配器，通常由重复安装或旧驱动残留导致，建议卸载后重新安装基地版。";
            }
            else if (status.DriverVersionMatch == ZakoDriverVersionMatch.Mismatch)
            {
                ZakoHintText.Text = "已安装驱动 " + NullSafe(status.Adapter == null ? null : status.Adapter.DriverVersion)
                    + " 与安装包 " + status.BundledDriverVersion + " 不一致，建议重新安装基地版对齐驱动。";
            }
            else if (status.State == ZakoDisplayState.Healthy)
            {
                ZakoHintText.Text = status.DriverVersionMatch == ZakoDriverVersionMatch.Match
                    ? "设备工作正常，驱动版本与安装包一致。设备管理器里出现「未连接」（Code 45）的显示器通常属于空闲状态。"
                    : "设备工作正常。设备管理器里出现「未连接」（Code 45）的显示器通常属于空闲状态。";
            }
            else
            {
                ZakoHintText.Text = "设备异常时先尝试重新安装基地版；不要手动卸载驱动，以免影响串流。";
            }

            ZakoOsText.Text = "系统：" + NullSafe(status.OsDisplay);

            if (status.Adapter != null)
            {
                ZakoAdapterText.Text = "适配器：" + NullSafe(status.Adapter.Name);
                ZakoProblemText.Text = "设备状态：" + NullSafe(status.Adapter.Status)
                    + " · " + NullSafe(status.Adapter.ProblemText);
            }
            else
            {
                ZakoAdapterText.Text = "适配器：未检测到";
                ZakoProblemText.Text = "设备状态：—";
            }

            ZakoDriverText.Text = "驱动版本：" + BuildDriverText(status.Adapter);
            ZakoBundledText.Text = "安装包驱动：" + BuildBundledDriverText(status);

            ZakoDeviceCountText.Text = "Zako 设备数：" + status.Devices.Count;

            List<string> names = new List<string>();
            for (int i = 0; i < status.Devices.Count && i < 4; i++)
            {
                names.Add(status.Devices[i].Name);
            }
            ZakoDeviceListText.Text = names.Count > 0 ? string.Join("、", names.ToArray()) : string.Empty;

            ZakoCopyButton.IsEnabled = true;

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
                ZakoWarningBanner.Visibility = Visibility.Visible;
                ZakoWarningText.Text = string.Join("  ·  ", warnings.ToArray());
            }
            else
            {
                ZakoWarningBanner.Visibility = Visibility.Collapsed;
                ZakoWarningText.Text = string.Empty;
            }
        }

        private static string BuildBundledDriverText(ZakoDisplayStatus status)
        {
            if (string.IsNullOrEmpty(status.BundledDriverVersion)) return "未找到";

            string text = status.BundledDriverVersion;
            if (!string.IsNullOrEmpty(status.BundledDriverDate)) text += "（" + status.BundledDriverDate + "）";
            return text + " · " + status.DriverVersionMatchLabel;
        }

        private static string BuildDriverText(ZakoDeviceInfo adapter)
        {
            if (adapter == null || string.IsNullOrEmpty(adapter.DriverVersion)) return "—";

            string text = adapter.DriverVersion;
            if (!string.IsNullOrEmpty(adapter.DriverDate)) text += "（" + adapter.DriverDate + "）";
            if (!string.IsNullOrEmpty(adapter.DriverProvider)) text += " · " + adapter.DriverProvider;
            return text;
        }

        private async void ZakoRefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshAsync();
        }

        private void ZakoCopyButton_Click(object sender, RoutedEventArgs e)
        {
            if (_status == null) return;

            if (CopyToClipboard(_status.ToDiagnostics()))
            {
                ShowCopyFeedback((Button)sender);
            }
            else
            {
                MessageBox.Show("复制失败：剪贴板可能被其他程序占用。", "复制诊断",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
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
                catch (Exception)
                {
                    Thread.Sleep(30);
                }
            }
            return false;
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
