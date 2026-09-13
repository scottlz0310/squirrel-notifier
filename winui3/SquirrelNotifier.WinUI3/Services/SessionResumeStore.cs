// <copyright file="SessionResumeStore.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Text.Json;
using System.Text.Json.Serialization;
using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>
/// PR・launcher role・agent ごとの session resume 情報を <c>sessions.json</c> に保存する（#303）.
/// </summary>
internal sealed class SessionResumeStore : ISessionResumeStore
{
    internal const int DefaultMaxEntries = 100;
    internal static readonly TimeSpan DefaultTtl = TimeSpan.FromDays(7);

    private static readonly JsonSerializerOptions _serializerOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _sessionsPath;
    private readonly string _tempPath;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    public SessionResumeStore()
        : this(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SquirrelNotifier"),
            TimeProvider.System)
    {
    }

    internal SessionResumeStore(string sessionsDirectory, TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(sessionsDirectory))
        {
            throw new ArgumentException("セッション保存先のディレクトリが不正です。", nameof(sessionsDirectory));
        }

        Directory.CreateDirectory(sessionsDirectory);
        _sessionsPath = Path.Combine(sessionsDirectory, "sessions.json");
        _tempPath = _sessionsPath + ".tmp";
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<SessionResumeLookupResult> TryGetAsync(
        ReviewEvent reviewEvent,
        LauncherRole role,
        string agentId,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        string key = CreateKey(reviewEvent, role, agentId);
        string normalizedWorkingDirectory = NormalizeWorkingDirectory(workingDirectory);

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SessionResumeDocument document = await LoadAsync(cancellationToken).ConfigureAwait(false);
            DateTimeOffset now = _timeProvider.GetUtcNow();
            bool changed = CleanupInvalidEntries(document);
            HashSet<string> expiredKeys = RemoveExpiredEntries(document, now);
            changed |= expiredKeys.Count > 0;
            changed |= EnforceEntryLimit(document);

            SessionResumeLookupResult result;
            if (expiredKeys.Contains(key))
            {
                result = new SessionResumeLookupResult(SessionResumeLookupStatus.Expired, null);
            }
            else if (!document.Entries.TryGetValue(key, out PersistedSessionResumeEntry? persisted)
                || persisted is null)
            {
                result = new SessionResumeLookupResult(SessionResumeLookupStatus.NotFound, null);
            }
            else if (!string.Equals(
                NormalizeWorkingDirectory(persisted.WorkingDirectory),
                normalizedWorkingDirectory,
                StringComparison.OrdinalIgnoreCase))
            {
                document.Entries.Remove(key);
                changed = true;
                result = new SessionResumeLookupResult(SessionResumeLookupStatus.WorkingDirectoryMismatch, null);
            }
            else
            {
                result = new SessionResumeLookupResult(
                    SessionResumeLookupStatus.Found,
                    new SessionResumeEntry(
                        persisted.SessionId,
                        persisted.AgentId,
                        persisted.WorkingDirectory,
                        persisted.CreatedAt,
                        persisted.LastUsedAt));
            }

            if (changed)
            {
                await SaveDocumentAsync(document, cancellationToken).ConfigureAwait(false);
            }

            return result;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task SaveAsync(
        ReviewEvent reviewEvent,
        LauncherRole role,
        string agentId,
        string workingDirectory,
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        string key = CreateKey(reviewEvent, role, agentId);
        string normalizedAgentId = NormalizeAgentId(agentId);
        string normalizedWorkingDirectory = NormalizeWorkingDirectory(workingDirectory);
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("Session ID に空の UUID は使用できません。", nameof(sessionId));
        }

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SessionResumeDocument document = await LoadAsync(cancellationToken).ConfigureAwait(false);
            DateTimeOffset now = _timeProvider.GetUtcNow();
            _ = CleanupInvalidEntries(document);
            _ = RemoveExpiredEntries(document, now);

            DateTimeOffset createdAt = now;
            if (document.Entries.TryGetValue(key, out PersistedSessionResumeEntry? existing)
                && existing is not null
                && existing.SessionId == sessionId)
            {
                createdAt = existing.CreatedAt;
            }

            document.Entries[key] = new PersistedSessionResumeEntry
            {
                SessionId = sessionId,
                AgentId = normalizedAgentId,
                WorkingDirectory = normalizedWorkingDirectory,
                CreatedAt = createdAt,
                LastUsedAt = now,
            };

            _ = EnforceEntryLimit(document);
            await SaveDocumentAsync(document, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task RemoveAsync(
        ReviewEvent reviewEvent,
        LauncherRole role,
        string agentId,
        CancellationToken cancellationToken = default)
    {
        string key = CreateKey(reviewEvent, role, agentId);

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SessionResumeDocument document = await LoadAsync(cancellationToken).ConfigureAwait(false);
            DateTimeOffset now = _timeProvider.GetUtcNow();
            bool changed = CleanupInvalidEntries(document);
            changed |= RemoveExpiredEntries(document, now).Count > 0;
            changed |= EnforceEntryLimit(document);
            changed |= document.Entries.Remove(key);

            if (changed)
            {
                await SaveDocumentAsync(document, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private static string CreateKey(ReviewEvent reviewEvent, LauncherRole role, string agentId)
    {
        ArgumentNullException.ThrowIfNull(reviewEvent);
        if (string.IsNullOrWhiteSpace(reviewEvent.Repository))
        {
            throw new ArgumentException("Repository は空にできません。", nameof(reviewEvent));
        }

        if (reviewEvent.PrNumber <= 0)
        {
            throw new ArgumentException("PR number は 1 以上である必要があります。", nameof(reviewEvent));
        }

        if (!Enum.IsDefined(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role), role, "未知の launcher role です。");
        }

        string repository = reviewEvent.Repository.Trim();
        return $"{repository}#{reviewEvent.PrNumber}|{role}|{NormalizeAgentId(agentId)}";
    }

    private static string NormalizeAgentId(string agentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        return agentId.Trim();
    }

    private static string NormalizeWorkingDirectory(string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        if (!Path.IsPathFullyQualified(workingDirectory))
        {
            throw new ArgumentException("Working directory には絶対パスが必要です。", nameof(workingDirectory));
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingDirectory));
    }

    private static bool CleanupInvalidEntries(SessionResumeDocument document)
    {
        string[] invalidKeys = document.Entries
            .Where(static pair => !IsValid(pair.Value))
            .Select(static pair => pair.Key)
            .ToArray();

        foreach (string key in invalidKeys)
        {
            document.Entries.Remove(key);
        }

        return invalidKeys.Length > 0;
    }

    private static bool IsValid(PersistedSessionResumeEntry? entry)
    {
        if (entry is null
            || entry.SessionId == Guid.Empty
            || string.IsNullOrWhiteSpace(entry.AgentId)
            || string.IsNullOrWhiteSpace(entry.WorkingDirectory)
            || entry.CreatedAt == default
            || entry.LastUsedAt == default
            || !Path.IsPathFullyQualified(entry.WorkingDirectory))
        {
            return false;
        }

        try
        {
            _ = NormalizeWorkingDirectory(entry.WorkingDirectory);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private static HashSet<string> RemoveExpiredEntries(SessionResumeDocument document, DateTimeOffset now)
    {
        DateTimeOffset cutoff = now - DefaultTtl;
        string[] expiredKeys = document.Entries
            .Where(pair => pair.Value!.LastUsedAt <= cutoff)
            .Select(static pair => pair.Key)
            .ToArray();

        foreach (string key in expiredKeys)
        {
            document.Entries.Remove(key);
        }

        return new HashSet<string>(expiredKeys, StringComparer.Ordinal);
    }

    private static bool EnforceEntryLimit(SessionResumeDocument document)
    {
        int excessCount = document.Entries.Count - DefaultMaxEntries;
        if (excessCount <= 0)
        {
            return false;
        }

        string[] keysToRemove = document.Entries
            .OrderBy(static pair => pair.Value!.LastUsedAt)
            .ThenBy(static pair => pair.Key, StringComparer.Ordinal)
            .Take(excessCount)
            .Select(static pair => pair.Key)
            .ToArray();

        foreach (string key in keysToRemove)
        {
            document.Entries.Remove(key);
        }

        return true;
    }

    private async Task<SessionResumeDocument> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_sessionsPath))
        {
            return new SessionResumeDocument();
        }

        try
        {
            string json = await File.ReadAllTextAsync(_sessionsPath, cancellationToken).ConfigureAwait(false);
            SessionResumeDocument? document = JsonSerializer.Deserialize<SessionResumeDocument>(json, _serializerOptions);
            if (document?.Entries is null)
            {
                return new SessionResumeDocument();
            }

            document.Entries = new Dictionary<string, PersistedSessionResumeEntry?>(
                document.Entries,
                StringComparer.Ordinal);
            return document;
        }
        catch (JsonException)
        {
            return new SessionResumeDocument();
        }
        catch (NotSupportedException)
        {
            return new SessionResumeDocument();
        }
        catch (IOException ex)
        {
            throw new IOException($"セッションストアを読み込めません: {_sessionsPath}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new UnauthorizedAccessException($"セッションストアを読み込む権限がありません: {_sessionsPath}", ex);
        }
    }

    private async Task SaveDocumentAsync(SessionResumeDocument document, CancellationToken cancellationToken)
    {
        try
        {
            string json = JsonSerializer.Serialize(document, _serializerOptions);
            await File.WriteAllTextAsync(_tempPath, json, cancellationToken).ConfigureAwait(false);

            // CacheService と同じく、書き込み完了後にのみ本体を置き換える。
            File.Move(_tempPath, _sessionsPath, overwrite: true);
        }
        catch (IOException ex)
        {
            throw new IOException($"セッションストアを保存できません: {_sessionsPath}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new UnauthorizedAccessException($"セッションストアを保存する権限がありません: {_sessionsPath}", ex);
        }
    }

    private sealed class SessionResumeDocument
    {
        [JsonPropertyName("entries")]
        public Dictionary<string, PersistedSessionResumeEntry?> Entries { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class PersistedSessionResumeEntry
    {
        [JsonPropertyName("sessionId")]
        public Guid SessionId { get; set; }

        [JsonPropertyName("agentId")]
        public string AgentId { get; set; } = string.Empty;

        [JsonPropertyName("workingDirectory")]
        public string WorkingDirectory { get; set; } = string.Empty;

        [JsonPropertyName("createdAt")]
        public DateTimeOffset CreatedAt { get; set; }

        [JsonPropertyName("lastUsedAt")]
        public DateTimeOffset LastUsedAt { get; set; }
    }
}
