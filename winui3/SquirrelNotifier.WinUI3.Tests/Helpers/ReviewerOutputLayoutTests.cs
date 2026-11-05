// <copyright file="ReviewerOutputLayoutTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using System.IO;
using FluentAssertions;
using SquirrelNotifier.WinUI3.Helpers;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Helpers;

public class ReviewerOutputLayoutTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void GetRoot_ShouldBeSeparateFromWorkspaceRoots()
    {
        string settings = Path.Combine("base", "settings");

        string root = ReviewerOutputLayout.GetRoot(settings);

        root.Should().Be(Path.Combine(settings, "reviewer-output"));
        root.Should().NotStartWith(ReviewerWorkspaceLayout.GetReviewerRoot(settings));
    }

    [Theory]
    [InlineData("ScottLZ0310/Machinery-Report-System", 804, "scottlz0310", "machinery-report-system", "804")]
    [InlineData("a/b", 1, "a", "b", "1")]
    public void GetFilePath_ShouldNestByRepositoryAndPrWithStartTimeName(string repository, int pr, string owner, string repo, string prSegment)
    {
        var startedAt = new DateTimeOffset(2026, 11, 5, 12, 55, 4, 123, TimeSpan.FromHours(9));

        string path = ReviewerOutputLayout.GetFilePath("root", repository, pr, startedAt);

        path.Should().Be(Path.Combine("root", owner, repo, prSegment, "20261105T035504.123Z.log"));
    }

    [Fact]
    public void GetFilePath_ShouldSortInStartOrder()
    {
        var first = new DateTimeOffset(2026, 11, 5, 10, 4, 5, 9, TimeSpan.Zero);
        string a = ReviewerOutputLayout.GetFilePath("root", "o/r", 1, first);
        string b = ReviewerOutputLayout.GetFilePath("root", "o/r", 1, first.AddMilliseconds(1));
        string c = ReviewerOutputLayout.GetFilePath("root", "o/r", 1, first.AddMinutes(30));

        string.CompareOrdinal(a, b).Should().BeLessThan(0);
        string.CompareOrdinal(b, c).Should().BeLessThan(0);
    }

    [Theory]
    [InlineData("not-a-repo")]
    [InlineData("a/../b")]
    [InlineData("a/b/c")]
    public void GetFilePath_ShouldRejectUnsafeRepository(string repository)
    {
        Action act = () => ReviewerOutputLayout.GetFilePath("root", repository, 1, _now);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void SelectDeletions_ShouldSelectExpiredFiles()
    {
        ReviewerOutputFile[] files =
        [
            new("old", _now.UtcDateTime - TimeSpan.FromDays(14), 10),
            new("edge", _now.UtcDateTime - TimeSpan.FromDays(14) + TimeSpan.FromSeconds(1), 10),
            new("new", _now.UtcDateTime - TimeSpan.FromHours(1), 10),
        ];

        ReviewerOutputLayout.SelectDeletions(files, _now, TimeSpan.FromDays(14), 1000).Should().BeEquivalentTo(["old"]);
    }

    [Fact]
    public void SelectDeletions_ShouldDropOldestFirstWhenOverTotalLimit()
    {
        ReviewerOutputFile[] files =
        [
            new("oldest", _now.UtcDateTime - TimeSpan.FromHours(3), 40),
            new("newest", _now.UtcDateTime - TimeSpan.FromHours(1), 40),
            new("middle", _now.UtcDateTime - TimeSpan.FromHours(2), 40),
        ];

        ReviewerOutputLayout.SelectDeletions(files, _now, TimeSpan.FromDays(14), 100).Should().BeEquivalentTo(["oldest"]);
    }

    [Fact]
    public void SelectDeletions_ShouldReturnEmptyWhenWithinLimits()
    {
        ReviewerOutputFile[] files = [new("a", _now.UtcDateTime - TimeSpan.FromHours(1), 100)];

        ReviewerOutputLayout.SelectDeletions(files, _now, TimeSpan.FromDays(14), 100).Should().BeEmpty();
        ReviewerOutputLayout.SelectDeletions([], _now, TimeSpan.FromDays(14), 100).Should().BeEmpty();
    }
}
