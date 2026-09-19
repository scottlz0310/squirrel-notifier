<#
.SYNOPSIS
    対話ログオン済み Windows runner で Squirrel Notifier の実デスクトップ E2E を実行します。

.DESCRIPTION
    DesktopSmoke は、専用 runner の事前状態を確認したうえで MSI の install、WinUI 起動、
    UI Automation、スクリーンショット、uninstall、残留確認を行います。DesktopFull は、
    sandbox の外部 stack が準備済みであることを検証してから、別途登録した driver を呼び出します。
    URL、token、settings の内容は artifact へ出力しません。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$MsiPath,

    [ValidateSet('DesktopSmoke', 'DesktopFull')]
    [string]$Scenario = 'DesktopSmoke',

    [string]$ArtifactsDirectory = (Join-Path (Get-Location) 'artifacts\e2e-local'),

    [string]$ExpectedVersion,

    [int]$TimeoutSeconds = 60
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$resolvedMsiPath = (Resolve-Path -LiteralPath $MsiPath).Path
$artifactRoot = [System.IO.Path]::GetFullPath($ArtifactsDirectory)
$runId = "{0}-{1}" -f (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ'), ([guid]::NewGuid().ToString('N').Substring(0, 8))
$scenarioArtifactDirectory = Join-Path $artifactRoot "$runId-$($Scenario.ToLowerInvariant())"
$runnerTemp = if ([string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) {
    [System.IO.Path]::GetTempPath()
}
else {
    $env:RUNNER_TEMP
}
$runRoot = Join-Path $runnerTemp "squirrel-notifier-e2e\desktop-$runId"
$installLogPath = Join-Path $runRoot 'msiexec-install.log'
$uninstallLogPath = Join-Path $runRoot 'msiexec-uninstall.log'
$installedDirectory = Join-Path $env:LOCALAPPDATA 'Programs\SquirrelNotifier'
$installedExecutable = Join-Path $installedDirectory 'SquirrelNotifier.WinUI3.exe'
$settingsDirectory = Join-Path $env:LOCALAPPDATA 'SquirrelNotifier'
$taskName = 'Squirrel Notifier'
$applicationProcessName = 'SquirrelNotifier.WinUI3'
$applicationProcessId = $null
$installStarted = $false
$script:runnerWasDirty = $false
$settingsExistedBefore = Test-Path -LiteralPath $settingsDirectory -PathType Container
$startedAt = [DateTimeOffset]::UtcNow
$logLines = [System.Collections.Generic.List[string]]::new()
$failure = $null
$cleanupErrors = [System.Collections.Generic.List[string]]::new()
$windowHandle = [IntPtr]::Zero

function Write-ScenarioLog {
    param(
        [Parameter(Mandatory)]
        [string]$Message
    )

    $line = "{0} {1}" -f [DateTimeOffset]::UtcNow.ToString('O'), $Message
    $logLines.Add($line)
    Write-Host $Message
}

function Write-JsonFile {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [object]$Value
    )

    $Value | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $Path -Encoding utf8
}

function Get-SafeMessage {
    param(
        [Parameter(Mandatory)]
        [System.Exception]$Exception
    )

    $message = $Exception.Message
    $message = [regex]::Replace($message, '(?i)Bearer\s+[A-Za-z0-9._-]{12,}', 'Bearer <redacted>')
    $message = [regex]::Replace($message, '(?i)\b(?:gh[pousr]_|github_pat_|sk-)[A-Za-z0-9_]{12,}\b', '<redacted>')
    return $message
}

function Invoke-Msi {
    param(
        [Parameter(Mandatory)]
        [ValidateSet('Install', 'Uninstall')]
        [string]$Operation,

        [Parameter(Mandatory)]
        [string]$LogPath
    )

    $arguments = if ($Operation -eq 'Install') {
        @('/i', ('"{0}"' -f $resolvedMsiPath), '/qn', '/norestart', '/L*v', ('"{0}"' -f $LogPath))
    }
    else {
        @('/x', ('"{0}"' -f $resolvedMsiPath), '/qn', '/norestart', '/L*v', ('"{0}"' -f $LogPath))
    }

    $process = Start-Process -FilePath (Join-Path $env:WINDIR 'System32\msiexec.exe') -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden
    if ($process.ExitCode -ne 0) {
        throw "MSI $Operation が終了コード $($process.ExitCode) で失敗しました。"
    }
}

function Wait-InstalledExecutable {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $installedExecutable -PathType Leaf) {
            return
        }
        Start-Sleep -Milliseconds 500
    }

    throw "インストール後の実行ファイルが見つかりません: $installedExecutable"
}

