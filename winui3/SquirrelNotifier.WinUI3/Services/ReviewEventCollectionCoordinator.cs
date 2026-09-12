// <copyright file="ReviewEventCollectionCoordinator.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Collections.ObjectModel;
using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>
/// Recent review events の表示順、保持上限、削除を管理する.
/// </summary>
internal sealed class ReviewEventCollectionCoordinator
{
    public const int MaxEvents = 20;

    private readonly ObservableCollection<ReviewEvent> _events = new();

    public ReviewEventCollectionCoordinator()
    {
        Events = new ReadOnlyObservableCollection<ReviewEvent>(_events);
    }

    public ReadOnlyObservableCollection<ReviewEvent> Events { get; }

    public ReviewEvent? Add(ReviewEvent reviewEvent)
    {
        ArgumentNullException.ThrowIfNull(reviewEvent);

        _events.Insert(0, reviewEvent);
        if (_events.Count <= MaxEvents)
        {
            return null;
        }

        ReviewEvent evictedEvent = _events[^1];
        _events.RemoveAt(_events.Count - 1);
        return evictedEvent;
    }

    public bool Remove(ReviewEvent reviewEvent)
    {
        ArgumentNullException.ThrowIfNull(reviewEvent);
        return _events.Remove(reviewEvent);
    }

    public bool RemoveByEventId(string eventId)
    {
        if (string.IsNullOrWhiteSpace(eventId))
        {
            return false;
        }

        for (int index = 0; index < _events.Count; index++)
        {
            if (string.Equals(_events[index].EventId, eventId, StringComparison.Ordinal))
            {
                _events.RemoveAt(index);
                return true;
            }
        }

        return false;
    }
}
