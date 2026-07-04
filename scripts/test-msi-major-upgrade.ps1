<#
.SYNOPSIS
    MSI が同一バージョンの MajorUpgrade を検出できることを検証します。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$MsiPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-ComMethod {
    param(
        [Parameter(Mandatory)]
        [object]$Target,

        [Parameter(Mandatory)]
        [string]$Name,

        [object[]]$Arguments = @()
    )

    return $Target.GetType().InvokeMember(
        $Name,
        [System.Reflection.BindingFlags]::InvokeMethod,
        $null,
        $Target,
        $Arguments)
}

function Get-ComProperty {
    param(
        [Parameter(Mandatory)]
        [object]$Target,

        [Parameter(Mandatory)]
        [string]$Name,

        [object[]]$Arguments = @()
    )

    return $Target.GetType().InvokeMember(
        $Name,
        [System.Reflection.BindingFlags]::GetProperty,
        $null,
        $Target,
        $Arguments)
}

function Get-MsiRows {
    param(
        [Parameter(Mandatory)]
        [object]$Database,

        [Parameter(Mandatory)]
        [string]$Query
    )

    $view = Invoke-ComMethod -Target $Database -Name 'OpenView' -Arguments @($Query)
    try {
        Invoke-ComMethod -Target $view -Name 'Execute' | Out-Null
        while ($record = Invoke-ComMethod -Target $view -Name 'Fetch') {
            try {
                $fieldCount = Get-ComProperty -Target $record -Name 'FieldCount'
                $row = for ($index = 1; $index -le $fieldCount; $index++) {
                    Get-ComProperty -Target $record -Name 'StringData' -Arguments @($index)
                }
                ,$row
            }
            finally {
                [System.Runtime.InteropServices.Marshal]::ReleaseComObject($record) | Out-Null
            }
        }
    }
    finally {
        Invoke-ComMethod -Target $view -Name 'Close' | Out-Null
        [System.Runtime.InteropServices.Marshal]::ReleaseComObject($view) | Out-Null
    }
}

$resolvedMsiPath = (Resolve-Path -LiteralPath $MsiPath).Path
$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $null
try {
    $database = Invoke-ComMethod -Target $installer -Name 'OpenDatabase' -Arguments @($resolvedMsiPath, 0)

    $properties = @{}
    foreach ($row in Get-MsiRows -Database $database -Query 'SELECT * FROM `Property`') {
        $properties[$row[0]] = $row[1]
    }

    $productVersion = $properties['ProductVersion']
    if ([string]::IsNullOrWhiteSpace($productVersion)) {
        throw "MSI の ProductVersion を取得できません: $resolvedMsiPath"
    }

    $upgradeRow = Get-MsiRows -Database $database -Query 'SELECT * FROM `Upgrade`' |
        Where-Object { $_[6] -eq 'WIX_UPGRADE_DETECTED' } |
        Select-Object -First 1

    if ($null -eq $upgradeRow) {
        throw "MSI に WIX_UPGRADE_DETECTED の Upgrade table 行がありません: $resolvedMsiPath"
    }

    $versionMax = $upgradeRow[2]
    $attributes = [int]$upgradeRow[4]
    $versionMaxInclusive = 0x200

    if ($versionMax -ne $productVersion -or ($attributes -band $versionMaxInclusive) -eq 0) {
        throw "同一バージョンの MajorUpgrade が有効ではありません: ProductVersion=$productVersion, VersionMax=$versionMax, Attributes=$attributes"
    }

    Write-Host "MSI MajorUpgrade 検証に成功しました: ProductVersion=$productVersion, Attributes=$attributes"
}
finally {
    if ($null -ne $database) {
        [System.Runtime.InteropServices.Marshal]::ReleaseComObject($database) | Out-Null
    }
    [System.Runtime.InteropServices.Marshal]::ReleaseComObject($installer) | Out-Null
}
