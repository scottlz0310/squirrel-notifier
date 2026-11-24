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
    [InlineData("[mcp_servers.agent-statusline]\ncommand = \"agent-statusline\"\n[tui]\nstatus_line_provider = { server = \"agent-statusline\", tool = \"statusline\" }\n", true, "agent-statusline")]
    [InlineData("[mcp_servers.my_custom-sl]\ncommand = \"custom\"\n[tui]\nstatus_line_provider = { tool = \"foo server = 'other'\", server = 'my_custom-sl' } # comment\n", true, "my_custom-sl")]
    [InlineData("[mcp_servers.agent-statusline]\ncommand = \"agent-statusline\"\n[tui.status_line_provider]\nserver = \"agent-statusline\"\ntool = \"statusline\"\n", true, "agent-statusline")]
    [InlineData("[mcp_servers.alt_provider]\ncommand = \"alt\"\n[tui]\nstatus_line_provider.server = 'alt_provider' # dotted key\n", true, "alt_provider")]
    [InlineData("tui.status_line_provider = { server = \"top_provider\", tool = \"statusline\" }\n[mcp_servers.top_provider]\ncommand = \"top\"\n", true, "top_provider")]
    [InlineData("[tui]\nstatus_line_provider = { server = \"orphan-provider\", tool = \"statusline\" }\n", false, null)]
    [InlineData("[tui]\nstatus_line_provider = \"default\"\n", false, null)]
    [InlineData("[mcp_servers.agent-statusline]\ncommand = \"agent-statusline\"\n[other]\nstatus_line_provider = { server = \"agent-statusline\", tool = \"statusline\" }\n", false, null)]
    [InlineData("[mcp_servers.agent-statusline]\ncommand = \"agent-statusline\"\n[tui]\n# status_line_provider = { server = \"agent-statusline\", tool = \"statusline\" }\n", false, null)]
    [InlineData("[mcp_servers.\"bad server name\"]\ncommand = \"x\"\n[tui]\nstatus_line_provider = { server = \"bad server name\", tool = \"statusline\" }\n", false, null)]
    [InlineData("[mcp_servers.\"bad.name\"]\ncommand = \"x\"\n[tui]\nstatus_line_provider = { server = \"bad.name\", tool = \"statusline\" }\n", false, null)]
    [InlineData("[tui]\nstatus_line_provider = { tool = \"statusline\" }\n", false, null)]
    [InlineData("invalid = [toml", false, null)]
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
    public void TryExtractProviderServer_ShouldIgnoreArrayOfTablesAndMultilineStringSections()
    {
        string arrayAfterTui = """
            [mcp_servers.unrelated]
            command = "unrelated"

            [tui.status_line_provider]
            tool = "statusline"

            [[custom_entries]]
            server = "unrelated"
            """;

        CodexStatuslineProviderReader.TryExtractProviderServer(arrayAfterTui, out string? arrayServer)
            .Should().BeFalse();
        arrayServer.Should().BeNull();

        string multilineString = """
            [mcp_servers.fake-server]
            command = "fake"

            note = '''
            [tui]
            status_line_provider = { server = "fake-server", tool = "statusline" }
            '''
            """;

        CodexStatuslineProviderReader.TryExtractProviderServer(multilineString, out string? multilineServer)
            .Should().BeFalse();
        multilineServer.Should().BeNull();
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
            "[mcp_servers.home-override]\ncommand = \"sl\"\n[tui]\nstatus_line_provider = { server = \"home-override\", tool = \"statusline\" }\n");
        File.WriteAllText(
            Path.Combine(userProfile, ".codex", "config.toml"),
            "[mcp_servers.profile-default]\ncommand = \"sl\"\n[tui]\nstatus_line_provider = { server = \"profile-default\", tool = \"statusline\" }\n");

        CodexStatuslineProviderReader.ReadConfiguredServer(codexHome, userProfile)
            .Should().Be("home-override");
        CodexStatuslineProviderReader.ReadConfiguredServer(null, userProfile)
            .Should().Be("profile-default");
        CodexStatuslineProviderReader.ReadConfiguredServer(Path.Combine(_tempDir, "missing"), userProfile)
            .Should().BeNull();
        CodexStatuslineProviderReader.ReadConfiguredServer(null, null)
            .Should().BeNull();
    }

    [Theory]
    [InlineData("-c", "mcp_servers.agent-statusline.enabled=true")]
    [InlineData("-c", "mcp_servers.agent-statusline.enabled = true")]
    [InlineData("--config", "mcp_servers.\"agent-statusline\".enabled=true")]
    [InlineData("--config", "mcp_servers.'agent-statusline'.enabled = false")]
    public void PrependDisableConfigArgs_ShouldRespectExistingSeparateConfigOverride(string flag, string value)
    {
        string[] existing = [flag, value, "exec", "--json", "prompt"];

        List<string> result = CodexStatuslineProviderReader.PrependDisableConfigArgs(existing, "agent-statusline");

        result.Should().Equal(existing);
    }

    [Theory]
    [InlineData("-c=mcp_servers.agent-statusline.enabled=true")]
    [InlineData("--config=mcp_servers.agent-statusline.enabled = true")]
    [InlineData("--config=mcp_servers.\"agent-statusline\".enabled=false")]
    public void PrependDisableConfigArgs_ShouldRespectExistingEqualsConfigOverride(string combinedFlag)
    {
        string[] existing = [combinedFlag, "exec", "--json", "prompt"];

        List<string> result = CodexStatuslineProviderReader.PrependDisableConfigArgs(existing, "agent-statusline");

        result.Should().Equal(existing);
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

        // "--" 以降の位置引数に含まれる文字列は既存オーバーライドとして誤認しない
        string[] afterDoubleDash = ["exec", "--", "-c", "mcp_servers.agent-statusline.enabled=true"];
        List<string> withDoubleDash = CodexStatuslineProviderReader.PrependDisableConfigArgs(afterDoubleDash, "agent-statusline");
        withDoubleDash.Should().Equal("-c", "mcp_servers.agent-statusline.enabled=false", "exec", "--", "-c", "mcp_servers.agent-statusline.enabled=true");
    }
}
