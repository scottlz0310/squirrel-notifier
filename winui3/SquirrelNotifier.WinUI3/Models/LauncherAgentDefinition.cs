// <copyright file="LauncherAgentDefinition.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SquirrelNotifier.WinUI3.Models;

/// <summary>
/// reviewer / reviewed launcher スロットへ既定のコマンド・引数テンプレートを提供する
/// 実行エージェントの定義（#149）。.
/// </summary>
/// <param name="Id">プリセット ID。<see cref="RateLimitAgentDefinition.Id"/> とは独立した名前空間.</param>
/// <param name="DisplayName">Settings UI に表示する名称.</param>
/// <param name="Command">既定のコマンド（<c>CommandPath</c>）.</param>
/// <param name="ReviewerArgumentsTemplate">reviewer スロット用の既定引数テンプレート.</param>
/// <param name="ReviewedArgumentsTemplate">reviewed スロット用の既定引数テンプレート.</param>
/// <param name="RateLimitAgentId">
/// レートリミット監視エージェント ID（<see cref="RateLimitAgentCatalog"/> の Id）への対応付け。
/// 取得手段が無いエージェントは <see langword="null"/>.
/// </param>
internal sealed record LauncherAgentDefinition(
    string Id,
    string DisplayName,
    string Command,
    string ReviewerArgumentsTemplate,
    string ReviewedArgumentsTemplate,
    string? RateLimitAgentId);

/// <summary>
/// launcher スロットに選択できる実行エージェントのプリセット一覧（#149）.
/// 新しいエージェントを追加・削除する場合はこのリストのみを変更する.
/// </summary>
internal static class LauncherAgentCatalog
{
    /// <summary>
    /// カタログのどのプリセットとも一致しない、自由編集された設定値を表す ID.
    /// </summary>
    public const string CustomPresetId = "custom";

    /// <summary>
    /// Settings UI のプリセット選択 ComboBox 専用の「カスタム」項目。command / arguments が
    /// どのプリセットとも一致しない自由編集状態であることを表す（起動には使われない）.
    /// </summary>
    public static readonly LauncherAgentDefinition CustomPreset = new(
        CustomPresetId, "カスタム", string.Empty, string.Empty, string.Empty, null);

    public static readonly IReadOnlyList<LauncherAgentDefinition> All =
    [
        new LauncherAgentDefinition(
            "claude",
            "claude",
            "claude",
            "-p \"/thread-owl-pr-reviewer {owner}/{repo}#{prNumber} を {reason} モードでレビューしてください\"",
            "-p \"/review-raven-thread-owl-cycle {owner}/{repo}#{prNumber} のレビュー指摘に対応してください\"",
            "claude-code"),

        // codex / agy / copilot はスキル呼び出し機構を持たないため、プロンプト全文を
        // テンプレートに埋め込む（MCP サーバー接続設定自体は Mcp-Docker の責務）.
        new LauncherAgentDefinition(
            "codex",
            "codex",
            "codex",
            "exec \"thread-owl MCP のツールを使って {owner}/{repo}#{prNumber} を {reason} モードでレビューしてください\"",
            "exec \"thread-owl MCP のツールを使って {owner}/{repo}#{prNumber} のレビュー指摘に対応し、修正・返信・resolve を行ってください\"",
            "codex"),

        new LauncherAgentDefinition(
            "agy",
            "agy (Antigravity CLI)",
            "agy",
            "-p \"thread-owl MCP のツールを使って {owner}/{repo}#{prNumber} を {reason} モードでレビューしてください\"",
            "-p \"thread-owl MCP のツールを使って {owner}/{repo}#{prNumber} のレビュー指摘に対応し、修正・返信・resolve を行ってください\"",
            "agy"),

        new LauncherAgentDefinition(
            "copilot",
            "copilot (GitHub Copilot CLI)",
            "copilot",
            "-p \"thread-owl MCP のツールを使って {owner}/{repo}#{prNumber} を {reason} モードでレビューしてください\"",
            "-p \"thread-owl MCP のツールを使って {owner}/{repo}#{prNumber} のレビュー指摘に対応し、修正・返信・resolve を行ってください\"",
            null),
    ];

    /// <summary>
    /// <see cref="All"/> に <see cref="CustomPreset"/> を加えた、Settings UI のプリセット選択
    /// ComboBox 用の一覧.
    /// </summary>
    public static readonly IReadOnlyList<LauncherAgentDefinition> AllWithCustomOption = [.. All, CustomPreset];

    /// <summary>
    /// 指定した ID のプリセットを取得する。見つからない場合は <see langword="null"/>.
    /// </summary>
    /// <param name="id">プリセット ID.</param>
    /// <returns>一致したプリセット。見つからない場合は <see langword="null"/>.</returns>
    public static LauncherAgentDefinition? Find(string id) => All.FirstOrDefault(d => d.Id == id);

    /// <summary>
    /// 現在の command / arguments 値がどのプリセットと一致するかを判定する。
    /// どれとも一致しない場合は <see cref="CustomPresetId"/> を返す.
    /// </summary>
    /// <param name="command">現在の command 値.</param>
    /// <param name="arguments">現在の arguments 値.</param>
    /// <param name="role">判定対象の launcher スロット.</param>
    /// <returns>一致したプリセットの ID。一致しない場合は <see cref="CustomPresetId"/>.</returns>
    public static string ResolvePresetId(string command, string arguments, LauncherRole role)
    {
        foreach (LauncherAgentDefinition definition in All)
        {
            string expectedArguments = role == LauncherRole.Reviewer
                ? definition.ReviewerArgumentsTemplate
                : definition.ReviewedArgumentsTemplate;

            if (definition.Command == command && expectedArguments == arguments)
            {
                return definition.Id;
            }
        }

        return CustomPresetId;
    }
}
