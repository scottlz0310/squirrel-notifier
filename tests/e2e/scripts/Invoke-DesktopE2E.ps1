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

    [string]$ExpectedVersion
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$scenarioManifestFile = switch ($Scenario) {
    'DesktopSmoke' { 'desktop-smoke.json' }
    'DesktopFull' { 'desktop-full.json' }
}
$scenarioManifestPath = Join-Path $repoRoot "tests\e2e\scenarios\$scenarioManifestFile"
$expectedScenarioId = if ($Scenario -eq 'DesktopSmoke') { 'desktop-smoke' } else { 'desktop-full' }
$scenarioId = $expectedScenarioId
$scenarioManifest = $null
$scenarioManifestSchemaVersion = $null
$scenarioManifestExpectedOutcome = $null
$timeoutSeconds = 60
$requiredComponentNames = @()
$requiredDriverSteps = @()

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
$fullDriverResultPath = Join-Path $scenarioArtifactDirectory 'full-driver-result.json'
$installedDirectory = Join-Path $env:LOCALAPPDATA 'Programs\SquirrelNotifier'
$installedExecutable = Join-Path $installedDirectory 'SquirrelNotifier.WinUI3.exe'
$settingsDirectory = Join-Path $env:LOCALAPPDATA 'SquirrelNotifier'
$taskName = 'Squirrel Notifier'
$applicationProcessName = 'SquirrelNotifier.WinUI3'
$applicationProcessId = $null
$script:fullDriverProcessId = $null
$installStarted = $false
$script:runnerWasDirty = $false
$settingsExistedBefore = Test-Path -LiteralPath $settingsDirectory -PathType Container
$startedAt = [DateTimeOffset]::UtcNow
$scenarioDeadline = $startedAt.AddSeconds($timeoutSeconds)
$logLines = [System.Collections.Generic.List[string]]::new()
$failure = $null
$cleanupErrors = [System.Collections.Generic.List[string]]::new()
$windowHandle = [IntPtr]::Zero
$script:failureCategoryOverride = $null
$script:componentManifestForArtifact = [ordered]@{}
$artifactDirectoryReady = $false

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

function Set-FailureCategory {
    param(
        [Parameter(Mandatory)]
        [string]$Category
    )

    $script:failureCategoryOverride = $Category
}

function New-FailureRecord {
    param(
        [Parameter(Mandatory)]
        [string]$Category,

        [Parameter(Mandatory)]
        [string]$Component,

        [Parameter(Mandatory)]
        [string]$Message
    )

    $artifactHints = @('result.json', 'versions.json', 'sanitized.log', 'cleanup.json')
    if ($Scenario -eq 'DesktopFull') {
        $artifactHints += 'full-driver-result.json'
    }

    return [ordered]@{
        schemaVersion = 1
        phase = 'desktop'
        scenarioId = $scenarioId
        category = $Category
        component = $Component
        message = $Message
        startedAt = $startedAt.ToString('O')
        completedAt = [DateTimeOffset]::UtcNow.ToString('O')
        artifactHints = $artifactHints
    }
}

