// <copyright file="RateLimitGaugeViewModel.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.ViewModels;

internal enum RateLimitGaugeSeverity
{
    /// <summary>使用率・鮮度を判定できない.</summary>
    Unknown,

    /// <summary>使用率が注意閾値未満.</summary>
    Normal,

    /// <summary>使用率が注意閾値以上.</summary>
    Warning,

    /// <summary>使用率が危険閾値以上.</summary>
    Critical,
}

internal sealed record RateLimitGaugeOption(
    string AgentId,
    string AgentDisplayName,
    string? LimitId,
    string LimitLabel,
    double? UsedPercentage,
    DateTimeOffset? ResetAt,
    DateTimeOffset? ObservedAt,
    bool IsFresh,
    RateLimitGaugeSeverity Severity,
    RateLimitDeltaResult? Delta)
{
    public string DisplayName => $"{AgentDisplayName} — {LimitLabel}";
}

/// <summary>
/// ライブログウィンドウのレートリミット燃料ゲージ表示状態を、snapshot と Delta から組み立てる（#146）。
/// WinUI 型に依存しないため、閾値・取得不可・選択優先順位を単体テストできる.
/// </summary>
internal sealed class RateLimitGaugeViewModel : INotifyPropertyChanged
{
    private const double _warningThreshold = 70;
    private const double _criticalThreshold = 90;

    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _freshnessThreshold;
    private RateLimitGaugeOption? _selectedOption;
    private string? _unavailableMessage;

