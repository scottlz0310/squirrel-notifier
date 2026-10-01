// <copyright file="ReviewStatusDocumentBuilderTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Helpers;
using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Tests.Helpers;

public sealed class ReviewStatusDocumentBuilderTests
{
    private static readonly DateTimeOffset _now = new(2026, 10, 1, 3, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("scottlz0310/Mcp-Docker", 340, "scottlz0310/mcp-docker#340")]
    [InlineData("  Owner/Repo  ", 7, "owner/repo#7")]
    [InlineData("owner/repo", 1, "owner/repo#1")]
    public void CreateKey_ShouldLowercaseAndTrimRepository(string repository, int prNumber, string expected)
    {
        ReviewStatusDocumentBuilder.CreateKey(repository, prNumber).Should().Be(expected);
    }

    [Fact]
    public void Build_ShouldOrderRunningFirstThenWaitingByHoldAndAssignQueuePositionToHeldOnly()
    {
        ReviewStatusEntry[] entries =
        [
            Waiting("owner/busy", 3, hold: ReviewHoldKind.Busy, holdSince: _now.AddMinutes(-3)),
            Waiting("owner/ci", 2, hold: ReviewHoldKind.CiPending, holdSince: _now.AddMinutes(-5)),
            Waiting("owner/evaluating", 4, receivedAt: _now.AddMinutes(-1)),
            Running("owner/running", 1, startedAt: _now.AddMinutes(-9)),
        ];

        ReviewStatusDocument document = Build(entries);

        document.Items.Select(static item => item.Key).Should().Equal(
            "owner/running#1",
            "owner/ci#2",
            "owner/busy#3",
            "owner/evaluating#4");
        document.Items.Select(static item => item.QueuePosition).Should().Equal(null, 1, 2, null);
        document.Items.Select(static item => item.State).Should().Equal("running", "waiting", "waiting", "waiting");
    }

    [Theory]
    [InlineData("Busy", true, "busy")]
    [InlineData("CiPending", true, "ciPending")]
    [InlineData("AutoPause", true, "autoPause")]
    [InlineData("Busy", false, "busy")]
    [InlineData("", false, "manual")]
    [InlineData("", true, null)]
    public void Build_ShouldDescribeWhyAWaitingReviewIsHeld(string hold, bool autoStartEnabled, string? expectedHoldReason)
    {
        ReviewHoldKind? kind = hold.Length == 0 ? null : Enum.Parse<ReviewHoldKind>(hold);
        ReviewStatusEntry entry = Waiting("owner/repo", 5, hold: kind, holdSince: kind is null ? null : _now.AddMinutes(-2));

        ReviewStatusItem item = Build([entry], autoStartEnabled).Items.Single();

        item.HoldReason.Should().Be(expectedHoldReason);
        if (expectedHoldReason is null)
        {
            item.HoldSince.Should().BeNull();
        }
        else
        {
            item.HoldSince.Should().Be((kind is null ? entry.ReceivedAt : entry.HoldSince!.Value).UtcDateTime);
        }
    }

