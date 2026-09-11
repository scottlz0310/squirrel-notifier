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

        while (true)
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
                        return await FailAsync($"HTTP {(int)response.StatusCode} ({response.StatusCode})").ConfigureAwait(false);
                    }

                    await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                    delayMs *= 2;
                    continue;
                }

                string content = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                using JsonDocument doc = JsonDocument.Parse(content);
                if (doc.RootElement.ValueKind != JsonValueKind.Object
                    || !doc.RootElement.TryGetProperty("tag_name", out JsonElement tagElement)
                    || tagElement.ValueKind != JsonValueKind.String)
                {
                    return await FailAsync("リリース情報にバージョンタグがありません。").ConfigureAwait(false);
                }

                string? tag = tagElement.GetString();
                if (string.IsNullOrWhiteSpace(tag))
                {
                    return await FailAsync("リリース情報のバージョンタグが空です。").ConfigureAwait(false);
                }

                string rawVersion = tag.TrimStart('v', 'V');
                if (!Version.TryParse(rawVersion, out Version? latestVersion))
                {
                    return await FailAsync("リリース情報のバージョンタグを解析できません。").ConfigureAwait(false);
                }

                string releaseUrl = doc.RootElement.TryGetProperty("html_url", out JsonElement htmlUrl)
                    && htmlUrl.ValueKind == JsonValueKind.String
                    ? htmlUrl.GetString() ?? string.Empty
                    : string.Empty;

                bool hasUpdate = latestVersion > _currentVersion;
                if (hasUpdate && !Helpers.UrlValidator.IsHttpOrHttpsAbsoluteUrl(releaseUrl))
                {
                    return await FailAsync("リリース情報のダウンロードページ URL が不正です。").ConfigureAwait(false);
                }

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
                    return await FailAsync(ex.Message).ConfigureAwait(false);
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
    }

    private async Task<AutoUpdateResult> FailAsync(string message)
    {
        await _loggingService.WriteAsync($"自動更新チェックに失敗しました: {message}").ConfigureAwait(false);
        return new AutoUpdateResult(_currentVersion, _currentVersion, false, null, string.Empty, message);
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}

internal sealed record AutoUpdateResult(
    Version CurrentVersion,
    Version LatestVersion,
    bool HasUpdate,
    string? Tag,
    string ReleaseUrl,
    string? ErrorMessage = null);
