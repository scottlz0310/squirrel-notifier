// <copyright file="AgentExecutionViewModelTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using SquirrelNotifier.WinUI3.Helpers;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.ViewModels;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.ViewModels;

public class AgentExecutionViewModelTests
{
    private static readonly DateTimeOffset _now = new(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);

    private static AgentExecutionViewModel CreateViewModel(bool autoCloseEnabled = true, params string?[] knownSecrets)
        => new("owner/repo#1（レビューする）", autoCloseEnabled, new SecretMasker(knownSecrets));

    private static AgentExecutionEvent StdoutEvent(string text)
        => new() { Kind = AgentExecutionEventKind.Stdout, Timestamp = _now, Text = text };

    private static AgentExecutionEvent StderrEvent(string text)
        => new() { Kind = AgentExecutionEventKind.Stderr, Timestamp = _now, Text = text };

    private static AgentExecutionEvent ProgressEvent(int phaseIndex, int totalPhases, string label, string? message = null, string? verdict = null)
        => new()
        {
            Kind = AgentExecutionEventKind.Progress,
            Timestamp = _now,
            Progress = new AgentProgressEvent(1, phaseIndex, totalPhases, label, message, verdict, null),
        };

    private static AgentExecutionEvent CompletedEvent(AgentExecutionOutcome outcome, LauncherResult? result = null)
        => new()
        {
            Kind = AgentExecutionEventKind.Completed,
            Timestamp = _now,
            Outcome = outcome,
            Result = result ?? new LauncherResult { Success = outcome == AgentExecutionOutcome.Succeeded },
        };

    [Fact]
    public void InitialState_ShouldBeRunningAndIndeterminate()
    {
        AgentExecutionViewModel vm = CreateViewModel();

        vm.IsRunning.Should().BeTrue();
        vm.IsIndeterminate.Should().BeTrue();
        vm.IsCompleted.Should().BeFalse();
        vm.Outcome.Should().BeNull();
        vm.Verdict.Should().BeNull();
        vm.LogLines.Should().BeEmpty();
    }

    [Fact]
    public void Apply_Stdout_ShouldAppendSanitizedAndMaskedLine()
    {
        AgentExecutionViewModel vm = CreateViewModel(autoCloseEnabled: true, "local-secret");

        vm.Apply(StdoutEvent("\x1b[32mtoken=local-secret\x1b[0m"));

        vm.LogLines.Should().ContainSingle();
        vm.LogLines[0].Kind.Should().Be(AgentExecutionEventKind.Stdout);
        vm.LogLines[0].Text.Should().Be("token=***");
        vm.LogLines[0].Timestamp.Should().Be(_now);
    }

    [Fact]
    public void Apply_Stderr_ShouldAppendLineWithStderrKind()
    {
        AgentExecutionViewModel vm = CreateViewModel();

        vm.Apply(StderrEvent("error output"));

        vm.LogLines.Should().ContainSingle().Which.Kind.Should().Be(AgentExecutionEventKind.Stderr);
    }

    [Fact]
    public void Apply_Progress_ShouldUpdatePhaseStateAndAppendLogLine()
    {
        AgentExecutionViewModel vm = CreateViewModel();

        vm.Apply(ProgressEvent(3, 8, "修正", message: "accept 2件"));

        vm.IsIndeterminate.Should().BeFalse();
        vm.ProgressValue.Should().Be(50);
        vm.StatusText.Should().Be("Phase 4/8: 修正");
        vm.LogLines.Should().ContainSingle();
        vm.LogLines[0].Kind.Should().Be(AgentExecutionEventKind.Progress);
        vm.LogLines[0].Text.Should().Be("Phase 4/8: 修正 — accept 2件");
    }

    [Fact]
    public void Apply_Progress_ShouldUpdateVerdict_AndKeepItAfterwards()
    {
        AgentExecutionViewModel vm = CreateViewModel();

        vm.Apply(ProgressEvent(7, 8, "Verdict 待機", verdict: "APPROVED"));
        vm.Apply(ProgressEvent(7, 8, "Verdict 待機"));

        vm.Verdict.Should().Be("APPROVED", "verdict 無しの後続イベントで上書きされない");
    }

    [Theory]
    [InlineData(0, 8, 12.5)]
    [InlineData(7, 8, 100)]

