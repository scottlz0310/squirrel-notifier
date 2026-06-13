# WSL Kernel Update Notifier - Windows完結型プロトタイプ
# 単一PowerShellスクリプトでWSLカーネル更新を監視・通知

param(
    [string]$ConfigPath = "$env:TEMP\wsl-kernel-notifier-config.json",
    [switch]$Install,
    [switch]$Uninstall,
    [switch]$Test,
    [switch]$RunTests,
    [switch]$TestAll
)

# 設定
$Config = @{
    Repository = "microsoft/WSL2-Linux-Kernel"
    CheckIntervalHours = 24
    LogPath = "$env:TEMP\wsl-kernel-notifier.log"
    TaskName = "WSL Kernel Update Notifier"
}

function Write-Log {
    param([string]$Message, [string]$Level = "INFO")
    $Timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $LogEntry = "[$Timestamp] [$Level] $Message"
    Add-Content -Path $Config.LogPath -Value $LogEntry
    Write-Host $LogEntry
}

function Get-WSLKernelVersion {
    try {
        $Result = wsl.exe uname -r 2>$null
        if ($LASTEXITCODE -eq 0) {
            Write-Log "現在のWSLカーネル: $Result"
            return $Result.Trim()
        }
        throw "WSL not available"
    }
    catch {
        Write-Log "WSLカーネルバージョン取得失敗: $_" "ERROR"
        return $null
    }
}

function Get-LatestKernelVersion {
    try {
        $Uri = "https://api.github.com/repos/$($Config.Repository)/releases/latest"
        $Response = Invoke-RestMethod -Uri $Uri -Method Get
        $Version = $Response.tag_name
        Write-Log "最新カーネルバージョン: $Version"
        return $Version
    }
    catch {
        Write-Log "GitHub API呼び出し失敗: $_" "ERROR"
        return $null
    }
}

function Compare-KernelVersions {
    param([string]$Current, [string]$Latest)

    if (-not $Current -or -not $Latest) {
        return $false
    }

    # バージョン文字列から数値部分を抽出
    $CurrentVersion = [regex]::Match($Current, '(\d+\.\d+\.\d+\.\d+)').Groups[1].Value
    $LatestVersion = [regex]::Match($Latest, '(\d+\.\d+\.\d+\.\d+)').Groups[1].Value

    if (-not $CurrentVersion -or -not $LatestVersion) {
        Write-Log "バージョン解析失敗: Current=$Current, Latest=$Latest" "WARN"
        return $false
    }

    try {
        $CurrentVer = [System.Version]$CurrentVersion
        $LatestVer = [System.Version]$LatestVersion
        $IsNewer = $LatestVer -gt $CurrentVer
        Write-Log "バージョン比較: $CurrentVersion vs $LatestVersion = $IsNewer"
        return $IsNewer
    }
    catch {
        Write-Log "バージョン比較エラー: $_" "ERROR"
        return $false
    }
}

function Send-UpdateNotification {
    param([string]$CurrentVersion, [string]$LatestVersion)

    try {
        # BurntToastが利用可能かチェック
        if (-not (Get-Module -ListAvailable -Name BurntToast)) {
            Write-Log "BurntToastモジュールが見つかりません。標準通知を使用します。" "WARN"
            # Windows標準の通知（簡易版）
            Add-Type -AssemblyName System.Windows.Forms
            [System.Windows.Forms.MessageBox]::Show(
                "WSL2カーネルの新しいバージョンが利用可能です。`n現在: $CurrentVersion`n最新: $LatestVersion",
                "WSL Kernel Update",
                [System.Windows.Forms.MessageBoxButtons]::OK,
                [System.Windows.Forms.MessageBoxIcon]::Information
            )
            return
        }

        # BurntToastを使用した高度な通知
        Import-Module BurntToast
        $Text1 = New-BTText -Content "WSL2カーネル更新通知"
        $Text2 = New-BTText -Content "新しいバージョンが利用可能です"
        $Text3 = New-BTText -Content "現在: $CurrentVersion → 最新: $LatestVersion"
        $Binding = New-BTBinding -Children $Text1, $Text2, $Text3
        $Visual = New-BTVisual -BindingGeneric $Binding
        $Content = New-BTContent -Visual $Visual

        Submit-BTNotification -Content $Content
        Write-Log "通知を送信しました: $CurrentVersion → $LatestVersion"
    }
    catch {
        Write-Log "通知送信失敗: $_" "ERROR"
    }
}

