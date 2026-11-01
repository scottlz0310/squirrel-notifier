function Assert-InstalledPayload {
    param(
        [Parameter(Mandatory)][string]$PayloadDirectory,
        [Parameter(Mandatory)][string]$InstallDirectory
    )

    $root = (Resolve-Path -LiteralPath $PayloadDirectory).Path
    $files = @(Get-ChildItem -LiteralPath $root -File -Recurse)
    if ($files.Count -eq 0) { throw "比較元の payload が空です: $root" }
    foreach ($file in $files) {
        $relative = [IO.Path]::GetRelativePath($root, $file.FullName)
        $installed = Join-Path $InstallDirectory $relative
        if (-not (Test-Path -LiteralPath $installed -PathType Leaf)) {
            throw "インストール済み payload が欠落しています: $relative"
        }
        if ((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -cne
            (Get-FileHash -LiteralPath $installed -Algorithm SHA256).Hash) {
            throw "インストール済み payload の SHA256 が一致しません: $relative"
        }
    }
    return $files.Count
}
