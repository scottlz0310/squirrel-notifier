// <copyright file="RateLimitAlertFormatter.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Collections.Generic;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Helpers;

/// <summary>
/// レートリミット更新（#266）でユーザーへ提示する文言を組み立てる。表示手段（<c>ContentDialog</c> /
/// <c>InfoBar</c>）は呼び出し側が決め、ここでは文面だけを決める.
/// </summary>
internal static class RateLimitAlertFormatter
{
    /// <summary>
    /// codex の取得不可理由を原因ごとに出し分ける（#174）。JSON-RPC error の code/message は
    /// codex CLI バージョンにより変わりうるため確実に判別できず、
    /// <see cref="CodexRateLimitFailureReason.Unknown"/> は「未ログインの可能性を含む」表現に留めて断定しない.
    /// </summary>
    /// <param name="agentDisplayName">エージェントの表示名.</param>
    /// <param name="failureReason">推定された失敗理由。判別できなかった場合は <see langword="null"/>.</param>
    /// <returns>ダイアログに出す本文.</returns>
    public static string BuildCodexFailureMessage(string agentDisplayName, CodexRateLimitFailureReason? failureReason)
    {
        return failureReason switch
        {
            CodexRateLimitFailureReason.CommandNotFound =>
                $"{agentDisplayName} の codex コマンドが見つかりませんでした。codex CLI がインストールされ、PATH が通っているか確認してください。",
            CodexRateLimitFailureReason.Timeout =>
                $"{agentDisplayName} の Codex App Server が応答しませんでした（タイムアウト）。しばらく待ってから再試行してください。",
            _ =>
                $"{agentDisplayName} のレートリミット情報を Codex App Server から取得できませんでした。codex にログイン済みか確認するか、しばらく待って再試行してください。",
        };
    }

    /// <summary>
    /// copilot の取得不可の原因を案内に組み立てる。原因は <c>gh</c> のマスク・要約済みの出力で、
    /// 未ログイン・非公開 API の仕様変更・Copilot 未契約などを含みうるため断定しない.
    /// </summary>
    /// <param name="agentDisplayName">エージェントの表示名.</param>
    /// <param name="failureDetail">取得不可の原因.</param>
    /// <returns>ダイアログに出す本文.</returns>
    public static string BuildCopilotFailureMessage(string agentDisplayName, string? failureDetail)
        => $"{agentDisplayName} のレートリミット情報を GitHub（gh api copilot_internal/user）から取得できませんでした。gh にログイン済みか確認してください。{(string.IsNullOrWhiteSpace(failureDetail) ? string.Empty : $" 詳細: {failureDetail}")}";

    /// <summary>statusline フックがまだ snapshot を書き出していないエージェント向けの案内（#139）.</summary>
    /// <param name="agentDisplayName">エージェントの表示名.</param>
    /// <returns>ダイアログに出す本文.</returns>
    public static string BuildMissingStatusMessage(string agentDisplayName)
        => $"{agentDisplayName} のレートリミット情報がまだありません。statusline スクリプトの拡張が必要です。詳細は docs/statusline-integration.md を参照してください。";

    /// <summary>ローカルファイルの読み取りが例外で失敗したときの案内.</summary>
    /// <param name="agentDisplayName">エージェントの表示名.</param>
    /// <param name="detail">例外メッセージ.</param>
    /// <returns>ダイアログに出す本文.</returns>
    public static string BuildReadFailureMessage(string agentDisplayName, string detail)
        => $"{agentDisplayName} のレートリミット状態の読み取りに失敗しました: {detail}";

    /// <summary>
    /// 旧形式（schemaVersion を欠く resetAt-only）の snapshot を書き出しているエージェントの警告（#168）。
    /// 一覧表示はできてしまうため気づかれにくく、Auto-Pause gate（#147）が silent に無効化される.
    /// </summary>
    /// <param name="agentDisplayNames">旧形式を書き出しているエージェントの表示名.</param>
    /// <returns>警告の本文。該当なしの場合は <see langword="null"/>（InfoBar を閉じる）.</returns>
    public static string? BuildLegacySchemaMessage(IReadOnlyList<string> agentDisplayNames)
    {
        ArgumentNullException.ThrowIfNull(agentDisplayNames);
        if (agentDisplayNames.Count == 0)
        {
            return null;
        }

        return $"{string.Join("、", agentDisplayNames)} の statusline snapshot が旧形式（schemaVersion なし）のため、"
            + "Auto-Pause は機能しません。statusline フックを更新してください（docs/statusline-integration.md 参照）。";
    }
}