function Assert-InstalledVersion {
    if ([string]::IsNullOrWhiteSpace($ExpectedVersion)) {
        return
    }

    $versionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($installedExecutable)
    $actualVersion = $versionInfo.ProductVersion
    if ([string]::IsNullOrWhiteSpace($actualVersion) -or -not $actualVersion.StartsWith($ExpectedVersion, [StringComparison]::Ordinal)) {
        throw "インストールされた製品 version が期待値と異なります。期待値=$ExpectedVersion"
    }
}

function Ensure-WindowStateType {
    if ($null -ne ('DesktopE2E.WindowState' -as [type])) {
        return
    }

    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace DesktopE2E
{
    public static class WindowState
    {
        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int command);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);
    }
}
'@
}

function Wait-MainWindow {
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Process]$Process
    )

    Ensure-WindowStateType
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $Process.Refresh()
        if ($Process.HasExited) {
            throw "アプリケーションが main window 作成前に終了しました。終了コード=$($Process.ExitCode)"
        }

        $handle = $Process.MainWindowHandle
        if ($handle -ne [IntPtr]::Zero) {
            [DesktopE2E.WindowState]::ShowWindow($handle, 5) | Out-Null
            if ([DesktopE2E.WindowState]::IsWindowVisible($handle) -and -not [DesktopE2E.WindowState]::IsIconic($handle)) {
                [DesktopE2E.WindowState]::SetForegroundWindow($handle) | Out-Null
                Start-Sleep -Milliseconds 500
                return $handle
            }
        }
        Start-Sleep -Milliseconds 500
    }

    throw 'アプリケーションの main window が timeout 内に作成されませんでした。'
}

function Ensure-WindowCaptureType {
    if ($null -ne ('DesktopE2E.WindowCapture' -as [type])) {
        return
    }

    Add-Type -AssemblyName System.Drawing.Common
    $drawingAssembly = [System.Drawing.Bitmap].Assembly
    $referenceAssemblies = [System.Collections.Generic.List[string]]::new()
    $referenceAssemblies.Add($drawingAssembly.Location)
    foreach ($referenceName in $drawingAssembly.GetReferencedAssemblies()) {
        $referenceAssembly = [System.Reflection.Assembly]::Load($referenceName)
        if (-not [string]::IsNullOrWhiteSpace($referenceAssembly.Location)) {
            $referenceAssemblies.Add($referenceAssembly.Location)
        }
    }

    Add-Type -ReferencedAssemblies $referenceAssemblies.ToArray() -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace DesktopE2E
{
    public static class WindowCapture
    {
        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        public static void Capture(IntPtr hWnd, string path)
        {
            if (!GetWindowRect(hWnd, out RECT rect))
            {
                throw new InvalidOperationException("window rect を取得できません。");
            }

            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0)
            {
                throw new InvalidOperationException("window のサイズが不正です。");
            }

            using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, bitmap.Size, CopyPixelOperation.SourceCopy);
            bitmap.Save(path, ImageFormat.Png);
        }
    }
}
'@
}

function Capture-Window {
    param(
        [Parameter(Mandatory)]
        [IntPtr]$Handle,

        [Parameter(Mandatory)]
        [string]$Path
    )

    Ensure-WindowCaptureType
    [DesktopE2E.WindowCapture]::Capture($Handle, $Path)
}

