// <copyright file="RateLimitAgentDefinition.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SquirrelNotifier.WinUI3.Models;

/// <summary>
/// レートリミット状態を取得できる CLI エージェントの定義。取得経路はエージェントにより異なる
/// （claude-code / agy は statusline フック由来のローカルファイル、codex は App Server 直読み #163）.
/// </summary>
internal sealed record RateLimitAgentDefinition(string Id, string DisplayName, bool IsAvailable);

/// <summary>
/// レートリミット監視対象の候補エージェント一覧（#139）.
/// 新しいエージェントを追加・削除する場合はこのリストのみを変更する.
/// 表示名はサービス名として読める形に揃える（#335）。<see cref="RateLimitAgentDefinition.Id"/> は
/// settings.json の永続値のため変更しない.
/// </summary>
internal static class RateLimitAgentCatalog
{
    public static readonly IReadOnlyList<RateLimitAgentDefinition> All =
    [
        new RateLimitAgentDefinition("claude-code", "Claude Code", IsAvailable: true),
        new RateLimitAgentDefinition("agy", "Antigravity (agy)", IsAvailable: true),

        // codex は statusline フックが外部コマンドに JSON を渡さないためローカルファイル経由は
        // 不可能だが、App Server（account/rateLimits/read）経由で取得できる（spike #157 / #163）.
        new RateLimitAgentDefinition("codex", "Codex", IsAvailable: true),
    ];
}
