// <copyright file="UpdateCarryOverStoreTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Text.Json;
using FluentAssertions;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public sealed class UpdateCarryOverStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"UpdateCarryOverTests_{Guid.NewGuid()}");

    private string FilePath => Path.Combine(_directory, "update-carryover.json");

    [Fact]
    public void Take_ShouldReturnSavedEventsOnceInOrder()
    {
        var store = new UpdateCarryOverStore(_directory);
        var first = new ReviewEvent
        {
            EventId = "a",
            Repository = "owner/repo",
            PrNumber = 1,
            PrUrl = "https://github.com/owner/repo/pull/1",
            Reason = "opened",
            Source = "thread-owl",
            Message = "確認してください",
            ReceivedTime = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Local),
        };
        var second = new ReviewEvent { EventId = "b", Repository = "owner/repo", PrNumber = 2 };

        store.Save([first, second]);
        IReadOnlyList<ReviewEvent> taken = new UpdateCarryOverStore(_directory).Take();

        taken.Should().BeEquivalentTo([first, second], options => options.WithStrictOrdering());
        File.Exists(FilePath).Should().BeFalse();
        store.Take().Should().BeEmpty();
    }

    [Fact]
    public void Save_ShouldRemoveStaleFileWhenNothingIsHeld()
    {
        var store = new UpdateCarryOverStore(_directory);
        store.Save([new ReviewEvent { EventId = "a" }]);

        store.Save([]);

        File.Exists(FilePath).Should().BeFalse();
    }

    [Fact]
    public void Take_ShouldDeleteUnreadableFile()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, "broken");
        var store = new UpdateCarryOverStore(_directory);

        Action act = () => store.Take();

        act.Should().Throw<JsonException>();
        File.Exists(FilePath).Should().BeFalse();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
