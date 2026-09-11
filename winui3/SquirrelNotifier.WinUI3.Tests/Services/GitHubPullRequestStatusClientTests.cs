// <copyright file="GitHubPullRequestStatusClientTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using SquirrelNotifier.WinUI3.Services;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public class GitHubPullRequestStatusClientTests
{
    [Fact]
    public async Task GetStateAsync_ShouldReturnOpenAndBuildSafeApiRequest()
    {
        using var handler = new RecordingHandler(_ => JsonResponse("{\"state\":\"open\",\"merged_at\":null}"));
        using var httpClient = new HttpClient(handler);
        using var client = new GitHubPullRequestStatusClient(httpClient);

        PullRequestLifecycleState result = await client.GetStateAsync("owner/repo", 42, CancellationToken.None);

        result.Should().Be(PullRequestLifecycleState.Open);
        handler.Request!.RequestUri!.ToString().Should().Be("https://api.github.com/repos/owner/repo/pulls/42");
        handler.Request.Headers.UserAgent.Should().ContainSingle(value => value.Product != null && value.Product.Name == "Squirrel-Notifier-WinUI3");
        handler.Request.Headers.Accept.Should().ContainSingle(value => value.MediaType == "application/vnd.github+json");
    }

    [Fact]
    public async Task GetStateAsync_ShouldDistinguishClosedAndMerged()
    {
        using GitHubPullRequestStatusClient closedClient = CreateClient("{\"state\":\"closed\",\"merged_at\":null}");
        using GitHubPullRequestStatusClient mergedClient = CreateClient("{\"state\":\"closed\",\"merged_at\":\"2026-09-11T00:00:00Z\"}");

        PullRequestLifecycleState closed = await closedClient.GetStateAsync("owner/repo", 1, CancellationToken.None);
        PullRequestLifecycleState merged = await mergedClient.GetStateAsync("owner/repo", 2, CancellationToken.None);

        closed.Should().Be(PullRequestLifecycleState.Closed);
        merged.Should().Be(PullRequestLifecycleState.Merged);
    }

    [Fact]
    public async Task GetStateAsync_ShouldThrow_WhenGitHubReturnsError()
    {
        using var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var client = new GitHubPullRequestStatusClient(new HttpClient(handler));

        Func<Task> act = () => client.GetStateAsync("owner/repo", 42, CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>().WithMessage("*HTTP 404*");
    }

    [Theory]
    [InlineData("{\"state\":\"unknown\"}")]
    [InlineData("{\"merged_at\":null}")]
    [InlineData("not-json")]
    public async Task GetStateAsync_ShouldThrow_WhenResponseIsInvalid(string content)
    {
        using GitHubPullRequestStatusClient client = CreateClient(content);

        Func<Task> act = () => client.GetStateAsync("owner/repo", 42, CancellationToken.None);

        await act.Should().ThrowAsync<JsonException>();
    }

    [Theory]
    [InlineData("", 1)]
    [InlineData("owner/repo", 0)]
    [InlineData("owner/repo/other", 1)]
    [InlineData("owner/repo?token=secret", 1)]
    public async Task GetStateAsync_ShouldThrow_WhenReferenceIsInvalid(string repository, int prNumber)
    {
        using GitHubPullRequestStatusClient client = CreateClient("{\"state\":\"open\"}");

        Func<Task> act = () => client.GetStateAsync(repository, prNumber, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public void Constructor_ShouldThrow_WhenTimeoutIsNotPositive()
    {
        Action act = () => _ = new GitHubPullRequestStatusClient(requestTimeout: TimeSpan.Zero);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    private static GitHubPullRequestStatusClient CreateClient(string content)
        => new(new HttpClient(new RecordingHandler(_ => JsonResponse(content))));

    private static HttpResponseMessage JsonResponse(string content)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(content),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return response;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(_responseFactory(request));
        }
    }
}
