// <copyright file="LauncherSessionConflictPolicyTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Helpers;

namespace SquirrelNotifier.WinUI3.Tests.Helpers;

public class LauncherSessionConflictPolicyTests
{
    [Theory]
    [InlineData("", "failed to initialize thread persistence: thread-store conflict: thread {id} already has an active writer", true)]
    [InlineData(" \n", "ERROR failed to initialize thread persistence:\nthread-store conflict: thread {id} already has an active writer", true)]
    [InlineData("output", "failed to initialize thread persistence: thread-store conflict: thread {id} already has an active writer", false)]
    [InlineData("", "thread {id} already has an active writer", false)]
    [InlineData("", "authentication failed", false)]
    [InlineData("", "failed to initialize thread persistence: thread-store conflict: thread 11111111-2222-3333-4444-555555555555 already has an active writer", false)]
    [InlineData("", "failed to initialize thread persistence: thread-store conflict: thread zzzzzzzz-zzzz-zzzz-zzzz-zzzzzzzzzzzz already has an active writer", false)]
    public void IsStartupConflict_ShouldRequireMatchingInitializationFailure(string stdout, string stderr, bool expected)
    {
        Guid sessionId = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
        LauncherSessionConflictPolicy.IsStartupConflict(stdout, stderr.Replace("{id}", sessionId.ToString("D"), StringComparison.Ordinal), sessionId)
            .Should().Be(expected);
    }
}
