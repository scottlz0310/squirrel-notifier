// <copyright file="McpSubscriptionService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

internal sealed class McpSubscriptionService : IAsyncDisposable
{
    private const int _maxRecentEvents = 20;
    private const string _authenticationRequiredErrorTag = "[AUTH_REQUIRED]";

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
    private readonly ConcurrentDictionary<string, IProcessInstance> _activeProcesses = new();
    private readonly SemaphoreSlim _cacheSemaphore = new(1, 1);

    private Task? _loopTask;
    private CancellationTokenSource? _activeProcessCts;
    private CancellationTokenSource? _stopCts;
    private IProcessInstance? _activeProcess;
    private SubscriptionState _state = SubscriptionState.Stopped;
    private string _lastError = string.Empty;
    private bool _isAuthenticationRequired;

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

    public bool IsAuthenticationRequired
    {
        get => _isAuthenticationRequired;
        private set => _isAuthenticationRequired = value;
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
        IsAuthenticationRequired = false;
        State = SubscriptionState.Starting;
        _stopCts?.Dispose();
        _stopCts = new CancellationTokenSource();
        _loopTask = Task.Run(() => RunSubscriptionLoopAsync(_stopCts.Token));
    }

    public async Task StopAsync()
    {
        State = SubscriptionState.Stopping;

        // Cancel the subscription loop (including backoff delays)
        _stopCts?.Cancel();

        // Cancel all active processes immediately
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

        foreach (IProcessInstance process in _activeProcesses.Values)
        {
            try
            {
                process.Kill(entireProcessTree: true);
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
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
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

        AppSettings settings = _settingsService.Settings;
        List<string> uris = settings.ResourceUris.Count > 0
            ? settings.ResourceUris
            : new List<string> { settings.ResourceUri };

        if (uris.Count == 1)
        {
            await RunSingleUriLoopAsync(uris[0], token).ConfigureAwait(false);
        }
        else
        {
            await LogAsync($"Starting parallel subscription for {uris.Count} URIs.").ConfigureAwait(false);
            Task[] tasks = uris.Select(uri => RunSingleUriLoopAsync(uri, token)).ToArray();
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
    }

    private async Task RunSingleUriLoopAsync(string resourceUri, CancellationToken token)
    {
        int retryCount = 0;
        int retryDelayMs = 1000;

        while (!token.IsCancellationRequested)
        {
            using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            CancellationToken processToken = loopCts.Token;
            string processKey = $"{resourceUri}:{Environment.TickCount64}";

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
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8,
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
                psi.ArgumentList.Add(resourceUri);

                psi.ArgumentList.Add("--timeout-ms");
                psi.ArgumentList.Add(settings.NotificationTimeoutMs.ToString(System.Globalization.CultureInfo.InvariantCulture));

                psi.ArgumentList.Add("--json");

                await LogAsync($"Launching subscriber for resource: {resourceUri}").ConfigureAwait(false);

                IProcessInstance process = _processRunner.Start(psi);
                _activeProcesses[processKey] = process;

                try
                {
                    Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(processToken);
                    Task<string> stderrTask = process.StandardError.ReadToEndAsync(processToken);

                    await process.WaitForExitAsync(processToken).ConfigureAwait(false);

                    string stdout = await stdoutTask.ConfigureAwait(false);
                    string stderr = await stderrTask.ConfigureAwait(false);

                    // NOTIFICATION_TIMEOUT は待機時間内にイベントが無かっただけの正常な満了。
                    // subscriber は timeout でも非ゼロ終了するため、終了コードより先に stdout で判定し、
                    // リトライを消費せずに再購読へ進む。
                    SubscriptionResult? result = null;
                    JsonException? parseError = null;
                    if (!string.IsNullOrWhiteSpace(stdout))
                    {
                        try
                        {
                            result = JsonSerializer.Deserialize<SubscriptionResult>(stdout);
                        }
                        catch (JsonException ex)
                        {
                            parseError = ex;
                        }
                    }

                    bool isIdleTimeout = result?.Route == "timeout" && result.ErrorCode == "NOTIFICATION_TIMEOUT";

                    if (isIdleTimeout)
                    {
                        await LogAsync($"[{resourceUri}] No notification within timeout window; re-subscribing.").ConfigureAwait(false);
                    }
                    else if (process.ExitCode != 0)
                    {
                        string errorCode = string.IsNullOrWhiteSpace(result?.ErrorCode) ? "unknown" : result.ErrorCode;
                        string stdoutDetail = !string.IsNullOrWhiteSpace(result?.FinalText) ? result.FinalText : stdout.Trim();
                        string parseErrorDetail = parseError != null ? $" ParseError: {parseError.Message}." : string.Empty;
                        throw new SubscriberProcessException($"Subscriber process exited with non-zero code {process.ExitCode}. ErrorCode: {errorCode}. Stdout: {stdoutDetail}.{parseErrorDetail} Stderr: {stderr.Trim()}", result?.ErrorCode);
                    }
                    else if (parseError != null)
                    {
                        throw parseError;
                    }
                    else if (result == null)
                    {
                        throw new JsonException(string.IsNullOrWhiteSpace(stdout)
                            ? "Subscriber process output was empty."
                            : "Failed to deserialize subscriber output JSON.");
                    }
                    else
                    {
                        result.Validate();

                        if (result.Route == "failed" || result.Route == "timeout" || result.ErrorCode != null)
                        {
                            throw new SubscriberProcessException($"Subscription failure: Route={result.Route}, ErrorCode={result.ErrorCode}, Message={result.FinalText}", result.ErrorCode);
                        }

                        if (result.Route == "subscription" && result.NotificationReceived == true)
                        {
                            await LogAsync($"Notification payload received: {result.FinalText}").ConfigureAwait(false);

                            List<ReviewEvent> reviewEvents = ReviewEventParser.Parse(result.FinalText, resourceUri);
                            if (reviewEvents.Count == 0)
                            {
                                await LogAsync($"Warning: Malformed or unsupported review event payload received: {result.FinalText}").ConfigureAwait(false);
                            }
                            else
                            {
                                foreach (ReviewEvent reviewEvent in reviewEvents)
                                {
                                    if (TryMarkAsSeen(reviewEvent.EventId, reviewEvent))
                                    {
                                        try
                                        {
                                            _notificationService.NotifyReviewEvent(reviewEvent);
                                            await PersistCacheAsync().ConfigureAwait(false);
                                        }
                                        catch (Exception notifyEx)
                                        {
                                            // 通知失敗時は claim を解除し、ディスクにも反映して再起動後の duplicate 扱いを防ぐ
                                            UndoMarkAsSeen(reviewEvent.EventId);
                                            await PersistCacheAsync().ConfigureAwait(false);
                                            await LogAsync($"Error: Failed to show Windows notification: {notifyEx.Message}").ConfigureAwait(false);
                                        }
                                    }
                                    else
                                    {
                                        await LogAsync($"Duplicate event ignored: {reviewEvent.EventId}").ConfigureAwait(false);
                                    }
                                }
                            }
                        }
                        else
                        {
                            await LogAsync($"Subscriber finished execution. Route={result.Route}").ConfigureAwait(false);
                        }
                    }
                }
                finally
                {
                    _activeProcesses.TryRemove(processKey, out _);
                    process.Dispose();
                }

                retryCount = 0;
                retryDelayMs = 1000;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Loop cancellation (StopAsync/DisposeAsync) — do not retry
                break;
            }
            catch (Exception ex)
            {
                retryCount++;
                string? structuredErrorCode = (ex as SubscriberProcessException)?.ErrorCode;
                (string friendlyMessage, string tag) = GetErrorInfo(ex.Message, structuredErrorCode);

                if (retryCount > _maxRetries)
                {
                    LastError = friendlyMessage;
                    IsAuthenticationRequired = tag == _authenticationRequiredErrorTag;
                    State = SubscriptionState.Error;
                    ReportStatus($"Error: {friendlyMessage}");
                    await LogAsync($"[{resourceUri}] Subscription loop error (max retries exceeded) {tag}: {ex.Message}").ConfigureAwait(false);
                    break;
                }

                await LogAsync($"[{resourceUri}] Subscription loop error (retry {retryCount}/{_maxRetries}) {tag}: {ex.Message}").ConfigureAwait(false);
                ReportStatus($"Retrying ({retryCount}/{_maxRetries})...");

                try
                {
                    await Task.Delay(retryDelayMs, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                retryDelayMs = Math.Min(retryDelayMs * 2, 32000);
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

    private bool TryMarkAsSeen(string eventId, ReviewEvent? reviewEvent = null)
    {
        if (string.IsNullOrEmpty(eventId))
        {
            return false;
        }

        lock (_lock)
        {
            if (!_seenEventIds.Add(eventId))
            {
                return false;
            }

            _eventIdQueue.Enqueue(eventId);

            while (_eventIdQueue.Count > 100)
            {
                string oldId = _eventIdQueue.Dequeue();
                _seenEventIds.Remove(oldId);
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

            return true;
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

    private void UndoMarkAsSeen(string eventId)
    {
        if (string.IsNullOrEmpty(eventId))
        {
            return;
        }

        lock (_lock)
        {
            _seenEventIds.Remove(eventId);
            Queue<string> newQueue = new(_eventIdQueue.Where(id => id != eventId));
            _eventIdQueue.Clear();
            foreach (string id in newQueue)
            {
                _eventIdQueue.Enqueue(id);
            }

            Queue<CachedReviewEvent> newEvents = new(_recentEvents.Where(e => e.EventId != eventId));
            _recentEvents.Clear();
            foreach (CachedReviewEvent e in newEvents)
            {
                _recentEvents.Enqueue(e);
            }
        }
    }

    private async Task PersistCacheAsync()
    {
        if (_cacheService == null)
        {
            return;
        }

        await _cacheSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            NotificationCache cache;
            lock (_lock)
            {
                cache = new NotificationCache
                {
                    SeenEventIds = new List<string>(_eventIdQueue),
                    RecentEvents = new List<CachedReviewEvent>(_recentEvents),
                };
            }

            await _cacheService.SaveAsync(cache).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await LogAsync($"[WARN] Cache save failed: {ex.Message}").ConfigureAwait(false);
        }
        finally
        {
            _cacheSemaphore.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _stopCts?.Cancel();
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

        foreach (IProcessInstance process in _activeProcesses.Values)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // ignore
            }

            process.Dispose();
        }

        _activeProcesses.Clear();

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
        _stopCts?.Dispose();
        _activeProcessCts?.Dispose();
        _cacheSemaphore.Dispose();
    }

    internal static (string FriendlyMessage, string ErrorTag) GetErrorInfo(string rawError, string? structuredErrorCode = null)
    {
        // 構造化された ErrorCode（mcp-resource-subscriber の JSON stdout 由来）が
        // 得られている場合は、それを最優先で判定する。非ゼロ終了時は stdout 全文
        // （serverUrl / resourceUri 等の URL・URI を含む）が rawError に結合されるため、
        // legacy な部分一致判定だけでは URL・URI 中の数字列（例: ポート番号やパス
        // セグメントとしての "401"）と実際の HTTP status を区別できない。ErrorCode が
        // 判明していれば信頼できる分類材料になるため、legacy 文字列マッチングより優先する。
        if (!string.IsNullOrWhiteSpace(structuredErrorCode) &&
            !structuredErrorCode.Equals("unknown", StringComparison.OrdinalIgnoreCase))
        {
            if (structuredErrorCode.Equals("AUTH_LOGIN_REQUIRED", StringComparison.OrdinalIgnoreCase) ||
                structuredErrorCode.Equals("REAUTH_REQUIRED", StringComparison.OrdinalIgnoreCase))
            {
                return ("mcp-gateway への認証が必要です。mcp-resource-subscriber の --login を実行して再認証してください。", _authenticationRequiredErrorTag);
            }

            // AUTH_REFRESH_FAILED / AUTH_TIMEOUT を含め、AUTH_LOGIN_REQUIRED 以外の
            // 既知の ErrorCode は認証エラーではないため、legacy 文字列マッチングを
            // スキップして直接 GENERAL_ERROR とする。
            return ($"予期しないエラーが発生しました: {rawError}", "[GENERAL_ERROR]");
        }

        if (string.IsNullOrEmpty(rawError))
        {
            return ("不明なエラーが発生しました。", "[UNKNOWN_ERROR]");
        }

        if (rawError.Contains("fetch failed", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("ECONNREFUSED", StringComparison.OrdinalIgnoreCase))
        {
            return ("mcp-gateway への接続に失敗しました。mcp-gateway コンテナが起動しているか、または Gateway URL の設定が正しいか確認してください。", "[CONN_REFUSED]");
        }

        if (rawError.Contains("404 page not found", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("Error POSTing to endpoint: 404", StringComparison.OrdinalIgnoreCase))
        {
            return ("指定されたエンドポイントが見つかりませんでした (404)。Gateway URL のポート番号やパスプレフィックス、または Resource URI が正しいか確認してください。", "[HTTP_404]");
        }

        if (rawError.Contains("AUTH_LOGIN_REQUIRED", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("REAUTH_REQUIRED", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("invalid_token", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("invalid_client", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("unauthorized_client", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("No access token provided", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("run --login", StringComparison.OrdinalIgnoreCase))
        {
            return ("mcp-gateway への認証が必要です。mcp-resource-subscriber の --login を実行して再認証してください。", _authenticationRequiredErrorTag);
        }

        // MCP_PROBE_AUTH_TOKEN を明示指定している場合、mcp-resource-subscriber は
        // --login のトークンキャッシュを完全に読み飛ばすため、401 の原因が env 変数側の
        // 設定不備でも --login の再認証では解消しない。両方の案内を残す。
        // ここは structuredErrorCode が得られない場合（stdout が空・パース不能等）の
        // フォールバックであり、その場合は stdout に URL・URI が含まれる可能性は低いが、
        // 念のため "401" は単語境界付きで判定する。
        if (Regex.IsMatch(rawError, @"\b401\b") ||
            rawError.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase))
        {
            return ("mcp-gateway への認証が必要です。mcp-resource-subscriber の --login を実行して再認証してください（MCP_PROBE_AUTH_TOKEN を指定している場合は、そのトークンが有効か確認してください）。", _authenticationRequiredErrorTag);
        }

        if (rawError.Contains("Forbidden", StringComparison.OrdinalIgnoreCase))
        {
            return ("mcp-gateway で認証エラーが発生しました。認証トークン（MCP_PROBE_AUTH_TOKEN）の設定を確認してください。", "[AUTH_ERROR]");
        }

        return ($"予期しないエラーが発生しました: {rawError}", "[GENERAL_ERROR]");
    }
}

/// <summary>
/// Subscriber プロセスの構造化された <c>errorCode</c> を保持する例外.
/// stdout 全文（serverUrl / resourceUri 等の URL・URI を含む）はメッセージ文字列に
/// 結合されるため、legacy な部分一致判定だけでは URL・URI 中の数字列（例: ポート番号
/// や パスセグメントとしての "401"）と HTTP status を区別できない. 構造化された
/// ErrorCode を別途保持することで、GetErrorInfo がそちらを優先判定できるようにする.
/// </summary>
internal sealed class SubscriberProcessException : Exception
{
    public SubscriberProcessException()
    {
    }

    public SubscriberProcessException(string message)
        : base(message)
    {
    }

    public SubscriberProcessException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public SubscriberProcessException(string message, string? errorCode)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    public string? ErrorCode { get; }
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
