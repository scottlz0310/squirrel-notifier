<#
.SYNOPSIS
    実 publish payload、MSI、セットアップ ZIP の build・install・uninstall を検証します。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ScenarioFile,

    [Parameter(Mandatory)]
    [string]$RunRoot,

    [Parameter(Mandatory)]
    [string]$ArtifactsDirectory
)

class DistributionE2EException : System.Exception
{
    [string]$Category

    DistributionE2EException([string]$Category, [string]$Message) : base($Message)
    {
        $this.Category = $Category
    }
}

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$runRootFullPath = [System.IO.Path]::GetFullPath($RunRoot)
$artifactsFullPath = [System.IO.Path]::GetFullPath($ArtifactsDirectory)
$marker = [System.IO.Path]::DirectorySeparatorChar + 'squirrel-notifier-e2e' + [System.IO.Path]::DirectorySeparatorChar
if (-not $runRootFullPath.Contains($marker, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'RunRoot は squirrel-notifier-e2e 専用 root 配下でなければなりません。'
}

$manifest = Get-Content -LiteralPath $ScenarioFile -Raw -Encoding utf8 | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1 -or
    $manifest.phase -ne 'headless' -or
    $manifest.scenarioKind -ne 'distribution-install' -or
    $manifest.id -ne 'distribution-install') {
    throw [DistributionE2EException]::new('TEST_HARNESS_FAILED', '配布物 E2E scenario manifest の schema または scenario kind が不正です。')
}

$publishProject = Join-Path $repoRoot 'winui3\SquirrelNotifier.WinUI3\SquirrelNotifier.WinUI3.csproj'
$installerProject = Join-Path $repoRoot 'winui3\SquirrelNotifier.Installer\SquirrelNotifier.Installer.wixproj'
$majorUpgradeScript = Join-Path $repoRoot 'scripts\test-msi-major-upgrade.ps1'
$msiexecPath = Join-Path $env:SystemRoot 'System32\msiexec.exe'
$taskName = 'Squirrel Notifier'
$processName = 'SquirrelNotifier.WinUI3'
$installRoot = Join-Path $env:LOCALAPPDATA 'Programs\SquirrelNotifier'
$settingsPath = Join-Path $env:LOCALAPPDATA 'SquirrelNotifier'
$markerRegistryPath = 'HKCU:\Software\SquirrelNotifier'
$uninstallRegistryPaths = @(
    'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*',
    'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*',
    'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'
)

$publishDirectory = Join-Path $runRootFullPath 'publish\x64'
$msiOutputDirectory = Join-Path $runRootFullPath 'msi'
$bundleSourceDirectory = Join-Path $runRootFullPath 'bundle-source'
$bundleDirectory = Join-Path $runRootFullPath 'bundle'
$zipPath = Join-Path $runRootFullPath 'SquirrelNotifier-Setup-x64.zip'
$msiPath = $null
$expectedVersion = 'unknown'
$dotnetVersion = 'unknown'
$publishedFileVersion = 'unknown'
$publishedProductVersion = 'unknown'
$msiProductVersion = 'unknown'
$publishFileCount = 0
$msiLogPaths = [System.Collections.Generic.List[string]]::new()
$logLines = [System.Collections.Generic.List[string]]::new()
$assertions = [System.Collections.Generic.List[string]]::new()
$observations = [System.Collections.Generic.List[object]]::new()
$requiredBundleFiles = [System.Collections.Generic.List[string]]::new()
$cleanupErrors = [System.Collections.Generic.List[string]]::new()
$startedAt = [DateTimeOffset]::UtcNow
$failureCategory = $null
$failureMessage = $null
$preflightCompleted = $false
$msiScenarioStarted = $false
$msiInstallAttempted = $false
$bundleScenarioStarted = $false
$bundleInstallAttempted = $false
$failureState = $null
$finalState = $null
$msiTimeoutSeconds = 180

function Add-Log {
    param(
        [Parameter(Mandatory)]
        [string]$Message
    )

    $logLines.Add("$([DateTimeOffset]::UtcNow.ToString('O')) $Message")
}

function Write-Phase {
    param(
        [Parameter(Mandatory)]
        [string]$Message
    )

    Add-Log $Message
    Write-Host "distribution E2E: $Message"
}

function Sanitize-Text {
    param(
        [AllowEmptyString()]
        [string]$Text
    )

    $sanitized = [regex]::Replace(
        $Text,
        '(?i)\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b',
        '<redacted-id>')
    foreach ($pattern in @(
            '(?i)\bgh[pousr]_[A-Za-z0-9_]{20,}\b',
            '(?i)\bgithub_pat_[A-Za-z0-9_]{20,}\b',
            '(?i)\bsk-[A-Za-z0-9]{20,}\b',
            '(?i)\bBearer\s+[A-Za-z0-9._-]{20,}\b')) {
        $sanitized = [regex]::Replace($sanitized, $pattern, '<redacted-secret>')
    }

    return $sanitized
}

