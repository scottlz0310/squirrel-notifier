<#
.SYNOPSIS
  リリース準備 PR に、実デスクトップのランブック（tests/e2e/agent-runbooks）の実行結果があり、
  リリース対象のコミットで実行されたことを確認する（#433）。
.DESCRIPTION
  runbook-guard.yml が PR で実行する。csproj の <Version> が base から変わっていなければ、
  リリース準備 PR ではないため検査しない。変わっていれば次を確かめる。
  - results/v<version>.json があり、schemaVersion と version が一致する
  - 実装したセッションとは別のセッションが実行した（executor.implementerSession が false）
  - scenarios/ の必須シナリオがすべて pass で、スクリーンショット以外の証跡を 1 つ以上持つ
  - テストしたコミットから HEAD までの差分が results/ 配下だけである（テスト後にコードが変わっていない）
  - テストしたコミットが base を含む（base の変更を取り込まずにテストしていない）
.EXAMPLE
  ./scripts/Assert-RunbookResult.ps1 -HeadSha <PR の head SHA> -BaseSha <PR の base SHA>
#>
param(
    [Parameter(Mandatory)][string]$HeadSha,
    [Parameter(Mandatory)][string]$BaseSha,
    [string]$CsprojPath = 'winui3/SquirrelNotifier.WinUI3/SquirrelNotifier.WinUI3.csproj',
    [string]$RunbookDirectory = 'tests/e2e/agent-runbooks',
    [switch]$FetchTestedCommit
)

$ErrorActionPreference = 'Stop'

function Invoke-Git {
    $output = git @args
    if ($LASTEXITCODE -ne 0) {
        throw "git $($args -join ' ') に失敗しました（exit $LASTEXITCODE）。"
    }
    $output
}

function Get-CsprojVersion([string]$Sha) {
    $xml = [xml]((Invoke-Git show "${Sha}:$CsprojPath") -join "`n")
    $node = $xml.SelectSingleNode('//Version')
    if (-not $node) { throw "$Sha の csproj から <Version> を取得できません" }
    $node.InnerText.Trim()
}

$version = Get-CsprojVersion $HeadSha
$baseVersion = Get-CsprojVersion $BaseSha
if ($version -eq $baseVersion) {
    Write-Host "csproj の version が base と同じ（$version）ため、リリース準備 PR ではないとみなして検査しません"
    return
}

$resultRelativePath = "$RunbookDirectory/results/v$version.json"
$resultPath = Join-Path $PWD $resultRelativePath
if (-not (Test-Path -LiteralPath $resultPath)) {
    throw "$resultRelativePath がありません。リリース準備 PR では、別セッションでランブックを実行し、結果を追加してください（$RunbookDirectory/README.md）。"
}

$result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
if ($result.schemaVersion -ne 1) {
    throw "$resultRelativePath の schemaVersion は 1 である必要があります: $($result.schemaVersion)"
}
if ($result.version -ne $version) {
    throw "$resultRelativePath の version（$($result.version)）が csproj（$version）と一致しません。"
}
if ($result.testedCommit -notmatch '^[0-9a-f]{40}$') {
    throw "$resultRelativePath の testedCommit は 40 桁の commit SHA である必要があります: $($result.testedCommit)"
}
if ($null -eq $result.executor -or $result.executor.implementerSession -ne $false) {
    throw "$resultRelativePath の executor.implementerSession が false ではありません。ランブックは対象リリースを実装したセッションとは別のセッションで実行してください。"
}

$required = @(Get-ChildItem -LiteralPath (Join-Path $PWD "$RunbookDirectory/scenarios") -Filter '*.md' |
    ForEach-Object {
        $text = Get-Content -LiteralPath $_.FullName -Raw
        if ($text -match '(?m)^required:\s*true\s*$') {
            if ($text -notmatch '(?m)^id:\s*([a-z0-9-]+)\s*$') { throw "$($_.Name) に id がありません" }
            $Matches[1]
        }
    })
if ($required.Count -eq 0) {
    throw "$RunbookDirectory/scenarios に必須シナリオがありません。"
}

$failures = [Collections.Generic.List[string]]::new()
foreach ($id in $required) {
    $entry = @($result.scenarios | Where-Object { $_.id -eq $id }) | Select-Object -First 1
    if (-not $entry) {
        $failures.Add("$id の結果がありません")
        continue
    }
    if ($entry.result -ne 'pass') {
        $failures.Add("$id が pass ではありません（$($entry.result)）")
        continue
    }
    $machineEvidence = @($entry.evidence | Where-Object { $_.kind -and $_.kind -ne 'screenshot' -and $_.summary })
    if ($machineEvidence.Count -eq 0) {
        $failures.Add("$id にスクリーンショット以外の証跡がありません")
    }
}
if ($failures.Count -gt 0) {
    throw "ランブックの結果が基準を満たしていません（$resultRelativePath）: $($failures -join ' / ')"
}

if ($FetchTestedCommit) {
    # Squash merge と準備ブランチ削除の後は、testedCommit が main の履歴だけでは取得されない。
    Invoke-Git fetch --no-tags origin $result.testedCommit | Out-Null
}
$changed = @(Invoke-Git diff --name-only $result.testedCommit $HeadSha | Where-Object { $_ })
$outside = @($changed | Where-Object { -not $_.StartsWith("$RunbookDirectory/results/", [StringComparison]::Ordinal) })
if ($outside.Count -gt 0) {
    throw "テストしたコミット（$($result.testedCommit)）の後に results/ 以外が変更されています。ランブックを実行し直してください: $($outside -join ', ')"
}

git merge-base --is-ancestor $BaseSha $result.testedCommit
if ($LASTEXITCODE -ne 0) {
    throw "テストしたコミット（$($result.testedCommit)）が base（$BaseSha）を含みません。base を取り込んでからランブックを実行し直してください。"
}

Write-Host "OK: v$version のランブック結果（$($required.Count) シナリオ）は $($result.testedCommit) で実行され、その後の変更は results/ だけです"
