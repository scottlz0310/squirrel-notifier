// <copyright file="McpSubscriptionService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Diagnostics;
using System.Text.Json;
using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

internal sealed class McpSubscriptionService : IAsyncDisposable
{
    private readonly SettingsService _settingsService;
    private readonly INotificationService _notificationService;
    private readonly LoggingService _loggingService;
    private readonly IProcessRunner _processRunner;
    private readonly CancellationTokenSource _cts = new();
    private readonly HashSet<string> _seenEventIds = new();
    private readonly Queue<string> _eventIdQueue = new();
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
        IProcessRunner? processRunner = null)
    {
        _settingsService = settingsService;
        _notificationService = notificationService;
        _loggingService = loggingService;
        _processRunner = processRunner ?? new ProcessRunner();
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

    private static string ResolveCommandPath(string command)
    {
        if (File.Exists(command))
        {
            return Path.GetFullPath(command);
        }

        if (OperatingSystem.IsWindows())
        {
            string? pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathEnv))
            {
                string[] paths = pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries);
                string[] extensions = new[] { ".exe", ".cmd", ".bat", ".ps1" };

                foreach (string path in paths)
                {
                    string fullPath = Path.Combine(path, command);

                    foreach (string ext in extensions)
                    {
                        string extPath = fullPath + ext;
                        if (File.Exists(extPath))
                        {
                            return Path.GetFullPath(extPath);
                        }
                    }

                    if (File.Exists(fullPath))
                    {
                        return Path.GetFullPath(fullPath);
                    }
                }
            }
        }

        return command;
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
                FileName = ResolveCommandPath(settings.SubscriberCommandPath),
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

        while (!token.IsCancellationRequested)
        {
            _activeProcessCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            CancellationToken processToken = _activeProcessCts.Token;

            try
            {
                AppSettings settings = _settingsService.Settings;
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = ResolveCommandPath(settings.SubscriberCommandPath),
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

                        ReviewEvent? reviewEvent = ReviewEventParser.Parse(result.FinalText);
                        if (reviewEvent == null)
                        {
                            await LogAsync($"Warning: Malformed or unsupported review event payload received: {result.FinalText}").ConfigureAwait(false);
                        }
                        else
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
                                    MarkAsSeen(reviewEvent.EventId);
                                }
                                catch (Exception notifyEx)
                                {
                                    await LogAsync($"Error: Failed to show Windows notification: {notifyEx.Message}").ConfigureAwait(false);
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
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Loop cancellation requested, exit normally
                break;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                State = SubscriptionState.Error;
                ReportStatus($"Error: {ex.Message}");
                await LogAsync($"Subscription loop error: {ex.Message}").ConfigureAwait(false);
                break; // Stop loop on failure. Do not retry automatically.
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

    private void MarkAsSeen(string eventId)
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