function Invoke-ExternalCommand {
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,

        [Parameter(Mandatory)]
        [string[]]$Arguments,

        [Parameter(Mandatory)]
        [string]$Label,

        [Parameter(Mandatory)]
        [string]$FailureCategory
    )

    Add-Log "$Label を実行します: $FilePath $($Arguments -join ' ')"
    $output = ''
    try {
        $output = (& $FilePath @Arguments 2>&1 | Out-String)
    }
    catch {
        Add-Log "$Label の起動に失敗しました: $($_.Exception.Message)"
        throw [DistributionE2EException]::new($FailureCategory, "$Label の起動に失敗しました。")
    }

    $exitCode = [int]$LASTEXITCODE
    if (-not [string]::IsNullOrWhiteSpace($output)) {
        Add-Log $output.TrimEnd()
    }
    Add-Log "$Label の終了コード: $exitCode"
    if ($exitCode -ne 0) {
        throw [DistributionE2EException]::new($FailureCategory, "$Label が終了コード $exitCode で失敗しました。")
    }

    return [pscustomobject]@{
        ExitCode = $exitCode
        Output = $output
    }
}

function Quote-ProcessArgument {
    param(
        [Parameter(Mandatory)]
        [string]$Value
    )

    return '"' + $Value.Replace('"', '\"') + '"'
}

function Invoke-Msi {
    param(
        [Parameter(Mandatory)]
        [ValidateSet('install', 'uninstall')]
        [string]$Action,

        [Parameter(Mandatory)]
        [string]$PackagePath,

        [Parameter(Mandatory)]
        [string]$LogPath
    )

    if (-not (Test-Path -LiteralPath $msiexecPath -PathType Leaf)) {
        throw [DistributionE2EException]::new('TEST_HARNESS_FAILED', "msiexec.exe が見つかりません: $msiexecPath")
    }

    $arguments = "/$Action $(Quote-ProcessArgument $PackagePath) /qn /norestart /L*v $(Quote-ProcessArgument $LogPath)"
    Write-Phase "MSI $Action を実行します。timeout=${msiTimeoutSeconds}秒"
    $msiLogPaths.Add($LogPath)
    $process = $null
    try {
        $process = Start-Process -FilePath $msiexecPath -ArgumentList $arguments -PassThru
        if (-not $process.WaitForExit($msiTimeoutSeconds * 1000)) {
            try {
                $process.Kill($true)
                $process.WaitForExit(5000) | Out-Null
            }
            catch {
                Add-Log "MSI $Action の timeout 後のプロセス終了に失敗しました: $($_.Exception.Message)"
            }

            throw [DistributionE2EException]::new('TIMEOUT', "MSI $Action が $msiTimeoutSeconds 秒以内に終了しませんでした。")
        }

        $exitCode = $process.ExitCode
    }
    catch [DistributionE2EException] {
        throw
    }
    catch {
        Add-Log "MSI $Action の起動に失敗しました: $($_.Exception.Message)"
        throw [DistributionE2EException]::new('PRODUCT_INSTALL_FAILED', "MSI $Action の起動に失敗しました。")
    }

    Add-Log "MSI $Action の終了コード: $exitCode"
    if ($exitCode -ne 0) {
        throw [DistributionE2EException]::new('PRODUCT_INSTALL_FAILED', "MSI $Action が終了コード $exitCode で失敗しました。")
    }
}

function Get-ProjectVersion {
    [xml]$project = Get-Content -LiteralPath $publishProject -Raw -Encoding utf8
    $versions = @($project.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })
    if ($versions.Count -ne 1) {
        throw [DistributionE2EException]::new('TEST_HARNESS_FAILED', 'アプリケーションの Version を csproj から一意に取得できません。')
    }

    $version = [string]$versions[0]
    if ($version -notmatch '^\d+\.\d+\.\d+$') {
        throw [DistributionE2EException]::new('PRODUCT_BUILD_FAILED', "MSI に利用できない Version です: $version")
    }

    return $version
}

function Invoke-ComMethod {
    param(
        [Parameter(Mandatory)]
        [object]$Target,

        [Parameter(Mandatory)]
        [string]$Name,

        [object[]]$Arguments = @()
    )

    return $Target.GetType().InvokeMember(
        $Name,
        [System.Reflection.BindingFlags]::InvokeMethod,
        $null,
        $Target,
        $Arguments)
}

