// <copyright file="ReviewRegistrationCoordinatorTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public sealed class ReviewRegistrationCoordinatorTests
{
    private static readonly PrReference _reference = new("scottlz0310", "squirrel-notifier", 301);

    [Fact]
    public void Reasons_ShouldExposeSupportedRegistrationReasons()
    {
        ReviewRegistrationCoordinator.Reasons.Should().Equal(
            "opened",
            "synchronized",
            "re-review-requested");
    }

    [Fact]
    public async Task RegisterAsync_ShouldRejectInvalidInput_WithoutCallingRegistration()
    {
        int calls = 0;
        var coordinator = new ReviewRegistrationCoordinator(
            (_, _, _, _) =>
            {
                calls++;
                return Task.FromResult(new ReviewRegistrationResult());
            });

        ReviewRegistrationPresentation presentation = await coordinator.RegisterAsync(
            "not-a-pr-reference",
            "opened",
            _ => Task.FromResult(true),
            CancellationToken.None);

        presentation.HasDialog.Should().BeTrue();
        presentation.DialogTitle.Should().Be("入力エラー");
        presentation.DialogMessage.Should().Contain("owner/repo#123");
        presentation.ClearInput.Should().BeFalse();
        presentation.IsAuthenticationRequired.Should().BeNull();
        calls.Should().Be(0);
    }

    [Fact]
    public async Task RegisterAsync_ShouldFormatSuccessAndForwardParsedReference()
    {
        PrReference? capturedReference = null;
        string? capturedReason = null;
        var coordinator = new ReviewRegistrationCoordinator(
            (reference, reason, _, _) =>
            {
                capturedReference = reference;
                capturedReason = reason;
                return Task.FromResult(new ReviewRegistrationResult
                {
                    Outcome = ReviewRegistrationOutcome.Registered,
                });
            });

        ReviewRegistrationPresentation presentation = await coordinator.RegisterAsync(
            " https://github.com/scottlz0310/squirrel-notifier/pull/301 ",
            "synchronized",
            _ => Task.FromResult(true),
            CancellationToken.None);

        capturedReference.Should().Be(_reference);
        capturedReason.Should().Be("synchronized");
        presentation.ClearInput.Should().BeTrue();
        presentation.DialogTitle.Should().Be("レビュー登録完了");
        presentation.DialogMessage.Should().Contain("scottlz0310/squirrel-notifier#301");
        presentation.DialogMessage.Should().Contain("reason=synchronized");
        presentation.IsAuthenticationRequired.Should().BeNull();
    }

    [Fact]
    public async Task RegisterAsync_ShouldUseOpened_WhenReasonIsNotSelected()
    {
        string? capturedReason = null;
        var coordinator = new ReviewRegistrationCoordinator(
            (_, reason, _, _) =>
            {
                capturedReason = reason;
                return Task.FromResult(new ReviewRegistrationResult
                {
                    Outcome = ReviewRegistrationOutcome.Cancelled,
                });
            });

        ReviewRegistrationPresentation presentation = await coordinator.RegisterAsync(
            "scottlz0310/squirrel-notifier#301",
            null,
            _ => Task.FromResult(true),
            CancellationToken.None);

        capturedReason.Should().Be("opened");
        presentation.HasDialog.Should().BeFalse();
        presentation.ClearInput.Should().BeFalse();
    }

    [Theory]
    [InlineData(nameof(ReviewRegistrationOutcome.Cancelled))]
    [InlineData(nameof(ReviewRegistrationOutcome.AlreadyInProgress))]
    public async Task RegisterAsync_ShouldNotShowDialog_WhenRegistrationDoesNotComplete(string outcomeName)
    {
        var coordinator = new ReviewRegistrationCoordinator(
            (_, _, _, _) => Task.FromResult(new ReviewRegistrationResult
            {
                Outcome = Enum.Parse<ReviewRegistrationOutcome>(outcomeName),
            }));

        ReviewRegistrationPresentation presentation = await coordinator.RegisterAsync(
            "scottlz0310/squirrel-notifier#301",
            "opened",
            _ => Task.FromResult(true),
            CancellationToken.None);

        presentation.HasDialog.Should().BeFalse();
        presentation.DialogTitle.Should().BeNull();
        presentation.DialogMessage.Should().BeNull();
        presentation.IsAuthenticationRequired.Should().BeNull();
    }

    [Theory]
    [InlineData(nameof(ReviewRegistrationOutcome.SubscriptionStartFailed), "購読開始エラー", true)]
    [InlineData(nameof(ReviewRegistrationOutcome.EnqueueFailed), "レビュー登録エラー", false)]
    public async Task RegisterAsync_ShouldMapFailureToDialogAndAuthenticationState(
        string outcomeName,
        string expectedTitle,
        bool authenticationRequired)
    {
        var coordinator = new ReviewRegistrationCoordinator(
            (_, _, _, _) => Task.FromResult(new ReviewRegistrationResult
            {
                Outcome = Enum.Parse<ReviewRegistrationOutcome>(outcomeName),
                IsAuthenticationRequired = authenticationRequired,
                ErrorMessage = "処理に失敗しました。",
            }));

        ReviewRegistrationPresentation presentation = await coordinator.RegisterAsync(
            "scottlz0310/squirrel-notifier#301",
            "opened",
            _ => Task.FromResult(true),
            CancellationToken.None);

        presentation.HasDialog.Should().BeTrue();
        presentation.DialogTitle.Should().Be(expectedTitle);
        presentation.DialogMessage.Should().Be("処理に失敗しました。");
        presentation.IsAuthenticationRequired.Should().Be(authenticationRequired);
        presentation.ClearInput.Should().BeFalse();
    }
}
