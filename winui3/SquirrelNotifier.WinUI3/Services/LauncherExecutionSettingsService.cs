// <copyright file="LauncherExecutionSettingsService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Text.Json;
using SquirrelNotifier.WinUI3.Helpers;
using SquirrelNotifier.WinUI3.Models;
using Tomlyn;
using Tomlyn.Model;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>起動引数と設定ファイルから表示用の選択値を取得する。実行時の使用モデルは推測しない.</summary>
internal sealed class LauncherExecutionSettingsService
{
    private readonly string _userDirectory;
    private readonly Func<string, string?> _getEnvironment;
    private readonly Func<string, CancellationToken, Task<string?>> _readFile;
    private readonly Func<string, bool> _isRepositoryRoot;

    public LauncherExecutionSettingsService(
        string? userDirectory = null,
        Func<string, string?>? getEnvironment = null,
        Func<string, CancellationToken, Task<string?>>? readFile = null,
        Func<string, bool>? isRepositoryRoot = null)
    {
        _userDirectory = userDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _getEnvironment = getEnvironment ?? Environment.GetEnvironmentVariable;
        _readFile = readFile ?? ReadOptionalFileAsync;
        _isRepositoryRoot = isRepositoryRoot ?? (path => Directory.Exists(Path.Combine(path, ".git")) || File.Exists(Path.Combine(path, ".git")));
    }