function Get-ComProperty {
    param(
        [Parameter(Mandatory)]
        [object]$Target,

        [Parameter(Mandatory)]
        [string]$Name,

        [object[]]$Arguments = @()
    )

    return $Target.GetType().InvokeMember(
        $Name,
        [System.Reflection.BindingFlags]::GetProperty,
        $null,
        $Target,
        $Arguments)
}

function Get-MsiProductVersion {
    param(
        [Parameter(Mandatory)]
        [string]$PackagePath
    )

    $installer = New-Object -ComObject WindowsInstaller.Installer
    $database = $null
    $view = $null
    $record = $null
    try {
        $database = Invoke-ComMethod -Target $installer -Name 'OpenDatabase' -Arguments @((Resolve-Path -LiteralPath $PackagePath).Path, 0)
        $view = Invoke-ComMethod -Target $database -Name 'OpenView' -Arguments @('SELECT `Value` FROM `Property` WHERE `Property` = ''ProductVersion''')
        Invoke-ComMethod -Target $view -Name 'Execute' | Out-Null
        $record = Invoke-ComMethod -Target $view -Name 'Fetch'
        if ($null -eq $record) {
            throw 'ProductVersion が MSI にありません。'
        }

        return [string](Get-ComProperty -Target $record -Name 'StringData' -Arguments @(1))
    }
    catch {
        throw [DistributionE2EException]::new('PRODUCT_BUILD_FAILED', "MSI の ProductVersion を取得できません: $($_.Exception.Message)")
    }
    finally {
        if ($null -ne $record) {
            [System.Runtime.InteropServices.Marshal]::ReleaseComObject($record) | Out-Null
        }
        if ($null -ne $view) {
            [System.Runtime.InteropServices.Marshal]::ReleaseComObject($view) | Out-Null
        }
        if ($null -ne $database) {
            [System.Runtime.InteropServices.Marshal]::ReleaseComObject($database) | Out-Null
        }
        [System.Runtime.InteropServices.Marshal]::ReleaseComObject($installer) | Out-Null
    }
}

function Get-InstalledProductEntries {
    $entries = foreach ($path in $uninstallRegistryPaths) {
        @(Get-ItemProperty -Path $path -ErrorAction SilentlyContinue |
            Where-Object {
                $displayNameProperty = $_.PSObject.Properties['DisplayName']
                $null -ne $displayNameProperty -and [string]$displayNameProperty.Value -like 'Squirrel Notifier*'
            } |
            ForEach-Object {
                [ordered]@{
                    displayName = [string]$_.PSObject.Properties['DisplayName'].Value
                    displayVersion = if ($null -ne $_.PSObject.Properties['DisplayVersion']) { [string]$_.PSObject.Properties['DisplayVersion'].Value } else { '' }
                }
            })
    }

    return @($entries)
}

function Get-TaskSnapshot {
    $task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    if ($null -eq $task) {
        return [ordered]@{
            exists = $false
            state = $null
            execute = $null
            arguments = $null
        }
    }

    $action = @($task.Actions) | Select-Object -First 1
    return [ordered]@{
        exists = $true
        state = [string]$task.State
        execute = if ($null -ne $action) { [string]$action.Execute } else { $null }
        arguments = if ($null -ne $action) { [string]$action.Arguments } else { $null }
    }
}

function Get-InstallationState {
    $marker = Get-ItemProperty -LiteralPath $markerRegistryPath -ErrorAction SilentlyContinue
    $processCount = @(Get-Process -Name $processName -ErrorAction SilentlyContinue).Count
    $installedProperty = if ($null -ne $marker) { $marker.PSObject.Properties['installed'] } else { $null }
    return [ordered]@{
        installRootExists = Test-Path -LiteralPath $installRoot -PathType Container
        executableExists = Test-Path -LiteralPath (Join-Path $installRoot 'SquirrelNotifier.WinUI3.exe') -PathType Leaf
        settingsExists = Test-Path -LiteralPath $settingsPath -PathType Container
        markerExists = $null -ne $marker
        markerInstalled = if ($null -ne $installedProperty) { [string]$installedProperty.Value } else { $null }
        task = Get-TaskSnapshot
        processCount = $processCount
        installedProducts = @(Get-InstalledProductEntries)
    }
}

function Get-RelativeInstalledFiles {
    if (-not (Test-Path -LiteralPath $installRoot -PathType Container)) {
        return @()
    }

    $rootWithSeparator = $installRoot.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    return @(
        Get-ChildItem -LiteralPath $installRoot -File -Recurse -ErrorAction SilentlyContinue |
            ForEach-Object { $_.FullName.Substring($rootWithSeparator.Length) }
    )
}

