// <copyright file="WindowPlacementCalculatorTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Helpers;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Helpers;

public class WindowPlacementCalculatorTests
{
    [Theory]
    [InlineData(0, 0, 1920, 1080, 400, 300, 16, 1504, 764)]
    [InlineData(0, 0, 1920, 1080, 400, 300, 0, 1520, 780)]

    // マルチモニター: 負座標のセカンダリモニター（プライマリの左側）でも work area 内に収まる
    [InlineData(-1920, 0, 1920, 1080, 400, 300, 16, -416, 764)]

    // タスクバー分の work area オフセット（上側タスクバー等）
    [InlineData(0, 48, 2560, 1392, 500, 400, 16, 2044, 1024)]
    public void CalculateBottomRight_ShouldPlaceWindowInsideWorkArea(
        int workX, int workY, int workWidth, int workHeight, int windowWidth, int windowHeight, int margin, int expectedX, int expectedY)
    {
        (int x, int y) = WindowPlacementCalculator.CalculateBottomRight(
            workX, workY, workWidth, workHeight, windowWidth, windowHeight, margin);

        x.Should().Be(expectedX);
        y.Should().Be(expectedY);

        // 左上が work area 内に収まっていること（画面外配置の防止）
        x.Should().BeGreaterThanOrEqualTo(workX);
        y.Should().BeGreaterThanOrEqualTo(workY);
    }

    [Theory]

    // ウィンドウが work area より大きい場合は左上へ clamp する
    [InlineData(0, 0, 800, 600, 1000, 700, 16, 0, 0)]
    [InlineData(100, 50, 800, 600, 1000, 700, 16, 100, 50)]

    // 高さのみ超過
    [InlineData(0, 0, 1920, 400, 400, 500, 16, 1504, 0)]
    public void CalculateBottomRight_ShouldClampToWorkAreaOrigin_WhenWindowIsLarger(
        int workX, int workY, int workWidth, int workHeight, int windowWidth, int windowHeight, int margin, int expectedX, int expectedY)
    {
        (int x, int y) = WindowPlacementCalculator.CalculateBottomRight(
            workX, workY, workWidth, workHeight, windowWidth, windowHeight, margin);

        x.Should().Be(expectedX);
        y.Should().Be(expectedY);
    }
}
