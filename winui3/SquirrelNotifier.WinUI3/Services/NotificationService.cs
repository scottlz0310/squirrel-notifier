// <copyright file="NotificationService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using SquirrelNotifier.WinUI3.Helpers;
using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

[ExcludeFromCodeCoverage]
internal sealed class NotificationService : INotificationService
{
    private bool _initialized;

    public event EventHandler<ReviewEvent>? ReviewEventReceived;

    public event EventHandler? OpenAppRequested;

    public void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        AppNotificationManager.Default.NotificationInvoked += OnNotificationInvoked;
        _initialized = true;
    }

    public void NotifyReviewEventReceived(string? message, string? recommendedNextAction)
    {
        try
        {
            AppNotificationBuilder builder = new AppNotificationBuilder()
                .AddText("New Review Event Received")
                .AddText(message ?? "A review event has occurred.")
                .AddText(recommendedNextAction ?? "Check the gateway/repository.");

            AppNotification notification = builder.BuildNotification();
            AppNotificationManager.Default.Show(notification);
        }
        catch
        {
            // Fallback could be added here
        }
    }

    public event EventHandler<LaunchReviewRequest>? LaunchReviewRequested;

    // 現行の購読イベント（opened / synchronized / re-review-requested）はいずれも
    // 「次のアクションは reviewer side（レビューする）」に対応する（#127 決定事項 3）。
    // reviewed side を推奨するイベント種別（レビューが投稿された等）は queue に未定義のため、
    // 未知の reason では起動ボタンを出さず「アプリを開く」で両ロールの行 UI へ誘導する。
    private static bool IsReviewerRecommended(string reason)
        => reason is "opened" or "synchronized" or "re-review-requested";

    public void NotifyReviewEvent(ReviewEvent reviewEvent)
    {
        ArgumentNullException.ThrowIfNull(reviewEvent);

        try
        {
            AppNotificationBuilder builder = new AppNotificationBuilder()
                .AddText($"{reviewEvent.Reason}: {reviewEvent.Repository}#{reviewEvent.PrNumber}")
                .AddText(reviewEvent.Message);

            if (UrlValidator.IsSafeGitHubUrl(reviewEvent.PrUrl, reviewEvent.Repository, reviewEvent.PrNumber))
            {
                builder.AddButton(new AppNotificationButton("PRを開く")
                    .AddArgument("action", "openUrl")
                    .AddArgument("url", reviewEvent.PrUrl));
            }

            if (IsReviewerRecommended(reviewEvent.Reason))
            {
                builder.AddButton(new AppNotificationButton("レビューする")
                    .AddArgument("action", "launchReview")
                    .AddArgument("role", "reviewer")
                    .AddArgument("eventId", reviewEvent.EventId));
            }

            builder.AddButton(new AppNotificationButton("アプリを開く")
                .AddArgument("action", "openApp"));

            AppNotification notification = builder.BuildNotification();
            AppNotificationManager.Default.Show(notification);

            ReviewEventReceived?.Invoke(this, reviewEvent);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to show AppNotification: {ex.Message}", ex);
        }
    }

    public void NotifyRateLimitReset(string label)
    {
        try
        {
            AppNotificationBuilder builder = new AppNotificationBuilder()
                .AddText("レートリミット解除")
                .AddText($"{label} の制限が解除されました。");

            AppNotification notification = builder.BuildNotification();
            AppNotificationManager.Default.Show(notification);
        }
        catch
        {
            // Fallback could be added here
        }
    }

    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        HandleActivation(args);
    }

    // アプリ未起動時に通知ボタンから新プロセスが起動された場合は NotificationInvoked が
    // 発火しないため、起動時アクティベーション引数（AppActivationArguments.Data）にも
    // 同じ処理を適用できるように公開している。
    public void HandleActivation(AppNotificationActivatedEventArgs args)
    {
        if (args.Arguments.TryGetValue("action", out string? action))
        {
            if (action == "openUrl" && args.Arguments.TryGetValue("url", out string? url))
            {
                if (UrlValidator.IsSafeGitHubUrl(url))
                {
                    TryOpenUrl(url);
                }
            }
            else if (action == "launchReview" && args.Arguments.TryGetValue("eventId", out string? eventId))
            {
                if (!string.IsNullOrEmpty(eventId))
                {
                    // role 引数を持たない旧形式の通知（アプリ更新前に表示されたもの）は reviewer として扱う
                    LauncherRole role = args.Arguments.TryGetValue("role", out string? roleValue) && roleValue == "reviewed"
                        ? LauncherRole.Reviewed
                        : LauncherRole.Reviewer;
                    LaunchReviewRequested?.Invoke(this, new LaunchReviewRequest(eventId, role));
                }
            }
            else if (action == "openApp")
            {
                OpenAppRequested?.Invoke(this, EventArgs.Empty);
            }
        }
        else
        {
            // Default action (body click) opens app
            OpenAppRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private static void TryOpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch
        {
            // ignore
        }
    }
}
