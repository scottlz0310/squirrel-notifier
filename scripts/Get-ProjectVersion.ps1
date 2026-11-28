<#
.SYNOPSIS
  csproj の <Version> を一意に取得する。
.EXAMPLE
  ./scripts/Get-ProjectVersion.ps1 -ProjectPath winui3/SquirrelNotifier.WinUI3/SquirrelNotifier.WinUI3.csproj
#>
param(
    [Parameter(Mandatory)][string]$ProjectPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

[xml]$project = Get-Content -LiteralPath $ProjectPath -Raw -Encoding utf8
$versions = @($project.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })
if ($versions.Count -ne 1) {
    throw 'csproj から release version を一意に取得できません。'
}

return [string]$versions[0]