function Capture-FailureState {
    if (-not $preflightCompleted) {
        return
    }

    try {
        $script:failureState = [ordered]@{
            phase = 'on-failure'
            state = Get-InstallationState
            installedFiles = @(Get-RelativeInstalledFiles)
        }
    }
    catch {
        Add-Log "失敗時の配置状態取得に失敗しました: $($_.Exception.Message)"
    }
}

function Assert-NoPreexistingInstallation {
    $state = Get-InstallationState
    $findings = [System.Collections.Generic.List[string]]::new()
    if ($state.installRootExists) { $findings.Add('インストール先') }
    if ($state.settingsExists) { $findings.Add('%LOCALAPPDATA%\SquirrelNotifier の設定') }
    if ($state.markerExists) { $findings.Add('HKCU\\Software\\SquirrelNotifier') }
    if ($state.task.exists) { $findings.Add('Task Scheduler の Squirrel Notifier タスク') }
    if ($state.processCount -gt 0) { $findings.Add('SquirrelNotifier.WinUI3 プロセス') }
    if (@($state.installedProducts).Count -gt 0) { $findings.Add('アンインストール情報') }
    if ($findings.Count -ne 0) {
        throw [DistributionE2EException]::new(
            'PRODUCT_INSTALL_FAILED',
            "既存の Squirrel Notifier を変更しないため E2E を開始できません。検出対象: $($findings -join ', ')")
    }

    $script:preflightCompleted = $true
    $assertions.Add('既存インストール、レジストリ、タスク、プロセスがないことを確認した。')
}

function Assert-PublishedVersion {
    param(
        [Parameter(Mandatory)]
        [string]$ExecutablePath
    )

    $info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($ExecutablePath)
    $script:publishedFileVersion = [string]$info.FileVersion
    $script:publishedProductVersion = [string]$info.ProductVersion
    $versionPattern = '^' + [regex]::Escape($expectedVersion) + '(\.0)?([+].*)?$'
    if ($publishedFileVersion -notmatch $versionPattern -or $publishedProductVersion -notmatch $versionPattern) {
        throw [DistributionE2EException]::new(
            'PRODUCT_BUILD_FAILED',
            "publish payload の version が期待値と一致しません: expected=$expectedVersion, file=$publishedFileVersion, product=$publishedProductVersion")
    }

    $assertions.Add("publish payload の FileVersion / ProductVersion が $expectedVersion と一致した。")
}

function Assert-MsiInstalled {
    $state = Get-InstallationState
    if (-not $state.installRootExists -or -not $state.executableExists) {
        throw [DistributionE2EException]::new('PRODUCT_INSTALL_FAILED', "MSI の配置先が期待値と一致しません: $installRoot")
    }
    if (-not $state.markerExists -or $state.markerInstalled -ne '1') {
        throw [DistributionE2EException]::new('PRODUCT_INSTALL_FAILED', 'MSI の製品登録が見つかりません。')
    }

    $installedProduct = @($state.installedProducts) | Select-Object -First 1
    if ($null -eq $installedProduct -or $installedProduct.displayVersion -ne $expectedVersion) {
        throw [DistributionE2EException]::new('PRODUCT_INSTALL_FAILED', "アンインストール情報の version が期待値と一致しません: expected=$expectedVersion")
    }

    $installedInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $installRoot 'SquirrelNotifier.WinUI3.exe'))
    $installedVersionPattern = '^' + [regex]::Escape($expectedVersion) + '(\.0)?([+].*)?$'
    if ([string]$installedInfo.FileVersion -notmatch $installedVersionPattern -or [string]$installedInfo.ProductVersion -notmatch $installedVersionPattern) {
        throw [DistributionE2EException]::new(
            'PRODUCT_INSTALL_FAILED',
            "インストール後 EXE の FileVersion / ProductVersion が期待値と一致しません: file=$($installedInfo.FileVersion), product=$($installedInfo.ProductVersion)")
    }

    $observations.Add([ordered]@{
        phase = 'after-msi-install'
        state = $state
        installedFiles = @(Get-RelativeInstalledFiles)
    })
    $assertions.Add('MSI の silent install 後に配置先、EXE version、製品登録を確認した。')
    if ($state.task.exists) {
        $assertions.Add('MSI install 後の Task Scheduler 状態を記録した。')
    }
    else {
        $assertions.Add('MSI install 後に Task Scheduler タスクが残っていないことを確認した。')
    }
}

