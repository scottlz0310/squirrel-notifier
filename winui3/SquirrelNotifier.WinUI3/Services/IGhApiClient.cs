// <copyright file="IGhApiClient.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SquirrelNotifier.WinUI3.Services;

/// <summary><see cref="IGhApiClient.GetAsync"/> の結果（#456）.</summary>
/// <param name="IsSuccess"><c>gh</c> が終了コード 0 で終わったか.</param>
/// <param name="Output">成功時の標準出力（<c>--jq</c> で絞った出力）。失敗時は空.</param>
/// <param name="HttpStatus">失敗が GitHub API の HTTP エラーだった場合のステータスコード。それ以外は <see langword="null"/>.</param>
/// <param name="Error">失敗時の原因。マスク・要約済みで、ログへそのまま出せる.</param>
internal sealed record GhApiResult(bool IsSuccess, string Output, int? HttpStatus, string? Error)
{
    public static GhApiResult Success(string output) => new(true, output, null, null);

    public static GhApiResult Failure(int? httpStatus, string error) => new(false, string.Empty, httpStatus, error);
}

/// <summary>
/// GitHub CLI（<c>gh api</c>）で GitHub REST API を読み取る（#456）。GET だけを行い、
/// 401 / 403 / 404 などの HTTP エラーは例外ではなく <see cref="GhApiResult"/> として返す
/// （呼び出し側が「設定なし」と「権限不足」を区別するため）.
/// </summary>
internal interface IGhApiClient
{
    /// <summary>
    /// API を GET する。<c>gh</c> が使えない・タイムアウトした場合も <see cref="GhApiResult"/> の失敗として返し、
    /// キャンセルされた場合だけ <see cref="OperationCanceledException"/> を送出する.
    /// </summary>
    /// <param name="path"><c>repos/...</c> 形式の API パス（クエリ文字列を含んでよい）.</param>
    /// <param name="jq">出力を必要な項目だけに絞る jq 式。コメント本文などを読み込まないために必須とする.</param>
    /// <param name="paginate">全ページを取得するか。<paramref name="jq"/> はページごとに適用される.</param>
    /// <param name="cancellationToken">実行を中断するトークン.</param>
    /// <returns>実行結果.</returns>
    Task<GhApiResult> GetAsync(string path, string jq, bool paginate, CancellationToken cancellationToken);
}
