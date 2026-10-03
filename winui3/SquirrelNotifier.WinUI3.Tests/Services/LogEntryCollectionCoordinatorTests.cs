// <copyright file="LogEntryCollectionCoordinatorTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public sealed class LogEntryCollectionCoordinatorTests
{
    [Theory]
    [InlineData("同じログ行")]
    [InlineData("")]
    public void Add_WhenTextIsEqual_ShouldPreserveDistinctItems(string text)
    {
        LogEntryCollectionCoordinator coordinator = new();

        coordinator.Add(text);
        coordinator.Add(text);

        coordinator.Entries.Select(entry => entry.Text).Should().Equal(text, text);
        coordinator.Entries[0].Should().NotBeSameAs(coordinator.Entries[1]);
        coordinator.Entries[0].Equals(coordinator.Entries[1]).Should().BeFalse();
    }

    [Fact]
    public void Add_ShouldKeepEntriesInArrivalOrder()
    {
        LogEntryCollectionCoordinator coordinator = new();

        coordinator.Add("最初の行");
        coordinator.Add("次の行");

        coordinator.Entries.Select(entry => entry.Text).Should().Equal("最初の行", "次の行");
    }

    [Fact]
    public void Add_WhenMaximumIsExceeded_ShouldRemoveOldestEntry()
    {
        LogEntryCollectionCoordinator coordinator = new();
        for (int index = 0; index <= LogEntryCollectionCoordinator.MaxEntries; index++)
        {
            coordinator.Add($"ログ {index}");
        }

        coordinator.Entries.Should().HaveCount(LogEntryCollectionCoordinator.MaxEntries);
        coordinator.Entries[0].Text.Should().Be("ログ 1");
        coordinator.Entries[^1].Text.Should().Be($"ログ {LogEntryCollectionCoordinator.MaxEntries}");
    }
}
