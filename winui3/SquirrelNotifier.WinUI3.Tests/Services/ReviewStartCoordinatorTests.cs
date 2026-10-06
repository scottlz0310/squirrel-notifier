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
    private readonly PendingReviewStartQueue _pendingQueue = new();
    private readonly AutoPauseResumeScheduler _autoPauseResumeScheduler = new();
    private readonly CiSettleTestClock _ciClock = new(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));
    private readonly ScriptedCiSettleSource _ciSource = new();

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

    // 自動起動では、判定と起動の間に別のレビューが始まった場合も保留する（#339）
    [Theory]
    [InlineData("Manual", false)]
    [InlineData("Automatic", true)]
    public async Task StartAsync_ShouldSkipBusy_WhenAnotherReviewIsRunning(string trigger, bool expectsHeld)
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
            .Should().Be(expectsHeld);
        _pendingQueue.Count.Should().Be(expectsHeld ? 1 : 0);
    }

    [Theory]
    [InlineData("Reviewer", "Manual", false, 0)]
    [InlineData("Reviewer", "Automatic", false, 0)]
    [InlineData("Reviewed", "Manual", false, 1)]
    [InlineData("Reviewer", "Manual", true, 1)]
    public async Task StartAsync_ShouldRemovePendingPullRequest_OnlyWhenReviewerStarts(
        string role,
        string trigger,
        bool startSessionThrows,
        int expectedPendingCount)
    {
        // 保留は reviewer の起動を待つためのもの。reviewed 側の起動や起動失敗では外さない
        FakeLauncherService launcher = new()
        {
            StartSessionException = startSessionThrows ? new InvalidOperationException("起動できません") : null,
        };
        ReviewStartCoordinator coordinator = CreateCoordinator(launcher);
        _pendingQueue.AddOrReplace(CreateReviewEvent("synchronized"));

        await coordinator.StartAsync(
            CreateReviewEvent(),
            Enum.Parse<LauncherRole>(role),
            Enum.Parse<ReviewStartTrigger>(trigger),
            _ => Task.FromResult(true));

        _pendingQueue.Count.Should().Be(expectedPendingCount);
    }

    // Auto-Pause 中の自動起動は、解除後の再評価まで保留する（#340）
    [Fact]
    public async Task StartAsync_ShouldHoldEvent_WhenAutomaticAndAgentIsPaused()
    {
        await WriteSnapshotAsync(_pausedAgentId, usedPercentage: 96);
        FakeLauncherService launcher = new();
        ReviewStartCoordinator coordinator = CreateCoordinator(launcher);
        ReviewEvent reviewEvent = CreateReviewEvent();
        bool promptShown = false;

        ReviewStartResult result = await coordinator.StartAsync(
            reviewEvent,
            LauncherRole.Reviewer,
            ReviewStartTrigger.Automatic,
            _ =>
            {
                promptShown = true;
                return Task.FromResult(true);
            });

        result.Status.Should().Be(ReviewStartStatus.SkippedAutoPaused);
        result.HoldReason.Should().Be(ReviewAutoStartPolicy.AutoPausedHoldLabel);
        promptShown.Should().BeFalse();
        launcher.StartSessionCalls.Should().BeEmpty();
        _pendingQueue.Snapshot()[0].Should().BeSameAs(reviewEvent);
        _logLines.Should().ContainSingle(line =>
            line.Contains("のレビューを保留しました: Auto-Pause 中のため", StringComparison.Ordinal)
            && line.Contains("解除後に自動起動します（reason: opened）。", StringComparison.Ordinal));
    }

    // 保留中の agent が別スロットで解除された等で gate が Paused を返さなくなったら、そのまま起動する（#340）
    [Fact]
    public async Task StartAsync_ShouldStartHeldEvent_WhenAutoPauseIsReleased()
    {
        await WriteSnapshotAsync(_pausedAgentId, usedPercentage: 96);
        FakeLauncherService launcher = new();
        ReviewStartCoordinator coordinator = CreateCoordinator(launcher);
        ReviewEvent reviewEvent = CreateReviewEvent();
        await coordinator.StartAsync(reviewEvent, LauncherRole.Reviewer, ReviewStartTrigger.Automatic);
        await WriteSnapshotAsync(_pausedAgentId, usedPercentage: 42);

        ReviewStartResult result = await coordinator.StartAsync(
            reviewEvent, LauncherRole.Reviewer, ReviewStartTrigger.Automatic);

        result.Status.Should().Be(ReviewStartStatus.Started);
        _pendingQueue.Count.Should().Be(0);
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

    // 確認ダイアログの表示中はまだ IsRunning が false のため、連打による再入は
    // 起動中フラグだけが止められる（#147 レビュー指摘の多重表示回帰）。
    // 自動起動の再入は、実行中と同じく保留しないとイベントが失われる（#339）
    [Theory]
    [InlineData("Manual", "SkippedReentrant", 0)]
    [InlineData("Automatic", "SkippedBusy", 1)]
    public async Task StartAsync_ShouldNotStartReentrantCall_WhenCalledWhileOverridePromptIsOpen(
        string reentrantTrigger,
        string expectedStatus,
        int expectedPendingCount)
    {
        await WriteSnapshotAsync(_pausedAgentId, usedPercentage: 96);
        FakeLauncherService launcher = new();
        ReviewStartCoordinator coordinator = CreateCoordinator(launcher);
        ReviewStartResult? reentrantResult = null;
        ReviewEvent reentrantEvent = new() { Repository = "owner/repo", PrNumber = 43, Reason = "opened" };

        ReviewStartResult result = await coordinator.StartAsync(
            CreateReviewEvent(),
            LauncherRole.Reviewer,
            ReviewStartTrigger.Manual,
            async _ =>
            {
                reentrantResult = await coordinator.StartAsync(
                    reentrantEvent,
                    LauncherRole.Reviewer,
                    Enum.Parse<ReviewStartTrigger>(reentrantTrigger),
                    _ => Task.FromResult(true));
                return true;
            });

        result.Status.Should().Be(ReviewStartStatus.Started);
        reentrantResult!.Status.Should().Be(Enum.Parse<ReviewStartStatus>(expectedStatus));
        launcher.StartSessionCalls.Should().ContainSingle();
        _pendingQueue.Count.Should().Be(expectedPendingCount);
    }

    // 再評価中の自動起動がログ書き込みを await している間に手動起動が始まると、自動起動は再入になる。
    // このとき保留を失わず、手動起動が起動せずに終わったら StartAbandoned で再評価を促す（#339 レビュー指摘）
    [Fact]
    public async Task TryStartAutomaticallyAsync_ShouldKeepPendingEvent_WhenManualStartBeginsDuringAutoStartLog()
    {
        await WriteSnapshotAsync(_pausedAgentId, usedPercentage: 96);
        FakeLauncherService launcher = new();
        SettingsService settingsService = CreateSettingsService();
        settingsService.UpdateAutoReviewStartEnabled(true);
        TaskCompletionSource<bool> overridePrompt = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<ReviewStartResult>? manualStart = null;
        ReviewStartCoordinator? coordinator = null;
        coordinator = CreateCoordinator(launcher, settingsService, line =>
        {
            if (manualStart is null && line.Contains("のレビューを自動起動します", StringComparison.Ordinal))
            {
                manualStart = coordinator!.StartAsync(
                    new ReviewEvent { Repository = "owner/repo", PrNumber = 43, Reason = "opened" },
                    LauncherRole.Reviewer,
                    ReviewStartTrigger.Manual,
                    _ => overridePrompt.Task);
            }
        });
        int abandonedCount = 0;
        coordinator.StartAbandoned += (_, _) => Interlocked.Increment(ref abandonedCount);
        ReviewEvent pendingEvent = CreateReviewEvent();
        _pendingQueue.AddOrReplace(pendingEvent);

        ReviewStartResult result = await coordinator.TryStartAutomaticallyAsync(pendingEvent);

        result.Status.Should().Be(ReviewStartStatus.SkippedBusy);
        _pendingQueue.Snapshot()[0].Should().BeSameAs(pendingEvent);
        abandonedCount.Should().Be(0);

        overridePrompt.SetResult(false);
        (await manualStart!).Status.Should().Be(ReviewStartStatus.CancelledByUser);

        launcher.StartSessionCalls.Should().BeEmpty();
        _pendingQueue.Snapshot()[0].Should().BeSameAs(pendingEvent);
        abandonedCount.Should().Be(1);
    }

    // Auto-Pause で保留した場合は、再評価しても同じ判定になるため契機にしない（#340）
    [Theory]
    [InlineData("Started", 0)]
    [InlineData("SkippedBusy", 0)]
    [InlineData("SkippedAutoPaused", 0)]
    [InlineData("CancelledByUser", 1)]
    [InlineData("Failed", 1)]
    [InlineData("Cancelled", 1)]
    public async Task StartAsync_ShouldRaiseStartAbandoned_OnlyWhenStartBeganWithoutLaunching(
        string scenario,
        int expectedAbandonedCount)
    {
        await WriteSnapshotAsync(
            _pausedAgentId,
            usedPercentage: scenario is "CancelledByUser" or "SkippedAutoPaused" ? 96 : 42);
        FakeLauncherService launcher = new()
        {
            IsRunning = scenario == "SkippedBusy",
            StartSessionException = scenario == "Failed" ? new InvalidOperationException("起動できません") : null,
        };
        ReviewStartCoordinator coordinator = CreateCoordinator(launcher);
        int abandonedCount = 0;
        coordinator.StartAbandoned += (_, _) => abandonedCount++;
        using CancellationTokenSource cts = new();
        if (scenario == "Cancelled")
        {
            await cts.CancelAsync();
        }

        try
        {
            await coordinator.StartAsync(
                CreateReviewEvent(),
                LauncherRole.Reviewer,
                scenario == "SkippedAutoPaused" ? ReviewStartTrigger.Automatic : ReviewStartTrigger.Manual,
                _ => Task.FromResult(false),
                cts.Token);
        }
        catch (OperationCanceledException) when (scenario == "Cancelled")
        {
        }

        abandonedCount.Should().Be(expectedAbandonedCount);
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

    [Theory]
    [InlineData("Manual")]
    [InlineData("Automatic")]
    public async Task StartAsync_ShouldPropagateCancellation_WhenCallerCancelled(string trigger)
    {
        // 呼出元のキャンセルは起動失敗（Failed・自動起動失敗ログ）に変えず例外として伝播させる（#333）
        await WriteSnapshotAsync(_pausedAgentId, usedPercentage: 42);
        FakeLauncherService launcher = new();
        ReviewStartCoordinator coordinator = CreateCoordinator(launcher);
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        Func<Task> act = () => coordinator.StartAsync(
            CreateReviewEvent(),
            LauncherRole.Reviewer,
            Enum.Parse<ReviewStartTrigger>(trigger),
            _ => Task.FromResult(true),
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        launcher.StartSessionCalls.Should().BeEmpty();
        _logLines.Should().BeEmpty();
        coordinator.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task StartAsync_ShouldAcceptNextStart_AfterCancellation()
    {
        await WriteSnapshotAsync(_pausedAgentId, usedPercentage: 42);
        FakeLauncherService launcher = new();
        ReviewStartCoordinator coordinator = CreateCoordinator(launcher);
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();
        Func<Task> cancelled = () => coordinator.StartAsync(
            CreateReviewEvent(),
            LauncherRole.Reviewer,
            ReviewStartTrigger.Manual,
            _ => Task.FromResult(true),
            cts.Token);
        await cancelled.Should().ThrowAsync<OperationCanceledException>();

        ReviewStartResult result = await coordinator.StartAsync(
            CreateReviewEvent(),
            LauncherRole.Reviewer,
            ReviewStartTrigger.Manual,
            _ => Task.FromResult(true));

        result.Status.Should().Be(ReviewStartStatus.Started);
        launcher.StartSessionCalls.Should().ContainSingle();
    }

    [Theory]
    [InlineData(false, "Manual")]
    [InlineData(true, "Manual")]
    [InlineData(true, "Automatic")]
    public async Task StartAsync_ShouldReturnFailure_WhenCancellationIsNotRequestedByCaller(
        bool isTaskCanceled,
        string trigger)
    {
        // 起動処理内のタイムアウト等は呼出元のキャンセルではないため、従来どおり起動失敗として扱う
        FakeLauncherService launcher = new()
        {
            StartSessionException = isTaskCanceled
                ? new TaskCanceledException("起動がタイムアウトしました")
                : new OperationCanceledException("起動を中断しました"),
        };
        ReviewStartCoordinator coordinator = CreateCoordinator(launcher);

        ReviewStartResult result = await coordinator.StartAsync(
            CreateReviewEvent(),
            LauncherRole.Reviewer,
            Enum.Parse<ReviewStartTrigger>(trigger),
            _ => Task.FromResult(true));

        result.Status.Should().Be(ReviewStartStatus.Failed);
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
    public async Task TryStartAutomaticallyAsync_ShouldHoldEvent_WhenAnotherReviewIsRunning()
    {
        FakeLauncherService launcher = new() { IsRunning = true };
        SettingsService settingsService = CreateSettingsService();
        settingsService.UpdateAutoReviewStartEnabled(true);
        ReviewStartCoordinator coordinator = CreateCoordinator(launcher, settingsService);
        ReviewEvent reviewEvent = CreateReviewEvent();

        ReviewStartResult result = await coordinator.TryStartAutomaticallyAsync(reviewEvent);

        result.Status.Should().Be(ReviewStartStatus.SkippedBusy);
        _pendingQueue.Snapshot()[0].Should().BeSameAs(reviewEvent);
        _logLines.Should().ContainSingle().Which.Should().Contain(
            $"[Auto] owner/repo #42 のレビューを保留しました: {ReviewAutoStartPolicy.BusyReasonText}。実行終了後に自動起動します（reason: opened）。");
    }

    [Theory]
    [InlineData("owner/repo", 42, 1, 1, "保留中の owner/repo #42 を新しいイベントで更新しました（reason: re-review-requested）。")]
    [InlineData("owner/repo", 43, 0, 2, "owner/repo #43 のレビューを保留しました")]
    [InlineData("owner/repo", 42, -1, 1, null)]
    public async Task TryStartAutomaticallyAsync_ShouldLogHoldChange_WhenAnotherEventArrivesWhileRunning(
        string repository,
        int prNumber,
        int receivedOffsetSeconds,
        int expectedPendingCount,
        string? expectedLog)
    {
        FakeLauncherService launcher = new() { IsRunning = true };
        SettingsService settingsService = CreateSettingsService();
        settingsService.UpdateAutoReviewStartEnabled(true);
        ReviewStartCoordinator coordinator = CreateCoordinator(launcher, settingsService);
        ReviewEvent first = CreateReviewEvent("synchronized");
        await coordinator.TryStartAutomaticallyAsync(first);
        _logLines.Clear();

        await coordinator.TryStartAutomaticallyAsync(new ReviewEvent
        {
            Repository = repository,
            PrNumber = prNumber,
            Reason = "re-review-requested",
            ReceivedTime = first.ReceivedTime.AddSeconds(receivedOffsetSeconds),
        });

        _pendingQueue.Count.Should().Be(expectedPendingCount);
        if (expectedLog is null)
        {
            _logLines.Should().BeEmpty();
        }
        else
        {
            _logLines.Should().ContainSingle().Which.Should().Contain(expectedLog);
        }
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

    // 保留の理由は、公開状態（review-status.json）が「なぜ待っているか」を出せるよう、保留のたびに通知する（#462）
    [Fact]
    public async Task TryStartAutomaticallyAsync_ShouldObserveBusyHold_EvenWhenQueueKeepsTheSameEvent()
    {
        FakeLauncherService launcher = new() { IsRunning = true };
        ReviewCycleCoordinator cycle = CreateCycleCoordinator();
        List<ReviewHoldObservedEventArgs> holds = CaptureHolds(cycle);
        ReviewStartCoordinator coordinator = CreateCoordinator(launcher, CreateAutoStartSettings(), reviewCycleCoordinator: cycle);
        ReviewEvent reviewEvent = CreateReviewEvent();

        await coordinator.TryStartAutomaticallyAsync(reviewEvent);
        await coordinator.TryStartAutomaticallyAsync(reviewEvent);

        holds.Should().HaveCount(2);
        holds.Should().OnlyContain(hold => hold.Kind == ReviewHoldKind.Busy && ReferenceEquals(hold.ReviewEvent, reviewEvent));
        _pendingQueue.Count.Should().Be(1);
    }

    [Fact]
    public async Task StartAsync_ShouldObserveAutoPauseHold_WhenAutomaticAndAgentIsPaused()
    {
        await WriteSnapshotAsync(_pausedAgentId, usedPercentage: 96);
        ReviewCycleCoordinator cycle = CreateCycleCoordinator();
        List<ReviewHoldObservedEventArgs> holds = CaptureHolds(cycle);
        ReviewStartCoordinator coordinator = CreateCoordinator(new FakeLauncherService(), reviewCycleCoordinator: cycle);
        ReviewEvent reviewEvent = CreateReviewEvent();

        await coordinator.StartAsync(
            reviewEvent,
            LauncherRole.Reviewer,
            ReviewStartTrigger.Automatic,
            _ => Task.FromResult(true));

        holds.Should().ContainSingle().Which.Kind.Should().Be(ReviewHoldKind.AutoPause);
    }

    [Fact]
    public async Task TryStartAutomaticallyAsync_ShouldObserveCiPendingHold_WhenCiIsPending()
    {
        _ciSource.Add(ScriptedCiSettleSource.Snapshot("Pending"));
        ReviewCycleCoordinator cycle = CreateCycleCoordinator();
        List<ReviewHoldObservedEventArgs> holds = CaptureHolds(cycle);
        using ReviewCiSettleGate gate = CreateCiSettleGate();
        ReviewStartCoordinator coordinator = CreateCoordinator(
            new FakeLauncherService(),
            CreateAutoStartSettings(),
            ciSettleGate: gate,
            reviewCycleCoordinator: cycle);

        await coordinator.TryStartAutomaticallyAsync(CreateReviewEvent());

        holds.Should().ContainSingle().Which.Kind.Should().Be(ReviewHoldKind.CiPending);
    }

    [Fact]
    public async Task TryStartAutomaticallyAsync_ShouldNotObserveHold_WhenReviewerStarts()
    {
        ReviewCycleCoordinator cycle = CreateCycleCoordinator();
        List<ReviewHoldObservedEventArgs> holds = CaptureHolds(cycle);
        ReviewStartCoordinator coordinator = CreateCoordinator(
            new FakeLauncherService(),
            CreateAutoStartSettings(),
            reviewCycleCoordinator: cycle);

        ReviewStartResult result = await coordinator.TryStartAutomaticallyAsync(CreateReviewEvent());

        result.Status.Should().Be(ReviewStartStatus.Started);
        holds.Should().BeEmpty();
    }

    // --- CI の確定待ち（暫定、#456） ---

    [Fact]
    public async Task TryStartAutomaticallyAsync_ShouldHoldEvent_WhenCiIsPending()
    {
        _ciSource.Add(ScriptedCiSettleSource.Snapshot("Pending"));
        FakeLauncherService launcher = new();
        using ReviewCiSettleGate gate = CreateCiSettleGate();
        ReviewStartCoordinator coordinator = CreateCoordinator(launcher, CreateAutoStartSettings(), ciSettleGate: gate);
        ReviewEvent reviewEvent = CreateReviewEvent();

        ReviewStartResult result = await coordinator.TryStartAutomaticallyAsync(reviewEvent);

        result.Status.Should().Be(ReviewStartStatus.SkippedCiPending);
        result.HoldReason.Should().Be("CI 完了待ち");
        result.Launch.Should().BeNull();
        launcher.StartSessionCalls.Should().BeEmpty();
        _pendingQueue.Snapshot()[0].Should().BeSameAs(reviewEvent);
        coordinator.IsWaitingForCiSettle(reviewEvent).Should().BeTrue();
        _logLines.Should().ContainSingle().Which.Should().Contain(
            "[Auto] owner/repo #42 のレビューを保留しました: CI 完了待ち（Pending detail）。確定後に自動起動します（reason: opened）。");
    }

    // 保留したイベントを再評価して、確定していたら起動する。待った時間を Recent activity に残す
    [Fact]
    public async Task TryStartAutomaticallyAsync_ShouldStart_WhenPendingCiSettlesOnReevaluation()
    {
        _ciSource.Add(ScriptedCiSettleSource.Snapshot("Pending"), ScriptedCiSettleSource.Snapshot("Passed"));
        FakeLauncherService launcher = new();
        using ReviewCiSettleGate gate = CreateCiSettleGate();
        ReviewStartCoordinator coordinator = CreateCoordinator(launcher, CreateAutoStartSettings(), ciSettleGate: gate);
        ReviewEvent reviewEvent = CreateReviewEvent();
        await coordinator.TryStartAutomaticallyAsync(reviewEvent);
        _ciClock.Advance(TimeSpan.FromSeconds(60));
        _logLines.Clear();

        ReviewStartResult result = await coordinator.TryStartAutomaticallyAsync(reviewEvent);

        result.Status.Should().Be(ReviewStartStatus.Started);
        result.StartNote.Should().BeNull();
        launcher.StartSessionCalls.Should().ContainSingle();
        _pendingQueue.Count.Should().Be(0);
        coordinator.IsWaitingForCiSettle(reviewEvent).Should().BeFalse();
        _logLines.Should().HaveCount(2);
        _logLines[0].Should().Contain("[Auto] owner/repo #42: CI が確定しました（Passed detail。待機 1 分 0 秒）。");
        _logLines[1].Should().Contain("のレビューを自動起動します");
    }

    // 待機が続いている間の再評価は、保留を更新せず、Recent activity にも行を増やさない
    [Fact]
    public async Task TryStartAutomaticallyAsync_ShouldNotLogAgain_WhenStillPendingOnReevaluation()
    {
        _ciSource.Add(ScriptedCiSettleSource.Snapshot("Pending"));
        using ReviewCiSettleGate gate = CreateCiSettleGate();
        ReviewStartCoordinator coordinator = CreateCoordinator(new FakeLauncherService(), CreateAutoStartSettings(), ciSettleGate: gate);
        ReviewEvent reviewEvent = CreateReviewEvent();
        await coordinator.TryStartAutomaticallyAsync(reviewEvent);
        _ciClock.Advance(TimeSpan.FromSeconds(30));
        _logLines.Clear();

        ReviewStartResult result = await coordinator.TryStartAutomaticallyAsync(reviewEvent);

        result.Status.Should().Be(ReviewStartStatus.SkippedCiPending);
        _pendingQueue.Count.Should().Be(1);
        _logLines.Should().BeEmpty();
    }

    [Fact]
    public async Task TryStartAutomaticallyAsync_ShouldStartWithNote_WhenMaxWaitIsReached()
    {
        _ciSource.Add(ScriptedCiSettleSource.Snapshot("Pending"));
        FakeLauncherService launcher = new();
        using ReviewCiSettleGate gate = CreateCiSettleGate();
        ReviewStartCoordinator coordinator = CreateCoordinator(launcher, CreateAutoStartSettings(), ciSettleGate: gate);
        ReviewEvent reviewEvent = CreateReviewEvent();
        ReviewStartResult result = await coordinator.TryStartAutomaticallyAsync(reviewEvent);

        // 確認の間隔ごとの再評価を、上限に達するまで続ける
        for (TimeSpan elapsed = TimeSpan.Zero; elapsed < ReviewCiSettleGate.WaitOptions.MaxWait; elapsed += ReviewCiSettleGate.WaitOptions.Interval)
        {
            result.Status.Should().Be(ReviewStartStatus.SkippedCiPending);
            _ciClock.Advance(ReviewCiSettleGate.WaitOptions.Interval);
            _logLines.Clear();
            result = await coordinator.TryStartAutomaticallyAsync(reviewEvent);
        }

        result.Status.Should().Be(ReviewStartStatus.Started);
        result.StartNote.Should().Be(ReviewAutoStartPolicy.CiSettleTimedOutText);
        launcher.StartSessionCalls.Should().ContainSingle();
        _logLines[0].Should().Contain("CI 待機の上限に達したため起動します");
    }

    // 失敗・取得不能は待たずに起動し、理由を Recent activity に残す。PR が閉じていれば起動しない
    [Theory]
    [InlineData("Failed", "Started", "CI に失敗があるため、待たずに起動します（Failed detail）。")]
    [InlineData("Unavailable", "Started", "CI の状態を取得できないため、待たずに起動します: Unavailable detail")]
    [InlineData("PullRequestClosed", "SkippedPullRequestClosed", "PR が merge または close されているため、自動起動しません。")]
    public async Task TryStartAutomaticallyAsync_ShouldNotWait_WhenCiIsAlreadyDetermined(
        string ciState,
        string expectedStatus,
        string expectedLog)
    {
        _ciSource.Add(ScriptedCiSettleSource.Snapshot(ciState));
        FakeLauncherService launcher = new();
        using ReviewCiSettleGate gate = CreateCiSettleGate();
        ReviewStartCoordinator coordinator = CreateCoordinator(launcher, CreateAutoStartSettings(), ciSettleGate: gate);

        ReviewStartResult result = await coordinator.TryStartAutomaticallyAsync(CreateReviewEvent());

        result.Status.Should().Be(Enum.Parse<ReviewStartStatus>(expectedStatus));
        result.StartNote.Should().BeNull();
        launcher.StartSessionCalls.Should().HaveCount(expectedStatus == "Started" ? 1 : 0);
        _pendingQueue.Count.Should().Be(0);
        _logLines.Should().Contain(line => line.Contains($"[Auto] owner/repo #42: {expectedLog}", StringComparison.Ordinal));
    }

    // 待たずに確定した場合は、CI の待機を入れる前と記録が変わらない
    [Fact]
    public async Task TryStartAutomaticallyAsync_ShouldOnlyLogStart_WhenCiIsAlreadyPassed()
    {
        _ciSource.Add(ScriptedCiSettleSource.Snapshot("Passed"));
        using ReviewCiSettleGate gate = CreateCiSettleGate();
        ReviewStartCoordinator coordinator = CreateCoordinator(new FakeLauncherService(), CreateAutoStartSettings(), ciSettleGate: gate);

        ReviewStartResult result = await coordinator.TryStartAutomaticallyAsync(CreateReviewEvent());

        result.Status.Should().Be(ReviewStartStatus.Started);
        _logLines.Should().ContainSingle().Which.Should().Contain("[Auto] owner/repo #42 のレビューを自動起動します（reason: opened）。");
    }

    // 設定 off・対象外の reason・別のレビューの実行中では、CI を確認しない（gh を呼ばない）
    [Theory]
    [InlineData(false, "opened", false)]
    [InlineData(true, "review-posted", false)]
    [InlineData(true, "opened", true)]
    public async Task TryStartAutomaticallyAsync_ShouldNotCheckCi_WhenNotReadyToStart(bool autoStartEnabled, string reason, bool isRunning)
    {
        _ciSource.Add(ScriptedCiSettleSource.Snapshot("Passed"));
        SettingsService settingsService = CreateSettingsService();
        settingsService.UpdateAutoReviewStartEnabled(autoStartEnabled);
        using ReviewCiSettleGate gate = CreateCiSettleGate();
        ReviewStartCoordinator coordinator = CreateCoordinator(
            new FakeLauncherService { IsRunning = isRunning }, settingsService, ciSettleGate: gate);

        await coordinator.TryStartAutomaticallyAsync(CreateReviewEvent(reason));

        _ciSource.Calls.Should().BeEmpty();
    }

    // 手動の「レビューする」は従来どおり即時に起動する。CI を確認せず、待機の状態も破棄する
    [Fact]
    public async Task StartAsync_ShouldStartManuallyWithoutCheckingCi_AndForgetWait()
    {
        _ciSource.Add(ScriptedCiSettleSource.Snapshot("Pending"));
        FakeLauncherService launcher = new();
        using ReviewCiSettleGate gate = CreateCiSettleGate();
        ReviewStartCoordinator coordinator = CreateCoordinator(launcher, CreateAutoStartSettings(), ciSettleGate: gate);
        ReviewEvent reviewEvent = CreateReviewEvent();
        await coordinator.TryStartAutomaticallyAsync(reviewEvent);
        _ciSource.Calls.Should().ContainSingle();

        ReviewStartResult result = await coordinator.StartAsync(
            reviewEvent,
            LauncherRole.Reviewer,
            ReviewStartTrigger.Manual,
            _ => Task.FromResult(true));

        result.Status.Should().Be(ReviewStartStatus.Started);
        launcher.StartSessionCalls.Should().ContainSingle();
        _ciSource.Calls.Should().ContainSingle();
        coordinator.IsWaitingForCiSettle(reviewEvent).Should().BeFalse();
        _pendingQueue.Count.Should().Be(0);
    }

    // reviewed 側の起動は CI の待機と無関係
    [Fact]
    public async Task StartAsync_ShouldKeepWait_WhenReviewedSideStarts()
    {
        _ciSource.Add(ScriptedCiSettleSource.Snapshot("Pending"));
        using ReviewCiSettleGate gate = CreateCiSettleGate();
        ReviewStartCoordinator coordinator = CreateCoordinator(new FakeLauncherService(), CreateAutoStartSettings(), ciSettleGate: gate);
        ReviewEvent reviewEvent = CreateReviewEvent();
        await coordinator.TryStartAutomaticallyAsync(reviewEvent);

        await coordinator.StartAsync(
            reviewEvent,
            LauncherRole.Reviewed,
            ReviewStartTrigger.Manual,
            _ => Task.FromResult(true));

        coordinator.IsWaitingForCiSettle(reviewEvent).Should().BeTrue();
    }

    // CI の確認（gh）の await の間に、同じ PR を手動で起動した場合、自動評価はそれを知らずに続行してはならない。
    // 続行すると、手動のレビューが実行中なら保留に復活して終了後に二重起動し、終わっていればそのまま二重起動する（#459 のレビュー指摘）
    [Theory]
    [InlineData("Passed")]
    [InlineData("Pending")]
    [InlineData("Failed")]
    public async Task TryStartAutomaticallyAsync_ShouldAbandon_WhenReviewerIsStartedManuallyDuringCiCheck(string ciState)
    {
        ControllableCiSettleSource source = new();
        FakeLauncherService launcher = new();
        using ReviewCiSettleGate gate = new(new CiSettleWaiter(source, _ciClock), _ciClock);
        ReviewStartCoordinator coordinator = CreateCoordinator(launcher, CreateAutoStartSettings(), ciSettleGate: gate);
        ReviewEvent reviewEvent = CreateReviewEvent();
        Task<ReviewStartResult> automatic = coordinator.TryStartAutomaticallyAsync(reviewEvent);
        source.CallCount.Should().Be(1);

        ReviewStartResult manual = await coordinator.StartAsync(
            reviewEvent,
            LauncherRole.Reviewer,
            ReviewStartTrigger.Manual,
            _ => Task.FromResult(true));
        source.Complete(0, ScriptedCiSettleSource.Snapshot(ciState));
        ReviewStartResult result = await automatic;

        manual.Status.Should().Be(ReviewStartStatus.Started);
        result.Status.Should().Be(ReviewStartStatus.SkippedSuperseded);
        result.Launch.Should().BeNull();
        launcher.StartSessionCalls.Should().ContainSingle();
        _pendingQueue.Count.Should().Be(0);
        coordinator.IsWaitingForCiSettle(reviewEvent).Should().BeFalse();
        _logLines.Should().ContainSingle(line => line.Contains("同じ PR のレビューが起動されたため、自動起動を取りやめます", StringComparison.Ordinal));
    }

    // 自動起動どうしでも同じ。同じ PR の 2 つの評価が並行し、先に起動した側があれば、後の側は起動しない
    [Fact]
    public async Task TryStartAutomaticallyAsync_ShouldAbandon_WhenAnotherAutomaticEvaluationStartedTheSamePullRequest()
    {
        ControllableCiSettleSource source = new();
        FakeLauncherService launcher = new();
        using ReviewCiSettleGate gate = new(new CiSettleWaiter(source, _ciClock), _ciClock);
        ReviewStartCoordinator coordinator = CreateCoordinator(launcher, CreateAutoStartSettings(), ciSettleGate: gate);
        Task<ReviewStartResult> first = coordinator.TryStartAutomaticallyAsync(CreateReviewEvent());
        Task<ReviewStartResult> second = coordinator.TryStartAutomaticallyAsync(CreateReviewEvent("synchronized"));
        source.CallCount.Should().Be(2);

        source.Complete(0, ScriptedCiSettleSource.Snapshot("Passed"));
        ReviewStartResult firstResult = await first;
        source.Complete(1, ScriptedCiSettleSource.Snapshot("Passed"));
        ReviewStartResult secondResult = await second;

        firstResult.Status.Should().Be(ReviewStartStatus.Started);
        secondResult.Status.Should().Be(ReviewStartStatus.SkippedSuperseded);
        launcher.StartSessionCalls.Should().ContainSingle();
    }

    // 世代は PR ごと。別の PR の手動起動では、自動評価を取りやめない（取りやめると、その PR のイベントが失われる）
    [Fact]
    public async Task TryStartAutomaticallyAsync_ShouldContinue_WhenAnotherPullRequestIsStartedManuallyDuringCiCheck()
    {
        ControllableCiSettleSource source = new();
        FakeLauncherService launcher = new();
        using ReviewCiSettleGate gate = new(new CiSettleWaiter(source, _ciClock), _ciClock);
        ReviewStartCoordinator coordinator = CreateCoordinator(launcher, CreateAutoStartSettings(), ciSettleGate: gate);
        ReviewEvent automaticEvent = CreateReviewEvent();
        ReviewEvent otherEvent = new() { Repository = "owner/repo", PrNumber = 43, Reason = "opened" };
        Task<ReviewStartResult> automatic = coordinator.TryStartAutomaticallyAsync(automaticEvent);

        await coordinator.StartAsync(
            otherEvent,
            LauncherRole.Reviewer,
            ReviewStartTrigger.Manual,
            _ => Task.FromResult(true));
        source.Complete(0, ScriptedCiSettleSource.Snapshot("Passed"));
        ReviewStartResult result = await automatic;

        result.Status.Should().Be(ReviewStartStatus.Started);
        launcher.StartSessionCalls.Should().HaveCount(2);
    }

    // reviewed 側の起動は reviewer の世代を進めない
    [Fact]
    public async Task TryStartAutomaticallyAsync_ShouldContinue_WhenReviewedSideIsStartedDuringCiCheck()
    {
        ControllableCiSettleSource source = new();
        FakeLauncherService launcher = new();
        using ReviewCiSettleGate gate = new(new CiSettleWaiter(source, _ciClock), _ciClock);
        ReviewStartCoordinator coordinator = CreateCoordinator(launcher, CreateAutoStartSettings(), ciSettleGate: gate);
        ReviewEvent reviewEvent = CreateReviewEvent();
        Task<ReviewStartResult> automatic = coordinator.TryStartAutomaticallyAsync(reviewEvent);

        await coordinator.StartAsync(
            reviewEvent,
            LauncherRole.Reviewed,
            ReviewStartTrigger.Manual,
            _ => Task.FromResult(true));
        source.Complete(0, ScriptedCiSettleSource.Snapshot("Passed"));
        ReviewStartResult result = await automatic;

        result.Status.Should().Be(ReviewStartStatus.Started);
    }

    [Fact]
    public async Task TryStartAutomaticallyAsync_ShouldStartWithoutCiCheck_WhenNoGateIsConfigured()
    {
        FakeLauncherService launcher = new();
        ReviewStartCoordinator coordinator = CreateCoordinator(launcher, CreateAutoStartSettings());
        ReviewEvent reviewEvent = CreateReviewEvent();

        ReviewStartResult result = await coordinator.TryStartAutomaticallyAsync(reviewEvent);

        result.Status.Should().Be(ReviewStartStatus.Started);
        coordinator.IsWaitingForCiSettle(reviewEvent).Should().BeFalse();
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
        _autoPauseResumeScheduler.Dispose();
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

    [Theory]
    [InlineData("Reviewer")]
    [InlineData("Reviewed")]
    public async Task StartAsync_ShouldApplyPinDefaultToNewWindowWithoutChangingExistingWindow(string roleName)
    {
        SettingsService settings = CreateSettingsService();
        ReviewStartCoordinator coordinator = CreateCoordinator(new FakeLauncherService(), settings);
        LauncherRole role = Enum.Parse<LauncherRole>(roleName);
        ReviewStartResult first = await coordinator.StartAsync(CreateReviewEvent(), role, ReviewStartTrigger.Manual, _ => Task.FromResult(true));
        settings.UpdateLiveLogAlwaysOnTopEnabled(true);

        ReviewStartResult second = await coordinator.StartAsync(CreateReviewEvent(), role, ReviewStartTrigger.Manual, _ => Task.FromResult(true));

        first.Launch!.ViewModel.InitiallyAlwaysOnTop.Should().BeFalse();
        second.Launch!.ViewModel.InitiallyAlwaysOnTop.Should().BeTrue();
    }

    private SettingsService CreateSettingsService() => new(_workingDirectory, pnpmBinDir: string.Empty);

    private SettingsService CreateAutoStartSettings()
    {
        SettingsService settingsService = CreateSettingsService();
        settingsService.UpdateAutoReviewStartEnabled(true);
        return settingsService;
    }

    private ReviewCiSettleGate CreateCiSettleGate() => new(new CiSettleWaiter(_ciSource, _ciClock), _ciClock);

    private ReviewStartCoordinator CreateCoordinator(
        IReviewLauncherService launcherService,
        SettingsService? settingsService = null,
        Action<string>? onLogAppended = null,
        ReviewCiSettleGate? ciSettleGate = null,
        ReviewCycleCoordinator? reviewCycleCoordinator = null)
    {
        LoggingService loggingService = new(_workingDirectory);
        loggingService.LogAppended += (_, line) =>
        {
            _logLines.Add(line);
            onLogAppended?.Invoke(line);
        };
        return new ReviewStartCoordinator(
            launcherService,
            settingsService ?? CreateSettingsService(),
            new RateLimitSnapshotService(new RateLimitFileService(_workingDirectory)),
            new AutoPauseGate(),
            _pendingQueue,
            _autoPauseResumeScheduler,
            loggingService,
            reviewCycleCoordinator,
            ciSettleGate);
    }

    private ReviewCycleCoordinator CreateCycleCoordinator()
        => new(new ReviewCycleStore(Path.Combine(_workingDirectory, "cycles")), new LoggingService(_workingDirectory));

    private static List<ReviewHoldObservedEventArgs> CaptureHolds(ReviewCycleCoordinator cycle)
    {
        List<ReviewHoldObservedEventArgs> holds = [];
        cycle.HoldObserved += (_, args) => holds.Add(args);
        return holds;
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
        public event EventHandler? RunCompleted
        {
            add { }
            remove { }
        }

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
