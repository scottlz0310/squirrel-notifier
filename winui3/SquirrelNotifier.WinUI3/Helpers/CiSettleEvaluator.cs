// <copyright file="CiSettleEvaluator.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Globalization;
using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Helpers;

/// <summary>
/// SHA に対して報告された check runs と commit statuses から、CI が確定したかを判定する（#456）。
/// thread-owl の <c>post_review_verdict</c> の検証と同じく、check runs と commit status の両方を見る.
/// </summary>
/// <remarks>
/// <para>
/// required checks が定義されている場合は、その一覧だけを対象にし、未報告のものは未完了として扱う
/// （<c>codecov/patch</c> のように、他の check の完了より少し遅れて現れる check を待つため）。
/// 定義が無い場合は、報告済みの check すべてを対象にする。1 件も報告されていなければ待つ対象が無いため、
/// 確定として扱う.
/// </para>
/// <para>失敗は、他に未完了の check が残っていても優先する（失敗はレビューの入力になるため、待たずに起動できる）.</para>
/// </remarks>
internal static class CiSettleEvaluator
{
    private const int _maxNamesInDetail = 5;

    private enum CheckOutcome
    {
        Missing,
        Pending,
        Failed,
        Passed,
    }

    public static CiSettleSnapshot Evaluate(
        string headSha,
        IReadOnlyList<RequiredCheck> requiredChecks,
        IReadOnlyList<CheckRunInfo> checkRuns,
        IReadOnlyList<CommitStatusInfo> statuses)
    {
        // 再実行で同じ App・同じ名前の run が増えるため、ID が最大のものだけを採用する
        List<CheckRunInfo> latestRuns = checkRuns
            .GroupBy(run => (run.AppId, run.Name))
            .Select(group => group.MaxBy(run => run.Id)!)
            .ToList();
        Dictionary<string, CommitStatusInfo> latestStatuses = statuses
            .GroupBy(status => status.Context, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);

        List<(string Name, CheckOutcome Outcome)> entries = requiredChecks.Count > 0
            ? [.. requiredChecks.Select(required => (required.Context, EvaluateRequired(required, latestRuns, latestStatuses)))]
            :
            [
                .. latestRuns.Select(run => (run.Name, FromRun(run))),
                .. latestStatuses.Values.Select(status => (status.Context, FromStatus(status))),
            ];

        string[] failed = DistinctNames(entries, CheckOutcome.Failed);
        if (failed.Length > 0)
        {
            return new CiSettleSnapshot(CiSettleState.Failed, headSha, $"失敗: {JoinNames(failed)}");
        }

        string[] unsettled = entries
            .Where(entry => entry.Outcome is CheckOutcome.Pending or CheckOutcome.Missing)
            .Select(entry => entry.Outcome == CheckOutcome.Missing ? $"{entry.Name}（未報告）" : entry.Name)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (unsettled.Length > 0)
        {
            return new CiSettleSnapshot(CiSettleState.Pending, headSha, $"未完了: {JoinNames(unsettled)}");
        }

        return new CiSettleSnapshot(CiSettleState.Passed, headSha, DescribePassed(requiredChecks.Count, entries.Count));
    }

    private static CheckOutcome EvaluateRequired(
        RequiredCheck required,
        List<CheckRunInfo> runs,
        Dictionary<string, CommitStatusInfo> statuses)
    {
        CheckRunInfo? run = runs
            .Where(candidate => string.Equals(candidate.Name, required.Context, StringComparison.Ordinal)
                && (required.IntegrationId is null || candidate.AppId == required.IntegrationId))
            .MaxBy(candidate => candidate.Id);
        if (run is not null)
        {
            return FromRun(run);
        }

        return statuses.TryGetValue(required.Context, out CommitStatusInfo? status)
            ? FromStatus(status)
            : CheckOutcome.Missing;
    }

    // 完了した run は success / neutral / skipped を成功として扱う（GitHub が required check の通過とみなす結論）
    private static CheckOutcome FromRun(CheckRunInfo run)
    {
        if (!string.Equals(run.Status, "completed", StringComparison.Ordinal))
        {
            return CheckOutcome.Pending;
        }

        return run.Conclusion is "success" or "neutral" or "skipped" ? CheckOutcome.Passed : CheckOutcome.Failed;
    }

    private static CheckOutcome FromStatus(CommitStatusInfo status)
        => status.State switch
        {
            "success" => CheckOutcome.Passed,
            "pending" => CheckOutcome.Pending,
            _ => CheckOutcome.Failed,
        };

    private static string[] DistinctNames(List<(string Name, CheckOutcome Outcome)> entries, CheckOutcome outcome)
        => entries
            .Where(entry => entry.Outcome == outcome)
            .Select(entry => entry.Name)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static string JoinNames(string[] names)
        => names.Length <= _maxNamesInDetail
            ? string.Join(", ", names)
            : $"{string.Join(", ", names.Take(_maxNamesInDetail))} ほか {(names.Length - _maxNamesInDetail).ToString(CultureInfo.InvariantCulture)} 件";

    private static string DescribePassed(int requiredCount, int reportedCount)
    {
        if (requiredCount > 0)
        {
            return $"required checks {requiredCount.ToString(CultureInfo.InvariantCulture)} 件がすべて成功";
        }

        return reportedCount > 0
            ? $"required checks の定義は無く、報告済みの check {reportedCount.ToString(CultureInfo.InvariantCulture)} 件がすべて完了"
            : "required checks の定義は無く、報告済みの check もありません";
    }
}
