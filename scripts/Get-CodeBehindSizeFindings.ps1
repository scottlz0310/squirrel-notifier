function Get-CodeBehindSizeFindings {
    param(
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][System.Collections.IDictionary]$Limits
    )

    $violations = [System.Collections.Generic.List[string]]::new()
    $missing = [System.Collections.Generic.List[string]]::new()
    $measurements = [System.Collections.Generic.List[object]]::new()
    $tracked = @($Limits.Keys | ForEach-Object { $_.ToLowerInvariant() })
    $found = Get-ChildItem -Path (Join-Path $RepositoryRoot 'winui3') -Filter '*.xaml.cs' -Recurse -File |
        Where-Object { $_.FullName -notmatch '[\\/](obj|bin)[\\/]' }

    foreach ($file in $found) {
        $relative = [System.IO.Path]::GetRelativePath($RepositoryRoot, $file.FullName).Replace('\', '/')
        if (-not ($tracked -contains $relative.ToLowerInvariant())) {
            $missing.Add($relative)
        }
    }

    foreach ($entry in $Limits.GetEnumerator()) {
        $relative = $entry.Key
        $limit = $entry.Value
        $path = Join-Path $RepositoryRoot $relative
        if (-not (Test-Path -LiteralPath $path)) {
            $violations.Add("$relative : 上限が登録されているファイルが存在しません（削除したなら limits からも消してください）")
            continue
        }

        # Measure-Object -Line は空行を数えないため、物理行数を使う。
        $actual = @(Get-Content -LiteralPath $path).Count
        $measurements.Add([pscustomobject]@{ Path = $relative; Actual = $actual; Limit = $limit })
        if ($actual -gt $limit) {
            $violations.Add("$relative : $actual 行（上限 $limit 行、超過 $($actual - $limit) 行）")
        }
    }

    [pscustomobject]@{
        Measurements = $measurements.ToArray()
        Violations = $violations.ToArray()
        Missing = $missing.ToArray()
    }
}
