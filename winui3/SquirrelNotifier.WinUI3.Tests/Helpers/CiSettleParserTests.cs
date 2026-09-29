// <copyright file="CiSettleParserTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Text.Json;
using FluentAssertions;
using SquirrelNotifier.WinUI3.Helpers;

namespace SquirrelNotifier.WinUI3.Tests.Helpers;

public sealed class CiSettleParserTests
{
    private const string _sha = "0123456789abcdef0123456789abcdef01234567";

    [Theory]
    [InlineData("open", false)]
    [InlineData("closed", true)]
    public void ParsePullRequest_ShouldReadStateHeadAndBase(string state, bool expectedClosed)
    {
        CiPullRequestInfo info = CiSettleParser.ParsePullRequest(
            $$"""{"state":"{{state}}","headSha":"{{_sha}}","baseRef":"release/1.0"}""");

        info.IsClosed.Should().Be(expectedClosed);
        info.HeadSha.Should().Be(_sha);
        info.BaseRef.Should().Be("release/1.0");
    }

    [Theory]
    [InlineData("""{"state":"draft","headSha":"0123456789abcdef0123456789abcdef01234567","baseRef":"main"}""")]
    [InlineData("""{"state":"open","headSha":"not-a-sha","baseRef":"main"}""")]
    [InlineData("""{"state":"open","headSha":"0123456789ABCDEF0123456789ABCDEF01234567","baseRef":"main"}""")]
    [InlineData("""{"state":"open","headSha":"0123456789abcdef0123456789abcdef01234567"}""")]
    [InlineData("""{"state":"open","headSha":"0123456789abcdef0123456789abcdef01234567","baseRef":""}""")]
    [InlineData("""["open"]""")]
    [InlineData("not json")]
    public void ParsePullRequest_ShouldThrowJsonException_WhenShapeIsUnexpected(string output)
    {
        Action act = () => CiSettleParser.ParsePullRequest(output);

        act.Should().Throw<JsonException>();
    }

    // ruleset はページングされる。jq はページごとに適用されるため、複数ページ分の出力は複数行として連結される
    [Fact]
    public void ParseRulesetRequiredChecks_ShouldReadOneCheckPerLine_AcrossPages()
    {
        IReadOnlyList<RequiredCheck> checks = CiSettleParser.ParseRulesetRequiredChecks(
            "{\"context\":\"build\",\"integrationId\":15368}\r\n{\"context\":\"lint\",\"integrationId\":null}\n\n{\"context\":\"build\",\"integrationId\":15368}\n");

        checks.Should().Equal(new RequiredCheck("build", 15368), new RequiredCheck("lint", null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    public void ParseRulesetRequiredChecks_ShouldReturnEmpty_WhenNoRule(string output)
        => CiSettleParser.ParseRulesetRequiredChecks(output).Should().BeEmpty();

    [Theory]
    [InlineData("""[{"context":"build","integrationId":null}]""")]
    [InlineData("""{"integrationId":1}""")]
    [InlineData("""{"context":""}""")]
    [InlineData("not json")]
    public void ParseRulesetRequiredChecks_ShouldThrowJsonException_WhenShapeIsUnexpected(string output)
    {
        Action act = () => CiSettleParser.ParseRulesetRequiredChecks(output);

        act.Should().Throw<JsonException>();
    }

    // contexts は checks の旧形式の写し。App の指定が無い check は contexts にだけ現れる。-1 は「任意の App」
    [Fact]
    public void ParseClassicRequiredChecks_ShouldMergeChecksAndLegacyContexts()
    {
        IReadOnlyList<RequiredCheck> checks = CiSettleParser.ParseClassicRequiredChecks(
            """{"contexts":["build","lint","codecov/patch"],"checks":[{"context":"build","appId":15368},{"context":"lint","appId":-1}]}""");

        checks.Should().Equal(
            new RequiredCheck("build", 15368),
            new RequiredCheck("lint", null),
            new RequiredCheck("codecov/patch", null));
    }

    [Theory]
    [InlineData("""{"contexts":[],"checks":[]}""")]
    public void ParseClassicRequiredChecks_ShouldReturnEmpty_WhenNothingIsRequired(string output)
        => CiSettleParser.ParseClassicRequiredChecks(output).Should().BeEmpty();

    [Theory]
    [InlineData("""{"contexts":["build"]}""")]
    [InlineData("""{"contexts":[1],"checks":[]}""")]
    [InlineData("""{"contexts":[""],"checks":[]}""")]
    [InlineData("""[]""")]
    public void ParseClassicRequiredChecks_ShouldThrowJsonException_WhenShapeIsUnexpected(string output)
    {
        Action act = () => CiSettleParser.ParseClassicRequiredChecks(output);

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void ParseCheckRuns_ShouldReadOneRunPerLine_AndIgnoreBlankLines()
    {
        string output = string.Join(
            "\r\n",
            """{"id":2,"name":"build","status":"completed","conclusion":"success","appId":15368}""",
            string.Empty,
            """{"id":3,"name":"lint","status":"in_progress","conclusion":null,"appId":null}""",
            string.Empty);

        IReadOnlyList<CheckRunInfo> runs = CiSettleParser.ParseCheckRuns(output);

        runs.Should().Equal(
            new CheckRunInfo(2, "build", "completed", "success", 15368),
            new CheckRunInfo(3, "lint", "in_progress", null, null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    public void ParseCheckRuns_ShouldReturnEmpty_WhenNoRunIsReported(string output)
        => CiSettleParser.ParseCheckRuns(output).Should().BeEmpty();

    [Theory]
    [InlineData("""{"name":"build","status":"completed"}""")]
    [InlineData("""{"id":"1","name":"build","status":"completed"}""")]
    [InlineData("""{"id":1,"status":"completed"}""")]
    [InlineData("""{"id":1,"name":"build"}""")]
    [InlineData("not json")]
    public void ParseCheckRuns_ShouldThrowJsonException_WhenShapeIsUnexpected(string output)
    {
        Action act = () => CiSettleParser.ParseCheckRuns(output);

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void ParseCommitStatuses_ShouldReadOneStatusPerLine()
    {
        IReadOnlyList<CommitStatusInfo> statuses = CiSettleParser.ParseCommitStatuses(
            "{\"context\":\"ci/a\",\"state\":\"success\"}\n{\"context\":\"ci/b\",\"state\":\"pending\"}\n");

        statuses.Should().Equal(new CommitStatusInfo("ci/a", "success"), new CommitStatusInfo("ci/b", "pending"));
    }

    [Theory]
    [InlineData("""{"context":"ci/a"}""")]
    [InlineData("""{"state":"success"}""")]
    public void ParseCommitStatuses_ShouldThrowJsonException_WhenShapeIsUnexpected(string output)
    {
        Action act = () => CiSettleParser.ParseCommitStatuses(output);

        act.Should().Throw<JsonException>();
    }
}
