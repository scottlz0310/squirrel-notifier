// <copyright file="ReviewStartCoordinatorTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Globalization;
using FluentAssertions;
using SquirrelNotifier.WinUI3.Helpers;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Tests.Services;

// 起動経路の enum は internal のため、InlineData では名前で受け取り Enum.Parse で解決する
public sealed class ReviewStartCoordinatorTests : IDisposable
{
    private const string _pausedAgentId = "claude-code";

    private readonly string _workingDirectory = Path.Combine(Path.GetTempPath(), $"ReviewStartCoordinatorTests_{Guid.NewGuid()}");
    private readonly List<string> _logLines = [];

    [Theory]
    [InlineData("Reviewer", "Manual")]
    [InlineData("Reviewer", "Automatic")]
    [InlineData("Reviewed", "Manual")]
    [InlineData("Reviewed", "Automatic")]
    public async Task StartAsync_ShouldStartSession_WhenIdleAndNotPaused(string role, string trigger)
    {
        FakeLauncherService launcher = new();
        ReviewStartCoordinator coordinator = CreateCoordinator(launcher);
        LauncherRole launcherRole = Enum.Parse<LauncherRole>(role);

        ReviewStartResult result = await coordinator.StartAsync(
            CreateReviewEvent(),
            launcherRole,
            Enum.Parse<ReviewStartTrigger>(trigger),
            _ => Task.FromResult(true));

        result.Status.Should().Be(ReviewStartStatus.Started);
        result.IsStarted.Should().BeTrue();
        result.Launch.Should().NotBeNull();
        launcher.StartSessionCalls.Should().ContainSingle().Which.Should().Be(launcherRole);
    }

    [Theory]
    [InlineData("Reviewer", "レビューする")]
    [InlineData("Reviewed", "レビューに対応")]
    public async Task StartAsync_ShouldTitleSessionWithRoleLabel(string role, string expectedRoleLabel)
    {
        ReviewStartCoordinator coordinator = CreateCoordinator(new FakeLauncherService());

        ReviewStartResult result = await coordinator.StartAsync(
            CreateReviewEvent(),
            Enum.Parse<LauncherRole>(role),
            ReviewStartTrigger.Manual,
            _ => Task.FromResult(true));

        result.Launch!.ViewModel.Title.Should().Be($"owner/repo#42（{expectedRoleLabel}）");
    }

    [Theory]
    [InlineData("Manual", false)]
    [InlineData("Automatic", true)]
    public async Task StartAsync_ShouldSkipBusy_WhenAnotherReviewIsRunning(string trigger, bool expectsLog)
    {
        FakeLauncherService launcher = new() { IsRunning = true };
        ReviewStartCoordinator coordinator = CreateCoordinator(launcher);

        ReviewStartResult result = await coordinator.StartAsync(
            CreateReviewEvent(),
            LauncherRole.Reviewer,
            Enum.Parse<ReviewStartTrigger>(trigger),
            _ => Task.FromResult(true));

        result.Status.Should().Be(ReviewStartStatus.SkippedBusy);
        result.Launch.Should().BeNull();
        launcher.StartSessionCalls.Should().BeEmpty();
        _logLines.Any(line => line.Contains(ReviewAutoStartPolicy.BusyReasonText, StringComparison.Ordinal))
            .Should().Be(expectsLog);
    }

