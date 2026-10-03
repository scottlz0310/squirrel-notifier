// <copyright file="LogEntryCollectionCoordinator.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Collections.ObjectModel;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>
/// Recent activity に表示するログ行の保持数と追加順を管理する.
/// </summary>
internal sealed class LogEntryCollectionCoordinator
{
    public const int MaxEntries = 200;

    private readonly ObservableCollection<LogDisplayEntry> _entries = new();

    public LogEntryCollectionCoordinator()
    {
        Entries = new ReadOnlyObservableCollection<LogDisplayEntry>(_entries);
    }

    public ReadOnlyObservableCollection<LogDisplayEntry> Entries { get; }

    public void Add(string line)
    {
        _entries.Add(new LogDisplayEntry(line));
        if (_entries.Count > MaxEntries)
        {
            _entries.RemoveAt(0);
        }
    }
}