function Get-UiElementByAutomationId {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$Root,

        [Parameter(Mandatory)]
        [string]$AutomationId
    )

    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId)
    return $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Wait-UiElementByAutomationId {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$Root,

        [Parameter(Mandatory)]
        [string]$AutomationId
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $element = Get-UiElementByAutomationId -Root $Root -AutomationId $AutomationId
        if ($null -ne $element) {
            return $element
        }
        Start-Sleep -Milliseconds 200
    }

    throw "UI Automation element が見つかりません: $AutomationId"
}

function Assert-UiContract {
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes

    $root = [System.Windows.Automation.AutomationElement]::FromHandle($windowHandle)
    if ($null -eq $root) {
        throw 'UI Automation の root element を取得できません。'
    }

    # WinUI 3 の Panel は UI Automation ツリーへ必ず公開されるとは限らないため、
    # window handle から取得した root で main window の存在を確認し、子コントロールだけを検証する。
    $settingsExpander = Wait-UiElementByAutomationId -Root $root -AutomationId 'SettingsExpander'
    $expandCollapsePattern = $null
    if (-not $settingsExpander.TryGetCurrentPattern(
            [System.Windows.Automation.ExpandCollapsePattern]::Pattern,
            [ref]$expandCollapsePattern)) {
        throw 'SettingsExpander の ExpandCollapsePattern を取得できません。'
    }

    if ($expandCollapsePattern.Current.ExpandCollapseState -eq [System.Windows.Automation.ExpandCollapseState]::Collapsed) {
        $expandCollapsePattern.Expand()
    }

    $requiredElements = @(
        'GatewayUrlBox',
        'ResourceUrisBox',
        'GatewayLoginButton',
        'EnqueueReviewButton'
    )
    foreach ($automationId in $requiredElements) {
        Wait-UiElementByAutomationId -Root $root -AutomationId $automationId | Out-Null
    }
}

