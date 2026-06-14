using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace SquirrelNotifier.WinUI3.Services;

internal sealed class AutoUpdateService : IDisposable
{
    private const string _releasesUrl = "https://api.github.com/repos/scottlz0310/squirrel-notifier/releases/latest";
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly LoggingService _loggingService;
    private readonly Version _currentVersion;

    public AutoUpdateService(LoggingService loggingService, HttpClient? httpClient = null, Version? currentVersionOverride = null)
    {
        _loggingService = loggingService ?? throw new ArgumentNullException(nameof(loggingService));
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Squirrel-Notifier-WinUI3", "3.0"));
        }

        _currentVersion = currentVersionOverride ?? Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);
    }

    public async Task<AutoUpdateResult> CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;
        int delayMs = 1000;
        int attempt = 0;

        while (attempt < maxAttempts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempt++;
            bool isLastAttempt = attempt == maxAttempts;

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(5));

                using var request = new HttpRequestMessage(HttpMethod.Get, _releasesUrl);
                using HttpResponseMessage response = await _httpClient.SendAsync(request, cts.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    if (isLastAttempt)
                    {
                        await _loggingService.WriteAsync($"自動更新チェックに失敗しました: {response.StatusCode}").ConfigureAwait(false);
                        return AutoUpdateResult.NoUpdate(_currentVersion);
                    }

                    await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                    delayMs *= 2;
                    continue;
                }

                string content = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                using JsonDocument doc = JsonDocument.Parse(content);
                if (!doc.RootElement.TryGetProperty("tag_name", out JsonElement tagElement))
                {
                    return AutoUpdateResult.NoUpdate(_currentVersion);
                }

                string? tag = tagElement.GetString();
                if (string.IsNullOrWhiteSpace(tag))
                {
                    return AutoUpdateResult.NoUpdate(_currentVersion);
                }

                string rawVersion = tag.TrimStart('v', 'V');
                if (!Version.TryParse(rawVersion, out Version? latestVersion))
                {
                    return AutoUpdateResult.NoUpdate(_currentVersion);
                }

                string releaseUrl = doc.RootElement.TryGetProperty("html_url", out JsonElement htmlUrl)
                    ? htmlUrl.GetString() ?? string.Empty
                    : string.Empty;

                bool hasUpdate = latestVersion > _currentVersion;
                return new AutoUpdateResult(_currentVersion, latestVersion, hasUpdate, tag, releaseUrl);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is JsonException || ex is OperationCanceledException)
            {
                if (isLastAttempt)
                {
                    await _loggingService.WriteAsync($"自動更新チェックに失敗しました: {ex.Message}").ConfigureAwait(false);
                    return AutoUpdateResult.NoUpdate(_currentVersion);
                }

                try
                {
                    await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }

                delayMs *= 2;
            }
        }

        return AutoUpdateResult.NoUpdate(_currentVersion);
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}

internal sealed record AutoUpdateResult(Version CurrentVersion, Version LatestVersion, bool HasUpdate, string? Tag, string ReleaseUrl)
{
    public static AutoUpdateResult NoUpdate(Version current)
    {
        return new AutoUpdateResult(current, current, false, null, string.Empty);
    }
}
