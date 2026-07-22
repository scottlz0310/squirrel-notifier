// <copyright file="ReviewRegistrationServiceTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using Moq;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public class ReviewRegistrationServiceTests
{
    private static readonly PrReference _reference = new("scottlz0310", "squirrel-notifier", 209);

    [Fact]
    public async Task RegisterAsync_ShouldEnqueueImmediately_WhenSubscriptionIsRunning()
    {
        var subscription = new Mock<IReviewSubscriptionService>(MockBehavior.Strict);
        subscription.SetupGet(service => service.State).Returns(SubscriptionState.Running);

        var enqueue = new Mock<IEnqueueReviewService>(MockBehavior.Strict);
        enqueue.Setup(service => service.EnqueueAsync(_reference, "opened", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EnqueueReviewResult { Success = true });

        var service = new ReviewRegistrationService(subscription.Object, enqueue.Object);
        bool confirmationRequested = false;

        ReviewRegistrationResult result = await service.RegisterAsync(
            _reference,
            "opened",
            _ =>
            {
                confirmationRequested = true;
                return Task.FromResult(true);
            },
            CancellationToken.None);

        result.Outcome.Should().Be(ReviewRegistrationOutcome.Registered);
        confirmationRequested.Should().BeFalse();
        subscription.Verify(service => service.StartAsync(It.IsAny<CancellationToken>()), Times.Never);
        enqueue.VerifyAll();
    }

    [Theory]
    [InlineData(nameof(SubscriptionState.Stopped))]
    [InlineData(nameof(SubscriptionState.Error))]
    public async Task RegisterAsync_ShouldNotStartOrEnqueue_WhenSubscriptionStartIsDeclined(string stateName)
    {
        SubscriptionState state = Enum.Parse<SubscriptionState>(stateName);
        var subscription = new Mock<IReviewSubscriptionService>(MockBehavior.Strict);
        subscription.SetupGet(service => service.State).Returns(state);

        var enqueue = new Mock<IEnqueueReviewService>(MockBehavior.Strict);
        var service = new ReviewRegistrationService(subscription.Object, enqueue.Object);

        ReviewRegistrationResult result = await service.RegisterAsync(
            _reference,
            "opened",
            _ => Task.FromResult(false),
            CancellationToken.None);

        result.Outcome.Should().Be(ReviewRegistrationOutcome.Cancelled);
        subscription.Verify(service => service.StartAsync(It.IsAny<CancellationToken>()), Times.Never);
        enqueue.Verify(service => service.EnqueueAsync(
            It.IsAny<PrReference>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(nameof(SubscriptionState.Stopped))]
    [InlineData(nameof(SubscriptionState.Error))]
    public async Task RegisterAsync_ShouldStartSubscriptionBeforeEnqueue_WhenStartIsConfirmed(string stateName)
    {
        SubscriptionState initialState = Enum.Parse<SubscriptionState>(stateName);
        SubscriptionState currentState = initialState;
        var order = new List<string>();

        var subscription = new Mock<IReviewSubscriptionService>(MockBehavior.Strict);
        subscription.SetupGet(service => service.State).Returns(() => currentState);
        subscription.Setup(service => service.StartAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                order.Add("start");
                currentState = SubscriptionState.Running;
                return new SubscriptionStartResult { Outcome = SubscriptionStartOutcome.Started };
            });

        var enqueue = new Mock<IEnqueueReviewService>(MockBehavior.Strict);
        enqueue.Setup(service => service.EnqueueAsync(_reference, "opened", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                order.Add("enqueue");
                return new EnqueueReviewResult { Success = true };
            });

        var service = new ReviewRegistrationService(subscription.Object, enqueue.Object);

        ReviewRegistrationResult result = await service.RegisterAsync(
            _reference,
            "opened",
            _ =>
            {
                order.Add("confirm");
                return Task.FromResult(true);
            },
            CancellationToken.None);

        result.Outcome.Should().Be(ReviewRegistrationOutcome.Registered);
        order.Should().ContainInOrder("confirm", "start", "enqueue");
    }

    [Theory]
    [InlineData(nameof(SubscriptionStartOutcome.Failed))]
    [InlineData(nameof(SubscriptionStartOutcome.TimedOut))]
    [InlineData(nameof(SubscriptionStartOutcome.Cancelled))]
    public async Task RegisterAsync_ShouldNotEnqueue_WhenSubscriptionStartDoesNotSucceed(
        string outcomeName)
    {
        SubscriptionStartOutcome outcome = Enum.Parse<SubscriptionStartOutcome>(outcomeName);
        var subscription = new Mock<IReviewSubscriptionService>(MockBehavior.Strict);
        subscription.SetupGet(service => service.State).Returns(SubscriptionState.Error);
        subscription.SetupGet(service => service.IsAuthenticationRequired).Returns(true);
        subscription.Setup(service => service.StartAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubscriptionStartResult
            {
                Outcome = outcome,
                ErrorMessage = "購読開始に失敗しました。",
            });

        var enqueue = new Mock<IEnqueueReviewService>(MockBehavior.Strict);
        var service = new ReviewRegistrationService(subscription.Object, enqueue.Object);

        ReviewRegistrationResult result = await service.RegisterAsync(
            _reference,
            "opened",
            _ => Task.FromResult(true),
            CancellationToken.None);

        result.Outcome.Should().Be(ReviewRegistrationOutcome.SubscriptionStartFailed);
        result.IsAuthenticationRequired.Should().BeTrue();
        result.ErrorMessage.Should().Be("購読開始に失敗しました。");
        enqueue.Verify(service => service.EnqueueAsync(
            It.IsAny<PrReference>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RegisterAsync_ShouldNotEnqueue_WhenStartedResultDoesNotLeaveSubscriptionRunning()
    {
        var subscription = new Mock<IReviewSubscriptionService>(MockBehavior.Strict);
        subscription.SetupGet(service => service.State).Returns(SubscriptionState.Error);
        subscription.SetupGet(service => service.IsAuthenticationRequired).Returns(false);
        subscription.Setup(service => service.StartAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubscriptionStartResult { Outcome = SubscriptionStartOutcome.Started });

        var enqueue = new Mock<IEnqueueReviewService>(MockBehavior.Strict);
        var service = new ReviewRegistrationService(subscription.Object, enqueue.Object);

        ReviewRegistrationResult result = await service.RegisterAsync(
            _reference,
            "opened",
            _ => Task.FromResult(true),
            CancellationToken.None);

        result.Outcome.Should().Be(ReviewRegistrationOutcome.SubscriptionStartFailed);
        enqueue.Verify(service => service.EnqueueAsync(
            It.IsAny<PrReference>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RegisterAsync_ShouldPropagateAuthenticationRequirement_WhenEnqueueFails()
    {
        var subscription = new Mock<IReviewSubscriptionService>(MockBehavior.Strict);
        subscription.SetupGet(service => service.State).Returns(SubscriptionState.Running);

        var enqueue = new Mock<IEnqueueReviewService>(MockBehavior.Strict);
        enqueue.Setup(service => service.EnqueueAsync(_reference, "opened", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EnqueueReviewResult
            {
                Success = false,
                IsAuthenticationRequired = true,
                ErrorMessage = "認証が必要です。",
            });

        var service = new ReviewRegistrationService(subscription.Object, enqueue.Object);

        ReviewRegistrationResult result = await service.RegisterAsync(
            _reference,
            "opened",
            _ => Task.FromResult(true),
            CancellationToken.None);

        result.Outcome.Should().Be(ReviewRegistrationOutcome.EnqueueFailed);
        result.IsAuthenticationRequired.Should().BeTrue();
        result.ErrorMessage.Should().Be("認証が必要です。");
    }

    [Fact]
    public async Task RegisterAsync_ShouldNotStartOrEnqueue_WhenCallerAlreadyCancelled()
    {
        var subscription = new Mock<IReviewSubscriptionService>(MockBehavior.Strict);
        var enqueue = new Mock<IEnqueueReviewService>(MockBehavior.Strict);
        var service = new ReviewRegistrationService(subscription.Object, enqueue.Object);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        ReviewRegistrationResult result = await service.RegisterAsync(
            _reference,
            "opened",
            _ => Task.FromResult(true),
            cts.Token);

        result.Outcome.Should().Be(ReviewRegistrationOutcome.Cancelled);
        subscription.VerifyNoOtherCalls();
        enqueue.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RegisterAsync_ShouldRejectConcurrentAttempt_WithoutDuplicateEnqueue()
    {
        SubscriptionState currentState = SubscriptionState.Stopped;
        var confirmationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseConfirmation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var subscription = new Mock<IReviewSubscriptionService>(MockBehavior.Strict);
        subscription.SetupGet(service => service.State).Returns(() => currentState);
        subscription.Setup(service => service.StartAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                currentState = SubscriptionState.Running;
                return new SubscriptionStartResult { Outcome = SubscriptionStartOutcome.Started };
            });

        var enqueue = new Mock<IEnqueueReviewService>(MockBehavior.Strict);
        enqueue.Setup(service => service.EnqueueAsync(_reference, "opened", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EnqueueReviewResult { Success = true });

        var service = new ReviewRegistrationService(subscription.Object, enqueue.Object);
        Task<ReviewRegistrationResult> first = service.RegisterAsync(
            _reference,
            "opened",
            async _ =>
            {
                confirmationStarted.SetResult();
                await releaseConfirmation.Task;
                return true;
            },
            CancellationToken.None);

        await confirmationStarted.Task;

        ReviewRegistrationResult concurrent = await service.RegisterAsync(
            _reference,
            "opened",
            _ => Task.FromResult(true),
            CancellationToken.None);

        concurrent.Outcome.Should().Be(ReviewRegistrationOutcome.AlreadyInProgress);

        releaseConfirmation.SetResult();
        ReviewRegistrationResult completed = await first;

        completed.Outcome.Should().Be(ReviewRegistrationOutcome.Registered);
        subscription.Verify(service => service.StartAsync(It.IsAny<CancellationToken>()), Times.Once);
        enqueue.Verify(service => service.EnqueueAsync(
            _reference, "opened", It.IsAny<CancellationToken>()), Times.Once);
    }
}
