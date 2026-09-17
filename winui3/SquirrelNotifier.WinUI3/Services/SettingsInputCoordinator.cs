// <copyright file="SettingsInputCoordinator.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using SquirrelNotifier.WinUI3.Helpers;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>
/// Settings 画面の入力反映状態と、入力値の保存を管理する.
/// </summary>
internal sealed class SettingsInputCoordinator
{
    private readonly SettingsService _settingsService;
    private readonly SettingsCoordinator _settingsCoordinator;
    private bool _isInitializing = true;

    public SettingsInputCoordinator(
        SettingsService settingsService,
        SettingsCoordinator settingsCoordinator)
    {
        ArgumentNullException.ThrowIfNull(settingsService);
        ArgumentNullException.ThrowIfNull(settingsCoordinator);

        _settingsService = settingsService;
        _settingsCoordinator = settingsCoordinator;
    }

    public bool IsInitializing => _isInitializing;

    public void CompleteInitialization()
    {
        _isInitializing = false;
    }

    public SettingsSaveResult? SaveIfReady(
        SettingsInput input,
        bool isLauncherPresetApplying)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (_isInitializing || isLauncherPresetApplying)
        {
            return null;
        }

        return _settingsCoordinator.Save(input);
    }

    public void UpdateLiveLogAutoCloseEnabled(bool enabled)
    {
        if (_isInitializing)
        {
            return;
        }

        _settingsService.UpdateLiveLogAutoCloseEnabled(enabled);
    }

    public void UpdateAutoReviewStartEnabled(bool enabled)
    {
        if (_isInitializing)
        {
            return;
        }

        _settingsService.UpdateAutoReviewStartEnabled(enabled);
    }

    public void UpdateReviewedActionVisible(bool visible)
    {
        if (_isInitializing)
        {
            return;
        }

        _settingsService.UpdateReviewedActionVisible(visible);
    }
}
