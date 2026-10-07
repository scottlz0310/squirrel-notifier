// <copyright file="GatewayLoginCoordinatorTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Tests.Services;

// 判定対象の enum は internal のため、InlineData では名前で受け取り Enum.Parse で解決する
public sealed class GatewayLoginCoordinatorTests
{
    [Theory]
    [InlineData("http://localhost:8080/mcp/thread-owl")]
    [InlineData("https://localhost:8080/mcp/thread-owl")]
    [InlineData("https://gateway.example.com")]
    public void TryBeginLogin_ShouldStart_WhenGatewayUrlIsHttpOrHttps(string gatewayUrl)
    {
        GatewayLoginCoordinator coordinator = new();

        GatewayLoginStartDecision decision = coordinator.TryBeginLogin(gatewayUrl);

        decision.Status.Should().Be(GatewayLoginStartStatus.Started);
        decision.CanStart.Should().BeTrue();
        decision.ErrorTitle.Should().BeNull();
        decision.ErrorMessage.Should().BeNull();
        coordinator.IsLoginPending.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("localhost:8080")]
    [InlineData("/mcp/thread-owl")]
    [InlineData("ftp://gateway.example.com")]
    [InlineData("file:///C:/gateway")]
    public void TryBeginLogin_ShouldRejectUnsupportedGatewayUrl(string? gatewayUrl)
    {
        GatewayLoginCoordinator coordinator = new();

        GatewayLoginStartDecision decision = coordinator.TryBeginLogin(gatewayUrl);

        decision.Status.Should().Be(GatewayLoginStartStatus.InvalidGatewayUrl);
        decision.CanStart.Should().BeFalse();
        decision.ErrorTitle.Should().Be("設定エラー");
        decision.ErrorMessage.Should().Contain("http(s)://");

        // 開始していないため、URL を直してすぐ再試行できる
        coordinator.IsLoginPending.Should().BeFalse();
    }

