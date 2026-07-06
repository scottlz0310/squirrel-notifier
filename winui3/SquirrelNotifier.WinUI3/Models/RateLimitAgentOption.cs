// <copyright file="RateLimitAgentOption.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.ComponentModel;

namespace SquirrelNotifier.WinUI3.Models;

/// <summary>
/// Settings の「レートリミット監視対象」チェックボックス一覧の表示用アイテム.
/// </summary>
internal sealed class RateLimitAgentOption(string id, string displayName, bool isAvailable) : INotifyPropertyChanged
{
    private bool _isMonitored;

    public string Id { get; } = id;

    public string DisplayName { get; } = displayName;

    public bool IsAvailable { get; } = isAvailable;

    public bool IsMonitored
    {
        get => _isMonitored;
        set
        {
            if (_isMonitored != value)
            {
                _isMonitored = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsMonitored)));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
