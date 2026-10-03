<#
.SYNOPSIS
  固定 SHA の draft に成果物と日本語ノートを添付し、または添付済みの成果物を再取得して検証する。
#>
param(
    [Parameter(Mandatory)][ValidateSet('Attach', 'Verify')][string]$Action,
    [Parameter(Mandatory)][string]$Repository,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$TargetSha,
    [Parameter(Mandatory)][string]$Directory,
    [Parameter(Mandatory)][string]$NotesPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Assert-ReleasePackageFiles.ps1')
& (Join-Path $PSScriptRoot 'Assert-ReleaseVersion.ps1') -Version $Version
$tag = "v$Version"
$info = gh release view $tag --repo $Repository --json tagName,targetCommitish,isDraft,body
if ($LASTEXITCODE -ne 0) { throw "draft Release を取得できません: $Repository $tag" }
$release = $info | ConvertFrom-Json
if (-not $release.isDraft -or $release.tagName -cne $tag -or $release.targetCommitish -cne $TargetSha) {
    throw "固定 SHA の draft Release ではありません: $Repository $tag"
}
$names = @("SquirrelNotifier-Setup-$Version-x64.zip", "SquirrelNotifier-Setup-$Version-x64.msi", 'checksums-x64.txt')

if ($Action -eq 'Attach') {
    Assert-ReleasePackageFiles -Directory $Directory -Version $Version
    $assets = @($names | ForEach-Object { Join-Path $Directory $_ })
    # 同じ SHA の draft の再実行では置換してから再取得・検証する。公開済みの Release は上で拒否する。
    gh release upload $tag @assets --repo $Repository --clobber
    if ($LASTEXITCODE -ne 0) { throw "draft の成果物添付に失敗しました: $tag" }
    gh release edit $tag --repo $Repository --notes-file $NotesPath
    if ($LASTEXITCODE -ne 0) { throw "draft の日本語ノート更新に失敗しました: $tag" }
}
else {
    $expectedNotes = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $NotesPath).Path).Replace("`r`n", "`n").Trim()
    if ($release.body.Replace("`r`n", "`n").Trim() -cne $expectedNotes) {
        throw "draft のリリースノートが CHANGELOG の合成結果と一致しません: $tag"
    }
    New-Item -ItemType Directory -Path $Directory -Force | Out-Null
    foreach ($name in $names) {
        gh release download $tag --repo $Repository --pattern $name --dir $Directory
        if ($LASTEXITCODE -ne 0) { throw "draft の成果物を取得できません: $name" }
    }
    Assert-ReleasePackageFiles -Directory $Directory -Version $Version
    $msi = Join-Path $Directory $names[1]
    pwsh -NoProfile -File (Join-Path $PSScriptRoot 'test-msi-major-upgrade.ps1') -MsiPath $msi -ExpectedVersion $Version
    if ($LASTEXITCODE -ne 0) { throw "draft の MSI 検証に失敗しました: $tag" }

    $archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $Directory $names[0]))
    $binary = Join-Path $Directory 'SquirrelNotifier.WinUI3.exe'
    try {
        $inputStream = $archive.GetEntry('SquirrelNotifier.WinUI3.exe').Open()
        $outputStream = [IO.File]::Create($binary)
        try { $inputStream.CopyTo($outputStream) }
        finally { $outputStream.Dispose(); $inputStream.Dispose() }
    }
    finally { $archive.Dispose() }
    $actualVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($binary).ProductVersion
    if ($actualVersion -cne "$Version+$TargetSha") {
        throw "setup ZIP の ProductVersion が対象 SHA と一致しません: $actualVersion"
    }
}