function Test-ExternalStackReadiness {
    if ([string]::IsNullOrWhiteSpace($env:DESKTOP_E2E_GATEWAY_URL)) {
        throw 'DesktopFull には DESKTOP_E2E_GATEWAY_URL が必要です。値はログへ出力しません。'
    }
    if ([string]::IsNullOrWhiteSpace($env:DESKTOP_E2E_RESOURCE_URIS)) {
        throw 'DesktopFull には DESKTOP_E2E_RESOURCE_URIS が必要です。値はログへ出力しません。'
    }

    try {
        $gatewayUri = [Uri]::new($env:DESKTOP_E2E_GATEWAY_URL)
    }
    catch {
        throw 'DESKTOP_E2E_GATEWAY_URL が有効な URI ではありません。'
    }
    if ($gatewayUri.Scheme -notin @('http', 'https')) {
        throw 'DESKTOP_E2E_GATEWAY_URL は http または https である必要があります。'
    }

    $resourceUris = @($env:DESKTOP_E2E_RESOURCE_URIS -split '\r?\n' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($resourceUris.Count -eq 0) {
        throw 'DESKTOP_E2E_RESOURCE_URIS に URI がありません。'
    }
    foreach ($resourceUri in $resourceUris) {
        try {
            $parsedResourceUri = [Uri]::new($resourceUri)
        }
        catch {
            throw 'DESKTOP_E2E_RESOURCE_URIS に不正な URI があります。'
        }
        if ($parsedResourceUri.Scheme -notin @('http', 'https')) {
            throw 'DESKTOP_E2E_RESOURCE_URIS は http または https である必要があります。'
        }
    }

    $manifestJson = $env:DESKTOP_E2E_COMPONENT_MANIFEST_JSON
    if ([string]::IsNullOrWhiteSpace($manifestJson) -and -not [string]::IsNullOrWhiteSpace($env:DESKTOP_E2E_COMPONENT_MANIFEST_PATH)) {
        if (-not (Test-Path -LiteralPath $env:DESKTOP_E2E_COMPONENT_MANIFEST_PATH -PathType Leaf)) {
            throw 'DESKTOP_E2E_COMPONENT_MANIFEST_PATH が見つかりません。'
        }
        $manifestJson = Get-Content -LiteralPath $env:DESKTOP_E2E_COMPONENT_MANIFEST_PATH -Raw -Encoding utf8
    }
    if ([string]::IsNullOrWhiteSpace($manifestJson)) {
        throw 'DesktopFull には component manifest が必要です。値は artifact へ出力しません。'
    }

    try {
        $manifest = $manifestJson | ConvertFrom-Json
    }
    catch {
        throw 'component manifest が JSON として解釈できません。'
    }
    if ($null -eq $manifest.schemaVersion -or $null -eq $manifest.components) {
        throw 'component manifest に schemaVersion または components がありません。'
    }

    $client = [System.Net.Sockets.TcpClient]::new()
    try {
        $port = if ($gatewayUri.IsDefaultPort) {
            if ($gatewayUri.Scheme -eq 'https') { 443 } else { 80 }
        }
        else {
            $gatewayUri.Port
        }
        $connection = $client.ConnectAsync($gatewayUri.Host, $port)
        if (-not $connection.Wait(5000) -or -not $client.Connected) {
            throw 'gateway の TCP endpoint に接続できません。'
        }
    }
    finally {
        $client.Dispose()
    }

    $docker = Get-Command docker -ErrorAction SilentlyContinue
    if ($null -eq $docker) {
        throw 'DesktopFull には Docker CLI が必要です。'
    }
    & $docker.Source info *> $null
    if ($LASTEXITCODE -ne 0) {
        throw 'Docker daemon が利用できません。'
    }

    Write-ScenarioLog '外部 stack の URI、component manifest、Docker、gateway endpoint を確認しました。'
}

function Invoke-FullScenarioDriver {
    if ([string]::IsNullOrWhiteSpace($env:DESKTOP_E2E_FULL_DRIVER)) {
        throw 'DesktopFull には DESKTOP_E2E_FULL_DRIVER が必要です。外部 stack の停止・復旧、device flow、queue、通知、sleep/resume を実行する driver を runner image に登録してください。'
    }
    if (-not (Test-Path -LiteralPath $env:DESKTOP_E2E_FULL_DRIVER -PathType Leaf)) {
        throw 'DESKTOP_E2E_FULL_DRIVER が見つかりません。'
    }

    & pwsh -NoProfile -File $env:DESKTOP_E2E_FULL_DRIVER `
        -MsiPath $resolvedMsiPath `
        -ArtifactDirectory $scenarioArtifactDirectory `
        -WindowHandle $windowHandle.ToInt64()
    if ($LASTEXITCODE -ne 0) {
        throw 'DesktopFull driver が失敗しました。'
    }
}

function Assert-CleanRunner {
    $dirtyReasons = [System.Collections.Generic.List[string]]::new()
    if (Test-Path -LiteralPath $installedDirectory -PathType Container) {
        $dirtyReasons.Add('install-directory')
    }
    if ($settingsExistedBefore) {
        $dirtyReasons.Add('user-settings')
    }
    if (Get-Process -Name $applicationProcessName -ErrorAction SilentlyContinue) {
        $dirtyReasons.Add('process')
    }
    if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
        $dirtyReasons.Add('scheduled-task')
    }
    if (Test-Path -LiteralPath 'HKCU:\Software\SquirrelNotifier') {
        $dirtyReasons.Add('registry-key')
    }
    if ($dirtyReasons.Count -ne 0) {
        $script:runnerWasDirty = $true
        throw "専用 runner が clean ではないため変更せず停止しました: $($dirtyReasons -join ', ')"
    }
}

function Get-ToolVersion {
    param(
        [Parameter(Mandatory)]
        [string]$Name
    )

    $command = Get-Command $Name -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $command) {
        return [ordered]@{ available = $false; path = $null; version = $null }
    }
    $version = $null
    try {
        $version = (& $command.Source --version 2>$null | Select-Object -First 1 | Out-String).Trim()
    }
    catch {
        $version = $null
    }
    return [ordered]@{
        available = $true
        path = $command.Source
        version = if ([string]::IsNullOrWhiteSpace($version)) { $null } else { $version }
    }
}

New-Item -ItemType Directory -Path $runRoot, $scenarioArtifactDirectory -Force | Out-Null

$result = [ordered]@{
    schemaVersion = 1
    phase = 'desktop'
    scenarioId = $Scenario
    runId = $runId
    status = 'running'
    startedAt = $startedAt.ToString('O')
    completedAt = $null
    steps = [ordered]@{
        preflight = 'not-run'
        externalStack = 'not-run'
        msiSha256 = $null
        install = 'not-run'
        launch = 'not-run'
        screenshot = 'not-run'
        uiAutomation = 'not-run'
        fullDriver = 'not-run'
    }
}

try {
    if (-not [Environment]::UserInteractive -or [Environment]::UserName -eq 'SYSTEM') {
        throw '対話ログオン済み desktop session が必要です。'
    }
    if (-not (Get-Process -Name explorer -ErrorAction SilentlyContinue)) {
        throw 'explorer.exe がないため desktop session を確認できません。'
    }

    Write-ScenarioLog "Desktop E2E を開始しました: scenario=$Scenario"
    Assert-CleanRunner
    $result.steps.preflight = 'passed'

    if ($Scenario -eq 'DesktopFull') {
        Test-ExternalStackReadiness
        $result.steps.externalStack = 'ready'
    }

    $msiHash = (Get-FileHash -LiteralPath $resolvedMsiPath -Algorithm SHA256).Hash
    $result.steps.msiSha256 = $msiHash
    $installStarted = $true
    Invoke-Msi -Operation Install -LogPath $installLogPath
    Wait-InstalledExecutable
    Assert-InstalledVersion
    $result.steps.install = 'passed'

    $applicationProcess = Start-Process -FilePath $installedExecutable -PassThru
    $applicationProcessId = $applicationProcess.Id
    $windowHandle = Wait-MainWindow -Process $applicationProcess
    $result.steps.launch = 'passed'

    $screenshotPath = Join-Path $scenarioArtifactDirectory 'main-window.png'
    Capture-Window -Handle $windowHandle -Path $screenshotPath
    $result.steps.screenshot = 'passed'

    Assert-UiContract
    $result.steps.uiAutomation = 'passed'

    if ($Scenario -eq 'DesktopFull') {
        Invoke-FullScenarioDriver
        $result.steps.fullDriver = 'passed'
    }

    Write-ScenarioLog 'install、launch、UI Automation、screenshot の確認に成功しました。'
    $result.status = 'passed'
}
catch {
    $failure = [ordered]@{
        schemaVersion = 1
        phase = 'desktop'
        scenarioId = $Scenario
        category = if ($result.steps.preflight -ne 'passed') { 'INFRA_RUNNER_FAILED' } elseif ($result.steps.install -ne 'passed') { 'PRODUCT_INSTALL_FAILED' } else { 'PRODUCT_UI_FAILED' }
        component = 'squirrel-notifier'
        message = Get-SafeMessage -Exception $_.Exception
        startedAt = $startedAt.ToString('O')
        completedAt = [DateTimeOffset]::UtcNow.ToString('O')
        artifactHints = @('result.json', 'versions.json', 'sanitized.log', 'cleanup.json')
    }
    $result.status = 'failed'
    $result.failureCategory = $failure.category
    Write-ScenarioLog "Desktop E2E に失敗しました: $($failure.category)"
}
finally {
    if ($null -ne $applicationProcessId) {
        try {
            $process = Get-Process -Id $applicationProcessId -ErrorAction SilentlyContinue
            if ($null -ne $process) {
                Stop-Process -Id $applicationProcessId -Force -ErrorAction Stop
                $process.WaitForExit(10000)
            }
        }
        catch {
            $cleanupErrors.Add('アプリケーションプロセスを停止できませんでした。')
        }
    }

    if ($installStarted) {
        try {
            Invoke-Msi -Operation Uninstall -LogPath $uninstallLogPath
        }
        catch {
            $cleanupErrors.Add('MSI uninstall に失敗しました。')
        }
    }

    if (-not $settingsExistedBefore -and (Test-Path -LiteralPath $settingsDirectory -PathType Container)) {
        try {
            Remove-Item -LiteralPath $settingsDirectory -Recurse -Force -ErrorAction Stop
        }
        catch {
            $cleanupErrors.Add('テスト中に作成された user settings を削除できませんでした。')
        }
    }

    $residuals = [System.Collections.Generic.List[string]]::new()
    if ($installStarted -and -not $script:runnerWasDirty) {
        if (Test-Path -LiteralPath $installedDirectory -PathType Container) {
            $residuals.Add('install-directory')
        }
        if (Get-Process -Name $applicationProcessName -ErrorAction SilentlyContinue) {
            $residuals.Add('process')
        }
        if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
            $residuals.Add('scheduled-task')
        }
        if (Test-Path -LiteralPath 'HKCU:\Software\SquirrelNotifier') {
            $residuals.Add('registry-key')
        }
    }
    if ($residuals.Count -ne 0) {
        $cleanupErrors.Add("残留が検出されました: $($residuals -join ', ')")
    }

    $cleanup = [ordered]@{
        schemaVersion = 1
        phase = 'desktop'
        scenarioId = $Scenario
        status = if ($cleanupErrors.Count -eq 0) { 'passed' } else { 'failed' }
        installAttempted = $installStarted
        residuals = $residuals
        errors = @($cleanupErrors)
        completedAt = [DateTimeOffset]::UtcNow.ToString('O')
    }
    Write-JsonFile -Path (Join-Path $scenarioArtifactDirectory 'cleanup.json') -Value $cleanup

    $versions = [ordered]@{
        schemaVersion = 1
        phase = 'desktop'
        measuredAt = [DateTimeOffset]::UtcNow.ToString('O')
        os = [ordered]@{
            caption = (Get-CimInstance Win32_OperatingSystem).Caption
            version = [Environment]::OSVersion.Version.ToString()
            build = (Get-CimInstance Win32_OperatingSystem).BuildNumber
        }
        powershell = $PSVersionTable.PSVersion.ToString()
        dotnet = Get-ToolVersion -Name 'dotnet'
        docker = Get-ToolVersion -Name 'docker'
        wix = Get-ToolVersion -Name 'wix'
        githubSha = if ([string]::IsNullOrWhiteSpace($env:GITHUB_SHA)) { $null } else { $env:GITHUB_SHA }
        scenario = $Scenario
    }
    Write-JsonFile -Path (Join-Path $scenarioArtifactDirectory 'versions.json') -Value $versions

    $logLines | Set-Content -LiteralPath (Join-Path $scenarioArtifactDirectory 'sanitized.log') -Encoding utf8
    $result.completedAt = [DateTimeOffset]::UtcNow.ToString('O')
    $result.cleanupStatus = $cleanup.status
    Write-JsonFile -Path (Join-Path $scenarioArtifactDirectory 'result.json') -Value $result
    if ($null -ne $failure) {
        Write-JsonFile -Path (Join-Path $scenarioArtifactDirectory 'failure.json') -Value $failure
    }
}

$artifactScanFailed = $false
& (Join-Path $PSScriptRoot 'Test-E2EArtifacts.ps1') -ArtifactsDirectory $scenarioArtifactDirectory
if ($LASTEXITCODE -ne 0) {
    $artifactScanFailed = $true
}

if ($null -ne $failure -or $cleanupErrors.Count -ne 0 -or $artifactScanFailed) {
    exit 1
}

Write-Host "Desktop E2E artifact: $scenarioArtifactDirectory"
exit 0
