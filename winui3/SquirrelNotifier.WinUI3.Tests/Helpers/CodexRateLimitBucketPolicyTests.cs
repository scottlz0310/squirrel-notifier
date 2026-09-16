// <copyright file="CodexRateLimitBucketPolicyTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Helpers;

namespace SquirrelNotifier.WinUI3.Tests.Helpers;

public class CodexRateLimitBucketPolicyTests
{
    [Theory]
    [InlineData("codex", "primary", true)]
    [InlineData("codex", "secondary", true)]
    [InlineData("codex", "tertiary", false)]
    [InlineData("codex", "", false)]
    [InlineData("codex", null, false)]
    [InlineData("base_model_inference", "primary", false)]
    [InlineData("future_bucket", "primary", false)]
    [InlineData("Codex", "primary", false)]
    [InlineData("", "primary", false)]
    [InlineData(null, "primary", false)]
    public void IsAutoPauseEligible_ShouldAllowOnlyKnownGeneralWindows(string? bucketId, string? slot, bool expected)
    {
        // 通常枠の primary / secondary だけを Auto-Pause の判断材料にする。未知の bucket や
        // 通常枠に増えた未知 slot を対象にすると、用途を確認できない枠の枯渇で起動を止めてしまう（#335）
        CodexRateLimitBucketPolicy.IsAutoPauseEligible(bucketId, slot).Should().Be(expected);
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
    [InlineData("codex", "tertiary", 300, "5時間枠（Auto-Pause対象外）")]
    [InlineData("codex", "tertiary", null, "tertiary 枠（Auto-Pause対象外）")]
    public void BuildLabel_ShouldFallBackToWindowTextForUnknownCombination(
        string bucketId,
        string slot,
        int? windowDurationMins,
        string expected)
    {
        // 既知でない組み合わせは用途を断定せず期間表記に戻すが、対象外である事実は表示に残す。
        // 通常枠に未知 slot が増えた場合も同じ扱いにし、ラベルと判定の対象範囲を一致させる
        CodexRateLimitBucketPolicy.BuildLabel(bucketId, slot, windowDurationMins).Should().Be(expected);
        CodexRateLimitBucketPolicy.IsAutoPauseEligible(bucketId, slot).Should().BeFalse();
    }
}