    public RateLimitGaugeViewModel(TimeSpan freshnessThreshold, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(freshnessThreshold, TimeSpan.Zero);

        _freshnessThreshold = freshnessThreshold;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<RateLimitGaugeOption> Options { get; } = new();

    public RateLimitGaugeOption? SelectedOption => _selectedOption;

    public double ProgressValue => _selectedOption?.Severity == RateLimitGaugeSeverity.Unknown
        ? 0
        : _selectedOption?.UsedPercentage ?? 0;

    public string StatusText => _selectedOption?.Severity switch
    {
        RateLimitGaugeSeverity.Normal => "状態: 正常",
        RateLimitGaugeSeverity.Warning => "状態: 注意",
        RateLimitGaugeSeverity.Critical => "状態: 危険",
        _ => "状態: 取得不可",
    };

    public string UsageText => _selectedOption?.UsedPercentage is double used && _selectedOption.Severity != RateLimitGaugeSeverity.Unknown
        ? string.Format(CultureInfo.CurrentCulture, "使用率: {0:0.#}%（残量: {1:0.#}%）", used, 100 - used)
        : "使用率: 取得不可";

    public string TimingText
    {
        get
        {
            if (_selectedOption?.ObservedAt is not DateTimeOffset observedAt)
            {
                return _unavailableMessage ?? "対応するレートリミット情報がありません";
            }

            string observed = observedAt.ToLocalTime().ToString("yyyy/MM/dd HH:mm", CultureInfo.CurrentCulture);
            string reset = _selectedOption.ResetAt is DateTimeOffset resetAt
                ? resetAt.ToLocalTime().ToString("yyyy/MM/dd HH:mm", CultureInfo.CurrentCulture)
                : "取得不可";
            string freshness = _selectedOption.IsFresh ? "fresh" : "古いデータ";
            return $"観測: {observed}（{freshness}） / リセット: {reset}";
        }
    }

    public string DeltaText
    {
        get
        {
            RateLimitDeltaResult? delta = _selectedOption?.Delta;
            if (delta?.IsAvailable == true && delta.DeltaPercentage is double value)
            {
                return string.Format(CultureInfo.CurrentCulture, "Delta: {0:+0.##;-0.##;0}%", value);
            }

            return delta is null
                ? "Delta: 取得不可"
                : $"Delta: 取得不可（{GetUnavailableReasonText(delta.UnavailableReason)}）";
        }
    }

    public RateLimitGaugeSeverity Severity => _selectedOption?.Severity ?? RateLimitGaugeSeverity.Unknown;

    public void Update(
        IReadOnlyList<string> monitoredAgentIds,
        IReadOnlyList<RateLimitSnapshot> snapshots,
        string? activeAgentId,
        IReadOnlyList<RateLimitDeltaResult> activeAgentDeltas)
    {
        ArgumentNullException.ThrowIfNull(monitoredAgentIds);
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(activeAgentDeltas);

        string? previousAgentId = _selectedOption?.AgentId;
        string? previousLimitId = _selectedOption?.LimitId;
        string[] candidateAgentIds = monitoredAgentIds
            .Append(activeAgentId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Dictionary<string, RateLimitSnapshot> snapshotByAgentId = snapshots.ToDictionary(snapshot => snapshot.AgentId, StringComparer.Ordinal);
        Dictionary<string, RateLimitDeltaResult> deltasByLimitId = activeAgentDeltas
            .GroupBy(delta => delta.LimitId)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        Options.Clear();
        foreach (string agentId in candidateAgentIds)
        {
            if (!snapshotByAgentId.TryGetValue(agentId, out RateLimitSnapshot? snapshot))
            {
                Options.Add(CreateUnavailableOption(agentId, "レートリミット情報がありません"));
                continue;
            }

            bool isFresh = RateLimitFreshnessPolicy.IsFresh(snapshot.ObservedAt, _timeProvider.GetUtcNow(), _freshnessThreshold);
            foreach (IGrouping<string, RateLimitInfo> group in snapshot.Limits.GroupBy(limit => limit.Id))
            {
                RateLimitInfo limit = group.First();
                bool isUnique = group.Count() == 1;
                RateLimitGaugeSeverity severity = isFresh && isUnique && limit.UsedPercentage is double usedPercentage
                    ? GetSeverity(usedPercentage)
                    : RateLimitGaugeSeverity.Unknown;
                RateLimitDeltaResult? delta = agentId == activeAgentId && isUnique && deltasByLimitId.TryGetValue(limit.Id, out RateLimitDeltaResult? result)
                    ? result
                    : null;

                Options.Add(new RateLimitGaugeOption(
                    agentId,
                    GetAgentDisplayName(agentId),
                    limit.Id,
                    limit.Label,
                    severity == RateLimitGaugeSeverity.Unknown ? null : limit.UsedPercentage,
                    limit.ResetAt,
                    snapshot.ObservedAt,
                    isFresh,
                    severity,
                    delta));
            }
        }

        _unavailableMessage = null;
        _selectedOption = FindPreferredOption(previousAgentId, previousLimitId, activeAgentId);
        RaiseGaugePropertiesChanged();
    }

    public void SetUnavailable(string message)
    {
        _unavailableMessage = string.IsNullOrWhiteSpace(message) ? "レートリミット情報を取得できません" : message;
        Options.Clear();
        _selectedOption = null;
        RaiseGaugePropertiesChanged();
    }

    public void Select(RateLimitGaugeOption? option)
    {
        if (option is null || !Options.Contains(option) || EqualityComparer<RateLimitGaugeOption?>.Default.Equals(_selectedOption, option))
        {
            return;
        }

        _selectedOption = option;
        RaiseGaugePropertiesChanged();
    }

    private RateLimitGaugeOption? FindPreferredOption(string? previousAgentId, string? previousLimitId, string? activeAgentId)
    {
        RateLimitGaugeOption? previous = Options.FirstOrDefault(option => option.AgentId == previousAgentId && option.LimitId == previousLimitId);
        if (previous is not null)
        {
            return previous;
        }

        IEnumerable<RateLimitGaugeOption> activeOptions = Options.Where(option => option.AgentId == activeAgentId);
        return activeOptions
            .OrderByDescending(option => option.Severity != RateLimitGaugeSeverity.Unknown)
            .ThenByDescending(option => option.UsedPercentage)
            .FirstOrDefault()
            ?? Options
                .OrderByDescending(option => option.Severity != RateLimitGaugeSeverity.Unknown)
                .ThenByDescending(option => option.UsedPercentage)
                .FirstOrDefault();
    }

    private static RateLimitGaugeOption CreateUnavailableOption(string agentId, string message)
        => new(agentId, GetAgentDisplayName(agentId), null, message, null, null, null, IsFresh: false, RateLimitGaugeSeverity.Unknown, null);

    private static RateLimitGaugeSeverity GetSeverity(double usedPercentage)
    {
        if (usedPercentage >= _criticalThreshold)
        {
            return RateLimitGaugeSeverity.Critical;
        }

        return usedPercentage >= _warningThreshold ? RateLimitGaugeSeverity.Warning : RateLimitGaugeSeverity.Normal;
    }

    private static string GetAgentDisplayName(string agentId)
        => RateLimitAgentCatalog.All.FirstOrDefault(agent => agent.Id == agentId)?.DisplayName ?? agentId;

    private static string GetUnavailableReasonText(RateLimitDeltaUnavailableReason reason)
    {
        return reason switch
        {
            RateLimitDeltaUnavailableReason.MissingStartSnapshot => "開始時点の情報がありません",
            RateLimitDeltaUnavailableReason.MissingEndSnapshot => "終了時点の情報がありません",
            RateLimitDeltaUnavailableReason.StartSnapshotStale => "開始時点の情報が古すぎます",
            RateLimitDeltaUnavailableReason.EndSnapshotStale => "終了時点の情報が古すぎます",
            RateLimitDeltaUnavailableReason.LimitMissingInStart => "開始時点に同じ枠がありません",
            RateLimitDeltaUnavailableReason.LimitMissingInEnd => "終了時点に同じ枠がありません",
            RateLimitDeltaUnavailableReason.UsedPercentageMissing => "使用率がありません",
            RateLimitDeltaUnavailableReason.ResetBoundaryCrossed => "リセットを跨ぎました",
            RateLimitDeltaUnavailableReason.DuplicateLimitId => "重複した枠があります",
            _ => "不明な理由です",
        };
    }

    private void RaiseGaugePropertiesChanged()
    {
        foreach (string propertyName in new[]
        {
            nameof(SelectedOption),
            nameof(ProgressValue),
            nameof(StatusText),
            nameof(UsageText),
            nameof(TimingText),
            nameof(DeltaText),
            nameof(Severity),
        })
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
