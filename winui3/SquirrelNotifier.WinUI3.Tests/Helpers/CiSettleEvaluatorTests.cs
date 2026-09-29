// <copyright file="CiSettleEvaluatorTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Globalization;
using FluentAssertions;
using SquirrelNotifier.WinUI3.Helpers;
using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Tests.Helpers;

// 入力は ';' 区切りの短い記法で渡す（enum は internal のため InlineData では名前で受け取る）。
//   required: "name" または "name@appId"
//   run:      "name|status|conclusion|id[|appId]"（conclusion が未完了なら "-"）
//   status:   "context|state"
public sealed class CiSettleEvaluatorTests
{
    private const string _headSha = "0123456789abcdef0123456789abcdef01234567";

    [Theory]
    [InlineData("build;lint", "build|completed|success|1;lint|completed|success|2", "", "Passed", "required checks 2 件")]
    [InlineData("build;lint", "build|completed|success|1;lint|in_progress|-|2", "", "Pending", "未完了: lint")]
    [InlineData("build;lint", "build|completed|success|1;lint|queued|-|2", "", "Pending", "未完了: lint")]
    [InlineData("build;codecov/patch", "build|completed|success|1", "", "Pending", "codecov/patch（未報告）")]
    [InlineData("build;lint", "build|completed|failure|1;lint|in_progress|-|2", "", "Failed", "失敗: build")]
    [InlineData("build", "build|completed|cancelled|1", "", "Failed", "失敗: build")]
    [InlineData("build", "build|completed|timed_out|1", "", "Failed", "失敗: build")]
    [InlineData("build", "build|completed|action_required|1", "", "Failed", "失敗: build")]
    [InlineData("build;lint", "build|completed|neutral|1;lint|completed|skipped|2", "", "Passed", "required checks 2 件")]
    public void Evaluate_ShouldJudgeRequiredChecks_FromCheckRuns(
        string required,
        string runs,
        string statuses,
        string expectedState,
        string expectedDetail)
        => AssertEvaluation(required, runs, statuses, expectedState, expectedDetail);

    // 再実行で同じ名前の run が増える。ID が最大のものが現在の結果
    [Theory]
    [InlineData("build|completed|failure|1;build|completed|success|2", "Passed")]
    [InlineData("build|completed|success|1;build|completed|failure|2", "Failed")]
    [InlineData("build|completed|success|1;build|in_progress|-|2", "Pending")]
    [InlineData("build|in_progress|-|1;build|completed|success|2", "Passed")]
    public void Evaluate_ShouldAdoptLatestRun_WhenSameNameIsReRun(string runs, string expectedState)
        => AssertEvaluation("build", runs, string.Empty, expectedState, expectedDetail: null);

    // 別の App が同じ名前で報告した run は別物として扱う
    [Theory]
    [InlineData("build@15368", "build|completed|success|1|15368", "Passed")]
    [InlineData("build@15368", "build|completed|success|1|99", "Pending")]
    [InlineData("build@15368", "build|completed|failure|1|99;build|completed|success|2|15368", "Passed")]
    [InlineData("build", "build|completed|success|1|99", "Passed")]
    public void Evaluate_ShouldMatchIntegrationId_WhenRequiredCheckSpecifiesApp(string required, string runs, string expectedState)
        => AssertEvaluation(required, runs, string.Empty, expectedState, expectedDetail: null);

    // required には commit status（Status API）の context も入り得る
    [Theory]
    [InlineData("ci/status", "", "ci/status|success", "Passed")]
    [InlineData("ci/status", "", "ci/status|pending", "Pending")]
    [InlineData("ci/status", "", "ci/status|failure", "Failed")]
    [InlineData("ci/status", "", "ci/status|error", "Failed")]
    [InlineData("ci/status;build", "build|completed|success|1", "ci/status|pending", "Pending")]
    [InlineData("ci/status;build", "build|completed|failure|1", "ci/status|pending", "Failed")]
    public void Evaluate_ShouldJudgeRequiredChecks_FromCommitStatuses(
        string required,
        string runs,
        string statuses,
        string expectedState)
        => AssertEvaluation(required, runs, statuses, expectedState, expectedDetail: null);

