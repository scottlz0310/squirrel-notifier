// <copyright file="LauncherAgentDefinition.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SquirrelNotifier.WinUI3.Models;

/// <summary>
/// エージェントプリセットの progress event contract（#143、<c>docs/progress-event-contract.md</c>）
/// への対応度（#151）。consumer 側の挙動は「構造化イベントを出力する統合が存在するか」のみで
/// 決まるため 2 値とし、将来 producer 統合の形が増えた場合に値を追加する.
/// </summary>
internal enum ProgressEventSupport
{
    /// <summary>構造化 progress event を出力する統合が無い。phase 表示のない indeterminate 表示になる.</summary>
    None,

    /// <summary>@squirrel-progress マーカー行を出力する統合（スキル組み込み等）が存在する.</summary>
    Structured,
}

/// <summary>
/// launcher が session ID を取得する方法。consumer は CLI 名や出力から推測せず、
/// この宣言だけで新規 session の採番または出力解析の要否を判断する（#303）.
/// </summary>
internal enum SessionIdSupply
{
    /// <summary>session resume をサポートしない.</summary>
    None,

    /// <summary>アプリが新規 session の UUID を採番して CLI へ渡す.</summary>
    ClientAssigned,

    /// <summary>CLI の構造化出力から session ID を取得する.</summary>
    ParsedFromOutput,
}

