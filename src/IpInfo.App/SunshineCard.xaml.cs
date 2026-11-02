using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using IpInfo.Core;

namespace IpInfo.App
{
    /// <summary>Sunshine 版本自检卡片。</summary>
    public partial class SunshineCard : UserControl
    {
        private SunshineDetectionResult _result;
        private bool _busy;
        private DispatcherTimer _copyFeedbackTimer;
        private Button _copyFeedbackButton;
        private object _copyFeedbackContent;

        public SunshineCard()
        {
            InitializeComponent();
            Loaded += SunshineCard_Loaded;
        }

        private async void SunshineCard_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= SunshineCard_Loaded;
            await RefreshAsync();
        }

        private async Task RefreshAsync()
        {
            if (_busy) return;

            _busy = true;
            SunshineRefreshButton.IsEnabled = false;
            SunshineStatusChip.Background = UiPalette.GrayBg;
            SunshineStatusText.Foreground = UiPalette.GrayFg;
            SunshineStatusText.Text = "检测中…";

            try
            {
                SunshineDetectionResult result = await Task.Run(delegate
                {
                    return SunshineDetector.Detect();
                });

                if (result == null) throw new InvalidOperationException("Sunshine 检测返回了空结果。");
                _result = result;
                Render(result);
            }
            catch (Exception ex)
            {
                SunshineHeadlineText.Text = "检测失败";
                SunshineSummaryText.Text = ex.Message;
                SunshineStatusChip.Background = UiPalette.WarnBg;
                SunshineStatusText.Foreground = UiPalette.WarnFg;
                SunshineStatusText.Text = "检测失败";
                SunshineWarningBanner.Visibility = Visibility.Visible;
                SunshineWarningText.Text = "无法读取 Sunshine 状态：" + ex.Message;
                SunshineCopyButton.IsEnabled = false;
                SunshineFolderButton.IsEnabled = false;
            }
            finally
            {
                _busy = false;
                SunshineRefreshButton.IsEnabled = true;
            }
        }

