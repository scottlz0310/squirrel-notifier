// <copyright file="StatuslineSummaryService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Text.Json;
using System.Text.Json.Serialization;
using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>
/// agent-statusline 連携用に、reviewer 起動待ちの PR と実行中のレビューを
/// <c>statusline-summary.json</c> へアトミックに書き出す（#427）.
/// </summary>
/// <remarks>
/// 内部ストアの <c>review-cycles.json</c> とは独立した公開契約で、スキーマは
/// <c>docs/statusline-integration.md</c> に記載する。内容はこのプロセスが観測した状態だけで構成し、
/// 起動時は空のサマリを書き出し、終了時はファイルを削除する（未起動 = ファイル不在）.
/// </remarks>
internal sealed class StatuslineSummaryService
{
    internal const string FileName = "statusline-summary.json";
    internal const int SchemaVersion = 1;

    private const int _maxReplaceAttempts = 3;
    private static readonly TimeSpan _replaceRetryDelay = TimeSpan.FromMilliseconds(50);
    private static readonly JsonSerializerOptions _serializerOptions = new();

    private readonly string _summaryPath;
    private readonly string _tempPath;
    private readonly ReviewCycleCoordinator _reviewCycleCoordinator;
    private readonly ReviewEventCleanupCoordinator _cleanupCoordinator;
    private readonly LoggingService _loggingService;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly Dictionary<string, ReviewCycleState> _states = new(StringComparer.Ordinal);

    // 終了済み PR のイベントを削除した後にも、実行中だった reviewer の終了通知が同じイベントの状態を
    // 発行する。削除済みイベントの状態で PR を再登録しないよう、削除した ID を保持する
    private readonly HashSet<string> _removedEventIds = new(StringComparer.Ordinal);
    private bool _isShutDown;

    public StatuslineSummaryService(
        string outputDirectory,
        ReviewCycleCoordinator reviewCycleCoordinator,
        ReviewEventCleanupCoordinator cleanupCoordinator,
        LoggingService loggingService,
        TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new ArgumentException("statusline サマリの出力先ディレクトリが不正です。", nameof(outputDirectory));
        }

        _reviewCycleCoordinator = reviewCycleCoordinator ?? throw new ArgumentNullException(nameof(reviewCycleCoordinator));
        _cleanupCoordinator = cleanupCoordinator ?? throw new ArgumentNullException(nameof(cleanupCoordinator));
        _loggingService = loggingService ?? throw new ArgumentNullException(nameof(loggingService));
        _timeProvider = timeProvider ?? TimeProvider.System;

        Directory.CreateDirectory(outputDirectory);
        _summaryPath = Path.Combine(outputDirectory, FileName);
        _tempPath = _summaryPath + ".tmp";

