// <copyright file="AgyModelSelectionService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Text.Json;
using SquirrelNotifier.WinUI3.Helpers;
using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

internal sealed record AgyModelSelection(string? Model, string? Error);

/// <summary>起動引数を優先し、未指定時に agy の永続モデル設定を読む.</summary>
internal sealed class AgyModelSelectionService
{
    // オプションの値（特にプロンプト）に含まれる --model を起動フラグと誤認しない。
    private static readonly HashSet<string> _valueOptions = new(StringComparer.Ordinal)
    {
        "-p", "--print", "--prompt", "-i", "--prompt-interactive", "--agent", "--effort",
        "--conversation", "--add-dir", "--project", "--new-project", "--output-format",
        "--input-format", "--json-schema", "--log-file", "--mode", "--print-timeout",
    };

    private readonly string _settingsPath;
    private readonly Func<string, CancellationToken, Task<string>> _readSettings;

    public AgyModelSelectionService(
        string? settingsPath = null,
        Func<string, CancellationToken, Task<string>>? readSettings = null)
    {
        _settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".gemini",
            "antigravity-cli",
            "settings.json");
        _readSettings = readSettings ?? File.ReadAllTextAsync;
    }

    public Task<AgyModelSelection> ResolveAsync(string arguments, CancellationToken cancellationToken)
        => ResolveAsync(McpSubscriptionService.ParseArguments(arguments), cancellationToken);

    public async Task<AgyModelSelection> ResolveAsync(IReadOnlyList<string> parsed, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? model = null;
        bool specified = false;
        for (int index = 0; index < parsed.Count; index++)
        {
            string argument = parsed[index];
            if (argument == "--")
            {
                break;
            }

            if (_valueOptions.Contains(argument))
            {
                index++;
                continue;
            }

            if (argument.StartsWith("--model=", StringComparison.Ordinal))
            {
                model = argument["--model=".Length..];
                specified = true;
            }
            else if (argument == "--model")
            {
                model = index + 1 < parsed.Count ? parsed[++index] : null;
                specified = true;
            }
        }

        if (specified)
        {
            return new AgyModelSelection(model, null);
        }

        try
        {
            string content = await _readSettings(_settingsPath, cancellationToken).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(content);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("model", out JsonElement modelElement)
                && modelElement.ValueKind == JsonValueKind.String)
            {
                return new AgyModelSelection(modelElement.GetString(), null);
            }

            return new AgyModelSelection(null, "agy の設定にモデル名がありません。全枠で Auto-Pause を評価します。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AgyModelSelection(null, $"agy のモデル設定を取得できません。全枠で Auto-Pause を評価します: {ex.Message}");
        }
    }

    public async Task<AgyModelSelection> ResolveConfiguredAsync(AppSettings settings, LauncherRole role, CancellationToken cancellationToken)
    {
        (string command, string arguments, string resumeArguments, string presetId) = role == LauncherRole.Reviewer
            ? (settings.ReviewerLauncherCommandPath, settings.ReviewerLauncherArguments, settings.ReviewerLauncherResumeArguments, settings.ReviewerLauncherPresetId)
            : (settings.ReviewedLauncherCommandPath, settings.ReviewedLauncherArguments, settings.ReviewedLauncherResumeArguments, settings.ReviewedLauncherPresetId);
        AgyModelSelection normal = await ResolveAsync(arguments, cancellationToken).ConfigureAwait(false);
        if (normal.Error is not null || !settings.SessionResumeEnabled || !LauncherSessionResumePolicy.Evaluate(command, arguments, resumeArguments, presetId, role).IsSupported)
        {
            return normal;
        }

        AgyModelSelection resumed = await ResolveAsync(resumeArguments, cancellationToken).ConfigureAwait(false);
        if (resumed.Error is not null)
        {
            return resumed;
        }

        return AgyAutoPausePolicy.GetLimitPrefix(normal.Model) == AgyAutoPausePolicy.GetLimitPrefix(resumed.Model)
            ? normal
            : new AgyModelSelection(null, "agy の通常起動と再開時のモデル枠が異なるため、更新時は全枠で Auto-Pause を評価します。");
    }
}
