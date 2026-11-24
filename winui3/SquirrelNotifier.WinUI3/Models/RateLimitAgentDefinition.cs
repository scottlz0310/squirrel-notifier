// <copyright file="RateLimitAgentDefinition.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SquirrelNotifier.WinUI3.Models;

/// <summary>
/// レートリミット状態を取得できる CLI エージェントの定義。取得経路はエージェントにより異なる
/// （claude-code / agy は statusline フック由来のローカルファイル、codex は App Server 直読み #163、
/// copilot は GitHub の quota API 直読み）.
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

        // codex は 0.171.0+ で TUI 向け外部 statusline（実験機能・stdio MCP provider）に対応したが、
        // 非対話 exec 実行時の鮮度・Delta・Auto-Pause 自動解除および実験機能非依存のため、
        // 引き続き App Server（account/rateLimits/read）直読みを正本とする（spike #157 / #163）.
        new RateLimitAgentDefinition("codex", "Codex", IsAvailable: true),

        // copilot は statusline を持たないため、GitHub の quota（gh api copilot_internal/user、非公開 API）から取得する.
        new RateLimitAgentDefinition("copilot", "GitHub Copilot", IsAvailable: true),
    ];
}
