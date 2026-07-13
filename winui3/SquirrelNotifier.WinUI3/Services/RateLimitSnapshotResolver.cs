// <copyright file="RateLimitSnapshotResolver.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>
/// Auto-Pause gate（#147）の評価に使う snapshot を agent 単位で解決する（#167 レビュー対応）。
/// 呼び出し元が別経路（表示更新等）で既に取得済みの snapshot は再取得せずそのまま使い、
/// 未取得の agent のみ <see cref="RateLimitSnapshotService"/> から取得する。取得済み snapshot を
/// 再利用しないと、同一操作内で 2 回取得した snapshot の一方だけ一時的に取得不可になった場合に
/// 表示と gate の判定結果が食い違う（#167 の再発）。cancellation 以外の取得失敗は
/// <see cref="RateLimitSessionMonitor"/> と同様に当該 agent の取得不可として扱い、呼び出し元の
/// イベントハンドラーへ例外を伝播させない.
/// </summary>
internal sealed class RateLimitSnapshotResolver
{
    private readonly RateLimitSnapshotService _snapshotService;

    public RateLimitSnapshotResolver(RateLimitSnapshotService snapshotService)
    {
        ArgumentNullException.ThrowIfNull(snapshotService);
        _snapshotService = snapshotService;
    }

    /// <summary>
    /// 指定した agentId 群の snapshot を解決する。.
    /// </summary>
    /// <param name="agentIds">解決対象の agentId 一覧.</param>
    /// <param name="capturedSnapshots">別経路で既に取得済みの snapshot（agentId をキーとする）.</param>
    /// <param name="cancellationToken">キャンセル用トークン.</param>
    /// <returns>解決できた snapshot 一覧。取得不可だった agent は含まれない.</returns>
    public async Task<IReadOnlyList<RateLimitSnapshot>> ResolveAsync(
        IReadOnlyList<string> agentIds,
        IReadOnlyDictionary<string, RateLimitSnapshot> capturedSnapshots,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agentIds);
        ArgumentNullException.ThrowIfNull(capturedSnapshots);

        var resolved = new List<RateLimitSnapshot>();
        foreach (string agentId in agentIds)
        {
            if (capturedSnapshots.TryGetValue(agentId, out RateLimitSnapshot? cached))
            {
                resolved.Add(cached);
                continue;
            }

            RateLimitSnapshot? snapshot;
            try
            {
                snapshot = await _snapshotService.CaptureAsync(agentId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // 取得不可は正常系として扱い、他 agent の解決を継続する（RateLimitSessionMonitor と同様の方針）
                continue;
            }

            if (snapshot is not null)
            {
                resolved.Add(snapshot);
            }
        }

        return resolved;
    }
}
