// <copyright file="ReviewObservationBuilderTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using SquirrelNotifier.WinUI3.Helpers;
using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Tests.Helpers;

public sealed class ReviewObservationBuilderTests
{
    private const string _cycleId = "cyc_01jbq3x8n5r7t9v2w4y6z8a0c1";
    private const string _candidateId = "cand_01jbq3x8n5r7t9v2w4y6z8a0d1";
    private const string _headSha = "0123456789abcdef0123456789abcdef01234567";
    private static readonly DateTimeOffset _now = new(2026, 10, 24, 13, 0, 0, TimeSpan.Zero);
    private static readonly ReviewCycleRef _cycle = new(_candidateId, _cycleId, 1, _headSha);

    [Fact]
    public void Build_ShouldReturnNull_WhenTheEntryHasNoCycle()
    {
        ReviewStatusEntry entry = Entry(ReviewStatusState.Waiting) with { Cycle = null };

        ReviewObservationBuilder.Build(entry, true, "squirrel-notifier", 1, 0, _now).Should().BeNull();
    }

    [Theory]
    [InlineData("Busy", true, "busy")]
    [InlineData("CiPending", true, "ciPending")]
    [InlineData("AutoPause", true, "autoPause")]
    [InlineData("Busy", false, "busy")]
    [InlineData("", false, "manual")]
    [InlineData("", true, null)]
    public void Build_ShouldDescribeWhyAWaitingReviewIsHeld_WithTheSameRuleAsThePublicStatus(string hold, bool autoStartEnabled, string? expected)
    {
        ReviewHoldKind? kind = hold.Length == 0 ? null : Enum.Parse<ReviewHoldKind>(hold);
        ReviewStatusEntry entry = Entry(ReviewStatusState.Waiting, hold: kind);

        ReviewObservation observation = ReviewObservationBuilder.Build(entry, autoStartEnabled, "squirrel-notifier", 1, 0, _now)!;

        observation.State.Should().Be("waiting");
        observation.HoldReason.Should().Be(expected);
        observation.Outcome.Should().BeNull();
        observation.ExitCode.Should().BeNull();
    }

    [Fact]
    public void Build_ShouldNotCarryAHoldReasonOrAResultWhileRunning()
    {
        // 契約は、保留の理由を起動待ちのときだけ、終了結果を終了のときだけ許す。前の段階の値が残っていても送らない
        ReviewStatusEntry entry = Entry(ReviewStatusState.Running, hold: ReviewHoldKind.Busy, outcome: ReviewerOutcome.Completed, exitCode: 0);

        ReviewObservation observation = ReviewObservationBuilder.Build(entry, false, "squirrel-notifier", 1, 4, _now)!;

        observation.State.Should().Be("running");
        observation.HoldReason.Should().BeNull();
        observation.Outcome.Should().BeNull();
        observation.ExitCode.Should().BeNull();
    }

    [Theory]
    [InlineData("Completed", 0, "completed")]
    [InlineData("Failed", 1, "failed")]
    [InlineData("Failed", -1, "failed")]
    public void Build_ShouldCarryTheProcessResult_WhenFinished(string outcome, int exitCode, string expected)
    {
        ReviewStatusEntry entry = Entry(ReviewStatusState.Finished, outcome: Enum.Parse<ReviewerOutcome>(outcome), exitCode: exitCode);

        ReviewObservation observation = ReviewObservationBuilder.Build(entry, true, "squirrel-notifier", 1, 5, _now)!;

        observation.State.Should().Be("finished");
        observation.HoldReason.Should().BeNull();
        observation.Outcome.Should().Be(expected);
        observation.ExitCode.Should().Be(exitCode);
    }

    [Fact]
    public void Build_ShouldIdentifyTheCycleCandidateAndHeadOfTheEntry()
    {
        ReviewObservation observation = ReviewObservationBuilder.Build(
            Entry(ReviewStatusState.Running), true, "squirrel-notifier", 7, 12, _now.AddMilliseconds(250), stopped: true)!;

        observation.Should().BeEquivalentTo(new ReviewObservation(
            1, _cycleId, _candidateId, _headSha, "squirrel-notifier", 7, 12, "2026-10-24T13:00:00.250Z", "running", null, null, null, true));
    }