function Initialize-ScenarioManifest {
    try {
        $script:scenarioManifest = Get-Content -LiteralPath $scenarioManifestPath -Raw -Encoding utf8 | ConvertFrom-Json
    }
    catch {
        Set-FailureCategory -Category 'CONTRACT_VERSION_MISMATCH'
        throw "desktop scenario manifest を読み込めません: $scenarioManifestFile"
    }
    if ($null -eq $scenarioManifest) {
        Set-FailureCategory -Category 'CONTRACT_VERSION_MISMATCH'
        throw "desktop scenario manifest が空です: $scenarioManifestFile"
    }

    $schemaVersionProperty = $scenarioManifest.PSObject.Properties['schemaVersion']
    $schemaVersion = if ($null -eq $schemaVersionProperty) { $null } else { $schemaVersionProperty.Value }
    if ($null -eq $schemaVersion -or $schemaVersion -ne 1) {
        Set-FailureCategory -Category 'CONTRACT_VERSION_MISMATCH'
        throw "desktop scenario manifest の schemaVersion は 1 である必要があります: $scenarioManifestFile"
    }
    $phaseProperty = $scenarioManifest.PSObject.Properties['phase']
    $phase = if ($null -eq $phaseProperty) { $null } else { [string]$phaseProperty.Value }
    if ($phase -ne 'desktop') {
        Set-FailureCategory -Category 'CONTRACT_VERSION_MISMATCH'
        throw "desktop scenario manifest の phase が不正です: $scenarioManifestFile"
    }
    $idProperty = $scenarioManifest.PSObject.Properties['id']
    $manifestScenarioId = if ($null -eq $idProperty) { '' } else { [string]$idProperty.Value }
    if ($manifestScenarioId -ne $expectedScenarioId) {
        Set-FailureCategory -Category 'CONTRACT_VERSION_MISMATCH'
        throw "desktop scenario manifest の id が不正です: 期待値=$expectedScenarioId"
    }
    $timeoutProperty = $scenarioManifest.PSObject.Properties['timeoutSeconds']
    try {
        if ($null -eq $timeoutProperty) {
            throw 'timeoutSeconds がありません。'
        }
        $manifestTimeoutSeconds = [int]$timeoutProperty.Value
    }
    catch {
        Set-FailureCategory -Category 'CONTRACT_VERSION_MISMATCH'
        throw "desktop scenario manifest の timeoutSeconds が不正です: $manifestScenarioId"
    }
    if ($manifestTimeoutSeconds -le 0) {
        Set-FailureCategory -Category 'CONTRACT_VERSION_MISMATCH'
        throw "desktop scenario manifest の timeoutSeconds が不正です: $manifestScenarioId"
    }
    $expectedOutcomeProperty = $scenarioManifest.PSObject.Properties['expectedOutcome']
    $manifestExpectedOutcome = if ($null -eq $expectedOutcomeProperty) { '' } else { [string]$expectedOutcomeProperty.Value }
    if ([string]::IsNullOrWhiteSpace($manifestExpectedOutcome)) {
        Set-FailureCategory -Category 'CONTRACT_VERSION_MISMATCH'
        throw "desktop scenario manifest の expectedOutcome が不正です: $manifestScenarioId"
    }

    $script:scenarioId = $manifestScenarioId
    $script:scenarioManifestSchemaVersion = $schemaVersion
    $script:scenarioManifestExpectedOutcome = $manifestExpectedOutcome
    $script:timeoutSeconds = $manifestTimeoutSeconds
    $script:scenarioDeadline = $startedAt.AddSeconds($manifestTimeoutSeconds)
    $script:requiredComponentNames = @()
    $script:requiredDriverSteps = @()
    if ($Scenario -eq 'DesktopFull') {
        $requiredComponentsProperty = $scenarioManifest.PSObject.Properties['requiredComponents']
        $requiredDriverStepsProperty = $scenarioManifest.PSObject.Properties['requiredDriverSteps']
        if ($null -eq $requiredComponentsProperty -or $null -eq $requiredDriverStepsProperty) {
            Set-FailureCategory -Category 'CONTRACT_VERSION_MISMATCH'
            throw 'DesktopFull manifest に requiredComponents または requiredDriverSteps がありません。'
        }
        $script:requiredComponentNames = @($requiredComponentsProperty.Value | ForEach-Object { [string]$_ })
        $script:requiredDriverSteps = @($requiredDriverStepsProperty.Value | ForEach-Object { [string]$_ })
        if ($requiredComponentNames.Count -eq 0 -or $requiredDriverSteps.Count -eq 0) {
            Set-FailureCategory -Category 'CONTRACT_VERSION_MISMATCH'
            throw 'DesktopFull manifest に requiredComponents または requiredDriverSteps がありません。'
        }
    }
}

function Get-ScenarioRemainingMilliseconds {
    $remainingMilliseconds = [Math]::Floor(($scenarioDeadline - [DateTimeOffset]::UtcNow).TotalMilliseconds)
    if ($remainingMilliseconds -le 0) {
        Set-FailureCategory -Category 'TIMEOUT'
        throw "scenario の timeoutSeconds を超過しました: $scenarioId"
    }

    return [int][Math]::Min($remainingMilliseconds, [int]::MaxValue)
}

