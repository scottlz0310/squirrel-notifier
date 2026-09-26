// <copyright file="ReviewCycleStore.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Text.Json;
using System.Text.Json.Serialization;
using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>
/// PR 単位のレビューサイクル状態を <c>review-cycles.json</c> に保存する.
/// </summary>
internal sealed class ReviewCycleStore : IReviewCycleStore
{
    internal const int DefaultMaxEntries = 100;
    internal static readonly TimeSpan DefaultTtl = TimeSpan.FromDays(7);

    private static readonly JsonSerializerOptions _serializerOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _cyclesPath;
    private readonly string _tempPath;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    public ReviewCycleStore()
        : this(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SquirrelNotifier"),
            TimeProvider.System)
    {
    }

    internal ReviewCycleStore(string cyclesDirectory, TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(cyclesDirectory))
        {
            throw new ArgumentException("レビューサイクル保存先のディレクトリが不正です。", nameof(cyclesDirectory));
        }

        Directory.CreateDirectory(cyclesDirectory);
        _cyclesPath = Path.Combine(cyclesDirectory, "review-cycles.json");
        _tempPath = _cyclesPath + ".tmp";
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ReviewCycleState?> TryGetAsync(
        string repository,
        int prNumber,
        CancellationToken cancellationToken = default)
    {
        string key = CreateKey(repository, prNumber);

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ReviewCycleDocument document = await LoadAsync(cancellationToken).ConfigureAwait(false);
            DateTimeOffset now = _timeProvider.GetUtcNow();
            bool changed = Cleanup(document, now);
            ReviewCycleState? state = document.Cycles.TryGetValue(key, out PersistedReviewCycleState? persisted)
                && persisted is not null
                ? ToState(persisted)
                : null;

            if (changed)
            {
                await SaveDocumentAsync(document, cancellationToken).ConfigureAwait(false);
            }

            return state;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task SaveAsync(
        ReviewCycleState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        string key = CreateKey(state.Repository, state.PrNumber);

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ReviewCycleDocument document = await LoadAsync(cancellationToken).ConfigureAwait(false);
            DateTimeOffset now = _timeProvider.GetUtcNow();
            _ = Cleanup(document, now);
            document.Cycles[key] = FromState(state);
            _ = EnforceEntryLimit(document);
            await SaveDocumentAsync(document, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private static string CreateKey(string repository, int prNumber)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        if (prNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(prNumber), "PR number は 1 以上である必要があります。");
        }

        return $"{repository.Trim().ToUpperInvariant()}#{prNumber}";
    }

    private static ReviewCycleState ToState(PersistedReviewCycleState persisted)
        => new(
            persisted.Repository,
            persisted.PrNumber,
            persisted.Round,
            persisted.LastEventId,
            persisted.LastReason,
            persisted.Status,
            persisted.ActiveEventId,
            persisted.ActiveRound,
            persisted.UpdatedAt,
            persisted.ProcessedEventIds ?? [],
            persisted.ActiveAgent);

    private static PersistedReviewCycleState FromState(ReviewCycleState state)
        => new()
        {
            Repository = state.Repository.Trim(),
            PrNumber = state.PrNumber,
            Round = state.Round,
            LastEventId = state.LastEventId,
            LastReason = state.LastReason,
            Status = state.Status,
            ActiveEventId = state.ActiveEventId,
            ActiveRound = state.ActiveRound,
            ActiveAgent = state.ActiveAgent,
            UpdatedAt = state.UpdatedAt,
            ProcessedEventIds = state.ProcessedEventIds.Distinct(StringComparer.Ordinal).ToList(),
        };

    private static bool Cleanup(ReviewCycleDocument document, DateTimeOffset now)
    {
        string[] invalidKeys = document.Cycles
            .Where(static pair => !IsValid(pair.Value))
            .Select(static pair => pair.Key)
            .ToArray();

        foreach (string key in invalidKeys)
        {
            document.Cycles.Remove(key);
        }

        DateTimeOffset cutoff = now - DefaultTtl;
        string[] expiredKeys = document.Cycles
            .Where(pair => pair.Value!.UpdatedAt <= cutoff)
            .Select(static pair => pair.Key)
            .ToArray();

        foreach (string key in expiredKeys)
        {
            document.Cycles.Remove(key);
        }

        return invalidKeys.Length > 0 || expiredKeys.Length > 0 || EnforceEntryLimit(document);
    }

    private static bool IsValid(PersistedReviewCycleState? state)
        => state is not null
            && !string.IsNullOrWhiteSpace(state.Repository)
            && state.PrNumber > 0
            && state.Round > 0
            && !string.IsNullOrWhiteSpace(state.LastEventId)
            && state.UpdatedAt != default
            && Enum.IsDefined(state.Status)
            && state.ProcessedEventIds is not null;

    private static bool EnforceEntryLimit(ReviewCycleDocument document)
    {
        int excessCount = document.Cycles.Count - DefaultMaxEntries;
        if (excessCount <= 0)
        {
            return false;
        }

        string[] keysToRemove = document.Cycles
            .OrderBy(static pair => pair.Value!.UpdatedAt)
            .ThenBy(static pair => pair.Key, StringComparer.Ordinal)
            .Take(excessCount)
            .Select(static pair => pair.Key)
            .ToArray();

        foreach (string key in keysToRemove)
        {
            document.Cycles.Remove(key);
        }

        return true;
    }

    private async Task<ReviewCycleDocument> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_cyclesPath))
        {
            return new ReviewCycleDocument();
        }

        try
        {
            string json = await File.ReadAllTextAsync(_cyclesPath, cancellationToken).ConfigureAwait(false);
            ReviewCycleDocument? document = JsonSerializer.Deserialize<ReviewCycleDocument>(json, _serializerOptions);
            if (document?.Cycles is null)
            {
                return new ReviewCycleDocument();
            }

            document.Cycles = new Dictionary<string, PersistedReviewCycleState?>(
                document.Cycles,
                StringComparer.Ordinal);
            return document;
        }
        catch (JsonException)
        {
            return new ReviewCycleDocument();
        }
        catch (NotSupportedException)
        {
            return new ReviewCycleDocument();
        }
        catch (IOException ex)
        {
            throw new IOException($"レビューサイクルストアを読み込めません: {_cyclesPath}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new UnauthorizedAccessException($"レビューサイクルストアを読み込む権限がありません: {_cyclesPath}", ex);
        }
    }

    private async Task SaveDocumentAsync(ReviewCycleDocument document, CancellationToken cancellationToken)
    {
        try
        {
            string json = JsonSerializer.Serialize(document, _serializerOptions);
            await File.WriteAllTextAsync(_tempPath, json, cancellationToken).ConfigureAwait(false);
            File.Move(_tempPath, _cyclesPath, overwrite: true);
        }
        catch (IOException ex)
        {
            throw new IOException($"レビューサイクルストアを保存できません: {_cyclesPath}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new UnauthorizedAccessException($"レビューサイクルストアを保存する権限がありません: {_cyclesPath}", ex);
        }
    }

    private sealed class ReviewCycleDocument
    {
        [JsonPropertyName("cycles")]
        public Dictionary<string, PersistedReviewCycleState?> Cycles { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class PersistedReviewCycleState
    {
        [JsonPropertyName("repository")]
        public string Repository { get; set; } = string.Empty;

        [JsonPropertyName("prNumber")]
        public int PrNumber { get; set; }

        [JsonPropertyName("round")]
        public int Round { get; set; }

        [JsonPropertyName("lastEventId")]
        public string LastEventId { get; set; } = string.Empty;

        [JsonPropertyName("lastReason")]
        public string LastReason { get; set; } = string.Empty;

        [JsonPropertyName("status")]
        public ReviewCycleStatus Status { get; set; }

        [JsonPropertyName("activeEventId")]
        public string? ActiveEventId { get; set; }

        [JsonPropertyName("activeRound")]
        public int? ActiveRound { get; set; }

        [JsonPropertyName("activeAgent")]
        public string? ActiveAgent { get; set; }

        [JsonPropertyName("updatedAt")]
        public DateTimeOffset UpdatedAt { get; set; }

        [JsonPropertyName("processedEventIds")]
        public List<string>? ProcessedEventIds { get; set; }
    }
}