        private void Render(SunshineDetectionResult result)
        {
            bool installed = !string.IsNullOrEmpty(result.ExePath);

            Brush chipBackground;
            Brush chipForeground;
            switch (result.Kind)
            {
                case SunshineBuildKind.Official:
                    chipBackground = UiPalette.SuccessBg;
                    chipForeground = UiPalette.SuccessFg;
                    break;
                case SunshineBuildKind.BaseEdition:
                    chipBackground = UiPalette.IndigoBg;
                    chipForeground = UiPalette.IndigoFg;
                    break;
                case SunshineBuildKind.Axixi:
                    chipBackground = UiPalette.WarnBg;
                    chipForeground = UiPalette.WarnFg;
                    break;
                default:
                    chipBackground = UiPalette.GrayBg;
                    chipForeground = UiPalette.GrayFg;
                    break;
            }

            bool leftover = result.InstallState == SunshineInstallState.Leftover;
            bool removed = result.InstallState == SunshineInstallState.Uninstalled;
            bool active = installed && !leftover && !removed;

            if (!active)
            {
                chipBackground = UiPalette.GrayBg;
                chipForeground = UiPalette.GrayFg;
            }

            SunshineStatusChip.Background = chipBackground;
            SunshineStatusText.Foreground = chipForeground;

            if (leftover)
                SunshineStatusText.Text = "未安装";
            else if (removed)
                SunshineStatusText.Text = "已卸载";
            else
                SunshineStatusText.Text = installed ? result.KindLabel : "未安装";

            if (leftover)
            {
                SunshineHeadlineText.Text = "未安装（发现残留文件）";
                SunshineSummaryText.Text = string.IsNullOrEmpty(result.Version)
                    ? "没有服务与安装记录，只找到残留文件。"
                    : "没有服务与安装记录，残留版本 " + result.Version + "。";
                SunshineHintText.Text = "如果确认已经卸载，可以删除安装目录后再点「重新检测」。";
            }
            else if (removed)
            {
                SunshineHeadlineText.Text = "已卸载（注册表残留）";
                SunshineSummaryText.Text = "安装记录还在，但安装目录里找不到 Sunshine.exe。";
                SunshineHintText.Text = "重新安装后点击「重新检测」。";
            }
            else if (!installed)
            {
                SunshineHeadlineText.Text = "未检测到 Sunshine";
                SunshineSummaryText.Text = "未找到 Sunshine.exe，可能尚未安装或安装在非默认位置。";
                SunshineHintText.Text = "安装官方版或基地版后点击「重新检测」。";
            }
            else
            {
                SunshineHeadlineText.Text = result.KindLabel
                    + (string.IsNullOrEmpty(result.Version) ? string.Empty : " · " + result.Version);

                string summary = "置信度 " + result.ConfidenceLabel;
                if (!string.IsNullOrEmpty(result.InstallerKind)) summary += " · " + result.InstallerKind;
                if (!string.IsNullOrEmpty(result.CompanyName)) summary += " · " + result.CompanyName;
                SunshineSummaryText.Text = summary;
                SunshineHintText.Text = BuildHint(result);
            }

            SunshineVersionText.Text = (leftover ? "残留版本：" : "版本：") + NullSafe(result.Version);
            SunshinePublisherText.Text = "发布者：" + NullSafe(result.CompanyName);
            SunshineInstallerText.Text = "安装来源：" + NullSafe(result.InstallerKind);
            SunshineServiceText.Text = "服务：" + (string.IsNullOrEmpty(result.ServiceName)
                ? "未检测到"
                : result.ServiceName + " · " + NullSafe(result.ServiceStartMode));
            SunshinePathText.Text = "安装路径：" + NullSafe(result.InstallPath);
            SunshineServicePathText.Text = "服务路径：" + NullSafe(result.ServicePath);

            SunshineCopyButton.IsEnabled = true;
            SunshineFolderButton.IsEnabled = !string.IsNullOrEmpty(result.InstallPath)
                && Directory.Exists(result.InstallPath);

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
                SunshineWarningBanner.Visibility = Visibility.Visible;
                SunshineWarningText.Text = string.Join("  ·  ", warnings.ToArray());
            }
            else
            {
                SunshineWarningBanner.Visibility = Visibility.Collapsed;
                SunshineWarningText.Text = string.Empty;
            }
        }

        private static string BuildHint(SunshineDetectionResult result)
        {
            switch (result.Kind)
            {
                case SunshineBuildKind.BaseEdition:
                    return "基地版：与本群的适配与更新保持一致。";
                case SunshineBuildKind.Official:
                    return "官方发行版：更新直接从 LizardByte 获取，需要自行配置本群环境。";
                case SunshineBuildKind.Axixi:
                    return "第三方二次分发版本：更新与兼容性由分发者负责，如需本群支持建议改用官方版或基地版。";
                default:
                    return "无法确认发行渠道，点击「复制诊断」把结果发给支持人员。";
            }
        }

        private async void SunshineRefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshAsync();
        }

        private void SunshineCopyButton_Click(object sender, RoutedEventArgs e)
        {
            if (_result == null) return;

            if (CopyToClipboard(_result.ToDiagnostics()))
            {
                ShowCopyFeedback((Button)sender);
            }
            else
            {
                MessageBox.Show("复制失败：剪贴板可能被其他程序占用。", "复制诊断",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void SunshineFolderButton_Click(object sender, RoutedEventArgs e)
        {
            if (_result == null) return;
            if (string.IsNullOrEmpty(_result.InstallPath) || !Directory.Exists(_result.InstallPath)) return;

            try
            {
                Process.Start("explorer.exe", "\"" + _result.InstallPath + "\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show("无法打开安装目录：" + ex.Message, "安装目录",
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
