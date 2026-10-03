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
    [Fact]
    public void GetScrollTarget_InitialDisplay_ShouldSelectLastEntry()
    {
        LogTailViewModel model = new();
        LogDisplayEntry[] entries = [new("first"), new("last")];

        model.GetScrollTarget(entries, 200).Should().BeSameAs(entries[^1]);
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

        model.GetScrollTarget(entries, viewport).Should().BeNull();
        model.GetScrollTarget(entries, 200).Should().BeSameAs(entries[^1]);
    }

    [Fact]
    public void GetScrollTarget_EmptyDisplay_ShouldNotSelectEntry()
    {
        new LogTailViewModel().GetScrollTarget([], 200).Should().BeNull();
    }

    [Fact]
    public void GetScrollTarget_FollowingDisplay_ShouldRespondToAppendAndResize()
    {
        LogTailViewModel model = new();
        LogDisplayEntry[] entries = [new("last")];
        model.GetScrollTarget(entries, 200).Should().NotBeNull();
        model.GetScrollTarget(entries, 200).Should().BeNull();

        model.OnLogAdded();
        model.GetScrollTarget(entries, 200).Should().NotBeNull();
        model.OnLayoutChanged();
        model.GetScrollTarget(entries, 300).Should().NotBeNull();
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

        model.GetScrollTarget([new("last")], 200).Should().BeNull();
        model.NewEntryCount.Should().Be(2);
        model.NewEntryText.Should().Be("新着 2 件");
        model.ResumeVisibility.Should().Be(Visibility.Visible);
        changed.Should().Contain(nameof(LogTailViewModel.NewEntryText));
        changed.Should().Contain(nameof(LogTailViewModel.ResumeVisibility));

        model.ObserveViewport(0, 0, 300, false);
        model.OnLayoutChanged();
        model.GetScrollTarget([new("last")], 200).Should().BeNull();
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
        model.ObserveViewport(0, 400, 200, false);
        model.OnWheelInput(-120);
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
        model.ObserveViewport(0, 400, 200, false);
        model.ObserveViewport(400, 400, 200, false);
        model.IsFollowing.Should().BeFalse();
        model.EndPointerScroll();
        model.ObserveViewport(0, 400, 200, false);
        model.Pause();
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
    [InlineData(VirtualKey.Down, true)]
    [InlineData(VirtualKey.PageUp, false)]
    [InlineData(VirtualKey.PageDown, true)]
    [InlineData(VirtualKey.Home, false)]
    [InlineData(VirtualKey.End, true)]
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
        model.GetScrollTarget([new("last")], 200).Should().NotBeNull();
        model.ObserveViewport(0, 400, 200, false);
        model.IsFollowing.Should().BeTrue();
    }

    [Fact]
    public void ObserveViewport_LayoutAtTailBeforeWheelMovement_ShouldNotResume()
    {
        LogTailViewModel model = new();
        model.Pause();
        model.ObserveViewport(400, 400, 200, false);
        model.IsFollowing.Should().BeFalse();
        model.ObserveViewport(100, 400, 200, false);
        model.Pause();
        model.ObserveViewport(400, 400, 200, false);
        model.IsFollowing.Should().BeTrue();
    }

    [Theory]
    [InlineData(120, false)]
    [InlineData(-120, true)]
    [InlineData(0, true)]
    public void OnWheelInput_FollowingTail_ShouldPauseOnlyForUpwardMovement(int delta, bool following)
    {
        LogTailViewModel model = new();
        model.OnWheelInput(delta);
        model.IsFollowing.Should().Be(following);
    }

    [Theory]
    [InlineData(VirtualKey.Down)]
    [InlineData(VirtualKey.PageDown)]
    [InlineData(VirtualKey.End)]
    public void OnKeyInput_ReturningToTail_ShouldResume(VirtualKey key)
    {
        LogTailViewModel model = new();
        model.Pause();
        model.ObserveViewport(0, 400, 200, false);
        model.OnKeyInput(key);
        model.ObserveViewport(400, 400, 200, false);
        model.IsFollowing.Should().BeTrue();
    }

    [Fact]
    public void ScrollIfFollowing_UserInterruptsQueuedScroll_ShouldCancelMovement()
    {
        LogTailViewModel model = new();
        int movements = 0;
        model.ScrollIfFollowing(() => movements++);
        model.Pause();
        model.ScrollIfFollowing(() => movements++);
        movements.Should().Be(1);
    }

    [Theory]
    [InlineData(390, 400)]
    [InlineData(399, 400)]
    [InlineData(399.75, 400)]
    [InlineData(0, 24)]
    [InlineData(0, 10)]
    public void ObserveViewport_SmallWheelRoundTrip_ShouldResumeOnlyOnReturn(double offset, double extent)
    {
        LogTailViewModel model = new();
        model.OnWheelInput(120);
        model.ObserveViewport(extent, extent, 200, false);
        model.IsFollowing.Should().BeFalse("入力直後の古い末尾通知では再開しない");
        model.ObserveViewport(offset, extent, 200, true);
        model.ObserveViewport(offset, extent, 200, false);
        model.IsFollowing.Should().BeFalse("24px以内でも上向き移動では停止を維持する");
        model.OnLogAdded();

        model.OnWheelInput(-120);
        model.ObserveViewport(extent, extent, 200, false);
        model.IsFollowing.Should().BeTrue();
        model.NewEntryCount.Should().Be(0);
    }

    [Theory]
    [InlineData(390, 400)]
    [InlineData(0, 10)]
    public void ObserveViewport_SmallPointerRoundTrip_ShouldResumeAfterRelease(double offset, double extent)
    {
        LogTailViewModel model = new();
        model.BeginPointerScroll(true);
        model.ObserveViewport(offset, extent, 200, false);
        model.EndPointerScroll();
        model.ObserveViewport(offset, extent, 200, false);
        model.IsFollowing.Should().BeFalse();

        model.BeginPointerScroll(true);
        model.ObserveViewport(extent, extent, 200, false);
        model.IsFollowing.Should().BeFalse();
        model.EndPointerScroll();
        model.ObserveViewport(extent, extent, 200, false);
        model.IsFollowing.Should().BeTrue();
    }

    [Theory]
    [InlineData(VirtualKey.Down)]
    [InlineData(VirtualKey.PageDown)]
    [InlineData(VirtualKey.End)]
    public void OnKeyInput_SmallMovementThenReturn_ShouldResume(VirtualKey key)
    {
        LogTailViewModel model = new();
        model.OnKeyInput(VirtualKey.Up);
        model.ObserveViewport(390, 400, 200, false);
        model.IsFollowing.Should().BeFalse();
        model.OnKeyInput(key);
        model.ObserveViewport(400, 400, 200, false);
        model.IsFollowing.Should().BeTrue();
    }
}
