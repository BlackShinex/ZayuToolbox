using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Threading;

namespace IpInfo.App
{
    /// <summary>
    /// Windows 剪贴板有时会被其他进程短暂占用。这里用短间隔重试，
    /// 失败时只更新按钮文字，不弹模态框打断用户。
    /// </summary>
    internal static class ClipboardHelper
    {
        public static bool TrySetText(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;

            for (int attempt = 0; attempt < 8; attempt++)
            {
                try
                {
                    System.Windows.Clipboard.SetDataObject(text, true);
                    return true;
                }
                catch (ExternalException)
                {
                }
                catch (Exception)
                {
                }

                Thread.Sleep(35 + attempt * 20);
            }
            return false;
        }

        public static void ShowCopyFailure(Button button)
        {
            if (button == null) return;

            object original = button.Content;
            button.Content = "复制失败";
            DispatcherTimer timer = new DispatcherTimer();
            timer.Interval = TimeSpan.FromMilliseconds(1300);
            timer.Tick += delegate
            {
                timer.Stop();
                if (object.Equals(button.Content, "复制失败")) button.Content = original;
            };
            timer.Start();
        }
    }
}
