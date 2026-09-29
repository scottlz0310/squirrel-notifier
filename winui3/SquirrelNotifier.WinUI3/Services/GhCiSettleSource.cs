// <copyright file="GhCiSettleSource.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Globalization;
using System.Text.Json;
using SquirrelNotifier.WinUI3.Helpers;
using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>
/// <c>gh api</c> で PR の head SHA と required checks の確定状態を 1 回取得する、暫定の取得元（#456）。
/// thread-owl が CI の状態を返す tool を提供したら、<see cref="ICiSettleSource"/> の実装ごと差し替え、
/// <c>gh</c> への依存と required checks の解決の複製を削除する.
/// </summary>
/// <remarks>
/// <para>読み取りのみで、読むのは check の名前と状態だけ。<c>--jq</c> でその項目に絞り、コメント本文などは取り込まない.</para>
/// <para>
/// required checks は ruleset（<c>rules/branches/&lt;branch&gt;</c>）を先に、classic の branch protection を後に読み、
/// 和集合にする。HTTP エラーの扱いは thread-owl#227 に合わせる: classic の 404（保護設定なし）は
/// 「required なし」、403（権限不足）は「設定を読めない」として <see cref="CiSettleState.Unavailable"/> にする.
/// </para>
/// </remarks>
internal sealed class GhCiSettleSource : ICiSettleSource
{
    private const string _pullRequestJq = "{state: .state, headSha: .head.sha, baseRef: .base.ref} | tojson";
    private const string _rulesetJq = "[.[] | select(.type == \"required_status_checks\") | (.parameters.required_status_checks // [])[] | {context: .context, integrationId: .integration_id}] | tojson";
    private const string _classicJq = "{contexts: (.contexts // []), checks: [(.checks // [])[] | {context: .context, appId: .app_id}]} | tojson";
    private const string _checkRunsJq = ".check_runs[] | {id: .id, name: .name, status: .status, conclusion: .conclusion, appId: .app.id} | tojson";
    private const string _statusesJq = ".statuses[] | {context: .context, state: .state} | tojson";

    private readonly IGhApiClient _ghApiClient;

    public GhCiSettleSource(IGhApiClient ghApiClient)
    {
        ArgumentNullException.ThrowIfNull(ghApiClient);
        _ghApiClient = ghApiClient;
    }

    public async Task<CiSettleSnapshot> GetAsync(string repository, int prNumber, CancellationToken cancellationToken)
    {
        // gh へ渡すパスの構成要素は、owner / repo の許可文字と、API が返した SHA の形式で検証してから使う
        if (!PrReferenceParser.TryParse($"{repository}#{prNumber.ToString(CultureInfo.InvariantCulture)}", out PrReference? reference)
            || reference is null
            || reference.Repo is "." or "..")
        {
            return CiSettleSnapshot.Unavailable($"リポジトリまたは PR 番号が不正です: {repository}#{prNumber.ToString(CultureInfo.InvariantCulture)}");
        }

        try
        {
            return await FetchAsync(reference, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            return CiSettleSnapshot.Unavailable($"gh の応答を解釈できません: {ex.Message}");
        }
    }

    private async Task<CiSettleSnapshot> FetchAsync(PrReference reference, CancellationToken cancellationToken)
    {
        string repositoryPath = $"repos/{Uri.EscapeDataString(reference.Owner)}/{Uri.EscapeDataString(reference.Repo)}";

        GhApiResult pullRequestResult = await _ghApiClient
            .GetAsync($"{repositoryPath}/pulls/{reference.PrNumber.ToString(CultureInfo.InvariantCulture)}", _pullRequestJq, paginate: false, cancellationToken)
            .ConfigureAwait(false);
        if (!pullRequestResult.IsSuccess)
        {
            return CiSettleSnapshot.Unavailable($"PR の取得に失敗しました: {pullRequestResult.Error}");
        }

        CiPullRequestInfo pullRequest = CiSettleParser.ParsePullRequest(pullRequestResult.Output);
        if (pullRequest.IsClosed)
        {
            return new CiSettleSnapshot(CiSettleState.PullRequestClosed, pullRequest.HeadSha, "PR は merge または close されています");
        }

        (IReadOnlyList<RequiredCheck>? requiredChecks, string? requiredChecksError) =
            await GetRequiredChecksAsync(repositoryPath, pullRequest.BaseRef, cancellationToken).ConfigureAwait(false);
        if (requiredChecks is null)
        {
            return CiSettleSnapshot.Unavailable(requiredChecksError!);
        }

        string commitPath = $"{repositoryPath}/commits/{pullRequest.HeadSha}";
        GhApiResult checkRunsResult = await _ghApiClient
            .GetAsync($"{commitPath}/check-runs?per_page=100", _checkRunsJq, paginate: true, cancellationToken)
            .ConfigureAwait(false);
        if (!checkRunsResult.IsSuccess)
        {
            return CiSettleSnapshot.Unavailable($"check runs の取得に失敗しました: {checkRunsResult.Error}");
        }

        GhApiResult statusesResult = await _ghApiClient
            .GetAsync($"{commitPath}/status?per_page=100", _statusesJq, paginate: true, cancellationToken)
            .ConfigureAwait(false);
        if (!statusesResult.IsSuccess)
        {
            return CiSettleSnapshot.Unavailable($"commit status の取得に失敗しました: {statusesResult.Error}");
        }

        return CiSettleEvaluator.Evaluate(
            pullRequest.HeadSha,
            requiredChecks,
            CiSettleParser.ParseCheckRuns(checkRunsResult.Output),
            CiSettleParser.ParseCommitStatuses(statusesResult.Output));
    }

    private async Task<(IReadOnlyList<RequiredCheck>? RequiredChecks, string? Error)> GetRequiredChecksAsync(
        string repositoryPath,
        string baseRef,
        CancellationToken cancellationToken)
    {
        string branchPath = Uri.EscapeDataString(baseRef);

        GhApiResult rulesetResult = await _ghApiClient
            .GetAsync($"{repositoryPath}/rules/branches/{branchPath}", _rulesetJq, paginate: false, cancellationToken)
            .ConfigureAwait(false);
        if (!rulesetResult.IsSuccess)
        {
            return (null, DescribeRequiredChecksFailure("ruleset", rulesetResult));
        }

        IReadOnlyList<RequiredCheck> rulesetChecks = CiSettleParser.ParseRulesetRequiredChecks(rulesetResult.Output);

        GhApiResult classicResult = await _ghApiClient
            .GetAsync($"{repositoryPath}/branches/{branchPath}/protection/required_status_checks", _classicJq, paginate: false, cancellationToken)
            .ConfigureAwait(false);

        // 404 は保護設定なし（"Branch not protected"）または required status checks が無効。required が無いだけで、エラーではない
        if (classicResult.HttpStatus == 404)
        {
            return (rulesetChecks, null);
        }

        if (!classicResult.IsSuccess)
        {
            return (null, DescribeRequiredChecksFailure("branch protection", classicResult));
        }

        return ([.. rulesetChecks.Union(CiSettleParser.ParseClassicRequiredChecks(classicResult.Output))], null);
    }

    private static string DescribeRequiredChecksFailure(string source, GhApiResult result)
        => result.HttpStatus == 403
            ? $"required checks の設定（{source}）を読み取れません。権限不足またはレート制限の可能性があります: {result.Error}"
            : $"required checks の設定（{source}）の取得に失敗しました: {result.Error}";
}
