# UI 冒烟测试：离屏启动 WPF 应用，通过 UI Automation 验证：
#   1. 默认状态 IP 被遮罩（不出现完整 IPv4/IPv6）；
#   2. 每个「复制」按钮复制的是完整 IP（同时覆盖 IPv4 与 IPv6）；
#   3. 「显示 IP」按钮能切换为完整地址，「隐藏 IP」能恢复遮罩；
#   4. IPv6 入站自检可启动、停止，并能从回环地址收到连接；
#   5. WebView2 运行库状态、诊断复制与安装窗口可正常打开。
#
# 用法（先构建 Debug）：
#   powershell -ExecutionPolicy Bypass -File .\tools\ui-smoke-test.ps1
param(
    [string]$AppPath = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrEmpty($AppPath)) {
    $AppPath = Join-Path $root "src\IpInfo.App\bin\Debug\杂鱼串流工具箱.exe"
}
if (-not (Test-Path $AppPath)) {
    throw "找不到应用：$AppPath，请先运行 tools\build.ps1"
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms

$originalClipboard = $null
try { $originalClipboard = [System.Windows.Forms.Clipboard]::GetText() } catch { }
$originalLoopbackEnv = $env:IPINFO_INBOUND_BIND_LOOPBACK
$env:IPINFO_INBOUND_BIND_LOOPBACK = "1"

$checks = 0
$failures = 0
function Check([string]$name, [bool]$ok, [string]$detail = "") {
    $script:checks++
    if ($ok) {
        Write-Host ("  [PASS] " + $name) -ForegroundColor Green
    } else {
        $script:failures++
        Write-Host ("  [FAIL] " + $name + $(if ($detail) { "：" + $detail } else { "" })) -ForegroundColor Red
    }
}

function Get-WindowTexts($window) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Text)
    $elements = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
    $names = New-Object System.Collections.Generic.List[string]
    foreach ($element in $elements) {
        $name = $element.Current.Name
        if (-not [string]::IsNullOrEmpty($name)) { $names.Add($name) }
    }
    return ,$names.ToArray()
}

function Get-WindowByTitle([string]$title) {
    $nameCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $title)
    $typeCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Window)
    $condition = New-Object System.Windows.Automation.AndCondition($nameCondition, $typeCondition)
    return [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Get-ElementByAutomationId($window, [string]$automationId) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $automationId)
    return $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Get-EnabledCopyButtons($window) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    $elements = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
    $result = New-Object System.Collections.Generic.List[object]
    foreach ($element in $elements) {
        if ($element.Current.Name -eq "复制" -and $element.Current.IsEnabled) { $result.Add($element) }
    }
    return ,$result.ToArray()
}

function Invoke-Element($element) {
    $pattern = $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
}

function Wait-ClipboardTextMatch([string]$pattern, [int]$timeoutMs = 2500) {
    $deadline = (Get-Date).AddMilliseconds($timeoutMs)
    while ((Get-Date) -lt $deadline) {
        try {
            $text = [System.Windows.Forms.Clipboard]::GetText()
            if (-not [string]::IsNullOrEmpty($text) -and $text -match $pattern) { return $true }
        } catch { }
        Start-Sleep -Milliseconds 150
    }
    return $false
}

function Select-NavItem($window, [string]$name) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    $item = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if ($item -eq $null) { return $false }
    try {
        $pattern = $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        $pattern.Select()
    } catch {
        return $false
    }
    Start-Sleep -Milliseconds 600
    return $true
}

