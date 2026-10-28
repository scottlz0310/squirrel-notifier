// <copyright file="LauncherWatchdog.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using SquirrelNotifier.WinUI3.Helpers;
using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

internal sealed class LauncherWatchdog : IDisposable
{
    private readonly object _lock = new();
    private readonly TimeProvider _timeProvider;
    private readonly long _started;
    private readonly LauncherWatchdogState _state;
    private readonly Action<LauncherWatchdogTimeout> _onTimeout;
    private readonly ITimer _timer;
    private bool _finished;

    public LauncherWatchdog(TimeProvider timeProvider, TimeSpan launcherLimit, TimeSpan noProgressLimit, TimeSpan toolLimit, Action<LauncherWatchdogTimeout> onTimeout)
    {
        _timeProvider = timeProvider;
        _started = timeProvider.GetTimestamp();
        _state = new LauncherWatchdogState(launcherLimit, noProgressLimit, toolLimit);
        _onTimeout = onTimeout;
        _timer = timeProvider.CreateTimer(_ => Check(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public void Observe(string line, LauncherOutputFormat format)
    {
        LauncherWatchdogTimeout timeout;
        lock (_lock)
        {
            if (_finished)
            {
                return;
            }

            timeout = _state.Observe(_timeProvider.GetElapsedTime(_started), LauncherActivityParser.Parse(line, format));
            _finished = timeout != LauncherWatchdogTimeout.None;
            if (timeout != LauncherWatchdogTimeout.None)
            {
                _onTimeout(timeout);
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _finished = true;
        }

        _timer.Dispose();
    }

    private void Check()
    {
        LauncherWatchdogTimeout timeout;
        lock (_lock)
        {
            if (_finished)
            {
                return;
            }

            timeout = _state.Check(_timeProvider.GetElapsedTime(_started));
            _finished = timeout != LauncherWatchdogTimeout.None;
            if (timeout != LauncherWatchdogTimeout.None)
            {
                _onTimeout(timeout);
            }
        }
    }
}
