// <copyright file="CopilotQuotaRateLimitClientTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using Moq;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public class CopilotQuotaRateLimitClientTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 5, 13, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(96.6, 3.4)]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    [InlineData(-5, 100)]
    [InlineData(120, 0)]
    public async Task CaptureAsync_ShouldConvertPercentRemainingToUsedPercentage(double percentRemaining, double expectedUsed)
    {
        string json = $"{{\"reset\":\"2026-12-01T00:00:00.000Z\",\"unlimited\":false,\"percentRemaining\":{percentRemaining.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}";
        CopilotQuotaRateLimitClient client = CreateClient(GhApiResult.Success(json));

        RateLimitSnapshot? snapshot = await client.CaptureAsync("copilot", CancellationToken.None);

        snapshot.Should().NotBeNull();
        snapshot!.AgentId.Should().Be("copilot");
        snapshot.ObservedAt.Should().Be(_now);
        RateLimitInfo limit = snapshot.Limits.Should().ContainSingle().Subject;
        limit.Id.Should().Be(CopilotQuotaRateLimitClient.PremiumLimitId);
        limit.UsedPercentage.Should().BeApproximately(expectedUsed, 0.001);
        limit.ResetAt.Should().Be(new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero));
        limit.IsAutoPauseEligible.Should().BeTrue();
    }

    [Theory]
    [InlineData("{\"reset\":\"2026-12-01T00:00:00Z\",\"unlimited\":true,\"percentRemaining\":100}", "無制限")]
    [InlineData("{\"reset\":\"2026-12-01T00:00:00Z\",\"unlimited\":false,\"percentRemaining\":null}", "使用率")]
    [InlineData("{\"reset\":null,\"unlimited\":false,\"percentRemaining\":50}", "リセット日")]
    [InlineData("{\"reset\":\"not-a-date\",\"unlimited\":false,\"percentRemaining\":50}", "リセット日")]
    [InlineData("null", "解釈")]
    [InlineData("<html>", "解釈")]
    public async Task CaptureWithFailureDetailAsync_ShouldReturnNull_WhenPayloadIsUnusable(string json, string expectedDetail)
    {
        CopilotQuotaRateLimitClient client = CreateClient(GhApiResult.Success(json));

        (RateLimitSnapshot? snapshot, string? detail) = await client.CaptureWithFailureDetailAsync("copilot", CancellationToken.None);

        snapshot.Should().BeNull();
        detail.Should().Contain(expectedDetail);
    }

    [Fact]
    public async Task CaptureWithFailureDetailAsync_ShouldReturnGhError_WhenRequestFails()
    {
        CopilotQuotaRateLimitClient client = CreateClient(GhApiResult.Failure(401, "認証が必要です"));

        (RateLimitSnapshot? snapshot, string? detail) = await client.CaptureWithFailureDetailAsync("copilot", CancellationToken.None);

        snapshot.Should().BeNull();
        detail.Should().Be("認証が必要です");
    }

    [Fact]
    public async Task CaptureAsync_ShouldRequestOnlyRequiredFieldsWithoutPagination()
    {
        Mock<IGhApiClient> api = new();
        api.Setup(a => a.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(GhApiResult.Failure(null, "x"));
        CopilotQuotaRateLimitClient client = new(api.Object, new FixedTimeProvider(_now));

        await client.CaptureAsync("copilot", CancellationToken.None);

        api.Verify(
            a => a.GetAsync(
                "copilot_internal/user",
                It.Is<string>(jq => jq.Contains("quota_reset_date_utc") && !jq.Contains("login")),
                false,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CaptureAsync_ShouldPropagateCancellation()
    {
        Mock<IGhApiClient> api = new();
        api.Setup(a => a.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        CopilotQuotaRateLimitClient client = new(api.Object, new FixedTimeProvider(_now));

        Func<Task> act = () => client.CaptureAsync("copilot", CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static CopilotQuotaRateLimitClient CreateClient(GhApiResult result)
    {
        Mock<IGhApiClient> api = new();
        api.Setup(a => a.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
        return new CopilotQuotaRateLimitClient(api.Object, new FixedTimeProvider(_now));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