    // App が指定された required check は、App ID を照合できる check run だけで判定する。
    // commit status には送信元の App ID が無いため、別の App が同名の context に送った success で通してはならない
    [Theory]
    [InlineData("build@15368", "", "build|success", "Pending", "build（未報告）")]
    [InlineData("build@15368", "build|completed|success|1|99", "build|success", "Pending", "build（未報告）")]
    [InlineData("build@15368", "build|completed|failure|1|99", "build|success", "Pending", "build（未報告）")]
    [InlineData("build@15368", "build|completed|success|1|15368", "build|failure", "Passed", "required checks 1 件")]
    [InlineData("build@15368", "build|in_progress|-|1|15368", "build|success", "Pending", "未完了: build")]
    [InlineData("build", "", "build|success", "Passed", "required checks 1 件")]
    public void Evaluate_ShouldNotFallBackToCommitStatus_WhenRequiredCheckSpecifiesApp(
        string required,
        string runs,
        string statuses,
        string expectedState,
        string expectedDetail)
        => AssertEvaluation(required, runs, statuses, expectedState, expectedDetail);

    [Fact]
    public void Evaluate_ShouldIgnoreFailingOptionalCheck_WhenRequiredChecksAreDefined()
        => AssertEvaluation(
            "build",
            "build|completed|success|1;optional-lint|completed|failure|2",
            string.Empty,
            "Passed",
            expectedDetail: null);

    // required の定義が無いリポジトリでは、報告済みの check すべてを対象にする
    [Theory]
    [InlineData("", "", "Passed", "報告済みの check もありません")]
    [InlineData("build|completed|success|1;lint|completed|success|2", "", "Passed", "check 2 件がすべて完了")]
    [InlineData("build|completed|success|1;lint|in_progress|-|2", "", "Pending", "未完了: lint")]
    [InlineData("build|completed|failure|1;lint|in_progress|-|2", "", "Failed", "失敗: build")]
    [InlineData("build|completed|success|1", "ci/status|pending", "Pending", "未完了: ci/status")]
    [InlineData("build|completed|success|1|1;build|completed|failure|2|2", "", "Failed", "失敗: build")]
    public void Evaluate_ShouldJudgeAllReportedChecks_WhenNoRequiredChecksAreDefined(
        string runs,
        string statuses,
        string expectedState,
        string expectedDetail)
        => AssertEvaluation(string.Empty, runs, statuses, expectedState, expectedDetail);

    [Fact]
    public void Evaluate_ShouldTruncateNames_WhenManyChecksAreUnsettled()
    {
        string runs = string.Join(';', Enumerable.Range(1, 7).Select(index =>
            string.Create(CultureInfo.InvariantCulture, $"job-{index}|in_progress|-|{index}")));

        CiSettleSnapshot snapshot = CiSettleEvaluator.Evaluate(_headSha, [], ParseRuns(runs), []);

        snapshot.State.Should().Be(CiSettleState.Pending);
        snapshot.Detail.Should().Be("未完了: job-1, job-2, job-3, job-4, job-5 ほか 2 件");
    }

    [Fact]
    public void Evaluate_ShouldCarryHeadSha()
    {
        CiSettleSnapshot snapshot = CiSettleEvaluator.Evaluate(_headSha, [], [], []);

        snapshot.HeadSha.Should().Be(_headSha);
    }

    private static void AssertEvaluation(
        string required,
        string runs,
        string statuses,
        string expectedState,
        string? expectedDetail)
    {
        CiSettleSnapshot snapshot = CiSettleEvaluator.Evaluate(
            _headSha,
            Split(required).Select(ParseRequired).ToList(),
            ParseRuns(runs),
            Split(statuses).Select(ParseStatus).ToList());

        snapshot.State.Should().Be(Enum.Parse<CiSettleState>(expectedState));
        if (expectedDetail is not null)
        {
            snapshot.Detail.Should().Contain(expectedDetail);
        }
    }

    private static string[] Split(string value)
        => value.Length == 0 ? [] : value.Split(';');

    private static List<CheckRunInfo> ParseRuns(string runs)
        => Split(runs).Select(ParseRun).ToList();

    private static RequiredCheck ParseRequired(string spec)
    {
        string[] parts = spec.Split('@');
        return new RequiredCheck(parts[0], parts.Length > 1 ? long.Parse(parts[1], CultureInfo.InvariantCulture) : null);
    }

    private static CheckRunInfo ParseRun(string spec)
    {
        string[] parts = spec.Split('|');
        return new CheckRunInfo(
            long.Parse(parts[3], CultureInfo.InvariantCulture),
            parts[0],
            parts[1],
            parts[2] == "-" ? null : parts[2],
            parts.Length > 4 ? long.Parse(parts[4], CultureInfo.InvariantCulture) : null);
    }

    private static CommitStatusInfo ParseStatus(string spec)
    {
        string[] parts = spec.Split('|');
        return new CommitStatusInfo(parts[0], parts[1]);
    }
}