    [Fact]
    public void Build_ShouldNotDescribeHoldForRunningOrFinishedReviews_EvenWhenAutoStartIsOff()
    {
        ReviewStatusDocument document = Build(
            [Running("owner/running", 1), Finished("owner/finished", 2, _now.AddMinutes(-1))],
            autoStartEnabled: false);

        document.Items.Single().HoldReason.Should().BeNull();
        document.Recent.Single().HoldReason.Should().BeNull();
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    public void Build_ShouldCountRunningReviewsAndFixMaxConcurrency(int running, int expectedActive)
    {
        ReviewStatusEntry[] entries =
        [
            .. Enumerable.Range(1, running).Select(number => Running("owner/running", number)),
            Waiting("owner/waiting", 99),
        ];

        ReviewStatusDocument document = Build(entries);

        document.Concurrency.Active.Should().Be(expectedActive);
        document.Concurrency.Max.Should().Be(ReviewStatusDocumentBuilder.MaxConcurrent).And.Be(1);
    }

    [Fact]
    public void Build_ShouldKeepRecentWithinRetentionAndLimitNewestFirst()
    {
        DateTimeOffset cutoff = _now - ReviewStatusDocumentBuilder.RecentRetention;
        List<ReviewStatusEntry> entries =
        [
            Finished("owner/expired", 1, cutoff.AddSeconds(-1)),
            Finished("owner/boundary", 2, cutoff),
        ];
        entries.AddRange(Enumerable.Range(100, ReviewStatusDocumentBuilder.RecentLimit + 5)
            .Select(number => Finished("owner/repo", number, _now.AddMinutes(-(number - 99)))));

        ReviewStatusDocument document = Build(entries);

        document.Recent.Should().HaveCount(ReviewStatusDocumentBuilder.RecentLimit);
        document.Recent.Select(static item => item.PrNumber).First().Should().Be(100);
        document.Recent.Select(static item => item.FinishedAt).Should().BeInDescendingOrder();
        document.Recent.Select(static item => item.PrNumber).Should().NotContain([1, 2]);
    }

    [Fact]
    public void Build_ShouldIncludeBoundaryOfRetention_WhenWithinLimit()
    {
        DateTimeOffset cutoff = _now - ReviewStatusDocumentBuilder.RecentRetention;

        ReviewStatusDocument document = Build(
            [Finished("owner/expired", 1, cutoff.AddSeconds(-1)), Finished("owner/boundary", 2, cutoff)]);

        document.Recent.Select(static item => item.PrNumber).Should().Equal(2);
    }

    [Theory]
    [InlineData("Completed", "completed", 0)]
    [InlineData("Failed", "failed", 1)]
    public void Build_ShouldDescribeOutcomeAndExitCodeOfFinishedReview(string outcome, string expectedOutcome, int exitCode)
    {
        ReviewStatusEntry entry = Finished("owner/repo", 8, _now.AddMinutes(-4)) with
        {
            Outcome = Enum.Parse<ReviewerOutcome>(outcome),
            ExitCode = exitCode,
        };

        ReviewStatusItem item = Build([entry]).Recent.Single();

        item.State.Should().Be("finished");
        item.Outcome.Should().Be(expectedOutcome);
        item.ExitCode.Should().Be(exitCode);
        item.StartedAt.Should().Be(entry.StartedAt!.Value.UtcDateTime);
        item.FinishedAt.Should().Be(entry.FinishedAt!.Value.UtcDateTime);
    }

    [Fact]
    public void Build_ShouldCarryEnvironmentAndSchemaVersion()
    {
        ReviewStatusDocument document = Build([]);

        document.SchemaVersion.Should().Be(1);
        document.UpdatedAt.Should().Be(_now.UtcDateTime);
        document.App.Version.Should().Be("0.15.0");
        document.App.StartedAt.Should().Be(_now.AddHours(-1).UtcDateTime);
        document.Subscription.State.Should().Be("running");
        document.Subscription.Since.Should().Be(_now.AddMinutes(-30).UtcDateTime);
        document.Items.Should().BeEmpty();
        document.Recent.Should().BeEmpty();
    }

    private static ReviewStatusDocument Build(IEnumerable<ReviewStatusEntry> entries, bool autoStartEnabled = true)
        => ReviewStatusDocumentBuilder.Build(
            entries,
            new ReviewStatusEnvironment("0.15.0", _now.AddHours(-1), "running", _now.AddMinutes(-30), autoStartEnabled),
            _now);

    private static ReviewStatusEntry Waiting(
        string repository,
        int prNumber,
        ReviewHoldKind? hold = null,
        DateTimeOffset? holdSince = null,
        DateTimeOffset? receivedAt = null)
        => new(
            ReviewStatusDocumentBuilder.CreateKey(repository, prNumber),
            repository,
            prNumber,
            1,
            $"evt-{prNumber}",
            "opened",
            ReviewStatusState.Waiting,
            receivedAt ?? _now.AddMinutes(-20),
            Hold: hold,
            HoldSince: holdSince);

    private static ReviewStatusEntry Running(string repository, int prNumber, DateTimeOffset? startedAt = null)
        => new(
            ReviewStatusDocumentBuilder.CreateKey(repository, prNumber),
            repository,
            prNumber,
            1,
            $"evt-{prNumber}",
            "opened",
            ReviewStatusState.Running,
            _now.AddMinutes(-15),
            StartedAt: startedAt ?? _now.AddMinutes(-10),
            Agent: "codex");

    private static ReviewStatusEntry Finished(string repository, int prNumber, DateTimeOffset finishedAt)
        => new(
            ReviewStatusDocumentBuilder.CreateKey(repository, prNumber),
            repository,
            prNumber,
            1,
            $"evt-{prNumber}",
            "opened",
            ReviewStatusState.Finished,
            finishedAt.AddMinutes(-15),
            StartedAt: finishedAt.AddMinutes(-10),
            FinishedAt: finishedAt,
            Agent: "codex",
            Outcome: ReviewerOutcome.Completed,
            ExitCode: 0);
}
