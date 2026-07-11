// <copyright file="RateLimitSnapshotService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>
/// <see cref="RateLimitFileService"/>（ファイル読み取り）と <see cref="RateLimitStatusParser"/>
/// （新スキーマ解釈）を組み合わせ、型付きの <see cref="RateLimitSnapshot"/> を取得する（#145）。
/// レビューセッション開始／終了時点のスナップショット取得に使う想定（消費側は #146/#147）.
/// </summary>
internal sealed class RateLimitSnapshotService
{
    private readonly RateLimitFileService _fileService;

    public RateLimitSnapshotService(RateLimitFileService fileService)
    {
        ArgumentNullException.ThrowIfNull(fileService);
        _fileService = fileService;
    }

    /// <summary>
    /// 指定エージェントの現在のスナップショットを取得する。ファイル未書き出し・旧スキーマ
    /// （resetAt のみ）・malformed JSON のいずれの場合も <see langword="null"/>（取得不可）を返す.
    /// </summary>
    /// <param name="agentId">対象エージェント ID.</param>
    /// <param name="cancellationToken">キャンセル用トークン.</param>
    /// <returns>取得できた場合はスナップショット。それ以外は <see langword="null"/>.</returns>
    public async Task<RateLimitSnapshot?> CaptureAsync(string agentId, CancellationToken cancellationToken)
    {
        string? json = await _fileService.ReadAgentStatusAsync(agentId, cancellationToken).ConfigureAwait(false);
        return RateLimitStatusParser.ParseSnapshot(json);
    }
}
