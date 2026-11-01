<#
.SYNOPSIS
    製品識別子を隔離した実 MSI で、高 FileVersion からの更新と初回 WinUI 起動を検証します。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublishDirectory,
    [Parameter(Mandatory)][string]$RunRoot,
    [Parameter(Mandatory)][string]$ArtifactsDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
. (Join-Path $repoRoot 'scripts\Assert-InstalledPayload.ps1')
$payload = (Resolve-Path -LiteralPath $PublishDirectory).Path
$run = Join-Path ([IO.Path]::GetFullPath($RunRoot)) ('msi-upgrade-' + [guid]::NewGuid().ToString('N'))
$identity = 'SquirrelNotifier-E2E-' + [guid]::NewGuid().ToString('N')
$displayName = $identity.Replace('SquirrelNotifier', 'Squirrel Notifier')
$installRoot = Join-Path $env:LOCALAPPDATA "Programs\$identity"
$registryPath = "HKCU:\Software\$identity"
$shortcutRoot = Join-Path ([Environment]::GetFolderPath('Programs')) $displayName
$dataRoot = Join-Path $run 'data'
$realSettings = Join-Path $env:LOCALAPPDATA 'SquirrelNotifier\settings.json'
$realHash = if (Test-Path -LiteralPath $realSettings) { (Get-FileHash -LiteralPath $realSettings).Hash } else { $null }
$log = [Collections.Generic.List[string]]::new()
$observations = [Collections.Generic.List[object]]::new()
$productCodes = @{}
$failure = $null
$cleanupErrors = [Collections.Generic.List[string]]::new()
$project = Join-Path $run 'installer\SquirrelNotifier.Installer.wixproj'
$source = Join-Path $run 'installer\Package.wxs'
$fixtureProject = Join-Path $run 'fixture\Fixture.csproj'
$appName = 'SquirrelNotifier.WinUI3.exe'
$dllNames = @('Microsoft.WinUI.dll', 'Microsoft.InteractiveExperiences.Projection.dll')

function Invoke-Checked([string]$Command, [string[]]$Arguments) {
    $output = & $Command @Arguments 2>&1 | Out-String
    $code = $LASTEXITCODE
    $log.Add($output)
    if ($code -ne 0) { throw "$Command が終了コード $code で失敗しました。`n$output" }
}

function Get-ProductCode([string]$Path) {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $database = $null
    $view = $null
    $record = $null
    try {
        $database = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @($Path, 0))
        $view = $database.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $database,
            @('SELECT `Value` FROM `Property` WHERE `Property` = ''ProductCode'''))
        $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, @()) | Out-Null
        $record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, @())
        return $record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, @(1))
    }
    finally {
        foreach ($object in @($record, $view, $database, $installer)) {
            if ($null -ne $object) { [Runtime.InteropServices.Marshal]::ReleaseComObject($object) | Out-Null }
        }
    }
}

function Invoke-Msi([string]$Path, [string]$Phase, [switch]$Uninstall) {
    $msiLog = Join-Path $run "$Phase.log"
    $action = if ($Uninstall) { '/x' } else { '/i' }
    $process = Start-Process -FilePath "$env:SystemRoot\System32\msiexec.exe" -WindowStyle Hidden -PassThru `
        -ArgumentList @($action, "`"$Path`"", '/qn', '/norestart', '/L*v', "`"$msiLog`"")
    try {
        if (-not $process.WaitForExit(180000)) {
            throw "MSI $Phase が180秒以内に終了しませんでした。インストーラーの完了確認が必要です。"
        }
        if ($process.ExitCode -ne 0) { throw "MSI $Phase が終了コード $($process.ExitCode) で失敗しました。" }
    }
    finally { $process.Dispose() }
    return $msiLog
}

function Build-Msi([string]$Name, [string]$Version, [string]$Directory, [string]$Definition) {
    [IO.File]::WriteAllText($source, $Definition)
    $output = Join-Path $run $Name
    Invoke-Checked 'dotnet' @('build', $project, '-t:Rebuild', '-c', 'Release', '-v', 'minimal',
        '-p:Platform=x64', "-p:HarvestPath=$Directory", "-p:PackageVersion=$Version", "-p:OutputPath=$output")
    $msi = @(Get-ChildItem -LiteralPath $output -Filter '*.msi' -File)
    if ($msi.Count -ne 1) { throw "MSI が一意に生成されませんでした: $output" }
    $productCodes[$msi[0].FullName] = Get-ProductCode $msi[0].FullName
    return $msi[0].FullName
}

