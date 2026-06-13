<#
.SYNOPSIS
    Squirrel Notifier のショートカットを作成します。

.DESCRIPTION
    指定された実行ファイルへのショートカットを Start Menu と/または Desktop に作成します。
    既存のショートカットは上書きされます。

.PARAMETER ExePath
    SquirrelNotifier.WinUI3.exe のフルパス。省略時はスクリプトと同じフォルダを優先し、見つからない場合は入力を求めます。

.PARAMETER Tray
    ショートカットの引数に --tray を付与してトレイ起動する場合に指定します。

.PARAMETER StartMenu
    スタートメニューにショートカットを作成します（既定: 有効）。

.PARAMETER Desktop
    デスクトップにショートカットを作成します（既定: 無効）。

.EXAMPLE
    .\create-shortcuts.ps1
    同一フォルダの SquirrelNotifier.WinUI3.exe へスタートメニューショートカットを作成。

.EXAMPLE
    .\create-shortcuts.ps1 -Tray -Desktop
    トレイ起動用のショートカットをスタートメニューとデスクトップに作成。
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$ExePath,

    [switch]$Tray,

    [switch]$StartMenu = $true,

    [switch]$Desktop = $false
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-ExePath {
    param([string]$Path)

    if (-not [string]::IsNullOrWhiteSpace($Path) -and (Test-Path $Path)) {
        return (Resolve-Path $Path).Path
    }

    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    $candidate = Join-Path $scriptDir "SquirrelNotifier.WinUI3.exe"
    if (Test-Path $candidate) {
        return (Resolve-Path $candidate).Path
    }

    while ($true) {
        $inputPath = Read-Host "SquirrelNotifier.WinUI3.exe のパスを入力してください"
        if ([string]::IsNullOrWhiteSpace($inputPath)) {
            continue
        }

        if (Test-Path $inputPath) {
            return (Resolve-Path $inputPath).Path
        }

        Write-Host "指定パスが見つかりませんでした。" -ForegroundColor Yellow
    }
}

function New-AppShortcut {
    param(
        [Parameter(Mandatory = $true)][string]$Target,
        [Parameter(Mandatory = $true)][string]$ShortcutPath,
        [string]$Arguments
    )

    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($ShortcutPath)
    $shortcut.TargetPath = $Target
    $shortcut.Arguments = $Arguments
    $shortcut.WorkingDirectory = Split-Path -Parent $Target
    $shortcut.IconLocation = "$Target,0"
    $shortcut.Save()
}

$exe = Resolve-ExePath -Path $ExePath
$args = if ($Tray) { "--tray" } else { "" }

if ($StartMenu) {
    $startMenuPath = Join-Path "$env:APPDATA\Microsoft\Windows\Start Menu\Programs" "Squirrel Notifier.lnk"
    New-AppShortcut -Target $exe -ShortcutPath $startMenuPath -Arguments $args
    Write-Host "スタートメニューにショートカットを作成しました: $startMenuPath" -ForegroundColor Green
}

if ($Desktop) {
    $desktopPath = Join-Path ([Environment]::GetFolderPath("Desktop")) "Squirrel Notifier.lnk"
    New-AppShortcut -Target $exe -ShortcutPath $desktopPath -Arguments $args
    Write-Host "デスクトップにショートカットを作成しました: $desktopPath" -ForegroundColor Green
}

Write-Host "完了しました。" -ForegroundColor Green
