// <copyright file="TrayMenuLayoutTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using SquirrelNotifier.WinUI3.Helpers;
using SquirrelNotifier.WinUI3.Services;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Helpers;

public class TrayMenuLayoutTests
{
    // SubscriptionState は internal のため名前で受けて本体で解決する
    private static IReadOnlyList<TrayMenuEntry> Build(string stateName)
        => TrayMenuLayout.Build(Enum.Parse<SubscriptionState>(stateName));

    private static TrayMenuEntry Entry(IReadOnlyList<TrayMenuEntry> entries, TrayMenuCommand command)
        => entries.Single(e => e.Command == command);

    [Theory]
    [InlineData("Running", false, true)]
    [InlineData("Stopped", true, false)]
    [InlineData("Starting", false, false)]
    [InlineData("Stopping", false, false)]
    [InlineData("Error", true, false)]
    public void Build_ShouldReflectSubscriptionState(string stateName, bool startEnabled, bool stopEnabled)
    {
        IReadOnlyList<TrayMenuEntry> entries = Build(stateName);

        Entry(entries, TrayMenuCommand.Start).IsEnabled.Should().Be(startEnabled);
        Entry(entries, TrayMenuCommand.Stop).IsEnabled.Should().Be(stopEnabled);
    }

    [Theory]
    [InlineData("Running")]
    [InlineData("Stopped")]
    [InlineData("Starting")]
    [InlineData("Stopping")]
    [InlineData("Error")]
    public void Build_ShouldKeepStateIndependentItemsAlwaysEnabled(string stateName)
    {
        IReadOnlyList<TrayMenuEntry> entries = Build(stateName);

        Entry(entries, TrayMenuCommand.Open).IsEnabled.Should().BeTrue();
        Entry(entries, TrayMenuCommand.CheckForUpdates).IsEnabled.Should().BeTrue();
        Entry(entries, TrayMenuCommand.Exit).IsEnabled.Should().BeTrue();
    }

    [Fact]
    public void Build_ShouldKeepItemOrderAndSeparator()
    {
        IReadOnlyList<TrayMenuEntry> entries = Build("Stopped");

        entries.Select(e => e.Command).Should().Equal(
            TrayMenuCommand.Open,
            TrayMenuCommand.Start,
            TrayMenuCommand.Stop,
            TrayMenuCommand.CheckForUpdates,
            TrayMenuCommand.Separator,
            TrayMenuCommand.Exit);

        entries.Single(e => e.IsSeparator).Text.Should().BeEmpty();
    }

    [Fact]
    public void Build_ShouldGiveEveryCommandItemANonEmptyLabel()
    {
        IReadOnlyList<TrayMenuEntry> entries = Build("Running");

        entries.Where(e => !e.IsSeparator).Should().OnlyContain(e => !string.IsNullOrWhiteSpace(e.Text));
    }

    [Fact]
    public void Build_ShouldUseDistinctCommandIds()
    {
        IReadOnlyList<TrayMenuEntry> entries = Build("Running");

        // TrackPopupMenuEx は選択された項目を ID で返すため、重複していると誤動作する
        entries.Select(e => e.Command).Should().OnlyHaveUniqueItems();
    }
}