    // phaseIndex が totalPhases 以上（1 始まりで出力する producer）でも 100% へ clamp
    [InlineData(8, 8, 100)]
    [InlineData(0, 1, 100)]
    public void Apply_Progress_ShouldCalculateProgressValue(int phaseIndex, int totalPhases, double expected)
    {
        AgentExecutionViewModel vm = CreateViewModel();

        vm.Apply(ProgressEvent(phaseIndex, totalPhases, "phase"));

        vm.ProgressValue.Should().Be(expected);
    }

    [Theory]
    [InlineData("Succeeded", "完了しました")]
    [InlineData("Cancelled", "キャンセルされました")]
    [InlineData("TimedOut", "タイムアウトしました")]
    public void Apply_Completed_ShouldSetTerminalState(string outcomeName, string expectedStatus)
    {
        AgentExecutionOutcome outcome = Enum.Parse<AgentExecutionOutcome>(outcomeName);
        AgentExecutionViewModel vm = CreateViewModel();

        vm.Apply(CompletedEvent(outcome));

        vm.IsRunning.Should().BeFalse();
        vm.IsCompleted.Should().BeTrue();
        vm.Outcome.Should().Be(outcome);
        vm.StatusText.Should().Be(expectedStatus);
    }

    [Fact]
    public void Apply_CompletedWithFailure_ShouldIncludeErrorMessage()
    {
        AgentExecutionViewModel vm = CreateViewModel();

        vm.Apply(CompletedEvent(
            AgentExecutionOutcome.Failed,
            new LauncherResult { Success = false, ErrorMessage = "起動に失敗" }));

        vm.StatusText.Should().Be("失敗しました: 起動に失敗");
    }

    [Fact]
    public void Apply_CompletedWithNonZeroExitCode_ShouldIncludeExitCode()
    {
        AgentExecutionViewModel vm = CreateViewModel();

        vm.Apply(CompletedEvent(
            AgentExecutionOutcome.Failed,
            new LauncherResult { Success = false, ExitCode = 2 }));

        vm.StatusText.Should().Be("失敗しました（終了コード: 2）");
    }

    [Theory]
    [InlineData("Succeeded", true, true)]
    [InlineData("Succeeded", false, false)]
    [InlineData("Failed", true, false)]
    [InlineData("Cancelled", true, false)]
    [InlineData("TimedOut", true, false)]
    public void ShouldAutoClose_ShouldBeTrueOnlyForSuccessWithAutoCloseEnabled(
        string outcomeName, bool autoCloseEnabled, bool expected)
    {
        AgentExecutionOutcome outcome = Enum.Parse<AgentExecutionOutcome>(outcomeName);
        AgentExecutionViewModel vm = CreateViewModel(autoCloseEnabled);

        vm.Apply(CompletedEvent(outcome));

        vm.ShouldAutoClose.Should().Be(expected);
    }

    [Fact]
    public void Apply_ShouldTrimOldestLines_WhenExceedingMaxLogLines()
    {
        AgentExecutionViewModel vm = CreateViewModel();

        for (int i = 0; i < AgentExecutionViewModel.MaxLogLines + 5; i++)
        {
            vm.Apply(StdoutEvent($"line-{i}"));
        }

        vm.LogLines.Should().HaveCount(AgentExecutionViewModel.MaxLogLines);
        vm.LogLines[0].Text.Should().Be("line-5", "古い行から破棄される");
        vm.LogLines[^1].Text.Should().Be($"line-{AgentExecutionViewModel.MaxLogLines + 4}");
    }

    [Fact]
    public void ApplyBatch_ShouldApplyAllEventsInOrder()
    {
        AgentExecutionViewModel vm = CreateViewModel();

        vm.ApplyBatch(
        [
            StdoutEvent("first"),
            ProgressEvent(0, 2, "sync"),
            CompletedEvent(AgentExecutionOutcome.Succeeded),
        ]);

        vm.LogLines.Select(l => l.Kind).Should().ContainInOrder(
            AgentExecutionEventKind.Stdout, AgentExecutionEventKind.Progress);
        vm.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public void Apply_Completed_ShouldRaisePropertyChangedForDerivedProperties()
    {
        AgentExecutionViewModel vm = CreateViewModel();
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? string.Empty);

        vm.Apply(CompletedEvent(AgentExecutionOutcome.Succeeded));

        changed.Should().Contain(nameof(AgentExecutionViewModel.IsRunning));
        changed.Should().Contain(nameof(AgentExecutionViewModel.Outcome));
        changed.Should().Contain(nameof(AgentExecutionViewModel.IsCompleted));
        changed.Should().Contain(nameof(AgentExecutionViewModel.ShouldAutoClose));
    }
}