function New-OldPayload([string]$Name, [string]$FileVersion) {
    $output = Join-Path $run "$Name-dll"
    Invoke-Checked 'dotnet' @('build', $fixtureProject, '-c', 'Release', '-v', 'minimal',
        "-p:FileVersion=$FileVersion", '-o', $output)
    $directory = Join-Path $run "$Name-payload"
    New-Item -ItemType Directory -Path $directory | Out-Null
    Copy-Item -LiteralPath (Join-Path $payload $appName) -Destination $directory
    foreach ($fileName in $dllNames) {
        Copy-Item -LiteralPath (Join-Path $output 'Fixture.dll') -Destination (Join-Path $directory $fileName)
        $old = [version][Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $directory $fileName)).FileVersion
        $new = [version][Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $payload $fileName)).FileVersion
        if (($Name -eq 'high' -and $old -le $new) -or ($Name -eq 'low' -and $old -ge $new)) {
            throw "再現用 DLL の大小関係が不正です: $fileName old=$old new=$new"
        }
        $observations.Add([ordered]@{ phase = "$Name-fixture"; file = $fileName; oldVersion = "$old"; newVersion = "$new" })
    }
    return $directory
}

function Get-TaskXml {
    return Export-ScheduledTask -TaskName $displayName
}

function Set-PreservationMarkers {
    # 製品既定と異なる条件・引数を保存し、更新時の /Create /F を検出する。
    $action = New-ScheduledTaskAction -Execute (Join-Path $installRoot $appName) -Argument '--tray -t'
    $trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddDays(7)
    $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 23)
    Register-ScheduledTask -TaskName $displayName -Action $action -Trigger $trigger -Settings $settings -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $dataRoot 'settings.json'), '{"AutoReviewStartEnabled":false,"customMarker":"msi-preserve"}')
    [IO.File]::WriteAllText((Join-Path $dataRoot 'review-cycles.json'), '{"msi-preserve":true}')
    return Get-PreservationSnapshot
}

function Get-PreservationSnapshot {
    return [ordered]@{
        task = Get-TaskXml
        settings = (Get-FileHash -LiteralPath (Join-Path $dataRoot 'settings.json')).Hash
        cycles = (Get-FileHash -LiteralPath (Join-Path $dataRoot 'review-cycles.json')).Hash
    } | ConvertTo-Json -Compress
}

function Assert-Preserved([string]$Before) {
    if ((Get-PreservationSnapshot) -cne $Before) { throw 'MSI 更新で設定・専用データまたはタスク条件が変わりました。' }
}

function Assert-WinUiLaunch([string]$Phase) {
    $launchRoot = Join-Path $run "launch-$Phase"
    New-Item -ItemType Directory -Path $launchRoot | Out-Null
    # 実 subscriber・認証・外部接続を起動しない。公開状態が作られるのは WinUI の OnLaunched 後。
    @{
        SubscriberCommandPath = (Join-Path $launchRoot 'absent-subscriber.exe')
        GatewayUrl = 'http://127.0.0.1:1/mcp'
        AutoReviewStartEnabled = $false
        SessionResumeEnabled = $false
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $launchRoot 'settings.json') -Encoding utf8NoBOM
    $info = [Diagnostics.ProcessStartInfo]::new((Join-Path $installRoot $appName))
    $info.ArgumentList.Add('--tray')
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.Environment['SQUIRREL_NOTIFIER_DATA_ROOT'] = $launchRoot
    $info.Environment.Remove('MCP_PROBE_AUTH_TOKEN') | Out-Null
    $process = [Diagnostics.Process]::Start($info)
    try {
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
        $statusFile = Join-Path $launchRoot 'review-status.json'
        while (-not (Test-Path -LiteralPath $statusFile)) {
            if ($process.HasExited) { throw "更新後の WinUI 初回起動が失敗しました: $Phase exit=$($process.ExitCode)" }
            if ([DateTimeOffset]::UtcNow -ge $deadline) { throw "WinUI 起動後の公開状態が30秒以内に作られませんでした: $Phase" }
            Start-Sleep -Milliseconds 200
        }
        $status = Get-Content -LiteralPath $statusFile -Raw | ConvertFrom-Json
        if ($status.schemaVersion -ne 1 -or [DateTimeOffset]$status.updatedAt -lt $deadline.AddSeconds(-30)) {
            throw "WinUI 起動後の公開状態が不正です: $Phase"
        }
        if ($process.WaitForExit(2000)) { throw "WinUI 起動直後に終了しました: $Phase exit=$($process.ExitCode)" }
        $observations.Add([ordered]@{ phase = $Phase; winUiLaunched = $true; statusSchemaVersion = $status.schemaVersion })
    }
    finally {
        if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit(10000) | Out-Null }
        $process.Dispose()
    }
}

