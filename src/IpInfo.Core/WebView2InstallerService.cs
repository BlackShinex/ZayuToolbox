using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace IpInfo.Core
{
    public class WebView2DownloadProgress
    {
        public long BytesReceived { get; set; }
        public long TotalBytes { get; set; }
        public int Percent { get; set; }
        public string Stage { get; set; }

        public WebView2DownloadProgress()
        {
            Stage = string.Empty;
        }
    }

    public class WebView2SignatureVerification
    {
        public bool Valid { get; set; }
        public string Signer { get; set; }
        public string Error { get; set; }

        public WebView2SignatureVerification()
        {
            Signer = string.Empty;
            Error = string.Empty;
        }
    }

    public class WebView2InstallerService
    {
        public const string PrimaryBootstrapperUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";
        public const string AlternateBootstrapperUrl = "https://go.microsoft.com/fwlink/?linkid=2124703";
        public const string OfficialDownloadPage = "https://developer.microsoft.com/microsoft-edge/webview2/";

        private static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        static WebView2InstallerService()
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            }
            catch
            {
            }
        }

        public static string GetDownloadDirectory()
        {
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IpInfo", "WebView2", "Downloads");
            Directory.CreateDirectory(root);
            return root;
        }

        public static string GetDefaultDownloadPath()
        {
            return Path.Combine(GetDownloadDirectory(), "MicrosoftEdgeWebview2Setup.exe");
        }

        public async Task<string> DownloadBootstrapperAsync(string destinationPath, IProgress<WebView2DownloadProgress> progress, CancellationToken cancellationToken)
        {
            string[] urls = new string[] { PrimaryBootstrapperUrl, AlternateBootstrapperUrl };

            Exception lastError = null;
            for (int attempt = 0; attempt < urls.Length; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    string result = await DownloadSingleAsync(urls[attempt], destinationPath, progress, cancellationToken).ConfigureAwait(false);
                    return result;
                }
                catch (OperationCanceledException)
                {
                    TryDelete(destinationPath + ".part");
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    TryDelete(destinationPath + ".part");
                    if (progress != null)
                    {
                        WebView2DownloadProgress retry = new WebView2DownloadProgress();
                        retry.Stage = "第 " + (attempt + 1) + " 个下载地址失败，正在重试…";
                        retry.Percent = 0;
                        progress.Report(retry);
                    }
                }

                if (attempt + 1 < urls.Length)
                    await Task.Delay(700, cancellationToken).ConfigureAwait(false);
            }

            string reason = lastError == null ? string.Empty : "（" + lastError.Message + "）";
            throw new InvalidOperationException("无法从 Microsoft 官方服务器下载安装包" + reason + "。请检查网络连接，或使用“选择本地安装包”功能进行离线安装。", lastError);
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }

        private async Task<string> DownloadSingleAsync(string url, string destinationPath, IProgress<WebView2DownloadProgress> progress, CancellationToken cancellationToken)
        {
            string directory = Path.GetDirectoryName(destinationPath);
            if (string.IsNullOrEmpty(directory)) directory = GetDownloadDirectory();
            Directory.CreateDirectory(directory);

            string tempPath = destinationPath + ".part";
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); }
                catch { }
            }

            using (HttpClientHandler handler = new HttpClientHandler())
            {
                handler.AllowAutoRedirect = true;
                handler.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

                using (HttpClient client = new HttpClient(handler))
                {
                    client.Timeout = TimeSpan.FromMinutes(20);
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("ZayuToolbox/1.7 (Windows NT 10.0; Win64; x64)");

                    using (HttpResponseMessage response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
                    {
                        response.EnsureSuccessStatusCode();

                        long total = -1;
                        if (response.Content.Headers.ContentLength.HasValue)
                            total = response.Content.Headers.ContentLength.Value;

                        long received = 0;
                        int lastPercent = -1;
                        DateTime lastReport = DateTime.MinValue;

                        using (Stream input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (FileStream output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                        {
                            byte[] buffer = new byte[81920];
                            while (true)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                int read = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                                if (read <= 0) break;

                                await output.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
                                received += read;

                                int percent = total > 0 ? (int)Math.Min(100, (received * 100L) / total) : -1;
                                DateTime now = DateTime.UtcNow;
                                if (progress != null && (percent != lastPercent || (now - lastReport).TotalMilliseconds >= 180))
                                {
                                    WebView2DownloadProgress p = new WebView2DownloadProgress();
                                    p.BytesReceived = received;
                                    p.TotalBytes = total;
                                    p.Percent = percent;
                                    p.Stage = "正在下载官方安装包…";
                                    progress.Report(p);
                                    lastPercent = percent;
                                    lastReport = now;
                                }
                            }
                        }

                        if (received < 300 * 1024)
                            throw new InvalidDataException("下载文件大小异常（" + received.ToString("N0") + " 字节），已中止。");
                    }
                }
            }

            if (File.Exists(destinationPath))
            {
                try { File.Delete(destinationPath); }
                catch { }
            }
            File.Move(tempPath, destinationPath);

            if (progress != null)
            {
                WebView2DownloadProgress done = new WebView2DownloadProgress();
                done.BytesReceived = new FileInfo(destinationPath).Length;
                done.TotalBytes = done.BytesReceived;
                done.Percent = 100;
                done.Stage = "下载完成";
                progress.Report(done);
            }
            return destinationPath;
        }

        public static bool IsAdministrator()
        {
            try
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    WindowsPrincipal principal = new WindowsPrincipal(identity);
                    return principal.IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch
            {
                return false;
            }
        }

        public static WebView2SignatureVerification VerifySignature(string filePath)
        {
            WebView2SignatureVerification result = new WebView2SignatureVerification();
            result.Valid = false;

            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                result.Error = "文件不存在。";
                return result;
            }

            try
            {
                result.Signer = GetSignerSubject(filePath);
            }
            catch (Exception ex)
            {
                result.Error = "无法读取数字签名：" + ex.Message;
                return result;
            }

            if (string.IsNullOrEmpty(result.Signer))
            {
                result.Error = "文件没有 Authenticode 数字签名。";
                return result;
            }

            if (result.Signer.IndexOf("Microsoft Corporation", StringComparison.OrdinalIgnoreCase) < 0)
            {
                result.Error = "数字签名主体不是 Microsoft Corporation：" + result.Signer;
                return result;
            }

            uint trustResult = VerifyTrust(filePath);
            if (trustResult != 0)
            {
                result.Error = "Authenticode 信任校验失败（0x" + trustResult.ToString("X8") + "）。";
                return result;
            }

            result.Valid = true;
            return result;
        }

        /// <summary>
        /// USB drives and download folders may contain many Microsoft-signed executables.
        /// Local installers must additionally identify themselves as WebView2 installers.
        /// </summary>
        public static bool LooksLikeWebView2Installer(string filePath, out string reason)
        {
            reason = string.Empty;
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                reason = "文件不存在。";
                return false;
            }

            string fileName = Path.GetFileName(filePath);
            if (fileName.IndexOf("webview2", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            try
            {
                FileVersionInfo info = FileVersionInfo.GetVersionInfo(filePath);
                string metadata = (info.FileDescription + " " + info.ProductName + " " + info.OriginalFilename + " " + info.InternalName).ToLowerInvariant();
                if (metadata.IndexOf("webview2", StringComparison.Ordinal) >= 0 ||
                    metadata.IndexOf("edge webview", StringComparison.Ordinal) >= 0 ||
                    metadata.IndexOf("edgewebview", StringComparison.Ordinal) >= 0)
                    return true;
            }
            catch
            {
            }

            reason = "文件名和产品信息均未标识为 WebView2 安装程序。";
            return false;
        }

        private static string GetSignerSubject(string filePath)
        {
            X509Certificate certificate = X509Certificate.CreateFromSignedFile(filePath);
            if (certificate == null) return string.Empty;
            using (X509Certificate2 certificate2 = new X509Certificate2(certificate))
            {
                return certificate2.Subject;
            }
        }

        private static uint VerifyTrust(string filePath)
        {
            WINTRUST_FILE_INFO fileInfo = new WINTRUST_FILE_INFO();
            fileInfo.cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_FILE_INFO));
            fileInfo.pcwszFilePath = filePath;
            fileInfo.hFile = IntPtr.Zero;
            fileInfo.pgKnownSubject = IntPtr.Zero;

            IntPtr fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WINTRUST_FILE_INFO)));
            IntPtr trustDataPtr = IntPtr.Zero;
            try
            {
                Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);

                WINTRUST_DATA trustData = new WINTRUST_DATA();
                trustData.cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_DATA));
                trustData.pPolicyCallbackData = IntPtr.Zero;
                trustData.pSIPClientData = IntPtr.Zero;
                trustData.dwUIChoice = 2; // WTD_UI_NONE
                trustData.fdwRevocationChecks = 0; // WTD_REVOKE_NONE
                trustData.dwUnionChoice = 1; // WTD_CHOICE_FILE
                trustData.pFile = fileInfoPtr;
                trustData.dwStateAction = 0; // WTD_STATEACTION_IGNORE
                trustData.hWVTStateData = IntPtr.Zero;
                trustData.pwszURLReference = IntPtr.Zero;
                trustData.dwProvFlags = 0x10; // WTD_REVOCATION_CHECK_NONE
                trustData.dwUIContext = 0;

                trustDataPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WINTRUST_DATA)));
                Marshal.StructureToPtr(trustData, trustDataPtr, false);

                uint result = WinVerifyTrust(IntPtr.Zero, WINTRUST_ACTION_GENERIC_VERIFY_V2, trustDataPtr);

                // Release WinVerifyTrust cached state.
                WINTRUST_DATA closeData = (WINTRUST_DATA)Marshal.PtrToStructure(trustDataPtr, typeof(WINTRUST_DATA));
                closeData.dwStateAction = 2; // WTD_STATEACTION_CLOSE
                Marshal.StructureToPtr(closeData, trustDataPtr, false);
                WinVerifyTrust(IntPtr.Zero, WINTRUST_ACTION_GENERIC_VERIFY_V2, trustDataPtr);

                return result;
            }
            finally
            {
                if (trustDataPtr != IntPtr.Zero) Marshal.FreeHGlobal(trustDataPtr);
                Marshal.FreeHGlobal(fileInfoPtr);
            }
        }

        [DllImport("wintrust.dll", PreserveSig = true, CharSet = CharSet.Unicode)]
        private static extern uint WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID, IntPtr pWVTData);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WINTRUST_FILE_INFO
        {
            public uint cbStruct;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINTRUST_DATA
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
        }

        public async Task<int> RunInstallerAsync(string installerPath, bool systemWide, Action<string> status,
            CancellationToken cancellationToken)
        {
            if (!File.Exists(installerPath))
                throw new FileNotFoundException("安装程序不存在。", installerPath);

            cancellationToken.ThrowIfCancellationRequested();

            using (Process process = new Process())
            {
                // Keep the verified file open with read-only sharing until the process has
                // started. This prevents a local process from replacing the installer between
                // signature verification and the elevated launch (TOCTOU).
                using (FileStream guard = new FileStream(installerPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    // Re-check immediately before execution, including the elevated launch path.
                    WebView2SignatureVerification verification = VerifySignature(installerPath);
                    if (!verification.Valid)
                        throw new InvalidOperationException("数字签名复校验失败：" + verification.Error);

                    string identityReason;
                    if (!LooksLikeWebView2Installer(installerPath, out identityReason))
                        throw new InvalidOperationException("该文件不是 WebView2 安装程序：" + identityReason);

                    bool elevated = systemWide && !IsAdministrator();
                    ProcessStartInfo startInfo = new ProcessStartInfo();
                    startInfo.FileName = installerPath;
                    startInfo.Arguments = "/silent /install";
                    // For elevated launches, avoid using the user-writable download directory
                    // as the working directory so the DLL search path does not include it.
                    startInfo.WorkingDirectory = elevated
                        ? Environment.GetFolderPath(Environment.SpecialFolder.System)
                        : Path.GetDirectoryName(installerPath);
                    startInfo.UseShellExecute = elevated;
                    startInfo.CreateNoWindow = !elevated;

                    if (elevated)
                    {
                        startInfo.Verb = "runas";
                        if (status != null) status("正在请求管理员权限（UAC）…");
                    }
                    else
                    {
                        if (status != null) status(systemWide ? "正在以系统级静默安装…" : "正在以当前用户静默安装…");
                    }

                    if (status != null && !elevated) status("安装程序已启动，请稍候…");

                    cancellationToken.ThrowIfCancellationRequested();
                    process.StartInfo = startInfo;
                    process.Start();
                }

                int exitCode = await Task.Run(delegate
                {
                    DateTime deadline = DateTime.UtcNow.AddMinutes(15);
                    while (!process.WaitForExit(250))
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            try { process.Kill(); }
                            catch { }
                            return -3;
                        }
                        if (DateTime.UtcNow >= deadline)
                        {
                            try { process.Kill(); }
                            catch { }
                            return -2;
                        }
                    }
                    return process.ExitCode;
                }).ConfigureAwait(false);

                if (exitCode == -3)
                    return -3;

                if (exitCode == -2)
                    throw new TimeoutException("安装程序在 15 分钟内未结束，已请求终止。请检查安全软件是否拦截了静默安装。");

                return exitCode;
            }
        }

        public static void OpenUrl(string url)
        {
            ProcessStartInfo info = new ProcessStartInfo(url);
            info.UseShellExecute = true;
            Process.Start(info);
        }

        public static void OpenFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                throw new DirectoryNotFoundException("目录不存在。");
            ProcessStartInfo info = new ProcessStartInfo("explorer.exe", "\"" + folder + "\"");
            info.UseShellExecute = true;
            Process.Start(info);
        }
    }
}
