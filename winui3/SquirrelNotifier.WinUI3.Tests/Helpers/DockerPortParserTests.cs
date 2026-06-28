// <copyright file="DockerPortParserTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Helpers;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Helpers;

public class DockerPortParserTests
{
    [Theory]
    [InlineData("0.0.0.0:8080->8080/tcp", "http://localhost:8080/mcp/thread-owl")]
    [InlineData("0.0.0.0:3000->3000/tcp", "http://localhost:3000/mcp/thread-owl")]
    [InlineData(":::8080->8080/tcp", "http://localhost:8080/mcp/thread-owl")]
    public void ParseGatewayUrls_SinglePort_ReturnsCorrectUrl(string dockerPsOutput, string expectedUrl)
    {
        IReadOnlyList<string> result = DockerPortParser.ParseGatewayUrls(dockerPsOutput);

        result.Should().ContainSingle().Which.Should().Be(expectedUrl);
    }

    [Fact]
    public void ParseGatewayUrls_MultipleContainerLines_ReturnsAllUrls()
    {
        string dockerPsOutput = "0.0.0.0:8080->8080/tcp\n0.0.0.0:8081->8080/tcp";

        IReadOnlyList<string> result = DockerPortParser.ParseGatewayUrls(dockerPsOutput);

        result.Should().HaveCount(2);
        result.Should().Contain("http://localhost:8080/mcp/thread-owl");
        result.Should().Contain("http://localhost:8081/mcp/thread-owl");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no port mapping here")]
    [InlineData("8080/tcp")]
    public void ParseGatewayUrls_NoMatchingPorts_ReturnsEmpty(string dockerPsOutput)
    {
        IReadOnlyList<string> result = DockerPortParser.ParseGatewayUrls(dockerPsOutput);

        result.Should().BeEmpty();
    }

    [Fact]
    public void ParseGatewayUrls_DefaultRoute_IsThreadOwlMcpRoute()
    {
        DockerPortParser.DefaultMcpRoute.Should().Be("/mcp/thread-owl");
    }

    [Fact]
    public void ParseGatewayUrls_CustomRoute_AppendsCorrectly()
    {
        string dockerPsOutput = "0.0.0.0:8080->8080/tcp";

        IReadOnlyList<string> result = DockerPortParser.ParseGatewayUrls(dockerPsOutput, "/mcp/custom-service");

        result.Should().ContainSingle().Which.Should().Be("http://localhost:8080/mcp/custom-service");
    }

    [Theory]
    [InlineData("0.0.0.0:8080->8080/tcp", "http://localhost:8080")]
    [InlineData(":::3000->3000/tcp", "http://localhost:3000")]
    public void ParseGatewayBaseUrls_SinglePort_ReturnsBaseWithoutRoute(string dockerPsOutput, string expected)
    {
        IReadOnlyList<string> result = DockerPortParser.ParseGatewayBaseUrls(dockerPsOutput);

        result.Should().ContainSingle().Which.Should().Be(expected);
    }

    [Fact]
    public void ParseGatewayBaseUrls_MultipleContainerLines_ReturnsAllBaseUrls()
    {
        string dockerPsOutput = "0.0.0.0:8080->8080/tcp\n0.0.0.0:8081->8080/tcp";

        IReadOnlyList<string> result = DockerPortParser.ParseGatewayBaseUrls(dockerPsOutput);

        result.Should().HaveCount(2);
        result.Should().Contain("http://localhost:8080");
        result.Should().Contain("http://localhost:8081");
    }

    [Theory]
    [InlineData("http://localhost:8080", "/mcp/thread-owl", "http://localhost:8080/mcp/thread-owl")]
    [InlineData("http://localhost:8080", "mcp/thread-owl", "http://localhost:8080/mcp/thread-owl")] // 先頭スラッシュ補完
    [InlineData("http://localhost:8080", "/mcp/thread-owl/", "http://localhost:8080/mcp/thread-owl")] // 末尾スラッシュ除去
    [InlineData("http://localhost:8080/", "/mcp/thread-owl", "http://localhost:8080/mcp/thread-owl")] // base 末尾スラッシュ除去
    [InlineData("http://localhost:8080", "  /mcp/thread-owl  ", "http://localhost:8080/mcp/thread-owl")] // 前後空白除去
    [InlineData("http://localhost:8080", "", "http://localhost:8080")] // 空 route は base のみ
    [InlineData("http://localhost:8080", "   ", "http://localhost:8080")] // 空白のみ route は base のみ
    [InlineData("http://localhost:8080", "/", "http://localhost:8080")] // ルートのみ route は base のみ
    [InlineData("http://localhost:8080", null, "http://localhost:8080")] // null route は base のみ
    public void CombineRoute_NormalizesAndCombines(string baseUrl, string? route, string expected)
    {
        DockerPortParser.CombineRoute(baseUrl, route).Should().Be(expected);
    }
}