    [Theory]
    [InlineData("squirrel-notifier", true)]
    [InlineData("a", true)]
    [InlineData("squirrel.notifier_1-2", true)]
    [InlineData("", false)]
    [InlineData("Squirrel", false)]
    [InlineData("-squirrel", false)]
    [InlineData("squirrel notifier", false)]
    [InlineData("squirrel/notifier", false)]
    public void IsValidProducerId_ShouldFollowTheContract(string producerId, bool expected)
    {
        ReviewObservationBuilder.IsValidProducerId(producerId).Should().Be(expected);
    }

    [Fact]
    public void Build_ShouldRejectAProducerIdOutsideTheContract()
    {
        Action act = () => ReviewObservationBuilder.Build(Entry(ReviewStatusState.Running), true, "Not Valid", 1, 0, _now);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(0L, 0L)]
    [InlineData(-1L, 0L)]
    [InlineData(1L, -1L)]
    public void Build_ShouldRejectAnEpochOrSequenceOutsideTheContract(long epoch, long sequence)
    {
        Action act = () => ReviewObservationBuilder.Build(Entry(ReviewStatusState.Running), true, "squirrel-notifier", epoch, sequence, _now);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Build_ShouldNotCarryAnythingBeyondTheContractFields()
    {
        ReviewObservation observation = ReviewObservationBuilder.Build(Entry(ReviewStatusState.Running), true, "squirrel-notifier", 1, 0, _now)!;

        JsonObject json = JsonNode.Parse(JsonSerializer.Serialize(observation))!.AsObject();

        // ローカルパス・コマンドライン・ログ本文・秘密値、および reviewer の選択（agent）は載せない
        json.Select(static pair => pair.Key).Should().BeEquivalentTo(
            "schemaVersion", "cycleId", "candidateId", "headSha", "producerId", "producerEpoch", "sequence",
            "observedAt", "state", "holdReason", "outcome", "exitCode", "stopped");
    }

    // review-raven の契約 v1 のフィクスチャ（TestData/review-observation/README.md）と、項目と値が一致する
    [Theory]
    [InlineData("waiting-ci-pending")]
    [InlineData("running")]
    [InlineData("finished-completed")]
    [InlineData("stopped")]
    public void Build_ShouldProduceExactlyTheContractFixture(string name)
    {
        JsonObject fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "review-observation", name + ".json")))!.AsObject();
        ReviewStatusEntry entry = Entry(
            fixture["state"]!.GetValue<string>() switch
            {
                "waiting" => ReviewStatusState.Waiting,
                "running" => ReviewStatusState.Running,
                _ => ReviewStatusState.Finished,
            },
            hold: fixture["holdReason"]?.GetValue<string>() == "ciPending" ? ReviewHoldKind.CiPending : null,
            outcome: fixture["outcome"] is null ? null : ReviewerOutcome.Completed,
            exitCode: fixture["exitCode"]?.GetValue<int>());

        ReviewObservation observation = ReviewObservationBuilder.Build(
            entry,
            true,
            fixture["producerId"]!.GetValue<string>(),
            fixture["producerEpoch"]!.GetValue<long>(),
            fixture["sequence"]!.GetValue<long>(),
            DateTimeOffset.Parse(fixture["observedAt"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture),
            stopped: fixture["stopped"]!.GetValue<bool>())!;

        JsonObject produced = JsonNode.Parse(JsonSerializer.Serialize(observation))!.AsObject();

        // observedAt は、契約が許す小数部つきの形式で出力する。それ以外は値まで一致する
        produced["observedAt"]!.GetValue<string>().Should().StartWith("2026-10-24T13:00:00").And.EndWith("Z");
        produced.Remove("observedAt");
        fixture.Remove("observedAt");
        JsonNode.DeepEquals(produced, fixture).Should().BeTrue($"produced {produced.ToJsonString()} but the contract fixture is {fixture.ToJsonString()}");
    }

    private static ReviewStatusEntry Entry(
        ReviewStatusState state,
        ReviewHoldKind? hold = null,
        ReviewerOutcome? outcome = null,
        int? exitCode = null)
        => new(
            "owner/repo#42",
            "owner/Repo",
            42,
            1,
            "evt_1",
            "opened",
            state,
            _now.AddMinutes(-5),
            Hold: hold,
            Outcome: outcome,
            ExitCode: exitCode,
            Cycle: _cycle);
}
