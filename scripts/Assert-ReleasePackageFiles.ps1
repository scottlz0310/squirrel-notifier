function Assert-ReleasePackageFiles {
    param(
        [Parameter(Mandatory)][string]$Directory,
        [Parameter(Mandatory)][string]$Version
    )

    & (Join-Path $PSScriptRoot 'Assert-ReleaseVersion.ps1') -Version $Version
    $names = @("SquirrelNotifier-Setup-$Version-x64.zip", "SquirrelNotifier-Setup-$Version-x64.msi")
    $checksums = Join-Path $Directory 'checksums-x64.txt'
    $hashes = [System.Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    foreach ($line in Get-Content -LiteralPath $checksums) {
        if ($line -cnotmatch '^([A-Fa-f0-9]{64})  (.+)$') {
            throw "checksum の形式が不正です: $checksums"
        }
        $name = $Matches[2]
        if ($name -cnotin $names -or $hashes.ContainsKey($name)) {
            throw "checksum の対象が不正または重複しています: $name"
        }
        $hashes.Add($name, $Matches[1])
    }
    foreach ($name in $names) {
        if (-not $hashes.ContainsKey($name)) {
            throw "checksum がありません: $name"
        }
        $actual = (Get-FileHash -LiteralPath (Join-Path $Directory $name) -Algorithm SHA256).Hash
        if ($actual -ne $hashes[$name]) {
            throw "release asset の checksum が一致しません: $name"
        }
    }

    $archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $Directory $names[0]))
    try {
        foreach ($required in @('SquirrelNotifier.WinUI3.exe', 'install.ps1', 'install.cmd', 'uninstall.ps1', 'uninstall.cmd', 'create-shortcuts.ps1', 'create-shortcuts.cmd')) {
            if (@($archive.Entries | Where-Object FullName -CEQ $required).Count -ne 1) {
                throw "setup ZIP に必要なファイルが一意に存在しません: $required"
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}
