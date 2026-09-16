// <copyright file="PendingReviewStartQueueTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Tests.Services;

// PendingReviewStartChange は internal のため、InlineData では名前で受け取り Enum.Parse で解決する
public sealed class PendingReviewStartQueueTests
{
    private static readonly DateTime _baseTime = new(2026, 9, 16, 12, 0, 0, DateTimeKind.Local);

    [Theory]
    [InlineData("owner/other", 42, 1, "Added", "evt_1", 2)]
    [InlineData("owner/repo", 43, 1, "Added", "evt_1", 2)]
    [InlineData("owner/repo", 42, 1, "Replaced", "evt_2", 1)]
    [InlineData("OWNER/Repo", 42, 1, "Replaced", "evt_2", 1)]
    [InlineData("owner/repo", 42, 0, "Replaced", "evt_2", 1)]
    [InlineData("owner/repo", 42, -1, "Ignored", "evt_1", 1)]
    public void AddOrReplace_ShouldKeepOneEventPerPullRequest(
        string repository,
        int prNumber,
        int receivedOffsetSeconds,
        string expectedChange,
        string expectedFirstEventId,
        int expectedCount)
    {
        PendingReviewStartQueue queue = new();
        queue.AddOrReplace(CreateReviewEvent("evt_1", "owner/repo", 42, _baseTime));

        PendingReviewStartChange change = queue.AddOrReplace(
            CreateReviewEvent("evt_2", repository, prNumber, _baseTime.AddSeconds(receivedOffsetSeconds)));

        change.Should().Be(Enum.Parse<PendingReviewStartChange>(expectedChange));
        queue.Count.Should().Be(expectedCount);
        queue.Peek()!.EventId.Should().Be(expectedFirstEventId);
    }

    [Fact]
    public void AddOrReplace_ShouldIgnoreSameInstance()
    {
        PendingReviewStartQueue queue = new();
        ReviewEvent reviewEvent = CreateReviewEvent("evt_1", "owner/repo", 42, _baseTime);
        queue.AddOrReplace(reviewEvent);

        queue.AddOrReplace(reviewEvent).Should().Be(PendingReviewStartChange.Ignored);

        queue.Count.Should().Be(1);
    }

    [Fact]
    public void AddOrReplace_ShouldKeepPositionOfReplacedPullRequest()
    {
        PendingReviewStartQueue queue = new();
        queue.AddOrReplace(CreateReviewEvent("evt_1", "owner/repo", 42, _baseTime));
        queue.AddOrReplace(CreateReviewEvent("evt_2", "owner/repo", 43, _baseTime.AddSeconds(1)));

        queue.AddOrReplace(CreateReviewEvent("evt_3", "owner/repo", 42, _baseTime.AddSeconds(2)));

        queue.Peek()!.EventId.Should().Be("evt_3");
        queue.Remove(queue.Peek()!).Should().BeTrue();
        queue.Peek()!.EventId.Should().Be("evt_2");
    }

    [Fact]
    public void Peek_ShouldReturnNull_WhenEmpty()
    {
        new PendingReviewStartQueue().Peek().Should().BeNull();
    }

    [Theory]
    [InlineData(true, true, 0)]
    [InlineData(false, false, 1)]
    public void Remove_ShouldRemoveOnlySameInstance(bool removeSameInstance, bool expectedRemoved, int expectedCount)
    {
        PendingReviewStartQueue queue = new();
        ReviewEvent pending = CreateReviewEvent("evt_1", "owner/repo", 42, _baseTime);
        queue.AddOrReplace(pending);
        ReviewEvent target = removeSameInstance ? pending : CreateReviewEvent("evt_1", "owner/repo", 42, _baseTime);

        queue.Remove(target).Should().Be(expectedRemoved);

        queue.Count.Should().Be(expectedCount);
    }

    [Theory]
    [InlineData("owner/repo", 42, true, 0)]
    [InlineData("Owner/Repo", 42, true, 0)]
    [InlineData("owner/repo", 43, false, 1)]
    public void RemovePullRequest_ShouldRemoveAnyEventOfSamePullRequest(
        string repository,
        int prNumber,
        bool expectedRemoved,
        int expectedCount)
    {
        PendingReviewStartQueue queue = new();
        queue.AddOrReplace(CreateReviewEvent("evt_1", "owner/repo", 42, _baseTime));

        queue.RemovePullRequest(CreateReviewEvent("evt_2", repository, prNumber, _baseTime)).Should().Be(expectedRemoved);

        queue.Count.Should().Be(expectedCount);
    }

    [Fact]
    public void Methods_ShouldRejectNullEvent()
    {
        PendingReviewStartQueue queue = new();

        Action add = () => queue.AddOrReplace(null!);
        Action remove = () => queue.Remove(null!);
        Action removePullRequest = () => queue.RemovePullRequest(null!);

        add.Should().Throw<ArgumentNullException>();
        remove.Should().Throw<ArgumentNullException>();
        removePullRequest.Should().Throw<ArgumentNullException>();
    }

    private static ReviewEvent CreateReviewEvent(string eventId, string repository, int prNumber, DateTime receivedTime)
        => new()
        {
            EventId = eventId,
            Repository = repository,
            PrNumber = prNumber,
            Reason = "opened",
            ReceivedTime = receivedTime,
        };
}
