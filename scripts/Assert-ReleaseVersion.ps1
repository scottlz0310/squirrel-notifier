<#
.SYNOPSIS
  リリースの version 入力が x.y.z 形式であることを確認する。
.DESCRIPTION
  workflow_dispatch の手動入力を後続のコマンドへ渡す前に検証する（command injection 防止）。
  先頭の v や prerelease 表記は受け付けない。
.EXAMPLE
  ./scripts/Assert-ReleaseVersion.ps1 -Version 0.14.0
#>
param(
    [Parameter(Mandatory)][AllowEmptyString()][string]$Version
)

$ErrorActionPreference = 'Stop'

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "version は x.y.z 形式で指定してください: '$Version'"
}
