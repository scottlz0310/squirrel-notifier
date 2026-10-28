// <copyright file="LauncherHeadGuardTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using Moq;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public class LauncherHeadGuardTests
{
    [Theory]
    [InlineData("open", "0123456789abcdef0123456789abcdef01234567", false)]
    [InlineData("open", "1123456789abcdef0123456789abcdef01234567", true)]
    [InlineData("closed", "0123456789abcdef0123456789abcdef01234567", true)]
    [InlineData("open", "0123456789abcdef0123456789abcdef012345670123456789abcdef01234567", true)]
    public async Task VerifyAsync_ShouldRejectMovedOrClosedPullRequest(string state, string sha, bool shouldFail)
    {
        var client = new Mock<IGhApiClient>();
        client.Setup(x => x.GetAsync("repos/owner/repo/pulls/42", It.IsAny<string>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(GhApiResult.Success($$"""{"state":"{{state}}","headSha":"{{sha}}","baseRef":"main"}"""));
        var guard = new LauncherHeadGuard(client.Object);
        var reviewEvent = new ReviewEvent { Repository = "owner/repo", PrNumber = 42 };
        Func<Task> verify = () => guard.VerifyAsync(reviewEvent, "0123456789abcdef0123456789abcdef01234567", CancellationToken.None);
        if (shouldFail)
        {
            await verify.Should().ThrowAsync<InvalidOperationException>();
        }
        else
        {
            await verify.Should().NotThrowAsync();
        }
    }
}
