// <copyright file="LauncherWatchdogState.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using SquirrelNotifier.WinUI3.Helpers;

namespace SquirrelNotifier.WinUI3.Services;

internal enum LauncherWatchdogTimeout
{
    /// <summary>上限未到達.</summary>
    None,

    /// <summary>無進捗の上限.</summary>
    NoProgress,

    /// <summary>ランチャーの上限.</summary>
    Launcher,

    /// <summary>ツールごとの上限.</summary>
    Tool,
}

internal sealed class LauncherWatchdogState(TimeSpan launcherLimit, TimeSpan noProgressLimit, TimeSpan toolLimit)
{
    private readonly Dictionary<string, TimeSpan> _activeTools = [];
    private TimeSpan _last;
    private TimeSpan _launcherElapsed;
    private TimeSpan _idleElapsed;

    public LauncherWatchdogTimeout Observe(TimeSpan elapsed, IReadOnlyList<LauncherToolActivity> activities)
    {
        LauncherWatchdogTimeout timeout = Check(elapsed);
        if (timeout != LauncherWatchdogTimeout.None)
        {
            return timeout;
        }

        _idleElapsed = TimeSpan.Zero;
        foreach (LauncherToolActivity activity in activities)
        {
            if (activity.Kind == LauncherToolActivityKind.Started)
            {
                _activeTools.TryAdd(activity.Id, elapsed);
            }
            else
            {
                _activeTools.Remove(activity.Id);
            }
        }

        return LauncherWatchdogTimeout.None;
    }

    public LauncherWatchdogTimeout Check(TimeSpan elapsed)
    {
        Advance(elapsed);
        if (_activeTools.Values.Any(start => elapsed - start >= toolLimit))
        {
            return LauncherWatchdogTimeout.Tool;
        }

        if (_launcherElapsed >= launcherLimit)
        {
            return LauncherWatchdogTimeout.Launcher;
        }

        return _activeTools.Count == 0 && _idleElapsed >= noProgressLimit
            ? LauncherWatchdogTimeout.NoProgress
            : LauncherWatchdogTimeout.None;
    }

    private void Advance(TimeSpan elapsed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(elapsed, _last);

        if (_activeTools.Count == 0)
        {
            TimeSpan delta = elapsed - _last;
            _launcherElapsed += delta;
            _idleElapsed += delta;
        }

        _last = elapsed;
    }
}
