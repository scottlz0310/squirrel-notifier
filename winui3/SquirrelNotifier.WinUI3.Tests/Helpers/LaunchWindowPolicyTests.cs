// <copyright file="LaunchWindowPolicyTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using Microsoft.Windows.AppLifecycle;
using SquirrelNotifier.WinUI3.Helpers;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Helpers;

public class LaunchWindowPolicyTests
{
    [Theory]
    [InlineData((int)ExtendedActivationKind.Launch, "app.exe", true)]
    [InlineData((int)ExtendedActivationKind.Launch, "app.exe --tray", false)]
    [InlineData((int)ExtendedActivationKind.Launch, "app.exe -t", false)]
    [InlineData((int)ExtendedActivationKind.StartupTask, "app.exe", false)]
    public void ShouldShowWindow_ShouldStayInTrayForAutoStart(int kind, string commandLine, bool expected)
    {
        LaunchWindowPolicy.ShouldShowWindow(commandLine.Split(' '), (ExtendedActivationKind)kind).Should().Be(expected);
    }

    [Theory]
    [InlineData((int)ExtendedActivationKind.Launch, true)]
    [InlineData((int)ExtendedActivationKind.StartupTask, false)]
    public void ShouldBringToFront_ShouldIgnoreRedirectedStartupTask(int kind, bool expected)
    {
        LaunchWindowPolicy.ShouldBringToFront((ExtendedActivationKind)kind).Should().Be(expected);
    }
}
