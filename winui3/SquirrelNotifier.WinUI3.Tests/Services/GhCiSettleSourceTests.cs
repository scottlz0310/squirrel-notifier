// <copyright file="GhCiSettleSourceTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public sealed class GhCiSettleSourceTests
{
    private const string _sha = "0123456789abcdef0123456789abcdef01234567";
    private const string _repositoryPath = "repos/owner/repo";

    private static long _nextRunId;

    private readonly FakeGhApiClient _client = new();

    [Fact]
    public async Task GetAsync_ShouldReadPullRequestRequiredChecksAndChecks_ForTheHeadSha()
    {
        SetOpenPullRequest("main");
        SetRuleset(RulesetWith("build"));
        SetClassic(ClassicWith());
        SetCheckRuns(Run("build", "completed", "success"));
        SetStatuses();

        CiSettleSnapshot snapshot = await CreateSource().GetAsync("owner/repo", 42, CancellationToken.None);

        snapshot.State.Should().Be(CiSettleState.Passed);
        snapshot.HeadSha.Should().Be(_sha);
        _client.Calls.Select(call => call.Path).Should().Equal(
            $"{_repositoryPath}/pulls/42",
            $"{_repositoryPath}/rules/branches/main?per_page=100",
            $"{_repositoryPath}/branches/main/protection/required_status_checks",
            $"{_repositoryPath}/commits/{_sha}/check-runs?per_page=100",
            $"{_repositoryPath}/commits/{_sha}/status?per_page=100");
        _client.Calls.Select(call => call.Paginate).Should().Equal(false, true, false, true, true);
    }

    // コメント本文などを読み込まないため、すべての呼び出しで出力を jq で絞る
    [Fact]
    public async Task GetAsync_ShouldNarrowEveryCallWithJq()
    {
        SetOpenPullRequest("main");
        SetRuleset(string.Empty);
        SetClassic(ClassicWith());
        SetCheckRuns();
        SetStatuses();

        await CreateSource().GetAsync("owner/repo", 42, CancellationToken.None);

        _client.Calls.Should().OnlyContain(call => call.Jq.Contains("tojson", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetAsync_ShouldReturnClosed_WithoutReadingChecks_WhenPullRequestIsClosed()
    {
        _client.Set($"{_repositoryPath}/pulls/42", GhApiResult.Success($$"""{"state":"closed","headSha":"{{_sha}}","baseRef":"main"}"""));

        CiSettleSnapshot snapshot = await CreateSource().GetAsync("owner/repo", 42, CancellationToken.None);

        snapshot.State.Should().Be(CiSettleState.PullRequestClosed);
        snapshot.HeadSha.Should().Be(_sha);
        _client.Calls.Should().ContainSingle();
    }

    // ruleset と classic は和集合にする
    [Fact]
    public async Task GetAsync_ShouldUnionRulesetAndClassicRequiredChecks()
    {
        SetOpenPullRequest("main");
        SetRuleset(RulesetWith("build"));
        SetClassic(ClassicWith("codecov/patch"));
        SetCheckRuns(Run("build", "completed", "success"));
        SetStatuses();

        CiSettleSnapshot snapshot = await CreateSource().GetAsync("owner/repo", 42, CancellationToken.None);

        snapshot.State.Should().Be(CiSettleState.Pending);
        snapshot.Detail.Should().Contain("codecov/patch（未報告）");
    }

    // 保護設定なし（404 "Branch not protected"）は「required なし」。thread-owl#227
    [Fact]
    public async Task GetAsync_ShouldTreatClassic404AsNoRequiredChecks()
    {
        SetOpenPullRequest("main");
        SetRuleset(RulesetWith("build"));
        _client.Set(
            $"{_repositoryPath}/branches/main/protection/required_status_checks",
            GhApiResult.Failure(404, "gh: Branch not protected (HTTP 404)"));
        SetCheckRuns(Run("build", "completed", "success"), Run("optional", "in_progress", null));
        SetStatuses();

        CiSettleSnapshot snapshot = await CreateSource().GetAsync("owner/repo", 42, CancellationToken.None);

        snapshot.State.Should().Be(CiSettleState.Passed);
    }

    [Fact]
    public async Task GetAsync_ShouldWaitForAllReportedChecks_WhenNoRequiredChecksAreDefined()
    {
        SetOpenPullRequest("main");
        SetRuleset(string.Empty);
        _client.Set(
            $"{_repositoryPath}/branches/main/protection/required_status_checks",
            GhApiResult.Failure(404, "gh: Branch not protected (HTTP 404)"));
        SetCheckRuns(Run("build", "completed", "success"), Run("lint", "in_progress", null));
        SetStatuses();

        CiSettleSnapshot snapshot = await CreateSource().GetAsync("owner/repo", 42, CancellationToken.None);

        snapshot.State.Should().Be(CiSettleState.Pending);
        snapshot.Detail.Should().Contain("lint");
    }

    [Fact]
    public async Task GetAsync_ShouldEscapeBaseBranchInPath()
    {
        SetOpenPullRequest("release/1.0");
        SetRuleset(string.Empty);
        SetClassic(ClassicWith());
        SetCheckRuns();
        SetStatuses();

        await CreateSource().GetAsync("owner/repo", 42, CancellationToken.None);

        _client.Calls.Select(call => call.Path).Should().Contain(new[]
        {
            $"{_repositoryPath}/rules/branches/release%2F1.0?per_page=100",
            $"{_repositoryPath}/branches/release%2F1.0/protection/required_status_checks",
        });
    }

    // 取得できなかった原因は握りつぶさず、Detail（Recent activity）へ残す
    [Theory]
    [InlineData("pulls", null, "gh コマンドが見つかりません", "PR の取得に失敗しました")]
    [InlineData("pulls", 404, "gh: Not Found (HTTP 404)", "PR の取得に失敗しました")]
    [InlineData("ruleset", 403, "gh: Forbidden (HTTP 403)", "権限不足またはレート制限の可能性")]
    [InlineData("ruleset", 404, "gh: Not Found (HTTP 404)", "required checks の設定（ruleset）の取得に失敗しました")]
    [InlineData("ruleset", 500, "gh: Server Error (HTTP 500)", "required checks の設定（ruleset）の取得に失敗しました")]
    [InlineData("classic", 403, "gh: Forbidden (HTTP 403)", "権限不足またはレート制限の可能性")]
    [InlineData("classic", 500, "gh: Server Error (HTTP 500)", "required checks の設定（branch protection）の取得に失敗しました")]
    [InlineData("check-runs", 500, "gh: Server Error (HTTP 500)", "check runs の取得に失敗しました")]
    [InlineData("status", 500, "gh: Server Error (HTTP 500)", "commit status の取得に失敗しました")]
    public async Task GetAsync_ShouldReturnUnavailable_WhenAnyCallFails(
        string failingCall,
        int? httpStatus,
        string error,
        string expectedDetail)
    {
        SetOpenPullRequest("main");
        SetRuleset(string.Empty);
        SetClassic(ClassicWith());
        SetCheckRuns();
        SetStatuses();
        string failingPath = failingCall switch
        {
            "pulls" => $"{_repositoryPath}/pulls/42",
            "ruleset" => $"{_repositoryPath}/rules/branches/main?per_page=100",
            "classic" => $"{_repositoryPath}/branches/main/protection/required_status_checks",
            "check-runs" => $"{_repositoryPath}/commits/{_sha}/check-runs?per_page=100",
            _ => $"{_repositoryPath}/commits/{_sha}/status?per_page=100",
        };
        _client.Set(failingPath, GhApiResult.Failure(httpStatus, error));

        CiSettleSnapshot snapshot = await CreateSource().GetAsync("owner/repo", 42, CancellationToken.None);

        snapshot.State.Should().Be(CiSettleState.Unavailable);
        snapshot.Detail.Should().Contain(expectedDetail).And.Contain(error);
    }

    [Theory]
    [InlineData("pulls", "not json")]
    [InlineData("ruleset", "[{\"context\":\"build\"}]")]
    [InlineData("classic", "[]")]
    [InlineData("check-runs", "{\"id\":\"x\"}")]
    [InlineData("status", "{\"context\":\"only-context\"}")]
    public async Task GetAsync_ShouldReturnUnavailable_WhenResponseCannotBeParsed(string malformedCall, string output)
    {
        SetOpenPullRequest("main");
        SetRuleset(string.Empty);
        SetClassic(ClassicWith());
        SetCheckRuns();
        SetStatuses();
        string malformedPath = malformedCall switch
        {
            "pulls" => $"{_repositoryPath}/pulls/42",
            "ruleset" => $"{_repositoryPath}/rules/branches/main?per_page=100",
            "classic" => $"{_repositoryPath}/branches/main/protection/required_status_checks",
            "check-runs" => $"{_repositoryPath}/commits/{_sha}/check-runs?per_page=100",
            _ => $"{_repositoryPath}/commits/{_sha}/status?per_page=100",
        };
        _client.Set(malformedPath, GhApiResult.Success(output));

        CiSettleSnapshot snapshot = await CreateSource().GetAsync("owner/repo", 42, CancellationToken.None);

        snapshot.State.Should().Be(CiSettleState.Unavailable);
        snapshot.Detail.Should().Contain("gh の応答を解釈できません");
    }

    // gh へ渡すパスの構成要素になるため、不正な参照では gh を呼ばない
    [Theory]
    [InlineData("owner/repo/../other", 42)]
    [InlineData("owner/..", 42)]
    [InlineData("owner/.", 42)]
    [InlineData("owner repo", 42)]
    [InlineData("owner/repo", 0)]
    [InlineData("owner/repo", -1)]
    [InlineData("", 42)]
    [InlineData("repo-only", 42)]
    public async Task GetAsync_ShouldReturnUnavailable_WithoutCallingGh_WhenReferenceIsInvalid(string repository, int prNumber)
    {
        CiSettleSnapshot snapshot = await CreateSource().GetAsync(repository, prNumber, CancellationToken.None);

        snapshot.State.Should().Be(CiSettleState.Unavailable);
        _client.Calls.Should().BeEmpty();
    }

    // ruleset は既定 30 件でページングされる。required_status_checks が後続のページにだけあっても見落とさない。
    // gh の --paginate を使わない場合は先頭ページ（required なし）しか返らない、という応答を模して固定する
    [Fact]
    public async Task GetAsync_ShouldReadRequiredChecksOnLaterRulesetPages()
    {
        SetOpenPullRequest("main");
        _client.SetPaged(
            $"{_repositoryPath}/rules/branches/main?per_page=100",
            firstPage: GhApiResult.Success(string.Empty),
            allPages: GhApiResult.Success(RulesetWith("codecov/patch")));
        _client.Set(
            $"{_repositoryPath}/branches/main/protection/required_status_checks",
            GhApiResult.Failure(404, "gh: Branch not protected (HTTP 404)"));
        SetCheckRuns(Run("build", "completed", "success"));
        SetStatuses();

        CiSettleSnapshot snapshot = await CreateSource().GetAsync("owner/repo", 42, CancellationToken.None);

        snapshot.State.Should().Be(CiSettleState.Pending);
        snapshot.Detail.Should().Contain("codecov/patch（未報告）");
        _client.Calls.Should().Contain(call => call.Path.Contains("/rules/branches/", StringComparison.Ordinal) && call.Paginate);
    }

    // 複数ページ分の出力は複数行として連結される。すべてを集約する
    [Fact]
    public async Task GetAsync_ShouldAggregateRequiredChecksFromAllRulesetPages()
    {
        SetOpenPullRequest("main");
        SetRuleset(RulesetWith("build", "lint", "codecov/patch"));
        SetClassic(ClassicWith());
        SetCheckRuns(Run("build", "completed", "success"), Run("lint", "completed", "success"));
        SetStatuses();

        CiSettleSnapshot snapshot = await CreateSource().GetAsync("owner/repo", 42, CancellationToken.None);

        snapshot.State.Should().Be(CiSettleState.Pending);
        snapshot.Detail.Should().Be("未完了: codecov/patch（未報告）");
    }

    [Fact]
    public async Task GetAsync_ShouldPropagateCancellation()
    {
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();
        _client.ThrowOnCall = new OperationCanceledException(cts.Token);

        Func<Task> act = () => CreateSource().GetAsync("owner/repo", 42, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private GhCiSettleSource CreateSource() => new(_client);

    private void SetOpenPullRequest(string baseRef)
        => _client.Set(
            $"{_repositoryPath}/pulls/42",
            GhApiResult.Success($$"""{"state":"open","headSha":"{{_sha}}","baseRef":"{{baseRef}}"}"""));

    private void SetRuleset(string output)
        => _client.SetPrefix($"{_repositoryPath}/rules/branches/", GhApiResult.Success(output));

    private void SetClassic(string output)
        => _client.SetPrefix($"{_repositoryPath}/branches/", GhApiResult.Success(output));

    private void SetCheckRuns(params string[] lines)
        => _client.Set(
            $"{_repositoryPath}/commits/{_sha}/check-runs?per_page=100",
            GhApiResult.Success(string.Join('\n', lines)));

    private void SetStatuses(params string[] lines)
        => _client.Set(
            $"{_repositoryPath}/commits/{_sha}/status?per_page=100",
            GhApiResult.Success(string.Join('\n', lines)));

    private static string RulesetWith(params string[] contexts)
        => string.Join('\n', contexts.Select(context => $$"""{"context":"{{context}}","integrationId":null}"""));

    private static string ClassicWith(params string[] contexts)
        => $$"""{"contexts":[{{string.Join(',', contexts.Select(context => $"\"{context}\""))}}],"checks":[]}""";

    private static string Run(string name, string status, string? conclusion)
        => $$"""{"id":{{Interlocked.Increment(ref _nextRunId)}},"name":"{{name}}","status":"{{status}}","conclusion":{{(conclusion is null ? "null" : $"\"{conclusion}\"")}},"appId":15368}""";

    private sealed class FakeGhApiClient : IGhApiClient
    {
        private readonly Dictionary<string, GhApiResult> _exact = new(StringComparer.Ordinal);
        private readonly List<(string Prefix, GhApiResult Result)> _prefixes = [];
        private readonly Dictionary<string, (GhApiResult FirstPage, GhApiResult AllPages)> _paged = new(StringComparer.Ordinal);

        public List<(string Path, string Jq, bool Paginate)> Calls { get; } = [];

        public Exception? ThrowOnCall { get; set; }

        public void Set(string path, GhApiResult result) => _exact[path] = result;

        public void SetPrefix(string prefix, GhApiResult result) => _prefixes.Add((prefix, result));

        // paginate=false なら先頭ページだけ、paginate=true なら全ページを連結した出力を返す
        public void SetPaged(string path, GhApiResult firstPage, GhApiResult allPages) => _paged[path] = (firstPage, allPages);

        public Task<GhApiResult> GetAsync(string path, string jq, bool paginate, CancellationToken cancellationToken)
        {
            Calls.Add((path, jq, paginate));
            if (ThrowOnCall is not null)
            {
                throw ThrowOnCall;
            }

            if (_exact.TryGetValue(path, out GhApiResult? result))
            {
                return Task.FromResult(result);
            }

            if (_paged.TryGetValue(path, out (GhApiResult FirstPage, GhApiResult AllPages) pages))
            {
                return Task.FromResult(paginate ? pages.AllPages : pages.FirstPage);
            }

            foreach ((string prefix, GhApiResult prefixResult) in _prefixes)
            {
                if (path.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return Task.FromResult(prefixResult);
                }
            }

            throw new InvalidOperationException($"想定していない呼び出しです: {path}");
        }
    }
}
