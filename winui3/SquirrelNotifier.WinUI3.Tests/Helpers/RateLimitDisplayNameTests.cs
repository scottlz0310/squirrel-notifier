// <copyright file="RateLimitDisplayNameTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Helpers;
using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Tests.Helpers;

public class RateLimitDisplayNameTests
{
    [Theory]
    [InlineData("codex", "Codex")]
    [InlineData("claude-code", "Claude Code")]
    [InlineData("agy", "Antigravity (agy)")]
    [InlineData("unknown-agent", "unknown-agent")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void ResolveAgentDisplayName_ShouldUseCatalogDisplayName(string? agentId, string expected)
    {
        RateLimitDisplayName.ResolveAgentDisplayName(agentId).Should().Be(expected);
    }

    [Theory]
    [InlineData("agent://codex", "Weekly制限（全モデル）", "Codex — Weekly制限（全モデル）")]
    [InlineData("agent://claude-code", "5時間枠", "Claude Code — 5時間枠")]
    [InlineData("agent://unknown-agent", "5時間枠", "unknown-agent — 5時間枠")]
    [InlineData("ratelimit://queue/limits", "weekly", "weekly")]
    [InlineData("", "weekly", "weekly")]
    public void BuildFromSourceUri_ShouldPrefixServiceNameForAgentLimits(string sourceUri, string label, string expected)
    {
        // MCP の ratelimit:// リソース由来の枠は URI 自体が出所を示すためラベルのみを使う（#335）
        RateLimitDisplayName.BuildFromSourceUri(sourceUri, label).Should().Be(expected);
    }

    [Fact]
    public void DisplayLabel_ShouldFollowSourceUri()
    {
        // 一覧（MainWindow の RateLimitList）と通知はこのプロパティを表示する
        RateLimitInfo limit = new()
        {
            Id = "base_model_inference:primary",
            Label = "Luna Reserve Weekly制限（Luna専用・Auto-Pause対象外）",
            ResetAt = DateTimeOffset.UtcNow.AddDays(3),
            SourceUri = "agent://codex",
        };

        limit.DisplayLabel.Should().Be("Codex — Luna Reserve Weekly制限（Luna専用・Auto-Pause対象外）");
    }
}
