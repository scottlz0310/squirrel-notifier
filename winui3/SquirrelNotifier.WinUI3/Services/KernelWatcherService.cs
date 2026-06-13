// <copyright file="KernelWatcherService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SquirrelNotifier.WinUI3.Services;

[ExcludeFromCodeCoverage]
internal sealed class KernelWatcherService : IAsyncDisposable
{
    private static readonly Regex _versionRegex = new("(\\d+\\.\\d+\\.\\d+\\.\\d+)", RegexOptions.Compiled);
    private readonly TimeSpan _interval;
    private readonly HttpClient _httpClient;
    private readonly NotificationService _notificationService;
    private readonly LoggingService _loggingService;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loopTask;

    public event EventHandler<string>? StatusChanged;

    public KernelWatcherService(NotificationService notificationService, LoggingService loggingService, TimeSpan? interval = null)
    {
        _interval = interval ?? TimeSpan.FromHours(2);
        _notificationService = notificationService;
        _loggingService = loggingService;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Squirrel-Notifier-WinUI3/0.1");
    }

    public void Start()
    {
        _loopTask ??= Task.Run(() => RunAsync(_cts.Token));
    }

    public async Task CheckOnceAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await ReportStatusAsync("Checking kernel versions...").ConfigureAwait(false);
            string? current = await GetCurrentKernelVersionAsync(cancellationToken).ConfigureAwait(false);
            string? latest = await GetLatestKernelVersionAsync(cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(current) || string.IsNullOrWhiteSpace(latest))
            {
                await ReportStatusAsync("Unable to determine versions (WSL or GitHub API failed)").ConfigureAwait(false);
                return;
            }

            await ReportStatusAsync($"Current: {current} | Latest: {latest}").ConfigureAwait(false);

            if (IsLatestNewer(latest, current))
            {
                await ReportStatusAsync("Newer kernel detected. Sending notification.").ConfigureAwait(false);
                _notificationService.NotifyUpdateAvailable(current, latest);
            }
            else
            {
                await ReportStatusAsync("Already up to date.").ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            await ReportStatusAsync($"Error: {ex.Message}").ConfigureAwait(false);
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(_interval);
        while (!token.IsCancellationRequested)
        {
            await CheckOnceAsync(token).ConfigureAwait(false);
            try
            {
                await timer.WaitForNextTickAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static async Task<string?> GetCurrentKernelVersionAsync(CancellationToken token)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "wsl.exe",
                Arguments = "uname -r",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.Start();

            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> _ = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(token).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                return null;
            }

            string output = await outputTask.ConfigureAwait(false);
            return output.Trim();
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> GetLatestKernelVersionAsync(CancellationToken token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/microsoft/WSL2-Linux-Kernel/releases/latest");
            HttpResponseMessage response = await _httpClient.SendAsync(request, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            string content = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(content);
            if (!doc.RootElement.TryGetProperty("tag_name", out JsonElement tag))
            {
                return null;
            }

            return tag.GetString();
        }
        catch
        {
            return null;
        }
    }

    private static bool IsLatestNewer(string latest, string current)
    {
        Match currentMatch = _versionRegex.Match(current);
        Match latestMatch = _versionRegex.Match(latest);
        if (!currentMatch.Success || !latestMatch.Success)
        {
            return false;
        }

        if (!Version.TryParse(currentMatch.Groups[1].Value, out Version? currentVer))
        {
            return false;
        }

        if (!Version.TryParse(latestMatch.Groups[1].Value, out Version? latestVer))
        {
            return false;
        }

        return latestVer > currentVer;
    }

    private async Task ReportStatusAsync(string message)
    {
        StatusChanged?.Invoke(this, message);
        await _loggingService.WriteAsync(message).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _httpClient.Dispose();
        if (_loopTask is not null)
        {
            try
            {
                await _loopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown
            }
        }

        _cts.Dispose();
    }
}