    [Fact]
    public async Task StartAsync_ShouldSkipAutoPaused_WhenAutomaticAndAgentIsPaused()
    {
        await WriteSnapshotAsync(_pausedAgentId, usedPercentage: 96);
        FakeLauncherService launcher = new();
        ReviewStartCoordinator coordinator = CreateCoordinator(launcher);
        bool promptShown = false;

        ReviewStartResult result = await coordinator.StartAsync(
            CreateReviewEvent(),
            LauncherRole.Reviewer,
            ReviewStartTrigger.Automatic,
            _ =>
            {
                promptShown = true;
                return Task.FromResult(true);
            });

        result.Status.Should().Be(ReviewStartStatus.SkippedAutoPaused);
        promptShown.Should().BeFalse();
        launcher.StartSessionCalls.Should().BeEmpty();
        _logLines.Should().ContainSingle(line => line.Contains("Auto-Pause 中のため", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true, "Started")]
    [InlineData(false, "CancelledByUser")]
    public async Task StartAsync_ShouldFollowOverridePrompt_WhenManualAndAgentIsPaused(
        bool overrideConfirmed,
        string expectedStatus)
    {
        await WriteSnapshotAsync(_pausedAgentId, usedPercentage: 96);
        FakeLauncherService launcher = new();
        ReviewStartCoordinator coordinator = CreateCoordinator(launcher);
        AutoPausedLimit? promptedLimit = null;

        ReviewStartResult result = await coordinator.StartAsync(
            CreateReviewEvent(),
            LauncherRole.Reviewer,
            ReviewStartTrigger.Manual,
            pausedLimit =>
            {
                promptedLimit = pausedLimit;
                return Task.FromResult(overrideConfirmed);
            });

        result.Status.Should().Be(Enum.Parse<ReviewStartStatus>(expectedStatus));
        promptedLimit.Should().NotBeNull();
        promptedLimit!.AgentId.Should().Be(_pausedAgentId);
        launcher.StartSessionCalls.Should().HaveCount(overrideConfirmed ? 1 : 0);
    }

    [Fact]
    public async Task StartAsync_ShouldNotEvaluateAutoPause_WhenSlotHasNoRateLimitAgent()
    {
        await WriteSnapshotAsync(_pausedAgentId, usedPercentage: 96);
        FakeLauncherService launcher = new();
        SettingsService settingsService = CreateSettingsService();

        // copilot はレートリミットを取得できないため gate 対象外（NotApplicable）になる
        settingsService.Settings.ReviewerLauncherPresetId = "copilot";
        ReviewStartCoordinator coordinator = CreateCoordinator(launcher, settingsService);

        ReviewStartResult result = await coordinator.StartAsync(
            CreateReviewEvent(),
            LauncherRole.Reviewer,
            ReviewStartTrigger.Manual,
            _ => Task.FromResult(false));

        result.Status.Should().Be(ReviewStartStatus.Started);
    }

    [Fact]
    public async Task StartAsync_ShouldSkipReentrant_WhenCalledWhileOverridePromptIsOpen()
    {
        await WriteSnapshotAsync(_pausedAgentId, usedPercentage: 96);
        FakeLauncherService launcher = new();
        ReviewStartCoordinator coordinator = CreateCoordinator(launcher);
        ReviewStartResult? reentrantResult = null;

        // 確認ダイアログの表示中はまだ IsRunning が false のため、連打による再入は
        // 起動中フラグだけが止められる（#147 レビュー指摘の多重表示回帰）
        ReviewStartResult result = await coordinator.StartAsync(
            CreateReviewEvent(),
            LauncherRole.Reviewer,
            ReviewStartTrigger.Manual,
            async _ =>
            {
                reentrantResult = await coordinator.StartAsync(
                    CreateReviewEvent(),
                    LauncherRole.Reviewer,
                    ReviewStartTrigger.Manual,
                    _ => Task.FromResult(true));
                return true;
            });

        result.Status.Should().Be(ReviewStartStatus.Started);
        reentrantResult!.Status.Should().Be(ReviewStartStatus.SkippedReentrant);
        launcher.StartSessionCalls.Should().ContainSingle();
    }

    [Theory]
    [InlineData("Manual", false)]
    [InlineData("Automatic", true)]
    public async Task StartAsync_ShouldReturnFailure_WhenStartSessionThrows(string trigger, bool expectsLog)
    {
        FakeLauncherService launcher = new() { StartSessionException = new InvalidOperationException("起動できません") };
        ReviewStartCoordinator coordinator = CreateCoordinator(launcher);

        ReviewStartResult result = await coordinator.StartAsync(
            CreateReviewEvent(),
            LauncherRole.Reviewer,
            Enum.Parse<ReviewStartTrigger>(trigger),
            _ => Task.FromResult(true));

        result.Status.Should().Be(ReviewStartStatus.Failed);
        result.FailureMessage.Should().Be("起動できません");
        _logLines.Any(line => line.Contains("自動起動が失敗しました", StringComparison.Ordinal))
            .Should().Be(expectsLog);
    }

    [Fact]
    public async Task StartAsync_ShouldClearPendingFlag_AfterFailure()
    {
        FakeLauncherService launcher = new() { StartSessionException = new InvalidOperationException("起動できません") };
        ReviewStartCoordinator coordinator = CreateCoordinator(launcher);

        await coordinator.StartAsync(
            CreateReviewEvent(),
            LauncherRole.Reviewer,
            ReviewStartTrigger.Manual,
            _ => Task.FromResult(true));

        coordinator.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task StartAsync_ShouldThrow_WhenManualHasNoOverridePrompt()
    {
        ReviewStartCoordinator coordinator = CreateCoordinator(new FakeLauncherService());

        Func<Task> act = () => coordinator.StartAsync(
            CreateReviewEvent(), LauncherRole.Reviewer, ReviewStartTrigger.Manual);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Theory]
    [InlineData(false, "opened", "SkippedDisabled")]
    [InlineData(true, "closed", "SkippedUnsupportedReason")]
    [InlineData(true, "opened", "Started")]
    [InlineData(true, "re-review-requested", "Started")]
    public async Task TryStartAutomaticallyAsync_ShouldFollowAutoStartSetting(
        bool autoStartEnabled,
        string reason,
        string expectedStatus)
    {
        FakeLauncherService launcher = new();
        SettingsService settingsService = CreateSettingsService();
        settingsService.UpdateAutoReviewStartEnabled(autoStartEnabled);
        ReviewStartCoordinator coordinator = CreateCoordinator(launcher, settingsService);

        ReviewStartResult result = await coordinator.TryStartAutomaticallyAsync(CreateReviewEvent(reason));

        result.Status.Should().Be(Enum.Parse<ReviewStartStatus>(expectedStatus));
        launcher.StartSessionCalls.Should().HaveCount(expectedStatus == "Started" ? 1 : 0);
    }

    [Fact]
    public async Task TryStartAutomaticallyAsync_ShouldNotLog_WhenSettingIsDisabled()
    {
        ReviewStartCoordinator coordinator = CreateCoordinator(new FakeLauncherService());

        await coordinator.TryStartAutomaticallyAsync(CreateReviewEvent());

        _logLines.Should().BeEmpty();
    }

    [Fact]
    public async Task TryStartAutomaticallyAsync_ShouldSkipBusy_WhenAnotherReviewIsRunning()
    {
        FakeLauncherService launcher = new() { IsRunning = true };
        SettingsService settingsService = CreateSettingsService();
        settingsService.UpdateAutoReviewStartEnabled(true);
        ReviewStartCoordinator coordinator = CreateCoordinator(launcher, settingsService);

        ReviewStartResult result = await coordinator.TryStartAutomaticallyAsync(CreateReviewEvent());

        result.Status.Should().Be(ReviewStartStatus.SkippedBusy);
        _logLines.Should().ContainSingle(line =>
            line.Contains(ReviewAutoStartPolicy.BusyReasonText, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TryStartAutomaticallyAsync_ShouldLogStart_WhenAutoStarting()
    {
        SettingsService settingsService = CreateSettingsService();
        settingsService.UpdateAutoReviewStartEnabled(true);
        ReviewStartCoordinator coordinator = CreateCoordinator(new FakeLauncherService(), settingsService);

        await coordinator.TryStartAutomaticallyAsync(CreateReviewEvent());

        _logLines.Should().ContainSingle(line =>
            line.Contains("[Auto] owner/repo #42 のレビューを自動起動します（reason: opened）。", StringComparison.Ordinal));
    }

    [Fact]
    public void IsBusy_ShouldFollowLauncherRunningState()
    {
        FakeLauncherService launcher = new();
        ReviewStartCoordinator coordinator = CreateCoordinator(launcher);

        coordinator.IsBusy.Should().BeFalse();

        launcher.IsRunning = true;

        coordinator.IsBusy.Should().BeTrue();
    }

    public void Dispose()
    {
        if (Directory.Exists(_workingDirectory))
        {
            Directory.Delete(_workingDirectory, recursive: true);
        }
    }

    private static ReviewEvent CreateReviewEvent(string reason = "opened") => new()
    {
        Repository = "owner/repo",
        PrNumber = 42,
        Reason = reason,
    };

    private SettingsService CreateSettingsService() => new(_workingDirectory, pnpmBinDir: string.Empty);

    private ReviewStartCoordinator CreateCoordinator(
        IReviewLauncherService launcherService,
        SettingsService? settingsService = null)
    {
        LoggingService loggingService = new(_workingDirectory);
        loggingService.LogAppended += (_, line) => _logLines.Add(line);
        return new ReviewStartCoordinator(
            launcherService,
            settingsService ?? CreateSettingsService(),
            new RateLimitSnapshotService(new RateLimitFileService(_workingDirectory)),
            new AutoPauseGate(),
            loggingService);
    }

    private async Task WriteSnapshotAsync(string agentId, double usedPercentage)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string directory = Path.Combine(_workingDirectory, "ratelimit-status");
        Directory.CreateDirectory(directory);
        string json = $$"""
            {"schemaVersion":1,"agentId":"{{agentId}}","observedAt":"{{now:O}}","limits":[{"id":"five-hour","label":"5時間枠","resetAt":"{{now.AddHours(5):O}}","usedPercentage":{{usedPercentage.ToString(CultureInfo.InvariantCulture)}}}]}
            """;
        await File.WriteAllTextAsync(Path.Combine(directory, $"{agentId}.json"), json);
    }

    private sealed class FakeLauncherService : IReviewLauncherService
    {
        public bool IsRunning { get; set; }

        public Exception? StartSessionException { get; set; }

        public List<LauncherRole> StartSessionCalls { get; } = [];

        public AgentExecutionSession StartSession(ReviewEvent reviewEvent, LauncherRole role, CancellationToken cancellationToken)
        {
            if (StartSessionException is not null)
            {
                throw StartSessionException;
            }

            StartSessionCalls.Add(role);
            return new AgentExecutionSession(TimeProvider.System);
        }

        public Task<LauncherResult> LaunchAsync(ReviewEvent reviewEvent, LauncherRole role, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public void Cancel() => throw new NotSupportedException();

        public Task<string> BuildCommandLineAsync(ReviewEvent reviewEvent, LauncherRole role, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