        _reviewCycleCoordinator.StateChanged += OnReviewCycleStateChanged;
        _cleanupCoordinator.EventsRemoved += OnReviewEventsRemoved;
    }

    /// <summary>前回のプロセスが残したサマリを、空のサマリで置き換える.</summary>
    /// <returns>書き出しが完了したタスク.</returns>
    public Task StartAsync() => UpdateAsync(static _ => true);

    /// <summary>購読を解除し、サマリを削除する。以降の状態変化は書き出さない.</summary>
    /// <returns>削除が完了したタスク.</returns>
    public async Task ShutdownAsync()
    {
        _reviewCycleCoordinator.StateChanged -= OnReviewCycleStateChanged;
        _cleanupCoordinator.EventsRemoved -= OnReviewEventsRemoved;

        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _isShutDown = true;
            File.Delete(_tempPath);
            File.Delete(_summaryPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await _loggingService.WriteAsync(
                $"[Statusline] サマリを削除できません: {_summaryPath}: {ex.Message}").ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    internal Task ApplyStateAsync(ReviewCycleState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return UpdateAsync(states =>
        {
            if (_removedEventIds.Contains(state.LastEventId))
            {
                return false;
            }

            string key = $"{state.Repository.Trim().ToUpperInvariant()}#{state.PrNumber}";

            // 状態変化の通知はロックの外で発火されるため、到着順が前後した古い状態で上書きしない
            if (states.TryGetValue(key, out ReviewCycleState? existing) && existing.UpdatedAt > state.UpdatedAt)
            {
                return false;
            }

            states[key] = state;
            return true;
        });
    }

    internal Task RemoveEventsAsync(IReadOnlyCollection<string> eventIds)
    {
        ArgumentNullException.ThrowIfNull(eventIds);
        return UpdateAsync(states =>
        {
            _removedEventIds.UnionWith(eventIds);
            string[] keysToRemove = states
                .Where(pair => eventIds.Contains(pair.Value.LastEventId)
                    || (pair.Value.ActiveEventId is not null && eventIds.Contains(pair.Value.ActiveEventId)))
                .Select(static pair => pair.Key)
                .ToArray();

            foreach (string key in keysToRemove)
            {
                states.Remove(key);
            }

            return keysToRemove.Length > 0;
        });
    }

    private void OnReviewCycleStateChanged(object? sender, ReviewCycleStateChangedEventArgs e)
        => _ = ApplyStateAsync(e.State);

    private void OnReviewEventsRemoved(object? sender, ReviewEventsRemovedEventArgs e)
        => _ = RemoveEventsAsync(e.EventIds);

    private async Task UpdateAsync(Func<Dictionary<string, ReviewCycleState>, bool> mutate)
    {
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_isShutDown || !mutate(_states))
            {
                return;
            }

            string json = JsonSerializer.Serialize(BuildDocument(), _serializerOptions);
            await File.WriteAllTextAsync(_tempPath, json).ConfigureAwait(false);
            await ReplaceAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 次の状態変化で全体を書き直すため、ここでは記録だけ残す
            await _loggingService.WriteAsync(
                $"[Statusline] サマリを書き出せません: {_summaryPath}: {ex.Message}").ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReplaceAsync()
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(_tempPath, _summaryPath, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < _maxReplaceAttempts && ex is IOException or UnauthorizedAccessException)
            {
                // 読み取り側が削除共有なしで開いている間は置換が共有違反になる。読み取りは数ミリ秒で終わるため短く待つ
                await Task.Delay(_replaceRetryDelay).ConfigureAwait(false);
            }
        }
    }

    private SummaryDocument BuildDocument()
    {
        ReviewCycleState[] ordered = _states.Values
            .OrderBy(static state => state.UpdatedAt)
            .ThenBy(static state => state.Repository, StringComparer.Ordinal)
            .ThenBy(static state => state.PrNumber)
            .ToArray();

        QueueItem[] queueItems = ordered
            .Where(static state => state.Status == ReviewCycleStatus.AwaitingReviewer)
            .Select(static state => new QueueItem(state.Repository, state.PrNumber, state.Round, state.LastReason))
            .ToArray();

        ActiveReview[] activeReviews = ordered
            .Where(static state => state.Status == ReviewCycleStatus.ReviewerRunning)
            .Select(static state => new ActiveReview(
                state.Repository,
                state.PrNumber,
                state.ActiveRound ?? state.Round,
                state.ActiveAgent))
            .ToArray();

        return new SummaryDocument(
            SchemaVersion,
            _timeProvider.GetUtcNow().UtcDateTime,
            new QueueSummary(queueItems.Length, queueItems),
            activeReviews);
    }

    private sealed record SummaryDocument(
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: JsonPropertyName("updatedAt")] DateTime UpdatedAt,
        [property: JsonPropertyName("queue")] QueueSummary Queue,
        [property: JsonPropertyName("activeReviews")] IReadOnlyList<ActiveReview> ActiveReviews);

    private sealed record QueueSummary(
        [property: JsonPropertyName("totalWaiting")] int TotalWaiting,
        [property: JsonPropertyName("items")] IReadOnlyList<QueueItem> Items);

    private sealed record QueueItem(
        [property: JsonPropertyName("repository")] string Repository,
        [property: JsonPropertyName("prNumber")] int PrNumber,
        [property: JsonPropertyName("round")] int Round,
        [property: JsonPropertyName("reason")] string Reason);

    private sealed record ActiveReview(
        [property: JsonPropertyName("repository")] string Repository,
        [property: JsonPropertyName("prNumber")] int PrNumber,
        [property: JsonPropertyName("round")] int Round,
        [property: JsonPropertyName("agent")] string? Agent);
}
