// <copyright file="UpdateApplyCoordinatorTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using Moq;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public sealed class UpdateApplyCoordinatorTests : IDisposable
{
    private static readonly Uri _uri = new("https://example.com/app.appinstaller");

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"UpdateApplyTests_{Guid.NewGuid()}");
    private readonly Mock<IPackageUpdateAccessor> _accessor = new();
    private readonly Mock<IUpdateRestartPreparation> _preparation = new();
    private readonly ManualTimeProvider _time = new();
    private readonly List<string> _failures = [];
    private bool _isReviewActive;
    private TimeSpan _uptime = TimeSpan.FromMinutes(10);
    private int _reevaluations;

    // 適用できた場合はプロセスが終了するため、戻らない Task で表す
    private readonly TaskCompletionSource _processExit = new();
    private readonly TaskCompletionSource _applyStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public UpdateApplyCoordinatorTests()
    {
        _accessor.SetupGet(accessor => accessor.AppInstallerUri).Returns(_uri);
        _accessor.Setup(accessor => accessor.ApplyAsync(It.IsAny<Uri>(), It.IsAny<string>())).Returns(() =>
        {
            _applyStarted.SetResult();
            return _processExit.Task;
        });
        _preparation.Setup(preparation => preparation.PrepareAsync()).ReturnsAsync("--tray");
    }

    [Fact]
    public async Task RequestAsync_ShouldApplyImmediatelyWhenNoReviewIsActive()
    {
        UpdateApplyCoordinator coordinator = Create();

        Task request = coordinator.RequestAsync();
        await ApplyStartedAsync();

        coordinator.IsPending.Should().BeTrue();
        coordinator.IsWaiting.Should().BeFalse();
        request.IsCompleted.Should().BeFalse();
        _accessor.Verify(accessor => accessor.ApplyAsync(_uri, "--tray"), Times.Once);

        // 適用中の再依頼・取りやめ・再試行は何もしない
        await coordinator.RequestAsync();
        coordinator.Cancel();
        await coordinator.TryApplyAsync();
        coordinator.IsPending.Should().BeTrue();
        _preparation.Verify(preparation => preparation.PrepareAsync(), Times.Once);
    }

    [Fact]
    public async Task RequestAsync_ShouldWaitUntilReviewEnds()
    {
        _isReviewActive = true;
        UpdateApplyCoordinator coordinator = Create();

        await coordinator.RequestAsync();
        await coordinator.TryApplyAsync();

        coordinator.IsWaiting.Should().BeTrue();
        _preparation.Verify(preparation => preparation.PrepareAsync(), Times.Never);

        _isReviewActive = false;
        _ = coordinator.TryApplyAsync();
        await ApplyStartedAsync();

        _accessor.Verify(accessor => accessor.ApplyAsync(_uri, "--tray"), Times.Once);
    }

    [Fact]
    public async Task RequestAsync_ShouldWaitUntilProcessCanBeRestarted()
    {
        _uptime = TimeSpan.FromSeconds(20);
        UpdateApplyCoordinator coordinator = Create();

        await coordinator.RequestAsync();

        coordinator.IsWaiting.Should().BeTrue();
        _time.DueTime.Should().Be(UpdateApplyCoordinator.MinimumUptime - _uptime);
        _preparation.Verify(preparation => preparation.PrepareAsync(), Times.Never);

        _time.Fire();
        _reevaluations.Should().Be(1);
        _uptime = UpdateApplyCoordinator.MinimumUptime;
        _ = coordinator.TryApplyAsync();
        await ApplyStartedAsync();

        _accessor.Verify(accessor => accessor.ApplyAsync(_uri, "--tray"), Times.Once);
        _time.IsTimerDisposed.Should().BeTrue();
    }

    [Fact]
    public async Task Cancel_ShouldReleaseHeldReviews()
    {
        _uptime = TimeSpan.Zero;
        UpdateApplyCoordinator coordinator = Create();
        await coordinator.RequestAsync();

        coordinator.Cancel();

        coordinator.IsPending.Should().BeFalse();
        _time.IsTimerDisposed.Should().BeTrue();
        _reevaluations.Should().Be(1);
        _uptime = TimeSpan.FromMinutes(10);
        await coordinator.TryApplyAsync();
        _preparation.Verify(preparation => preparation.PrepareAsync(), Times.Never);
    }

    [Theory]
    [InlineData("noUri", ".appinstaller の URI を取得できません")]
    [InlineData("prepare", "保存に失敗")]
    [InlineData("apply", "登録に失敗")]
    [InlineData("returned", "アプリが再起動されませんでした")]
    public async Task RequestAsync_ShouldRecoverWhenUpdateDoesNotRestartApp(string failure, string expected)
    {
        switch (failure)
        {
            case "noUri":
                _accessor.SetupGet(accessor => accessor.AppInstallerUri).Returns((Uri?)null);
                break;
            case "prepare":
                _preparation.Setup(preparation => preparation.PrepareAsync()).ThrowsAsync(new IOException("保存に失敗"));
                break;
            case "apply":
                _accessor.Setup(accessor => accessor.ApplyAsync(It.IsAny<Uri>(), It.IsAny<string>()))
                    .ThrowsAsync(new InvalidOperationException("登録に失敗"));
                break;
            default:
                _processExit.SetResult();
                break;
        }

        UpdateApplyCoordinator coordinator = Create();

        await coordinator.RequestAsync();

        coordinator.IsPending.Should().BeFalse();
        _failures.Should().ContainSingle().Which.Should().Contain(expected);
        _preparation.Verify(preparation => preparation.RecoverAsync(), Times.Once);
        _reevaluations.Should().Be(1);
    }

    [Fact]
    public async Task RequestAsync_ShouldStopHoldingReviewsEvenWhenRecoveryFails()
    {
        _processExit.SetResult();
        _preparation.Setup(preparation => preparation.RecoverAsync()).ThrowsAsync(new IOException("復元に失敗"));
        UpdateApplyCoordinator coordinator = Create();

        await coordinator.RequestAsync();

        coordinator.IsPending.Should().BeFalse();
        _failures.Should().ContainSingle().Which.Should()
            .Contain("アプリが再起動されませんでした").And.Contain("復元に失敗");
        _reevaluations.Should().Be(1);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private Task ApplyStartedAsync() => _applyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

    private UpdateApplyCoordinator Create()
        => new(
            _accessor.Object,
            () => _isReviewActive,
            () => _uptime,
            _preparation.Object,
            new LoggingService(_directory),
            _failures.Add,
            () => _reevaluations++,
            _time);

    private sealed class ManualTimeProvider : TimeProvider
    {
        private ManualTimer? _timer;

        public TimeSpan DueTime { get; private set; }

        public bool IsTimerDisposed => _timer?.IsDisposed ?? false;

        public void Fire() => _timer!.Callback(null);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            DueTime = dueTime;
            _timer = new ManualTimer(callback);
            return _timer;
        }

        private sealed class ManualTimer(TimerCallback callback) : ITimer
        {
            public TimerCallback Callback { get; } = callback;

            public bool IsDisposed { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose() => IsDisposed = true;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