function Install-TaskScheduler {
    try {
        # pwshの存在確認（フォールバックとしてpowershell.exeを使用）
        $Executor = if (Get-Command pwsh.exe -ErrorAction SilentlyContinue) { "pwsh.exe" } else { "PowerShell.exe" }
        Write-Log "タスクスケジューラ実行環境: $Executor"

        $Action = New-ScheduledTaskAction -Execute $Executor -Argument "-File `"$PSCommandPath`""
        $Trigger = New-ScheduledTaskTrigger -Once -At (Get-Date) -RepetitionInterval (New-TimeSpan -Hours 2) -RepetitionDuration (New-TimeSpan -Days 9999)
        $Settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
        $Principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive

        Register-ScheduledTask -TaskName $Config.TaskName -Action $Action -Trigger $Trigger -Settings $Settings -Principal $Principal -Force
        Write-Log "タスクスケジューラに登録しました: $($Config.TaskName) (実行環境: $Executor)"

        # BurntToastのインストール確認
        if (-not (Get-Module -ListAvailable -Name BurntToast)) {
            Write-Log "BurntToastモジュールをインストールしています..."
            Install-Module -Name BurntToast -Scope CurrentUser -Force
        }
    }
    catch {
        Write-Log "タスクスケジューラ登録失敗: $_" "ERROR"
    }
}

function Uninstall-TaskScheduler {
    try {
        Unregister-ScheduledTask -TaskName $Config.TaskName -Confirm:$false
        Write-Log "タスクスケジューラから削除しました: $($Config.TaskName)"
    }
    catch {
        Write-Log "タスクスケジューラ削除失敗: $_" "ERROR"
    }
}

function Test-Notification {
    Write-Log "テスト通知を送信します..."
    Send-UpdateNotification -CurrentVersion "5.15.90.1-microsoft-standard-WSL2" -LatestVersion "5.15.95.1-microsoft-standard-WSL2"
}

# テスト関数群
function Test-VersionComparison {
    Write-Host "=== バージョン比較テスト ===" -ForegroundColor Cyan
    $TestCases = @(
        @{ Current = "5.15.90.1-microsoft-standard-WSL2"; Latest = "linux-msft-wsl-5.15.95.1"; Expected = $true },
        @{ Current = "5.15.95.1-microsoft-standard-WSL2"; Latest = "linux-msft-wsl-5.15.90.1"; Expected = $false },
        @{ Current = "5.15.90.1-microsoft-standard-WSL2"; Latest = "linux-msft-wsl-5.15.90.1"; Expected = $false },
        @{ Current = "6.0.0.1-microsoft-standard-WSL2"; Latest = "linux-msft-wsl-5.15.95.1"; Expected = $false }
    )

    $PassCount = 0
    foreach ($Case in $TestCases) {
        $Result = Compare-KernelVersions -Current $Case.Current -Latest $Case.Latest
        $Status = if ($Result -eq $Case.Expected) { "PASS"; $PassCount++ } else { "FAIL" }
        $Color = if ($Status -eq "PASS") { "Green" } else { "Red" }
        Write-Host "  [$Status] $($Case.Current) vs $($Case.Latest) = $Result (期待値: $($Case.Expected))" -ForegroundColor $Color
    }
    Write-Host "バージョン比較テスト: $PassCount/$($TestCases.Count) 通過" -ForegroundColor $(if ($PassCount -eq $TestCases.Count) { "Green" } else { "Yellow" })
    return $PassCount -eq $TestCases.Count
}

function Test-WSLConnection {
    Write-Host "=== WSL接続テスト ===" -ForegroundColor Cyan
    try {
        $Version = Get-WSLKernelVersion
        if ($Version) {
            Write-Host "  [PASS] WSL接続成功: $Version" -ForegroundColor Green
            return $true
        } else {
            Write-Host "  [FAIL] WSLバージョン取得失敗" -ForegroundColor Red
            return $false
        }
    }
    catch {
        Write-Host "  [FAIL] WSL接続エラー: $_" -ForegroundColor Red
        return $false
    }
}

function Test-GitHubAPI {
    Write-Host "=== GitHub API接続テスト ===" -ForegroundColor Cyan
    try {
        $Version = Get-LatestKernelVersion
        if ($Version) {
            Write-Host "  [PASS] GitHub API接続成功: $Version" -ForegroundColor Green
            return $true
        } else {
            Write-Host "  [FAIL] GitHub APIバージョン取得失敗" -ForegroundColor Red
            return $false
        }
    }
    catch {
        Write-Host "  [FAIL] GitHub API接続エラー: $_" -ForegroundColor Red
        return $false
    }
}

function Test-NotificationSystem {
    Write-Host "=== 通知システムテスト ===" -ForegroundColor Cyan
    try {
        # BurntToast利用可能性チェック
        $HasBurntToast = Get-Module -ListAvailable -Name BurntToast
        if ($HasBurntToast) {
            Write-Host "  [PASS] BurntToastモジュール利用可能" -ForegroundColor Green
        } else {
            Write-Host "  [INFO] BurntToastモジュール未インストール（標準通知を使用）" -ForegroundColor Yellow
        }

        # 通知テスト実行
        Write-Host "  [INFO] テスト通知を送信中..." -ForegroundColor Blue
        Send-UpdateNotification -CurrentVersion "5.15.90.1-test" -LatestVersion "5.15.95.1-test"
        Write-Host "  [PASS] 通知送信完了" -ForegroundColor Green
        return $true
    }
    catch {
        Write-Host "  [FAIL] 通知システムエラー: $_" -ForegroundColor Red
        return $false
    }
}

function Test-TaskScheduler {
    Write-Host "=== タスクスケジューラテスト ===" -ForegroundColor Cyan
    try {
        # 既存タスクの確認
        $ExistingTask = Get-ScheduledTask -TaskName $Config.TaskName -ErrorAction SilentlyContinue
        if ($ExistingTask) {
            Write-Host "  [INFO] タスクが既に登録されています: $($Config.TaskName)" -ForegroundColor Blue
            Write-Host "  [PASS] タスクスケジューラ接続成功" -ForegroundColor Green
            return $true
        } else {
            Write-Host "  [INFO] タスクが未登録です" -ForegroundColor Yellow
            Write-Host "  [PASS] タスクスケジューラ接続成功" -ForegroundColor Green
            return $true
        }
    }
    catch {
        Write-Host "  [FAIL] タスクスケジューラエラー: $_" -ForegroundColor Red
        return $false
    }
}

function Test-LogSystem {
    Write-Host "=== ログシステムテスト ===" -ForegroundColor Cyan
    try {
        $TestMessage = "テストログメッセージ - $(Get-Date)"
        Write-Log $TestMessage "TEST"

        if (Test-Path $Config.LogPath) {
            $LogContent = Get-Content $Config.LogPath -Tail 1
            if ($LogContent -like "*$TestMessage*") {
                Write-Host "  [PASS] ログ書き込み成功: $($Config.LogPath)" -ForegroundColor Green
                return $true
            }
        }
        Write-Host "  [FAIL] ログ書き込み失敗" -ForegroundColor Red
        return $false
    }
    catch {
        Write-Host "  [FAIL] ログシステムエラー: $_" -ForegroundColor Red
        return $false
    }
}

function Invoke-AllTests {
    Write-Host "`n🧪 WSL Kernel Update Notifier - 機能テスト実行" -ForegroundColor Magenta
    Write-Host ("=" * 50) -ForegroundColor Magenta

    $TestResults = @()
    $TestResults += Test-LogSystem
    $TestResults += Test-VersionComparison
    $TestResults += Test-WSLConnection
    $TestResults += Test-GitHubAPI
    $TestResults += Test-NotificationSystem
    $TestResults += Test-TaskScheduler

    $PassCount = ($TestResults | Where-Object { $_ -eq $true }).Count
    $TotalCount = $TestResults.Count

    Write-Host ("`n" + ("=" * 50)) -ForegroundColor Magenta
    Write-Host "📊 テスト結果: $PassCount/$TotalCount 通過" -ForegroundColor $(if ($PassCount -eq $TotalCount) { "Green" } else { "Yellow" })

    if ($PassCount -eq $TotalCount) {
        Write-Host "✅ すべてのテストが通過しました！" -ForegroundColor Green
    } else {
        Write-Host "⚠️  一部のテストが失敗しました。上記の詳細を確認してください。" -ForegroundColor Yellow
    }

    return $PassCount -eq $TotalCount
}

# メイン処理
function Main {
    Write-Log "WSL Kernel Update Notifier 開始"

    if ($Install) {
        Install-TaskScheduler
        return
    }

    if ($Uninstall) {
        Uninstall-TaskScheduler
        return
    }

    if ($Test) {
        Test-Notification
        return
    }

    if ($RunTests -or $TestAll) {
        $TestResult = Invoke-AllTests
        if ($TestAll) {
            # テスト後に通常処理も実行
            Write-Host "\n⚙️  テスト完了。通常処理を続行します..." -ForegroundColor Blue
        } else {
            return
        }
    }

    # 通常のチェック処理
    $CurrentVersion = Get-WSLKernelVersion
    if (-not $CurrentVersion) {
        Write-Log "WSLが利用できません。処理を終了します。" "ERROR"
        return
    }

    $LatestVersion = Get-LatestKernelVersion
    if (-not $LatestVersion) {
        Write-Log "最新バージョンの取得に失敗しました。処理を終了します。" "ERROR"
        return
    }

    if (Compare-KernelVersions -Current $CurrentVersion -Latest $LatestVersion) {
        Write-Log "新しいバージョンが検出されました。通知を送信します。"
        Send-UpdateNotification -CurrentVersion $CurrentVersion -LatestVersion $LatestVersion
    } else {
        Write-Log "最新バージョンです。通知は不要です。"
    }

    Write-Log "WSL Kernel Update Notifier 完了"
}

# スクリプト実行
Main

<#
.SYNOPSIS
WSL Kernel Update Notifier - Windows完結型プロトタイプ

.DESCRIPTION
単一PowerShellスクリプトでWSLカーネル更新を監視・通知

.PARAMETER Install
タスクスケジューラに登録し、BurntToastをインストール

.PARAMETER Uninstall
タスクスケジューラから削除

.PARAMETER Test
テスト通知を送信

.PARAMETER RunTests
機能テストを実行

.PARAMETER TestAll
機能テスト実行後、通常処理も実行

.EXAMPLE
.\windows-only-prototype.ps1 -Install
インストールし、現在時刻より2時間おきに自動実行するよう設定

.EXAMPLE
.\windows-only-prototype.ps1 -RunTests
機能テストを実行

.EXAMPLE
.\windows-only-prototype.ps1
通常のバージョンチェックと通知を実行
#>
