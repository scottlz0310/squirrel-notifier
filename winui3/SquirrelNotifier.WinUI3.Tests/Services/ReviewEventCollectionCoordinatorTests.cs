// <copyright file="ReviewEventCollectionCoordinatorTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public sealed class ReviewEventCollectionCoordinatorTests
{
    [Fact]
    public void Add_ShouldInsertNewestEventFirst()
    {
        ReviewEventCollectionCoordinator coordinator = new();
        ReviewEvent first = CreateReviewEvent("first");
        ReviewEvent second = CreateReviewEvent("second");

        coordinator.Add(first);
        coordinator.Add(second);

        coordinator.Events.Should().Equal(second, first);
    }

    [Fact]
    public void Add_ShouldRejectNullEvent()
    {
        ReviewEventCollectionCoordinator coordinator = new();

        Action act = () => coordinator.Add(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Add_WhenMaximumIsExceeded_ShouldEvictOldestEvent()
    {
        ReviewEventCollectionCoordinator coordinator = new();
        for (int index = 0; index <= ReviewEventCollectionCoordinator.MaxEvents; index++)
        {
            coordinator.Add(CreateReviewEvent($"event-{index}"));
        }

        coordinator.Events.Should().HaveCount(ReviewEventCollectionCoordinator.MaxEvents);
        coordinator.Events[0].EventId.Should().Be($"event-{ReviewEventCollectionCoordinator.MaxEvents}");
        coordinator.Events[^1].EventId.Should().Be("event-1");
    }

    [Fact]
    public void Add_WhenMaximumIsExceeded_ShouldReturnEvictedEvent()
    {
        ReviewEventCollectionCoordinator coordinator = new();
        for (int index = 0; index < ReviewEventCollectionCoordinator.MaxEvents; index++)
        {
            coordinator.Add(CreateReviewEvent($"event-{index}"));
        }

        ReviewEvent? evictedEvent = coordinator.Add(CreateReviewEvent("new-event"));

        evictedEvent.Should().NotBeNull();
        evictedEvent!.EventId.Should().Be("event-0");
    }

    [Fact]
    public void RemoveByEventId_ShouldRemoveMatchingEvent()
    {
        ReviewEventCollectionCoordinator coordinator = new();
        ReviewEvent retained = CreateReviewEvent("retained");
        coordinator.Add(CreateReviewEvent("removed"));
        coordinator.Add(retained);

        bool removed = coordinator.RemoveByEventId("removed");

        removed.Should().BeTrue();
        coordinator.Events.Should().ContainSingle().Which.Should().Be(retained);
    }

    [Fact]
    public void Remove_ShouldRemoveTheSpecifiedEvent()
    {
        ReviewEventCollectionCoordinator coordinator = new();
        ReviewEvent removed = CreateReviewEvent("removed");
        coordinator.Add(removed);

        bool result = coordinator.Remove(removed);

        result.Should().BeTrue();
        coordinator.Events.Should().BeEmpty();
    }

    [Fact]
    public void Remove_ShouldRejectNullEvent()
    {
        ReviewEventCollectionCoordinator coordinator = new();

        Action act = () => coordinator.Remove(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void RemoveByEventId_ShouldIgnoreBlankEventId(string eventId)
    {
        ReviewEventCollectionCoordinator coordinator = new();
        coordinator.Add(CreateReviewEvent("existing"));

        bool removed = coordinator.RemoveByEventId(eventId);

        removed.Should().BeFalse();
        coordinator.Events.Should().ContainSingle();
    }

    [Fact]
    public void RemoveByEventId_WhenEventDoesNotExist_ShouldLeaveCollectionUnchanged()
    {
        ReviewEventCollectionCoordinator coordinator = new();
        ReviewEvent reviewEvent = CreateReviewEvent("existing");
        coordinator.Add(reviewEvent);

        bool removed = coordinator.RemoveByEventId("missing");

        removed.Should().BeFalse();
        coordinator.Events.Should().ContainSingle().Which.Should().Be(reviewEvent);
    }

    private static ReviewEvent CreateReviewEvent(string eventId) => new()
    {
        EventId = eventId,
        Repository = "owner/repo",
        PrNumber = 42,
        PrUrl = "https://github.com/owner/repo/pull/42",
        Reason = "opened",
        Message = "Review requested",
    };
}
