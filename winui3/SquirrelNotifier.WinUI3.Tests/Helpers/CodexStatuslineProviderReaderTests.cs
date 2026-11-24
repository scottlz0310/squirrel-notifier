// <copyright file="CodexStatuslineProviderReaderTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using SquirrelNotifier.WinUI3.Helpers;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Helpers;

public sealed class CodexStatuslineProviderReaderTests : IDisposable
{
    private readonly string _tempDir;

    public CodexStatuslineProviderReaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sn-codex-sl-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Theory]
    [InlineData("codex", true)]
    [InlineData("CODEX", true)]
    [InlineData("codex.exe", true)]
    [InlineData("codex.cmd", true)]
    [InlineData(@"C:\Users\dev\AppData\Local\Programs\OpenAI\Codex\bin\codex.exe", true)]
    [InlineData("\"C:\\Program Files\\Codex\\codex.cmd\"", true)]
    [InlineData("claude", false)]
    [InlineData("agy", false)]
    [InlineData("copilot", false)]
    [InlineData("codex-wrapper", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsCodexCommand_ShouldDetectCodexExecutableOnly(string? commandPath, bool expected)
    {
        CodexStatuslineProviderReader.IsCodexCommand(commandPath).Should().Be(expected);
    }

    [Theory]
    [InlineData("[tui]\nstatus_line_provider = { server = \"agent-statusline\", tool = \"statusline\" }\n", true, "agent-statusline")]
    [InlineData("[tui]\nstatus_line_provider = { tool = 'statusline', server = 'my_custom-sl' } # comment\n", true, "my_custom-sl")]
    [InlineData("[tui.status_line_provider]\nserver = \"agent-statusline\"\ntool = \"statusline\"\n", true, "agent-statusline")]
    [InlineData("[tui.status_line_provider]\nserver = 'alt_provider' # inline comment\n", true, "alt_provider")]
    [InlineData("[tui]\nstatus_line_provider = \"default\"\n", false, null)]
    [InlineData("[other]\nstatus_line_provider = { server = \"agent-statusline\", tool = \"statusline\" }\n", false, null)]
    [InlineData("[tui]\n# status_line_provider = { server = \"agent-statusline\", tool = \"statusline\" }\n", false, null)]
    [InlineData("[tui]\nstatus_line_provider = { server = \"bad server name\", tool = \"statusline\" }\n", false, null)]
    [InlineData("[tui]\nstatus_line_provider = { server = \"bad.name\", tool = \"statusline\" }\n", false, null)]
    [InlineData("[tui]\nstatus_line_provider = { tool = \"statusline\" }\n", false, null)]
    [InlineData("", false, null)]
    [InlineData(null, false, null)]
    public void TryExtractProviderServer_ShouldParseSupportedFormatsAndRejectInvalid(
        string? toml,
        bool expectedOk,
        string? expectedServer)
    {
        bool ok = CodexStatuslineProviderReader.TryExtractProviderServer(toml, out string? server);

        ok.Should().Be(expectedOk);
        server.Should().Be(expectedServer);
    }

    [Fact]
    public void TryExtractProviderServer_ShouldUseLastDefinitionInConfig()
    {
        string toml = """
            [tui]
            status_line_provider = { server = "first-provider", tool = "statusline" }
            status_line_provider = "default"
            """;

        bool ok = CodexStatuslineProviderReader.TryExtractProviderServer(toml, out string? server);

        ok.Should().BeFalse();
        server.Should().BeNull();
    }

    [Fact]
    public void ReadConfiguredServer_ShouldPreferCodexHomeOverUserProfile()
    {
        string codexHome = Path.Combine(_tempDir, "custom-codex");
        string userProfile = Path.Combine(_tempDir, "home");
        Directory.CreateDirectory(codexHome);
        Directory.CreateDirectory(Path.Combine(userProfile, ".codex"));

        File.WriteAllText(
            Path.Combine(codexHome, "config.toml"),
            "[tui]\nstatus_line_provider = { server = \"home-override\", tool = \"statusline\" }\n");
        File.WriteAllText(
            Path.Combine(userProfile, ".codex", "config.toml"),
            "[tui]\nstatus_line_provider = { server = \"profile-default\", tool = \"statusline\" }\n");

        CodexStatuslineProviderReader.ReadConfiguredServer(codexHome, userProfile)
            .Should().Be("home-override");
        CodexStatuslineProviderReader.ReadConfiguredServer(null, userProfile)
            .Should().Be("profile-default");
        CodexStatuslineProviderReader.ReadConfiguredServer(Path.Combine(_tempDir, "missing"), userProfile)
            .Should().BeNull();
    }

    [Fact]
    public void PrependDisableConfigArgs_ShouldInsertAtFrontOnlyWhenNeeded()
    {
        string[] baseArgs = ["exec", "--skip-git-repo-check", "--json", "prompt"];

        List<string> withDisable = CodexStatuslineProviderReader.PrependDisableConfigArgs(baseArgs, "agent-statusline");
        withDisable.Should().Equal("-c", "mcp_servers.agent-statusline.enabled=false", "exec", "--skip-git-repo-check", "--json", "prompt");

        List<string> withoutServer = CodexStatuslineProviderReader.PrependDisableConfigArgs(baseArgs, null);
        withoutServer.Should().Equal(baseArgs);

        List<string> unsafeServer = CodexStatuslineProviderReader.PrependDisableConfigArgs(baseArgs, "bad;cmd");
        unsafeServer.Should().Equal(baseArgs);

        string[] alreadyConfigured = ["-c", "mcp_servers.agent-statusline.enabled=true", "exec", "--json", "prompt"];
        List<string> preserved = CodexStatuslineProviderReader.PrependDisableConfigArgs(alreadyConfigured, "agent-statusline");
        preserved.Should().Equal(alreadyConfigured);
    }
}
