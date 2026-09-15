// <copyright file="RequestTokenBucket.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>
/// 一定速度で補充されるトークンで外部 API の呼出し回数を制限する.
/// 前借りを使わない限り、任意の 1 時間に取得できるトークン数は容量と 1 時間あたりの補充数の和を超えない.
/// </summary>
internal sealed class RequestTokenBucket
{
    private readonly object _lock = new();
    private readonly double _capacity;
    private readonly double _refillPerHour;
    private readonly TimeProvider _timeProvider;
    private double _tokens;
    private DateTimeOffset _lastRefillAt;

    public RequestTokenBucket(int capacity, int refillPerHour, TimeProvider timeProvider)
    {
        _capacity = capacity;
        _refillPerHour = refillPerHour;
        _timeProvider = timeProvider;
        _tokens = capacity;
        _lastRefillAt = timeProvider.GetUtcNow();
    }

    /// <summary>トークンが 1 個以上あれば 1 個消費する.</summary>
    /// <returns>消費できた場合は <see langword="true"/>.</returns>
    public bool TryAcquire()
    {
        lock (_lock)
        {
            Refill();
            if (_tokens < 1)
            {
                return false;
            }

            _tokens -= 1;
            return true;
        }
    }

    /// <summary>
    /// トークンが不足していても 1 個消費する。不足分は前借りとなり、後続の <see cref="TryAcquire"/> を遅らせる.
    /// </summary>
    public void AcquireWithDebt()
    {
        lock (_lock)
        {
            Refill();
            _tokens -= 1;
        }
    }

    private void Refill()
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        TimeSpan elapsed = now - _lastRefillAt;
        if (elapsed <= TimeSpan.Zero)
        {
            return;
        }

        _tokens = Math.Min(_capacity, _tokens + (elapsed.TotalHours * _refillPerHour));
        _lastRefillAt = now;
    }
}
