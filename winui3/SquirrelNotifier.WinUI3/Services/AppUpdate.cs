// <copyright file="AppUpdate.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Diagnostics;
using SquirrelNotifier.WinUI3.Helpers;
using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>更新の適用が、アプリの他の部分に求める操作.</summary>
internal sealed record AppUpdateHost(
    Func<bool> IsReviewActive,
    PendingReviewStartQueue PendingReviews,
    UpdateCarryOverStore CarryOver,
    Func<SubscriptionState> GetSubscriptionState,
    Func<Task> StopSubscriptionAsync,
    Action StartSubscription,
    Func<bool> IsWindowVisible,
    Action<string> ReportFailure,
    Action RequestReevaluation);

/// <summary>
/// 配布方式に合う更新の確認と適用を組み立てて保持する（#569）。
/// .appinstaller から導入したパッケージだけがアプリ内で適用でき、それ以外はリリースページを開く.
/// </summary>
internal sealed class AppUpdate : IDisposable
{
    private readonly LoggingService _loggingService;
    private readonly UpdateCarryOverStore _carryOver;
    private readonly UpdateApplyCoordinator? _apply;
    private readonly HttpClient? _appInstallerClient;

    public AppUpdate(
        IPackageUpdateAccessor? accessor,
        AutoUpdateService gitHubUpdates,
        SettingsService settingsService,
        LoggingService loggingService,
        IUrlOpener urlOpener,
        AppUpdateHost host,
        HttpClient? httpClient = null,
        Func<TimeSpan>? getProcessUptime = null)
    {
        _loggingService = loggingService;
        _carryOver = host.CarryOver;

        if (accessor?.AppInstallerUri is not Uri appInstallerUri)
        {
            Check = new UpdateCheckCoordinator(gitHubUpdates.CheckForUpdatesAsync, settingsService, loggingService, urlOpener);
            return;
        }

        if (httpClient is null)
        {
            _appInstallerClient = new HttpClient();
            ApplicationUserAgent.AddDefaultIfMissing(_appInstallerClient);
        }

        var updates = new AppInstallerUpdateService(
            appInstallerUri, accessor.CurrentVersion, loggingService, httpClient ?? _appInstallerClient!);
        _apply = new UpdateApplyCoordinator(
            accessor,
            host.IsReviewActive,
            getProcessUptime ?? GetCurrentProcessUptime,
            new UpdateRestartPreparation(host),
            loggingService,
            host.ReportFailure,
            host.RequestReevaluation,
            TimeProvider.System);
        Check = new UpdateCheckCoordinator(updates.CheckForUpdatesAsync, settingsService, loggingService, urlOpener, applier: _apply);
    }

    public UpdateCheckCoordinator Check { get; }

    public bool IsApplyPending => _apply?.IsPending ?? false;

    public static IPackageUpdateAccessor? CreateAccessor()
        => PackageIdentity.Exists() ? new WindowsPackageUpdateAccessor() : null;

    public Task TryApplyAsync() => _apply?.TryApplyAsync() ?? Task.CompletedTask;

    /// <summary>更新の再起動の前に保留していたレビューイベントを取り出す.</summary>
    /// <returns>保留した順のイベント。読めない場合は記録して空.</returns>
    public IReadOnlyList<ReviewEvent> TakeCarriedReviews()
    {
        try
        {
            return _carryOver.Take();
        }
        catch (Exception ex)
        {
            _ = _loggingService.WriteAsync($"[Update] 引き継いだ保留中のレビューを読めませんでした: {ex.Message}");
            return [];
        }
    }

    public void Dispose()
    {
        _apply?.Dispose();
        _appInstallerClient?.Dispose();
    }

    private static TimeSpan GetCurrentProcessUptime()
    {
        using var process = Process.GetCurrentProcess();
        return DateTime.Now - process.StartTime;
    }
}
