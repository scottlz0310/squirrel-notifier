// <copyright file="LogTailViewModelTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using Microsoft.UI.Xaml;
using SquirrelNotifier.WinUI3.Services;
using SquirrelNotifier.WinUI3.ViewModels;
using Windows.System;

namespace SquirrelNotifier.WinUI3.Tests.ViewModels;

public class LogTailViewModelTests
{
    [Theory]
    [InlineData(0, 400, 200)]
    [InlineData(400, 400, 200)]
    [InlineData(0, 0, 200)]
    public void GetScrollTarget_InitialDisplay_ShouldSelectLastEntry(double offset, double extent, double viewport)
    {
        LogTailViewModel model = new();
        LogDisplayEntry[] entries = [new("first"), new("last")];

        model.GetScrollTarget(entries, offset, extent, viewport).Should().BeSameAs(entries[^1]);
        model.IsFollowing.Should().BeTrue();
        model.ResumeVisibility.Should().Be(Visibility.Collapsed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void GetScrollTarget_HiddenDisplay_ShouldPreservePendingScroll(double viewport)
    {
        LogTailViewModel model = new();
        LogDisplayEntry[] entries = [new("last")];

        model.GetScrollTarget(entries, 0, 400, viewport).Should().BeNull();
        model.GetScrollTarget(entries, 0, 400, 200).Should().BeSameAs(entries[^1]);
    }

    [Fact]
    public void GetScrollTarget_EmptyDisplay_ShouldNotSelectEntry()
    {
        new LogTailViewModel().GetScrollTarget([], 0, 0, 200).Should().BeNull();
    }

    [Fact]
    public void GetScrollTarget_FollowingDisplay_ShouldRespondToAppendAndResize()
    {
        LogTailViewModel model = new();
        LogDisplayEntry[] entries = [new("last")];
        model.GetScrollTarget(entries, 400, 400, 200).Should().NotBeNull();
        model.GetScrollTarget(entries, 400, 400, 200).Should().BeNull();

        model.OnLogAdded();
        model.GetScrollTarget(entries, 400, 400, 200).Should().NotBeNull();
        model.GetScrollTarget(entries, 0, 400, 300).Should().NotBeNull();
        model.NewEntryCount.Should().Be(0);
    }

    [Fact]
    public void Pause_ReadingHistory_ShouldPreservePositionAndCountNewEntries()
    {
        LogTailViewModel model = new();
        List<string?> changed = [];
        model.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        model.Pause();
        model.ObserveViewport(0, 400, 200, false);
        model.OnLogAdded();
        model.OnLogAdded();

        model.GetScrollTarget([new("last")], 0, 400, 200).Should().BeNull();
        model.NewEntryCount.Should().Be(2);
        model.NewEntryText.Should().Be("新着 2 件");
        model.ResumeVisibility.Should().Be(Visibility.Visible);
        changed.Should().Contain(nameof(LogTailViewModel.NewEntryText));
        changed.Should().Contain(nameof(LogTailViewModel.ResumeVisibility));

        model.ObserveViewport(0, 0, 300, false);
        model.IsFollowing.Should().BeFalse("サイズ変更だけでは過去ログを読む状態を解除しない");
    }

    [Theory]
    [InlineData(400, 400)]
    [InlineData(380, 400)]
    [InlineData(0, 0)]
    public void ObserveViewport_UserReturnsToTail_ShouldResumeAndResetCount(double offset, double extent)
    {
        LogTailViewModel model = new();
        model.Pause();
        model.OnLogAdded();
        model.ObserveViewport(offset, extent, 200, false);

        model.IsFollowing.Should().BeTrue();
        model.NewEntryCount.Should().Be(0);
    }

    [Fact]
    public void ObserveViewport_PointerDragOrIntermediateChange_ShouldNotResumeEarly()
    {
        LogTailViewModel model = new();
        model.BeginPointerScroll(true);
        model.ObserveViewport(400, 400, 200, false);
        model.IsFollowing.Should().BeFalse();
        model.EndPointerScroll();
        model.ObserveViewport(400, 400, 200, true);
        model.IsFollowing.Should().BeFalse();
        model.ObserveViewport(400, 400, 0, false);
        model.IsFollowing.Should().BeFalse();
        model.ObserveViewport(400, 400, 200, false);
        model.IsFollowing.Should().BeTrue();
    }

    [Fact]
    public void BeginPointerScroll_OrdinaryLogClick_ShouldKeepFollowing()
    {
        LogTailViewModel model = new();
        model.BeginPointerScroll(false);
        model.IsFollowing.Should().BeTrue();
    }

    [Theory]
    [InlineData(VirtualKey.Up, false)]
    [InlineData(VirtualKey.Down, false)]
    [InlineData(VirtualKey.PageUp, false)]
    [InlineData(VirtualKey.PageDown, false)]
    [InlineData(VirtualKey.Home, false)]
    [InlineData(VirtualKey.End, false)]
    [InlineData(VirtualKey.A, true)]
    public void OnKeyInput_ShouldPauseOnlyForScrollKeys(VirtualKey key, bool following)
    {
        LogTailViewModel model = new();
        model.OnKeyInput(key);
        model.IsFollowing.Should().Be(following);
    }

    [Fact]
    public void Resume_ReturnButton_ShouldSelectTailAndResetCount()
    {
        LogTailViewModel model = new();
        model.Pause();
        model.NewEntryText.Should().Be("自動追従を停止中");
        model.OnLogAdded();
        model.Resume();

        model.NewEntryCount.Should().Be(0);
        model.GetScrollTarget([new("last")], 0, 400, 200).Should().NotBeNull();
        model.ObserveViewport(0, 400, 200, false);
        model.IsFollowing.Should().BeTrue();
    }
}
