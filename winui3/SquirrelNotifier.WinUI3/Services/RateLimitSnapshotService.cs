// <copyright file="RateLimitSnapshotService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>
/// エージェント別の取得経路を束ね、型付きの <see cref="RateLimitSnapshot"/> を取得する（#145/#163）。
/// claude-code / agy は <see cref="RateLimitFileService"/>（statusline フック由来のローカルファイル）
/// + <see cref="RateLimitStatusParser"/>、codex は <see cref="CodexAppServerRateLimitClient"/>
/// （App Server 直読み）を使う。レビューセッション開始／終了時点のスナップショット取得に
/// 使う想定（消費側は #146/#147）.
/// </summary>
internal sealed class RateLimitSnapshotService
{
    /// <summary>App Server 経由で取得するエージェント ID（<see cref="RateLimitAgentCatalog"/> の codex）.</summary>
    public const string CodexAgentId = "codex";

    private readonly RateLimitFileService _fileService;
    private readonly CodexAppServerRateLimitClient _codexClient;

    public RateLimitSnapshotService(RateLimitFileService fileService, CodexAppServerRateLimitClient? codexClient = null)
    {
        ArgumentNullException.ThrowIfNull(fileService);
        _fileService = fileService;
        _codexClient = codexClient ?? new CodexAppServerRateLimitClient(new ProcessRunner());
    }

    /// <summary>
    /// 指定エージェントの現在のスナップショットを取得する。ファイル未書き出し・旧スキーマ
    /// （resetAt のみ）・malformed JSON・App Server の取得失敗のいずれの場合も
    /// <see langword="null"/>（取得不可）を返す.
    /// </summary>
    /// <param name="agentId">対象エージェント ID.</param>
    /// <param name="cancellationToken">キャンセル用トークン.</param>
    /// <returns>取得できた場合はスナップショット。それ以外は <see langword="null"/>.</returns>
    public async Task<RateLimitSnapshot?> CaptureAsync(string agentId, CancellationToken cancellationToken)
    {
        if (agentId == CodexAgentId)
        {
            return await _codexClient.CaptureAsync(agentId, cancellationToken).ConfigureAwait(false);
        }

        string? json = await _fileService.ReadAgentStatusAsync(agentId, cancellationToken).ConfigureAwait(false);
        return RateLimitStatusParser.ParseSnapshot(json);
    }
}
