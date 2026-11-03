# 在独立 Windows 桌面上运行 UI 冒烟测试，避免窗口抢焦点、干扰当前桌面。
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File .\tools\ui-test-background.ps1
#   powershell -ExecutionPolicy Bypass -File .\tools\ui-test-background.ps1 -AppPath "D:\...\杂鱼串流工具箱.exe"
param(
    [string]$AppPath = "",
    [int]$TimeoutSeconds = 300
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrEmpty($AppPath)) {
    $AppPath = Join-Path $root "src\IpInfo.App\bin\Debug\杂鱼串流工具箱.exe"
}
if (-not (Test-Path $AppPath)) {
    throw "找不到应用：$AppPath，请先构建"
}

$testScript = Join-Path $PSScriptRoot "ui-smoke-test.ps1"
if (-not (Test-Path $testScript)) {
    throw "找不到 UI 测试脚本：$testScript"
}

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class DshDesktopRunner
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFO
    {
        public int cb;
        public string lpReserved;
        public string lpDesktop;
        public string lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateDesktop(string lpszDesktop, IntPtr lpszDevice,
        IntPtr pDevmode, int dwFlags, uint dwDesiredAccess, IntPtr lpsa);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool CloseDesktop(IntPtr hDesktop);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool CreateProcess(string lpApplicationName, StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles,
        uint dwCreationFlags, IntPtr lpEnvironment, string lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll")]
    public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll")]
    public static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll")]
    public static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(IntPtr hObject);
}
'@

$desktopName = "ZayuUITest-" + [Guid]::NewGuid().ToString("N")
$desktop = [DshDesktopRunner]::CreateDesktop($desktopName, [IntPtr]::Zero, [IntPtr]::Zero, 0,
    [uint32]0x000F01FF, [IntPtr]::Zero)
if ($desktop -eq [IntPtr]::Zero) {
    $err = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
    throw "创建测试桌面失败（Win32 错误 $err）。"
}

$logPath = Join-Path $env:TEMP ("zayu-ui-test-" + [Guid]::NewGuid().ToString("N") + ".log")
$psExe = Join-Path $PSHOME "powershell.exe"
if (-not (Test-Path $psExe)) {
    $psExe = "powershell.exe"
}

$inner = "& '" + $testScript + "' -AppPath '" + $AppPath + "' -SkipClipboard *> '" + $logPath + "'; exit `$LASTEXITCODE"
$encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($inner))
$commandLine = "`"$psExe`" -NoProfile -ExecutionPolicy Bypass -EncodedCommand $encoded"

$si = New-Object DshDesktopRunner+STARTUPINFO
$si.cb = [Runtime.InteropServices.Marshal]::SizeOf([Type]([DshDesktopRunner+STARTUPINFO]))
$si.lpDesktop = "winsta0\" + $desktopName
$si.dwFlags = 0
$si.wShowWindow = 0

$pi = New-Object DshDesktopRunner+PROCESS_INFORMATION
$cmd = New-Object Text.StringBuilder $commandLine
$createOk = [DshDesktopRunner]::CreateProcess($psExe, $cmd, [IntPtr]::Zero, [IntPtr]::Zero,
    $false, [uint32]0x08000000, [IntPtr]::Zero, $root, [ref]$si, [ref]$pi)
if (-not $createOk) {
    $err = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
    [DshDesktopRunner]::CloseDesktop($desktop) | Out-Null
    throw "在测试桌面启动 UI 测试失败（Win32 错误 $err）。"
}

$exitCode = 1
try {
    $wait = [DshDesktopRunner]::WaitForSingleObject($pi.hProcess, [uint32]($TimeoutSeconds * 1000))
    if ($wait -eq 0x00000102) {
        [DshDesktopRunner]::TerminateProcess($pi.hProcess, 124) | Out-Null
        Write-Host "UI 测试超时（$TimeoutSeconds 秒），已终止。" -ForegroundColor Yellow
    }

    $code = 0
    [DshDesktopRunner]::GetExitCodeProcess($pi.hProcess, [ref]$code) | Out-Null
    $exitCode = [int]$code
}
finally {
    if ($pi.hThread -ne [IntPtr]::Zero) { [DshDesktopRunner]::CloseHandle($pi.hThread) | Out-Null }
    if ($pi.hProcess -ne [IntPtr]::Zero) { [DshDesktopRunner]::CloseHandle($pi.hProcess) | Out-Null }
    [DshDesktopRunner]::CloseDesktop($desktop) | Out-Null
}

if (Test-Path $logPath) {
    Get-Content -LiteralPath $logPath -Encoding UTF8 | ForEach-Object { Write-Host $_ }
    Remove-Item -LiteralPath $logPath -Force -ErrorAction SilentlyContinue
}
else {
    Write-Host "UI 测试没有生成日志。" -ForegroundColor Yellow
}

exit $exitCode