function Assert-MsiUninstalled {
    $state = Get-InstallationState
    if ($state.installRootExists -or $state.settingsExists -or $state.markerExists -or $state.task.exists -or $state.processCount -ne 0 -or @($state.installedProducts).Count -ne 0) {
        throw [DistributionE2EException]::new('PRODUCT_INSTALL_FAILED', 'MSI uninstall 後に設定、製品登録、ファイル、タスク、プロセスの残留を検出しました。')
    }

    $observations.Add([ordered]@{
        phase = 'after-msi-uninstall'
        state = $state
        installedFiles = @()
    })
    $assertions.Add('MSI の silent uninstall 後に製品登録、配置ファイル、タスク、プロセスがないことを確認した。')
}

function Assert-BundleContents {
    $expectedFiles = @(
        'SquirrelNotifier.WinUI3.exe',
        'install.ps1',
        'install.cmd',
        'uninstall.ps1',
        'uninstall.cmd',
        'create-shortcuts.ps1',
        'create-shortcuts.cmd'
    )
    foreach ($file in $expectedFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $bundleDirectory $file) -PathType Leaf)) {
            throw [DistributionE2EException]::new('PRODUCT_BUILD_FAILED', "setup ZIP に必須ファイルがありません: $file")
        }
        $requiredBundleFiles.Add($file)
    }

    Assert-PublishedVersion -ExecutablePath (Join-Path $bundleDirectory 'SquirrelNotifier.WinUI3.exe')
    $assertions.Add('setup ZIP に publish payload、install/uninstall script、shortcut script が含まれることを確認した。')
}

function Assert-BundleInstalled {
    $state = Get-InstallationState
    if (-not $state.task.exists) {
        throw [DistributionE2EException]::new('PRODUCT_INSTALL_FAILED', 'setup ZIP の install.ps1 実行後に Task Scheduler タスクがありません。')
    }

    $expectedExecutable = [System.IO.Path]::GetFullPath((Join-Path $bundleDirectory 'SquirrelNotifier.WinUI3.exe'))
    $actualExecutable = [System.IO.Path]::GetFullPath([string]$state.task.execute)
    if ($actualExecutable -ne $expectedExecutable) {
        throw [DistributionE2EException]::new('PRODUCT_INSTALL_FAILED', "setup ZIP の Task Scheduler 実行ファイルが一致しません: $actualExecutable")
    }

    $observations.Add([ordered]@{
        phase = 'after-bundle-install'
        state = $state
        installedFiles = @()
    })
    $assertions.Add('setup ZIP の install.ps1 が Task Scheduler に bundle の EXE を登録した。')
}

function Assert-BundleUninstalled {
    $state = Get-InstallationState
    if ($state.settingsExists -or $state.task.exists) {
        throw [DistributionE2EException]::new('PRODUCT_INSTALL_FAILED', 'setup ZIP の uninstall.ps1 実行後に設定または Task Scheduler タスクが残っています。')
    }

    $observations.Add([ordered]@{
        phase = 'after-bundle-uninstall'
        state = $state
        installedFiles = @()
    })
    $assertions.Add('setup ZIP の uninstall.ps1 実行後に Task Scheduler タスクがないことを確認した。')
}

function Write-ArtifactText {
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [AllowEmptyString()]
        [string]$Content
    )

    New-Item -ItemType Directory -Path $artifactsFullPath -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $artifactsFullPath $Name) -Value (Sanitize-Text $Content) -Encoding utf8
}

function Write-ArtifactJson {
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [object]$Value
    )

    Write-ArtifactText -Name $Name -Content ($Value | ConvertTo-Json -Depth 12)
}

function Invoke-TestCleanup {
    if (-not $preflightCompleted) {
        return
    }

    if ($bundleInstallAttempted) {
        $uninstallScript = Join-Path $bundleDirectory 'uninstall.ps1'
        if (Test-Path -LiteralPath $uninstallScript -PathType Leaf) {
            try {
                Invoke-ExternalCommand `
                    -FilePath 'pwsh' `
                    -Arguments @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $uninstallScript, '-KeepSettings', '-NonInteractive') `
                    -Label 'setup ZIP cleanup uninstall' `
                    -FailureCategory 'CLEANUP_FAILED' | Out-Null
            }
            catch {
                $cleanupErrors.Add($_.Exception.Message)
            }
        }
    }

    if ($msiInstallAttempted -and $null -ne $msiPath -and (Test-Path -LiteralPath $msiPath -PathType Leaf)) {
        try {
            Invoke-Msi -Action 'uninstall' -PackagePath $msiPath -LogPath (Join-Path $runRootFullPath 'logs\msi-cleanup-uninstall.log')
        }
        catch {
            $cleanupErrors.Add($_.Exception.Message)
        }
    }

    try {
        $task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
        if ($null -ne $task) {
            Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
            Add-Log 'cleanup でテストが作成した Task Scheduler タスクを解除しました。'
        }
    }
    catch {
        $cleanupErrors.Add("Task Scheduler cleanup に失敗しました: $($_.Exception.Message)")
    }

    try {
        $state = Get-InstallationState
        $script:finalState = $state
        if ($state.installRootExists -or $state.settingsExists -or $state.markerExists -or $state.task.exists -or $state.processCount -ne 0 -or @($state.installedProducts).Count -ne 0) {
            $cleanupErrors.Add('cleanup 後も設定、製品登録、ファイル、タスク、プロセス、アンインストール情報が残っています。')
        }
    }
    catch {
        $cleanupErrors.Add("cleanup 後の残留確認に失敗しました: $($_.Exception.Message)")
    }
}

