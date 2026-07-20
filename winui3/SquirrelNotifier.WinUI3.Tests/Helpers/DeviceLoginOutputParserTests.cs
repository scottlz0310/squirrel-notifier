// <copyright file="DeviceLoginOutputParserTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Helpers;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Helpers;

public class DeviceLoginOutputParserTests
{
    [Theory]
    [InlineData("user-code WDJB-MJHT", nameof(DeviceLoginSignalKind.UserCode), "WDJB-MJHT")]
    [InlineData("verification-uri https://gateway.example/device", nameof(DeviceLoginSignalKind.VerificationUri), "https://gateway.example/device")]
    [InlineData("verification-uri-complete https://gateway.example/device?user_code=WDJB-MJHT", nameof(DeviceLoginSignalKind.VerificationUriComplete), "https://gateway.example/device?user_code=WDJB-MJHT")]
    [InlineData("login-status success", nameof(DeviceLoginSignalKind.StatusSuccess), "")]
    [InlineData("login-status failed", nameof(DeviceLoginSignalKind.StatusFailed), "")]
    [InlineData("error-code SERVER_URL_UNKNOWN", nameof(DeviceLoginSignalKind.ErrorCode), "SERVER_URL_UNKNOWN")]
    public void Parse_ShouldClassifyKnownLines(string line, string expectedKind, string expectedValue)
    {
        DeviceLoginSignal signal = DeviceLoginOutputParser.Parse(line);

        signal.Kind.ToString().Should().Be(expectedKind);
        signal.Value.Should().Be(expectedValue);
    }

    [Theory]
    [InlineData("token-origin http://127.0.0.1:8080")]
    [InlineData("token-expires-at 2026-07-21T00:00:00.000Z")]
    [InlineData("some unrelated diagnostic line")]
    [InlineData("")]
    [InlineData("user-code ")]
    public void Parse_ShouldIgnoreUnrelatedOrValuelessLines(string line)
    {
        DeviceLoginSignal signal = DeviceLoginOutputParser.Parse(line);

        signal.Kind.Should().Be(DeviceLoginSignalKind.Ignored);
    }

    [Fact]
    public void Parse_ShouldPreferCompleteOverBasePrefix()
    {
        // verification-uri-complete は verification-uri の接頭辞を包含するため、
        // complete として分類されなければならない。
        DeviceLoginSignal signal = DeviceLoginOutputParser.Parse("verification-uri-complete https://gateway.example/device?user_code=ABCD");

        signal.Kind.Should().Be(DeviceLoginSignalKind.VerificationUriComplete);
        signal.Value.Should().Be("https://gateway.example/device?user_code=ABCD");
    }
}