function ConvertTo-ReleaseVersion {
    param(
        [Parameter(Mandatory)]
        [string]$Value
    )

    $match = [regex]::Match($Value.Trim(), '^v?(\d+)\.(\d+)\.(\d+)(?:\.0)?(?:[-+][0-9A-Za-z.-]+)?$')
    if (-not $match.Success) {
        return $null
    }

    return '{0}.{1}.{2}' -f $match.Groups[1].Value, $match.Groups[2].Value, $match.Groups[3].Value
}

function Invoke-Msi {
    param(
        [Parameter(Mandatory)]
        [ValidateSet('Install', 'Uninstall')]
        [string]$Operation,

        [Parameter(Mandatory)]
        [string]$LogPath,

        [switch]$UseScenarioDeadline
    )

    $arguments = if ($Operation -eq 'Install') {
        @('/i', ('"{0}"' -f $resolvedMsiPath), '/qn', '/norestart', '/L*v', ('"{0}"' -f $LogPath))
    }
    else {
        @('/x', ('"{0}"' -f $resolvedMsiPath), '/qn', '/norestart', '/L*v', ('"{0}"' -f $LogPath))
    }

    $process = Start-Process -FilePath (Join-Path $env:WINDIR 'System32\msiexec.exe') -ArgumentList $arguments -PassThru -WindowStyle Hidden
    $timeoutMilliseconds = if ($UseScenarioDeadline) {
        Get-ScenarioRemainingMilliseconds
    }
    else {
        120000
    }
    if (-not $process.WaitForExit($timeoutMilliseconds)) {
        try {
            Stop-Process -Id $process.Id -Force -ErrorAction Stop
        }
        catch {
        }
        if ($UseScenarioDeadline) {
            Set-FailureCategory -Category 'TIMEOUT'
        }
        throw "MSI $Operation が timeout 内に終了しませんでした。"
    }
    if ($process.ExitCode -ne 0) {
        throw "MSI $Operation が終了コード $($process.ExitCode) で失敗しました。"
    }
}

function Wait-InstalledExecutable {
    while ([DateTimeOffset]::UtcNow -lt $scenarioDeadline) {
        if (Test-Path -LiteralPath $installedExecutable -PathType Leaf) {
            return
        }
        Start-Sleep -Milliseconds 500
    }

    Set-FailureCategory -Category 'TIMEOUT'
    throw "インストール後の実行ファイルが見つかりません: $installedExecutable"
}

