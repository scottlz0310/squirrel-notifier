// <copyright file="SettingsCoordinator.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.ComponentModel;
using System.Diagnostics;
using SquirrelNotifier.WinUI3.Helpers;

namespace SquirrelNotifier.WinUI3.Services;

internal enum SettingsSaveStatus
{
    /// <summary>入力値を設定へ保存した.</summary>
    Saved,

    /// <summary>入力途中または検証エラーのため保存しなかった.</summary>
    NotReady,
}

internal sealed record SettingsSaveResult(
    SettingsSaveStatus Status,
    string? ReviewerPresetId,
    string? ReviewedPresetId)
{
    public bool IsSaved => Status == SettingsSaveStatus.Saved;
}

internal sealed record GatewayDetectionResult(
    IReadOnlyList<string> BaseUrls,
    string? ErrorTitle,
    string? ErrorMessage)
{
    public bool Succeeded => ErrorTitle is null;
}

internal sealed record ResourceUriFetchResult(
    IReadOnlyList<string> ResourceUris,
    string? ErrorTitle,
    string? ErrorMessage)
{
    public bool Succeeded => ErrorTitle is null;
}

internal sealed record GatewayUrlSelectionRequest(
    IReadOnlyList<string> BaseUrls,
    string DefaultRoute);

internal sealed record GatewayUrlSelectionResult(
    bool IsConfirmed,
    string? SelectedBaseUrl,
    string Route);

internal sealed record ResourceUriSelectionRequest(
    string Title,
    IReadOnlyList<string> ResourceUris);

internal sealed record ResourceUriSelectionResult(
    bool IsConfirmed,
    IReadOnlyList<string> SelectedResourceUris);

internal sealed record SettingsInputPresentation(
    string? GatewayUrl,
    string? ResourceUrisText,
    string? ErrorTitle,
    string? ErrorMessage)
{
    public bool HasError => ErrorTitle is not null;
}

internal delegate Task<IReadOnlyList<string>> McpResourceUriReader(
    Uri endpoint,
    string? bearerToken,
    CancellationToken cancellationToken);

internal delegate Task<GatewayUrlSelectionResult> GatewayUrlSelector(
    GatewayUrlSelectionRequest request);

internal delegate Task<ResourceUriSelectionResult> ResourceUriSelector(
    ResourceUriSelectionRequest request);

/// <summary>
/// Settings の保存、Gateway URL と Resource URI の入力フローを担当する（#267）。
/// UI 要素やダイアログは持たず、呼び出し側へ結果を返す.
/// </summary>
internal sealed class SettingsCoordinator
{
    private static readonly string[] _knownResourceUris =
    [
        "queue://review/queue",
        "queue://review/re-review-requests",
    ];

    private readonly SettingsService _settingsService;
    private readonly IProcessRunner _processRunner;
    private readonly McpResourceUriReader _mcpResourceUriReader;

    public SettingsCoordinator(
        SettingsService settingsService,
        IProcessRunner? processRunner = null,
        McpResourceUriReader? mcpResourceUriReader = null)
    {
        ArgumentNullException.ThrowIfNull(settingsService);

        _settingsService = settingsService;
        _processRunner = processRunner ?? new ProcessRunner();

        McpResourceProbe probe = new();
        _mcpResourceUriReader = mcpResourceUriReader
            ?? ((endpoint, bearerToken, cancellationToken) =>
                probe.FetchResourceUrisAsync(endpoint, bearerToken, cancellationToken));
    }

    /// <summary>Gets 既知のレビュー queue 用 Resource URI.</summary>
    public static IReadOnlyList<string> KnownResourceUris => _knownResourceUris;

