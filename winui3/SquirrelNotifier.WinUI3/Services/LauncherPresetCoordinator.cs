// <copyright file="LauncherPresetCoordinator.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

internal sealed record LauncherPresetValues(
    string Command,
    string Arguments,
    string ResumeArguments);

/// <summary>Launcher プリセットの UI 反映に必要な状態と判断を管理する（#267）.</summary>
internal sealed class LauncherPresetCoordinator
{
    private bool _isApplying;
    private bool _isSynchronizing;

    public bool IsApplying => _isApplying;

    public bool IsSynchronizing => _isSynchronizing;

    public bool TryApply(
        LauncherAgentDefinition? selected,
        LauncherRole role,
        Action<LauncherPresetValues> apply)
    {
        ArgumentNullException.ThrowIfNull(apply);

        if (selected is null || selected.Id == LauncherAgentCatalog.CustomPresetId)
        {
            return false;
        }

        string arguments = role switch
        {
            LauncherRole.Reviewer => selected.ReviewerArgumentsTemplate,
            LauncherRole.Reviewed => selected.ReviewedArgumentsTemplate,
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, "未知の launcher role です。"),
        };
        string resumeArguments = role switch
        {
            LauncherRole.Reviewer => selected.ReviewerResumeArgumentsTemplate,
            LauncherRole.Reviewed => selected.ReviewedResumeArgumentsTemplate,
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, "未知の launcher role です。"),
        };

        _isApplying = true;
        try
        {
            apply(new LauncherPresetValues(selected.Command, arguments, resumeArguments));
        }
        finally
        {
            _isApplying = false;
        }

        return true;
    }

    public bool TrySynchronizeSelection(
        string presetId,
        Func<LauncherAgentDefinition?> currentSelection,
        Action<LauncherAgentDefinition?> setSelection)
    {
        ArgumentNullException.ThrowIfNull(currentSelection);
        ArgumentNullException.ThrowIfNull(setSelection);

        LauncherAgentDefinition? match = LauncherAgentCatalog.FindWithCustomOption(presetId);
        if (Equals(currentSelection(), match))
        {
            return false;
        }

        _isSynchronizing = true;
        try
        {
            setSelection(match);
        }
        finally
        {
            _isSynchronizing = false;
        }

        return true;
    }
}