try {
    New-Item -ItemType Directory -Path (Join-Path $run 'installer'), (Join-Path $run 'fixture'), $dataRoot, $ArtifactsDirectory -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repoRoot 'winui3\SquirrelNotifier.Installer\SquirrelNotifier.Installer.wixproj') -Destination $project
    Copy-Item -LiteralPath (Join-Path $repoRoot 'winui3\Directory.Packages.props') -Destination (Join-Path $run 'Directory.Packages.props')
    '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>' |
        Set-Content -LiteralPath $fixtureProject -Encoding utf8NoBOM
    $definition = [IO.File]::ReadAllText((Join-Path $repoRoot 'winui3\SquirrelNotifier.Installer\Package.wxs'))
    $definition = $definition.Replace('F7E8D9C0-B1A2-4C3D-8E5F-9A7B6C4D3E2F', [guid]::NewGuid().ToString())
    $definition = $definition.Replace('Squirrel Notifier', $displayName).Replace('Name="SquirrelNotifier"', "Name=`"$identity`"")
    $definition = $definition.Replace('Key="Software\SquirrelNotifier"', "Key=`"Software\$identity`"")
    $highPayload = New-OldPayload 'high' '65535.65535.65535.65535'
    $lowPayload = New-OldPayload 'low' '0.0.0.1'
    $highMsi = Build-Msi 'high-msi' '0.0.1' $highPayload $definition
    $lowMsi = Build-Msi 'low-msi' '0.0.1' $lowPayload $definition
    $fixedMsi = Build-Msi 'fixed-msi' '1.0.0' $payload $definition
    $rebuiltMsi = Build-Msi 'rebuilt-msi' '1.0.0' $payload $definition
    if ($productCodes[$fixedMsi] -eq $productCodes[$rebuiltMsi]) {
        throw '同版置換を検証する2つの MSI の ProductCode が同じです。'
    }
    # ファイル選択規則だけを外した対照 MSI で、試験が実際に欠落を検出することも固定する。
    $controlDefinition = [regex]::Replace($definition, '<SetProperty Id="REINSTALLMODE"[\s\S]*?/>', '')
    if ($controlDefinition -ceq $definition) { throw '対照 MSI 用の REINSTALLMODE 設定を特定できません。' }
    $controlMsi = Build-Msi 'control-msi' '1.0.0' $payload $controlDefinition

    Invoke-Msi $highMsi 'control-old-install' | Out-Null
    $controlLog = Invoke-Msi $controlMsi 'control-upgrade'
    $missing = @($dllNames | Where-Object { -not (Test-Path -LiteralPath (Join-Path $installRoot $_)) })
    if ($missing.Count -ne 2 -or (Get-Content -LiteralPath $controlLog -Raw) -notmatch 'higher versioned keyfile exists') {
        throw '対照 MSI が想定した高 FileVersion による2DLL欠落を再現しませんでした。'
    }
    $observations.Add([ordered]@{ phase = 'negative-control'; missingFiles = $missing; reproduced = $true })
    Invoke-Msi $controlMsi 'control-uninstall' -Uninstall | Out-Null

    foreach ($case in @(
        @{ name = 'higher-file-version'; old = $highMsi }
        @{ name = 'normal-upgrade'; old = $lowMsi }
    )) {
        $phase = $case.name
        Invoke-Msi $case.old "$phase-old-install" | Out-Null
        $before = Set-PreservationMarkers
        Invoke-Msi $fixedMsi "$phase-upgrade" | Out-Null
        Assert-Preserved $before
        $count = Assert-InstalledPayload -PayloadDirectory $payload -InstallDirectory $installRoot
        Assert-WinUiLaunch $phase
        $observations.Add([ordered]@{ phase = $phase; payloadFiles = $count; hashesMatch = $true; preserved = $true })
        Invoke-Msi $fixedMsi "$phase-reinstall" | Out-Null
        Assert-Preserved $before
        Assert-InstalledPayload -PayloadDirectory $payload -InstallDirectory $installRoot | Out-Null
        Assert-WinUiLaunch "$phase-reinstall"
        Invoke-Msi $rebuiltMsi "$phase-same-version-upgrade" | Out-Null
        Assert-Preserved $before
        Assert-InstalledPayload -PayloadDirectory $payload -InstallDirectory $installRoot | Out-Null
        Assert-WinUiLaunch "$phase-same-version"
        $observations.Add([ordered]@{ phase = "$phase-reinstall-and-same-version"; hashesMatch = $true; preserved = $true })
        Invoke-Msi $rebuiltMsi "$phase-uninstall" -Uninstall | Out-Null
    }
}
catch { $failure = $_.Exception.Message }
finally {
    # 更新途中に失敗して旧製品が残る場合も、試行した隔離製品の登録状態から後始末する。
    $installer = New-Object -ComObject WindowsInstaller.Installer
    try {
        foreach ($package in @($productCodes.Keys)) {
            try {
                $state = $installer.GetType().InvokeMember('ProductState', 'GetProperty', $null, $installer, @($productCodes[$package]))
                if ($state -eq 5) { Invoke-Msi $package ('cleanup-' + [IO.Path]::GetFileName((Split-Path $package))) -Uninstall | Out-Null }
                $state = $installer.GetType().InvokeMember('ProductState', 'GetProperty', $null, $installer, @($productCodes[$package]))
                if ($state -ne -1) { throw "隔離製品の登録が残っています: state=$state" }
            }
            catch { $cleanupErrors.Add($_.Exception.Message) }
        }
    }
    finally { [Runtime.InteropServices.Marshal]::ReleaseComObject($installer) | Out-Null }
    foreach ($path in @($installRoot, $registryPath, $shortcutRoot)) {
        if (Test-Path -LiteralPath $path) { $cleanupErrors.Add("隔離 MSI の残留があります: $path") }
    }
    if ($null -ne (Get-ScheduledTask -TaskName $displayName -ErrorAction SilentlyContinue)) {
        $cleanupErrors.Add('隔離 MSI のタスクが残っています。')
    }
    $afterHash = if (Test-Path -LiteralPath $realSettings) { (Get-FileHash -LiteralPath $realSettings).Hash } else { $null }
    if ($realHash -cne $afterHash) { $cleanupErrors.Add('実設定の hash が変わりました。') }
    $result = [ordered]@{
        success = ($null -eq $failure -and $cleanupErrors.Count -eq 0)
        isolated = $true
        realSettingsUnchanged = ($realHash -ceq $afterHash)
        observations = @($observations)
        failure = $failure
        cleanupErrors = @($cleanupErrors)
    }
    $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $ArtifactsDirectory 'msi-upgrade.json') -Encoding utf8NoBOM
    foreach ($file in @(Get-ChildItem -LiteralPath $run -Filter '*.log' -File)) {
        $log.Add("--- $($file.Name) ---")
        $log.Add([IO.File]::ReadAllText($file.FullName))
    }
    $text = [regex]::Replace(($log -join "`n"), '(?i)\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b', '<redacted-id>')
    [IO.File]::WriteAllText((Join-Path $ArtifactsDirectory 'msi-upgrade.log'), $text)
}
if (-not $result.success) { throw "MSI upgrade E2E が失敗しました: $failure $($cleanupErrors -join '; ')" }
Write-Host 'MSI upgrade E2E: 対照で欠落再現、修正版で全 payload・WinUI 起動・設定/タスク保持・cleanup 成功。'
