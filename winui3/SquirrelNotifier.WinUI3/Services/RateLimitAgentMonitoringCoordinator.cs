// <copyright file="RateLimitAgentMonitoringCoordinator.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.ComponentModel;
using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>レートリミット監視対象の選択と設定永続化を担当する.</summary>
internal sealed class RateLimitAgentMonitoringCoordinator
{
    private readonly SettingsService _settingsService;
    private bool _isInitializing = true;

    public RateLimitAgentMonitoringCoordinator(SettingsService settingsService)
    {
        ArgumentNullException.ThrowIfNull(settingsService);
        _settingsService = settingsService;
    }

    /// <summary>監視対象一覧の初期化完了を通知する.</summary>
    public void CompleteInitialization()
        => _isInitializing = false;

    /// <summary>監視対象の変更を必要な場合だけ設定へ保存する.</summary>
    /// <param name="options">監視対象の選択肢.</param>
    /// <param name="e">選択肢のプロパティ変更情報.</param>
    public void Handle(IEnumerable<RateLimitAgentOption> options, PropertyChangedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(e);

        if (_isInitializing || e.PropertyName != nameof(RateLimitAgentOption.IsMonitored))
        {
            return;
        }

        string[] monitoredIds = options
            .Where(option => option.IsMonitored)
            .Select(option => option.Id)
            .ToArray();
        _settingsService.UpdateRateLimitMonitoredAgentIds(monitoredIds);
    }
}
