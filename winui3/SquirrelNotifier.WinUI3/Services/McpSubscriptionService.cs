// <copyright file="McpSubscriptionService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Diagnostics;
using System.Text.Json;
using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

internal sealed class McpSubscriptionService : IAsyncDisposable
{
    private const int _maxRecentEvents = 20;

    private readonly SettingsService _settingsService;
    private readonly INotificationService _notificationService;
    private readonly LoggingService _loggingService;
    private readonly IProcessRunner _processRunner;
    private readonly ICacheService? _cacheService;
    private readonly int _maxRetries;
    private readonly CancellationTokenSource _cts = new();
    private readonly HashSet<string> _seenEventIds = new();
    private readonly Queue<string> _eventIdQueue = new();
    private readonly Queue<CachedReviewEvent> _recentEvents = new();
    private readonly object _lock = new();

    private Task? _loopTask;
    private CancellationTokenSource? _activeProcessCts;
    private IProcessInstance? _activeProcess;
    private SubscriptionState _state = SubscriptionState.Stopped;
    private string _lastError = string.Empty;

    public event EventHandler<SubscriptionState>? StateChanged;

    public event EventHandler<string>? StatusTextChanged;

    public SubscriptionState State
    {
        get => _state;
        private set
        {
            if (_state != value)
            {
                _state = value;
                StateChanged?.Invoke(this, _state);
            }
        }
    }

    public string LastError
    {
        get => _lastError;
        private set => _lastError = value;
    }

    public McpSubscriptionService(
        SettingsService settingsService,
        INotificationService notificationService,
        LoggingService loggingService,
        IProcessRunner? processRunner = null,
        int maxRetries = 5,
        ICacheService? cacheService = null)
    {
        _settingsService = settingsService;
        _notificationService = notificationService;
        _loggingService = loggingService;
        _processRunner = processRunner ?? new ProcessRunner();
        _maxRetries = maxRetries;
        _cacheService = cacheService;
    }

