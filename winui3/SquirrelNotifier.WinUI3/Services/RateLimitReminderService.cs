// <copyright file="RateLimitReminderService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace SquirrelNotifier.WinUI3.Services;

internal sealed class RateLimitReminderService : IRateLimitReminderService, IDisposable
{
    // Task.Delay がサポートする最大遅延（int.MaxValue ミリ秒 = 約24.8日）。
    // 想定利用（5H/7D 制限）を大きく上回るが、異常な resetAt でも例外にならないようクランプする。
    private static readonly TimeSpan _maxDelay = TimeSpan.FromMilliseconds(int.MaxValue - 1);

    private readonly INotificationService _notificationService;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _reminders = new();

    public RateLimitReminderService(INotificationService notificationService)
    {
        _notificationService = notificationService;
    }

    public bool IsScheduled(string id) => _reminders.ContainsKey(id);

    public void Schedule(string id, string label, DateTimeOffset resetAt)
    {
        Cancel(id);

        var cts = new CancellationTokenSource();
        _reminders[id] = cts;

        TimeSpan delay = resetAt - DateTimeOffset.Now;
        if (delay < TimeSpan.Zero)
        {
            delay = TimeSpan.Zero;
        }
        else if (delay > _maxDelay)
        {
            delay = _maxDelay;
        }

        _ = RunReminderAsync(id, label, delay, cts);
    }

    public void Cancel(string id)
    {
        if (_reminders.TryRemove(id, out CancellationTokenSource? existing))
        {
            existing.Cancel();
            existing.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (CancellationTokenSource cts in _reminders.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }

        _reminders.Clear();
    }

    private async Task RunReminderAsync(string id, string label, TimeSpan delay, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(delay, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // id だけで削除すると、Task.Delay 完了と Cancel/再 Schedule が競合した際に
        // 後から登録された新しい CancellationTokenSource を誤って取り除いてしまう。
        // 値（CTS インスタンス）も一致する場合のみ削除することで、この取り違えを防ぐ。
        var entry = new KeyValuePair<string, CancellationTokenSource>(id, cts);
        if (((ICollection<KeyValuePair<string, CancellationTokenSource>>)_reminders).Remove(entry))
        {
            cts.Dispose();
            _notificationService.NotifyRateLimitReset(label);
        }
    }
}
