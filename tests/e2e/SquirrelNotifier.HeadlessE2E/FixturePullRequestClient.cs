// <copyright file="FixturePullRequestClient.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.HeadlessE2E;

/// <summary>session resume fixture の HEAD 読み取りを実 GitHub から隔離する.</summary>
internal sealed class FixturePullRequestClient : IGhApiClient
{
    public Task<GhApiResult> GetAsync(string path, string jq, bool paginate, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (path != "repos/fixture-owner/fixture-repository/pulls/307" || paginate)
        {
            throw new InvalidOperationException($"fixture が想定していない PR 取得です: {path}");
        }

        return Task.FromResult(GhApiResult.Success("""{"state":"open","headSha":"0123456789abcdef0123456789abcdef01234567","baseRef":"main"}"""));
    }
}