    public void Start()
    {
        if (State == SubscriptionState.Running || State == SubscriptionState.Starting)
        {
            return;
        }

        LastError = string.Empty;
        State = SubscriptionState.Starting;
        _loopTask = Task.Run(() => RunSubscriptionLoopAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        State = SubscriptionState.Stopping;

        // Cancel process first to stop immediately
        _activeProcessCts?.Cancel();
        if (_activeProcess != null)
        {
            try
            {
                _activeProcess.Kill(entireProcessTree: true);
            }
            catch
            {
                // ignore
            }
        }

        if (_loopTask != null)
        {
            try
            {
                await _loopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
            finally
            {
                _loopTask = null;
            }
        }

        State = SubscriptionState.Stopped;
        await LogAsync("Subscription service stopped by user.").ConfigureAwait(false);
    }

    public static List<string> ParseArguments(string arguments)
    {
        List<string> result = new List<string>();
        if (string.IsNullOrEmpty(arguments))
        {
            return result;
        }

        var current = new System.Text.StringBuilder();
        bool inQuotes = false;
        bool hasArg = false;

        for (int i = 0; i < arguments.Length; i++)
        {
            char c = arguments[i];

            if (c == '\\')
            {
                int backslashCount = 0;
                while (i < arguments.Length && arguments[i] == '\\')
                {
                    backslashCount++;
                    i++;
                }

                if (i < arguments.Length && arguments[i] == '\"')
                {
                    int n = backslashCount / 2;
                    current.Append('\\', n);
                    hasArg = true;

                    if (backslashCount % 2 != 0)
                    {
                        current.Append('\"');
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else
                {
                    current.Append('\\', backslashCount);
                    hasArg = true;
                    i--;
                }
            }
            else if (c == '\"')
            {
                inQuotes = !inQuotes;
                hasArg = true;
            }
            else if ((c == ' ' || c == '\t') && !inQuotes)
            {
                if (hasArg)
                {
                    result.Add(current.ToString());
                    current.Clear();
                    hasArg = false;
                }
            }
            else
            {
                current.Append(c);
                hasArg = true;
            }
        }

        if (hasArg)
        {
            result.Add(current.ToString());
        }

        return result;
    }

    public async Task<bool> PreflightCheckAsync(CancellationToken token)
    {
        _activeProcessCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        CancellationToken processToken = _activeProcessCts.Token;

        try
        {
            AppSettings settings = _settingsService.Settings;
            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = SettingsService.ResolveCommandPath(settings.SubscriberCommandPath),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("--help");

            using (_activeProcess = _processRunner.Start(psi))
            {
                Task<string> stdoutTask = _activeProcess.StandardOutput.ReadToEndAsync(processToken);
                Task<string> stderrTask = _activeProcess.StandardError.ReadToEndAsync(processToken);

                await _activeProcess.WaitForExitAsync(processToken).ConfigureAwait(false);

                await stdoutTask.ConfigureAwait(false);
                await stderrTask.ConfigureAwait(false);

                return _activeProcess.ExitCode == 0;
            }
        }
        catch (Exception ex)
        {
            LastError = $"Preflight check failed: {ex.Message}";
            await LogAsync($"Preflight check error: {ex.Message}").ConfigureAwait(false);
            return false;
        }
        finally
        {
            _activeProcess = null;
            _activeProcessCts?.Dispose();
            _activeProcessCts = null;
        }
    }

    private async Task RunSubscriptionLoopAsync(CancellationToken token)
    {
        await LogAsync("Starting subscription service loop...").ConfigureAwait(false);

        await RestoreCacheAsync().ConfigureAwait(false);

        // Preflight check
        bool preflightPassed = await PreflightCheckAsync(token).ConfigureAwait(false);
        if (!preflightPassed)
        {
            State = SubscriptionState.Error;
            ReportStatus("Preflight check failed.");
            return;
        }

        State = SubscriptionState.Running;
        ReportStatus("Subscribed.");

        int retryCount = 0;
        int retryDelayMs = 1000;

        while (!token.IsCancellationRequested)
        {
            _activeProcessCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            CancellationToken processToken = _activeProcessCts.Token;

            try
            {
                AppSettings settings = _settingsService.Settings;
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = SettingsService.ResolveCommandPath(settings.SubscriberCommandPath),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };

                // Add token env
                string? tokenValue = Environment.GetEnvironmentVariable("MCP_PROBE_AUTH_TOKEN");
                if (!string.IsNullOrEmpty(tokenValue))
                {
                    psi.Environment["MCP_PROBE_AUTH_TOKEN"] = tokenValue;
                }

                // Add arguments
                if (!string.IsNullOrWhiteSpace(settings.SubscriberArguments))
                {
                    List<string> args = ParseArguments(settings.SubscriberArguments);
                    foreach (string arg in args)
                    {
                        psi.ArgumentList.Add(arg);
                    }
                }

                psi.ArgumentList.Add("--url");
                psi.ArgumentList.Add(settings.GatewayUrl);

                psi.ArgumentList.Add("--uri");
                psi.ArgumentList.Add(settings.ResourceUri);

                psi.ArgumentList.Add("--timeout-ms");
                psi.ArgumentList.Add(settings.NotificationTimeoutMs.ToString(System.Globalization.CultureInfo.InvariantCulture));

                psi.ArgumentList.Add("--json");

                await LogAsync($"Launching subscriber for resource: {settings.ResourceUri}").ConfigureAwait(false);

                using (_activeProcess = _processRunner.Start(psi))
                {
                    Task<string> stdoutTask = _activeProcess.StandardOutput.ReadToEndAsync(processToken);
                    Task<string> stderrTask = _activeProcess.StandardError.ReadToEndAsync(processToken);

                    await _activeProcess.WaitForExitAsync(processToken).ConfigureAwait(false);

                    string stdout = await stdoutTask.ConfigureAwait(false);
                    string stderr = await stderrTask.ConfigureAwait(false);

                    if (_activeProcess.ExitCode != 0)
                    {
                        throw new InvalidOperationException($"Subscriber process exited with non-zero code {_activeProcess.ExitCode}. Stderr: {stderr.Trim()}");
                    }

                    if (string.IsNullOrWhiteSpace(stdout))
                    {
                        throw new JsonException("Subscriber process output was empty.");
                    }

                    // Parse JSON
                    SubscriptionResult? result = JsonSerializer.Deserialize<SubscriptionResult>(stdout);
                    if (result == null)
                    {
                        throw new JsonException("Failed to deserialize subscriber output JSON.");
                    }

                    result.Validate();

                    if (result.Route == "failed" || result.Route == "timeout" || result.ErrorCode != null)
                    {
                        throw new InvalidOperationException($"Subscription failure: Route={result.Route}, ErrorCode={result.ErrorCode}, Message={result.FinalText}");
                    }

                    if (result.Route == "subscription" && result.NotificationReceived == true)
                    {
                        await LogAsync($"Notification payload received: {result.FinalText}").ConfigureAwait(false);

                        List<ReviewEvent> reviewEvents = ReviewEventParser.Parse(result.FinalText);
                        if (reviewEvents.Count == 0)
                        {
                            await LogAsync($"Warning: Malformed or unsupported review event payload received: {result.FinalText}").ConfigureAwait(false);
                        }
                        else
                        {
                            foreach (ReviewEvent reviewEvent in reviewEvents)
                            {
                                if (HasBeenSeen(reviewEvent.EventId))
                                {
                                    await LogAsync($"Duplicate event ignored: {reviewEvent.EventId}").ConfigureAwait(false);
                                }
                                else
                                {
                                    try
                                    {
                                        _notificationService.NotifyReviewEvent(reviewEvent);
                                        MarkAsSeen(reviewEvent.EventId, reviewEvent);
                                        await PersistCacheAsync().ConfigureAwait(false);
                                    }
                                    catch (Exception notifyEx)
                                    {
                                        await LogAsync($"Error: Failed to show Windows notification: {notifyEx.Message}").ConfigureAwait(false);
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        // Some other state, e.g. normal exit but not a notification
                        await LogAsync($"Subscriber finished execution. Route={result.Route}").ConfigureAwait(false);
                    }
                }

                retryCount = 0;
                retryDelayMs = 1000;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested || _activeProcessCts?.IsCancellationRequested == true)
            {
                // Loop or process cancellation (StopAsync/DisposeAsync) — do not retry
                break;
            }
            catch (Exception ex)
            {
                retryCount++;
                string friendlyMessage = GetFriendlyErrorMessage(ex.Message);
                string tag = GetErrorTag(ex.Message);

                if (retryCount > _maxRetries)
                {
                    LastError = friendlyMessage;
                    State = SubscriptionState.Error;
                    ReportStatus($"Error: {friendlyMessage}");
                    await LogAsync($"Subscription loop error (max retries exceeded) {tag}: {ex.Message}").ConfigureAwait(false);
                    break;
                }

                await LogAsync($"Subscription loop error (retry {retryCount}/{_maxRetries}) {tag}: {ex.Message}").ConfigureAwait(false);
                ReportStatus($"Retrying ({retryCount}/{_maxRetries})...");

                try
                {
                    await Task.Delay(retryDelayMs, processToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                retryDelayMs = Math.Min(retryDelayMs * 2, 32000);
            }
            finally
            {
                _activeProcess = null;
                _activeProcessCts?.Dispose();
                _activeProcessCts = null;
            }
        }
    }

    private void ReportStatus(string text)
    {
        StatusTextChanged?.Invoke(this, text);
    }

    private async Task LogAsync(string message)
    {
        await _loggingService.WriteAsync(message).ConfigureAwait(false);
    }

    private bool HasBeenSeen(string eventId)
    {
        if (string.IsNullOrEmpty(eventId))
        {
            return false;
        }

        lock (_lock)
        {
            return _seenEventIds.Contains(eventId);
        }
    }

    private void MarkAsSeen(string eventId, ReviewEvent? reviewEvent = null)
    {
        if (string.IsNullOrEmpty(eventId))
        {
            return;
        }

        lock (_lock)
        {
            if (_seenEventIds.Add(eventId))
            {
                _eventIdQueue.Enqueue(eventId);

                while (_eventIdQueue.Count > 100)
                {
                    string oldId = _eventIdQueue.Dequeue();
                    _seenEventIds.Remove(oldId);
                }
            }

            if (reviewEvent != null)
            {
                _recentEvents.Enqueue(new CachedReviewEvent
                {
                    EventId = reviewEvent.EventId,
                    Repository = reviewEvent.Repository,
                    PrNumber = reviewEvent.PrNumber,
                    PrUrl = reviewEvent.PrUrl,
                    Reason = reviewEvent.Reason,
                    Message = reviewEvent.Message,
                    ReceivedTime = reviewEvent.ReceivedTime,
                });

                while (_recentEvents.Count > _maxRecentEvents)
                {
                    _recentEvents.Dequeue();
                }
            }
        }
    }

    private async Task RestoreCacheAsync()
    {
        if (_cacheService == null)
        {
            return;
        }

        try
        {
            NotificationCache cache = await _cacheService.LoadAsync().ConfigureAwait(false);

            lock (_lock)
            {
                foreach (string id in cache.SeenEventIds)
                {
                    if (_seenEventIds.Add(id))
                    {
                        _eventIdQueue.Enqueue(id);
                    }
                }

                while (_eventIdQueue.Count > 100)
                {
                    string oldId = _eventIdQueue.Dequeue();
                    _seenEventIds.Remove(oldId);
                }

                foreach (CachedReviewEvent ev in cache.RecentEvents)
                {
                    _recentEvents.Enqueue(ev);
                }

                while (_recentEvents.Count > _maxRecentEvents)
                {
                    _recentEvents.Dequeue();
                }
            }

            await LogAsync($"Cache restored: {cache.SeenEventIds.Count} seen IDs, {cache.RecentEvents.Count} recent events.").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await LogAsync($"Warning: Failed to restore cache: {ex.Message}").ConfigureAwait(false);
        }
    }

    private async Task PersistCacheAsync()
    {
        if (_cacheService == null)
        {
            return;
        }

        NotificationCache cache;
        lock (_lock)
        {
            cache = new NotificationCache
            {
                SeenEventIds = new List<string>(_eventIdQueue),
                RecentEvents = new List<CachedReviewEvent>(_recentEvents),
            };
        }

        try
        {
            await _cacheService.SaveAsync(cache).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await LogAsync($"[WARN] Cache save failed: {ex.Message}").ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _activeProcessCts?.Cancel();
        if (_activeProcess != null)
        {
            try
            {
                _activeProcess.Kill(entireProcessTree: true);
            }
            catch
            {
                // ignore
            }

            _activeProcess.Dispose();
        }

        if (_loopTask != null)
        {
            try
            {
                await _loopTask.ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }
        }

        _cts.Dispose();
        _activeProcessCts?.Dispose();
    }

    private static string GetFriendlyErrorMessage(string rawError)
    {
        if (string.IsNullOrEmpty(rawError))
        {
            return "不明なエラーが発生しました。";
        }

        if (rawError.Contains("fetch failed", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("ECONNREFUSED", StringComparison.OrdinalIgnoreCase))
        {
            return "mcp-gateway への接続に失敗しました。mcp-gateway コンテナが起動しているか、または Gateway URL の設定が正しいか確認してください。";
        }

        if (rawError.Contains("404 page not found", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("Error POSTing to endpoint: 404", StringComparison.OrdinalIgnoreCase))
        {
            return "指定されたエンドポイントが見つかりませんでした (404)。Gateway URL のポート番号やパスプレフィックス、または Resource URI が正しいか確認してください。";
        }

        if (rawError.Contains("401", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("Forbidden", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("auth", StringComparison.OrdinalIgnoreCase))
        {
            return "mcp-gateway で認証エラーが発生しました。認証トークン（MCP_PROBE_AUTH_TOKEN）の設定を確認してください。";
        }

        return $"予期しないエラーが発生しました: {rawError}";
    }

    private static string GetErrorTag(string rawError)
    {
        if (string.IsNullOrEmpty(rawError))
        {
            return "[UNKNOWN_ERROR]";
        }

        if (rawError.Contains("fetch failed", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("ECONNREFUSED", StringComparison.OrdinalIgnoreCase))
        {
            return "[CONN_REFUSED]";
        }

        if (rawError.Contains("404 page not found", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("Error POSTing to endpoint: 404", StringComparison.OrdinalIgnoreCase))
        {
            return "[HTTP_404]";
        }

        if (rawError.Contains("401", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("Forbidden", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("auth", StringComparison.OrdinalIgnoreCase))
        {
            return "[AUTH_ERROR]";
        }

        return "[GENERAL_ERROR]";
    }
}

/// <summary>
/// Mcp subscription states.
/// </summary>
internal enum SubscriptionState
{
    /// <summary>
    /// Service is stopped.
    /// </summary>
    Stopped,

    /// <summary>
    /// Service is starting.
    /// </summary>
    Starting,

    /// <summary>
    /// Service is running and subscribed.
    /// </summary>
    Running,

    /// <summary>
    /// Service is stopping.
    /// </summary>
    Stopping,

    /// <summary>
    /// Service has encountered an error.
    /// </summary>
    Error,
}
