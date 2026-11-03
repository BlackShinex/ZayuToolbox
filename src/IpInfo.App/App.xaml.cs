using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using IpInfo.Core;

namespace IpInfo.App
{
    public partial class App : Application
    {
        /// <summary>自动化 UI 测试模式：所有窗口都放到屏幕外且不激活，避免打扰桌面。</summary>
        public static bool IsUiTestMode { get; private set; }

        private const int GwlExStyle = -20;
        private const int WsExNoActivate = 0x08000000;

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        /// <summary>把测试窗口移到屏幕外，并禁止激活 / 任务栏显示。</summary>
        public static void ApplyUiTestWindowMode(Window window)
        {
            if (!IsUiTestMode || window == null) return;

            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -12000;
            window.Top = -12000;
            window.ShowInTaskbar = false;
            window.ShowActivated = false;
            window.SourceInitialized += delegate
            {
                try
                {
                    IntPtr handle = new WindowInteropHelper(window).Handle;
                    if (handle == IntPtr.Zero) return;
                    int style = GetWindowLong(handle, GwlExStyle);
                    SetWindowLong(handle, GwlExStyle, style | WsExNoActivate);
                }
                catch
                {
                }
            };
        }

        // QRCoder.dll 以嵌入资源形式打包，发布时仍然只有一个 exe；
        // 在类型初始化阶段注册解析器，确保任何二维码代码运行前已经生效。
        static App()
        {
            AppDomain.CurrentDomain.AssemblyResolve += ResolveQRCoder;
        }

        private static Assembly ResolveQRCoder(object sender, ResolveEventArgs args)
        {
            if (args == null || string.IsNullOrEmpty(args.Name)) return null;
            if (!args.Name.StartsWith("QRCoder", StringComparison.Ordinal)) return null;

            try
            {
                using (Stream stream = typeof(App).Assembly.GetManifestResourceStream("QRCoder.dll"))
                {
                    if (stream == null) return null;
                    byte[] buffer = new byte[stream.Length];
                    int read = 0;
                    while (read < buffer.Length)
                    {
                        int chunk = stream.Read(buffer, read, buffer.Length - read);
                        if (chunk <= 0) break;
                        read += chunk;
                    }
                    if (read != buffer.Length) return null;
                    return Assembly.Load(buffer);
                }
            }
            catch
            {
                return null;
            }
        }

        private static void OnPreviewGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            FrameworkElement element = sender as FrameworkElement;
            if (element != null)
            {
                element.FocusVisualStyle = null;
            }
        }

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

            // 保险：即使某个控件没有显式设置 FocusVisualStyle，也不显示键盘焦点虚线框。
            EventManager.RegisterClassHandler(typeof(FrameworkElement),
                Keyboard.PreviewGotKeyboardFocusEvent,
                new KeyboardFocusChangedEventHandler(OnPreviewGotKeyboardFocus));

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

            IsUiTestMode = !string.IsNullOrEmpty(renderPath) || offscreen;
            MainWindow window = new MainWindow();
            MainWindow = window;
            if (!string.IsNullOrEmpty(pageKey)) window.SelectPage(pageKey);

            if (IsUiTestMode)
            {
                // 离屏模式：用于自动截图 / UI 自动化验证，不打扰桌面。
                ApplyUiTestWindowMode(window);
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