function Assert-InstalledVersion {
    if ([string]::IsNullOrWhiteSpace($ExpectedVersion)) {
        return
    }

    $versionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($installedExecutable)
    $expectedVersionKey = ConvertTo-ReleaseVersion -Value $ExpectedVersion
    $actualVersionKey = ConvertTo-ReleaseVersion -Value $versionInfo.ProductVersion
    if ($null -eq $expectedVersionKey -or $null -eq $actualVersionKey -or $actualVersionKey -cne $expectedVersionKey) {
        Set-FailureCategory -Category 'CONTRACT_VERSION_MISMATCH'
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
    while ([DateTimeOffset]::UtcNow -lt $scenarioDeadline) {
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

    Set-FailureCategory -Category 'TIMEOUT'
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

    while ([DateTimeOffset]::UtcNow -lt $scenarioDeadline) {
        $element = Get-UiElementByAutomationId -Root $Root -AutomationId $AutomationId
        if ($null -ne $element) {
            return $element
        }
        Start-Sleep -Milliseconds 200
    }

    Set-FailureCategory -Category 'TIMEOUT'
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

function Test-ComponentManifest {
    param(
        [Parameter(Mandatory)]
        [object]$Manifest
    )

    if ($null -eq $Manifest) {
        Set-FailureCategory -Category 'CONTRACT_VERSION_MISMATCH'
        throw 'component manifest が空です。'
    }

    $schemaVersionProperty = $Manifest.PSObject.Properties['schemaVersion']
    $schemaVersion = if ($null -eq $schemaVersionProperty) { $null } else { $schemaVersionProperty.Value }
    if ($null -eq $schemaVersion -or $schemaVersion -ne 1) {
        Set-FailureCategory -Category 'CONTRACT_VERSION_MISMATCH'
        throw 'component manifest の schemaVersion は 1 である必要があります。'
    }

    $componentsProperty = $Manifest.PSObject.Properties['components']
    $components = if ($null -eq $componentsProperty) { $null } else { $componentsProperty.Value }
    if ($null -eq $components) {
        Set-FailureCategory -Category 'CONTRACT_VERSION_MISMATCH'
        throw 'component manifest に components がありません。'
    }

    $componentProperties = @($components.PSObject.Properties)
    $actualComponentNames = @($componentProperties | ForEach-Object { $_.Name })
    $missingComponents = @($requiredComponentNames | Where-Object { $_ -notin $actualComponentNames })
    $unexpectedComponents = @($actualComponentNames | Where-Object { $_ -notin $requiredComponentNames })
    if ($missingComponents.Count -ne 0 -or $unexpectedComponents.Count -ne 0) {
        Set-FailureCategory -Category 'CONTRACT_VERSION_MISMATCH'
        throw 'component manifest の required component 一覧が scenario と一致しません。'
    }

    $safeComponents = [ordered]@{}
    foreach ($componentName in $requiredComponentNames) {
        $componentProperty = $components.PSObject.Properties[$componentName]
        $component = if ($null -eq $componentProperty) { $null } else { $componentProperty.Value }
        if ($null -eq $component) {
            Set-FailureCategory -Category 'CONTRACT_VERSION_MISMATCH'
            throw "component manifest の component が不正です: $componentName"
        }

        $versionProperty = $component.PSObject.Properties['version']
        $digestProperty = $component.PSObject.Properties['digest']
        $version = if ($null -eq $versionProperty) { '' } else { [string]$versionProperty.Value }
        $digest = if ($null -eq $digestProperty) { '' } else { [string]$digestProperty.Value }
        $validVersion = if ($componentName -eq 'mcp-resource-subscriber') {
            $version -match '^(?:[0-9a-fA-F]{40}|v?\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?)$'
        }
        else {
            $version -match '^[0-9a-fA-F]{40}$'
        }
        $validDigest = $digest -match '^sha256:[0-9a-fA-F]{64}$'
        if (-not $validVersion -or -not $validDigest) {
            Set-FailureCategory -Category 'CONTRACT_VERSION_MISMATCH'
            throw "component manifest の version または digest が不正です: $componentName"
        }

        $safeComponents[$componentName] = [ordered]@{
            version = $version
            digest = $digest
        }
    }
    $script:componentManifestForArtifact = $safeComponents
}

function Test-ExternalStackReadiness {
    if ([string]::IsNullOrWhiteSpace($env:DESKTOP_E2E_GATEWAY_URL)) {
        Set-FailureCategory -Category 'CONTRACT_VERSION_MISMATCH'
        throw 'DesktopFull には DESKTOP_E2E_GATEWAY_URL が必要です。値はログへ出力しません。'
    }
    if ([string]::IsNullOrWhiteSpace($env:DESKTOP_E2E_RESOURCE_URIS)) {
        Set-FailureCategory -Category 'CONTRACT_VERSION_MISMATCH'
        throw 'DesktopFull には DESKTOP_E2E_RESOURCE_URIS が必要です。値はログへ出力しません。'
    }

    try {
        $gatewayUri = [Uri]::new($env:DESKTOP_E2E_GATEWAY_URL)
    }
    catch {
        Set-FailureCategory -Category 'CONTRACT_VERSION_MISMATCH'
        throw 'DESKTOP_E2E_GATEWAY_URL が有効な URI ではありません。'
    }
    if ($gatewayUri.Scheme -notin @('http', 'https')) {
        Set-FailureCategory -Category 'CONTRACT_VERSION_MISMATCH'
        throw 'DESKTOP_E2E_GATEWAY_URL は http または https である必要があります。'
    }

    $resourceUris = @($env:DESKTOP_E2E_RESOURCE_URIS -split '\r?\n' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($resourceUris.Count -eq 0) {
        Set-FailureCategory -Category 'CONTRACT_VERSION_MISMATCH'
        throw 'DESKTOP_E2E_RESOURCE_URIS に URI がありません。'
    }
    foreach ($resourceUri in $resourceUris) {
        try {
            $parsedResourceUri = [Uri]::new($resourceUri)
        }
        catch {
            Set-FailureCategory -Category 'CONTRACT_VERSION_MISMATCH'
            throw 'DESKTOP_E2E_RESOURCE_URIS に不正な URI があります。'
        }
        if ($parsedResourceUri.Scheme -notin @('http', 'https')) {
            Set-FailureCategory -Category 'CONTRACT_VERSION_MISMATCH'
            throw 'DESKTOP_E2E_RESOURCE_URIS は http または https である必要があります。'
        }
    }

    $manifestJson = $env:DESKTOP_E2E_COMPONENT_MANIFEST_JSON
    if ([string]::IsNullOrWhiteSpace($manifestJson) -and -not [string]::IsNullOrWhiteSpace($env:DESKTOP_E2E_COMPONENT_MANIFEST_PATH)) {
        if (-not (Test-Path -LiteralPath $env:DESKTOP_E2E_COMPONENT_MANIFEST_PATH -PathType Leaf)) {
            Set-FailureCategory -Category 'CONTRACT_VERSION_MISMATCH'
            throw 'DESKTOP_E2E_COMPONENT_MANIFEST_PATH が見つかりません。'
        }
        try {
            $manifestJson = Get-Content -LiteralPath $env:DESKTOP_E2E_COMPONENT_MANIFEST_PATH -Raw -Encoding utf8
        }
        catch {
            Set-FailureCategory -Category 'CONTRACT_VERSION_MISMATCH'
            throw 'DESKTOP_E2E_COMPONENT_MANIFEST_PATH を読み込めません。'
        }
    }
    if ([string]::IsNullOrWhiteSpace($manifestJson)) {
        Set-FailureCategory -Category 'CONTRACT_VERSION_MISMATCH'
        throw 'DesktopFull には component manifest が必要です。値は artifact へ出力しません。'
    }

    try {
        $manifest = $manifestJson | ConvertFrom-Json
    }
    catch {
        Set-FailureCategory -Category 'CONTRACT_VERSION_MISMATCH'
        throw 'component manifest が JSON として解釈できません。'
    }
    Test-ComponentManifest -Manifest $manifest

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
            Set-FailureCategory -Category 'INFRA_RUNNER_FAILED'
            throw 'gateway の TCP endpoint に接続できません。'
        }
    }
    catch {
        if ($null -eq $script:failureCategoryOverride) {
            Set-FailureCategory -Category 'INFRA_RUNNER_FAILED'
        }
        throw
    }
    finally {
        $client.Dispose()
    }

    $docker = Get-Command docker -ErrorAction SilentlyContinue
    if ($null -eq $docker) {
        Set-FailureCategory -Category 'INFRA_RUNNER_FAILED'
        throw 'DesktopFull には Docker CLI が必要です。'
    }
    & $docker.Source info *> $null
    if ($LASTEXITCODE -ne 0) {
        Set-FailureCategory -Category 'INFRA_RUNNER_FAILED'
        throw 'Docker daemon が利用できません。'
    }

    Write-ScenarioLog '外部 stack の URI、component manifest、Docker、gateway endpoint を確認しました。'
}

function Invoke-FullScenarioDriver {
    if ([string]::IsNullOrWhiteSpace($env:DESKTOP_E2E_FULL_DRIVER)) {
        Set-FailureCategory -Category 'CONTRACT_VERSION_MISMATCH'
        throw 'DesktopFull には DESKTOP_E2E_FULL_DRIVER が必要です。外部 stack の停止・復旧、device flow、queue、通知、sleep/resume を実行する driver を runner image に登録してください。'
    }
    if (-not (Test-Path -LiteralPath $env:DESKTOP_E2E_FULL_DRIVER -PathType Leaf)) {
        Set-FailureCategory -Category 'CONTRACT_VERSION_MISMATCH'
        throw 'DESKTOP_E2E_FULL_DRIVER が見つかりません。'
    }

    $pwshCommand = Get-Command pwsh -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $pwshCommand) {
        Set-FailureCategory -Category 'CONTRACT_VERSION_MISMATCH'
        throw 'DesktopFull driver を実行する pwsh が見つかりません。'
    }

    $driverArguments = @(
        '-NoProfile',
        '-File', $env:DESKTOP_E2E_FULL_DRIVER,
        '-MsiPath', $resolvedMsiPath,
        '-ArtifactDirectory', $scenarioArtifactDirectory,
        '-WindowHandle', [string]$windowHandle.ToInt64(),
        '-ScenarioManifestPath', $scenarioManifestPath,
        '-ExpectedOutcome', [string]$scenarioManifest.expectedOutcome,
        '-ResultPath', $fullDriverResultPath
    )
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $pwshCommand.Source
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    foreach ($argument in $driverArguments) {
        $startInfo.ArgumentList.Add([string]$argument)
    }

    $driverProcess = [System.Diagnostics.Process]::new()
    $driverProcess.StartInfo = $startInfo
    $driverStarted = $false
    try {
        if (-not $driverProcess.Start()) {
            Set-FailureCategory -Category 'PRODUCT_CONTRACT_MISMATCH'
            throw 'DesktopFull driver を起動できません。'
        }
        $driverStarted = $true
        $script:fullDriverProcessId = $driverProcess.Id
        $remainingMilliseconds = Get-ScenarioRemainingMilliseconds
        if (-not $driverProcess.WaitForExit($remainingMilliseconds)) {
            try {
                Stop-Process -Id $driverProcess.Id -Force -ErrorAction Stop
            }
            catch {
            }
            Set-FailureCategory -Category 'TIMEOUT'
            throw 'DesktopFull driver が scenario timeout 内に終了しませんでした。'
        }
        if ($driverProcess.ExitCode -ne 0) {
            Set-FailureCategory -Category 'PRODUCT_CONTRACT_MISMATCH'
            throw 'DesktopFull driver が失敗しました。'
        }
    }
    catch {
        if ($null -eq $script:failureCategoryOverride) {
            Set-FailureCategory -Category 'PRODUCT_CONTRACT_MISMATCH'
        }
        throw
    }
    finally {
        if ($driverStarted -and $driverProcess.HasExited) {
            $script:fullDriverProcessId = $null
        }
        $driverProcess.Dispose()
    }

    if (-not (Test-Path -LiteralPath $fullDriverResultPath -PathType Leaf)) {
        Set-FailureCategory -Category 'PRODUCT_CONTRACT_MISMATCH'
        throw 'DesktopFull driver が machine-readable result を生成していません。'
    }

    try {
        $driverResult = Get-Content -LiteralPath $fullDriverResultPath -Raw -Encoding utf8 | ConvertFrom-Json
    }
    catch {
        Set-FailureCategory -Category 'PRODUCT_CONTRACT_MISMATCH'
        throw 'DesktopFull driver result が JSON として解釈できません。'
    }
    if ($null -eq $driverResult) {
        Set-FailureCategory -Category 'PRODUCT_CONTRACT_MISMATCH'
        throw 'DesktopFull driver result が空です。'
    }
    $driverSchemaVersionProperty = $driverResult.PSObject.Properties['schemaVersion']
    $driverScenarioIdProperty = $driverResult.PSObject.Properties['scenarioId']
    $driverExpectedOutcomeProperty = $driverResult.PSObject.Properties['expectedOutcome']
    $driverOutcomeProperty = $driverResult.PSObject.Properties['outcome']
    $driverStepsProperty = $driverResult.PSObject.Properties['steps']
    $driverSchemaVersion = if ($null -eq $driverSchemaVersionProperty) { $null } else { $driverSchemaVersionProperty.Value }
    $driverScenarioId = if ($null -eq $driverScenarioIdProperty) { '' } else { [string]$driverScenarioIdProperty.Value }
    $driverExpectedOutcome = if ($null -eq $driverExpectedOutcomeProperty) { '' } else { [string]$driverExpectedOutcomeProperty.Value }
    $driverOutcome = if ($null -eq $driverOutcomeProperty) { '' } else { [string]$driverOutcomeProperty.Value }
    $driverSteps = if ($null -eq $driverStepsProperty) { $null } else { $driverStepsProperty.Value }
    if ($driverSchemaVersion -ne 1 -or
        $driverScenarioId -ne $scenarioId -or
        $driverExpectedOutcome -ne $scenarioManifestExpectedOutcome -or
        $driverOutcome -ne $scenarioManifestExpectedOutcome -or
        $null -eq $driverSteps) {
        Set-FailureCategory -Category 'PRODUCT_CONTRACT_MISMATCH'
        throw 'DesktopFull driver result の scenario、expectedOutcome、outcome、または schemaVersion が不正です。'
    }

    foreach ($requiredStep in $requiredDriverSteps) {
        $stepProperty = $driverSteps.PSObject.Properties[$requiredStep]
        if ($null -eq $stepProperty -or [string]$stepProperty.Value -ne 'passed') {
            Set-FailureCategory -Category 'PRODUCT_CONTRACT_MISMATCH'
            throw "DesktopFull driver result の必須 step が成功していません: $requiredStep"
        }
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

$result = [ordered]@{
    schemaVersion = 1
    phase = 'desktop'
    scenarioId = $scenarioId
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
        fullDriverResult = 'not-run'
    }
}

try {
    try {
        New-Item -ItemType Directory -Path $runRoot -Force -ErrorAction Stop | Out-Null
        New-Item -ItemType Directory -Path $scenarioArtifactDirectory -Force -ErrorAction Stop | Out-Null
        if (-not (Test-Path -LiteralPath $scenarioArtifactDirectory -PathType Container)) {
            throw 'E2E artifact directory が作成されませんでした。'
        }
        $artifactDirectoryReady = $true
    }
    catch {
        Set-FailureCategory -Category 'INFRA_RUNNER_FAILED'
        throw 'Desktop E2E の temporary root または artifact directory を初期化できません。'
    }

    Initialize-ScenarioManifest

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
    Invoke-Msi -Operation Install -LogPath $installLogPath -UseScenarioDeadline
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
        $result.steps.fullDriverResult = 'passed'
    }

    Write-ScenarioLog 'install、launch、UI Automation、screenshot の確認に成功しました。'
    $result.status = 'passed'
}
catch {
    $failureCategory = $script:failureCategoryOverride
    if ([string]::IsNullOrWhiteSpace($failureCategory)) {
        $failureCategory = if ($result.steps.preflight -ne 'passed') {
            'INFRA_RUNNER_FAILED'
        }
        elseif ($result.steps.install -ne 'passed') {
            'PRODUCT_INSTALL_FAILED'
        }
        else {
            'PRODUCT_UI_FAILED'
        }
    }
    $failure = New-FailureRecord `
        -Category $failureCategory `
        -Component 'squirrel-notifier' `
        -Message (Get-SafeMessage -Exception $_.Exception)
    $result.status = 'failed'
    $result.failureCategory = $failure.category
    Write-ScenarioLog "Desktop E2E に失敗しました: $($failure.category)"
}
finally {
    if ($null -ne $script:fullDriverProcessId) {
        try {
            $driverProcess = Get-Process -Id $script:fullDriverProcessId -ErrorAction SilentlyContinue
            if ($null -ne $driverProcess) {
                Stop-Process -Id $script:fullDriverProcessId -Force -ErrorAction Stop
                $driverProcess.WaitForExit(5000) | Out-Null
            }
            $script:fullDriverProcessId = $null
        }
        catch {
            $cleanupErrors.Add('DesktopFull driver プロセスを停止できませんでした。')
        }
    }

    if ($null -ne $applicationProcessId) {
        try {
            $process = Get-Process -Id $applicationProcessId -ErrorAction SilentlyContinue
            if ($null -ne $process) {
                Stop-Process -Id $applicationProcessId -Force -ErrorAction Stop
                $process.WaitForExit(10000) | Out-Null
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

    $runRootRemoved = $false
    try {
        if (Test-Path -LiteralPath $runRoot) {
            Remove-Item -LiteralPath $runRoot -Recurse -Force -ErrorAction Stop
        }
        $runRootRemoved = -not (Test-Path -LiteralPath $runRoot)
        if (-not $runRootRemoved) {
            throw '専用 temporary root が残っています。'
        }
    }
    catch {
        $cleanupErrors.Add('専用 temporary root を削除できませんでした。')
    }

    try {
        if (-not (Test-Path -LiteralPath $scenarioArtifactDirectory -PathType Container)) {
            New-Item -ItemType Directory -Path $scenarioArtifactDirectory -Force -ErrorAction Stop | Out-Null
        }
        $artifactDirectoryReady = Test-Path -LiteralPath $scenarioArtifactDirectory -PathType Container
        if (-not $artifactDirectoryReady) {
            throw 'E2E artifact directory が利用できません。'
        }
    }
    catch {
        $artifactDirectoryReady = $false
    }

    $cleanup = [ordered]@{
        schemaVersion = 1
        phase = 'desktop'
        scenarioId = $scenarioId
        status = if ($cleanupErrors.Count -eq 0) { 'passed' } else { 'failed' }
        installAttempted = $installStarted
        residuals = $residuals
        errors = @($cleanupErrors)
        runRootRemoved = $runRootRemoved
        completedAt = [DateTimeOffset]::UtcNow.ToString('O')
    }
    if ($artifactDirectoryReady) {
        Write-JsonFile -Path (Join-Path $scenarioArtifactDirectory 'cleanup.json') -Value $cleanup

        if ($cleanupErrors.Count -ne 0) {
            $failure = New-FailureRecord `
                -Category 'CLEANUP_FAILED' `
                -Component 'desktop-e2e-harness' `
                -Message "cleanup に失敗しました: $($cleanupErrors -join ' ')"
            $result.status = 'failed'
            $result.failureCategory = $failure.category
            Write-ScenarioLog 'Desktop E2E の cleanup に失敗しました。'
        }

        $versions = [ordered]@{
            schemaVersion = 1
            phase = 'desktop'
            measuredAt = [DateTimeOffset]::UtcNow.ToString('O')
            scenarioId = $scenarioId
            timeoutSeconds = $timeoutSeconds
            scenarioManifest = [ordered]@{
                schemaVersion = $scenarioManifestSchemaVersion
                id = $scenarioId
                expectedOutcome = $scenarioManifestExpectedOutcome
            }
            components = $script:componentManifestForArtifact
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
}

$artifactScanFailed = $false
if ($artifactDirectoryReady) {
    & (Join-Path $PSScriptRoot 'Test-E2EArtifacts.ps1') -ArtifactsDirectory $scenarioArtifactDirectory
    if ($LASTEXITCODE -ne 0) {
        $artifactScanFailed = $true
        $failure = New-FailureRecord `
            -Category 'SECURITY_SECRET_EXPOSURE' `
            -Component 'desktop-e2e-harness' `
            -Message 'E2E artifact の必須ファイルまたは secret scan の検査に失敗しました。'
        $result.status = 'failed'
        $result.failureCategory = $failure.category
        $result.completedAt = [DateTimeOffset]::UtcNow.ToString('O')
        Write-JsonFile -Path (Join-Path $scenarioArtifactDirectory 'result.json') -Value $result
        Write-JsonFile -Path (Join-Path $scenarioArtifactDirectory 'failure.json') -Value $failure
    }
}
else {
    $artifactScanFailed = $true
}

if ($null -ne $failure -or $cleanupErrors.Count -ne 0 -or $artifactScanFailed) {
    exit 1
}

Write-Host "Desktop E2E artifact: $scenarioArtifactDirectory"
exit 0
