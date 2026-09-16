// <copyright file="RateLimitRefreshCoordinator.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SquirrelNotifier.WinUI3.Helpers;
using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>レートリミット更新の結果の種類（#266）.</summary>
internal enum RateLimitRefreshStatus
{
    /// <summary>取得を行った。一覧を差し替える.</summary>
    Completed,

    /// <summary>監視対象エージェントも ratelimit:// URI も無いため何も取得していない。一覧は触らない.</summary>
    NoTargets,
}

/// <summary>取得中に発生した、ユーザーへ伝えるべき事象。表示手段は呼び出し側が決める.</summary>
internal sealed record RateLimitRefreshAlert(string Title, string Message);

/// <summary>
/// レートリミット更新の入力。<see cref="ResourceUrisText"/> と <see cref="GatewayUrl"/> は
/// 画面のテキストボックスの生の内容で、解釈は <see cref="RateLimitRefreshCoordinator"/> が行う.
/// </summary>
internal sealed record RateLimitRefreshRequest(
    IReadOnlyList<RateLimitAgentOption> Agents,
    string ResourceUrisText,
    string GatewayUrl);

/// <summary>
/// レートリミット更新の結果。<see cref="Status"/> が <see cref="RateLimitRefreshStatus.NoTargets"/> の
/// ときは一覧を差し替えず、<see cref="Alerts"/> の提示だけを行う.
/// </summary>
/// <param name="Status">取得を行ったかどうか.</param>
/// <param name="Limits">一覧に表示するレートリミット。リマインダー予約状態は反映済み.</param>
/// <param name="Alerts">提示する事象。発生順に並ぶ.</param>
/// <param name="LegacySchemaMessage">旧形式 snapshot の警告。<see langword="null"/> なら警告を閉じる.</param>
internal sealed record RateLimitRefreshResult(
    RateLimitRefreshStatus Status,
    IReadOnlyList<RateLimitInfo> Limits,
    IReadOnlyList<RateLimitRefreshAlert> Alerts,
    string? LegacySchemaMessage);

/// <summary>
/// MCP リソースのテキストを読む処理。既定は <see cref="McpResourceProbe"/> で、テストでは差し替える.
/// </summary>
/// <param name="endpoint">mcp-gateway のエンドポイント.</param>
/// <param name="bearerToken">認証トークン（未設定なら <see langword="null"/>）.</param>
/// <param name="resourceUri">読み取るリソースの URI.</param>
/// <param name="cancellationToken">キャンセル用トークン.</param>
/// <returns>リソースのテキスト.</returns>
internal delegate Task<string> McpResourceTextReader(
    Uri endpoint,
    string? bearerToken,
    string resourceUri,
    CancellationToken cancellationToken);

/// <summary>
/// 「更新」操作で表示するレートリミットを決める（#266）。監視対象エージェントの列挙、
/// 経路別（codex は App Server、それ以外は statusline フックのローカルファイル）の取得、
/// 旧形式 snapshot（#168）の検出、MCP <c>ratelimit://</c> リソースの取得、
/// Auto-Pause gate（#147/#167）の再評価までを持ち、<c>ObservableCollection</c> への反映と
/// <c>InfoBar</c> ・ダイアログの表示だけを呼び出し側に残す.
/// </summary>
/// <remarks>
/// <para>
/// 本クラスは ConfigureAwait(false) を使わない。<see cref="AutoPauseGate.Evaluate"/> は
/// <see cref="AutoPauseGate.StateChanged"/> を通じて UI の InfoBar 更新を呼ぶため、
/// 呼び出し元（UI スレッド）の同期コンテキストを維持する必要がある（#264 と同じ理由）.
/// </para>
/// <para>
/// 取得失敗は正常系として扱い、途中で打ち切らない。ローカルファイル経由で取得済みの結果は
/// MCP 側が失敗しても破棄せず、部分成功として表示する（#139 レビュー対応）.
/// </para>
/// <para>
/// ただし呼び出し元によるキャンセルは失敗として扱わず、残りの取得を打ち切って
/// <see cref="OperationCanceledException"/> を伝播させる（#333）.
/// </para>
/// </remarks>
internal sealed class RateLimitRefreshCoordinator
{
    private static readonly char[] _resourceUriLineSeparators = ['\r', '\n'];

    private readonly RateLimitFileService _fileService;
    private readonly RateLimitSnapshotService _snapshotService;
    private readonly RateLimitSnapshotResolver _snapshotResolver;
    private readonly SettingsService _settingsService;
    private readonly AutoPauseGate _autoPauseGate;
    private readonly IRateLimitReminderService _reminderService;
    private readonly McpResourceTextReader _mcpResourceReader;

