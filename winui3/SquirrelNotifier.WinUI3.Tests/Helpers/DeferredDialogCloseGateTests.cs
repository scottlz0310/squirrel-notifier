// <copyright file="DeferredDialogCloseGateTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Helpers;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Helpers;

public class DeferredDialogCloseGateTests
{
    [Fact]
    public void RequestClose_ShouldCloseImmediately_WhenAlreadyOpened()
    {
        var gate = new DeferredDialogCloseGate();

        gate.MarkOpened().Should().BeFalse("開いた時点ではクローズ要求がない");
        gate.RequestClose().Should().BeTrue("開き終わっているのでその場で閉じてよい");
    }

    [Fact]
    public void RequestClose_ShouldDefer_UntilOpened()
    {
        var gate = new DeferredDialogCloseGate();

        gate.RequestClose().Should().BeFalse("まだ開いていないので閉じられない");
        gate.MarkOpened().Should().BeTrue("保留していたクローズ要求をここで実行する");
    }

    [Fact]
    public void MarkOpened_ShouldNotRequestClose_WhenNoCloseRequested()
    {
        var gate = new DeferredDialogCloseGate();

        gate.MarkOpened().Should().BeFalse();
        gate.MarkOpened().Should().BeFalse("Opened が複数回発火しても勝手に閉じない");
    }

    [Fact]
    public void RequestClose_ShouldStayTrue_WhenCalledRepeatedlyAfterOpened()
    {
        var gate = new DeferredDialogCloseGate();

        gate.RequestClose().Should().BeFalse();
        gate.MarkOpened().Should().BeTrue();
        gate.RequestClose().Should().BeTrue("開いた後の再要求はそのまま閉じてよい");
    }
}
