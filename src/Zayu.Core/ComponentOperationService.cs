using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Zayu.Core
{
    /// <summary>
    /// 基地版组件的修复 / 卸载操作。只允许白名单组件，下载源来自基地版安装目录中的官方清单。
    /// </summary>
    public sealed class ComponentOperationService
    {
        private static readonly TimeSpan ProcessTimeout = TimeSpan.FromMinutes(10);
        private const string UsbIpInstallerUrl =
            "https://github.com/vadimgrn/usbip-win2/releases/download/v.0.9.8.1/USBip-0.9.8.1-x64.exe";
        private const string UsbIpInstallerSha256 =
            "38cad6d4432b52d5bb9409d9ad03b72fdffc4ada4cd3a48fbeca1a2752a8518a";
        private const long UsbIpInstallerSize = 26083632L;
        private readonly string _sunshineInstallPath;

        public ComponentOperationService(string sunshineInstallPath)
        {
            _sunshineInstallPath = sunshineInstallPath == null ? string.Empty : sunshineInstallPath.TrimEnd('\\');
        }

        public async Task<ComponentOperationResult> RepairAsync(string key, string deviceId,
            IProgress<ComponentOperationProgress> progress, CancellationToken cancellationToken)
        {
            return await RepairAsync(key, deviceId, null, progress, cancellationToken).ConfigureAwait(false);
        }

        public async Task<ComponentOperationResult> RepairAsync(string key, string deviceId,
            string localInstallerPath, IProgress<ComponentOperationProgress> progress,
            CancellationToken cancellationToken)
        {
            try
            {
                if (!ComponentOperationCatalog.CanRepair(key))
                    return Fail("该组件暂不支持一键修复。");
                if (string.IsNullOrEmpty(_sunshineInstallPath))
                    return Fail("未检测到 Sunshine 安装目录。");

                if (key == ComponentOperationCatalog.ZakoDisplay)
                    return await RepairInfAsync(key, "vdd", "ZakoVDD.inf", progress, cancellationToken);
                if (key == ComponentOperationCatalog.ZakoMouse)
                    return await RepairInfAsync(key, "vmouse", "ZakoVirtualMouse.inf", progress, cancellationToken);
                if (key == ComponentOperationCatalog.DualSense)
                    return await RepairDualSenseAsync(progress, cancellationToken);
                if (key == ComponentOperationCatalog.ViGEm)
                    return await RepairViGEmAsync(progress, cancellationToken);
                if (key == ComponentOperationCatalog.SteamSpeakers)
                    return await RepairSteamSpeakersAsync(progress, cancellationToken);
                if (key == ComponentOperationCatalog.UsbIp)
                    return await RepairUsbIpAsync(progress, cancellationToken);
                if (key == ComponentOperationCatalog.SunshineMicrophone)
                    return await RepairSunshineMicrophoneAsync(progress, cancellationToken);
                if (key == ComponentOperationCatalog.VbCable)
                    return await RepairVbCableAsync(localInstallerPath, progress, cancellationToken);

                return Fail("未实现该组件的修复。");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Fail("修复失败：" + ex.Message);
            }
        }

        public async Task<ComponentOperationResult> UninstallAsync(string key, string deviceId,
            IProgress<ComponentOperationProgress> progress, CancellationToken cancellationToken)
        {
            try
            {
                if (!ComponentOperationCatalog.CanUninstall(key))
                    return Fail("该组件暂不支持一键卸载。");
                if (string.IsNullOrEmpty(_sunshineInstallPath))
                    return Fail("未检测到 Sunshine 安装目录。");

                if (key == ComponentOperationCatalog.ZakoDisplay)
                    return await UninstallInfAsync("ZakoVDD.inf", deviceId, progress, cancellationToken);
                if (key == ComponentOperationCatalog.ZakoMouse)
                    return await UninstallInfAsync("ZakoVirtualMouse.inf", deviceId, progress, cancellationToken);
                if (key == ComponentOperationCatalog.DualSense)
                    return await UninstallDualSenseAsync(progress, cancellationToken);
                if (key == ComponentOperationCatalog.ViGEm)
                    return await UninstallViGEmAsync(progress, cancellationToken);

                return Fail("未实现该组件的卸载。");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Fail("卸载失败：" + ex.Message);
            }
        }

        private async Task<ComponentOperationResult> RepairViGEmAsync(
            IProgress<ComponentOperationProgress> progress, CancellationToken cancellationToken)
        {
            string batch = Path.Combine(_sunshineInstallPath, "scripts", "install-gamepad.bat");
            if (!File.Exists(batch))
                return Fail("找不到基地版 scripts\\install-gamepad.bat，请确认 Sunshine 安装完整。");

            Report(progress, "正在安装 / 修复 ViGEmBus（UAC）…", true, 0, false);
            string arguments = "/c \"\"" + batch + "\" force\"";
            int code = await RunElevatedAsync(
                Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                arguments, progress, cancellationToken).ConfigureAwait(false);
            if (code == -1223) return Fail("已取消 UAC 授权。");
            if (code != 0 && code != 3010)
                return Fail("ViGEmBus 安装脚本退出码 " + code + "。");

            await Task.Delay(1500, cancellationToken).ConfigureAwait(false);
            Report(progress, "ViGEmBus 安装脚本已结束", false, 100, false);
            return code == 3010
                ? Success("ViGEmBus 已安装；系统提示需要重启时请重启后再刷新状态。")
                : Success("ViGEmBus 安装 / 修复已完成。");
        }

        private async Task<ComponentOperationResult> UninstallViGEmAsync(
            IProgress<ComponentOperationProgress> progress, CancellationToken cancellationToken)
        {
            string batch = Path.Combine(_sunshineInstallPath, "scripts", "uninstall-gamepad.bat");
            if (!File.Exists(batch))
                return Fail("找不到基地版 scripts\\uninstall-gamepad.bat，请确认 Sunshine 安装完整。");

            Report(progress, "正在卸载 ViGEmBus（UAC）…", true, 0, false);
            string arguments = "/c \"\"" + batch + "\"\"";
            int code = await RunElevatedAsync(
                Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                arguments, progress, cancellationToken).ConfigureAwait(false);
            if (code == -1223) return Fail("已取消 UAC 授权。");
            if (code != 0 && code != 3010)
                return Fail("ViGEmBus 卸载脚本退出码 " + code + "。");

            Report(progress, "ViGEmBus 卸载脚本已结束", false, 100, false);
            return Success("ViGEmBus 卸载脚本已结束；如提示重启请重启。");
        }

        private async Task<ComponentOperationResult> RepairSteamSpeakersAsync(
            IProgress<ComponentOperationProgress> progress, CancellationToken cancellationToken)
        {
            string common = Environment.GetEnvironmentVariable("CommonProgramFiles(x86)");
            if (string.IsNullOrEmpty(common))
            {
                common = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    "Common Files");
            }
            string infPath = Path.Combine(common, "Steam", "drivers", "Windows10", "x64",
                "SteamStreamingSpeakers.inf");
            if (!File.Exists(infPath))
            {
                return Fail("未找到 Steam 音频驱动；请先安装 Steam，或从 Steam 安装目录修复。");
            }

            Report(progress, "正在安装 Steam Streaming Speakers（UAC）…", true, 0, false);
            int code = await RunElevatedAsync(
                Path.Combine(Environment.SystemDirectory, "pnputil.exe"),
                "/add-driver \"" + infPath + "\" /install",
                progress, cancellationToken).ConfigureAwait(false);
            if (code == -1223) return Fail("已取消 UAC 授权。");
            if (code != 0) return Fail("Steam 音频驱动安装失败，pnputil 退出码 " + code + "。");

            Report(progress, "Steam Streaming Speakers 已安装", false, 100, false);
            return Success("Steam Streaming Speakers 已重新安装。");
        }

        private async Task<ComponentOperationResult> RepairUsbIpAsync(
            IProgress<ComponentOperationProgress> progress, CancellationToken cancellationToken)
        {
            if (HasPinnedUsbIpVersion())
            {
                Report(progress, "USB/IP 0.9.8.1 已安装", false, 100, false);
                return Success("USB/IP 0.9.8.1 已安装，跳过重复安装。");
            }

            string root = Path.Combine(Path.GetTempPath(), "ZayuToolbox", "Components",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string installer = Path.Combine(root, "USBip-0.9.8.1-x64.exe");
            try
            {
                await DownloadFileAsync(UsbIpInstallerUrl, installer, UsbIpInstallerSize,
                    progress, cancellationToken).ConfigureAwait(false);

                Report(progress, "正在校验 USB/IP 安装程序…", true, 0, false);
                if (new FileInfo(installer).Length != UsbIpInstallerSize
                    || !string.Equals(Sha256File(installer), UsbIpInstallerSha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return Fail("USB/IP 安装程序校验失败，已停止安装。");
                }

                Report(progress, "正在安装 USB/IP 0.9.8.1（UAC）…", true, 0, false);
                int code = await RunElevatedAsync(installer,
                    "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /RESTARTEXITCODE=3010 /SP-",
                    progress, cancellationToken).ConfigureAwait(false);
                if (code == -1223) return Fail("已取消 UAC 授权。");
                if (code != 0 && code != 3010)
                    return Fail("USB/IP 安装程序退出码 " + code + "。");

                await Task.Delay(1500, cancellationToken).ConfigureAwait(false);
                if (!HasPinnedUsbIpVersion())
                {
                    return Success("USB/IP 安装程序已结束，但尚未检测到 0.9.8.1；如已提示重启，请重启后重试。");
                }
                if (!IsUsbIpReady())
                {
                    return Success("USB/IP 0.9.8.1 已安装；驱动尚未全部运行，如提示重启请重启。");
                }

                Report(progress, "USB/IP 0.9.8.1 已就绪", false, 100, false);
                return Success("USB/IP 0.9.8.1 已安装。");
            }
            finally
            {
                TryDeleteDirectory(root);
            }
        }

        private async Task<ComponentOperationResult> RepairSunshineMicrophoneAsync(
            IProgress<ComponentOperationProgress> progress, CancellationToken cancellationToken)
        {
            string active = Path.Combine(_sunshineInstallPath, "tools", "sunshine-ds5-component",
                "active", "Sunshine.Ds5Sidecar.exe");
            if (!File.Exists(active))
            {
                ComponentOperationResult component = await RepairDualSenseAsync(progress, cancellationToken)
                    .ConfigureAwait(false);
                if (component == null || !component.Success) return component ?? Fail("DualSense 组件安装失败。");
            }

            if (!HasPinnedUsbIpVersion())
            {
                ComponentOperationResult usbip = await RepairUsbIpAsync(progress, cancellationToken)
                    .ConfigureAwait(false);
                if (usbip == null || !usbip.Success) return usbip ?? Fail("USB/IP 安装失败。");
            }

            Report(progress, "虚拟麦克风依赖已就绪", false, 100, false);
            return Success("DualSense 组件与 USB/IP 依赖已就绪；虚拟麦克风会在串流时创建。");
        }

        private async Task<ComponentOperationResult> RepairVbCableAsync(string localInstallerPath,
            IProgress<ComponentOperationProgress> progress, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(localInstallerPath) || !File.Exists(localInstallerPath))
            {
                return Fail("请先选择 VB-Audio 官方安装包（VBCABLE_Setup_x64.exe 或官方 ZIP）。");
            }

            string root = Path.Combine(Path.GetTempPath(), "ZayuToolbox", "Components",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string setup = localInstallerPath;
                if (string.Equals(Path.GetExtension(localInstallerPath), ".zip",
                    StringComparison.OrdinalIgnoreCase))
                {
                    Report(progress, "正在解压 VB-CABLE 安装包…", true, 0, false);
                    string staging = Path.Combine(root, "staging");
                    ExtractZipSafe(localInstallerPath, staging);
                    setup = FindFile(staging, "VBCABLE_Setup_x64.exe");
                    if (string.IsNullOrEmpty(setup)) setup = FindFile(staging, "VBCABLE_Setup.exe");
                    if (string.IsNullOrEmpty(setup)) return Fail("安装包中没有找到 VBCABLE_Setup 程序。");
                }

                if (!IsVbCableSigned(setup))
                {
                    return Fail("VB-CABLE 安装程序数字签名校验失败，已停止运行。");
                }

                Report(progress, "请在 VB-CABLE 安装程序中完成安装…", true, 0, false);
                int code = await RunInstallerAsync(setup, progress, cancellationToken)
                    .ConfigureAwait(false);
                if (code == -1223) return Fail("已取消 UAC 授权。");
                if (code != 0) return Fail("VB-CABLE 安装程序退出码 " + code + "。");

                Report(progress, "VB-CABLE 安装程序已结束", false, 100, false);
                return Success("VB-CABLE 安装程序已结束；如提示重启，请重启后再刷新状态。");
            }
            finally
            {
                TryDeleteDirectory(root);
            }
        }

        private static string GetFileVersion(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return string.Empty;
                FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
                return string.IsNullOrEmpty(info.FileVersion) ? string.Empty : info.FileVersion;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static bool AreUsbIpDriversRunning()
        {
            try
            {
                using (System.Management.ManagementObjectSearcher searcher =
                    new System.Management.ManagementObjectSearcher(
                        "SELECT Name,State FROM Win32_SystemDriver WHERE Name='usbip2_ude' OR Name='usbip2_filter'"))
                using (System.Management.ManagementObjectCollection items = searcher.Get())
                {
                    int running = 0;
                    foreach (System.Management.ManagementObject item in items)
                    {
                        using (item)
                        {
                            if (string.Equals(Convert.ToString(item["State"]), "Running",
                                StringComparison.OrdinalIgnoreCase))
                            {
                                running++;
                            }
                        }
                    }
                    return running >= 2;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool IsUsbIpReady()
        {
            return AreUsbIpDriversRunning() && HasPinnedUsbIpVersion();
        }

        private static bool HasPinnedUsbIpVersion()
        {
            string version = InstalledUsbIpVersion();
            if (string.IsNullOrEmpty(version)) return false;
            return !VersionUpdateService.IsNewer(ComponentOperationCatalog.RequiredUsbIpVersion, version)
                && !VersionUpdateService.IsNewer(version, ComponentOperationCatalog.RequiredUsbIpVersion);
        }

        private static string InstalledUsbIpVersion()
        {
            // Base Control Panel resolves USB/IP from its uninstall registration
            // (DisplayVersion, then "USBip <version>" DisplayName), not from the
            // driver file. Match that order so an already-installed 0.9.8.1 is
            // never downloaded again just because a driver node is temporarily stopped.
            List<string> versions = new List<string>();
            try
            {
                for (int i = 0; i < 2; i++)
                {
                    RegistryView view = i == 0 ? RegistryView.Registry64 : RegistryView.Registry32;
                    using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (RegistryKey uninstall = baseKey.OpenSubKey(
                        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
                    {
                        if (uninstall == null) continue;
                        string[] names = uninstall.GetSubKeyNames();
                        for (int n = 0; n < names.Length; n++)
                        {
                            using (RegistryKey key = uninstall.OpenSubKey(names[n]))
                            {
                                if (key == null) continue;
                                string display = Convert.ToString(key.GetValue("DisplayName"));
                                if (string.IsNullOrEmpty(display)
                                    || display.IndexOf("USBip", StringComparison.OrdinalIgnoreCase) < 0)
                                {
                                    continue;
                                }
                                string version = Convert.ToString(key.GetValue("DisplayVersion"));
                                if (string.IsNullOrEmpty(version)) version = ParseVersionText(display);
                                version = VersionUpdateService.NormalizeVersion(version);
                                if (!string.IsNullOrEmpty(version)) versions.Add(version);
                            }
                        }
                    }
                }
            }
            catch
            {
            }

            if (versions.Count > 0)
            {
                string first = versions[0];
                for (int i = 1; i < versions.Count; i++)
                {
                    if (!string.Equals(first, versions[i], StringComparison.OrdinalIgnoreCase))
                    {
                        return string.Empty;
                    }
                }
                return first;
            }

            try
            {
                using (System.Management.ManagementObjectSearcher searcher =
                    new System.Management.ManagementObjectSearcher(
                        "SELECT PathName FROM Win32_SystemDriver WHERE Name='usbip2_ude'"))
                using (System.Management.ManagementObjectCollection items = searcher.Get())
                {
                    foreach (System.Management.ManagementObject item in items)
                    {
                        using (item)
                        {
                            string path = Environment.ExpandEnvironmentVariables(
                                Convert.ToString(item["PathName"]).Trim('"'));
                            string version = GetFileVersion(path);
                            if (!string.IsNullOrEmpty(version)) return version;
                        }
                    }
                }
            }
            catch
            {
            }
            return string.Empty;
        }

        private static string ParseVersionText(string text)
        {
            Match match = Regex.Match(text ?? string.Empty, "(\\d+\\.\\d+\\.\\d+(?:\\.\\d+)?)");
            return match.Success ? match.Groups[1].Value : string.Empty;
        }

        private static string FindFile(string root, string name)
        {
            try
            {
                string[] files = Directory.GetFiles(root, name, SearchOption.AllDirectories);
                return files.Length == 0 ? string.Empty : files[0];
            }
            catch
            {
                return string.Empty;
            }
        }

        private static bool IsVbCableSigned(string path)
        {
            try
            {
                using (X509Certificate certificate = X509Certificate.CreateFromSignedFile(path))
                {
                    string subject = certificate.Subject ?? string.Empty;
                    return subject.IndexOf("VB-Audio", StringComparison.OrdinalIgnoreCase) >= 0
                        || subject.IndexOf("Vincent Burel", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch
            {
                return false;
            }
        }

        private async Task<int> RunInstallerAsync(string installerPath,
            IProgress<ComponentOperationProgress> progress, CancellationToken cancellationToken)
        {
            try
            {
                ProcessStartInfo info = new ProcessStartInfo(installerPath);
                info.UseShellExecute = true;
                info.WorkingDirectory = Path.GetDirectoryName(installerPath);
                if (!WebView2InstallerService.IsAdministrator())
                {
                    info.Verb = "runas";
                }
                using (Process process = Process.Start(info))
                {
                    if (process == null) return -1;
                    return await Task.Run(delegate
                    {
                        while (!process.WaitForExit(250))
                        {
                            if (cancellationToken.IsCancellationRequested)
                            {
                                try { process.Kill(); } catch { }
                                cancellationToken.ThrowIfCancellationRequested();
                            }
                        }
                        return process.ExitCode;
                    }, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Win32Exception ex)
            {
                return ex.NativeErrorCode == 1223 ? -1223 : -ex.NativeErrorCode;
            }
        }

        private async Task<ComponentOperationResult> RepairInfAsync(string key, string folder, string infName,
            IProgress<ComponentOperationProgress> progress, CancellationToken cancellationToken)
        {
            string infPath = Path.Combine(_sunshineInstallPath, "tools", folder, infName);
            if (!File.Exists(infPath))
                return Fail("找不到基地版驱动文件：" + infPath);

            Report(progress, "正在请求管理员权限并安装驱动…", true, 0, false);
            int exitCode = await RunElevatedAsync(
                Path.Combine(Environment.SystemDirectory, "pnputil.exe"),
                "/add-driver \"" + infPath + "\" /install",
                progress, cancellationToken).ConfigureAwait(false);

            if (exitCode == -1223) return Fail("已取消 UAC 授权。");
            if (exitCode != 0) return Fail("驱动安装失败，pnputil 退出码 " + exitCode + "。");

            Report(progress, "驱动安装完成", false, 100, false);
            return Success(ComponentOperationCatalog.DisplayName(key) + " 已重新安装完成。");
        }

        private async Task<ComponentOperationResult> UninstallInfAsync(string infName, string deviceId,
            IProgress<ComponentOperationProgress> progress, CancellationToken cancellationToken)
        {
            Report(progress, "正在移除虚拟设备…", true, 0, false);
            if (!string.IsNullOrEmpty(deviceId))
            {
                int removeCode = await RunElevatedAsync(
                    Path.Combine(Environment.SystemDirectory, "pnputil.exe"),
                    "/remove-device \"" + deviceId + "\"",
                    progress, cancellationToken).ConfigureAwait(false);
                if (removeCode == -1223) return Fail("已取消 UAC 授权。");
                if (removeCode != 0)
                {
                    return Fail("移除设备失败，pnputil 退出码 " + removeCode + "。");
                }
            }

            Report(progress, "正在删除驱动包…", true, 0, false);
            string publishedName = await FindPublishedDriverNameAsync(infName, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(publishedName))
            {
                return Success("虚拟设备已移除；未找到可删除的驱动包，或驱动包已被系统占用。");
            }

            int deleteCode = await RunElevatedAsync(
                Path.Combine(Environment.SystemDirectory, "pnputil.exe"),
                "/delete-driver " + publishedName + " /uninstall /force",
                progress, cancellationToken).ConfigureAwait(false);
            if (deleteCode == -1223) return Fail("已取消 UAC 授权。");
            if (deleteCode != 0) return Fail("删除驱动包失败，pnputil 退出码 " + deleteCode + "。");

            Report(progress, "卸载完成", false, 100, false);
            return Success("虚拟设备与驱动包已移除，必要时请重启系统。");
        }

        private async Task<string> FindPublishedDriverNameAsync(string infName, CancellationToken cancellationToken)
        {
            ProcessResult result = await RunProcessAsync(
                Path.Combine(Environment.SystemDirectory, "pnputil.exe"), "/enum-drivers", null,
                cancellationToken).ConfigureAwait(false);
            if (!result.Success || string.IsNullOrEmpty(result.Output)) return string.Empty;

            string[] lines = result.Output.Replace("\r", string.Empty).Split('\n');
            List<string> block = new List<string>();
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0)
                {
                    string found = MatchPublishedName(block, infName);
                    if (!string.IsNullOrEmpty(found)) return found;
                    block.Clear();
                    continue;
                }
                block.Add(line);
            }
            return MatchPublishedName(block, infName);
        }

        private static string MatchPublishedName(List<string> block, string infName)
        {
            bool match = false;
            string published = string.Empty;
            for (int i = 0; i < block.Count; i++)
            {
                if (block[i].IndexOf(infName, StringComparison.OrdinalIgnoreCase) >= 0) match = true;
                Match name = Regex.Match(block[i], "(?i)(oem\\d+\\.inf)");
                if (name.Success) published = name.Groups[1].Value;
            }
            return match ? published : string.Empty;
        }

        private async Task<ComponentOperationResult> RepairDualSenseAsync(
            IProgress<ComponentOperationProgress> progress, CancellationToken cancellationToken)
        {
            string tools = Path.Combine(_sunshineInstallPath, "tools");
            string manifestPath = Path.Combine(tools, "ds5-sidecar-package.json");
            if (!File.Exists(manifestPath))
                return Fail("找不到基地版 DualSense 组件清单，请先安装基地版。");

            string manifest = File.ReadAllText(manifestPath);
            string downloadUrl = JsonString(manifest, "download_url");
            string expectedSha = JsonString(manifest, "sha256").ToLowerInvariant();
            string assetName = SafeFileName(JsonString(manifest, "asset_name"));
            long expectedSize = JsonLong(manifest, "size");
            string manifestVersion = JsonString(manifest, "component_version");
            int manifestProtocol = JsonInt(manifest, "protocol");

            if (!IsOfficialDownloadUrl(downloadUrl))
                return Fail("组件清单中的下载地址不是基地版官方地址，已拒绝下载。");
            if (string.IsNullOrEmpty(assetName)) assetName = "Sunshine.Ds5Sidecar.x64.zip";
            if (expectedSha.Length != 64)
                return Fail("组件清单缺少有效的 SHA-256。");

            if (!HasPinnedUsbIpVersion())
            {
                Report(progress, "正在准备匹配的 USB/IP 0.9.8.1 传输…", true, 0, false);
                ComponentOperationResult usbip = await RepairUsbIpAsync(progress, cancellationToken)
                    .ConfigureAwait(false);
                if (usbip == null || !usbip.Success) return usbip ?? Fail("USB/IP 准备失败。");
            }
            bool usbIpReady = IsUsbIpReady();

            string root = Path.Combine(Path.GetTempPath(), "ZayuToolbox", "Components", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string zipPath = Path.Combine(root, assetName);
            string staging = Path.Combine(root, "staging");

            try
            {
                await DownloadFileAsync(downloadUrl, zipPath, expectedSize, progress, cancellationToken)
                    .ConfigureAwait(false);

                Report(progress, "正在校验 SHA-256…", true, 0, false);
                string actualSha = Sha256File(zipPath);
                if (!string.Equals(actualSha, expectedSha, StringComparison.OrdinalIgnoreCase))
                    return Fail("组件包 SHA-256 校验失败，已停止安装。");
                if (expectedSize > 0 && new FileInfo(zipPath).Length != expectedSize)
                    return Fail("组件包大小校验失败，已停止安装。");

                Report(progress, "正在解压组件包…", true, 0, false);
                ExtractZipSafe(zipPath, staging);
                string sidecar = Path.Combine(staging, "Sunshine.Ds5Sidecar.exe");
                string core = Path.Combine(staging, "HIDMaestro.Core.dll");
                string compatibility = Path.Combine(staging, "component-compatibility.json");
                string runtime = Path.Combine(staging, "runtime.json");
                if (!File.Exists(sidecar) || !File.Exists(core) || !File.Exists(compatibility) || !File.Exists(runtime))
                    return Fail("组件包内容不完整，已停止安装。");

                string runtimeText = File.ReadAllText(runtime);
                string compatibilityText = File.ReadAllText(compatibility);
                string componentVersion = JsonString(runtimeText, "component_version");
                int runtimeProtocol = JsonInt(runtimeText, "protocol");
                string runtimeUsbip = JsonString(runtimeText, "usbip_version");
                string runtimeTarget = JsonString(runtimeText, "target");
                string compatibilityVersion = JsonString(compatibilityText, "component_version");
                string hidmaestroVersion = JsonString(compatibilityText, "hidmaestro_version");
                string compatibilityUsbip = JsonNestedString(compatibilityText, "usbip", "version");
                string compatibilityPolicy = JsonNestedString(compatibilityText, "usbip", "policy");
                if (string.IsNullOrEmpty(componentVersion)
                    || componentVersion != compatibilityVersion
                    || runtimeUsbip != compatibilityUsbip
                    || compatibilityPolicy != "externally-managed-v1")
                {
                    return Fail("组件包版本 / USB-IP 兼容性不一致，已停止安装。");
                }
                if (string.IsNullOrEmpty(hidmaestroVersion))
                {
                    return Fail("组件包缺少 HIDMaestro 版本信息，已停止安装。");
                }
                if (!string.IsNullOrEmpty(manifestVersion)
                    && !string.Equals(componentVersion, manifestVersion, StringComparison.Ordinal))
                {
                    return Fail("组件包版本与基地版清单不匹配（清单 "
                        + manifestVersion + "，包内 " + componentVersion + "），已停止安装。");
                }
                if (manifestProtocol > 0 && runtimeProtocol != manifestProtocol)
                {
                    return Fail("组件包协议版本与基地版清单不匹配，已停止安装。");
                }
                if (runtimeProtocol <= 0 || runtimeUsbip.Length == 0)
                {
                    return Fail("组件包缺少协议或 USB/IP 版本信息，已停止安装。");
                }
                if (!string.Equals(runtimeTarget, "win-x64-self-contained",
                    StringComparison.Ordinal))
                {
                    return Fail("组件包目标平台与基地版不一致，已停止安装。");
                }
                if (VersionUpdateService.IsNewer(
                    ComponentOperationCatalog.RequiredUsbIpVersion, runtimeUsbip))
                {
                    return Fail("组件包 USB/IP 版本低于基地版要求的 "
                        + ComponentOperationCatalog.RequiredUsbIpVersion + "，已停止安装。");
                }

                if (Process.GetProcessesByName("Sunshine.Ds5Sidecar").Length > 0)
                {
                    return Fail("DualSense Sidecar 正在运行；请先结束当前串流再修复组件。");
                }

                Report(progress, "正在验证 Sidecar 运行时能力…", true, 0, false);
                if (!usbIpReady)
                {
                    Report(progress, "USB/IP 已安装但尚未就绪，跳过运行时探测；重启后再验证。", true, 0, false);
                }
                else
                {
                    ProcessResult probe = await RunProcessAsync(sidecar, "--probe", null,
                        Path.GetDirectoryName(sidecar), cancellationToken).ConfigureAwait(false);
                    if (!probe.Success || probe.ExitCode != 0)
                    {
                        return Fail("Sidecar 运行时探测失败，组件不会激活。");
                    }
                    if (!JsonBool(probe.Output, "standard")
                        || !JsonBool(probe.Output, "composite")
                        || !JsonBool(probe.Output, "genshin_compatibility_identity")
                        || JsonInt(probe.Output, "protocol") != runtimeProtocol
                        || !string.Equals(JsonString(probe.Output, "component_version"), componentVersion,
                            StringComparison.Ordinal)
                        || !string.Equals(JsonString(probe.Output, "usbip_policy"), compatibilityPolicy,
                            StringComparison.Ordinal)
                        || !string.Equals(JsonString(probe.Output, "usbip_version"), runtimeUsbip,
                            StringComparison.Ordinal))
                    {
                        return Fail("Sidecar 探测结果与基地版兼容组合不一致，组件不会激活。");
                    }
                }

                string componentJson = BuildComponentManifest(
                    runtimeProtocol, componentVersion, hidmaestroVersion, runtimeUsbip,
                    Sha256File(sidecar), Sha256File(core), Sha256File(compatibility),
                    expectedSha, downloadUrl);
                File.WriteAllText(Path.Combine(staging, "component.json"), componentJson,
                    new UTF8Encoding(false));

                string activePath = Path.Combine(tools, "sunshine-ds5-component", "active");
                Report(progress, "正在安装到 Sunshine 组件目录（UAC）…", true, 0, false);
                int copyCode = await CopyDirectoryAsync(staging, activePath, progress, cancellationToken)
                    .ConfigureAwait(false);
                if (copyCode == -1223) return Fail("已取消 UAC 授权。");
                if (copyCode < 0 || copyCode > 7)
                    return Fail("写入组件目录失败，robocopy 退出码 " + copyCode + "。");

                Report(progress, "组件安装完成", false, 100, false);
                return Success("DualSense 组件已修复到版本 " + componentVersion + "。");
            }
            finally
            {
                TryDeleteDirectory(root);
            }
        }

        private async Task<ComponentOperationResult> UninstallDualSenseAsync(
            IProgress<ComponentOperationProgress> progress, CancellationToken cancellationToken)
        {
            string activePath = Path.Combine(_sunshineInstallPath, "tools",
                "sunshine-ds5-component", "active");
            if (!Directory.Exists(activePath))
            {
                return Success("DualSense 组件未安装，无需卸载。");
            }

            Report(progress, "正在移除 DualSense 组件文件（UAC）…", true, 0, false);
            int code;
            if (WebView2InstallerService.IsAdministrator())
            {
                try
                {
                    Directory.Delete(activePath, true);
                    code = 0;
                }
                catch (Exception ex)
                {
                    return Fail("删除组件目录失败：" + ex.Message);
                }
            }
            else
            {
                code = await RunElevatedAsync(
                    Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                    "/c rd /s /q \"" + activePath + "\"",
                    progress, cancellationToken).ConfigureAwait(false);
            }
            if (code == -1223) return Fail("已取消 UAC 授权。");
            if (code != 0) return Fail("删除组件目录失败，退出码 " + code + "。");

            Report(progress, "组件已移除", false, 100, false);
            return Success("DualSense 组件已移除；HIDMaestro 与 USB/IP 驱动保留。");
        }

        private async Task DownloadFileAsync(string url, string destination, long expectedSize,
            IProgress<ComponentOperationProgress> progress, CancellationToken cancellationToken)
        {
            string directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            string part = destination + ".part";
            TryDelete(part);

            using (HttpClientHandler handler = new HttpClientHandler())
            {
                handler.AllowAutoRedirect = true;
                handler.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                using (HttpClient client = new HttpClient(handler))
                {
                    client.Timeout = TimeSpan.FromMinutes(30);
                    client.DefaultRequestHeaders.UserAgent.ParseAdd(
                        "ZayuToolbox/1.8 (Windows NT 10.0; Win64; x64)");
                    using (HttpResponseMessage response = await client.GetAsync(
                        url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
                    {
                        response.EnsureSuccessStatusCode();
                        long total = expectedSize;
                        if (total <= 0 && response.Content.Headers.ContentLength.HasValue)
                            total = response.Content.Headers.ContentLength.Value;

                        using (Stream input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (FileStream output = new FileStream(part, FileMode.Create, FileAccess.Write,
                            FileShare.None, 81920, true))
                        {
                            byte[] buffer = new byte[81920];
                            long received = 0;
                            long lastBytes = 0;
                            DateTime lastTime = DateTime.UtcNow;
                            DateTime lastReport = DateTime.MinValue;
                            double smoothedSpeed = 0;
                            while (true)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                int read = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken)
                                    .ConfigureAwait(false);
                                if (read <= 0) break;
                                await output.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
                                received += read;

                                DateTime now = DateTime.UtcNow;
                                double seconds = (now - lastTime).TotalSeconds;
                                if (seconds >= 0.2)
                                {
                                    double instant = (received - lastBytes) / seconds;
                                    smoothedSpeed = smoothedSpeed <= 0 ? instant : smoothedSpeed * 0.7 + instant * 0.3;
                                    lastBytes = received;
                                    lastTime = now;
                                }

                                if (progress != null && (now - lastReport).TotalMilliseconds >= 180)
                                {
                                    ComponentOperationProgress item = new ComponentOperationProgress();
                                    item.Stage = "正在下载组件包…";
                                    item.BytesReceived = received;
                                    item.TotalBytes = total;
                                    item.IsIndeterminate = total <= 0;
                                    item.Percent = total > 0 ? (int)Math.Min(100, received * 100L / total) : 0;
                                    item.SpeedText = smoothedSpeed > 0 ? FormatSpeed(smoothedSpeed) : "—";
                                    item.EtaText = total > 0 && smoothedSpeed > 0
                                        ? FormatEta((total - received) / smoothedSpeed) : "—";
                                    progress.Report(item);
                                    lastReport = now;
                                }
                            }
                        }
                    }
                }
            }

            if (File.Exists(destination)) TryDelete(destination);
            File.Move(part, destination);
            Report(progress, "下载完成", false, 100, false);
        }

        private async Task<int> CopyDirectoryAsync(string source, string destination,
            IProgress<ComponentOperationProgress> progress, CancellationToken cancellationToken)
        {
            string parent = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
            {
                try { Directory.CreateDirectory(parent); }
                catch { }
            }

            string arguments = "\"" + source + "\" \"" + destination +
                "\" /MIR /R:1 /W:1 /NFL /NDL /NJH /NJS /NP";
            if (WebView2InstallerService.IsAdministrator())
            {
                ProcessResult result = await RunProcessAsync(
                    Path.Combine(Environment.SystemDirectory, "robocopy.exe"), arguments, null,
                    cancellationToken).ConfigureAwait(false);
                return result.ExitCode;
            }
            return await RunElevatedAsync(
                Path.Combine(Environment.SystemDirectory, "robocopy.exe"), arguments,
                progress, cancellationToken).ConfigureAwait(false);
        }

        private async Task<int> RunElevatedAsync(string fileName, string arguments,
            IProgress<ComponentOperationProgress> progress, CancellationToken cancellationToken)
        {
            if (WebView2InstallerService.IsAdministrator())
            {
                ProcessResult result = await RunProcessAsync(fileName, arguments, null, cancellationToken)
                    .ConfigureAwait(false);
                return result.ExitCode;
            }

            try
            {
                ProcessStartInfo info = new ProcessStartInfo(fileName, arguments);
                info.UseShellExecute = true;
                info.Verb = "runas";
                info.WindowStyle = ProcessWindowStyle.Hidden;
                using (Process process = Process.Start(info))
                {
                    if (process == null) return -1;
                    return await Task.Run(delegate
                    {
                        while (!process.WaitForExit(250))
                        {
                            if (cancellationToken.IsCancellationRequested)
                            {
                                try { process.Kill(); } catch { }
                                cancellationToken.ThrowIfCancellationRequested();
                            }
                        }
                        return process.ExitCode;
                    }, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Win32Exception ex)
            {
                return ex.NativeErrorCode == 1223 ? -1223 : -ex.NativeErrorCode;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return -1;
            }
        }

        private static Task<ProcessResult> RunProcessAsync(string fileName, string arguments,
            Action<string> log, CancellationToken cancellationToken)
        {
            return RunProcessAsync(fileName, arguments, log, null, cancellationToken);
        }

        private static Task<ProcessResult> RunProcessAsync(string fileName, string arguments,
            Action<string> log, string workingDirectory, CancellationToken cancellationToken)
        {
            return Task.Run(delegate
            {
            ProcessResult result = new ProcessResult();
            try
            {
                ProcessStartInfo info = new ProcessStartInfo(fileName, arguments);
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                info.RedirectStandardOutput = true;
                info.RedirectStandardError = true;
                if (!string.IsNullOrEmpty(workingDirectory)) info.WorkingDirectory = workingDirectory;
                using (Process process = new Process())
                {
                    process.StartInfo = info;
                    process.Start();
                    Task<string> output = process.StandardOutput.ReadToEndAsync();
                    Task<string> error = process.StandardError.ReadToEndAsync();
                    DateTime deadline = DateTime.UtcNow.Add(ProcessTimeout);
                    while (!process.WaitForExit(250))
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            try { process.Kill(); } catch { }
                            cancellationToken.ThrowIfCancellationRequested();
                        }
                        if (DateTime.UtcNow >= deadline)
                        {
                            try { process.Kill(); } catch { }
                            result.ExitCode = -2;
                            return result;
                        }
                    }
                    result.ExitCode = process.ExitCode;
                    result.Output = output.Result + error.Result;
                    result.Success = true;
                    if (log != null && result.Output.Length > 0) log(result.Output.Trim());
                    return result;
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ExitCode = -1;
                result.Output = ex.Message;
                return result;
            }
            }, cancellationToken);
        }

        private static void ExtractZipSafe(string zipPath, string destination)
        {
            Directory.CreateDirectory(destination);
            string root = Path.GetFullPath(destination).TrimEnd('\\') + "\\";
            using (ZipArchive archive = ZipFile.OpenRead(zipPath))
            {
                for (int i = 0; i < archive.Entries.Count; i++)
                {
                    ZipArchiveEntry entry = archive.Entries[i];
                    string target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("组件包包含非法路径，已中止。");
                    if (entry.FullName.EndsWith("/", StringComparison.Ordinal)
                        || entry.FullName.EndsWith("\\", StringComparison.Ordinal))
                    {
                        Directory.CreateDirectory(target);
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    entry.ExtractToFile(target, true);
                }
            }
        }

        private static string BuildComponentManifest(int protocol, string version, string hidmaestroVersion,
            string usbipVersion, string sidecarSha, string coreSha, string compatibilitySha,
            string packageSha, string sidecarSource)
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("{");
            builder.AppendLine("  \"protocol\": " + protocol + ",");
            builder.AppendLine("  \"component_version\": \"" + JsonEscape(version) + "\",");
            builder.AppendLine("  \"hidmaestro_version\": \"" + JsonEscape(hidmaestroVersion) + "\",");
            builder.AppendLine("  \"usbip_policy\": \"externally-managed-v1\",");
            builder.AppendLine("  \"usbip_version\": \"" + JsonEscape(usbipVersion) + "\",");
            builder.AppendLine("  \"sidecar_file\": \"Sunshine.Ds5Sidecar.exe\",");
            builder.AppendLine("  \"sidecar_sha256\": \"" + sidecarSha.ToLowerInvariant() + "\",");
            builder.AppendLine("  \"sha256\": \"" + coreSha.ToLowerInvariant() + "\",");
            builder.AppendLine("  \"compatibility_sha256\": \"" + compatibilitySha.ToLowerInvariant() + "\",");
            builder.AppendLine("  \"sidecar_package_sha256\": \"" + (packageSha ?? string.Empty).ToLowerInvariant() + "\",");
            builder.AppendLine("  \"sidecar_source\": \"" + JsonEscape(sidecarSource) + "\"");
            builder.AppendLine("}");
            return builder.ToString();
        }

        private static string JsonString(string json, string key)
        {
            Match match = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"([^\"]*)\"");
            return match.Success ? match.Groups[1].Value : string.Empty;
        }

        private static string JsonNestedString(string json, string objectKey, string key)
        {
            Match match = Regex.Match(json,
                "\"" + Regex.Escape(objectKey) + "\"\\s*:\\s*\\{[^}]*\"" +
                Regex.Escape(key) + "\"\\s*:\\s*\"([^\"]*)\"");
            return match.Success ? match.Groups[1].Value : string.Empty;
        }

        private static long JsonLong(string json, string key)
        {
            Match match = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(\\d+)");
            long value;
            return match.Success && long.TryParse(match.Groups[1].Value, out value) ? value : 0;
        }

        private static int JsonInt(string json, string key)
        {
            Match match = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(\\d+)");
            int value;
            return match.Success && int.TryParse(match.Groups[1].Value, out value) ? value : 0;
        }

        private static bool JsonBool(string json, string key)
        {
            Match match = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(true|false)");
            return match.Success && string.Equals(match.Groups[1].Value, "true",
                StringComparison.OrdinalIgnoreCase);
        }

        private static string JsonEscape(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static bool IsOfficialDownloadUrl(string url)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri)) return false;
            return uri.Scheme == Uri.UriSchemeHttps
                && string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
                && uri.AbsolutePath.StartsWith("/AlkaidLab/foundation-sunshine/releases/download/",
                    StringComparison.OrdinalIgnoreCase);
        }

        private static string SafeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            string file = Path.GetFileName(name);
            return string.Equals(file, name, StringComparison.Ordinal) ? file : string.Empty;
        }

        private static string Sha256File(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
            {
                byte[] hash = sha.ComputeHash(stream);
                StringBuilder builder = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++) builder.Append(hash[i].ToString("x2"));
                return builder.ToString();
            }
        }

        private static string FormatSpeed(double bytesPerSecond)
        {
            if (bytesPerSecond < 1024) return bytesPerSecond.ToString("0") + " B/s";
            if (bytesPerSecond < 1024 * 1024) return (bytesPerSecond / 1024).ToString("0.0") + " KB/s";
            return (bytesPerSecond / 1024 / 1024).ToString("0.00") + " MB/s";
        }

        private static string FormatEta(double seconds)
        {
            if (seconds < 1 || double.IsNaN(seconds) || double.IsInfinity(seconds)) return "—";
            int total = (int)Math.Ceiling(seconds);
            return (total / 60).ToString("00") + ":" + (total % 60).ToString("00");
        }

        private static void Report(IProgress<ComponentOperationProgress> progress, string stage,
            bool indeterminate, int percent, bool showSpeed)
        {
            if (progress == null) return;
            ComponentOperationProgress item = new ComponentOperationProgress();
            item.Stage = stage;
            item.IsIndeterminate = indeterminate;
            item.Percent = percent;
            progress.Report(item);
        }

        private static ComponentOperationResult Success(string message)
        {
            ComponentOperationResult result = new ComponentOperationResult();
            result.Success = true;
            result.Message = message;
            return result;
        }

        private static ComponentOperationResult Fail(string message)
        {
            ComponentOperationResult result = new ComponentOperationResult();
            result.Success = false;
            result.Message = message;
            return result;
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }

        private static void TryDeleteDirectory(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); }
            catch { }
        }

        private sealed class ProcessResult
        {
            public bool Success;
            public int ExitCode;
            public string Output = string.Empty;
        }
    }
}
