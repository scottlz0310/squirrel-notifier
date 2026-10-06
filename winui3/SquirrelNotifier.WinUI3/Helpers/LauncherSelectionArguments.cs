// <copyright file="LauncherSelectionArguments.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using SquirrelNotifier.WinUI3.Models;
using Tomlyn;
using Tomlyn.Model;

namespace SquirrelNotifier.WinUI3.Helpers;

internal sealed record LauncherSelectionArguments(string? Model, string? Effort, string? Profile, bool IgnoreUserConfig, IReadOnlyList<string> SettingsOverrides, string? SettingsSources, string? Directory)
{
    public static string? ResolveAgentId(string presetId, string commandPath)
        => LauncherAgentCatalog.Find(presetId)?.Id
            ?? LauncherAgentCatalog.All.FirstOrDefault(agent =>
                string.Equals(agent.Command, Path.GetFileNameWithoutExtension(commandPath), StringComparison.OrdinalIgnoreCase))?.Id;

    private static readonly HashSet<string> _valueOptions = new(StringComparer.Ordinal)
    {
        "-p", "--print", "--prompt", "-i", "--prompt-interactive", "--agent", "--conversation",
        "--resume", "-r", "--session-id", "--add-dir", "--project", "--new-project", "--output-format",
        "--input-format", "--json-schema", "--log-file", "--mode", "--print-timeout", "--fallback-model",
        "--settings", "--setting-sources", "--settings-sources", "--output-schema", "-o", "--output-last-message",
        "-s", "--sandbox", "-a", "--ask-for-approval", "--local-provider", "--context", "--name", "-n",
    };

    public static LauncherSelectionArguments Parse(string? agentId, IReadOnlyList<string> arguments)
    {
        string? model = null;
        string? configModel = null;
        string? effort = null;
        string? profile = null;
        bool ignoreUserConfig = false;
        List<string> settingsOverrides = [];
        string? settingsSources = null;
        string? directory = null;
        for (int index = 0; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if (argument == "--")
            {
                break;
            }

            if (!argument.StartsWith('-'))
            {
                if (agentId == "codex" && argument is "exec" or "review")
                {
                    continue;
                }

                if (argument == "resume")
                {
                    if (index + 1 < arguments.Count && !arguments[index + 1].StartsWith('-'))
                    {
                        index++;
                    }

                    continue;
                }

                // 位置引数そのものは再解析しない。後続の独立したフラグは CLI が受け付ける。
                continue;
            }

            int separator = argument.IndexOf('=', StringComparison.Ordinal);
            string option = separator < 0 ? argument : argument[..separator];
            bool isModel = option == "--model" || (agentId == "codex" && option == "-m");
            bool isEffort = option is "--effort" or "--reasoning-effort";
            bool isProfile = agentId == "codex" && option is "-p" or "--profile";
            bool isConfig = agentId == "codex" && option is "-c" or "--config";
            bool isSettings = agentId == "claude" && option == "--settings";
            bool isSources = agentId == "claude" && option is "--setting-sources" or "--settings-sources";
            bool isDirectory = agentId == "codex" && option is "-C" or "--cd";
            if (isModel || isEffort || isProfile || isConfig || isSettings || isSources || isDirectory)
            {
                string? value = separator >= 0 ? argument[(separator + 1)..] : index + 1 < arguments.Count ? arguments[++index] : null;
                if (isModel)
                {
                    model = value;
                }
                else if (isEffort)
                {
                    effort = value;
                }
                else if (isProfile)
                {
                    profile = value;
                }
                else if (isSettings && value is not null)
                {
                    settingsOverrides.Add(value);
                }
                else if (isSources)
                {
                    settingsSources = value;
                }
                else if (isDirectory)
                {
                    directory = value;
                }
                else if (value is not null)
                {
                    int equals = value.IndexOf('=', StringComparison.Ordinal);
                    string key = equals < 0 ? value : value[..equals].Trim();
                    if (equals >= 0 && key is "model" or "model_reasoning_effort")
                    {
                        string raw = value[(equals + 1)..];
                        string parsed;
                        try
                        {
                            TomlTable table = TomlSerializer.Deserialize<TomlTable>("value = " + raw)!;
                            parsed = table["value"] as string ?? raw;
                        }
                        catch (TomlException)
                        {
                            // Codex の -c は TOML として読めない値を生の文字列として扱う。
                            parsed = raw;
                        }

                        if (key == "model")
                        {
                            configModel = parsed;
                        }
                        else
                        {
                            effort = parsed;
                        }
                    }
                }
            }
            else if (argument == "--ignore-user-config")
            {
                ignoreUserConfig = true;
            }
            else if (separator < 0 && _valueOptions.Contains(option))
            {
                if (!(agentId == "claude" && option is "--resume" or "-r" && index + 1 < arguments.Count && arguments[index + 1].StartsWith('-')))
                {
                    index++;
                }
            }
        }

        return new(model ?? configModel, effort, profile, ignoreUserConfig, settingsOverrides, settingsSources, directory);
    }
}
