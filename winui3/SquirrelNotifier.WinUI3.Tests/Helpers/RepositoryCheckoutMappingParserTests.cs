// <copyright file="RepositoryCheckoutMappingParserTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using SquirrelNotifier.WinUI3.Helpers;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Helpers;

public class RepositoryCheckoutMappingParserTests
{
    [Fact]
    public void Parse_ShouldReadMultipleMappingsAndIgnoreRepositoryCase()
    {
        string firstPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "checkout-a"));
        string secondPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "checkout-b"));

        Dictionary<string, string> mappings = RepositoryCheckoutMappingParser.Parse(
            $"Owner/Repo={firstPath}{Environment.NewLine}{Environment.NewLine}other/repo={secondPath}");

        mappings.Should().HaveCount(2);
        mappings["owner/repo"].Should().Be(firstPath);
        mappings["OTHER/REPO"].Should().Be(secondPath);
    }

    [Theory]
    [InlineData("owner/repo")]
    [InlineData("owner=C:\\src\\repo")]
    [InlineData("owner/repo=relative-path")]
    [InlineData("owner/repo=C:\\src\\one\nOWNER/REPO=C:\\src\\two")]
    public void Parse_ShouldRejectInvalidMapping(string text)
    {
        Action act = () => RepositoryCheckoutMappingParser.Parse(text);

        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void Format_ShouldSortMappingsByRepository()
    {
        var mappings = new Dictionary<string, string>
        {
            ["z-owner/z-repo"] = @"C:\src\z",
            ["a-owner/a-repo"] = @"C:\src\a",
        };

        RepositoryCheckoutMappingParser.Format(mappings).Should().Be(
            $"a-owner/a-repo=C:\\src\\a{Environment.NewLine}z-owner/z-repo=C:\\src\\z");
    }
}
