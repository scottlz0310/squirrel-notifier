// <copyright file="LauncherExecutionSettingsServiceTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public class LauncherExecutionSettingsServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "launch-settings-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _environment = new(StringComparer.Ordinal);

    public LauncherExecutionSettingsServiceTests()
    {
        Directory.CreateDirectory(Path.Combine(_directory, ".git"));
    }

    public void Dispose() => Directory.Delete(_directory, true);

    [Theory]
    [InlineData("claude", ".claude")]
    [InlineData("copilot", ".copilot")]
    public async Task ResolveAsync_ShouldMergeFilesPerFieldAndPreferArguments(string agent, string configDirectory)
    {
        _files[Path.Combine(_directory, configDirectory, "settings.json")] = "{\"model\":\"user-model\",\"effortLevel\":\"medium\"}";
        string projectDirectory = agent == "claude" ? ".claude" : Path.Combine(".github", "copilot");
        string workspace = Path.Combine(_directory, "workspace");
        _files[Path.Combine(_directory, ".copilot", "config.json")] = System.Text.Json.JsonSerializer.Serialize(new { trustedFolders = new[] { workspace } });
        _files[Path.Combine(workspace, projectDirectory, "settings.json")] = "{\"model\":\"project-model\"}";
        _files[Path.Combine(workspace, projectDirectory, "settings.local.json")] = "{\"effortLevel\":\"high\"}";

        LauncherExecutionSettings selected = await CreateService().ResolveAsync(agent, [], workspace, null, CancellationToken.None);
        LauncherExecutionSettings overridden = await CreateService().ResolveAsync(agent, ["--model", "explicit"], workspace, null, CancellationToken.None);

        selected.Model.Should().Be("project-model");
        selected.Effort.Should().Be("high");
        overridden.Model.Should().Be("explicit");
        overridden.ModelSource.Should().Be("引数");
        overridden.Effort.Should().Be("high");
        selected.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_ShouldUseCodexTomlAndProfile()
    {
        _files[Path.Combine(_directory, ".codex", "config.toml")] = "model = 'user-model'\nmodel_reasoning_effort = 'medium'\n[mcp_servers.example]\nmodel = 'unrelated'";
        _files[Path.Combine(_directory, ".codex", "review.config.toml")] = "model_reasoning_effort = 'high'";

        LauncherExecutionSettings selected = await CreateService().ResolveAsync("codex", ["--profile", "review"], _directory, null, CancellationToken.None);
        LauncherExecutionSettings ignored = await CreateService().ResolveAsync("codex", ["--ignore-user-config"], _directory, null, CancellationToken.None);

        selected.Model.Should().Be("user-model");
        selected.Effort.Should().Be("high");
        ignored.Model.Should().BeNull();
        ignored.Effort.Should().BeNull();
    }

    [Theory]
    [InlineData("trusted", "project-model")]
    [InlineData("untrusted", "user-model")]
    public async Task ResolveAsync_ShouldReadCodexProjectConfigOnlyWhenTrusted(string trust, string expected)
    {
        string home = Path.Combine(_directory, "home");
        _environment["CODEX_HOME"] = home;
        _files[Path.Combine(home, "config.toml")] = $"model = 'user-model'\n[projects.'{_directory}']\ntrust_level = '{trust}'";
        _files[Path.Combine(_directory, ".codex", "config.toml")] = "model = 'project-model'";

        LauncherExecutionSettings selected = await CreateService().ResolveAsync("codex", [], _directory, null, CancellationToken.None);

        selected.Model.Should().Be(expected);
    }

    [Theory]
    [InlineData("claude", "CLAUDE_CONFIG_DIR", "settings.json", "{\"model\":\"configured\"}")]
    [InlineData("copilot", "COPILOT_HOME", "settings.json", "{\"model\":\"configured\"}")]
    [InlineData("codex", "CODEX_HOME", "config.toml", "model = 'configured'")]
    public async Task ResolveAsync_ShouldHonorConfigDirectory(string agent, string variable, string file, string content)
    {
        _environment[variable] = Path.Combine(_directory, "custom");
        _files[Path.Combine(_environment[variable], file)] = content;

        LauncherExecutionSettings selected = await CreateService().ResolveAsync(agent, [], _directory, null, CancellationToken.None);

        selected.Model.Should().Be("configured");
    }

    [Fact]
    public async Task ResolveAsync_ShouldRespectClaudeEnvironmentAndReuseAgySelection()
    {
        _environment["ANTHROPIC_MODEL"] = "environment-model";
        _environment["CLAUDE_CODE_EFFORT_LEVEL"] = "max";
        LauncherExecutionSettings claude = await CreateService().ResolveAsync("claude", [], _directory, null, CancellationToken.None);
        LauncherExecutionSettings agy = await CreateService().ResolveAsync("agy", ["--effort", "high"], _directory, "Gemini Flash (High)", CancellationToken.None);

        claude.Model.Should().Be("environment-model");
        claude.ModelSource.Should().Be("環境");
        claude.Effort.Should().Be("max");
        agy.Model.Should().Be("Gemini Flash (High)");
        agy.Effort.Should().Be("high");
    }

    [Theory]
    [InlineData("claude", ".claude", "settings.json")]
    [InlineData("codex", ".codex", "config.toml")]
    public async Task ResolveAsync_ShouldReportInvalidFilesAndKeepExplicitSelection(string agent, string folder, string file)
    {
        _files[Path.Combine(_directory, folder, file)] = "invalid content";

        LauncherExecutionSettings selected = await CreateService().ResolveAsync(agent, ["--model", "explicit"], Path.Combine(_directory, "workspace"), null, CancellationToken.None);

        selected.Model.Should().Be("explicit");
        selected.Effort.Should().BeNull();
        selected.Errors.Should().ContainSingle();
        selected.Errors[0].Should().NotContain("invalid content");
    }

    [Fact]
    public async Task ResolveAsync_ShouldPropagateCancellation()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        Func<Task> action = () => CreateService().ResolveAsync("codex", [], _directory, null, cancellation.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData("user", "user-model")]
    [InlineData("project", "project-model")]
    [InlineData("local", "local-model")]
    [InlineData("", null)]
    public async Task ResolveAsync_ShouldRespectClaudeSettingsSources(string sources, string? expected)
    {
        string workspace = Path.Combine(_directory, "workspace");
        _files[Path.Combine(_directory, ".claude", "settings.json")] = "{\"model\":\"user-model\"}";
        _files[Path.Combine(workspace, ".claude", "settings.json")] = "{\"model\":\"project-model\"}";
        _files[Path.Combine(workspace, ".claude", "settings.local.json")] = "{\"model\":\"local-model\"}";

        LauncherExecutionSettings selected = await CreateService().ResolveAsync("claude", ["--setting-sources", sources], workspace, null, CancellationToken.None);

        selected.Model.Should().Be(expected);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResolveAsync_ShouldUseClaudeInlineOrFileOverrideAndModelEffort(bool inline)
    {
        string json = "{\"model\":\"exact-model\",\"effortLevel\":\"medium\",\"modelSettings\":{\"exact-model\":{\"effortLevel\":\"high\"}}}";
        _files[Path.Combine(_directory, "override.json")] = json;
        LauncherExecutionSettings selected = await CreateService().ResolveAsync("claude", ["--settings", inline ? json : "override.json"], _directory, null, CancellationToken.None);

        selected.Model.Should().Be("exact-model");
        selected.Effort.Should().Be("high");
    }

    [Theory]
    [InlineData("claude", false)]
    [InlineData("codex", true)]
    public async Task ResolveAsync_ShouldReportReadFailuresWithoutConfigurationContents(string agent, bool denied)
    {
        LauncherExecutionSettingsService service = new(_directory, _ => null, (_, _) => throw (denied ? new UnauthorizedAccessException("sensitive-content") : new IOException("sensitive-content")));

        LauncherExecutionSettings selected = await service.ResolveAsync(agent, ["--model", "chosen"], _directory, null, CancellationToken.None);

        selected.Model.Should().Be("chosen");
        selected.Errors.Should().NotBeEmpty().And.OnlyContain(error => !error.Contains("sensitive-content", StringComparison.Ordinal));
    }

    private LauncherExecutionSettingsService CreateService() => new(
        _directory,
        key => _environment.GetValueOrDefault(key),
        (path, _) => Task.FromResult(_files.GetValueOrDefault(path)),
        path => path == _directory);

    [Fact]
    public async Task ResolveAsync_ShouldUseClaudeEnvironmentFromSettingsWithoutReadingSecrets()
    {
        _files[Path.Combine(_directory, ".claude", "settings.json")] = "{\"model\":\"file-model\",\"env\":{\"ANTHROPIC_MODEL\":\"environment-model\",\"CLAUDE_CODE_EFFORT_LEVEL\":\"high\",\"API_KEY\":\"private-value\"}}";

        LauncherExecutionSettings selected = await CreateService().ResolveAsync("claude", [], Path.Combine(_directory, "workspace"), null, CancellationToken.None);

        selected.Model.Should().Be("environment-model");
        selected.Effort.Should().Be("high");
        selected.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_ShouldIgnoreUntrustedCopilotProjectModel()
    {
        _files[Path.Combine(_directory, ".copilot", "settings.json")] = "{\"model\":\"user-model\"}";
        _files[Path.Combine(_directory, ".github", "copilot", "settings.json")] = "{\"model\":\"project-model\"}";

        LauncherExecutionSettings selected = await CreateService().ResolveAsync("copilot", [], _directory, null, CancellationToken.None);

        selected.Model.Should().Be("user-model");
    }
}