    public SettingsSaveResult Save(SettingsInput input)
    {
        SettingsUpdateValues? values = SettingsInputParser.TryParse(input);
        if (values is null)
        {
            return new SettingsSaveResult(SettingsSaveStatus.NotReady, null, null);
        }

        try
        {
            _settingsService.UpdateSettings(
                values.CommandPath,
                values.Arguments,
                values.GatewayUrl,
                values.ResourceUris,
                values.NotificationTimeoutMs,
                values.ReviewerLauncherCommandPath,
                values.ReviewerLauncherArguments,
                values.ReviewedLauncherCommandPath,
                values.ReviewedLauncherArguments,
                values.LauncherTimeoutMs,
                values.ReviewerPresetId,
                values.ReviewedPresetId);
            _settingsService.UpdateRepositoryCheckoutMappings(values.RepositoryCheckoutMappings);
        }
        catch (ArgumentException)
        {
            // 入力途中の検証エラーは既存設定を保持する。予期しない例外は握り潰さない。
            return new SettingsSaveResult(SettingsSaveStatus.NotReady, null, null);
        }

        return new SettingsSaveResult(
            SettingsSaveStatus.Saved,
            values.ReviewerPresetId,
            values.ReviewedPresetId);
    }

    public async Task<GatewayDetectionResult> DetectGatewayUrlsAsync(CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "docker",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("ps");
        startInfo.ArgumentList.Add("--filter");
        startInfo.ArgumentList.Add("name=mcp-gateway");
        startInfo.ArgumentList.Add("--format");
        startInfo.ArgumentList.Add("{{.Ports}}");

        try
        {
            using IProcessInstance process = _processRunner.Start(startInfo);
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            string output = await stdoutTask.ConfigureAwait(false);
            string stderr = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(output))
            {
                return new GatewayDetectionResult(
                    [],
                    "Docker エラー",
                    $"Docker コマンドが失敗しました。\n{stderr.Trim()}");
            }

            IReadOnlyList<string> baseUrls = DockerPortParser.ParseGatewayBaseUrls(output);
            if (baseUrls.Count == 0)
            {
                return new GatewayDetectionResult(
                    [],
                    "コンテナが見つかりませんでした",
                    "コンテナ名に 'mcp-gateway' が含まれているか、コンテナが起動しているか確認してください。");
            }

            return new GatewayDetectionResult(baseUrls, null, null);
        }
        catch (Win32Exception)
        {
            return new GatewayDetectionResult(
                [],
                "Docker が見つかりませんでした",
                "Docker がインストールされていないか PATH に含まれていません。Gateway URL を手動で入力してください。");
        }
    }

    /// <summary>
    /// Docker から検出した Gateway URL を選択させ、UI へ反映する結果を返す.
    /// </summary>
    /// <param name="selectGatewayUrlAsync">検出候補を選択する UI 境界.</param>
    /// <param name="cancellationToken">Docker 検出を中止するトークン.</param>
    /// <returns>Gateway URL またはエラーを含む UI 向けの結果.</returns>
    public async Task<SettingsInputPresentation> AutoDetectGatewayUrlAsync(
        GatewayUrlSelector selectGatewayUrlAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selectGatewayUrlAsync);

        GatewayDetectionResult detection = await DetectGatewayUrlsAsync(cancellationToken);
        if (!detection.Succeeded)
        {
            return new SettingsInputPresentation(null, null, detection.ErrorTitle, detection.ErrorMessage);
        }

        GatewayUrlSelectionResult selection = await selectGatewayUrlAsync(
            new GatewayUrlSelectionRequest(detection.BaseUrls, DockerPortParser.DefaultMcpRoute));
        if (!selection.IsConfirmed || string.IsNullOrWhiteSpace(selection.SelectedBaseUrl))
        {
            return new SettingsInputPresentation(null, null, null, null);
        }

        return new SettingsInputPresentation(
            DockerPortParser.CombineRoute(selection.SelectedBaseUrl, selection.Route),
            null,
            null,
            null);
    }

    /// <summary>
    /// 既知の Resource URI を選択させ、UI へ反映する結果を返す.
    /// </summary>
    /// <param name="existingText">現在の Resource URI 入力.</param>
    /// <param name="selectResourceUrisAsync">Resource URI を選択する UI 境界.</param>
    /// <returns>Resource URI 入力の更新結果.</returns>
    public Task<SettingsInputPresentation> AddKnownResourceUrisAsync(
        string existingText,
        ResourceUriSelector selectResourceUrisAsync)
    {
        ArgumentNullException.ThrowIfNull(selectResourceUrisAsync);

        return SelectResourceUrisAsync(
            existingText,
            new ResourceUriSelectionRequest("Resource URI を追加", KnownResourceUris),
            selectResourceUrisAsync);
    }

    /// <summary>
    /// MCP から取得した Resource URI を選択させ、UI へ反映する結果を返す.
    /// </summary>
    /// <param name="existingText">現在の Resource URI 入力.</param>
    /// <param name="gatewayUrl">接続先の Gateway URL.</param>
    /// <param name="selectResourceUrisAsync">Resource URI を選択する UI 境界.</param>
    /// <param name="cancellationToken">取得処理を中止するトークン.</param>
    /// <returns>Resource URI 入力またはエラーを含む UI 向けの結果.</returns>
    public async Task<SettingsInputPresentation> FetchAndAddResourceUrisAsync(
        string existingText,
        string gatewayUrl,
        ResourceUriSelector selectResourceUrisAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selectResourceUrisAsync);

        ResourceUriFetchResult fetch = await FetchResourceUrisAsync(gatewayUrl, cancellationToken);
        if (!fetch.Succeeded)
        {
            return new SettingsInputPresentation(null, null, fetch.ErrorTitle, fetch.ErrorMessage);
        }

        return await SelectResourceUrisAsync(
            existingText,
            new ResourceUriSelectionRequest("追加する Resource URI を選択", fetch.ResourceUris),
            selectResourceUrisAsync);
    }

    /// <summary>MCP endpoint から Resource URI を取得し、UI 向けの結果へ変換する.</summary>
    /// <param name="gatewayUrl">接続先の Gateway URL.</param>
    /// <param name="cancellationToken">取得処理を中止するトークン.</param>
    /// <returns>取得できた URI またはユーザーへ表示するエラー情報.</returns>
    public async Task<ResourceUriFetchResult> FetchResourceUrisAsync(
        string gatewayUrl,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(gatewayUrl, UriKind.Absolute, out Uri? endpoint))
        {
            return new ResourceUriFetchResult(
                [],
                "設定エラー",
                "Gateway URL が正しくありません。先に Gateway URL を設定してください。");
        }

        string? bearerToken = Environment.GetEnvironmentVariable("MCP_PROBE_AUTH_TOKEN");

        try
        {
            IReadOnlyList<string> resourceUris = await _mcpResourceUriReader(
                endpoint,
                bearerToken,
                cancellationToken).ConfigureAwait(false);

            if (resourceUris.Count == 0)
            {
                return new ResourceUriFetchResult(
                    [],
                    "リソースが見つかりません",
                    "mcp-gateway からリソース URI を取得しましたが、リストが空でした。");
            }

            return new ResourceUriFetchResult(resourceUris, null, null);
        }
        catch (Exception ex)
        {
            return new ResourceUriFetchResult(
                [],
                "取得エラー",
                McpResourceProbe.GetUserMessage(ex));
        }
    }

    private static async Task<SettingsInputPresentation> SelectResourceUrisAsync(
        string existingText,
        ResourceUriSelectionRequest request,
        ResourceUriSelector selectResourceUrisAsync)
    {
        ResourceUriSelectionResult selection = await selectResourceUrisAsync(request);
        if (!selection.IsConfirmed || selection.SelectedResourceUris.Count == 0)
        {
            return new SettingsInputPresentation(null, null, null, null);
        }

        return new SettingsInputPresentation(
            null,
            SettingsInputParser.MergeResourceUris(existingText, selection.SelectedResourceUris),
            null,
            null);
    }
}
