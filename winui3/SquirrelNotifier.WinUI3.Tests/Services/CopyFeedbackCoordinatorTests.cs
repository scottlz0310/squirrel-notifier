// <copyright file="CopyFeedbackCoordinatorTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public sealed class CopyFeedbackCoordinatorTests
{
    [Theory]
    [InlineData(true, "起動コマンドをクリップボードにコピーしました。")]
    [InlineData(false, "クリップボードにコピーしました。")]
    public void ShowCopied_ShouldReturnSuccessPresentationAndScheduleExpiration(
        bool isLaunchCommand,
        string expectedMessage)
    {
        DelayRecorder delay = new();
        using CopyFeedbackCoordinator coordinator = new(delay.DelayAsync);

        CopyFeedbackPresentation presentation = isLaunchCommand
            ? coordinator.ShowLaunchCommandCopied()
            : coordinator.ShowTextCopied();

        presentation.Should().Be(new CopyFeedbackPresentation(
            CopyFeedbackSeverity.Success,
            expectedMessage));
        delay.Requests.Should().ContainSingle();
        delay.Requests[0].Duration.Should().Be(TimeSpan.FromSeconds(2.5));
    }

    [Theory]
    [InlineData("クリップボードを利用できません")]
    [InlineData("")]
    public void ShowFailure_ShouldReturnErrorPresentationWithExceptionMessage(string exceptionMessage)
    {
        DelayRecorder delay = new();
        using CopyFeedbackCoordinator coordinator = new(delay.DelayAsync);

        CopyFeedbackPresentation presentation = coordinator.ShowFailure(
            new InvalidOperationException(exceptionMessage));

        presentation.Should().Be(new CopyFeedbackPresentation(
            CopyFeedbackSeverity.Error,
            $"コピーに失敗しました: {exceptionMessage}"));
    }

    [Fact]
    public async Task Expiration_ShouldRaiseExpiredAfterDelay()
    {
        DelayRecorder delay = new();
        using CopyFeedbackCoordinator coordinator = new(delay.DelayAsync);
        var expired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.Expired += (_, _) => expired.TrySetResult();

        coordinator.ShowTextCopied();
        delay.Requests[0].Complete();

        await expired.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Show_ShouldCancelPreviousExpirationAndOnlyExpireLatestNotification()
    {
        DelayRecorder delay = new();
        using CopyFeedbackCoordinator coordinator = new(delay.DelayAsync);
        List<CopyFeedbackPresentation> expired = [];
        var latestExpired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.Expired += (_, _) =>
        {
            expired.Add(new CopyFeedbackPresentation(CopyFeedbackSeverity.Success, "期限切れ"));
            latestExpired.TrySetResult();
        };

        coordinator.ShowTextCopied();
        DelayRequest firstRequest = delay.Requests[0];
        coordinator.ShowLaunchCommandCopied();
        DelayRequest latestRequest = delay.Requests[1];

        firstRequest.Token.IsCancellationRequested.Should().BeTrue();
        latestRequest.Token.IsCancellationRequested.Should().BeFalse();

        firstRequest.Complete();
        latestRequest.Complete();
        await latestExpired.Task.WaitAsync(TimeSpan.FromSeconds(5));

        expired.Should().ContainSingle();
    }

    [Fact]
    public void Dispose_ShouldCancelPendingExpiration()
    {
        DelayRecorder delay = new();
        CopyFeedbackCoordinator coordinator = new(delay.DelayAsync);

        coordinator.ShowTextCopied();
        coordinator.Dispose();

        delay.Requests[0].Token.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public async Task ExpirationFailure_ShouldRaiseFailureWithLogMessage()
    {
        var failure = new InvalidOperationException("タイマーを開始できません");
        using CopyFeedbackCoordinator coordinator = new((_, _) => Task.FromException(failure));
        var failed = new TaskCompletionSource<CopyFeedbackExpirationFailure>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.ExpirationFailed += (_, result) => failed.TrySetResult(result);

        coordinator.ShowTextCopied();

        CopyFeedbackExpirationFailure result = await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        result.Message.Should().Be("コピー通知の自動クローズに失敗しました: タイマーを開始できません");
    }

    private sealed class DelayRecorder
    {
        public List<DelayRequest> Requests { get; } = [];

        public Task DelayAsync(TimeSpan duration, CancellationToken token)
        {
            DelayRequest request = new(duration, token);
            Requests.Add(request);
            _ = token.Register(() => request.Completion.TrySetCanceled(token));
            return request.Completion.Task;
        }
    }

    private sealed class DelayRequest(TimeSpan duration, CancellationToken token)
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TimeSpan Duration { get; } = duration;

        public CancellationToken Token { get; } = token;

        public void Complete()
        {
            Completion.TrySetResult();
        }
    }
}