    public async Task<LauncherExecutionSettings> ResolveAsync(
        string? agentId,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string? agyModel,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LauncherSelectionArguments selection = LauncherSelectionArguments.Parse(agentId, arguments);
        if (selection.Directory is not null)
        {
            workingDirectory = Path.GetFullPath(selection.Directory, workingDirectory);
        }

        string? model = agentId == "agy" ? agyModel : null;
        string? effort = null;
        string modelSource = "設定";
        string effortSource = "設定";
        List<string> errors = [];
        Dictionary<string, string> modelEfforts = new(StringComparer.Ordinal);
        Dictionary<string, string> settingEnvironment = new(StringComparer.Ordinal);
        if (agentId == "codex" && !selection.IgnoreUserConfig)
        {
            string home = GetConfigDirectory("CODEX_HOME", ".codex");
            TomlTable? userSettings = await ReadTomlAsync(Path.Combine(home, "config.toml"));
            if (selection.Profile is not null)
            {
                await ReadTomlAsync(Path.Combine(home, selection.Profile + ".config.toml"));
            }

            List<string> directories = GetProjectDirectories(workingDirectory);
            if (IsCodexProjectTrusted(userSettings, directories))
            {
                foreach (string directory in directories)
                {
                    await ReadTomlAsync(Path.Combine(directory, ".codex", "config.toml"));
                }
            }
        }
        else if (agentId is "claude" or "copilot")
        {
            string home = agentId == "claude" ? GetConfigDirectory("CLAUDE_CONFIG_DIR", ".claude") : GetConfigDirectory("COPILOT_HOME", ".copilot");
            bool ReadsSource(string source) => agentId != "claude" || selection.SettingsSources is null || selection.SettingsSources.Split(',').Contains(source, StringComparer.Ordinal);
            if (ReadsSource("user"))
            {
                await ReadJsonAsync(Path.Combine(home, "settings.json"));
            }

            // CLI が適用する作業ディレクトリのプロジェクト設定を、ユーザー設定より優先する。
            string projectDirectory = agentId == "claude" ? ".claude" : Path.Combine(".github", "copilot");
            if (agentId == "claude" || await IsCopilotTrustedAsync(home))
            {
                if (ReadsSource("project"))
                {
                    await ReadJsonAsync(Path.Combine(workingDirectory, projectDirectory, "settings.json"));
                }

                if (ReadsSource("local"))
                {
                    await ReadJsonAsync(Path.Combine(workingDirectory, projectDirectory, "settings.local.json"));
                }
            }

            if (agentId == "claude")
            {
                foreach (string settingsOverride in selection.SettingsOverrides)
                {
                    if (settingsOverride.StartsWith('{'))
                    {
                        try
                        {
                            using JsonDocument document = JsonDocument.Parse(settingsOverride);
                            ApplyJson(document.RootElement);
                        }
                        catch (JsonException)
                        {
                            model = null;
                            effort = null;
                            errors.Add("起動引数の --settings JSONを取得できません（JsonException）");
                        }
                    }
                    else
                    {
                        await ReadJsonAsync(Path.GetFullPath(settingsOverride, workingDirectory));
                    }
                }

                if ((settingEnvironment.GetValueOrDefault("ANTHROPIC_MODEL") ?? _getEnvironment("ANTHROPIC_MODEL")) is { } environmentModel)
                {
                    model = environmentModel;
                    modelSource = "環境";
                }

                if ((settingEnvironment.GetValueOrDefault("CLAUDE_CODE_EFFORT_LEVEL") ?? _getEnvironment("CLAUDE_CODE_EFFORT_LEVEL")) is { } environmentEffort)
                {
                    effort = environmentEffort;
                    effortSource = "環境";
                }
            }
        }

        if (agentId == "claude" && (selection.Model ?? model) is { } selectedModel && modelEfforts.TryGetValue(selectedModel, out string? modelEffort)
            && !settingEnvironment.ContainsKey("CLAUDE_CODE_EFFORT_LEVEL") && _getEnvironment("CLAUDE_CODE_EFFORT_LEVEL") is null)
        {
            effort = modelEffort;
        }

        if (selection.Model is not null)
        {
            model = selection.Model;
            modelSource = "引数";
        }

        if (selection.Effort is not null)
        {
            effort = selection.Effort;
            effortSource = "引数";
        }

        return new(model, effort, modelSource, effortSource, errors);

        async Task ReadJsonAsync(string path)
        {
            try
            {
                string? content = await _readFile(path, cancellationToken).ConfigureAwait(false);
                if (content is null)
                {
                    return;
                }

                using JsonDocument document = JsonDocument.Parse(content);
                ApplyJson(document.RootElement);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                model = null;
                effort = null;
                errors.Add($"起動設定を取得できません: {path}（{ex.GetType().Name}）");
            }
        }

        void ApplyJson(JsonElement root)
        {
            model = ReadString(root, "model") ?? model;
            effort = ReadString(root, "effortLevel") ?? effort;
            if (agentId == "claude" && root.ValueKind == JsonValueKind.Object && root.TryGetProperty("env", out JsonElement environment))
            {
                foreach (string key in new[] { "ANTHROPIC_MODEL", "CLAUDE_CODE_EFFORT_LEVEL" })
                {
                    if (ReadString(environment, key) is { } value)
                    {
                        settingEnvironment[key] = value;
                    }
                }
            }

            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("modelSettings", out JsonElement models) && models.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in models.EnumerateObject())
                {
                    if (ReadString(property.Value, "effortLevel") is { } configuredEffort)
                    {
                        modelEfforts[property.Name] = configuredEffort;
                    }
                }
            }
        }

        async Task<bool> IsCopilotTrustedAsync(string home)
        {
            string path = Path.Combine(home, "config.json");
            try
            {
                string? content = await _readFile(path, cancellationToken).ConfigureAwait(false);
                if (content is null)
                {
                    return false;
                }

                using JsonDocument document = JsonDocument.Parse(content);
                if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("trustedFolders", out JsonElement folders) && folders.ValueKind == JsonValueKind.Array)
                {
                    string current = workingDirectory.Replace('/', Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar);
                    foreach (JsonElement folder in folders.EnumerateArray())
                    {
                        if (folder.ValueKind == JsonValueKind.String)
                        {
                            string trusted = folder.GetString()!.Replace('/', Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar);
                            if (current.Equals(trusted, StringComparison.OrdinalIgnoreCase) || current.StartsWith(trusted + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                            {
                                return true;
                            }
                        }
                    }
                }

                return false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                errors.Add($"プロジェクト設定の信頼状態を取得できません: {path}（{ex.GetType().Name}）");
                return false;
            }
        }

        async Task<TomlTable?> ReadTomlAsync(string path)
        {
            try
            {
                string? content = await _readFile(path, cancellationToken).ConfigureAwait(false);
                if (content is null)
                {
                    return null;
                }

                TomlTable table = TomlSerializer.Deserialize<TomlTable>(content)!;
                model = table.TryGetValue("model", out object? m) && m is string modelValue ? modelValue : model;
                effort = table.TryGetValue("model_reasoning_effort", out object? e) && e is string effortValue ? effortValue : effort;
                return table;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TomlException)
            {
                model = null;
                effort = null;
                errors.Add($"起動設定を取得できません: {path}（{ex.GetType().Name}）");
                return null;
            }
        }
    }

    private List<string> GetProjectDirectories(string workingDirectory)
    {
        List<string> directories = [];
        for (DirectoryInfo? directory = new(workingDirectory); directory is not null; directory = directory.Parent)
        {
            directories.Add(directory.FullName);
            if (_isRepositoryRoot(directory.FullName))
            {
                break;
            }
        }

        directories.Reverse();
        return directories;
    }

    private static bool IsCodexProjectTrusted(TomlTable? settings, IReadOnlyList<string> directories)
    {
        if (settings is null || !settings.TryGetValue("projects", out object? value) || value is not TomlTable projects)
        {
            return false;
        }

        foreach (string directory in directories.Reverse())
        {
            foreach ((string key, object? entry) in projects)
            {
                if (string.Equals(key.TrimEnd(Path.DirectorySeparatorChar), directory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                    && entry is TomlTable project && project.TryGetValue("trust_level", out object? trust))
                {
                    return trust is "trusted";
                }
            }
        }

        return false;
    }

    private static string? ReadString(JsonElement root, string key)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private string GetConfigDirectory(string environmentVariable, string directory)
        => _getEnvironment(environmentVariable) ?? Path.Combine(_userDirectory, directory);

    private static async Task<string?> ReadOptionalFileAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }
}
