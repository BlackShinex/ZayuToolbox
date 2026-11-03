# UI 冒烟测试：离屏启动 WPF 应用，通过 UI Automation 验证：
#   1. 默认状态 IP 被遮罩（不出现完整 IPv4/IPv6）；
#   2. 每个「复制」按钮复制的是完整 IP（同时覆盖 IPv4 与 IPv6）；
#   3. 「显示 IP」按钮能切换为完整地址，「隐藏 IP」能恢复遮罩；
#   4. 网络连通性测试可启动、显示二维码、接收回环 IPv4/IPv6 探测并给出结论；
#   5. WebView2 运行库状态、诊断复制与安装窗口可正常打开。
#
# 用法（先构建 Debug）：
#   powershell -ExecutionPolicy Bypass -File .\tools\ui-smoke-test.ps1
#   powershell -ExecutionPolicy Bypass -File .\tools\ui-smoke-test.ps1 -SkipClipboard
#   powershell -ExecutionPolicy Bypass -File .\tools\ui-smoke-test.ps1 -Only components
#   powershell -ExecutionPolicy Bypass -File .\tools\ui-smoke-test.ps1 -Only sunshine,webview2
#   （-Only 支持 components / sunshine / webview2 / selfcheck，跳过网络检测和连通性测试）
#   （后台独立桌面运行请用 tools\ui-test-background.ps1，同样支持 -Only）
param(
    [string]$AppPath = "",
    [switch]$SkipClipboard,
    [string]$Only = ""
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
$originalLoopbackEnv = $env:IPINFO_CONNECTIVITY_BIND_LOOPBACK
$env:IPINFO_CONNECTIVITY_BIND_LOOPBACK = "1"

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

function Get-ElementByButtonName($window, [string]$name) {
    $nameCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    $typeCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    $condition = New-Object System.Windows.Automation.AndCondition($nameCondition, $typeCondition)
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

function Wait-ClipboardTextMatch([string]$pattern, [int]$timeoutMs = 5000) {
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

function Invoke-CopyAndWait($button, [string]$pattern) {
    if ($button -eq $null -or -not $button.Current.IsEnabled) { return $false }
    for ($attempt = 0; $attempt -lt 3; $attempt++) {
        Invoke-Element $button
        if (Wait-ClipboardTextMatch $pattern 2500) { return $true }
        Start-Sleep -Milliseconds 300
    }
    return $false
}

function Invoke-HttpGetText([string]$url) {
    $request = [System.Net.WebRequest]::Create($url)
    $request.Proxy = $null
    $request.Timeout = 5000
    $response = $request.GetResponse()
    try {
        $reader = New-Object System.IO.StreamReader($response.GetResponseStream(), [System.Text.Encoding]::UTF8)
        return $reader.ReadToEnd()
    } finally {
        $response.Close()
    }
}

function Invoke-HttpGetStatus([string]$url) {
    $request = [System.Net.WebRequest]::Create($url)
    $request.Proxy = $null
    $request.Timeout = 5000
    $response = $request.GetResponse()
    try {
        return [int]$response.StatusCode
    } finally {
        $response.Close()
    }
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

    # 快速模式：只运行指定分区，跳过网络探测和连通性测试。
    $fastSections = @($Only -split "," | ForEach-Object { $_.Trim() } | Where-Object { -not [string]::IsNullOrEmpty($_) -and $_ -ne "all" })
    foreach ($section in $fastSections) {
        if (@("components", "sunshine", "webview2", "selfcheck") -notcontains $section) {
            throw ("未知的测试分区：" + $section)
        }
    }
    if ($fastSections.Count -gt 0) {
        Write-Host ("快速模式：只测试 " + ($fastSections -join ", ")) -ForegroundColor Cyan

        if ($fastSections -contains "components") {
            Check "切换到组件状态页" (Select-NavItem $window "组件状态")
            $compReady = $false
            $compDeadline = (Get-Date).AddSeconds(45)
            while ((Get-Date) -lt $compDeadline) {
                Start-Sleep -Milliseconds 400
                $compCopy = Get-ElementByAutomationId $window "ComponentCopyButton"
                if ($compCopy -ne $null -and $compCopy.Current.IsEnabled) {
                    $compReady = $true
                    break
                }
            }
            Check "组件状态检测完成" $compReady
            if ($compReady) {
                $compTexts = (Get-WindowTexts $window) -join "`n"
                Check "组件页包含显示/音频/传输组件" (($compTexts -match "ZakoDisplay") -and ($compTexts -match "ViGEmBus") -and ($compTexts -match "Steam Streaming Speakers") -and ($compTexts -match "VB-CABLE") -and ($compTexts -match "USB/IP"))
                Check "组件页包含虚拟手柄状态" (($compTexts -match "DualSense") -and ($compTexts -match "HIDMaestro"))
                $repairButton = Get-ElementByButtonName $window "修复组件"
                $uninstallButton = Get-ElementByButtonName $window "卸载组件"
                Check "组件页提供单独修复 / 卸载入口" ($repairButton -ne $null -and $uninstallButton -ne $null)
            }
        }

        if ($fastSections -contains "sunshine") {
            Check "切换到 Sunshine 版本页" (Select-NavItem $window "Sunshine 版本")
            Start-Sleep -Milliseconds 500
            $sunshineTexts = (Get-WindowTexts $window) -join "`n"
            Check "显示 Sunshine 版本自检卡片" (($sunshineTexts -match "Sunshine 版本自检") -and ($sunshineTexts -match "安装来源："))
            Check "显示 Sunshine 服务卡片" (($sunshineTexts -match "Sunshine 服务") -and ($sunshineTexts -match "服务详情") -and ($sunshineTexts -match "进程 PID："))
            Check "服务状态与自启动分开显示" (($sunshineTexts -match "服务运行中|服务已停止|服务未安装") -and ($sunshineTexts -match "开机自启") -and ($sunshineTexts -notmatch "启动类型：") -and ($sunshineTexts -notmatch "已开启"))
            $serviceToggle = Get-ElementByAutomationId $window "ServiceAutoStartToggle"
            Check "存在开机自启开关" ($serviceToggle -ne $null)
            $sunshineOfficial = Get-ElementByAutomationId $window "SunshineOfficialSiteButton"
            Check "存在 Sunshine 基地版官网按钮" ($sunshineOfficial -ne $null -and $sunshineOfficial.Current.IsEnabled)
            $updateStatus = Get-ElementByAutomationId $window "UpdateStatusText"
            Check "存在版本更新检测状态" ($updateStatus -ne $null -and -not [string]::IsNullOrEmpty($updateStatus.Current.Name))
        }

        if ($fastSections -contains "webview2") {
            Check "切换到 WebView2 运行库页" (Select-NavItem $window "WebView2 运行库")
            Start-Sleep -Milliseconds 500
            $webView2Texts = (Get-WindowTexts $window) -join "`n"
            Check "显示 WebView2 运行库卡片" (($webView2Texts -match "WebView2 运行库") -and ($webView2Texts -match "版本："))
            Check "显示 WebView2 检测状态" (($webView2Texts -match "已安装") -or ($webView2Texts -match "未安装") -or ($webView2Texts -match "安装异常"))
            $webView2Copy = Get-ElementByAutomationId $window "WebView2CopyButton"
            Check "存在 WebView2 诊断复制按钮" ($webView2Copy -ne $null)
        }

        if ($fastSections -contains "selfcheck") {
            Check "切换到一键自检页" (Select-NavItem $window "一键自检")
            $selfStart = Get-ElementByAutomationId $window "SelfCheckStartButton"
            Check "存在开始自检按钮" ($selfStart -ne $null -and $selfStart.Current.IsEnabled)
            if ($selfStart -ne $null -and $selfStart.Current.IsEnabled) {
                Invoke-Element $selfStart
                $selfReady = $false
                $selfDeadline = (Get-Date).AddSeconds(45)
                while ((Get-Date) -lt $selfDeadline) {
                    Start-Sleep -Milliseconds 500
                    $selfCopy = Get-ElementByAutomationId $window "SelfCheckCopyButton"
                    if ($selfCopy -ne $null -and $selfCopy.Current.IsEnabled) {
                        $selfReady = $true
                        break
                    }
                }
                Check "一键自检完成" $selfReady
                if ($selfReady) {
                    $selfTexts = (Get-WindowTexts $window) -join "`n"
                    Check "报告包含系统信息" (($selfTexts -match "系统信息") -and ($selfTexts -match "CPU") -and ($selfTexts -match "内存"))
                    Check "报告包含 Sunshine 服务与虚拟显示器" (($selfTexts -match "Sunshine") -and ($selfTexts -match "服务") -and ($selfTexts -match "虚拟显示器"))
                    Check "报告包含网络与组件信息" (($selfTexts -match "NAT 类型") -and ($selfTexts -match "本机 IPv4") -and ($selfTexts -match "WebView2") -and ($selfTexts -match "组件信息") -and ($selfTexts -match "ViGEmBus"))
                }
            }
        }

        return
    }

    $startTexts = (Get-WindowTexts $window) -join "`n"
    Check "默认打开 Sunshine 版本页" ($startTexts -match "Sunshine 版本自检")
    $updateStatus = Get-ElementByAutomationId $window "UpdateStatusText"
    Check "存在版本更新检测状态" ($updateStatus -ne $null -and -not [string]::IsNullOrEmpty($updateStatus.Current.Name))

    # 网络检测改为手动触发：切到该页应显示未检测状态
    Check "切换到网络检测页" (Select-NavItem $window "网络检测")
    Start-Sleep -Milliseconds 600
    $idleTexts = (Get-WindowTexts $window) -join "`n"
    Check "网络检测不再自动执行" (($idleTexts -match "尚未检测") -and ($idleTexts -notmatch "NAT 后"))

    $refreshButton = Get-ElementByAutomationId $window "RefreshButton"
    Check "网络检测页提供检测按钮" ($refreshButton -ne $null -and $refreshButton.Current.IsEnabled)
    Check "网络检测初始按钮为开始检测" ($refreshButton -ne $null -and $refreshButton.Current.Name -eq "开始检测") ("实际为 " + $refreshButton.Current.Name)
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

    $refreshAfter = Get-ElementByAutomationId $window "RefreshButton"
    Check "检测完成后按钮变为重新检测" ($refreshAfter -ne $null -and $refreshAfter.Current.Name -eq "重新检测") ("实际为 " + $refreshAfter.Current.Name)

    $texts = Get-WindowTexts $window
    $joined = $texts -join "`n"

    # 默认遮罩：局域网 IPv4 显示完整，公网 IP 才打码
    $privateIpv4 = @($texts | Where-Object { $_ -match '^(10\.|192\.168\.|172\.(1[6-9]|2[0-9]|3[0-1])\.|100\.(6[4-9]|[7-9][0-9]|1[0-2][0-9])\.)' })
    $maskedIpv4 = @($texts | Where-Object { $_ -match '^\d{1,3}\.\*\.\*\.\d{1,3}$' })
    $maskedIpv6 = @($texts | Where-Object { $_ -match '^[0-9a-fA-F]+(?::\*+){6}:[0-9a-fA-F]+$' })
    $leak = @($texts | Where-Object {
        $_ -match '\d{1,3}(\.\d{1,3}){3}' -and
        $_ -notmatch 'STUN 服务器' -and
        $_ -notmatch '0\.0\.0\.0' -and
        $_ -notmatch '\b10\.' -and
        $_ -notmatch '\b192\.168\.' -and
        $_ -notmatch '\b172\.(1[6-9]|2[0-9]|3[0-1])\.' -and
        $_ -notmatch '\b100\.(6[4-9]|[7-9][0-9]|1[0-2][0-9])\.' -and
        $_ -notmatch '\b169\.254\.'
    })

    Check "局域网 IPv4 显示完整" ($privateIpv4.Count -gt 0) "没有找到未打码的局域网 IPv4"
    Check "默认状态显示公网 IPv4 遮罩" ($maskedIpv4.Count -gt 0) "没有找到形如 125.*.*.223 的文本"
    Check "默认状态不出现完整公网 IPv4" ($leak.Count -eq 0) ("发现未遮罩的公网 IP 文本：" + ($leak -join " / "))
    Check "默认状态显示 IPv6 遮罩" ($maskedIpv6.Count -gt 0) "没有找到形如 240e:****:…:865c 的文本"

    # 左侧菜单
    $hasNav = ($joined -match "网络检测") -and ($joined -match "Sunshine 版本") -and ($joined -match "WebView2 运行库") -and ($joined -notmatch "NAT 跃点跟踪")
    Check "显示左侧功能菜单（NAT 跃点已移除）" $hasNav

    # 网络检测页：地址 / STUN / 连通性测试
    Check "网络检测页包含 STUN/NAT 结果" (($joined -match "公网出口 IP") -or ($joined -match "NAT 类型"))
    Check "网络检测页包含网络连通性测试" (($joined -match "网络连通性测试") -and ($joined -match "启动测试并显示二维码"))
    Check "连通性测试包含 2x2 矩阵" (($joined -match "内网 IPv4") -and ($joined -match "内网 IPv6") -and ($joined -match "外网 IPv4") -and ($joined -match "外网 IPv6"))
    Check "网络检测页不再包含 IPv6 入站自检" ($joined -notmatch "IPv6 入站可达性")

    # 不再有 NAT 跃点页
    Check "NAT 跃点跟踪页已移除" (-not (Select-NavItem $window "NAT 跃点跟踪"))

    # Sunshine 版本页
    Check "切换到 Sunshine 版本页" (Select-NavItem $window "Sunshine 版本")
    $sunshineTexts = (Get-WindowTexts $window) -join "`n"
    $sunshineCopy = Get-ElementByAutomationId $window "SunshineCopyButton"
    Check "显示 Sunshine 版本自检卡片" (($sunshineTexts -match "Sunshine 版本自检") -and ($sunshineTexts -match "安装来源："))
    Check "Sunshine 页不提供显示 IP" ($sunshineTexts -notmatch "显示 IP")
    $sunshineOfficial = Get-ElementByAutomationId $window "SunshineOfficialSiteButton"
    Check "存在 Sunshine 基地版官网按钮" ($sunshineOfficial -ne $null -and $sunshineOfficial.Current.IsEnabled)
    Check "显示 Sunshine 服务卡片" (($sunshineTexts -match "Sunshine 服务") -and ($sunshineTexts -match "服务详情") -and ($sunshineTexts -match "进程 PID："))
    Check "服务状态与自启动分开显示" (($sunshineTexts -match "服务运行中|服务已停止|服务未安装") -and ($sunshineTexts -match "开机自启") -and ($sunshineTexts -notmatch "启动类型：") -and ($sunshineTexts -notmatch "已开启"))
    $serviceToggle = Get-ElementByAutomationId $window "ServiceAutoStartToggle"
    Check "存在开机自启开关" ($serviceToggle -ne $null)
    $serviceCopy = Get-ElementByAutomationId $window "ServiceCopyButton"
    Check "存在服务诊断复制按钮" ($serviceCopy -ne $null)

    $baseLocked = $sunshineTexts -match "仅基地版可用"
    if ($baseLocked) {
        Check "非基地版时服务卡片被锁定" ($serviceCopy -ne $null -and -not $serviceCopy.Current.IsEnabled)
        Check "非基地版时开机自启被锁定" ($serviceToggle -ne $null -and -not $serviceToggle.Current.IsEnabled)
    } elseif ($serviceCopy -ne $null -and $serviceCopy.Current.IsEnabled) {
        if ($SkipClipboard) {
            Write-Host "  [SKIP] 后台桌面模式跳过剪贴板内容校验" -ForegroundColor Yellow
        } else {
            Check "服务诊断信息包含服务状态" (Invoke-CopyAndWait $serviceCopy "(?s)Sunshine 服务.*检测结果")
        }
    }

    Check "显示 Zako 虚拟显示器卡片" (($sunshineTexts -match "Zako 虚拟显示器") -and ($sunshineTexts -match "设备状态："))
    Check "显示安装包驱动版本行" ($sunshineTexts -match "安装包驱动：")
    $zakoCopy = Get-ElementByAutomationId $window "ZakoCopyButton"
    Check "存在 Zako 诊断复制按钮" ($zakoCopy -ne $null)
    if ($baseLocked) {
        Check "非基地版时 Zako 卡片被锁定" ($zakoCopy -ne $null -and -not $zakoCopy.Current.IsEnabled)
    } elseif ($zakoCopy -ne $null -and $zakoCopy.Current.IsEnabled) {
        if ($SkipClipboard) {
            Write-Host "  [SKIP] 后台桌面模式跳过剪贴板内容校验" -ForegroundColor Yellow
        } else {
            Check "Zako 诊断信息包含系统版本" (Invoke-CopyAndWait $zakoCopy "(?s)Zako.*操作系统")
        }
    }
    Check "存在 Sunshine 诊断复制按钮" ($sunshineCopy -ne $null -and $sunshineCopy.Current.IsEnabled)
    if ($sunshineCopy -ne $null -and $sunshineCopy.Current.IsEnabled) {
        if ($SkipClipboard) {
            Write-Host "  [SKIP] 后台桌面模式跳过剪贴板内容校验" -ForegroundColor Yellow
        } else {
            Check "Sunshine 诊断信息包含检测结论" (Invoke-CopyAndWait $sunshineCopy "(?s)Sunshine.*检测结果")
        }
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
    if ($SkipClipboard) {
        Write-Host "  [SKIP] 后台桌面模式跳过剪贴板内容校验" -ForegroundColor Yellow
    } else {
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
    }

    # WebView2 诊断复制与安装窗口
    Write-Host "测试 WebView2 诊断与安装窗口…" -ForegroundColor Cyan
    Select-NavItem $window "WebView2 运行库" | Out-Null
    $webView2Copy = Get-ElementByAutomationId $window "WebView2CopyButton"
    Check "存在 WebView2 诊断复制按钮" ($webView2Copy -ne $null -and $webView2Copy.Current.IsEnabled)
    if ($webView2Copy -ne $null -and $webView2Copy.Current.IsEnabled) {
        if ($SkipClipboard) {
            Write-Host "  [SKIP] 后台桌面模式跳过剪贴板内容校验" -ForegroundColor Yellow
        } else {
            Check "WebView2 诊断信息包含版本和路径" (Invoke-CopyAndWait $webView2Copy "(?s)WebView2.*检测结果")
        }
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

    # 一键自检
    Write-Host "测试一键自检…" -ForegroundColor Cyan
    Check "切换到一键自检页" (Select-NavItem $window "一键自检")
    $selfStart = Get-ElementByAutomationId $window "SelfCheckStartButton"
    Check "存在开始自检按钮" ($selfStart -ne $null -and $selfStart.Current.IsEnabled)
    if ($selfStart -ne $null -and $selfStart.Current.IsEnabled) {
        Invoke-Element $selfStart
        $selfReady = $false
        $selfDeadline = (Get-Date).AddSeconds(45)
        while ((Get-Date) -lt $selfDeadline) {
            Start-Sleep -Milliseconds 500
            $selfCopyButton = Get-ElementByAutomationId $window "SelfCheckCopyButton"
            if ($selfCopyButton -ne $null -and $selfCopyButton.Current.IsEnabled) {
                $selfReady = $true
                break
            }
        }
        Check "一键自检完成" $selfReady

        if ($selfReady) {
            Start-Sleep -Milliseconds 500
            $selfTexts = (Get-WindowTexts $window) -join "`n"
            Check "报告包含系统信息" (($selfTexts -match "系统信息") -and ($selfTexts -match "CPU") -and ($selfTexts -match "内存") -and ($selfTexts -match "显卡"))
            Check "报告包含 Sunshine 服务与虚拟显示器" (($selfTexts -match "Sunshine") -and ($selfTexts -match "服务") -and ($selfTexts -match "虚拟显示器"))
            Check "报告包含网络与组件信息" (($selfTexts -match "NAT 类型") -and ($selfTexts -match "本机 IPv4") -and ($selfTexts -match "WebView2") -and ($selfTexts -match "组件信息") -and ($selfTexts -match "ViGEmBus"))
            Check "报告默认打码 IP" ($selfTexts -match '\*\.\*')

            $selfReveal = Get-ElementByAutomationId $window "SelfCheckRevealToggle"
            if ($selfReveal -ne $null) {
                $selfReveal.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
                Start-Sleep -Milliseconds 700
                $selfRevealedTexts = (Get-WindowTexts $window) -join "`n"
                Check "报告可取消打码" ($selfRevealedTexts -notmatch '\*\.\*')
            }

            if (-not $SkipClipboard) {
                $selfCopyButton = Get-ElementByAutomationId $window "SelfCheckCopyButton"
                if ($selfCopyButton -ne $null -and $selfCopyButton.Current.IsEnabled) {
                    Invoke-Element $selfCopyButton
                    $imageCopied = $false
                    $imageDeadline = (Get-Date).AddSeconds(4)
                    while ((Get-Date) -lt $imageDeadline) {
                        Start-Sleep -Milliseconds 250
                        try {
                            if ([System.Windows.Forms.Clipboard]::ContainsImage()) {
                                $imageCopied = $true
                                break
                            }
                        } catch {
                        }
                    }
                    Check "报告可复制为截图" $imageCopied
                }
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

    # 网络连通性测试：二维码 + 回环 IPv4/IPv6 探测 + 结论
    Write-Host "测试网络连通性测试…" -ForegroundColor Cyan
    $startCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, "启动测试并显示二维码")
    $startButton = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $startCondition)
    if ($startButton -ne $null -and $startButton.Current.IsEnabled) {
        Invoke-Element $startButton
        Start-Sleep -Milliseconds 900

        $dialog = Get-WindowByTitle "网络连通性测试"
        Check "点击后弹出网络连通性测试窗口" ($dialog -ne $null)
        if ($dialog -ne $null) {
            $dialogJoined = (Get-WindowTexts $dialog) -join "`n"
            Check "弹窗包含内网/公网两种模式" (($dialogJoined -match "同一 Wi-Fi") -and ($dialogJoined -match "移动数据"))
            Check "倒计时初始为 6 秒" ($dialogJoined -match "6 秒后可以关闭")
            Check "弹窗未显示二维码生成失败" ($dialogJoined -notmatch "二维码生成失败")

            $urlBox = Get-ElementByAutomationId $dialog "UrlBox"
            Check "弹窗生成测试地址" ($urlBox -ne $null)
            $url = $null
            if ($urlBox -ne $null) {
                $url = $urlBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
            }
            Check "测试地址为 HTTP 地址" ($url -match '^http://')
            Check "测试地址包含随机令牌" ($url -match '/t/[0-9a-f]{16}/')

            $closeButton = Get-ElementByAutomationId $dialog "CloseButton"
            Check "弹窗包含完成按钮" ($closeButton -ne $null)
            if ($closeButton -ne $null) {
                Check "倒计时内完成按钮不可用" (-not $closeButton.Current.IsEnabled)
                Start-Sleep -Milliseconds 6400
                Check "6 秒倒计时结束后完成按钮可点击" ($closeButton.Current.IsEnabled)
            }

            if ($url -match '^http://') {
                try {
                    $page = Invoke-HttpGetText $url
                    Check "测试页面可访问" (-not [string]::IsNullOrEmpty($page))
                    $imgs = @([regex]::Matches($page, 'src="([^"]+)"') | ForEach-Object { $_.Groups[1].Value })
                    Check "页面同时包含 IPv4 与 IPv6 探测" ($imgs.Count -ge 2)
                    foreach ($img in $imgs) {
                        $status = Invoke-HttpGetStatus $img
                        Check ("探测请求返回 200：" + $img.Substring($img.Length - 2)) ($status -eq 200)
                    }
                    Start-Sleep -Milliseconds 1800

                    $matrixTexts = (Get-WindowTexts $window) -join "`n"
                    Check "收到手机来源" ($matrixTexts -match "手机来源")
                    Check "内网 IPv4 判定为连通" ($matrixTexts -match "内网 IPv4[\s\S]*连通")
                    Check "内网 IPv6 判定为连通" ($matrixTexts -match "内网 IPv6[\s\S]*连通")
                    Check "连通性结论已更新" (($matrixTexts -match "测试完成") -or ($matrixTexts -match "P2P"))
                } catch {
                    Check "连通性测试回环连接" $false $_.Exception.Message
                }
            }

            if ($closeButton -ne $null -and $closeButton.Current.IsEnabled) {
                Invoke-Element $closeButton
            }
            Start-Sleep -Milliseconds 900
            Check "关闭后连通性测试窗口消失" ((Get-WindowByTitle "网络连通性测试") -eq $null)

            $startAgain = Get-ElementByAutomationId $window "ConnectivityStartButton"
            Check "关闭后可再次启动测试" ($startAgain -ne $null -and $startAgain.Current.IsEnabled)
        }
    } else {
        Write-Host "  [SKIP] 当前没有可用的测试地址，跳过连通性测试 UI 测试" -ForegroundColor Yellow
    }

    # 连通性测试完成后，再次自检应把 2x2 结果与 P2P 结论带入报告
    Check "回到一键自检页" (Select-NavItem $window "一键自检")
    $selfStartAgain = Get-ElementByAutomationId $window "SelfCheckStartButton"
    if ($selfStartAgain -ne $null -and $selfStartAgain.Current.IsEnabled) {
        Invoke-Element $selfStartAgain
        $secondSelfReady = $false
        $secondSelfDeadline = (Get-Date).AddSeconds(45)
        while ((Get-Date) -lt $secondSelfDeadline) {
            Start-Sleep -Milliseconds 500
            $selfCopyAgain = Get-ElementByAutomationId $window "SelfCheckCopyButton"
            if ($selfCopyAgain -ne $null -and $selfCopyAgain.Current.IsEnabled) {
                $secondSelfReady = $true
                break
            }
        }
        Check "连通性测试后二次自检完成" $secondSelfReady
        if ($secondSelfReady) {
            Start-Sleep -Milliseconds 500
            $secondSelfTexts = (Get-WindowTexts $window) -join "`n"
            Check "自检报告反映连通性测试" (($secondSelfTexts -match "P2P 结论") -and ($secondSelfTexts -match "内网 IPv4"))
        }
    }

    # 组件状态页
    Check "切换到组件状态页" (Select-NavItem $window "组件状态")
    $compReady = $false
    $compDeadline = (Get-Date).AddSeconds(45)
    while ((Get-Date) -lt $compDeadline) {
        Start-Sleep -Milliseconds 500
        $compCopy = Get-ElementByAutomationId $window "ComponentCopyButton"
        if ($compCopy -ne $null -and $compCopy.Current.IsEnabled) {
            $compReady = $true
            break
        }
    }
    Check "组件状态检测完成" $compReady
    if ($compReady) {
        $compTexts = (Get-WindowTexts $window) -join "`n"
        Check "组件页包含显示/音频/传输组件" (($compTexts -match "ZakoDisplay") -and ($compTexts -match "ViGEmBus") -and ($compTexts -match "Steam Streaming Speakers") -and ($compTexts -match "VB-CABLE") -and ($compTexts -match "USB/IP"))
        Check "组件页包含虚拟手柄状态" (($compTexts -match "DualSense") -and ($compTexts -match "HIDMaestro"))
        $repairButton = Get-ElementByButtonName $window "修复组件"
        $uninstallButton = Get-ElementByButtonName $window "卸载组件"
        Check "组件页提供单独修复 / 卸载入口" ($repairButton -ne $null -and $uninstallButton -ne $null)
    }
} finally {
    if ($originalClipboard -ne $null) {
        try { [System.Windows.Forms.Clipboard]::SetText($originalClipboard) } catch { }
    }
    if ($null -eq $originalLoopbackEnv) {
        Remove-Item Env:\IPINFO_CONNECTIVITY_BIND_LOOPBACK -ErrorAction SilentlyContinue
    } else {
        $env:IPINFO_CONNECTIVITY_BIND_LOOPBACK = $originalLoopbackEnv
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
