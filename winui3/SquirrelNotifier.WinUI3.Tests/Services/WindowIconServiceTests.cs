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

    private sealed class FakeWindowIconNativeMethods : IWindowIconNativeMethods
    {
        private readonly Queue<nint> _loadResults;

        public FakeWindowIconNativeMethods(params long[] loadResults)
        {
            _loadResults = new Queue<nint>(loadResults.Select(value => new nint(value)));
        }

        public List<(string Path, int PixelSize)> LoadCalls { get; } = new();

        public List<(nint WindowHandle, WindowIconSize IconSize, nint IconHandle)> SetCalls { get; } = new();

        public Exception? ExceptionToThrow { get; init; }

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
            SetCalls.Add((windowHandle, iconSize, iconHandle));
        }
    }
}
