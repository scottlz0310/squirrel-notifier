// <copyright file="RateLimitInfo.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace SquirrelNotifier.WinUI3.Models;

internal sealed class RateLimitInfo : INotifyPropertyChanged
{
    private bool _isReminderScheduled;

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("resetAt")]
    public DateTimeOffset ResetAt { get; set; }

    // 使用率（0〜100）。旧スキーマ（resetAt のみ）の payload には存在しないため null 許容とし、
    // 「未知」を「0%」と誤読しないようにする（#145）.
    [JsonPropertyName("usedPercentage")]
    public double? UsedPercentage { get; set; }

    [JsonIgnore]
    public string SourceUri { get; set; } = string.Empty;

    // 複数の ratelimit:// URI を同時に扱う場合、異なるリソース間で Id が衝突すると
    // リマインダー予約が相互に上書きされるため、URI を含めたキーで一意化する。
    [JsonIgnore]
    public string ReminderKey => $"{SourceUri}:{Id}";

    [JsonIgnore]
    public bool IsReminderScheduled
    {
        get => _isReminderScheduled;
        set
        {
            if (_isReminderScheduled != value)
            {
                _isReminderScheduled = value;
                OnPropertyChanged(nameof(IsReminderScheduled));
                OnPropertyChanged(nameof(ReminderButtonText));
            }
        }
    }

    [JsonIgnore]
    public string ResetAtDisplay => ResetAt.ToLocalTime().ToString("yyyy/MM/dd HH:mm", System.Globalization.CultureInfo.CurrentCulture);

    [JsonIgnore]
    public string ReminderButtonText => IsReminderScheduled ? "予約済み ⏰ (解除)" : "通知予約 ⏰";

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id))
        {
            throw new ArgumentException("Id cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(Label))
        {
            throw new ArgumentException("Label cannot be empty.");
        }

        if (ResetAt == default)
        {
            throw new ArgumentException("ResetAt must be a valid timestamp.");
        }

        if (UsedPercentage is < 0 or > 100)
        {
            throw new ArgumentException("UsedPercentage must be between 0 and 100.");
        }
    }

    private void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
