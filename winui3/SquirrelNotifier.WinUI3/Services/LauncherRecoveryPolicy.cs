// <copyright file="LauncherRecoveryPolicy.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SquirrelNotifier.WinUI3.Services;

internal enum LauncherRecoveryAction
{
    /// <summary>同じ会話を再開する.</summary>
    Resume,

    /// <summary>利用者に対応を依頼する.</summary>
    Escalate,
}

internal sealed class LauncherRecoveryPolicy
{
    private LauncherWatchdogTimeout _previousFailure;
    private int _resumeCount;

    public LauncherRecoveryAction OnFailure(LauncherWatchdogTimeout failure)
    {
        if (failure == LauncherWatchdogTimeout.None)
        {
            throw new ArgumentException("失敗の種類が必要です。", nameof(failure));
        }

        if (failure == _previousFailure || _resumeCount >= 2)
        {
            return LauncherRecoveryAction.Escalate;
        }

        _previousFailure = failure;
        _resumeCount++;
        return LauncherRecoveryAction.Resume;
    }
}