try {
    New-Item -ItemType Directory -Path $runRootFullPath, $artifactsFullPath, (Join-Path $runRootFullPath 'logs') -Force | Out-Null
    Write-Phase '配布物 E2E を開始しました。'
    $expectedVersion = Get-ProjectVersion
    Write-Phase "期待 version=$expectedVersion"
    $dotnetVersionOutput = Invoke-ExternalCommand -FilePath 'dotnet' -Arguments @('--version') -Label 'dotnet version' -FailureCategory 'DEPENDENCY_ACQUISITION_FAILED'
    $dotnetVersion = $dotnetVersionOutput.Output.Trim()

    Write-Phase '既存インストール状態の preflight を開始します。'
    Assert-NoPreexistingInstallation

    Write-Phase 'アプリケーション restore を開始します。'
    Invoke-ExternalCommand `
        -FilePath 'dotnet' `
        -Arguments @('restore', $publishProject, '/p:IncludeWindowsSdkBuildTools=false') `
        -Label 'アプリケーション restore' `
        -FailureCategory 'DEPENDENCY_ACQUISITION_FAILED' | Out-Null
    Invoke-ExternalCommand `
        -FilePath 'dotnet' `
        -Arguments @(
            'publish', $publishProject,
            '--configuration', 'Release',
            '--runtime', 'win-x64',
            '--self-contained', 'true',
            '--output', $publishDirectory,
            '/p:Platform=x64',
            "/p:Version=$expectedVersion",
            '/p:PublishSingleFile=false',
            '/p:IncludeNativeLibrariesForSelfExtract=true',
            '/p:IncludeWindowsSdkBuildTools=false',
            '--no-restore'
        ) `
        -Label '実 publish payload build' `
        -FailureCategory 'PRODUCT_BUILD_FAILED' | Out-Null
    Write-Phase '実 publish payload build が完了しました。'

    $publishExecutable = Join-Path $publishDirectory 'SquirrelNotifier.WinUI3.exe'
    if (-not (Test-Path -LiteralPath $publishExecutable -PathType Leaf)) {
        throw [DistributionE2EException]::new('PRODUCT_BUILD_FAILED', "publish payload に EXE がありません: $publishExecutable")
    }
    $publishFileCount = @(Get-ChildItem -LiteralPath $publishDirectory -File -Recurse).Count
    Assert-PublishedVersion -ExecutablePath $publishExecutable

    New-Item -ItemType Directory -Path $msiOutputDirectory -Force | Out-Null
    Write-Phase 'MSI build を開始します。'
    Invoke-ExternalCommand `
        -FilePath 'dotnet' `
        -Arguments @(
            'build', $installerProject,
            '--configuration', 'Release',
            '/p:Platform=x64',
            "/p:HarvestPath=$publishDirectory",
            "/p:PackageVersion=$expectedVersion",
            "/p:OutputPath=$msiOutputDirectory"
        ) `
        -Label 'MSI build' `
        -FailureCategory 'PRODUCT_BUILD_FAILED' | Out-Null
    Write-Phase 'MSI build が完了しました。'

    $msiFiles = @(Get-ChildItem -LiteralPath $msiOutputDirectory -Filter '*.msi' -File -Recurse | Sort-Object FullName)
    if ($msiFiles.Count -eq 0) {
        throw [DistributionE2EException]::new('PRODUCT_BUILD_FAILED', "MSI が見つかりません: $msiOutputDirectory")
    }
    $msiPath = $msiFiles[0].FullName
    Invoke-ExternalCommand `
        -FilePath 'pwsh' `
        -Arguments @('-NoProfile', '-File', $majorUpgradeScript, '-MsiPath', $msiPath) `
        -Label 'MSI MajorUpgrade 検証' `
        -FailureCategory 'PRODUCT_BUILD_FAILED' | Out-Null
    $msiProductVersion = Get-MsiProductVersion -PackagePath $msiPath
    if ($msiProductVersion -ne $expectedVersion) {
        throw [DistributionE2EException]::new('PRODUCT_BUILD_FAILED', "MSI ProductVersion が期待値と一致しません: expected=$expectedVersion, actual=$msiProductVersion")
    }
    $assertions.Add("MSI の ProductVersion が $expectedVersion と一致した。")

    Write-Phase 'setup ZIP の生成と内容検証を開始します。'
    New-Item -ItemType Directory -Path $bundleSourceDirectory, $bundleDirectory -Force | Out-Null
    Copy-Item -Path (Join-Path $publishDirectory '*') -Destination $bundleSourceDirectory -Recurse -Force
    Copy-Item -LiteralPath @(
        (Join-Path $repoRoot 'scripts\install.ps1'),
        (Join-Path $repoRoot 'scripts\install.cmd'),
        (Join-Path $repoRoot 'scripts\uninstall.ps1'),
        (Join-Path $repoRoot 'scripts\uninstall.cmd'),
        (Join-Path $repoRoot 'scripts\create-shortcuts.ps1'),
        (Join-Path $repoRoot 'scripts\create-shortcuts.cmd')
    ) -Destination $bundleSourceDirectory -Force
    Compress-Archive -Path (Join-Path $bundleSourceDirectory '*') -DestinationPath $zipPath -Force
    Expand-Archive -LiteralPath $zipPath -DestinationPath $bundleDirectory -Force
    Assert-BundleContents
    Write-Phase 'setup ZIP の内容検証が完了しました。'

    $msiScenarioStarted = $true
    $msiInstallAttempted = $true
    Write-Phase 'MSI silent install の検証を開始します。'
    Invoke-Msi -Action 'install' -PackagePath $msiPath -LogPath (Join-Path $runRootFullPath 'logs\msi-install.log')
    Assert-MsiInstalled
    Write-Phase 'MSI silent install の検証が完了しました。'
    Invoke-Msi -Action 'uninstall' -PackagePath $msiPath -LogPath (Join-Path $runRootFullPath 'logs\msi-uninstall.log')
    Assert-MsiUninstalled
    Write-Phase 'MSI silent uninstall の検証が完了しました。'
    $msiInstallAttempted = $false

    $bundleScenarioStarted = $true
    $bundleInstallAttempted = $true
    Write-Phase 'setup ZIP install.ps1 の検証を開始します。'
    Invoke-ExternalCommand `
        -FilePath 'pwsh' `
        -Arguments @(
            '-NoProfile',
            '-ExecutionPolicy', 'Bypass',
            '-File', (Join-Path $bundleDirectory 'install.ps1'),
            '-ExePath', (Join-Path $bundleDirectory 'SquirrelNotifier.WinUI3.exe'),
            '-NonInteractive'
        ) `
        -Label 'setup ZIP install.ps1' `
        -FailureCategory 'PRODUCT_INSTALL_FAILED' | Out-Null
    Assert-BundleInstalled
    Write-Phase 'setup ZIP install.ps1 の検証が完了しました。'
    Invoke-ExternalCommand `
        -FilePath 'pwsh' `
        -Arguments @(
            '-NoProfile',
            '-ExecutionPolicy', 'Bypass',
            '-File', (Join-Path $bundleDirectory 'uninstall.ps1'),
            '-KeepSettings',
            '-NonInteractive'
        ) `
        -Label 'setup ZIP uninstall.ps1' `
        -FailureCategory 'PRODUCT_INSTALL_FAILED' | Out-Null
    $bundleInstallAttempted = $false
    Assert-BundleUninstalled
    Write-Phase 'setup ZIP uninstall.ps1 の検証が完了しました。'
}
catch [DistributionE2EException] {
    $failureCategory = $_.Exception.Category
    $failureMessage = $_.Exception.Message
    Capture-FailureState
    Add-Log "配布物 E2E に失敗しました: $failureCategory; $failureMessage"
}
catch {
    $failureCategory = 'TEST_HARNESS_FAILED'
    $failureMessage = "配布物 E2E harness が予期しない例外で終了しました: $($_.Exception.Message)"
    Capture-FailureState
    Add-Log $failureMessage
}
finally {
    $completedAt = [DateTimeOffset]::UtcNow
    try {
        Invoke-TestCleanup
    }
    catch {
        $cleanupErrors.Add("cleanup に失敗しました: $($_.Exception.Message)")
    }

    if ($cleanupErrors.Count -ne 0) {
        Add-Log ($cleanupErrors -join [Environment]::NewLine)
        if ($null -eq $failureCategory) {
            $failureCategory = 'CLEANUP_FAILED'
            $failureMessage = '配布物 E2E の cleanup に失敗しました。'
        }
    }

    try {
        if ($null -eq $failureCategory) {
            $assertions.Add('配布物 E2E の全シナリオが成功した。')
        }

        foreach ($logPath in @($msiLogPaths)) {
            if (Test-Path -LiteralPath $logPath -PathType Leaf) {
                Write-ArtifactText -Name ("msi-" + [System.IO.Path]::GetFileName($logPath)) -Content (Get-Content -LiteralPath $logPath -Raw -ErrorAction SilentlyContinue)
            }
        }
        Write-ArtifactText -Name 'sanitized.log' -Content ($logLines -join [Environment]::NewLine)
        Write-ArtifactJson -Name 'versions.json' -Value ([ordered]@{
            schemaVersion = 1
            phase = 'headless'
            scenarioId = $manifest.id
            commitSha = $env:GITHUB_SHA ?? 'local'
            components = $manifest.components
            os = [System.Environment]::OSVersion.VersionString
            runtime = [System.Runtime.InteropServices.RuntimeInformation]::FrameworkDescription
            dotnetVersion = $dotnetVersion
            expectedVersion = $expectedVersion
            productAssembly = $publishedProductVersion
            msiProductVersion = $msiProductVersion
        })
        Write-ArtifactJson -Name 'distribution.json' -Value ([ordered]@{
            schemaVersion = 1
            scenarioId = $manifest.id
            expectedVersion = $expectedVersion
            publishFileCount = $publishFileCount
            publishedFileVersion = $publishedFileVersion
            publishedProductVersion = $publishedProductVersion
            msiProductVersion = $msiProductVersion
            bundleRequiredFiles = @($requiredBundleFiles)
            observations = @($observations)
            failureState = $failureState
            finalState = $finalState
            assertions = @($assertions)
            cleanupErrors = @($cleanupErrors)
        })
        Write-ArtifactJson -Name 'distribution-cleanup.json' -Value ([ordered]@{
            schemaVersion = 1
            phase = 'headless'
            success = ($cleanupErrors.Count -eq 0)
            msiScenarioStarted = $msiScenarioStarted
            msiInstallAttempted = $msiInstallAttempted
            bundleScenarioStarted = $bundleScenarioStarted
            bundleInstallAttempted = $bundleInstallAttempted
            errors = @($cleanupErrors)
            completedAt = $completedAt
        })
        Write-ArtifactJson -Name 'result.json' -Value ([ordered]@{
            schemaVersion = 1
            phase = 'headless'
            scenarioId = $manifest.id
            fixture = $manifest.fixture
            expectedOutcome = $manifest.expectedOutcome
            outcome = if ($null -eq $failureCategory) { 'passed' } else { 'failed' }
            category = $failureCategory
            startedAt = $startedAt
            completedAt = $completedAt
            durationMilliseconds = [Math]::Max(0, ($completedAt - $startedAt).TotalMilliseconds)
            assertions = @($assertions)
        })
        if ($null -ne $failureCategory) {
            Write-ArtifactJson -Name 'failure.json' -Value ([ordered]@{
                schemaVersion = 1
                phase = 'headless'
                scenarioId = $manifest.id
                category = $failureCategory
                component = 'squirrel-notifier'
                message = Sanitize-Text $failureMessage
                startedAt = $startedAt
                completedAt = $completedAt
                durationMilliseconds = [Math]::Max(0, ($completedAt - $startedAt).TotalMilliseconds)
                artifactHints = @('sanitized.log', 'distribution.json', 'distribution-cleanup.json', 'versions.json', 'msi-msi-install.log', 'msi-msi-uninstall.log')
            })
        }
    }
    catch {
        $failureCategory = 'TEST_HARNESS_FAILED'
        $failureMessage = "配布物 E2E artifact の生成に失敗しました: $($_.Exception.Message)"
        Add-Log $failureMessage
        try {
            Write-ArtifactText -Name 'sanitized.log' -Content ($logLines -join [Environment]::NewLine)
        }
        catch {
            Write-Verbose 'artifact 生成失敗後のログ保存にも失敗しました。'
        }
    }
}

if ($null -ne $failureCategory) {
    Write-Error "配布物 E2E 失敗: category=$failureCategory; message=$failureMessage"
    exit 1
}

Write-Host '配布物 E2E 成功: distribution-install'
exit 0