    public RateLimitRefreshCoordinator(
        RateLimitFileService fileService,
        RateLimitSnapshotService snapshotService,
        RateLimitSnapshotResolver snapshotResolver,
        SettingsService settingsService,
        AutoPauseGate autoPauseGate,
        IRateLimitReminderService reminderService,
        McpResourceTextReader? mcpResourceReader = null)
    {
        ArgumentNullException.ThrowIfNull(fileService);
        ArgumentNullException.ThrowIfNull(snapshotService);
        ArgumentNullException.ThrowIfNull(snapshotResolver);
        ArgumentNullException.ThrowIfNull(settingsService);
        ArgumentNullException.ThrowIfNull(autoPauseGate);
        ArgumentNullException.ThrowIfNull(reminderService);

        _fileService = fileService;
        _snapshotService = snapshotService;
        _snapshotResolver = snapshotResolver;
        _settingsService = settingsService;
        _autoPauseGate = autoPauseGate;
        _reminderService = reminderService;

        McpResourceProbe probe = new();
        _mcpResourceReader = mcpResourceReader
            ?? ((endpoint, token, uri, ct) => probe.ReadResourceTextAsync(endpoint, token, uri, ct));
    }

    /// <summary>
    /// 監視対象の snapshot を取得し、表示する一覧と警告を決める。あわせて Auto-Pause gate を
    /// 再評価する（#167。gate は起動試行時にしか再評価されず、「更新」で fresh な snapshot を
    /// 取得しても 95% 未満への解除が反映されなかった）.
    /// </summary>
    /// <param name="request">画面から渡す入力.</param>
    /// <param name="cancellationToken">
    /// キャンセル用トークン。キャンセルされた場合は取得エラーの alert に変えず
    /// <see cref="OperationCanceledException"/> を送出する.
    /// </param>
    /// <returns>表示に必要な結果一式.</returns>
    public async Task<RateLimitRefreshResult> RefreshAsync(
        RateLimitRefreshRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var alerts = new List<RateLimitRefreshAlert>();
        var fetchedLimits = new List<RateLimitInfo>();

        // Auto-Pause gate（#147/#167）の再評価に使う snapshot。表示取得と同じ操作内で
        // 取得したものを再利用し、gate 用に同じ agent を二重取得しない（#167 レビュー対応）。
        var capturedSnapshots = new Dictionary<string, RateLimitSnapshot>(StringComparer.Ordinal);

        // 旧形式（schemaVersion 等を欠く resetAt-only）の snapshot を書き出しているエージェント（#168）。
        // 一覧表示はできるため気づかれにくいが、Auto-Pause gate の判定対象からは silent に外れる。
        var legacySchemaAgentNames = new List<string>();

        // 1. ローカルファイル経由（statusline フック連携、#139）または App Server 経由（codex、#163）
        // IsAvailable=false は settings.json の手動編集等で IsMonitored=true に
        // なっていても読み取りの対象にしない。
        List<RateLimitAgentOption> monitoredAgents = request.Agents
            .Where(agent => agent.IsMonitored && agent.IsAvailable)
            .ToList();
        foreach (RateLimitAgentOption agent in monitoredAgents)
        {
            await CaptureAgentAsync(
                agent,
                fetchedLimits,
                capturedSnapshots,
                legacySchemaAgentNames,
                alerts,
                cancellationToken);
        }

        // 2. MCP ratelimit:// 経由（既存。サーバー側で将来対応された場合のために維持）
        List<string> rateLimitUris = request.ResourceUrisText
            .Split(_resourceUriLineSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(uri => uri.StartsWith(RateLimitStatusParser.UriScheme, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (rateLimitUris.Count > 0)
        {
            await CaptureMcpResourcesAsync(rateLimitUris, request.GatewayUrl, fetchedLimits, alerts, cancellationToken);
        }

        if (monitoredAgents.Count == 0 && rateLimitUris.Count == 0)
        {
            alerts.Add(new RateLimitRefreshAlert(
                "監視対象未設定",
                "レートリミット監視対象のエージェントが選択されていないか、Resource URIs に ratelimit:// で始まる URI が設定されていません。"));
            return new RateLimitRefreshResult(RateLimitRefreshStatus.NoTargets, [], alerts, null);
        }

        foreach (RateLimitInfo info in fetchedLimits)
        {
            info.IsReminderScheduled = _reminderService.IsScheduled(info.ReminderKey);
        }

        await RefreshAutoPauseGateAsync(capturedSnapshots, cancellationToken);

        return new RateLimitRefreshResult(
            RateLimitRefreshStatus.Completed,
            fetchedLimits,
            alerts,
            RateLimitAlertFormatter.BuildLegacySchemaMessage(legacySchemaAgentNames));
    }

    private async Task CaptureAgentAsync(
        RateLimitAgentOption agent,
        List<RateLimitInfo> fetchedLimits,
        Dictionary<string, RateLimitSnapshot> capturedSnapshots,
        List<string> legacySchemaAgentNames,
        List<RateLimitRefreshAlert> alerts,
        CancellationToken cancellationToken)
    {
        try
        {
            if (agent.Id == RateLimitSnapshotService.CodexAgentId)
            {
                // codex は statusline を持たないため App Server（account/rateLimits/read）から取得する
                (RateLimitSnapshot? snapshot, CodexRateLimitFailureReason? failureReason) =
                    await _snapshotService.CaptureCodexWithFailureReasonAsync(agent.Id, cancellationToken);
                if (snapshot == null)
                {
                    alerts.Add(new RateLimitRefreshAlert(
                        "レートリミット情報を取得できません",
                        RateLimitAlertFormatter.BuildCodexFailureMessage(agent.DisplayName, failureReason)));
                    return;
                }

                capturedSnapshots[agent.Id] = snapshot;

                string sourceUri = RateLimitFileService.BuildSourceIdentifier(agent.Id);
                foreach (RateLimitInfo info in snapshot.Limits)
                {
                    info.SourceUri = sourceUri;
                    fetchedLimits.Add(info);
                }

                return;
            }

            string? json = await _fileService.ReadAgentStatusAsync(agent.Id, cancellationToken);
            if (json == null)
            {
                alerts.Add(new RateLimitRefreshAlert(
                    "レートリミット情報がありません",
                    RateLimitAlertFormatter.BuildMissingStatusMessage(agent.DisplayName)));
                return;
            }

            RateLimitSnapshot? parsedSnapshot = RateLimitStatusParser.ParseSnapshot(json);
            if (parsedSnapshot is not null)
            {
                capturedSnapshots[agent.Id] = parsedSnapshot;
            }
            else if (RateLimitStatusParser.IsLegacySchema(json))
            {
                legacySchemaAgentNames.Add(agent.DisplayName);
            }

            fetchedLimits.AddRange(
                RateLimitStatusParser.Parse(json, RateLimitFileService.BuildSourceIdentifier(agent.Id)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            alerts.Add(new RateLimitRefreshAlert(
                "取得エラー",
                RateLimitAlertFormatter.BuildReadFailureMessage(agent.DisplayName, ex.Message)));
        }
    }

    private async Task CaptureMcpResourcesAsync(
        List<string> rateLimitUris,
        string gatewayUrl,
        List<RateLimitInfo> fetchedLimits,
        List<RateLimitRefreshAlert> alerts,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(gatewayUrl, UriKind.Absolute, out Uri? endpoint))
        {
            alerts.Add(new RateLimitRefreshAlert(
                "設定エラー",
                "Gateway URL が正しくありません。先に Gateway URL を設定してください。"));
            return;
        }

        string? token = Environment.GetEnvironmentVariable("MCP_PROBE_AUTH_TOKEN");
        foreach (string uri in rateLimitUris)
        {
            try
            {
                string json = await _mcpResourceReader(endpoint, token, uri, cancellationToken);
                fetchedLimits.AddRange(RateLimitStatusParser.Parse(json, uri));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                alerts.Add(new RateLimitRefreshAlert("取得エラー", McpResourceProbe.GetUserMessage(ex)));
            }
        }
    }

    // reviewer / reviewed 両スロットの rateLimitAgentId を、実行中プロセス・MCP subscription・
    // queue には作用しない読み取り専用の再評価として反映する。snapshot の取得・再利用ロジックは
    // RateLimitSnapshotResolver に委譲する（#167 レビュー対応）.
    private async Task RefreshAutoPauseGateAsync(
        IReadOnlyDictionary<string, RateLimitSnapshot> capturedSnapshots,
        CancellationToken cancellationToken)
    {
        AppSettings settings = _settingsService.Settings;
        TimeSpan freshnessThreshold = TimeSpan.FromMinutes(settings.RateLimitFreshnessThresholdMinutes);
        List<string> gateAgentIds = new[]
            {
                _settingsService.ResolveLauncherRateLimitAgentId(LauncherRole.Reviewer),
                _settingsService.ResolveLauncherRateLimitAgentId(LauncherRole.Reviewed),
            }
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (gateAgentIds.Count == 0)
        {
            return;
        }

        IReadOnlyList<RateLimitSnapshot> gateSnapshots = await _snapshotResolver
            .ResolveAsync(gateAgentIds, capturedSnapshots, cancellationToken);

        foreach (string agentId in gateAgentIds)
        {
            _autoPauseGate.Evaluate(agentId, gateSnapshots, freshnessThreshold);
        }
    }
}
