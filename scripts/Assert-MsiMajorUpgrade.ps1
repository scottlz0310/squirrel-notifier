function Assert-MsiMajorUpgrade {
    param(
        [AllowNull()][AllowEmptyString()][string]$ProductVersion,
        [AllowEmptyCollection()][object[]]$UpgradeRows,
        [Parameter(Mandatory)][string]$MsiPath
    )

    if ([string]::IsNullOrWhiteSpace($ProductVersion)) {
        throw "MSI の ProductVersion を取得できません: $MsiPath"
    }

    $upgradeRow = $UpgradeRows | Where-Object { $_[6] -eq 'WIX_UPGRADE_DETECTED' } | Select-Object -First 1
    if ($null -eq $upgradeRow) {
        throw "MSI に WIX_UPGRADE_DETECTED の Upgrade table 行がありません: $MsiPath"
    }

    $versionMax = $upgradeRow[2]
    $attributes = [int]$upgradeRow[4]
    $versionMaxInclusive = 0x200
    if ($versionMax -ne $ProductVersion -or ($attributes -band $versionMaxInclusive) -eq 0) {
        throw "同一バージョンの MajorUpgrade が有効ではありません: ProductVersion=$ProductVersion, VersionMax=$versionMax, Attributes=$attributes"
    }

    Write-Host "MSI MajorUpgrade 検証に成功しました: ProductVersion=$ProductVersion, Attributes=$attributes"
}
