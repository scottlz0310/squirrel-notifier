// <copyright file="ClipboardServiceTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using FluentAssertions;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public sealed class ClipboardServiceTests
{
    [Theory]
    [InlineData("claude -p \"レビュー 50% & echo safe\"")]
    [InlineData("日本語の URL\nhttps://example.com/認証")]
    public void SetText_ShouldForwardExactText(string text)
    {
        string? receivedText = null;
        ClipboardService service = new(value => receivedText = value);

        service.SetText(text);

        receivedText.Should().Be(text);
    }

    [Fact]
    public void Constructor_ShouldThrowForNullDelegate()
    {
        Action action = () => _ = new ClipboardService(null!);

        action.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void SetText_ShouldPropagatePlatformFailure()
    {
        InvalidOperationException expected = new("クリップボードを利用できません。");
        ClipboardService service = new(_ => throw expected);

        Action action = () => service.SetText("text");

        action.Should().Throw<InvalidOperationException>().Which.Should().BeSameAs(expected);
    }
}
