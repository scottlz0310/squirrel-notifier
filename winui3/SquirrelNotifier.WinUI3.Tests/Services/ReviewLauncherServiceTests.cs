// <copyright file="ReviewLauncherServiceTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using SquirrelNotifier.WinUI3.Helpers;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public class ReviewLauncherServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SettingsService _settingsService;
    private readonly LoggingService _loggingService;

    public ReviewLauncherServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ReviewLauncherTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
        _settingsService = new SettingsService(_tempDir);
        _loggingService = new LoggingService(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    private Mock<IProcessInstance> CreateMockProcess(int exitCode, string stdout, string stderr, int delayMs = 0)
    {
        var mockProcess = new Mock<IProcessInstance>();
        mockProcess.SetupGet(p => p.ExitCode).Returns(exitCode);
        mockProcess.SetupGet(p => p.StandardOutput).Returns(new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(stdout))));
        mockProcess.SetupGet(p => p.StandardError).Returns(new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(stderr))));
        mockProcess.SetupGet(p => p.StandardInput).Returns(new StreamWriter(new MemoryStream()));

        if (delayMs > 0)
        {
            mockProcess.Setup(p => p.WaitForExitAsync(It.IsAny<CancellationToken>()))
                .Returns(async (CancellationToken t) => await Task.Delay(delayMs, t));
        }
        else
        {
            mockProcess.Setup(p => p.WaitForExitAsync(It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
        }

        return mockProcess;
    }

    private void ConfigureSettings(
        string reviewerCmd = "reviewer-cmd",
        string reviewerArgs = "--reviewer-arg",
        string reviewedCmd = "reviewed-cmd",
        string reviewedArgs = "--reviewed-arg",
        int timeoutMs = 10000)
    {
        _settingsService.UpdateSettings(
            "my-review-cmd", "--repo {owner}/{repo}",
            "http://localhost:3000", new[] { "queue://res" }, 30000,
            reviewerCmd, reviewerArgs,
            reviewedCmd, reviewedArgs,
            timeoutMs,
            "custom", "custom");

        string checkoutPath = Path.Combine(_tempDir, "checkouts", "squirrel-notifier");
        Directory.CreateDirectory(Path.Combine(checkoutPath, ".git"));
        _settingsService.UpdateRepositoryCheckoutMappings(new Dictionary<string, string>
        {
            ["scottlz0310/squirrel-notifier"] = checkoutPath,
        });
    }

    [Fact]
    public async Task LaunchAsync_ShouldRunSuccessfullyAndReturnExitCodeZero()
    {
        // Arrange
        var reviewEvent = new ReviewEvent
        {
            EventId = "test-event-1",
            Repository = "scottlz0310/squirrel-notifier",
            PrNumber = 52,
            PrUrl = "https://github.com/scottlz0310/squirrel-notifier/pull/52",
            Source = "queue://review/queue",
        };

        ConfigureSettings(reviewerCmd: "launcher-cmd", reviewerArgs: "--launcher-arg");

        Mock<IProcessInstance> mockProcess = CreateMockProcess(0, "Success Output", "");
        ProcessStartInfo? capturedPsi = null;

        var mockRunner = new Mock<IProcessRunner>();
        mockRunner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>()))
            .Callback<ProcessStartInfo>(psi => capturedPsi = psi)
            .Returns(mockProcess.Object);

        var service = new ReviewLauncherService(_settingsService, _loggingService, mockRunner.Object);

        // Act
        LauncherResult result = await service.LaunchAsync(reviewEvent, LauncherRole.Reviewer, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.ExitCode.Should().Be(0);
        result.Stdout.Should().Be("Success Output");
        result.Stderr.Should().BeEmpty();

        capturedPsi.Should().NotBeNull();
        capturedPsi!.FileName.Should().Contain("launcher-cmd");
        capturedPsi.ArgumentList.Should().Contain("--launcher-arg");
        capturedPsi.WorkingDirectory.Should().Be(Path.Combine(_tempDir, "launcher-workspace", "reviewer"));
        Directory.Exists(capturedPsi.WorkingDirectory).Should().BeTrue();
        mockProcess.Verify(p => p.Dispose(), Times.Once);
    }

    [Fact]
    public async Task LaunchAsync_ShouldPreventDoubleActivation()
    {
        // Arrange
        var reviewEvent = new ReviewEvent
        {
            EventId = "test-event-2",
            Repository = "scottlz0310/squirrel-notifier",
            PrNumber = 52,
            PrUrl = "https://github.com/scottlz0310/squirrel-notifier/pull/52",
            Source = "queue://review/queue",
        };

        ConfigureSettings();

        Mock<IProcessInstance> mockProcess = CreateMockProcess(0, "Success Output", "", delayMs: 1000);

        var mockRunner = new Mock<IProcessRunner>();
        mockRunner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>()))
            .Returns(mockProcess.Object);

        var service = new ReviewLauncherService(_settingsService, _loggingService, mockRunner.Object);

        // Act & Assert
        Task<LauncherResult> firstRunTask = service.LaunchAsync(reviewEvent, LauncherRole.Reviewer, CancellationToken.None);

        // Wait a small delay to make sure the first run has set _isRunning to true
        await Task.Delay(100);

        LauncherResult secondResult = await service.LaunchAsync(reviewEvent, LauncherRole.Reviewer, CancellationToken.None);
        secondResult.Success.Should().BeFalse();
        secondResult.ErrorMessage.Should().Contain("already running");

        LauncherResult firstResult = await firstRunTask;
        firstResult.Success.Should().BeTrue();
    }

    [Fact]
    public async Task LaunchAsync_ShouldSupportCancellation()
    {
        // Arrange
        var reviewEvent = new ReviewEvent
        {
            EventId = "test-event-3",
            Repository = "scottlz0310/squirrel-notifier",
            PrNumber = 52,
            PrUrl = "https://github.com/scottlz0310/squirrel-notifier/pull/52",
            Source = "queue://review/queue",
        };

        ConfigureSettings();

        // Delay mock process to simulate execution time
        Mock<IProcessInstance> mockProcess = CreateMockProcess(0, "Pending...", "", delayMs: 5000);
        var processStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var mockRunner = new Mock<IProcessRunner>();
        mockRunner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>()))
            .Callback(() => processStarted.SetResult())
            .Returns(mockProcess.Object);

        var service = new ReviewLauncherService(_settingsService, _loggingService, mockRunner.Object);
        using var cts = new CancellationTokenSource();

        // Act
        Task<LauncherResult> launchTask = service.LaunchAsync(reviewEvent, LauncherRole.Reviewer, cts.Token);

        await processStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();

        LauncherResult result = await launchTask;

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("cancelled by user");
        mockProcess.Verify(p => p.Kill(true), Times.Once);
    }

    [Fact]
    public async Task LaunchAsync_ShouldTimeoutProcess()
    {
        // Arrange
        var reviewEvent = new ReviewEvent
        {
            EventId = "test-event-4",
            Repository = "scottlz0310/squirrel-notifier",
            PrNumber = 52,
            PrUrl = "https://github.com/scottlz0310/squirrel-notifier/pull/52",
            Source = "queue://review/queue",
        };

        ConfigureSettings(timeoutMs: 200);

        // Process takes 5000ms
        Mock<IProcessInstance> mockProcess = CreateMockProcess(0, "Slow...", "", delayMs: 5000);

        var mockRunner = new Mock<IProcessRunner>();
        mockRunner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>()))
            .Returns(mockProcess.Object);

        var service = new ReviewLauncherService(_settingsService, _loggingService, mockRunner.Object);

        // Act
        LauncherResult result = await service.LaunchAsync(reviewEvent, LauncherRole.Reviewer, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("timed out");
        mockProcess.Verify(p => p.Kill(true), Times.Once);
    }

    [Theory]
    [InlineData("reviewer", "reviewer-cmd")]
    [InlineData("reviewed", "reviewed-cmd")]
    public async Task LaunchAsync_ShouldSelectCorrectSlotByRole(
        string roleName, string expectedCmd)
    {
        // Arrange
        LauncherRole role = roleName == "reviewer" ? LauncherRole.Reviewer : LauncherRole.Reviewed;
        var reviewEvent = new ReviewEvent
        {
            EventId = "test-slot",
            Repository = "scottlz0310/squirrel-notifier",
            PrNumber = 52,
            PrUrl = "https://github.com/scottlz0310/squirrel-notifier/pull/52",
            Source = "queue://review/queue",
        };

        ConfigureSettings(
            reviewerCmd: "reviewer-cmd", reviewerArgs: "--reviewer-arg",
            reviewedCmd: "reviewed-cmd", reviewedArgs: "--reviewed-arg");

        Mock<IProcessInstance> mockProcess = CreateMockProcess(0, string.Empty, string.Empty);
        ProcessStartInfo? capturedPsi = null;

        var mockRunner = new Mock<IProcessRunner>();
        mockRunner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>()))
            .Callback<ProcessStartInfo>(psi => capturedPsi = psi)
            .Returns(mockProcess.Object);

        var service = new ReviewLauncherService(_settingsService, _loggingService, mockRunner.Object);

        // Act
        await service.LaunchAsync(reviewEvent, role, CancellationToken.None);

        // Assert
        capturedPsi.Should().NotBeNull();
        capturedPsi!.FileName.Should().Contain(expectedCmd);
        string expectedWorkingDirectory = role == LauncherRole.Reviewer
            ? Path.Combine(_tempDir, "launcher-workspace", "reviewer")
            : Path.Combine(_tempDir, "checkouts", "squirrel-notifier");
        capturedPsi.WorkingDirectory.Should().Be(expectedWorkingDirectory);
    }

    [Fact]
    public async Task LaunchAsync_ShouldFailBeforeStartingProcess_WhenReviewedCheckoutMappingIsMissing()
    {
        ReviewEvent reviewEvent = CreateReviewEvent("missing-checkout");
        ConfigureSettings();
        _settingsService.UpdateRepositoryCheckoutMappings(new Dictionary<string, string>());
        var mockRunner = new Mock<IProcessRunner>();
        var service = new ReviewLauncherService(_settingsService, _loggingService, mockRunner.Object);

        LauncherResult result = await service.LaunchAsync(reviewEvent, LauncherRole.Reviewed, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("checkout mapping");
        mockRunner.Verify(r => r.Start(It.IsAny<ProcessStartInfo>()), Times.Never);
    }

    [Fact]
    public async Task LaunchAsync_ShouldPersistMaskedFailureDiagnostics()
    {
        const string secret = "diagnostic-secret-value";
        ReviewEvent reviewEvent = CreateReviewEvent("failed-diagnostics");
        ConfigureSettings();
        Mock<IProcessInstance> mockProcess = CreateMockProcess(17, string.Empty, $"first failure {secret}\nsecond failure");
        var mockRunner = new Mock<IProcessRunner>();
        mockRunner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>())).Returns(mockProcess.Object);
        var service = new ReviewLauncherService(
            _settingsService,
            _loggingService,
            mockRunner.Object,
            secretMasker: new SecretMasker([secret]));

        LauncherResult result = await service.LaunchAsync(reviewEvent, LauncherRole.Reviewer, CancellationToken.None);
        string log = await File.ReadAllTextAsync(Path.Combine(_tempDir, "winui3.log"));

        result.Success.Should().BeFalse();
        log.Should().Contain("ExitCode=17");
        log.Should().Contain($"WorkingDirectory={Path.Combine(_tempDir, "launcher-workspace", "reviewer")}");
        log.Should().Contain("ResolvedExecutable=");
        log.Should().Contain("ExecutableKind=");
        log.Should().Contain("first failure *** | second failure");
        log.Should().NotContain(secret);
    }

    [Theory]
    [InlineData("reviewer", "reviewer-cmd", "--reviewer-arg")]
    [InlineData("reviewed", "reviewed-cmd", "--reviewed-arg")]
    public void BuildCommandLine_ShouldSelectCorrectSlotByRole(
        string roleName, string expectedCmd, string expectedArg)
    {
        // Arrange
        LauncherRole role = roleName == "reviewer" ? LauncherRole.Reviewer : LauncherRole.Reviewed;
        var reviewEvent = new ReviewEvent
        {
            EventId = "test-copy",
            Repository = "scottlz0310/squirrel-notifier",
            PrNumber = 52,
            PrUrl = "https://github.com/scottlz0310/squirrel-notifier/pull/52",
            Source = "queue://review/queue",
        };

        ConfigureSettings(
            reviewerCmd: "reviewer-cmd", reviewerArgs: "--reviewer-arg",
            reviewedCmd: "reviewed-cmd", reviewedArgs: "--reviewed-arg");

        var mockRunner = new Mock<IProcessRunner>();
        var service = new ReviewLauncherService(_settingsService, _loggingService, mockRunner.Object);

        // Act
        string commandLine = service.BuildCommandLine(reviewEvent, role);

        // Assert
        commandLine.Should().Be($"{expectedCmd} {expectedArg}");
        mockRunner.Verify(r => r.Start(It.IsAny<ProcessStartInfo>()), Times.Never);
    }

    [Fact]
    public void BuildCommandLine_ShouldExpandPlaceholdersAndQuoteArgumentsWithSpaces()
    {
        // Arrange: 既定のテンプレートは -p "<prompt>" 形式で、prompt にプレースホルダーを含む
        var reviewEvent = new ReviewEvent
        {
            EventId = "test-copy-2",
            Repository = "scottlz0310/squirrel-notifier",
            PrNumber = 123,
            PrUrl = "https://github.com/scottlz0310/squirrel-notifier/pull/123",
            Source = "queue://review/queue",
            Reason = "opened",
        };

        ConfigureSettings(
            reviewerCmd: "claude",
            reviewerArgs: "-p \"/thread-owl-pr-reviewer {owner}/{repo}#{prNumber} を {reason} モードでレビューしてください\" --verbose --output-format stream-json");

        var mockRunner = new Mock<IProcessRunner>();
        var service = new ReviewLauncherService(_settingsService, _loggingService, mockRunner.Object);

        // Act
        string commandLine = service.BuildCommandLine(reviewEvent, LauncherRole.Reviewer);

        // Assert
        commandLine.Should().Be("claude -p \"/thread-owl-pr-reviewer scottlz0310/squirrel-notifier#123 を opened モードでレビューしてください\" --verbose --output-format stream-json");
    }

    // ---- #143: 実行イベントのストリーミング ----

    // イベント時刻の DI を検証するための固定時刻プロバイダー
    private sealed class FixedTimeProvider : TimeProvider
    {
        public static readonly DateTimeOffset FixedNow = new(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => FixedNow;
    }

    private static ReviewEvent CreateReviewEvent(string eventId = "test-stream") => new()
    {
        EventId = eventId,
        Repository = "scottlz0310/squirrel-notifier",
        PrNumber = 52,
        PrUrl = "https://github.com/scottlz0310/squirrel-notifier/pull/52",
        Source = "queue://review/queue",
    };

    // stdout を実行中に逐次供給できる mock process。実プロセス同様、Kill でパイプが閉じて
    // 読み取り側が EOF / IOException で終了する挙動を再現する.
    private Mock<IProcessInstance> CreatePipeMockProcess(
        out StreamWriter stdoutWriter,
        out TaskCompletionSource exitTcs,
        TaskCompletionSource? processStarted = null)
    {
        var server = new AnonymousPipeServerStream(PipeDirection.Out);
        var client = new AnonymousPipeClientStream(PipeDirection.In, server.ClientSafePipeHandle);
        stdoutWriter = new StreamWriter(server, new UTF8Encoding(false)) { AutoFlush = true };

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        exitTcs = tcs;

        var mockProcess = new Mock<IProcessInstance>();
        mockProcess.SetupGet(p => p.ExitCode).Returns(0);
        mockProcess.SetupGet(p => p.StandardOutput).Returns(new StreamReader(client));
        mockProcess.SetupGet(p => p.StandardError).Returns(new StreamReader(new MemoryStream()));
        mockProcess.SetupGet(p => p.StandardInput).Returns(new StreamWriter(new MemoryStream()));
        mockProcess.Setup(p => p.WaitForExitAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken t) =>
            {
                processStarted?.TrySetResult();
                return tcs.Task.WaitAsync(t);
            });
        mockProcess.Setup(p => p.Kill(It.IsAny<bool>())).Callback(server.Dispose);
        return mockProcess;
    }

    [Fact]
    public async Task StartSession_ShouldStreamStdoutAndProgressBeforeProcessExit()
    {
        // Arrange
        const string progressLine = "@squirrel-progress {\"schemaVersion\":1,\"phaseIndex\":3,\"totalPhases\":8,\"phaseLabel\":\"修正\",\"message\":\"accept 2件\"}";
        const string malformedLine = "@squirrel-progress {broken";

        ConfigureSettings();
        Mock<IProcessInstance> mockProcess = CreatePipeMockProcess(out StreamWriter stdout, out TaskCompletionSource exitTcs);

        var mockRunner = new Mock<IProcessRunner>();
        mockRunner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>())).Returns(mockProcess.Object);

        var service = new ReviewLauncherService(_settingsService, _loggingService, mockRunner.Object, new FixedTimeProvider());

        // Act
        AgentExecutionSession session = service.StartSession(CreateReviewEvent(), LauncherRole.Reviewer, CancellationToken.None);

        using var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using IAsyncEnumerator<AgentExecutionEvent> events = session.ReadEventsAsync(readTimeout.Token).GetAsyncEnumerator();

        // Assert: 通常ログはプロセス終了前に届く（#143 AC）
        await stdout.WriteLineAsync("plain log line");
        (await events.MoveNextAsync()).Should().BeTrue();
        events.Current.Kind.Should().Be(AgentExecutionEventKind.Stdout);
        events.Current.Text.Should().Be("plain log line");
        events.Current.Timestamp.Should().Be(FixedTimeProvider.FixedNow);
        session.Completion.IsCompleted.Should().BeFalse("プロセス終了前に stdout が購読側へ届くこと");

        // 構造化 progress event は型付きモデルへ変換される
        await stdout.WriteLineAsync(progressLine);
        (await events.MoveNextAsync()).Should().BeTrue();
        events.Current.Kind.Should().Be(AgentExecutionEventKind.Progress);
        events.Current.Progress.Should().NotBeNull();
        events.Current.Progress!.PhaseIndex.Should().Be(3);
        events.Current.Progress.TotalPhases.Should().Be(8);
        events.Current.Progress.PhaseLabel.Should().Be("修正");

        // malformed な progress 行は通常ログとして流れ、実行は失敗しない
        await stdout.WriteLineAsync(malformedLine);
        (await events.MoveNextAsync()).Should().BeTrue();
        events.Current.Kind.Should().Be(AgentExecutionEventKind.Stdout);
        events.Current.Text.Should().Be(malformedLine);

        // EOF + プロセス終了 → terminal event で列挙が終端する
        stdout.Dispose();
        exitTcs.SetResult();

        (await events.MoveNextAsync()).Should().BeTrue();
        events.Current.Kind.Should().Be(AgentExecutionEventKind.Completed);
        events.Current.Outcome.Should().Be(AgentExecutionOutcome.Succeeded);
        events.Current.Result.Should().NotBeNull();
        events.Current.Result!.ExitCode.Should().Be(0);
        (await events.MoveNextAsync()).Should().BeFalse("terminal event の後にイベントは無い");

        // LauncherResult の集約 stdout は progress 行も含めた全行を保持する（既存セマンティクス維持）
        LauncherResult result = await session.Completion;
        result.Success.Should().BeTrue();
        result.Stdout.Should().Be($"plain log line\n{progressLine}\n{malformedLine}");
    }

    [Fact]
    public async Task StartSession_ShouldExtractProgressFromClaudeStreamJsonEvents()
    {
        // Arrange: claude -p --verbose --output-format stream-json の stdout を模す（#187）
        const string initEvent = """{"type":"system","subtype":"init","session_id":"s1","tools":["Bash"]}""";
        const string assistantTextEvent = """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"レビューを開始します。"}]},"session_id":"s1"}""";
        const string toolResultEvent = """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_01","content":[{"type":"text","text":"@squirrel-progress {\"schemaVersion\":1,\"phaseIndex\":2,\"totalPhases\":8,\"phaseLabel\":\"分類\"}"}]}]},"session_id":"s1"}""";
        const string unknownEvent = """{"type":"unknown_future_event","data":"x"}""";

        ConfigureSettings();
        Mock<IProcessInstance> mockProcess = CreatePipeMockProcess(out StreamWriter stdout, out TaskCompletionSource exitTcs);

        var mockRunner = new Mock<IProcessRunner>();
        mockRunner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>())).Returns(mockProcess.Object);

        var service = new ReviewLauncherService(_settingsService, _loggingService, mockRunner.Object, new FixedTimeProvider());

        // Act
        AgentExecutionSession session = service.StartSession(CreateReviewEvent(), LauncherRole.Reviewer, CancellationToken.None);

        using var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using IAsyncEnumerator<AgentExecutionEvent> events = session.ReadEventsAsync(readTimeout.Token).GetAsyncEnumerator();

        // Assert: init イベントは抑制され、assistant テキストが通常ログとして届く
        await stdout.WriteLineAsync(initEvent);
        await stdout.WriteLineAsync(assistantTextEvent);
        (await events.MoveNextAsync()).Should().BeTrue();
        events.Current.Kind.Should().Be(AgentExecutionEventKind.Stdout);
        events.Current.Text.Should().Be("レビューを開始します。");
        session.Completion.IsCompleted.Should().BeFalse("プロセス終了前にログが購読側へ届くこと");

        // tool_result 内のマーカーはプロセス終了前に progress event として届く（#187 AC）
        await stdout.WriteLineAsync(toolResultEvent);
        (await events.MoveNextAsync()).Should().BeTrue();
        events.Current.Kind.Should().Be(AgentExecutionEventKind.Progress);
        events.Current.Progress!.PhaseIndex.Should().Be(2);
        events.Current.Progress.PhaseLabel.Should().Be("分類");

        // 未知 type の JSON 行は生の行のまま通常ログとして流れ、実行は失敗しない
        await stdout.WriteLineAsync(unknownEvent);
        (await events.MoveNextAsync()).Should().BeTrue();
        events.Current.Kind.Should().Be(AgentExecutionEventKind.Stdout);
        events.Current.Text.Should().Be(unknownEvent);

        stdout.Dispose();
        exitTcs.SetResult();

        (await events.MoveNextAsync()).Should().BeTrue();
        events.Current.Kind.Should().Be(AgentExecutionEventKind.Completed);
        events.Current.Outcome.Should().Be(AgentExecutionOutcome.Succeeded);
    }

    [Fact]
    public async Task StartSession_ShouldEmitCancelledTerminalEvent_WhenCancelled()
    {
        // Arrange: Kill のコールバックがパイプを閉じるため、writer 側の後始末は不要
        ConfigureSettings();
        var processStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Mock<IProcessInstance> mockProcess = CreatePipeMockProcess(out _, out _, processStarted);

        var mockRunner = new Mock<IProcessRunner>();
        mockRunner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>())).Returns(mockProcess.Object);

        var service = new ReviewLauncherService(_settingsService, _loggingService, mockRunner.Object);
        using var cts = new CancellationTokenSource();

        // Act
        AgentExecutionSession session = service.StartSession(CreateReviewEvent("test-stream-cancel"), LauncherRole.Reviewer, cts.Token);
        await processStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();

        LauncherResult result = await session.Completion;

        // Assert: キャンセルは terminal event（Cancelled）として通知され、reader task も残存しない
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("cancelled by user");
        mockProcess.Verify(p => p.Kill(true), Times.Once);

        var events = new List<AgentExecutionEvent>();
        await foreach (AgentExecutionEvent e in session.ReadEventsAsync())
        {
            events.Add(e);
        }

        events.Should().ContainSingle(e => e.Kind == AgentExecutionEventKind.Completed)
            .Which.Outcome.Should().Be(AgentExecutionOutcome.Cancelled);
    }

    [Fact]
    public async Task StartSession_ShouldEmitCancelledTerminalEvent_WhenCancelMethodInvoked()
    {
        // Arrange: Cancel() は内部 CTS のみを cancel し、呼び出し元トークンは cancel されない経路。
        // timeout（TimedOut）に誤分類されないことを固定する（#143 レビュー対応）
        ConfigureSettings();
        var processStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Mock<IProcessInstance> mockProcess = CreatePipeMockProcess(out _, out _, processStarted);

        var mockRunner = new Mock<IProcessRunner>();
        mockRunner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>())).Returns(mockProcess.Object);

        var service = new ReviewLauncherService(_settingsService, _loggingService, mockRunner.Object);

        // Act
        AgentExecutionSession session = service.StartSession(CreateReviewEvent("test-stream-cancel-method"), LauncherRole.Reviewer, CancellationToken.None);
        await processStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        service.Cancel();

        LauncherResult result = await session.Completion;

        // Assert: Cancel() 自身と OCE ハンドラの両方が Kill を呼ぶため AtLeastOnce で検証
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("cancelled by user");
        mockProcess.Verify(p => p.Kill(true), Times.AtLeastOnce);

        var events = new List<AgentExecutionEvent>();
        await foreach (AgentExecutionEvent e in session.ReadEventsAsync())
        {
            events.Add(e);
        }

        events.Should().ContainSingle(e => e.Kind == AgentExecutionEventKind.Completed)
            .Which.Outcome.Should().Be(AgentExecutionOutcome.Cancelled);
    }

    [Fact]
    public async Task StartSession_ShouldNotStartProcess_WhenCancelledImmediatelyAfterStartSession()
    {
        // Arrange: StartSession 直後（fire-and-forget の RunSessionAsync がプロセスを起動する前）に
        // Cancel() を呼ぶ開始前レース。CTS は StartSession 内で生成済みのため cancel が
        // combinedToken に届き、プロセスは起動されず Cancelled で終端する（#143 レビュー対応）
        ConfigureSettings();
        Mock<IProcessInstance> mockProcess = CreateMockProcess(0, "should not run", "");

        var mockRunner = new Mock<IProcessRunner>();
        mockRunner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>())).Returns(mockProcess.Object);

        var service = new ReviewLauncherService(_settingsService, _loggingService, mockRunner.Object);

        // Act: StartSession が戻った直後に同期的に Cancel する
        AgentExecutionSession session = service.StartSession(CreateReviewEvent("test-stream-cancel-before-start"), LauncherRole.Reviewer, CancellationToken.None);
        service.Cancel();

        LauncherResult result = await session.Completion;

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("cancelled by user");
        mockRunner.Verify(r => r.Start(It.IsAny<ProcessStartInfo>()), Times.Never);

        var events = new List<AgentExecutionEvent>();
        await foreach (AgentExecutionEvent e in session.ReadEventsAsync())
        {
            events.Add(e);
        }

        events.Should().ContainSingle(e => e.Kind == AgentExecutionEventKind.Completed)
            .Which.Outcome.Should().Be(AgentExecutionOutcome.Cancelled);
    }

    [Fact]
    public async Task StartSession_ShouldEmitTimedOutTerminalEvent_WhenTimedOut()
    {
        // Arrange
        ConfigureSettings(timeoutMs: 200);
        Mock<IProcessInstance> mockProcess = CreateMockProcess(0, "Slow...", "", delayMs: 5000);

        var mockRunner = new Mock<IProcessRunner>();
        mockRunner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>())).Returns(mockProcess.Object);

        var service = new ReviewLauncherService(_settingsService, _loggingService, mockRunner.Object);

        // Act
        AgentExecutionSession session = service.StartSession(CreateReviewEvent("test-stream-timeout"), LauncherRole.Reviewer, CancellationToken.None);
        LauncherResult result = await session.Completion;

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("timed out");

        var events = new List<AgentExecutionEvent>();
        await foreach (AgentExecutionEvent e in session.ReadEventsAsync())
        {
            events.Add(e);
        }

        events.Should().ContainSingle(e => e.Kind == AgentExecutionEventKind.Completed)
            .Which.Outcome.Should().Be(AgentExecutionOutcome.TimedOut);
    }

    [Fact]
    public async Task StartSession_ShouldReturnFailedSession_WhenAlreadyRunning()
    {
        // Arrange
        ConfigureSettings();
        Mock<IProcessInstance> mockProcess = CreateMockProcess(0, "Running...", "", delayMs: 1000);

        var mockRunner = new Mock<IProcessRunner>();
        mockRunner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>())).Returns(mockProcess.Object);

        var service = new ReviewLauncherService(_settingsService, _loggingService, mockRunner.Object);

        // Act: 1 本目の実行中に 2 本目のセッションを開始する
        Task<LauncherResult> firstRun = service.LaunchAsync(CreateReviewEvent("test-stream-busy-1"), LauncherRole.Reviewer, CancellationToken.None);
        await Task.Delay(100);
        AgentExecutionSession second = service.StartSession(CreateReviewEvent("test-stream-busy-2"), LauncherRole.Reviewer, CancellationToken.None);

        // Assert: 同時実行抑止は即座に Failed の terminal event を持つセッションになる
        LauncherResult secondResult = await second.Completion;
        secondResult.Success.Should().BeFalse();
        secondResult.ErrorMessage.Should().Contain("already running");

        var events = new List<AgentExecutionEvent>();
        await foreach (AgentExecutionEvent e in second.ReadEventsAsync())
        {
            events.Add(e);
        }

        events.Should().ContainSingle().Which.Outcome.Should().Be(AgentExecutionOutcome.Failed);

        (await firstRun).Success.Should().BeTrue();
    }
}