    [Fact]
    public void TryBeginLogin_ShouldSkipReentrant_WhileLoginIsPending()
    {
        GatewayLoginCoordinator coordinator = new();
        coordinator.TryBeginLogin("https://localhost:8080");

        GatewayLoginStartDecision decision = coordinator.TryBeginLogin("https://localhost:8080");

        decision.Status.Should().Be(GatewayLoginStartStatus.SkippedReentrant);
        decision.CanStart.Should().BeFalse();

        // 再入は黙って無視する。ダイアログを出すと多重表示の原因になる
        decision.ErrorTitle.Should().BeNull();
        decision.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void TryBeginLogin_ShouldSkipReentrant_EvenWhenGatewayUrlIsInvalid()
    {
        GatewayLoginCoordinator coordinator = new();
        coordinator.TryBeginLogin("https://localhost:8080");

        GatewayLoginStartDecision decision = coordinator.TryBeginLogin("not-a-url");

        decision.Status.Should().Be(GatewayLoginStartStatus.SkippedReentrant);
    }

    [Fact]
    public void EndLogin_ShouldAllowNextLogin()
    {
        GatewayLoginCoordinator coordinator = new();
        coordinator.TryBeginLogin("https://localhost:8080");

        coordinator.EndLogin();

        coordinator.IsLoginPending.Should().BeFalse();
        coordinator.TryBeginLogin("https://localhost:8080").Status.Should().Be(GatewayLoginStartStatus.Started);
    }

    [Theory]
    [InlineData("Stopped", true)]
    [InlineData("Error", true)]
    [InlineData("Starting", false)]
    [InlineData("Running", false)]
    [InlineData("Stopping", false)]
    public void DescribeResult_ShouldRestartSubscription_OnlyWhenStoppedOrError(
        string subscriptionState,
        bool expectedRestart)
    {
        McpLoginResult result = new() { Outcome = McpLoginOutcome.Succeeded };

        GatewayLoginPresentation presentation = GatewayLoginCoordinator.DescribeResult(
            result, Enum.Parse<SubscriptionState>(subscriptionState));

        presentation.CloseAuthRequiredInfoBar.Should().BeTrue();
        presentation.RestartSubscription.Should().Be(expectedRestart);
        presentation.DialogTitle.Should().Be("ログイン成功");
        presentation.DialogMessage.Should().StartWith("mcp-gateway への認証に成功しました。");
        presentation.DialogMessage!.Contains("購読を再開しました。", StringComparison.Ordinal)
            .Should().Be(expectedRestart);
    }

    [Fact]
    public void DescribeResult_ShouldShowNothing_WhenCancelled()
    {
        McpLoginResult result = new() { Outcome = McpLoginOutcome.Cancelled };

        GatewayLoginPresentation presentation = GatewayLoginCoordinator.DescribeResult(result, SubscriptionState.Stopped);

        presentation.HasDialog.Should().BeFalse();
        presentation.DialogTitle.Should().BeNull();
        presentation.DialogMessage.Should().BeNull();
        presentation.CloseAuthRequiredInfoBar.Should().BeFalse();
        presentation.RestartSubscription.Should().BeFalse();
    }

    [Theory]
    [InlineData("TimedOut", "ログインがタイムアウトしました", "認証が時間内に完了しませんでした。")]
    [InlineData("Failed", "ログインに失敗しました", "mcp-gateway へのログインに失敗しました。")]
    public void DescribeResult_ShouldFallBackToDefaultMessage_WhenErrorMessageIsMissing(
        string outcome,
        string expectedTitle,
        string expectedMessage)
    {
        McpLoginResult result = new() { Outcome = Enum.Parse<McpLoginOutcome>(outcome) };

        GatewayLoginPresentation presentation = GatewayLoginCoordinator.DescribeResult(result, SubscriptionState.Running);

        presentation.DialogTitle.Should().Be(expectedTitle);
        presentation.DialogMessage.Should().Be(expectedMessage);
        presentation.CloseAuthRequiredInfoBar.Should().BeFalse();
        presentation.RestartSubscription.Should().BeFalse();
    }

    [Theory]
    [InlineData("TimedOut", "承認がタイムアウトしました（device flow）。")]
    [InlineData("Failed", "resource-bridge-cli が見つかりません。")]
    [InlineData("Failed", "gateway へ接続できません（ECONNREFUSED）。")]
    public void DescribeResult_ShouldSurfaceErrorMessage_WhenProvided(string outcome, string errorMessage)
    {
        McpLoginResult result = new()
        {
            Outcome = Enum.Parse<McpLoginOutcome>(outcome),
            ErrorMessage = errorMessage,
        };

        GatewayLoginPresentation presentation = GatewayLoginCoordinator.DescribeResult(result, SubscriptionState.Running);

        presentation.DialogMessage.Should().Be(errorMessage);
    }

    [Fact]
    public void DescribeVerification_ShouldPreferCompleteUri()
    {
        DeviceVerificationInfo info = new()
        {
            VerificationUri = "https://example.com/device",
            VerificationUriComplete = "https://example.com/device?user_code=ABCD-EFGH",
            UserCode = "ABCD-EFGH",
        };

        DeviceVerificationView view = GatewayLoginCoordinator.DescribeVerification(info);

        view.Url.Should().Be("https://example.com/device?user_code=ABCD-EFGH");
        view.UserCode.Should().Be("ABCD-EFGH");
        view.HasUserCode.Should().BeTrue();
    }

    [Fact]
    public void DescribeVerification_ShouldOmitUserCode_WhenSubscriberDidNotProvideOne()
    {
        DeviceVerificationInfo info = new()
        {
            VerificationUri = "https://example.com/device",
            UserCode = string.Empty,
        };

        DeviceVerificationView view = GatewayLoginCoordinator.DescribeVerification(info);

        view.Url.Should().Be("https://example.com/device");
        view.UserCode.Should().BeNull();
        view.HasUserCode.Should().BeFalse();
    }
}
