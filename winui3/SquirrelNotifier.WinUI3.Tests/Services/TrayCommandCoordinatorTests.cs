// <copyright file="TrayCommandCoordinatorTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using SquirrelNotifier.WinUI3.Helpers;
using SquirrelNotifier.WinUI3.Services;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public class TrayCommandCoordinatorTests
{
    [Theory]
    [InlineData("Open", "open")]
    [InlineData("Start", "start")]
    [InlineData("Stop", "stop")]
    [InlineData("CheckForUpdates", "check-for-updates")]
    [InlineData("Exit", "exit")]
    public async Task ExecuteAsync_ShouldDispatchSelectedCommand(string commandName, string expectedAction)
    {
        List<string> actions = new();
        TrayCommandCoordinator coordinator = CreateCoordinator(actions);

        await coordinator.ExecuteAsync(Enum.Parse<TrayMenuCommand>(commandName));

        actions.Should().Equal(expectedAction);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Separator")]
    public async Task ExecuteAsync_ShouldIgnoreNonCommandSelection(string? commandName)
    {
        List<string> actions = new();
        TrayCommandCoordinator coordinator = CreateCoordinator(actions);

        TrayMenuCommand? command = commandName is null
            ? null
            : Enum.Parse<TrayMenuCommand>(commandName);
        await coordinator.ExecuteAsync(command);

        actions.Should().BeEmpty();
    }

    private static TrayCommandCoordinator CreateCoordinator(List<string> actions)
    {
        return new TrayCommandCoordinator(
            () => actions.Add("open"),
            () => actions.Add("start"),
            () => AddActionAsync(actions, "stop"),
            () => AddActionAsync(actions, "check-for-updates"),
            () => actions.Add("exit"));
    }

    private static async Task AddActionAsync(List<string> actions, string action)
    {
        await Task.Yield();
        actions.Add(action);
    }
}
