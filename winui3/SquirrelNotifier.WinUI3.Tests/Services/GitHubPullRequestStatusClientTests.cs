// <copyright file="GitHubPullRequestStatusClientTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Moq;
using SquirrelNotifier.WinUI3.Services;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public class GitHubPullRequestStatusClientTests
{
    private static readonly DateTimeOffset _now = new(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("{\"state\":\"open\",\"merged_at\":null}", "Open")]
    [InlineData("{\"state\":\"closed\",\"merged_at\":null}", "Closed")]
    [InlineData("{\"state\":\"closed\"}", "Closed")]
    [InlineData("{\"state\":\"closed\",\"merged_at\":\"2026-09-11T00:00:00Z\"}", "Merged")]
    public async Task GetStateAsync_ShouldReturnLifecycleStateThroughAuthenticatedClient(string content, string expected)
    {
        Mock<IGhApiClient> api = CreateApi(GhApiResult.Success(content));
        GitHubPullRequestStatusClient client = new(api.Object);
        using CancellationTokenSource cts = new();

        PullRequestLifecycleState result = await client.GetStateAsync("private-owner/private-repo", 42, cts.Token);

        result.ToString().Should().Be(expected);
        api.Verify(a => a.GetAsync("repos/private-owner/private-repo/pulls/42", "{state, merged_at}", false, cts.Token), Times.Once);
    }

    [Theory]
    [InlineData(401, "認証が必要です")]
    [InlineData(403, "権限がありません")]
    [InlineData(404, "Not Found (HTTP 404)")]
    [InlineData(null, "gh コマンドが見つかりません")]
    [InlineData(null, "gh の実行がタイムアウトしました")]
    public async Task GetStateAsync_ShouldThrowWithoutAssumingClosed_WhenLookupFails(int? status, string error)
    {
        GitHubPullRequestStatusClient client = CreateClient(GhApiResult.Failure(status, error));

        Func<Task> act = () => client.GetStateAsync("owner/repo", 42, CancellationToken.None);

        (await act.Should().ThrowAsync<HttpRequestException>().WithMessage($"*owner/repo#42*{error}*"))
            .Which.Should().NotBeOfType<GitHubRateLimitException>();
    }

    public static TheoryData<int, string?, string?, string?, DateTimeOffset> RateLimitResponses => new()
    {
        { 429, "60", null, null, _now.AddSeconds(60) },
        { 403, "Tue, 15 Sep 2026 01:00:00 GMT", "0", "1789430400", new DateTimeOffset(2026, 9, 15, 1, 0, 0, TimeSpan.Zero) },
        { 403, null, "0", "1789434000", DateTimeOffset.FromUnixTimeSeconds(1789434000) },
        { 429, null, "0", "1789434000", DateTimeOffset.FromUnixTimeSeconds(1789434000) },
    };

    [Theory]
    [MemberData(nameof(RateLimitResponses))]
    public async Task GetStateAsync_ShouldThrowRateLimitException_WhenResetTimeIsProvided(
        int statusCode, string? retryAfter, string? remaining, string? reset, DateTimeOffset expectedResetAt)
    {
        GitHubPullRequestStatusClient client = CreateErrorClient(statusCode, retryAfter, remaining, reset);

        Func<Task> act = () => client.GetStateAsync("owner/repo", 42, CancellationToken.None);

        (await act.Should().ThrowAsync<GitHubRateLimitException>()).Which.ResetAt.Should().Be(expectedResetAt);
    }

    [Theory]
    [InlineData(403, null, null, null)]
    [InlineData(403, null, "10", "1789434000")]
    [InlineData(429, null, "0", null)]
    [InlineData(429, "invalid", "0", "invalid")]
    [InlineData(503, "60", "0", "1789434000")]
    public async Task GetStateAsync_ShouldThrowGeneralHttpError_WhenResetTimeIsUnavailable(
        int statusCode, string? retryAfter, string? remaining, string? reset)
    {
        GitHubPullRequestStatusClient client = CreateErrorClient(statusCode, retryAfter, remaining, reset);

        Func<Task> act = () => client.GetStateAsync("owner/repo", 42, CancellationToken.None);

        (await act.Should().ThrowAsync<HttpRequestException>()).Which.Should().NotBeOfType<GitHubRateLimitException>();
    }

    [Theory]
    [InlineData("{\"state\":\"unknown\"}")]
    [InlineData("{\"merged_at\":null}")]
    [InlineData("{\"state\":null}")]
    [InlineData("[]")]
    [InlineData("not-json")]
    public async Task GetStateAsync_ShouldThrow_WhenResponseIsInvalid(string content)
    {
        GitHubPullRequestStatusClient client = CreateClient(GhApiResult.Success(content));

        Func<Task> act = () => client.GetStateAsync("owner/repo", 42, CancellationToken.None);

        await act.Should().ThrowAsync<JsonException>();
    }

    [Theory]
    [InlineData("", 1)]
    [InlineData("owner/repo", 0)]
    [InlineData("owner/repo/other", 1)]
    [InlineData("owner/repo?token=secret", 1)]
    public async Task GetStateAsync_ShouldThrowWithoutCallingApi_WhenReferenceIsInvalid(string repository, int prNumber)
    {
        Mock<IGhApiClient> api = CreateApi(GhApiResult.Success("{\"state\":\"open\"}"));
        GitHubPullRequestStatusClient client = new(api.Object);

        Func<Task> act = () => client.GetStateAsync(repository, prNumber, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
        api.Verify(a => a.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetStateAsync_ShouldPropagateCallerCancellation()
    {
        using CancellationTokenSource cts = new();
        Mock<IGhApiClient> api = new();
        api.Setup(a => a.GetAsync(It.IsAny<string>(), It.IsAny<string>(), false, cts.Token))
            .ThrowsAsync(new OperationCanceledException(cts.Token));
        GitHubPullRequestStatusClient client = new(api.Object);

        Func<Task> act = () => client.GetStateAsync("owner/repo", 42, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task GetStateAsync_ShouldPreserveRetryAfterThroughGhProcessResponse()
    {
        Mock<IProcessInstance> process = new();
        process.SetupGet(p => p.ExitCode).Returns(1);
        process.SetupGet(p => p.StandardOutput).Returns(new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(
            "HTTP/2.0 403 Forbidden\r\nRetry-After: 60\r\n\r\n{\"message\":\"rate limit exceeded\"}"))));
        process.SetupGet(p => p.StandardError).Returns(new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(
            "gh: rate limit exceeded (HTTP 403)"))));
        process.Setup(p => p.WaitForExitAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        Mock<IProcessRunner> runner = new();
        runner.Setup(r => r.Start(It.IsAny<System.Diagnostics.ProcessStartInfo>())).Returns(process.Object);
        GhApiClient api = new(runner.Object, _ => @"C:\tools\gh.exe", includeResponseHeaders: true);
        GitHubPullRequestStatusClient client = new(api, new FixedTimeProvider(_now));

        Func<Task> act = () => client.GetStateAsync("owner/repo", 42, CancellationToken.None);

        (await act.Should().ThrowAsync<GitHubRateLimitException>()).Which.ResetAt.Should().Be(_now.AddSeconds(60));
        runner.Verify(r => r.Start(It.Is<System.Diagnostics.ProcessStartInfo>(info => info.ArgumentList.Contains("--include"))), Times.Once);
    }

    private static GitHubPullRequestStatusClient CreateClient(GhApiResult result)
        => new(CreateApi(result).Object, new FixedTimeProvider(_now));

    private static Mock<IGhApiClient> CreateApi(GhApiResult result)
    {
        Mock<IGhApiClient> api = new();
        api.Setup(a => a.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
        return api;
    }

    private static GitHubPullRequestStatusClient CreateErrorClient(int statusCode, string? retryAfter, string? remaining, string? reset)
        => CreateClient(GhApiResult.Failure(statusCode, "GitHub API の照会に失敗しました") with
        {
            RateLimitHeaders = new GhApiRateLimitHeaders(retryAfter, remaining, reset),
        });

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
