// <copyright file="RateLimitAgentOptionTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Models;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Models;

public class RateLimitAgentOptionTests
{
    [Fact]
    public void Constructor_ShouldSetProperties()
    {
        var option = new RateLimitAgentOption("claude-code", "claude-code", isAvailable: true);

        option.Id.Should().Be("claude-code");
        option.DisplayName.Should().Be("claude-code");
        option.IsAvailable.Should().BeTrue();
        option.IsMonitored.Should().BeFalse();
    }

    [Fact]
    public void IsMonitored_WhenChanged_ShouldRaisePropertyChanged()
    {
        var option = new RateLimitAgentOption("agy", "agy (Antigravity CLI)", isAvailable: true);
        List<string?> raisedProperties = new();
        option.PropertyChanged += (_, e) => raisedProperties.Add(e.PropertyName);

        option.IsMonitored = true;

        raisedProperties.Should().Contain(nameof(RateLimitAgentOption.IsMonitored));
    }

    [Fact]
    public void IsMonitored_WhenSetToSameValue_ShouldNotRaisePropertyChanged()
    {
        var option = new RateLimitAgentOption("agy", "agy (Antigravity CLI)", isAvailable: true);
        bool raised = false;
        option.PropertyChanged += (_, _) => raised = true;

        option.IsMonitored = false;

        raised.Should().BeFalse();
    }
}
