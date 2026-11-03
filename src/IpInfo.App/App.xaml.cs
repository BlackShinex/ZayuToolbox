using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using IpInfo.Core;

namespace IpInfo.App
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 高分屏适配：启用 Per-Monitor DPI 变化时的自动缩放（app.config 中也有同样配置）。
            try
            {
                AppContext.SetSwitch("Switch.System.Windows.DoNotScaleForDpiChanges", false);
            }
            catch
            {
            }

            DispatcherUnhandledException += App_DispatcherUnhandledException;

            string renderPath = null;
            bool reveal = false;
            bool offscreen = false;
            bool full = false;
            string serviceStartMode = null;
            string pageKey = null;
            for (int i = 0; i < e.Args.Length; i++)
            {
                if (string.Equals(e.Args[i], "--render", StringComparison.OrdinalIgnoreCase) && i + 1 < e.Args.Length)
                {
                    renderPath = e.Args[i + 1];
                    i++;
                }
                else if (string.Equals(e.Args[i], "--reveal", StringComparison.OrdinalIgnoreCase))
                {
                    reveal = true;
                }
                else if (string.Equals(e.Args[i], "--offscreen", StringComparison.OrdinalIgnoreCase))
                {
                    offscreen = true;
                }
                else if (string.Equals(e.Args[i], "--full", StringComparison.OrdinalIgnoreCase))
                {
                    full = true;
                }
                else if (string.Equals(e.Args[i], "--page", StringComparison.OrdinalIgnoreCase)
                    && i + 1 < e.Args.Length)
                {
                    pageKey = e.Args[i + 1];
                    i++;
                }
                else if (string.Equals(e.Args[i], "--service-start", StringComparison.OrdinalIgnoreCase)
                    && i + 1 < e.Args.Length)
                {
                    serviceStartMode = e.Args[i + 1];
                    i++;
                }
            }

            // 提权子进程专用：只改服务启动类型，不显示主窗口。
            if (!string.IsNullOrEmpty(serviceStartMode))
            {
                Shutdown(ApplyServiceStartMode(serviceStartMode));
                return;
            }

            MainWindow window = new MainWindow();
            MainWindow = window;
            if (!string.IsNullOrEmpty(pageKey)) window.SelectPage(pageKey);

            if (!string.IsNullOrEmpty(renderPath) || offscreen)
            {
                // 离屏模式：用于自动截图 / UI 自动化验证，不打扰桌面。
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -12000;
                window.Top = -12000;
                window.ShowInTaskbar = false;
                window.ShowActivated = false;
            }

            window.Show();

            if (!string.IsNullOrEmpty(renderPath))
            {
                RunRenderMode(window, renderPath, reveal, full);
            }
        }

        private static int ApplyServiceStartMode(string text)
        {
            SunshineStartMode mode;
            if (!SunshineServiceManager.TryParseStartMode(text, out mode)) return 1;

            SunshineServiceChangeResult result = SunshineServiceManager.SetStartMode(mode);
            if (result.Success) return 0;
            return result.NeedElevation ? 2 : 1;
        }

        private async void RunRenderMode(MainWindow window, string path, bool reveal, bool full)
        {
            try
            {
                await window.RenderSnapshotAsync(path, reveal, full);
            }
            catch (Exception ex)
            {
                try
                {
                    File.WriteAllText(path + ".error.txt", ex.ToString());
                }
                catch
                {
                }
            }
            finally
            {
                Shutdown();
            }
        }

        private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            MessageBox.Show("程序发生错误：" + e.Exception.Message, "杂鱼串流工具箱",
                MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        }
    }
}