$process = $null
try {
    Write-Host "启动应用（离屏）…" -ForegroundColor Cyan
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $AppPath
    $psi.Arguments = "--offscreen"
    $psi.WorkingDirectory = Split-Path -Parent $AppPath
    $psi.UseShellExecute = $false
    $process = [System.Diagnostics.Process]::Start($psi)

    $window = $null
    $deadline = (Get-Date).AddSeconds(25)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 400
        $condition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, "杂鱼串流工具箱")
        $window = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Children, $condition)
        if ($window -ne $null) {
            $texts = Get-WindowTexts $window
            if (($texts -join "`n") -match "Sunshine 版本自检") { break }
        }
    }
    if ($window -eq $null) { throw "没有找到应用窗口（离线启动失败）" }

    $startTexts = (Get-WindowTexts $window) -join "`n"
    Check "默认打开 Sunshine 版本页" ($startTexts -match "Sunshine 版本自检")

    # 网络检测改为手动触发：切到该页应显示未检测状态
    Check "切换到网络检测页" (Select-NavItem $window "网络检测")
    Start-Sleep -Milliseconds 600
    $idleTexts = (Get-WindowTexts $window) -join "`n"
    Check "网络检测不再自动执行" (($idleTexts -match "尚未检测") -and ($idleTexts -notmatch "NAT 后"))

    $refreshButton = Get-ElementByAutomationId $window "RefreshButton"
    Check "网络检测页提供重新检测按钮" ($refreshButton -ne $null -and $refreshButton.Current.IsEnabled)
    if ($refreshButton -ne $null -and $refreshButton.Current.IsEnabled) { Invoke-Element $refreshButton }

    # 等待检测完成：出现 NAT 结果（避免读到“检测中…”）
    $ready = $false
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        $texts = Get-WindowTexts $window
        $joined = $texts -join "`n"
        if ($joined -match "NAT 后|公网直连|检测失败") { $ready = $true; break }
    }
    Start-Sleep -Milliseconds 800
    Check "检测已完成" $ready "30 秒内未出现 NAT 结果"

    $texts = Get-WindowTexts $window
    $joined = $texts -join "`n"

    # 默认遮罩：不应出现独立的完整 IPv4 文本
    $plainIpv4 = @($texts | Where-Object { $_ -match '^\d{1,3}(\.\d{1,3}){3}$' })
    $maskedIpv4 = @($texts | Where-Object { $_ -match '^\d{1,3}\.\*\.\*\.\d{1,3}$' })
    $maskedIpv6 = @($texts | Where-Object { $_ -match '^[0-9a-fA-F]+(?::\*+){6}:[0-9a-fA-F]+$' })
    $leak = @($texts | Where-Object { $_ -match '\d{1,3}(\.\d{1,3}){3}' -and $_ -notmatch 'STUN 服务器' -and $_ -notmatch '0\.0\.0\.0' })

    Check "默认状态显示 IPv4 遮罩" ($maskedIpv4.Count -gt 0) "没有找到形如 192.*.*.100 的文本"
    Check "默认状态不出现完整 IPv4" ($leak.Count -eq 0) ("发现未遮罩的 IP 文本：" + ($leak -join " / "))
    Check "默认状态显示 IPv6 遮罩" ($maskedIpv6.Count -gt 0) "没有找到形如 240e:****:…:865c 的文本"

    # 左侧菜单
    $hasNav = ($joined -match "网络检测") -and ($joined -match "NAT 跃点跟踪") -and ($joined -match "Sunshine 版本") -and ($joined -match "WebView2 运行库")
    Check "显示左侧功能菜单" $hasNav

    # 网络检测页：地址 / STUN / 入站
    Check "网络检测页包含 STUN/NAT 结果" (($joined -match "公网出口 IP") -or ($joined -match "NAT 类型"))
    Check "网络检测页包含 IPv6 入站自检" (($joined -match "IPv6 入站可达性") -and ($joined -match "启动入站自检"))

    # NAT 跃点跟踪页
    Check "切换到 NAT 跃点跟踪页" (Select-NavItem $window "NAT 跃点跟踪")
    $natTraceStart = Get-ElementByAutomationId $window "NatTraceStartButton"
    Check "显示多层 NAT 跃点跟踪卡片" ($natTraceStart -ne $null -and $natTraceStart.Current.IsEnabled)
    $hopReveal = Get-ElementByAutomationId $window "HopRevealToggle"
    $hopRefresh = Get-ElementByAutomationId $window "HopRefreshButton"
    Check "跃点页提供显示 IP 与重新检测" ($hopReveal -ne $null -and $hopRefresh -ne $null)

    # Sunshine 版本页
    Check "切换到 Sunshine 版本页" (Select-NavItem $window "Sunshine 版本")
    $sunshineTexts = (Get-WindowTexts $window) -join "`n"
    $sunshineCopy = Get-ElementByAutomationId $window "SunshineCopyButton"
    Check "显示 Sunshine 版本自检卡片" (($sunshineTexts -match "Sunshine 版本自检") -and ($sunshineTexts -match "安装来源："))
    Check "Sunshine 页不提供显示 IP" ($sunshineTexts -notmatch "显示 IP")
    Check "存在 Sunshine 诊断复制按钮" ($sunshineCopy -ne $null -and $sunshineCopy.Current.IsEnabled)
    if ($sunshineCopy -ne $null -and $sunshineCopy.Current.IsEnabled) {
        Invoke-Element $sunshineCopy
        Check "Sunshine 诊断信息包含检测结论" (Wait-ClipboardTextMatch "(?s)Sunshine.*检测结果")
    }

    # WebView2 运行库页
    Check "切换到 WebView2 运行库页" (Select-NavItem $window "WebView2 运行库")
    $webView2Texts = (Get-WindowTexts $window) -join "`n"
    Check "显示 WebView2 运行库卡片" (($webView2Texts -match "WebView2 运行库") -and ($webView2Texts -match "版本：") -and ($webView2Texts -match "路径："))
    Check "显示 WebView2 检测状态" (($webView2Texts -match "已安装") -or ($webView2Texts -match "未安装") -or ($webView2Texts -match "安装异常"))
    Check "运行库页不提供显示 IP" ($webView2Texts -notmatch "显示 IP")

    # 返回网络检测页
    Check "返回网络检测页" (Select-NavItem $window "网络检测")

    # 复制按钮：应复制完整 IP（IPv4 与 IPv6 都覆盖）
    Write-Host "测试复制按钮…" -ForegroundColor Cyan
    $buttons = Get-EnabledCopyButtons $window
    Check "存在可用的复制按钮" ($buttons.Count -gt 0)
    $sawIpv4 = $false
    $sawIpv6 = $false
    $limit = [Math]::Min($buttons.Count, 6)
    for ($i = 0; $i -lt $limit; $i++) {
        Invoke-Element $buttons[$i]
        Start-Sleep -Milliseconds 450
        $clip = $null
        for ($attempt = 0; $attempt -lt 5 -and $clip -eq $null; $attempt++) {
            try {
                $clip = [System.Windows.Forms.Clipboard]::GetText()
                if ($clip -eq $null) { Start-Sleep -Milliseconds 200 }
            } catch {
                Start-Sleep -Milliseconds 300
            }
        }
        if ([string]::IsNullOrEmpty($clip)) { continue }
        $clip = $clip.Trim()
        if ($clip -match '^\d{1,3}(\.\d{1,3}){3}$') { $sawIpv4 = $true }
        if ($clip -match '^[0-9a-fA-F:]+$' -and $clip.Contains(":")) { $sawIpv6 = $true }
    }
    Check "复制按钮复制完整 IPv4" $sawIpv4
    Check "复制按钮复制完整 IPv6" $sawIpv6

    # WebView2 诊断复制与安装窗口
    Write-Host "测试 WebView2 诊断与安装窗口…" -ForegroundColor Cyan
    Select-NavItem $window "WebView2 运行库" | Out-Null
    $webView2Copy = Get-ElementByAutomationId $window "WebView2CopyButton"
    Check "存在 WebView2 诊断复制按钮" ($webView2Copy -ne $null -and $webView2Copy.Current.IsEnabled)
    if ($webView2Copy -ne $null -and $webView2Copy.Current.IsEnabled) {
        Invoke-Element $webView2Copy
        Check "WebView2 诊断信息包含版本和路径" (Wait-ClipboardTextMatch "(?s)WebView2.*检测结果")
    }

    $webView2Install = Get-ElementByAutomationId $window "WebView2InstallButton"
    Check "存在 WebView2 安装按钮" ($webView2Install -ne $null -and $webView2Install.Current.IsEnabled)
    if ($webView2Install -ne $null -and $webView2Install.Current.IsEnabled) {
        Invoke-Element $webView2Install
        Start-Sleep -Milliseconds 700
        $wvDialog = Get-WindowByTitle "安装 / 修复 WebView2"
        Check "点击后弹出 WebView2 安装窗口" ($wvDialog -ne $null)
        if ($wvDialog -ne $null) {
            $wvDialogTexts = (Get-WindowTexts $wvDialog) -join "`n"
            Check "安装窗口包含安装方式与签名说明" (($wvDialogTexts -match "安装方式") -and ($wvDialogTexts -match "数字签名"))
            $wvCancel = $wvDialog.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                (New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::NameProperty, "取消")))
            Check "安装窗口包含取消按钮" ($wvCancel -ne $null)
            if ($wvCancel -ne $null) {
                Invoke-Element $wvCancel
                Start-Sleep -Milliseconds 500
                Check "关闭 WebView2 安装窗口" ((Get-WindowByTitle "安装 / 修复 WebView2") -eq $null)
            }
        }
    }

    Select-NavItem $window "网络检测" | Out-Null

    # 显示 IP
    Write-Host "测试「显示 IP」按钮…" -ForegroundColor Cyan
    $toggleCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, "显示 IP")
    $toggle = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $toggleCondition)
    Check "找到「显示 IP」按钮" ($toggle -ne $null)
    if ($toggle -ne $null) {
        $togglePattern = $toggle.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
        $togglePattern.Toggle()
        Start-Sleep -Milliseconds 800

        $revealedTexts = Get-WindowTexts $window
        $plainAfter = @($revealedTexts | Where-Object { $_ -match '^\d{1,3}(\.\d{1,3}){3}$' })
        $v6After = @($revealedTexts | Where-Object { $_ -match '^[0-9a-fA-F:]{6,}$' -and $_.Contains(":") -and $_ -notmatch '\*' })
        Check "点击后显示完整 IPv4" ($plainAfter.Count -gt 0)
        Check "点击后显示完整 IPv6" ($v6After.Count -gt 0)

        $hideCondition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, "隐藏 IP")
        $hide = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $hideCondition)
        Check "按钮文字切换为「隐藏 IP」" ($hide -ne $null)
        if ($hide -ne $null) {
            $hidePattern = $hide.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
            $hidePattern.Toggle()
            Start-Sleep -Milliseconds 800
            $maskedAgain = @((Get-WindowTexts $window) | Where-Object { $_ -match '^\d{1,3}\.\*\.\*\.\d{1,3}$' })
            Check "再次点击恢复遮罩" ($maskedAgain.Count -gt 0)
        }
    }

    # IPv6 入站自检：弹窗说明 + 3 秒倒计时 + 启动/停止/回环连接
    Write-Host "测试 IPv6 入站自检弹窗…" -ForegroundColor Cyan
    $startCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, "启动入站自检")
    $startButton = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $startCondition)
    if ($startButton -ne $null -and $startButton.Current.IsEnabled) {
        Invoke-Element $startButton
        Start-Sleep -Milliseconds 700

        $dialog = Get-WindowByTitle "IPv6 入站自检"
        Check "点击后弹出 IPv6 入站说明窗口" ($dialog -ne $null)
        if ($dialog -ne $null) {
            $dialogJoined = (Get-WindowTexts $dialog) -join "`n"
            Check "弹窗提示关闭 Wi-Fi 并使用移动数据" (($dialogJoined -match "关闭手机") -and ($dialogJoined -match "移动数据"))
            $understandCondition = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::NameProperty, "了解")
            $understandButton = $dialog.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $understandCondition)
            Check "弹窗包含「了解」按钮" ($understandButton -ne $null)
            if ($understandButton -ne $null) {
                Check "倒计时内「了解」按钮不可用" (-not $understandButton.Current.IsEnabled)
                Start-Sleep -Milliseconds 3400
                Check "3 秒倒计时结束后「了解」可点击" ($understandButton.Current.IsEnabled)
                Invoke-Element $understandButton
                $closed = $false
                $closeDeadline = (Get-Date).AddSeconds(4)
                while ((Get-Date) -lt $closeDeadline) {
                    Start-Sleep -Milliseconds 200
                    if ((Get-WindowByTitle "IPv6 入站自检") -eq $null) { $closed = $true; break }
                }
                Check "点击「了解」后弹窗关闭" $closed
            }
        }

        Start-Sleep -Milliseconds 400
        $runningTexts = Get-WindowTexts $window
        $runningJoined = $runningTexts -join "`n"
        Check "入站自检启动后显示等待连接" ($runningJoined -match "正在等待外部连接")
        $urlText = ($runningTexts | Where-Object { $_ -match '^http://\[' } | Select-Object -First 1)
        Check "生成 IPv6 自测地址" (-not [string]::IsNullOrEmpty($urlText))

        $stopCondition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, "停止")
        $stopButton = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $stopCondition)
        if ($stopButton -ne $null) {
            Invoke-Element $stopButton
            Start-Sleep -Milliseconds 700
            $stoppedJoined = (Get-WindowTexts $window) -join "`n"
            Check "停止后恢复初始状态" ($stoppedJoined -match "尚未启动自检")
        } else {
            Check "停止后恢复初始状态" $false "没有找到停止按钮"
        }

        # 第二次启动，关闭弹窗后从回环地址连接，验证成功路径
        Invoke-Element $startButton
        Start-Sleep -Milliseconds 700
        $dialog2 = Get-WindowByTitle "IPv6 入站自检"
        if ($dialog2 -ne $null) {
            Start-Sleep -Milliseconds 3200
            $understand2 = $dialog2.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                (New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::NameProperty, "了解")))
            if ($understand2 -ne $null -and $understand2.Current.IsEnabled) {
                Invoke-Element $understand2
            }
            Start-Sleep -Milliseconds 400
        }

        $secondTexts = Get-WindowTexts $window
        $secondUrl = ($secondTexts | Where-Object { $_ -match '^http://\[' } | Select-Object -First 1)
        $port = 0
        if (-not [string]::IsNullOrEmpty($secondUrl) -and $secondUrl -match ':(\d+)/') {
            $port = [int]$Matches[1]
        }
        try {
            if ($port -le 0) { throw "未解析到第二次自检端口" }
            $client = New-Object System.Net.Sockets.TcpClient([System.Net.Sockets.AddressFamily]::InterNetworkV6)
            $client.Connect([System.Net.IPAddress]::IPv6Loopback, $port)
            Start-Sleep -Milliseconds 1000
            $client.Close()
            Start-Sleep -Milliseconds 700
            $successJoined = (Get-WindowTexts $window) -join "`n"
            Check "收到入站连接后显示成功" ($successJoined -match "检测成功")
        } catch {
            Check "收到入站连接后显示成功" $false $_.Exception.Message
        }
    } else {
        Write-Host "  [SKIP] 当前没有可用的公网 IPv6，跳过入站自检 UI 测试" -ForegroundColor Yellow
    }
} finally {
    if ($originalClipboard -ne $null) {
        try { [System.Windows.Forms.Clipboard]::SetText($originalClipboard) } catch { }
    }
    if ($null -eq $originalLoopbackEnv) {
        Remove-Item Env:\IPINFO_INBOUND_BIND_LOOPBACK -ErrorAction SilentlyContinue
    } else {
        $env:IPINFO_INBOUND_BIND_LOOPBACK = $originalLoopbackEnv
    }
    if ($process -ne $null -and -not $process.HasExited) {
        try { $process.Kill() } catch { }
    }
}

Write-Host ""
Write-Host "--------------------------------------------"
Write-Host ("UI 冒烟测试：共检查 {0} 项，失败 {1} 项。" -f $checks, $failures)
Write-Host "--------------------------------------------"
exit $(if ($failures -eq 0) { 0 } else { 1 })
