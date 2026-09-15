// <copyright file="WindowIconServiceTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public sealed class WindowIconServiceTests
{
    [Fact]
    public void TrySetIcon_ShouldReturnFalseAndSkipNativeCalls_WhenIconFileDoesNotExist()
    {
        var nativeMethods = new FakeWindowIconNativeMethods();
        var service = new WindowIconService("C:\\app", _ => false, nativeMethods);

        bool result = service.TrySetIcon(new nint(10));

        result.Should().BeFalse();
        nativeMethods.LoadCalls.Should().BeEmpty();
        nativeMethods.SetCalls.Should().BeEmpty();
    }

    [Theory]
    [InlineData(101L, 202L, true, 2)]
    [InlineData(101L, 0L, true, 1)]
    [InlineData(0L, 202L, true, 1)]
    [InlineData(0L, 0L, false, 0)]
    public void TrySetIcon_ShouldApplyOnlySuccessfullyLoadedIcons(
        long smallIconHandle,
        long largeIconHandle,
        bool expectedResult,
        int expectedSetCount)
    {
        var nativeMethods = new FakeWindowIconNativeMethods(smallIconHandle, largeIconHandle);
        string? filePath = null;
        var service = new WindowIconService(
            "C:\\app",
            path =>
            {
                filePath = path;
                return true;
            },
            nativeMethods);

        bool result = service.TrySetIcon(new nint(10));

        result.Should().Be(expectedResult);
        string expectedPath = Path.Combine("C:\\app", "Assets", "squirrel-notifier.ico");
        filePath.Should().Be(expectedPath);
        nativeMethods.LoadCalls.Should().Equal(
            (expectedPath, 16),
            (expectedPath, 32));
        nativeMethods.SetCalls.Should().HaveCount(expectedSetCount);
        var expectedSetCalls = new List<(nint WindowHandle, WindowIconSize IconSize, nint IconHandle)>();
        if (smallIconHandle != 0)
        {
            expectedSetCalls.Add((new nint(10), WindowIconSize.Small, new nint(smallIconHandle)));
        }

        if (largeIconHandle != 0)
        {
            expectedSetCalls.Add((new nint(10), WindowIconSize.Large, new nint(largeIconHandle)));
        }

        nativeMethods.SetCalls.Should().Equal(expectedSetCalls);
    }

    [Fact]
    public void TrySetIcon_ShouldReturnFalse_WhenNativeCallThrows()
    {
        var nativeMethods = new FakeWindowIconNativeMethods
        {
            ExceptionToThrow = new InvalidOperationException("icon loading failed"),
        };
        var service = new WindowIconService("C:\\app", _ => true, nativeMethods);

        bool result = service.TrySetIcon(new nint(10));

        result.Should().BeFalse();
        nativeMethods.LoadCalls.Should().BeEmpty();
        nativeMethods.SetCalls.Should().BeEmpty();
    }

    [Theory]
    [InlineData(101L, 202L, 1, new long[] { 101L, 202L })]
    [InlineData(101L, 0L, 1, new long[] { 101L })]
    [InlineData(0L, 202L, 1, new long[] { 202L })]
    [InlineData(0L, 0L, 1, new long[0])]
    [InlineData(101L, 202L, 2, new long[] { 101L, 202L })]
    public void ReleaseIcons_ShouldDestroyEachLoadedIconOnce(
        long smallIconHandle,
        long largeIconHandle,
        int releaseCount,
        long[] expectedDestroyedHandles)
    {
        var nativeMethods = new FakeWindowIconNativeMethods(smallIconHandle, largeIconHandle);
        var service = new WindowIconService("C:\\app", _ => true, nativeMethods);
        _ = service.TrySetIcon(new nint(10));

        for (int i = 0; i < releaseCount; i++)
        {
            service.ReleaseIcons();
        }

        nativeMethods.DestroyCalls.Should().Equal(expectedDestroyedHandles.Select(value => new nint(value)));
    }

    [Fact]
    public void ReleaseIcons_ShouldDestroyLoadedIcon_WhenSetIconThrows()
    {
        var nativeMethods = new FakeWindowIconNativeMethods(101L, 202L)
        {
            SetIconExceptionToThrow = new InvalidOperationException("icon setting failed"),
        };
        var service = new WindowIconService("C:\\app", _ => true, nativeMethods);

        bool result = service.TrySetIcon(new nint(10));
        service.ReleaseIcons();

        result.Should().BeFalse();
        nativeMethods.DestroyCalls.Should().Equal(new nint(101));
    }

    [Fact]
    public void ReleaseIcons_ShouldSkipNativeCalls_WhenIconFileDoesNotExist()
    {
        var nativeMethods = new FakeWindowIconNativeMethods();
        var service = new WindowIconService("C:\\app", _ => false, nativeMethods);
        _ = service.TrySetIcon(new nint(10));

        service.ReleaseIcons();

        nativeMethods.DestroyCalls.Should().BeEmpty();
    }

    private sealed class FakeWindowIconNativeMethods : IWindowIconNativeMethods
    {
        private readonly Queue<nint> _loadResults;

        public FakeWindowIconNativeMethods(params long[] loadResults)
        {
            _loadResults = new Queue<nint>(loadResults.Select(value => new nint(value)));
        }

        public List<(string Path, int PixelSize)> LoadCalls { get; } = new();

        public List<(nint WindowHandle, WindowIconSize IconSize, nint IconHandle)> SetCalls { get; } = new();

        public List<nint> DestroyCalls { get; } = new();

        public Exception? ExceptionToThrow { get; init; }

        public Exception? SetIconExceptionToThrow { get; init; }

        public nint LoadIcon(string iconPath, int pixelSize)
        {
            if (ExceptionToThrow != null)
            {
                throw ExceptionToThrow;
            }

            LoadCalls.Add((iconPath, pixelSize));
            return _loadResults.Dequeue();
        }

        public void SetIcon(nint windowHandle, WindowIconSize iconSize, nint iconHandle)
        {
            if (SetIconExceptionToThrow != null)
            {
                throw SetIconExceptionToThrow;
            }

            SetCalls.Add((windowHandle, iconSize, iconHandle));
        }

        public void DestroyIcon(nint iconHandle)
        {
            DestroyCalls.Add(iconHandle);
        }
    }
}
