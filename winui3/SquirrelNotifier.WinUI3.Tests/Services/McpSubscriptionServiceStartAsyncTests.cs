// <copyright file="McpSubscriptionServiceStartAsyncTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Services;

/// <summary>
/// 購読開始を await できる API（#208）の検証.
/// </summary>
public class McpSubscriptionServiceStartAsyncTests : IDisposable
{
    private readonly string _settingsDirectory;
    private readonly SettingsService _settingsService;
    private readonly INotificationService _notificationService;
    private readonly LoggingService _loggingService;

    public McpSubscriptionServiceStartAsyncTests()
    {
        _settingsDirectory = Path.Combine(Path.GetTempPath(), $"McpStartAsyncTests_{Guid.NewGuid()}");
        _settingsService = new SettingsService(_settingsDirectory);
        _notificationService = new Mock<INotificationService>().Object;
        _loggingService = new LoggingService(_settingsDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_settingsDirectory))
        {
            Directory.Delete(_settingsDirectory, true);
        }

        GC.SuppressFinalize(this);
    }

    private static IProcessInstance CreateMockProcess(int exitCode, string stdout, string stderr)
    {
        var mock = new Mock<IProcessInstance>();
        mock.SetupGet(p => p.ExitCode).Returns(exitCode);
        mock.SetupGet(p => p.StandardOutput).Returns(new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(stdout))));
        mock.SetupGet(p => p.StandardError).Returns(new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(stderr))));
        mock.Setup(p => p.WaitForExitAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        return mock.Object;
    }

    // 購読プロセスは通知を待ち続ける（＝Running のまま留まる）ふるまいを模す
    private static IProcessInstance CreateNeverReturningProcess()
    {
        var mock = new Mock<IProcessInstance>();
        mock.SetupGet(p => p.ExitCode).Returns(0);
        mock.SetupGet(p => p.StandardOutput).Returns(new StreamReader(new MemoryStream()));
        mock.SetupGet(p => p.StandardError).Returns(new StreamReader(new MemoryStream()));
        mock.Setup(p => p.WaitForExitAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken t) => Task.Delay(Timeout.Infinite, t));
        return mock.Object;
    }

    // キャンセルでは終了せず、テストが明示的に完了させるまでブロックするプロセス。
    // StopAsync は購読ループの終了を待つため、Stopping 状態を競合なく再現できる
    private static IProcessInstance CreateManuallyExitedProcess(TaskCompletionSource exitSignal)
    {
        var mock = new Mock<IProcessInstance>();
        mock.SetupGet(p => p.ExitCode).Returns(0);
        mock.SetupGet(p => p.StandardOutput).Returns(new StreamReader(new MemoryStream()));
        mock.SetupGet(p => p.StandardError).Returns(new StreamReader(new MemoryStream()));
        mock.Setup(p => p.WaitForExitAsync(It.IsAny<CancellationToken>())).Returns(exitSignal.Task);
        return mock.Object;
    }

    private McpSubscriptionService CreateService(Mock<IProcessRunner> runner, int startTimeoutMs = 5000)
        => new(
            _settingsService,
            _notificationService,
            _loggingService,
            runner.Object,
            maxRetries: 0,
            startTimeoutMs: startTimeoutMs);

    [Fact]
    public async Task StartAsync_ShouldReturnStarted_WhenSubscriptionReachesRunning()
    {
        var runner = new Mock<IProcessRunner>();
        runner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>()))
            .Returns<ProcessStartInfo>(psi => psi.ArgumentList.Contains("--help")
                ? CreateMockProcess(0, "help", string.Empty)
                : CreateNeverReturningProcess());

        await using McpSubscriptionService service = CreateService(runner);

        SubscriptionStartResult result = await service.StartAsync(CancellationToken.None);

        result.Outcome.Should().Be(SubscriptionStartOutcome.Started);
        result.Success.Should().BeTrue();
        service.State.Should().Be(SubscriptionState.Running);
    }

    [Fact]
    public async Task StartAsync_ShouldReturnFailedWithReason_WhenPreflightFails()
    {
        var runner = new Mock<IProcessRunner>();
        runner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>()))
            .Returns(CreateMockProcess(1, string.Empty, "gateway url is invalid"));

        await using McpSubscriptionService service = CreateService(runner);

        SubscriptionStartResult result = await service.StartAsync(CancellationToken.None);

        result.Outcome.Should().Be(SubscriptionStartOutcome.Failed);
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("gateway url is invalid");
        service.State.Should().Be(SubscriptionState.Error);
    }

    [Fact]
    public async Task StartAsync_ShouldSucceed_AndSurfaceAuthenticationRequirementOnTheServiceAfterwards()
    {
        // 購読開始の可否は preflight（--help）だけで決まり、gateway への接続は Running 到達後の
        // 購読プロセスが初めて行う。したがって認証要求は StartAsync の結果ではなく、
        // その後 Error へ落ちた時点の IsAuthenticationRequired に現れる（#208 の契約）。
        string authFailureJson = "{\"route\":\"failed\",\"errorCode\":\"AUTH_LOGIN_REQUIRED\"}";
        var runner = new Mock<IProcessRunner>();
        runner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>()))
            .Returns<ProcessStartInfo>(psi => psi.ArgumentList.Contains("--help")
                ? CreateMockProcess(0, "help", string.Empty)
                : CreateMockProcess(1, authFailureJson, string.Empty));

        await using McpSubscriptionService service = CreateService(runner);

        SubscriptionStartResult started = await service.StartAsync(CancellationToken.None);

        started.Outcome.Should().Be(SubscriptionStartOutcome.Started);

        await WaitForStateAsync(service, SubscriptionState.Error);

        service.IsAuthenticationRequired.Should().BeTrue();
        service.LastError.Should().Contain("認証");
    }

    [Fact]
    public async Task StartAsync_ShouldReturnTimedOut_WhenStateNeverSettles()
    {
        // preflight が返らない → Starting のまま確定しない
        var runner = new Mock<IProcessRunner>();
        runner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>())).Returns(CreateNeverReturningProcess());

        await using McpSubscriptionService service = CreateService(runner, startTimeoutMs: 300);

        SubscriptionStartResult result = await service.StartAsync(CancellationToken.None);

        result.Outcome.Should().Be(SubscriptionStartOutcome.TimedOut);
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task StartAsync_ShouldReturnCancelled_WhenCallerCancels()
    {
        var runner = new Mock<IProcessRunner>();
        runner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>())).Returns(CreateNeverReturningProcess());

        await using McpSubscriptionService service = CreateService(runner, startTimeoutMs: 30000);
        using var cts = new CancellationTokenSource();

        Task<SubscriptionStartResult> task = service.StartAsync(cts.Token);
        await cts.CancelAsync();
        SubscriptionStartResult result = await task;

        result.Outcome.Should().Be(SubscriptionStartOutcome.Cancelled);
    }

    [Fact]
    public async Task StartAsync_ShouldReturnImmediately_WhenAlreadyRunning()
    {
        // 購読ループの開始回数は preflight（--help）の起動回数で数える。State = Running は
        // 購読プロセスの起動より先に設定されるため、購読プロセス数で数えると競合する
        int preflightStarts = 0;
        var runner = new Mock<IProcessRunner>();
        runner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>()))
            .Returns<ProcessStartInfo>(psi =>
            {
                if (psi.ArgumentList.Contains("--help"))
                {
                    Interlocked.Increment(ref preflightStarts);
                    return CreateMockProcess(0, "help", string.Empty);
                }

                return CreateNeverReturningProcess();
            });

        await using McpSubscriptionService service = CreateService(runner);

        await service.StartAsync(CancellationToken.None);

        SubscriptionStartResult second = await service.StartAsync(CancellationToken.None);

        second.Outcome.Should().Be(SubscriptionStartOutcome.Started);
        preflightStarts.Should().Be(1, because: "Running 中の StartAsync は新しい購読ループを開始しない");
    }

    [Fact]
    public async Task StartAsync_ShouldNotStartTwice_WhenCalledConcurrently()
    {
        int preflightStarts = 0;
        var runner = new Mock<IProcessRunner>();
        runner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>()))
            .Returns<ProcessStartInfo>(psi =>
            {
                if (psi.ArgumentList.Contains("--help"))
                {
                    Interlocked.Increment(ref preflightStarts);
                    return CreateMockProcess(0, "help", string.Empty);
                }

                return CreateNeverReturningProcess();
            });

        await using McpSubscriptionService service = CreateService(runner);

        SubscriptionStartResult[] results = await Task.WhenAll(
            service.StartAsync(CancellationToken.None),
            service.StartAsync(CancellationToken.None),
            service.StartAsync(CancellationToken.None));

        results.Should().OnlyContain(r => r.Outcome == SubscriptionStartOutcome.Started);
        preflightStarts.Should().Be(1, because: "Starting 中の多重呼び出しは進行中の開始結果を待つ");
    }

    [Fact]
    public async Task StartAsync_ShouldReturnFailedWithoutHanging_WhenSubscriptionIsStoppedWhileStarting()
    {
        var runner = new Mock<IProcessRunner>();
        runner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>())).Returns(CreateNeverReturningProcess());

        // タイムアウトより十分に短い時間で確定することを確かめる（停止で待ちが解けずに
        // タイムアウトまで待たされると、呼び出し側の UI が固まる）
        await using McpSubscriptionService service = CreateService(runner, startTimeoutMs: 30000);

        Task<SubscriptionStartResult> startTask = service.StartAsync(CancellationToken.None);
        await WaitForStateAsync(service, SubscriptionState.Starting);
        await service.StopAsync();

        SubscriptionStartResult result = await startTask.WaitAsync(TimeSpan.FromSeconds(10));

        result.Outcome.Should().Be(SubscriptionStartOutcome.Failed);
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task StartAsync_ShouldNotStartNewLoop_WhileStopping()
    {
        int preflightStarts = 0;
        var subscribeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exitSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var runner = new Mock<IProcessRunner>();
        runner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>()))
            .Returns<ProcessStartInfo>(psi =>
            {
                if (psi.ArgumentList.Contains("--help"))
                {
                    Interlocked.Increment(ref preflightStarts);
                    return CreateMockProcess(0, "help", string.Empty);
                }

                _ = subscribeStarted.TrySetResult();
                return CreateManuallyExitedProcess(exitSignal);
            });

        await using McpSubscriptionService service = CreateService(runner, startTimeoutMs: 30000);
        await service.StartAsync(CancellationToken.None);

        // 購読プロセスが起動するまで待つ。State = Running はその手前で設定されるため、
        // ここを待たないと StopAsync が待機すべきループを掴めず Stopping に留まらない
        await subscribeStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // 購読プロセスがキャンセルで終了しないため、StopAsync はループ終了待ちで止まる
        Task stopTask = service.StopAsync();
        service.State.Should().Be(SubscriptionState.Stopping);

        SubscriptionStartResult result = await service.StartAsync(CancellationToken.None);

        result.Outcome.Should().Be(SubscriptionStartOutcome.Failed);
        result.ErrorMessage.Should().Contain("停止処理中");
        preflightStarts.Should().Be(1, because: "Stopping 中は新しい購読ループを開始しない");

        _ = exitSignal.TrySetResult();
        await stopTask.WaitAsync(TimeSpan.FromSeconds(10));

        service.State.Should().Be(SubscriptionState.Stopped);
    }

    [Fact]
    public async Task Start_ShouldBeRejected_WhenRequestedAtTheExactMomentStoppingIsSignalled()
    {
        // StopAsync が Stopping を設定した「まさにその瞬間」に開始を割り込ませ、
        // 判定と生成の間に停止が入り込む interleaving を決定的に再現する。
        // StateChanged は状態遷移と同じ排他区間で発火するため、このハンドラは
        // 「Stopping 設定直後・後続処理の前」という critical point で実行される。
        int preflightStarts = 0;
        var subscribeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exitSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var runner = new Mock<IProcessRunner>();
        runner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>()))
            .Returns<ProcessStartInfo>(psi =>
            {
                if (psi.ArgumentList.Contains("--help"))
                {
                    Interlocked.Increment(ref preflightStarts);
                    return CreateMockProcess(0, "help", string.Empty);
                }

                _ = subscribeStarted.TrySetResult();
                return CreateManuallyExitedProcess(exitSignal);
            });

        await using McpSubscriptionService service = CreateService(runner, startTimeoutMs: 30000);
        await service.StartAsync(CancellationToken.None);
        await subscribeStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        int interleavedStartAttempts = 0;
        SubscriptionState stateAfterInterleavedStart = default;

        void OnStateChanged(object? sender, SubscriptionState state)
        {
            if (state != SubscriptionState.Stopping)
            {
                return;
            }

            Interlocked.Increment(ref interleavedStartAttempts);
            service.Start();

            // 直列化されていれば Stopping のまま。直列化されていなければ Start() が
            // Starting へ遷移させ、新しい _stopCts / _loopTask を生成してしまう
            stateAfterInterleavedStart = service.State;
        }

        service.StateChanged += OnStateChanged;
        try
        {
            Task stopTask = service.StopAsync();
            _ = exitSignal.TrySetResult();
            await stopTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            service.StateChanged -= OnStateChanged;
        }

        interleavedStartAttempts.Should().Be(1, because: "割り込みが実際に起きたことを確かめる");
        stateAfterInterleavedStart.Should().Be(
            SubscriptionState.Stopping,
            because: "停止処理中の開始要求は状態を書き換えない");
        preflightStarts.Should().Be(1, because: "割り込んだ開始要求は新しい購読ループを生成しない");
        service.State.Should().Be(SubscriptionState.Stopped);
    }

    private static async Task WaitForStateAsync(McpSubscriptionService service, SubscriptionState expected)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnStateChanged(object? sender, SubscriptionState state)
        {
            if (state == expected)
            {
                _ = completion.TrySetResult();
            }
        }

        service.StateChanged += OnStateChanged;
        try
        {
            if (service.State == expected)
            {
                return;
            }

            await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            service.StateChanged -= OnStateChanged;
        }
    }
}
