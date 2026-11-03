using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using IpInfo.Core;

namespace IpInfo.App
{
    /// <summary>组件修复 / 卸载进度窗口。</summary>
    public partial class ComponentOperationDialog : Window
    {
        private readonly string _key;
        private readonly string _displayName;
        private readonly string _deviceId;
        private readonly ComponentOperationKind _kind;
        private readonly string _sunshineInstallPath;
        private CancellationTokenSource _cts;
        private bool _busy;
        private bool _success;
        private string _lastLoggedStage;

        public ComponentOperationDialog(string key, string displayName, string deviceId,
            ComponentOperationKind kind, string sunshineInstallPath)
            : this(key, displayName, deviceId, kind, sunshineInstallPath, false)
        {
        }

        public ComponentOperationDialog(string key, string displayName, string deviceId,
            ComponentOperationKind kind, string sunshineInstallPath, bool preview)
        {
            InitializeComponent();
            _key = key;
            _displayName = displayName;
            _deviceId = deviceId;
            _kind = kind;
            _sunshineInstallPath = sunshineInstallPath;

            string action = kind == ComponentOperationKind.Repair ? "修复" : "卸载";
            Title = action + "组件";
            OperationTitleText.Text = action + " " + displayName;
            OperationSubtitleText.Text = kind == ComponentOperationKind.Repair
                ? "重新安装或修复该组件，不会影响其他组件。"
                : "移除该组件，操作前会再次确认。";
            OperationImpactText.Text = kind == ComponentOperationKind.Repair
                ? ComponentOperationCatalog.RepairImpact(key)
                : ComponentOperationCatalog.UninstallImpact(key);
            OperationLogBox.Text = string.Empty;
            OperationStageText.Text = "准备中…";
            OperationSpeedText.Text = string.Empty;
            OperationEtaText.Text = string.Empty;
            OperationPercentText.Text = string.Empty;

            if (preview)
            {
                LoadPreview();
                return;
            }

            Loaded += ComponentOperationDialog_Loaded;
            Closing += ComponentOperationDialog_Closing;
        }

        private void LoadPreview()
        {
            OperationProgressBar.IsIndeterminate = false;
            OperationProgressBar.Value = 42;
            OperationPercentText.Text = "42%";
            OperationStageText.Text = "正在下载组件包…";
            OperationSpeedText.Text = "12.34 MB/s";
            OperationEtaText.Text = "剩余 00:12";
            Log("正在从基地版官方 Release 下载组件包…");
            Log("下载完成后会校验 SHA-256，再写入 Sunshine 组件目录。");
            CancelButton.Visibility = Visibility.Collapsed;
            CloseButton.Visibility = Visibility.Visible;
            CloseButton.Content = "关闭";
        }

        /// <summary>把预览窗口保存为 PNG，供文档 / 验证使用。</summary>
        public void SavePreviewPng(string path)
        {
            UpdateLayout();
            int width = Math.Max(1, (int)Math.Ceiling(ActualWidth));
            int height = Math.Max(1, (int)Math.Ceiling(ActualHeight));
            System.Windows.Media.Imaging.RenderTargetBitmap bitmap =
                new System.Windows.Media.Imaging.RenderTargetBitmap(
                    width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(this);
            System.Windows.Media.Imaging.PngBitmapEncoder encoder =
                new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using (System.IO.FileStream stream = new System.IO.FileStream(
                path, System.IO.FileMode.Create, System.IO.FileAccess.Write))
            {
                encoder.Save(stream);
            }
        }

        private async void ComponentOperationDialog_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= ComponentOperationDialog_Loaded;
            await StartAsync();
        }

        private async Task StartAsync()
        {
            if (_busy) return;
            _busy = true;
            _success = false;
            _cts = new CancellationTokenSource();
            CancellationToken token = _cts.Token;

            CancelButton.IsEnabled = true;
            CancelButton.Content = "取消";
            CloseButton.Visibility = Visibility.Collapsed;
            OperationProgressBar.IsIndeterminate = true;
            OperationProgressBar.Value = 0;
            Log("开始" + (_kind == ComponentOperationKind.Repair ? "修复" : "卸载") + "：" + _displayName);

            IProgress<ComponentOperationProgress> progress =
                new Progress<ComponentOperationProgress>(UpdateProgress);

            ComponentOperationResult result;
            try
            {
                ComponentOperationService service = new ComponentOperationService(_sunshineInstallPath);
                if (_kind == ComponentOperationKind.Repair)
                {
                    result = await service.RepairAsync(_key, _deviceId, progress, token);
                }
                else
                {
                    result = await service.UninstallAsync(_key, _deviceId, progress, token);
                }
            }
            catch (OperationCanceledException)
            {
                result = new ComponentOperationResult();
                result.Success = false;
                result.Message = "操作已取消。";
            }
            catch (Exception ex)
            {
                result = new ComponentOperationResult();
                result.Success = false;
                result.Message = "操作失败：" + ex.Message;
            }

            _success = result != null && result.Success;
            _busy = false;

            OperationProgressBar.IsIndeterminate = false;
            OperationProgressBar.Value = _success ? 100 : 0;
            OperationPercentText.Text = _success ? "100%" : string.Empty;
            OperationSpeedText.Text = string.Empty;
            OperationEtaText.Text = string.Empty;
            OperationStageText.Text = result == null ? "操作失败。" : result.Message;
            Log(result == null ? "操作失败。" : result.Message);

            CancelButton.Visibility = Visibility.Collapsed;
            CloseButton.Visibility = Visibility.Visible;
            CloseButton.Content = _success ? "完成" : "关闭";
            CloseButton.Focus();

            if (_cts != null)
            {
                _cts.Dispose();
                _cts = null;
            }
        }

        private void UpdateProgress(ComponentOperationProgress progress)
        {
            if (progress == null) return;
            OperationStageText.Text = string.IsNullOrEmpty(progress.Stage) ? "处理中…" : progress.Stage;
            OperationProgressBar.IsIndeterminate = progress.IsIndeterminate;
            if (!progress.IsIndeterminate)
            {
                OperationProgressBar.Value = Math.Max(0, Math.Min(100, progress.Percent));
                OperationPercentText.Text = progress.Percent + "%";
            }
            else
            {
                OperationPercentText.Text = "…";
            }
            OperationSpeedText.Text = progress.SpeedText ?? string.Empty;
            OperationEtaText.Text = string.IsNullOrEmpty(progress.EtaText)
                ? string.Empty : "剩余 " + progress.EtaText;

            if (!string.IsNullOrEmpty(progress.Stage)
                && !string.Equals(progress.Stage, _lastLoggedStage, StringComparison.Ordinal))
            {
                _lastLoggedStage = progress.Stage;
                if (!progress.Stage.StartsWith("正在下载组件包", StringComparison.Ordinal))
                {
                    Log(progress.Stage);
                }
            }
        }

        private void Log(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            string line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message
                + Environment.NewLine;
            OperationLogBox.AppendText(line);
            OperationLogBox.ScrollToEnd();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_busy)
            {
                DialogResult = _success;
                return;
            }

            if (_cts != null)
            {
                CancelButton.IsEnabled = false;
                CancelButton.Content = "正在取消…";
                try { _cts.Cancel(); }
                catch { }
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = _success;
        }

        private void ComponentOperationDialog_Closing(object sender, CancelEventArgs e)
        {
            if (_busy)
            {
                if (_cts != null)
                {
                    try { _cts.Cancel(); }
                    catch { }
                }
                e.Cancel = true;
            }
        }
    }
}
