// <copyright file="CodexRateLimitBucketPolicyTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Helpers;

namespace SquirrelNotifier.WinUI3.Tests.Helpers;

public class CodexRateLimitBucketPolicyTests
{
    [Theory]
    [InlineData("codex", true)]
    [InlineData("base_model_inference", false)]
    [InlineData("future_bucket", false)]
    [InlineData("Codex", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsAutoPauseEligible_ShouldAllowOnlyGeneralBucket(string? bucketId, bool expected)
    {
        // 通常枠だけを Auto-Pause の判断材料にする。未知 bucket を対象にすると、
        // 用途を確認できない枠の枯渇で起動を止めてしまう（#335）
        CodexRateLimitBucketPolicy.IsAutoPauseEligible(bucketId).Should().Be(expected);
    }

    [Theory]
    [InlineData("codex", "primary", 300, "5時間制限（全モデル）")]
    [InlineData("codex", "secondary", 10080, "Weekly制限（全モデル）")]
    [InlineData("base_model_inference", "primary", 10080, "Luna Reserve Weekly制限（Luna専用・Auto-Pause対象外）")]
    public void BuildLabel_ShouldNameKnownBucketPurpose(string bucketId, string slot, long windowDurationMins, string expected)
    {
        CodexRateLimitBucketPolicy.BuildLabel(bucketId, slot, windowDurationMins).Should().Be(expected);
    }

    [Theory]
    [InlineData("base_model_inference", "secondary", 10080, "7日枠（Auto-Pause対象外）")]
    [InlineData("future_bucket", "primary", 300, "5時間枠（Auto-Pause対象外）")]
    [InlineData("future_bucket", "primary", 90, "90分枠（Auto-Pause対象外）")]
    [InlineData("future_bucket", "primary", null, "primary 枠（Auto-Pause対象外）")]
    public void BuildLabel_ShouldFallBackToWindowTextForUnknownCombination(
        string bucketId,
        string slot,
        int? windowDurationMins,
        string expected)
    {
        // 既知でない組み合わせは用途を断定せず期間表記に戻すが、対象外である事実は表示に残す
        CodexRateLimitBucketPolicy.BuildLabel(bucketId, slot, windowDurationMins).Should().Be(expected);
    }

    [Theory]
    [InlineData(300, "5時間枠")]
    [InlineData(null, "tertiary 枠")]
    public void BuildLabel_ShouldFallBackToWindowTextForUnknownSlotOfGeneralBucket(int? windowDurationMins, string expected)
    {
        // 通常枠に未知の slot が増えた場合は対象のままなので、対象外の注記は付けない
        CodexRateLimitBucketPolicy.BuildLabel("codex", "tertiary", windowDurationMins).Should().Be(expected);
    }
}
