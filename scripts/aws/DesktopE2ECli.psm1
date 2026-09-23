# desktop E2E の workflow から使う scripts/aws のスクリプトが、aws CLI と gh CLI を呼び出すための関数（#380）。
# 失敗は例外へ変換し、「存在しない」を成功とみなすのは呼び出し側が明示したエラーコードだけに限る。
# テストでは global の aws / gh 関数で置き換える（関数はコマンド解決で外部実行ファイルより優先される）。

Set-StrictMode -Version Latest

function Invoke-AwsCli
{
    <#
    .SYNOPSIS
      aws CLI を呼び出し、失敗を例外へ変換する。
    .DESCRIPTION
      -AbsentErrorCode は「存在しない」を成功とみなしたい場合だけに使い、その AWS エラーコードの
      失敗に限って $null を返す。権限不足まで握り潰すと、処理できていないことに気付けない。
    #>
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments,

        [string]$AbsentErrorCode
    )

    $output = & aws @Arguments 2>&1
    if ($LASTEXITCODE -ne 0)
    {
        if ($AbsentErrorCode -and ($output | Out-String).Contains("($AbsentErrorCode)"))
        {
            return $null
        }

        throw "aws CLI が失敗しました: aws $($Arguments -join ' ')`n$output"
    }

    return ($output | Out-String).Trim()
}

function Invoke-AwsDryRun
{
    <#
    .SYNOPSIS
      aws CLI の --dry-run を呼び出し、終了コードと出力をそのまま返す。
    .DESCRIPTION
      DryRun は認可されても終了コードが 0 以外（DryRunOperation）になるため、例外へ変換しない。
      分類は Resolve-DesktopE2EDryRunOutcome が行う。
    #>
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    $output = & aws @Arguments 2>&1
    return [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Output   = ($output | Out-String).Trim()
    }
}

function Invoke-GhApi
{
    <#
    .SYNOPSIS
      gh api を呼び出し、失敗を例外へ変換する。
    .DESCRIPTION
      -InputJson は要求本文を標準入力で渡す（--input -）。応答に秘密情報を含む API
      （generate-jitconfig）では、呼び出し側が戻り値をログへ書かないこと。失敗時の応答は
      エラー内容だけなので例外に含める。
    #>
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments,

        [string]$InputJson
    )

    if ($PSBoundParameters.ContainsKey('InputJson'))
    {
        $output = $InputJson | & gh api @Arguments --input - 2>&1
    }
    else
    {
        $output = & gh api @Arguments 2>&1
    }

    if ($LASTEXITCODE -ne 0)
    {
        throw "gh api が失敗しました: gh api $($Arguments -join ' ')`n$output"
    }

    return $output
}

Export-ModuleMember -Function @('Invoke-AwsCli', 'Invoke-AwsDryRun', 'Invoke-GhApi')
