// <copyright file="SubscriptionControlAvailabilityTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using FluentAssertions;
using SquirrelNotifier.WinUI3.Helpers;
using SquirrelNotifier.WinUI3.Services;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Helpers;

public class SubscriptionControlAvailabilityTests
{
    // SubscriptionState は internal のため、public なテストメソッドの引数には取れない。
    // 既存テスト（LauncherAgentCatalogTests 等）と同じく名前で受けて本体で解決する。
    [Theory]
    [InlineData("Running", false, true, false)]
    [InlineData("Stopped", true, false, false)]
    [InlineData("Starting", false, false, false)]
    [InlineData("Stopping", false, false, false)]
    [InlineData("Error", true, false, true)]
    public void For_ShouldMapStateToAvailability(
        string stateName, bool canStart, bool canStop, bool canRetry)
    {
        SubscriptionState state = Enum.Parse<SubscriptionState>(stateName);

        SubscriptionControlAvailability availability = SubscriptionControlAvailability.For(state);

        availability.Should().Be(new SubscriptionControlAvailability(canStart, canStop, canRetry));
    }

    [Theory]
    [InlineData("Running")]
    [InlineData("Stopped")]
    [InlineData("Starting")]
    [InlineData("Stopping")]
    [InlineData("Error")]
    public void For_ShouldNeverEnableStartAndStopTogether(string stateName)
    {
        SubscriptionState state = Enum.Parse<SubscriptionState>(stateName);

        SubscriptionControlAvailability availability = SubscriptionControlAvailability.For(state);

        (availability.CanStart && availability.CanStop).Should().BeFalse();
    }

    [Fact]
    public void For_ShouldDisableEverything_WhenStateIsUnknown()
    {
        SubscriptionControlAvailability availability = SubscriptionControlAvailability.For((SubscriptionState)(-1));

        availability.Should().Be(new SubscriptionControlAvailability(CanStart: false, CanStop: false, CanRetry: false));
    }
}
