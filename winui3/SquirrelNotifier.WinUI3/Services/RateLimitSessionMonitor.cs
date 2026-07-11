// <copyright file="RateLimitSessionMonitor.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>
/// 1 回のエージェント実行の開始・終了 snapshot を取得し、表示用の Delta を計算する（#146）。
/// レビュー起動や WinUI 表示には依存しないため、snapshot の境界を単体で検証できる.
/// </summary>
internal sealed class RateLimitSessionMonitor
{
    private readonly RateLimitSnapshotService _snapshotService;
    private readonly RateLimitDeltaCalculator _deltaCalculator;
    private readonly TimeProvider _timeProvider;
    private readonly IReadOnlyList<string> _monitoredAgentIds;
    private readonly string? _activeAgentId;
    private readonly TimeSpan _freshnessThreshold;
    private IReadOnlyList<RateLimitSnapshot> _startSnapshots = [];
    private DateTimeOffset _startCapturedAt;

    public RateLimitSessionMonitor(
        RateLimitSnapshotService snapshotService,
        RateLimitDeltaCalculator deltaCalculator,
        IReadOnlyList<string> monitoredAgentIds,
        string? activeAgentId,
        TimeSpan freshnessThreshold,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(snapshotService);
        ArgumentNullException.ThrowIfNull(deltaCalculator);
        ArgumentNullException.ThrowIfNull(monitoredAgentIds);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(freshnessThreshold, TimeSpan.Zero);

        _snapshotService = snapshotService;
        _deltaCalculator = deltaCalculator;
        _monitoredAgentIds = monitoredAgentIds;
        _activeAgentId = activeAgentId;
        _freshnessThreshold = freshnessThreshold;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public IReadOnlyList<string> MonitoredAgentIds => _monitoredAgentIds;

    public string? ActiveAgentId => _activeAgentId;

    public async Task<IReadOnlyList<RateLimitSnapshot>> CaptureStartAsync(CancellationToken cancellationToken)
    {
        _startCapturedAt = _timeProvider.GetUtcNow();
        _startSnapshots = await CaptureSnapshotsAsync(cancellationToken).ConfigureAwait(false);
        return _startSnapshots;
    }

    public async Task<RateLimitSessionUpdate> CaptureEndAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<RateLimitSnapshot> endSnapshots = await CaptureSnapshotsAsync(cancellationToken).ConfigureAwait(false);
        RateLimitSnapshot? start = _startSnapshots.FirstOrDefault(snapshot => snapshot.AgentId == _activeAgentId);
        RateLimitSnapshot? end = endSnapshots.FirstOrDefault(snapshot => snapshot.AgentId == _activeAgentId);
        IReadOnlyList<RateLimitDeltaResult> deltas = _startCapturedAt == default
            ? []
            : _deltaCalculator.Compute(start, end, _startCapturedAt, _freshnessThreshold);

        return new RateLimitSessionUpdate(endSnapshots, deltas);
    }

    private async Task<IReadOnlyList<RateLimitSnapshot>> CaptureSnapshotsAsync(CancellationToken cancellationToken)
    {
        List<RateLimitSnapshot> snapshots = new();
        foreach (string agentId in _monitoredAgentIds
            .Append(_activeAgentId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal))
        {
            RateLimitSnapshot? snapshot = await _snapshotService.CaptureAsync(agentId, cancellationToken).ConfigureAwait(false);
            if (snapshot is not null && snapshot.AgentId == agentId)
            {
                snapshots.Add(snapshot);
            }
        }

        return snapshots;
    }
}

internal sealed record RateLimitSessionUpdate(
    IReadOnlyList<RateLimitSnapshot> Snapshots,
    IReadOnlyList<RateLimitDeltaResult> Deltas);
