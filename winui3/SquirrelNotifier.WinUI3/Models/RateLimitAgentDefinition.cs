// <copyright file="RateLimitAgentDefinition.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SquirrelNotifier.WinUI3.Models;

/// <summary>
/// statusline フック経由でレートリミット状態をローカルファイルに書き出せる CLI エージェントの定義.
/// </summary>
internal sealed record RateLimitAgentDefinition(string Id, string DisplayName, bool IsAvailable);

/// <summary>
/// レートリミット監視対象の候補エージェント一覧（#139）.
/// 新しいエージェントを追加・削除する場合はこのリストのみを変更する.
/// </summary>
internal static class RateLimitAgentCatalog
{
    public static readonly IReadOnlyList<RateLimitAgentDefinition> All =
    [
        new RateLimitAgentDefinition("claude-code", "claude-code", IsAvailable: true),
        new RateLimitAgentDefinition("agy", "agy (Antigravity CLI)", IsAvailable: true),

        // codex は statusline フックが外部コマンドに JSON を渡さず、notify にも
        // rate limit フィールドが無いため、現時点ではローカルファイル経由の取得が不可能.
        // 参照: https://github.com/openai/codex/issues/16037, /issues/20310
        new RateLimitAgentDefinition("codex", "codex (対応待ち)", IsAvailable: false),
    ];
}
