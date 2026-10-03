// <copyright file="LogTailViewModel.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using Microsoft.UI.Xaml;
using SquirrelNotifier.WinUI3.Helpers;
using SquirrelNotifier.WinUI3.Services;
using Windows.System;

namespace SquirrelNotifier.WinUI3.ViewModels;

/// <summary>ログ末尾への追従と、過去ログを読む間の新着件数を保持する.</summary>
[SuppressMessage("Design", "CA1515", Justification = "WinUI XAML のリソースとして生成するため public が必要です")]
public sealed class LogTailViewModel : INotifyPropertyChanged
{
    private bool _pointerScrolling;
    private bool _scrollPending = true;
    private bool _userScrollPending;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsFollowing { get; private set; } = true;

    public int NewEntryCount { get; private set; }

    public string NewEntryText => NewEntryCount == 0 ? "自動追従を停止中" : $"新着 {NewEntryCount} 件";

    public Visibility ResumeVisibility => IsFollowing ? Visibility.Collapsed : Visibility.Visible;

    internal void OnLogAdded()
    {
        if (IsFollowing)
        {
            _scrollPending = true;
        }
        else
        {
            NewEntryCount++;
            NotifyStateChanged();
        }
    }

    internal void Pause()
    {
        IsFollowing = false;
        _scrollPending = false;
        _userScrollPending = true;
        NotifyStateChanged();
    }

    internal void Resume()
    {
        IsFollowing = true;
        NewEntryCount = 0;
        _scrollPending = true;
        _userScrollPending = false;
        NotifyStateChanged();
    }

    internal void OnKeyInput(VirtualKey key)
    {
        if (key is VirtualKey.Up or VirtualKey.Down or VirtualKey.PageUp or VirtualKey.PageDown or VirtualKey.Home or VirtualKey.End)
        {
            Pause();
        }
    }

    internal void BeginPointerScroll(bool isScrollBar)
    {
        if (isScrollBar)
        {
            _pointerScrolling = true;
            Pause();
        }
    }

    internal void EndPointerScroll()
    {
        _pointerScrolling = false;
    }

    internal void ObserveViewport(double verticalOffset, double scrollableHeight, double viewportHeight, bool isIntermediate)
    {
        // 非表示・レイアウト変更だけでは、過去ログを読む状態を解除しない。
        if (!_userScrollPending || _pointerScrolling || isIntermediate || viewportHeight <= 0)
        {
            return;
        }

        _userScrollPending = false;
        if (LogFollowPolicy.ShouldFollow(verticalOffset, scrollableHeight))
        {
            Resume();
        }
    }

    internal LogDisplayEntry? GetScrollTarget(
        IReadOnlyList<LogDisplayEntry> entries, double verticalOffset, double scrollableHeight, double viewportHeight)
    {
        if (!IsFollowing || entries.Count == 0 || viewportHeight <= 0)
        {
            return null;
        }

        if (!_scrollPending && LogFollowPolicy.ShouldFollow(verticalOffset, scrollableHeight))
        {
            return null;
        }

        _scrollPending = false;
        return entries[^1];
    }

    private void NotifyStateChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFollowing)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NewEntryCount)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NewEntryText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ResumeVisibility)));
    }
}
