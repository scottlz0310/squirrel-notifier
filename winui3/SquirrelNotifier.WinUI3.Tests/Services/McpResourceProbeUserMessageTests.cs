// <copyright file="McpResourceProbeUserMessageTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using ModelContextProtocol;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Tests.Services;

/// <summary>
/// 例外からユーザー向けメッセージへの分類を検証する。protocol を一切話さないため、
/// テストサーバーではなく例外そのものを入力にする.
/// </summary>
public sealed class McpResourceProbeUserMessageTests
{
    [Fact]
    public void GetUserMessage_WithCancellationException_ReturnsCancelMessage()
    {
        string msg = McpResourceProbe.GetUserMessage(new OperationCanceledException("cancelled"));
        msg.Should().Contain("キャンセル");
    }

    [Fact]
    public void GetUserMessage_WithUnsupportedProtocolVersionException_ReturnsProtocolMessage()
    {
        var ex = new UnsupportedProtocolVersionException(
            McpResourceProbe.PinnedProtocolVersion,
            ["2025-11-25"],
            "unsupported");

        string msg = McpResourceProbe.GetUserMessage(ex);

        msg.Should().Contain(McpResourceProbe.PinnedProtocolVersion);
        msg.Should().Contain("ネゴシエーション");
    }

    [Fact]
    public void GetUserMessage_WithMcpExceptionNamingPinnedVersion_ReturnsProtocolMessage()
    {
        var ex = new McpException(
            $"The server does not support the requested protocol version '{McpResourceProbe.PinnedProtocolVersion}'. Server-supported versions: 2025-11-25.");

        string msg = McpResourceProbe.GetUserMessage(ex);

        msg.Should().Contain("ネゴシエーション");
    }

    [Fact]
    public void GetUserMessage_WithUnrelatedMcpException_DoesNotClaimProtocolMismatch()
    {
        string msg = McpResourceProbe.GetUserMessage(new McpException("Tool execution failed"));

        msg.Should().NotContain(McpResourceProbe.PinnedProtocolVersion);
        msg.Should().Contain("予期しないエラー");
    }

    [Theory]
    [InlineData("401 Unauthorized")]
    [InlineData("Unauthorized access")]
    [InlineData("Forbidden")]
    public void GetUserMessage_WithAuthError_ReturnsAuthMessage(string rawError)
    {
        string msg = McpResourceProbe.GetUserMessage(new InvalidOperationException(rawError));
        msg.Should().Contain("認証エラー");
    }

    [Theory]
    [InlineData("404 Not Found")]
    [InlineData("Endpoint Not Found")]
    public void GetUserMessage_WithNotFoundError_ReturnsNotFoundMessage(string rawError)
    {
        string msg = McpResourceProbe.GetUserMessage(new InvalidOperationException(rawError));
        msg.Should().Contain("404");
    }

    [Theory]
    [InlineData("Connection refused")]
    [InlineData("ECONNREFUSED")]
    public void GetUserMessage_WithConnectionRefused_ReturnsConnectionMessage(string rawError)
    {
        string msg = McpResourceProbe.GetUserMessage(new HttpRequestException(rawError));
        msg.Should().Contain("接続に失敗");
    }

    [Fact]
    public void GetUserMessage_WithUnknownError_ReturnsGenericMessage()
    {
        string msg = McpResourceProbe.GetUserMessage(new InvalidOperationException("something weird"));
        msg.Should().Contain("予期しないエラー");
    }
}
