// <copyright file="ReviewObservationPublishing.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>
/// review-raven への観測の送信を、アプリに組み込む（squirrel-notifier#497 の S1）.
/// code-behind（<c>App.xaml.cs</c>）には配線だけを置くため、組み立てはここに集める.
/// </summary>
internal static class ReviewObservationPublishing
{
    /// <summary>送信主体の世代を決め、観測の送信を、状態の変化と heartbeat に結び付ける.</summary>
    /// <param name="settingsService">設定。送信の有効・無効、URL、送信主体の ID は実行時に読み直す.</param>
    /// <param name="reviewStatusService">観測している起動待ち・実行中・終了の状態.</param>
    /// <param name="loggingService">ログ.</param>
    /// <param name="sender">送信。省略すると、resource-bridge-cli で送る（テストが差し替える）.</param>
    /// <returns>送信。アプリの終了時に <see cref="ReviewObservationPublisher.ShutdownAsync"/> を呼ぶ.</returns>
    internal static ReviewObservationPublisher Attach(
        SettingsService settingsService,
        ReviewStatusService reviewStatusService,
        LoggingService loggingService,
        IObservationSender? sender = null)
    {
        ArgumentNullException.ThrowIfNull(settingsService);
        ArgumentNullException.ThrowIfNull(reviewStatusService);
        ArgumentNullException.ThrowIfNull(loggingService);

        ReviewObservationPublisher publisher = new(
            reviewStatusService.GetEntriesAsync,
            sender ?? new ResourceBridgeObservationSender(settingsService),
            () => new ReviewObservationPublisherOptions(
                settingsService.Settings.ObservationPublishEnabled && !string.IsNullOrWhiteSpace(settingsService.Settings.ObservationPublishUrl),
                settingsService.Settings.ObservationProducerId),
            () => settingsService.Settings.AutoReviewStartEnabled,
            new ProducerEpochStore(settingsService.SettingsDirectory).AcquireNext(),
            loggingService);
        reviewStatusService.EntriesChanged += publisher.OnEntriesChanged;
        return publisher;
    }
}
