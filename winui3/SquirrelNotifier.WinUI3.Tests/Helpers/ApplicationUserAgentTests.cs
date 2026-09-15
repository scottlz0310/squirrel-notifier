// <copyright file="ApplicationUserAgentTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Helpers;
using SquirrelNotifier.WinUI3.Services;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Helpers;

public sealed class ApplicationUserAgentTests : IDisposable
{
    private readonly string _logDir = Path.Combine(Path.GetTempPath(), $"ApplicationUserAgentTests_{Guid.NewGuid()}");

    public enum HttpClientOwner
    {
        GitHubPullRequestStatusClient,
        AutoUpdateService,
    }

    public void Dispose()
    {
        if (Directory.Exists(_logDir))
        {
            Directory.Delete(_logDir, true);
        }
    }

    [Theory]
    [InlineData(0, 10, 0, 0, "Squirrel-Notifier-WinUI3/0.10.0")]
    [InlineData(1, 2, 3, 4, "Squirrel-Notifier-WinUI3/1.2.3")]
    public void Create_ShouldUseMajorMinorBuildOfVersion(int major, int minor, int build, int revision, string expected)
    {
        System.Net.Http.Headers.ProductInfoHeaderValue userAgent = ApplicationUserAgent.Create(new Version(major, minor, build, revision));

        userAgent.ToString().Should().Be(expected);
    }

    [Theory]
    [InlineData(HttpClientOwner.GitHubPullRequestStatusClient)]
    [InlineData(HttpClientOwner.AutoUpdateService)]
    public void Constructor_ShouldSetAssemblyVersionUserAgent_WhenUserAgentIsMissing(HttpClientOwner owner)
    {
        using var httpClient = new HttpClient();
        string expected = $"Squirrel-Notifier-WinUI3/{typeof(ApplicationUserAgent).Assembly.GetName().Version!.ToString(3)}";

        using IDisposable client = CreateClient(owner, httpClient);

        httpClient.DefaultRequestHeaders.UserAgent.ToString().Should().Be(expected);
    }

    [Theory]
    [InlineData(HttpClientOwner.GitHubPullRequestStatusClient)]
    [InlineData(HttpClientOwner.AutoUpdateService)]
    public void Constructor_ShouldKeepExistingUserAgent(HttpClientOwner owner)
    {
        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("CustomAgent/1.0");

        using IDisposable client = CreateClient(owner, httpClient);

        httpClient.DefaultRequestHeaders.UserAgent.ToString().Should().Be("CustomAgent/1.0");
    }

    private IDisposable CreateClient(HttpClientOwner owner, HttpClient httpClient)
    {
        return owner switch
        {
            HttpClientOwner.GitHubPullRequestStatusClient => new GitHubPullRequestStatusClient(httpClient),
            HttpClientOwner.AutoUpdateService => new AutoUpdateService(new LoggingService(_logDir), httpClient),
            _ => throw new ArgumentOutOfRangeException(nameof(owner), owner, null),
        };
    }
}
