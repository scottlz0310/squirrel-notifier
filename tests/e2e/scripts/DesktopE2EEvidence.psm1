<#
    desktop E2E harness の判定と証跡保存のうち、runner や製品に依存しない部分。
    Invoke-DesktopE2E.ps1 は runner 上でしか実行できないため、失敗時に何が残るかを
    Pester で固定できるようにここへ切り出す（#397）。
#>

Set-StrictMode -Version Latest

function ConvertTo-ReleaseVersion {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Value
    )

    $match = [regex]::Match($Value.Trim(), '^v?(\d+)\.(\d+)\.(\d+)(?:\.0)?(?:[-+][0-9A-Za-z.-]+)?$')
    if (-not $match.Success) {
        return $null
    }

    return '{0}.{1}.{2}' -f $match.Groups[1].Value, $match.Groups[2].Value, $match.Groups[3].Value
}

function Test-InstalledVersion {
    <#
        不一致時のメッセージには実測値と参照した実行ファイルを含める。
        期待値だけでは、別 version の製品が残っていたのか、判定そのものの誤りなのかを
        artifact から区別できない（#397）。
    #>
    param(
        [Parameter(Mandatory)]
        [string]$ExpectedVersion,

        [AllowNull()]
        [AllowEmptyString()]
        [string]$ProductVersion,

        [AllowNull()]
        [AllowEmptyString()]
        [string]$FileVersion,

        [Parameter(Mandatory)]
        [string]$ExecutablePath
    )

    $expectedKey = ConvertTo-ReleaseVersion -Value $ExpectedVersion
    $actualKey = if ([string]::IsNullOrEmpty($ProductVersion)) { $null } else { ConvertTo-ReleaseVersion -Value $ProductVersion }
    $isMatch = $null -ne $expectedKey -and $null -ne $actualKey -and $actualKey -ceq $expectedKey

    $message = if ($isMatch) {
        $null
    }
    else {
        $productText = if ([string]::IsNullOrEmpty($ProductVersion)) { '(なし)' } else { $ProductVersion }
        $fileText = if ([string]::IsNullOrEmpty($FileVersion)) { '(なし)' } else { $FileVersion }
        "インストールされた製品 version が期待値と異なります。期待値=$ExpectedVersion ProductVersion=$productText FileVersion=$fileText 実行ファイル=$ExecutablePath"
    }

    return [pscustomobject]@{
        Matches = $isMatch
        Message = $message
    }
}

function Save-MsiLogArtifact {
    <#
        msiexec のログは runRoot に書かれ、cleanup の runRoot 削除で消える。
        失敗時だけ artifact へサニタイズして退避する（#397）。
        /L*v のログは UTF-16 LE で書かれるため、BOM から encoding を判定して読む。
        返り値は保存した artifact 相対パスの一覧。存在しないログは無視する。
    #>
    param(
        [Parameter(Mandatory)]
        [string[]]$LogPath,

        [Parameter(Mandatory)]
        [string]$ArtifactDirectory,

        [Parameter(Mandatory)]
        [scriptblock]$Sanitize
    )

    $saved = [System.Collections.Generic.List[string]]::new()
    foreach ($path in $LogPath) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            continue
        }

        $destinationDirectory = Join-Path $ArtifactDirectory 'msi-logs'
        if (-not (Test-Path -LiteralPath $destinationDirectory -PathType Container)) {
            New-Item -ItemType Directory -Path $destinationDirectory -Force -ErrorAction Stop | Out-Null
        }

        $reader = [System.IO.StreamReader]::new($path, [System.Text.Encoding]::UTF8, $true)
        try {
            $content = $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }

        $name = Split-Path -Path $path -Leaf
        $sanitized = & $Sanitize $content
        Set-Content -LiteralPath (Join-Path $destinationDirectory $name) -Value $sanitized -Encoding utf8 -NoNewline
        $saved.Add("msi-logs/$name")
    }

    return , $saved.ToArray()
}

Export-ModuleMember -Function ConvertTo-ReleaseVersion, Test-InstalledVersion, Save-MsiLogArtifact
