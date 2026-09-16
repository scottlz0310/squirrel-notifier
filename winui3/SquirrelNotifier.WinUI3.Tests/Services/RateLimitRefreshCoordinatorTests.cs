// <copyright file="RateLimitRefreshCoordinatorTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Diagnostics;
using System.Globalization;
using System.Text;
using FluentAssertions;
using Moq;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public sealed class RateLimitRefreshCoordinatorTests : IDisposable
{
    private const string _claudeCode = "claude-code";
    private const string _agy = "agy";
    private const string _gatewayUrl = "http://127.0.0.1:8080/mcp";
    private const string _rateLimitUri = "ratelimit://queue/limits";

    private readonly string _settingsDirectory = Path.Combine(Path.GetTempPath(), $"RateLimitRefreshCoordinatorTests_{Guid.NewGuid()}");
    private readonly AutoPauseGate _autoPauseGate = new();
    private readonly Mock<IRateLimitReminderService> _reminderService = new();

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task RefreshAsync_ShouldReturnNoTargets_WhenNoAgentIsEffectivelyMonitored(bool isMonitored, bool isAvailable)
    {
        // IsAvailable=false は settings.json の手動編集等で IsMonitored=true になっていても対象にしない
        RateLimitRefreshCoordinator coordinator = CreateCoordinator();

        RateLimitRefreshResult result = await coordinator.RefreshAsync(
            CreateRequest(CreateAgent(_claudeCode, isMonitored, isAvailable)));

        result.Status.Should().Be(RateLimitRefreshStatus.NoTargets);
        result.Limits.Should().BeEmpty();
        result.LegacySchemaMessage.Should().BeNull();
        result.Alerts.Should().ContainSingle().Which.Title.Should().Be("監視対象未設定");
    }

    [Fact]
    public async Task RefreshAsync_ShouldReturnLimits_WhenSnapshotIsAvailable()
    {
        await WriteSnapshotAsync(_claudeCode, usedPercentage: 42);
        RateLimitRefreshCoordinator coordinator = CreateCoordinator();

        RateLimitRefreshResult result = await coordinator.RefreshAsync(CreateRequest(CreateAgent(_claudeCode)));

        result.Status.Should().Be(RateLimitRefreshStatus.Completed);
        result.Alerts.Should().BeEmpty();
        result.LegacySchemaMessage.Should().BeNull();
        RateLimitInfo limit = result.Limits.Should().ContainSingle().Subject;
        limit.UsedPercentage.Should().Be(42);
        limit.SourceUri.Should().Be("agent://claude-code");
    }

    [Fact]
    public async Task RefreshAsync_ShouldAlertMissingStatus_WhenSnapshotFileIsAbsent()
    {
        RateLimitRefreshCoordinator coordinator = CreateCoordinator();

        RateLimitRefreshResult result = await coordinator.RefreshAsync(CreateRequest(CreateAgent(_claudeCode)));

        result.Status.Should().Be(RateLimitRefreshStatus.Completed);
        result.Limits.Should().BeEmpty();
        result.Alerts.Should().ContainSingle().Which.Title.Should().Be("レートリミット情報がありません");
    }

    [Fact]
    public async Task RefreshAsync_ShouldKeepSucceededAgents_WhenAnotherAgentFails()
    {
        // 一部が取得できなくても取得済みの結果は破棄せず、部分成功として返す（#139 レビュー対応）
        await WriteSnapshotAsync(_agy, usedPercentage: 30);
        RateLimitRefreshCoordinator coordinator = CreateCoordinator();

        RateLimitRefreshResult result = await coordinator.RefreshAsync(
            CreateRequest(CreateAgent(_claudeCode), CreateAgent(_agy)));

        result.Status.Should().Be(RateLimitRefreshStatus.Completed);
        result.Limits.Should().ContainSingle().Which.SourceUri.Should().Be("agent://agy");
        result.Alerts.Should().ContainSingle().Which.Title.Should().Be("レートリミット情報がありません");
    }

    [Fact]
    public async Task RefreshAsync_ShouldAlertEveryAgent_WhenAllAgentsFail()
    {
        RateLimitRefreshCoordinator coordinator = CreateCoordinator();

        RateLimitRefreshResult result = await coordinator.RefreshAsync(
            CreateRequest(CreateAgent(_claudeCode), CreateAgent(_agy)));

        result.Status.Should().Be(RateLimitRefreshStatus.Completed);
        result.Limits.Should().BeEmpty();
        result.Alerts.Should().HaveCount(2);
        result.Alerts.Should().OnlyContain(alert => alert.Title == "レートリミット情報がありません");
    }

    [Fact]
    public async Task RefreshAsync_ShouldAlertReadFailure_WhenSnapshotFileCannotBeRead()
    {
        await WriteSnapshotAsync(_claudeCode, usedPercentage: 42);
        await WriteSnapshotAsync(_agy, usedPercentage: 30);
        RateLimitRefreshCoordinator coordinator = CreateCoordinator();
        string lockedPath = Path.Combine(_settingsDirectory, "ratelimit-status", $"{_claudeCode}.json");
        using FileStream exclusiveLock = new(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None);

        RateLimitRefreshResult result = await coordinator.RefreshAsync(
            CreateRequest(CreateAgent(_claudeCode), CreateAgent(_agy)));

        result.Limits.Should().ContainSingle().Which.SourceUri.Should().Be("agent://agy");
        RateLimitRefreshAlert alert = result.Alerts.Should().ContainSingle().Subject;
        alert.Title.Should().Be("取得エラー");
        alert.Message.Should().StartWith("claude-code のレートリミット状態の読み取りに失敗しました: ");
    }

    [Fact]
    public async Task RefreshAsync_ShouldReportLegacySchemaAgents_WhenSchemasAreMixed()
    {
        // 旧形式は一覧表示できてしまうため、警告を出さないと Auto-Pause の無効化に気づけない（#168）
        await WriteSnapshotAsync(_claudeCode, usedPercentage: 20);
        await WriteLegacySnapshotAsync(_agy);
        RateLimitRefreshCoordinator coordinator = CreateCoordinator();

        RateLimitRefreshResult result = await coordinator.RefreshAsync(
            CreateRequest(CreateAgent(_claudeCode), CreateAgent(_agy)));

        result.Limits.Should().HaveCount(2);
        result.Alerts.Should().BeEmpty();
        result.LegacySchemaMessage.Should().NotBeNull();
        result.LegacySchemaMessage.Should().StartWith("agy (Antigravity CLI) の statusline snapshot が旧形式");
    }

    [Fact]
    public async Task RefreshAsync_ShouldMarkScheduledReminders()
    {
        await WriteSnapshotAsync(_claudeCode, usedPercentage: 42);
        _reminderService.Setup(service => service.IsScheduled("agent://claude-code:five-hour")).Returns(true);
        RateLimitRefreshCoordinator coordinator = CreateCoordinator();

        RateLimitRefreshResult result = await coordinator.RefreshAsync(CreateRequest(CreateAgent(_claudeCode)));

        result.Limits.Should().ContainSingle().Which.IsReminderScheduled.Should().BeTrue();
    }

    [Fact]
    public async Task RefreshAsync_ShouldAddLimitsFromMcpResource()
    {
        RateLimitRefreshCoordinator coordinator = CreateCoordinator(
            mcpResourceReader: (_, _, uri, _) => Task.FromResult(BuildLegacyPayload(uri)));

        RateLimitRefreshResult result = await coordinator.RefreshAsync(
            new RateLimitRefreshRequest([], _rateLimitUri, _gatewayUrl));

        result.Status.Should().Be(RateLimitRefreshStatus.Completed);
        result.Alerts.Should().BeEmpty();
        result.Limits.Should().ContainSingle().Which.SourceUri.Should().Be(_rateLimitUri);
    }

    [Fact]
    public async Task RefreshAsync_ShouldIgnoreNonRateLimitUris()
    {
        bool readerCalled = false;
        RateLimitRefreshCoordinator coordinator = CreateCoordinator(
            mcpResourceReader: (_, _, uri, _) =>
            {
                readerCalled = true;
                return Task.FromResult(BuildLegacyPayload(uri));
            });

        RateLimitRefreshResult result = await coordinator.RefreshAsync(
            new RateLimitRefreshRequest([], $"queue://review/queue\r\n{_rateLimitUri}", _gatewayUrl));

        readerCalled.Should().BeTrue();
        result.Limits.Should().ContainSingle().Which.SourceUri.Should().Be(_rateLimitUri);
    }

    [Fact]
    public async Task RefreshAsync_ShouldAlertConfigurationError_WhenGatewayUrlIsInvalid()
    {
        RateLimitRefreshCoordinator coordinator = CreateCoordinator(
            mcpResourceReader: (_, _, _, _) => throw new InvalidOperationException("呼ばれてはならない"));

        RateLimitRefreshResult result = await coordinator.RefreshAsync(
            new RateLimitRefreshRequest([], _rateLimitUri, "gateway-url-ではない"));

        result.Status.Should().Be(RateLimitRefreshStatus.Completed);
        result.Alerts.Should().ContainSingle().Which.Title.Should().Be("設定エラー");
    }

    [Fact]
    public async Task RefreshAsync_ShouldKeepLocalResults_WhenMcpFetchFails()
    {
        await WriteSnapshotAsync(_claudeCode, usedPercentage: 42);
        InvalidOperationException failure = new("mcp-gateway に接続できません");
        RateLimitRefreshCoordinator coordinator = CreateCoordinator(
            mcpResourceReader: (_, _, _, _) => throw failure);

        RateLimitRefreshResult result = await coordinator.RefreshAsync(
            new RateLimitRefreshRequest([CreateAgent(_claudeCode)], _rateLimitUri, _gatewayUrl));

        result.Limits.Should().ContainSingle().Which.SourceUri.Should().Be("agent://claude-code");
        RateLimitRefreshAlert alert = result.Alerts.Should().ContainSingle().Subject;
        alert.Title.Should().Be("取得エラー");
        alert.Message.Should().Be(McpResourceProbe.GetUserMessage(failure));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RefreshAsync_ShouldPropagateCancellation_WhenCallerCancelled(bool hasAgent, bool hasMcpUri)
    {
        // 呼出元のキャンセルは「取得エラー」の alert ではなく例外として伝播させる（#333）
        await WriteSnapshotAsync(_claudeCode, usedPercentage: 42);
        int readerCalls = 0;
        RateLimitRefreshCoordinator coordinator = CreateCoordinator(
            mcpResourceReader: (_, _, uri, ct) =>
            {
                readerCalls++;
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(BuildLegacyPayload(uri));
            });
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        Func<Task> act = () => coordinator.RefreshAsync(
            new RateLimitRefreshRequest(
                hasAgent ? [CreateAgent(_claudeCode)] : [],
                hasMcpUri ? _rateLimitUri : string.Empty,
                _gatewayUrl),
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();

        // エージェント取得でキャンセルした場合は MCP 取得まで進まない
        readerCalls.Should().Be(hasAgent ? 0 : 1);
    }

    [Fact]
    public async Task RefreshAsync_ShouldStopRemainingUris_WhenCancelledDuringMcpFetch()
    {
        using CancellationTokenSource cts = new();
        List<string> requestedUris = [];
        RateLimitRefreshCoordinator coordinator = CreateCoordinator(
            mcpResourceReader: async (_, _, uri, ct) =>
            {
                requestedUris.Add(uri);
                await cts.CancelAsync();
                ct.ThrowIfCancellationRequested();
                return BuildLegacyPayload(uri);
            });

        Func<Task> act = () => coordinator.RefreshAsync(
            new RateLimitRefreshRequest([], $"{_rateLimitUri}\r\nratelimit://queue/weekly", _gatewayUrl),
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        requestedUris.Should().ContainSingle().Which.Should().Be(_rateLimitUri);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefreshAsync_ShouldAlertFetchError_WhenCancellationIsNotRequestedByCaller(bool isTaskCanceled)
    {
        // HTTP タイムアウト等、呼出元がキャンセルしていない OperationCanceledException は従来どおり取得エラー
        Exception failure = isTaskCanceled
            ? new TaskCanceledException("要求がタイムアウトしました")
            : new OperationCanceledException("取得を中断しました");
        RateLimitRefreshCoordinator coordinator = CreateCoordinator(
            mcpResourceReader: (_, _, _, _) => throw failure);

        RateLimitRefreshResult result = await coordinator.RefreshAsync(
            new RateLimitRefreshRequest([], _rateLimitUri, _gatewayUrl));

        result.Status.Should().Be(RateLimitRefreshStatus.Completed);
        RateLimitRefreshAlert alert = result.Alerts.Should().ContainSingle().Subject;
        alert.Title.Should().Be("取得エラー");
        alert.Message.Should().Be(McpResourceProbe.GetUserMessage(failure));
    }

    [Fact]
    public async Task RefreshAsync_ShouldAlertCodexFailure_WhenCommandIsNotFound()
    {
        // codex は statusline を持たないため、ローカルファイルではなく App Server 経路の失敗を伝える（#163/#174）
        RateLimitRefreshCoordinator coordinator = CreateCoordinator(commandResolver: _ => null);

        RateLimitRefreshResult result = await coordinator.RefreshAsync(
            CreateRequest(CreateAgent(RateLimitSnapshotService.CodexAgentId)));

        result.Limits.Should().BeEmpty();
        RateLimitRefreshAlert alert = result.Alerts.Should().ContainSingle().Subject;
        alert.Title.Should().Be("レートリミット情報を取得できません");
        alert.Message.Should().Contain("codex コマンドが見つかりませんでした");
    }

    [Fact]
    public async Task RefreshAsync_ShouldReturnCodexLimits_WhenAppServerResponds()
    {
        string stdout =
            "{\"id\":1,\"result\":{}}\n" +
            "{\"id\":2,\"result\":{\"rateLimits\":{\"limitId\":\"codex\"," +
            "\"primary\":{\"usedPercent\":55,\"windowDurationMins\":300,\"resetsAt\":1789000000}}}}\n";
        var process = new Mock<IProcessInstance>();
        process.SetupGet(p => p.StandardOutput).Returns(new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(stdout))));
        process.SetupGet(p => p.StandardError).Returns(new StreamReader(new MemoryStream()));
        process.SetupGet(p => p.StandardInput).Returns(new StreamWriter(new MemoryStream()));
        var runner = new Mock<IProcessRunner>();
        runner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>())).Returns(process.Object);
        RateLimitRefreshCoordinator coordinator = CreateCoordinator(
            processRunner: runner.Object,
            commandResolver: _ => @"C:\fake\codex.exe");

        RateLimitRefreshResult result = await coordinator.RefreshAsync(
            CreateRequest(CreateAgent(RateLimitSnapshotService.CodexAgentId)));

        result.Alerts.Should().BeEmpty();
        RateLimitInfo limit = result.Limits.Should().ContainSingle().Subject;
        limit.UsedPercentage.Should().Be(55);
        limit.SourceUri.Should().Be("agent://codex");
    }

    [Fact]
    public async Task RefreshAsync_ShouldPauseGate_WhenFreshSnapshotIsAtRisk()
    {
        // 「更新」でも gate を再評価する（#167。以前は起動試行時にしか評価されなかった）
        await WriteSnapshotAsync(_claudeCode, usedPercentage: 96);
        RateLimitRefreshCoordinator coordinator = CreateCoordinator();

        await coordinator.RefreshAsync(CreateRequest(CreateAgent(_claudeCode)));

        _autoPauseGate.PausedLimits.Should().ContainSingle().Which.AgentId.Should().Be(_claudeCode);
    }

    [Fact]
    public async Task RefreshAsync_ShouldReleasePause_WhenFreshSnapshotIsBelowThreshold()
    {
        await WriteSnapshotAsync(_claudeCode, usedPercentage: 96);
        RateLimitRefreshCoordinator coordinator = CreateCoordinator();
        await coordinator.RefreshAsync(CreateRequest(CreateAgent(_claudeCode)));

        await WriteSnapshotAsync(_claudeCode, usedPercentage: 10);
        await coordinator.RefreshAsync(CreateRequest(CreateAgent(_claudeCode)));

        _autoPauseGate.PausedLimits.Should().BeEmpty();
    }

    [Fact]
    public async Task RefreshAsync_ShouldKeepPause_WhenSnapshotIsStale()
    {
        // stale な snapshot では解除しない。fresh な 95% 未満を確認できたときだけ解除する（#145/#147）
        await WriteSnapshotAsync(_claudeCode, usedPercentage: 96);
        RateLimitRefreshCoordinator coordinator = CreateCoordinator();
        await coordinator.RefreshAsync(CreateRequest(CreateAgent(_claudeCode)));

        await WriteSnapshotAsync(_claudeCode, usedPercentage: 10, observedAt: DateTimeOffset.UtcNow.AddHours(-3));
        await coordinator.RefreshAsync(CreateRequest(CreateAgent(_claudeCode)));

        _autoPauseGate.PausedLimits.Should().ContainSingle().Which.UsedPercentage.Should().Be(96);
    }

    [Fact]
    public async Task RefreshAsync_ShouldEvaluateOnlyFreshAgents_WhenFreshnessIsMixed()
    {
        await WriteSnapshotAsync(_claudeCode, usedPercentage: 96);
        await WriteSnapshotAsync(_agy, usedPercentage: 99, observedAt: DateTimeOffset.UtcNow.AddHours(-3));
        SettingsService settingsService = CreateSettingsService();
        settingsService.Settings.ReviewedLauncherPresetId = _agy;
        RateLimitRefreshCoordinator coordinator = CreateCoordinator(settingsService: settingsService);

        await coordinator.RefreshAsync(CreateRequest(CreateAgent(_claudeCode), CreateAgent(_agy)));

        _autoPauseGate.PausedLimits.Should().ContainSingle().Which.AgentId.Should().Be(_claudeCode);
    }

    [Fact]
    public async Task RefreshAsync_ShouldNotEvaluateGate_WhenNoSlotHasRateLimitAgent()
    {
        await WriteSnapshotAsync(_claudeCode, usedPercentage: 96);
        SettingsService settingsService = CreateSettingsService();

        // copilot はレートリミットを取得できないため gate 対象外になる
        settingsService.Settings.ReviewerLauncherPresetId = "copilot";
        settingsService.Settings.ReviewedLauncherPresetId = "copilot";
        RateLimitRefreshCoordinator coordinator = CreateCoordinator(settingsService: settingsService);

        RateLimitRefreshResult result = await coordinator.RefreshAsync(CreateRequest(CreateAgent(_claudeCode)));

        result.Limits.Should().ContainSingle();
        _autoPauseGate.PausedLimits.Should().BeEmpty();
    }

    [Fact]
    public async Task RefreshAsync_ShouldThrow_WhenRequestIsNull()
    {
        RateLimitRefreshCoordinator coordinator = CreateCoordinator();

        Func<Task> act = () => coordinator.RefreshAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_ShouldThrow_WhenDependencyIsNull()
    {
        Action act = () => _ = new RateLimitRefreshCoordinator(
            new RateLimitFileService(_settingsDirectory),
            new RateLimitSnapshotService(new RateLimitFileService(_settingsDirectory)),
            null!,
            CreateSettingsService(),
            _autoPauseGate,
            _reminderService.Object);

        act.Should().Throw<ArgumentNullException>();
    }

    public void Dispose()
    {
        if (Directory.Exists(_settingsDirectory))
        {
            Directory.Delete(_settingsDirectory, recursive: true);
        }
    }

    private static RateLimitAgentOption CreateAgent(string id, bool isMonitored = true, bool isAvailable = true)
    {
        RateLimitAgentDefinition definition = RateLimitAgentCatalog.All.First(agent => agent.Id == id);
        return new RateLimitAgentOption(definition.Id, definition.DisplayName, isAvailable) { IsMonitored = isMonitored };
    }

    private static RateLimitRefreshRequest CreateRequest(params RateLimitAgentOption[] agents)
        => new(agents, string.Empty, _gatewayUrl);

    private static string BuildLegacyPayload(string sourceLabel)
        => $$"""
            {"limits":[{"id":"weekly","label":"{{sourceLabel}}","resetAt":"2026-09-20T00:00:00Z"}]}
            """;

    private SettingsService CreateSettingsService() => new(_settingsDirectory, pnpmBinDir: string.Empty);

    private RateLimitRefreshCoordinator CreateCoordinator(
        SettingsService? settingsService = null,
        McpResourceTextReader? mcpResourceReader = null,
        IProcessRunner? processRunner = null,
        Func<string, string?>? commandResolver = null)
    {
        RateLimitFileService fileService = new(_settingsDirectory);
        RateLimitSnapshotService snapshotService = new(
            fileService,
            new CodexAppServerRateLimitClient(
                processRunner ?? new Mock<IProcessRunner>().Object,
                commandResolver: commandResolver));
        return new RateLimitRefreshCoordinator(
            fileService,
            snapshotService,
            new RateLimitSnapshotResolver(snapshotService),
            settingsService ?? CreateSettingsService(),
            _autoPauseGate,
            _reminderService.Object,
            mcpResourceReader);
    }

    private async Task WriteSnapshotAsync(string agentId, double usedPercentage, DateTimeOffset? observedAt = null)
    {
        DateTimeOffset observed = observedAt ?? DateTimeOffset.UtcNow;
        string directory = Path.Combine(_settingsDirectory, "ratelimit-status");
        Directory.CreateDirectory(directory);
        string json = $$"""
            {"schemaVersion":1,"agentId":"{{agentId}}","observedAt":"{{observed:O}}","limits":[{"id":"five-hour","label":"5時間枠","resetAt":"{{observed.AddHours(5):O}}","usedPercentage":{{usedPercentage.ToString(CultureInfo.InvariantCulture)}}}]}
            """;
        await File.WriteAllTextAsync(Path.Combine(directory, $"{agentId}.json"), json);
    }

    private async Task WriteLegacySnapshotAsync(string agentId)
    {
        string directory = Path.Combine(_settingsDirectory, "ratelimit-status");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, $"{agentId}.json"), BuildLegacyPayload("5時間枠"));
    }
}