/// <summary>
/// reviewer / reviewed launcher スロットへ既定のコマンド・引数テンプレートを提供する
/// 実行エージェントの定義（#149）。.
/// </summary>
/// <param name="Id">プリセット ID。<see cref="RateLimitAgentDefinition.Id"/> とは独立した名前空間.</param>
/// <param name="DisplayName">Settings UI に表示する名称.</param>
/// <param name="Command">既定のコマンド（<c>CommandPath</c>）.</param>
/// <param name="ReviewerArgumentsTemplate">reviewer スロット用の既定引数テンプレート.</param>
/// <param name="ReviewedArgumentsTemplate">reviewed スロット用の既定引数テンプレート.</param>
/// <param name="NewSessionArgumentsTemplate">
/// client-assigned session の新規起動時に通常引数へ追加する引数テンプレート。
/// 空文字は追加引数なしを表す.
/// </param>
/// <param name="ReviewerResumeArgumentsTemplate">reviewer スロット用の resume 引数テンプレート.</param>
/// <param name="ReviewedResumeArgumentsTemplate">reviewed スロット用の resume 引数テンプレート.</param>
/// <param name="RateLimitAgentId">
/// レートリミット監視エージェント ID（<see cref="RateLimitAgentCatalog"/> の Id）への対応付け。
/// 取得手段が無いエージェントは <see langword="null"/>.
/// </param>
/// <param name="ProgressEventSupport">
/// progress event contract への対応度（#151）。省略時は <see cref="ProgressEventSupport.None"/>
/// （新規プリセットは安全側の未対応扱い）.
/// </param>
/// <param name="SessionIdSupply">
/// session ID の供給方式（#303）。省略時は <see cref="SessionIdSupply.None"/>
/// （新規プリセットは安全側の resume 未対応扱い）.
/// </param>
internal sealed record LauncherAgentDefinition(
    string Id,
    string DisplayName,
    string Command,
    string ReviewerArgumentsTemplate,
    string ReviewedArgumentsTemplate,
    string NewSessionArgumentsTemplate,
    string ReviewerResumeArgumentsTemplate,
    string ReviewedResumeArgumentsTemplate,
    string? RateLimitAgentId,
    ProgressEventSupport ProgressEventSupport = ProgressEventSupport.None,
    SessionIdSupply SessionIdSupply = SessionIdSupply.None);

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
        CustomPresetId,
        "カスタム",
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        null);

    // claude はスキル定義への progress スニペット組み込み（docs/samples/skill-progress-snippet.md）
    // により @squirrel-progress を出力できる。codex / agy / copilot はスキル機構が無く
    // 構造化イベントの producer 統合が未整備のため None（indeterminate 表示）とする.
    //
    // claude の print mode は既定（text）だと最終応答しか stdout へ出力せず、スキルが echo する
    // マーカーを実行中に取得できない（#187）。stream-json でリアルタイムイベントを受け取り、
    // ClaudeStreamJsonEventExtractor がマーカーを抽出する。-p + stream-json は CLI 仕様で
    // --verbose が必須（無いと即時エラー終了する）.
    public static readonly IReadOnlyList<LauncherAgentDefinition> All =
    [
        new LauncherAgentDefinition(
            "claude",
            "claude",
            "claude",
            "-p \"/thread-owl-pr-reviewer {owner}/{repo}#{prNumber} を {reason} モードでレビューしてください\" --verbose --output-format stream-json",
            "-p \"/review-raven-thread-owl-cycle {owner}/{repo}#{prNumber} のレビュー指摘に対応してください\" --verbose --output-format stream-json",
            "--session-id {sessionId}",
            "-p \"/thread-owl-pr-reviewer {owner}/{repo}#{prNumber} を {reason} モードでレビューしてください\" --resume {sessionId} --verbose --output-format stream-json",
            "-p \"/review-raven-thread-owl-cycle {owner}/{repo}#{prNumber} のレビュー指摘に対応してください\" --resume {sessionId} --verbose --output-format stream-json",
            "claude-code",
            ProgressEventSupport: ProgressEventSupport.Structured,
            SessionIdSupply: SessionIdSupply.ClientAssigned),

        // codex / agy / copilot はスキル呼び出し機構を持たないため、プロンプト全文を
        // テンプレートに埋め込む（MCP サーバー接続設定自体は Mcp-Docker の責務）.
        new LauncherAgentDefinition(
            "codex",
            "codex",
            "codex",
            "exec --skip-git-repo-check \"thread-owl MCP のツールを使って {owner}/{repo}#{prNumber} を {reason} モードでレビューしてください\"",
            "exec \"thread-owl MCP のツールを使って {owner}/{repo}#{prNumber} のレビュー指摘に対応し、修正・返信・resolve を行ってください\"",
            string.Empty,
            "exec resume --skip-git-repo-check {sessionId} \"thread-owl MCP のツールを使って {owner}/{repo}#{prNumber} を {reason} モードでレビューしてください\"",
            "exec resume {sessionId} \"thread-owl MCP のツールを使って {owner}/{repo}#{prNumber} のレビュー指摘に対応し、修正・返信・resolve を行ってください\"",
            "codex",
            SessionIdSupply: SessionIdSupply.ParsedFromOutput),

        new LauncherAgentDefinition(
            "agy",
            "agy (Antigravity CLI)",
            "agy",
            "--print-timeout 30m -p \"thread-owl MCP のツールを使って {owner}/{repo}#{prNumber} を {reason} モードでレビューしてください\"",
            "--print-timeout 30m -p \"thread-owl MCP のツールを使って {owner}/{repo}#{prNumber} のレビュー指摘に対応し、修正・返信・resolve を行ってください\"",
            string.Empty,
            "--print-timeout 30m --conversation {sessionId} -p \"thread-owl MCP のツールを使って {owner}/{repo}#{prNumber} を {reason} モードでレビューしてください\"",
            "--print-timeout 30m --conversation {sessionId} -p \"thread-owl MCP のツールを使って {owner}/{repo}#{prNumber} のレビュー指摘に対応し、修正・返信・resolve を行ってください\"",
            "agy",
            SessionIdSupply: SessionIdSupply.ParsedFromOutput),

        new LauncherAgentDefinition(
            "copilot",
            "copilot (GitHub Copilot CLI)",
            "copilot",
            "-p \"thread-owl MCP のツールを使って {owner}/{repo}#{prNumber} を {reason} モードでレビューしてください\"",
            "-p \"thread-owl MCP のツールを使って {owner}/{repo}#{prNumber} のレビュー指摘に対応し、修正・返信・resolve を行ってください\"",
            "--session-id {sessionId}",
            "-p \"thread-owl MCP のツールを使って {owner}/{repo}#{prNumber} を {reason} モードでレビューしてください\" --session-id {sessionId}",
            "-p \"thread-owl MCP のツールを使って {owner}/{repo}#{prNumber} のレビュー指摘に対応し、修正・返信・resolve を行ってください\" --session-id {sessionId}",
            null,
            SessionIdSupply: SessionIdSupply.ClientAssigned),
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
    /// Settings UI の ComboBox から選択可能なプリセットを取得する。カスタム項目も含む.
    /// </summary>
    /// <param name="id">検索するプリセット ID.</param>
    /// <returns>一致したプリセット。見つからない場合は <see langword="null"/>.</returns>
    public static LauncherAgentDefinition? FindWithCustomOption(string id)
        => AllWithCustomOption.FirstOrDefault(d => d.Id == id);

    /// <summary>
    /// command のみでプリセットを解決する。見つからない場合は <see langword="null"/>（#233）.
    /// </summary>
    /// <remarks>
    /// arguments を編集しただけで <see cref="ResolvePresetId"/> は <see cref="CustomPresetId"/> を返すが、
    /// 実行されるエージェントは変わっていない。レートリミット監視対象の解決のように「どのエージェントが
    /// 動くか」だけが問題になる用途では、arguments の差分で判定を落とさないためにこちらを使う。
    /// 逆に progress event 対応度は arguments（<c>--output-format stream-json</c> の有無等）に
    /// 依存するため、この緩い判定を使ってはならない.
    /// </remarks>
    /// <param name="command">現在の command 値.</param>
    /// <returns>command が一致したプリセット。見つからない場合は <see langword="null"/>.</returns>
    public static LauncherAgentDefinition? FindByCommand(string command)
        => All.FirstOrDefault(d => d.Command == command);

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
