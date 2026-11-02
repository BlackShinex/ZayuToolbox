using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using IpInfo.Core;
using Microsoft.Win32;

namespace IpInfo.App
{
    public partial class WebView2InstallDialog : Window
    {
        private readonly WebView2DetectionResult _result;
        private readonly WebView2InstallerService _installer = new WebView2InstallerService();
        private bool _busy;
        private string _localInstallerPath;

        public WebView2InstallDialog(WebView2DetectionResult result)
        {
            InitializeComponent();
            _result = result;

            string status = result == null ? "未知" : result.StatusText;
            CurrentStatusText.Text = "当前状态：" + status;
            if (result != null && result.Primary != null)
            {
                CurrentDetailText.Text = "版本 " + NullSafe(result.Primary.Version)
                    + " · " + NullSafe(result.Primary.Scope)
                    + " · " + NullSafe(result.OsArch);
            }
            else
            {
                CurrentDetailText.Text = "未检测到可用的 Evergreen 运行库。";
            }

            bool admin = WebView2InstallerService.IsAdministrator();
            AllUsersCheck.IsChecked = admin;
            AllUsersCheck.IsEnabled = !admin;
            AllUsersHintText.Text = admin
                ? "当前以管理员身份运行，将安装到所有用户。"
                : "取消勾选则只安装到当前用户，不需要管理员权限；勾选会弹出 UAC。";

            StartButton.Content = result != null && result.Status == WebView2Status.Installed
                ? "修复 / 重新安装"
                : "开始安装";

            Closing += WebView2InstallDialog_Closing;
        }

        private void WebView2InstallDialog_Closing(object sender, CancelEventArgs e)
        {
            if (_busy)
            {
                e.Cancel = true;
                StatusText.Text = "正在安装，请等待安装结束或超时。";
            }
        }

        private async void StartButton_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;

            _busy = true;
            SetBusy(true);
            AppendLog("开始安装 WebView2 运行库。");

            try
            {
                string installerPath = _localInstallerPath;
                if (string.IsNullOrEmpty(installerPath))
                {
                    AppendLog("正在从 Microsoft 官方服务器下载 Evergreen Bootstrapper…");
                    installerPath = WebView2InstallerService.GetDefaultDownloadPath();
                    Progress<WebView2DownloadProgress> progress = new Progress<WebView2DownloadProgress>(
                        delegate(WebView2DownloadProgress p)
                        {
                            if (p == null) return;
                            if (p.Percent >= 0)
                            {
                                InstallProgressBar.IsIndeterminate = false;
                                InstallProgressBar.Value = p.Percent;
                                StatusText.Text = p.Stage + "（" + p.Percent + "%）";
                            }
                            else
                            {
                                InstallProgressBar.IsIndeterminate = true;
                                StatusText.Text = p.Stage;
                            }
                        });
                    await _installer.DownloadBootstrapperAsync(installerPath, progress, CancellationToken.None);
                    InstallProgressBar.IsIndeterminate = false;
                    InstallProgressBar.Value = 100;
                    AppendLog("下载完成：" + installerPath);
                }
                else
                {
                    AppendLog("使用本地安装包：" + installerPath);
                }

                StatusText.Text = "正在校验数字签名…";
                WebView2SignatureVerification signature = WebView2InstallerService.VerifySignature(installerPath);
                if (!signature.Valid)
                {
                    throw new InvalidOperationException("数字签名校验失败：" + signature.Error);
                }
                AppendLog("数字签名校验通过：" + signature.Signer);

                string reason;
                if (!WebView2InstallerService.LooksLikeWebView2Installer(installerPath, out reason))
                {
                    throw new InvalidOperationException("该文件不是 WebView2 安装程序：" + reason);
                }

                bool systemWide = AllUsersCheck.IsChecked == true;
                StatusText.Text = systemWide ? "正在以系统级方式安装…" : "正在以当前用户方式安装…";
                int exitCode = await _installer.RunInstallerAsync(installerPath, systemWide,
                    delegate(string message)
                    {
                        Dispatcher.BeginInvoke(new Action(delegate
                        {
                            StatusText.Text = message;
                            AppendLog(message);
                        }));
                    });

                if (exitCode != 0)
                {
                    throw new InvalidOperationException("安装程序退出码：" + exitCode
                        + "。请查看日志或尝试以管理员身份运行。");
                }

                AppendLog("安装程序已完成（退出码 0）。");
                StatusText.Text = "安装完成。关闭此窗口后主界面会自动重新检测。";
                FooterHintText.Text = "安装完成后，主界面会自动刷新 WebView2 状态。";
                CloseButton.Content = "完成";
                CloseButton.IsEnabled = true;
                StartButton.IsEnabled = false;
                LocalInstallerButton.IsEnabled = false;
                AllUsersCheck.IsEnabled = false;
                _busy = false;
            }
            catch (Exception ex)
            {
                AppendLog("安装失败：" + ex.Message);
                StatusText.Text = "安装失败：" + ex.Message;
                _busy = false;
                SetBusy(false);
            }
        }

        private void LocalInstallerButton_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dialog = new OpenFileDialog();
            dialog.Title = "选择 WebView2 安装程序";
            dialog.Filter = "WebView2 安装程序 (*.exe)|*.exe|所有文件 (*.*)|*.*";
            dialog.CheckFileExists = true;
            if (dialog.ShowDialog(this) != true) return;

            WebView2SignatureVerification signature = WebView2InstallerService.VerifySignature(dialog.FileName);
            if (!signature.Valid)
            {
                MessageBox.Show(this, "该文件未通过 Microsoft 数字签名校验，不能用于安装：\n"
                    + signature.Error, "WebView2 安装", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string reason;
            if (!WebView2InstallerService.LooksLikeWebView2Installer(dialog.FileName, out reason))
            {
                MessageBox.Show(this, "该文件不是 WebView2 安装程序：\n" + reason, "WebView2 安装",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _localInstallerPath = dialog.FileName;
            LocalInstallerButton.Content = "已选择本地安装包";
            StartButton.Content = "开始安装本地包";
            StatusText.Text = "已选择本地安装包，点击「开始安装本地包」继续。";
            ProgressPanel.Visibility = Visibility.Visible;
            InstallProgressBar.IsIndeterminate = false;
            InstallProgressBar.Value = 0;
            AppendLog("已选择本地安装包：" + dialog.FileName);
            AppendLog("数字签名校验通过：" + signature.Signer);
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_busy) Close();
        }

        private void SetBusy(bool busy)
        {
            ProgressPanel.Visibility = Visibility.Visible;
            StartButton.IsEnabled = !busy;
            LocalInstallerButton.IsEnabled = !busy;
            AllUsersCheck.IsEnabled = !busy && !WebView2InstallerService.IsAdministrator();
            CloseButton.IsEnabled = !busy;
            if (busy)
            {
                InstallProgressBar.IsIndeterminate = true;
                InstallProgressBar.Value = 0;
                StatusText.Text = "准备安装…";
                CloseButton.Content = "安装中…";
            }
            else
            {
                CloseButton.Content = "取消";
            }
        }

        private void AppendLog(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            LogBox.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message + Environment.NewLine);
            LogBox.ScrollToEnd();
        }

        private static string NullSafe(string value)
        {
            return string.IsNullOrEmpty(value) ? "—" : value;
        }
    }
}
