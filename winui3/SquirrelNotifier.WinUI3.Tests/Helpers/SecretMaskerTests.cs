// <copyright file="SecretMaskerTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using FluentAssertions;
using SquirrelNotifier.WinUI3.Helpers;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Helpers;

public class SecretMaskerTests
{
    private static SecretMasker CreateMasker(params string?[] knownSecrets) => new(knownSecrets);

    [Theory]
    [InlineData("token: ghp_abcdefghijklmnopqrstuvwxyz0123456789", "token: ***")]
    [InlineData("gho_ABCDEFGHIJKLMNOP1234 を使用", "*** を使用")]
    [InlineData("github_pat_11ABCDEFG0abcdefghijklmnopqrstuv", "***")]
    [InlineData("key=sk-ant-api03-abcdefghijklmnop", "key=***")]
    [InlineData("sk-proj-1234567890abcdefghij", "***")]
    [InlineData("Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.payload", "Authorization: Bearer ***")]
    [InlineData("authorization: bearer abc123def456", "authorization: Bearer ***")]
    public void Mask_ShouldMaskKnownTokenPatterns(string input, string expected)
    {
        CreateMasker().Mask(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("ghp_short は短すぎるので対象外")]
    [InlineData("sk-abc は短すぎるので対象外")]
    [InlineData("Bearer x は短すぎるので対象外")]
    [InlineData("普通のログ行です")]
    [InlineData("")]
    public void Mask_ShouldNotChangeNonMatchingText(string input)
    {
        CreateMasker().Mask(input).Should().Be(input);
    }

    [Fact]
    public void Mask_ShouldMaskKnownSecretValues()
    {
        SecretMasker masker = CreateMasker("my-local-auth-token");

        masker.Mask("MCP_PROBE_AUTH_TOKEN=my-local-auth-token を送信").Should().Be("MCP_PROBE_AUTH_TOKEN=*** を送信");
    }

    [Fact]
    public void Mask_ShouldMaskMultipleOccurrences()
    {
        SecretMasker masker = CreateMasker("secret-value");

        masker.Mask("secret-value と secret-value").Should().Be("*** と ***");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Mask_ShouldIgnoreEmptyKnownSecrets(string? secret)
    {
        // null・空白の秘密値は照合対象にせず、任意のテキストがマスクされないこと
        SecretMasker masker = CreateMasker(secret);

        masker.Mask("どの部分もマスクされない通常テキスト").Should().Be("どの部分もマスクされない通常テキスト");
    }

    [Fact]
    public void Constructor_ShouldThrowForNullCollection()
    {
        FluentActions.Invoking(() => new SecretMasker(null!)).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void CreateDefault_ShouldMaskEnvironmentToken()
    {
        const string envName = "MCP_PROBE_AUTH_TOKEN";
        string? original = Environment.GetEnvironmentVariable(envName);
        try
        {
            Environment.SetEnvironmentVariable(envName, "env-secret-token-xyz");

            SecretMasker masker = SecretMasker.CreateDefault();

            masker.Mask("token=env-secret-token-xyz").Should().Be("token=***");
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, original);
        }
    }
}
