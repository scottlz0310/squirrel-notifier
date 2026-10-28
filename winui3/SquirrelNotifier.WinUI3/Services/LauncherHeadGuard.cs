// <copyright file="LauncherHeadGuard.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Globalization;
using System.Text.Json;
using SquirrelNotifier.WinUI3.Helpers;
using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

internal sealed class LauncherHeadGuard(IGhApiClient client)
{
    private const string _pullRequestJq = "{state: .state, headSha: .head.sha, baseRef: .base.ref} | tojson";

    public async Task<string> ReadOpenHeadAsync(ReviewEvent reviewEvent, CancellationToken cancellationToken)
    {
        if (!PrReferenceParser.TryParse($"{reviewEvent.Repository}#{reviewEvent.PrNumber.ToString(CultureInfo.InvariantCulture)}", out PrReference? reference)
            || reference is null
            || reference.Repo is "." or "..")
        {
            throw new InvalidOperationException("再開対象のPRを特定できません。");
        }

        string path = $"repos/{Uri.EscapeDataString(reference.Owner)}/{Uri.EscapeDataString(reference.Repo)}/pulls/{reference.PrNumber.ToString(CultureInfo.InvariantCulture)}";
        GhApiResult result = await client.GetAsync(path, _pullRequestJq, paginate: false, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException($"再開前のPR取得に失敗しました: {result.Error}");
        }

        try
        {
            CiPullRequestInfo pr = CiSettleParser.ParsePullRequest(result.Output);
            if (pr.HeadSha.Length != 40)
            {
                throw new InvalidOperationException("再開対象のPRのHEADは40桁のSHAである必要があります。");
            }

            return pr.IsClosed ? throw new InvalidOperationException("再開対象のPRは閉じています。") : pr.HeadSha;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("再開前のPR応答が不正です。", ex);
        }
    }

    public async Task VerifyAsync(ReviewEvent reviewEvent, string expectedHead, CancellationToken cancellationToken)
    {
        string currentHead = await ReadOpenHeadAsync(reviewEvent, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(currentHead, expectedHead, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"再開前にPRのHEADが変わりました: expected={expectedHead}, current={currentHead}");
        }
    }
}
