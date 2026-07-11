// <copyright file="RateLimitAgentCatalogTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Linq;
using FluentAssertions;
using SquirrelNotifier.WinUI3.Models;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Models;

public class RateLimitAgentCatalogTests
{
    [Fact]
    public void All_ShouldContainClaudeCodeAndAgyAsAvailable()
    {
        RateLimitAgentCatalog.All.Should().Contain(a => a.Id == "claude-code" && a.IsAvailable);
        RateLimitAgentCatalog.All.Should().Contain(a => a.Id == "agy" && a.IsAvailable);
    }

    [Fact]
    public void All_ShouldContainCodexAsAvailable()
    {
        // App Server 経由の取得経路（#163）により利用可能
        RateLimitAgentCatalog.All.Should().Contain(a => a.Id == "codex" && a.IsAvailable);
    }

    [Fact]
    public void All_ShouldHaveUniqueIds()
    {
        RateLimitAgentCatalog.All.Select(a => a.Id).Should().OnlyHaveUniqueItems();
    }
}
