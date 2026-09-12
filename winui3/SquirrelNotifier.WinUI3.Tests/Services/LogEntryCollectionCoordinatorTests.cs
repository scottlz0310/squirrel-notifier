// <copyright file="LogEntryCollectionCoordinatorTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public sealed class LogEntryCollectionCoordinatorTests
{
    [Fact]
    public void Add_ShouldKeepEntriesInArrivalOrder()
    {
        LogEntryCollectionCoordinator coordinator = new();

        coordinator.Add("最初の行");
        coordinator.Add("次の行");

        coordinator.Entries.Should().Equal("最初の行", "次の行");
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
        coordinator.Entries[0].Should().Be("ログ 1");
        coordinator.Entries[^1].Should().Be($"ログ {LogEntryCollectionCoordinator.MaxEntries}");
    }
}
