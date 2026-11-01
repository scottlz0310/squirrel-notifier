// <copyright file="ReviewEvent.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace SquirrelNotifier.WinUI3.Models;

internal sealed class ReviewEvent : INotifyPropertyChanged
{
    [JsonPropertyName("eventId")]
    public string EventId { get; set; } = string.Empty;

    [JsonPropertyName("repository")]
    public string Repository { get; set; } = string.Empty;

    [JsonPropertyName("prNumber")]
    public int PrNumber { get; set; }

    [JsonPropertyName("prUrl")]
    public string PrUrl { get; set; } = string.Empty;

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonIgnore]
    public DateTime ReceivedTime { get; set; } = DateTime.Now;

    [JsonIgnore]
    public string PrCaption => PrNumber > 0 ? $"{Repository} #{PrNumber}" : Repository;

    [JsonIgnore]
    public int CycleRound { get; private set; }

    [JsonIgnore]
    public ReviewCycleStatus CycleStatus { get; private set; }

    [JsonIgnore]
    public string CycleStatusLabel
        => CycleRound <= 0
            ? "サイクル状態未確認"
            : CycleStatus switch
            {
                ReviewCycleStatus.AwaitingReviewer => $"ラウンド {CycleRound} — reviewer 起動待ち",
                ReviewCycleStatus.ReviewerRunning => $"ラウンド {CycleRound} — reviewer 実行中",
                ReviewCycleStatus.ReviewerCompleted => $"ラウンド {CycleRound} — reviewer 実行完了（結果未確認）",
                ReviewCycleStatus.ReviewerFailed => $"ラウンド {CycleRound} — reviewer 実行失敗",
                _ => $"ラウンド {CycleRound} — 状態未確認",
            };

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void ApplyCycleState(ReviewCycleState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        bool roundChanged = CycleRound != state.Round;
        bool statusChanged = CycleStatus != state.Status;
        CycleRound = state.Round;
        CycleStatus = state.Status;

        if (roundChanged)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CycleRound)));
        }

        if (statusChanged)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CycleStatus)));
        }

        if (roundChanged || statusChanged)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CycleStatusLabel)));
        }
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(EventId))
        {
            throw new ArgumentException("EventId cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(PrUrl))
        {
            throw new ArgumentException("PrUrl cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(Repository))
        {
            throw new ArgumentException("Repository cannot be empty.");
        }
    }
}
