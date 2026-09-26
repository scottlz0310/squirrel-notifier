// <copyright file="ReviewerWorkspaceLayoutTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using System.IO;
using FluentAssertions;
using SquirrelNotifier.WinUI3.Helpers;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Helpers;

public class ReviewerWorkspaceLayoutTests
{
    private static readonly string _root = Path.Combine(Path.GetTempPath(), "reviewer-root");

    [Theory]
    [InlineData("scottlz0310/squirrel-notifier", 403, "scottlz0310", "squirrel-notifier", "403")]
    [InlineData("ScottLZ0310/Squirrel-Notifier", 403, "scottlz0310", "squirrel-notifier", "403")]
    [InlineData("owner/repo.name_x", 1, "owner", "repo.name_x", "1")]
    public void GetWorkspaceDirectory_ShouldNestOwnerRepoAndPr(
        string repository, int prNumber, string owner, string repo, string pr)
    {
        ReviewerWorkspaceLayout.GetWorkspaceDirectory(_root, repository, prNumber)
            .Should().Be(Path.Combine(_root, owner, repo, pr));
    }

    [Fact]
    public void GetWorkspaceDirectory_ShouldNotCollide_WhenNamesContainHyphen()
    {
        string first = ReviewerWorkspaceLayout.GetWorkspaceDirectory(_root, "a-b/c", 1);
        string second = ReviewerWorkspaceLayout.GetWorkspaceDirectory(_root, "a/b-c", 1);

        first.Should().NotBe(second);
    }

    [Theory]
    [InlineData("")]
    [InlineData("owner")]
    [InlineData("owner/repo/extra")]
    [InlineData("../repo")]
    [InlineData("owner/..")]
    [InlineData("owner/.")]
    [InlineData("owner/re po")]
    [InlineData("owner/re\\po")]
    [InlineData("owner/C:")]
    public void GetWorkspaceDirectory_ShouldRejectUnsafeRepository(string repository)
    {
        Action act = () => ReviewerWorkspaceLayout.GetWorkspaceDirectory(_root, repository, 1);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void GetWorkspaceDirectory_ShouldRejectNonPositivePrNumber(int prNumber)
    {
        Action act = () => ReviewerWorkspaceLayout.GetWorkspaceDirectory(_root, "owner/repo", prNumber);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void GetReviewerRoot_ShouldBeUnderSettingsDirectory()
    {
        string settings = Path.Combine(Path.GetTempPath(), "settings");

        ReviewerWorkspaceLayout.GetReviewerRoot(settings).Should().Be(Path.Combine(settings, "launcher-workspace", "reviewer"));
    }

    [Fact]
    public void GetScratchDirectory_ShouldBeUnderWorkspace()
    {
        string workspace = Path.Combine(_root, "owner", "repo", "1");

        ReviewerWorkspaceLayout.GetScratchDirectory(workspace).Should().Be(Path.Combine(workspace, "tmp"));
    }

    [Theory]
    [InlineData("TEMP")]
    [InlineData("TMP")]
    [InlineData(ReviewerWorkspaceLayout.ScratchDirectoryEnvironmentVariable)]
    public void BuildEnvironment_ShouldPointToScratchDirectory(string name)
    {
        string scratch = Path.Combine(_root, "owner", "repo", "1", "tmp");

        ReviewerWorkspaceLayout.BuildEnvironment(scratch).Should().ContainKey(name)
            .WhoseValue.Should().Be(scratch);
    }
}
