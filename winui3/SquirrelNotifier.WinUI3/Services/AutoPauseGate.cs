// <copyright file="AutoPauseGate.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Globalization;
using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>Auto-Pause gate の判定結果の種類（#147）.</summary>
internal enum AutoPauseStatus
{
    /// <summary>レートリミット情報を取得できないエージェント（rateLimitAgentId なし）。gate 対象外で常に許可.</summary>
    NotApplicable,

    /// <summary>起動を許可する.</summary>
    Allowed,

    /// <summary>危険水域のため新規起動を拒否する.</summary>
    Paused,
}

/// <summary>Paused の根拠となった limit の情報。理由表示に使う.</summary>
internal sealed record AutoPausedLimit(
    string AgentId,
    string LimitId,
    string LimitLabel,
    double UsedPercentage,
    DateTimeOffset? ResetAt,
    DateTimeOffset ObservedAt)
{
    public string BuildReasonText()
    {
        string agentName = RateLimitAgentCatalog.All.FirstOrDefault(agent => agent.Id == AgentId)?.DisplayName ?? AgentId;
        string observed = ObservedAt.ToLocalTime().ToString("yyyy/MM/dd HH:mm", CultureInfo.CurrentCulture);
        string reset = ResetAt is DateTimeOffset resetAt
            ? resetAt.ToLocalTime().ToString("yyyy/MM/dd HH:mm", CultureInfo.CurrentCulture)
            : "取得不可";
        return string.Format(
            CultureInfo.CurrentCulture,
            "{0} — {1} の使用率が {2:0.#}% に達しています（観測: {3} / リセット: {4}）",
            agentName,
            LimitLabel,
            UsedPercentage,
            observed,
            reset);
    }
}

/// <summary>gate の判定結果。Paused の場合は根拠 limit を含む.</summary>
internal sealed record AutoPauseDecision(AutoPauseStatus Status, AutoPausedLimit? PausedLimit);

/// <summary>
/// fresh なレートリミット snapshot が危険水域（95% 以上）に達したエージェントの新規 launcher
/// 起動を停止する Auto-Pause の状態遷移を担う（#147）。判定のみを行い、実行中プロセス・
/// MCP subscription・thread-owl queue には一切作用しない。
/// Paused 状態は agent 単位で保持し、fresh な snapshot で 95% 未満を確認した場合のみ解除する。
/// stale / missing data や resetAt 通過だけでは解除しない（snapshot は判定に使う値がすべて
/// 揃っているときのみ信頼する）。UI スレッドからの利用を前提とし、スレッドセーフではない.
/// </summary>
internal sealed class AutoPauseGate
{
    private const double _pauseThresholdPercentage = 95;

    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, AutoPausedLimit> _pausedByAgentId = new(StringComparer.Ordinal);

    public AutoPauseGate(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Paused な agent の集合が変化したときに発火する。メイン UI の表示更新用.</summary>
    public event EventHandler? StateChanged;

    // 現在 Paused な agent の根拠 limit 一覧（agentId 昇順）
    public IReadOnlyList<AutoPausedLimit> PausedLimits
        => _pausedByAgentId.Values.OrderBy(paused => paused.AgentId, StringComparer.Ordinal).ToList();

    /// <summary>
    /// 指定エージェントの起動可否を判定し、内部の Paused 状態を更新する。
    /// snapshot が fresh な場合のみ状態を遷移させる（Pause 開始・解除とも）.
    /// </summary>
    /// <param name="agentId">起動する launcher スロットの rateLimitAgentId。取得手段が無い場合は null.</param>
    /// <param name="snapshots">直近に取得した snapshot 一覧.</param>
    /// <param name="freshnessThreshold">鮮度判定のしきい値.</param>
    /// <returns>判定結果.</returns>
    public AutoPauseDecision Evaluate(string? agentId, IReadOnlyList<RateLimitSnapshot> snapshots, TimeSpan freshnessThreshold)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(freshnessThreshold, TimeSpan.Zero);

        if (string.IsNullOrWhiteSpace(agentId))
        {
            return new AutoPauseDecision(AutoPauseStatus.NotApplicable, null);
        }

        RateLimitSnapshot? snapshot = snapshots.FirstOrDefault(candidate => candidate.AgentId == agentId);
        bool isFresh = snapshot is not null
            && RateLimitFreshnessPolicy.IsFresh(snapshot.ObservedAt, _timeProvider.GetUtcNow(), freshnessThreshold);
        if (!isFresh)
        {
            return KeepCurrentState(agentId);
        }

        RateLimitInfo? worst = snapshot!.Limits
            .Where(limit => limit.UsedPercentage is not null)
            .OrderByDescending(limit => limit.UsedPercentage)
            .FirstOrDefault();
        if (worst?.UsedPercentage is not double usedPercentage)
        {
            // fresh でも usedPercentage を持つ limit が無い場合は「95% 未満」を確認できて
            // いないため、既存の Paused を解除しない
            return KeepCurrentState(agentId);
        }

        if (usedPercentage >= _pauseThresholdPercentage)
        {
            AutoPausedLimit paused = new(agentId, worst.Id, worst.Label, usedPercentage, worst.ResetAt, snapshot.ObservedAt);
            bool changed = !_pausedByAgentId.TryGetValue(agentId, out AutoPausedLimit? current) || current != paused;
            _pausedByAgentId[agentId] = paused;
            if (changed)
            {
                StateChanged?.Invoke(this, EventArgs.Empty);
            }

            return new AutoPauseDecision(AutoPauseStatus.Paused, paused);
        }

        if (_pausedByAgentId.Remove(agentId))
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        return new AutoPauseDecision(AutoPauseStatus.Allowed, null);
    }

    private AutoPauseDecision KeepCurrentState(string agentId)
        => _pausedByAgentId.TryGetValue(agentId, out AutoPausedLimit? existing)
            ? new AutoPauseDecision(AutoPauseStatus.Paused, existing)
            : new AutoPauseDecision(AutoPauseStatus.Allowed, null);
}
