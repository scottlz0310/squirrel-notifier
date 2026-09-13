// <copyright file="LauncherSessionResumePolicy.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Helpers;

internal enum SessionResumeUnavailableReason
{
    /// <summary>保存済み entry がない.</summary>
    NotFound,

    /// <summary>保存済み entry の TTL が切れている.</summary>
    Expired,

    /// <summary>launcher 設定が ClientAssigned resume に対応していない.</summary>
    UnsupportedPreset,

    /// <summary>保存時と現在の working directory が一致しない.</summary>
    WorkingDirectoryMismatch,
}

internal sealed record LauncherSessionResumeCapability(
    SessionIdSupply SessionIdSupply,
    string? AgentId,
    string NewSessionArgumentsTemplate)
{
    public bool IsSupported => SessionIdSupply == SessionIdSupply.ClientAssigned && AgentId is not null;
}

/// <summary>
/// launcher の実値から ClientAssigned resume の可否と新規 session 引数を決定する。
/// Settings UI と実行経路が同じ判定を共有するための stateless policy.
/// </summary>
internal static class LauncherSessionResumePolicy
{
    private const string _sessionIdPlaceholder = "{sessionId}";

    public static LauncherSessionResumeCapability Evaluate(
        string command,
        string arguments,
        string resumeArguments,
        string presetId,
        LauncherRole role)
    {
        if (presetId != LauncherAgentCatalog.CustomPresetId)
        {
            LauncherAgentDefinition? definition = LauncherAgentCatalog.Find(presetId);
            if (definition is null
                || definition.SessionIdSupply != SessionIdSupply.ClientAssigned
                || definition.Command != command
                || ResolveArguments(definition, role) != arguments
                || ResolveResumeArguments(definition, role) != resumeArguments
                || !ContainsSessionId(definition.NewSessionArgumentsTemplate)
                || !ContainsSessionId(resumeArguments))
            {
                return Unsupported();
            }

            return new LauncherSessionResumeCapability(
                SessionIdSupply.ClientAssigned,
                definition.Id,
                definition.NewSessionArgumentsTemplate);
        }

        if (!ContainsSessionId(resumeArguments))
        {
            return Unsupported();
        }

        LauncherAgentDefinition? commandMatch = LauncherAgentCatalog.FindByCommand(command);
        if (commandMatch?.SessionIdSupply == SessionIdSupply.ClientAssigned
            && ContainsSessionId(commandMatch.NewSessionArgumentsTemplate))
        {
            return new LauncherSessionResumeCapability(
                SessionIdSupply.ClientAssigned,
                commandMatch.Id,
                commandMatch.NewSessionArgumentsTemplate);
        }

        // 任意 CLI の新規 session ID 指定方法は推測できない。通常引数にも placeholder が
        // 明示されている場合だけ、カスタム設定を ClientAssigned として安全に扱う。
        return ContainsSessionId(arguments)
            ? new LauncherSessionResumeCapability(
                SessionIdSupply.ClientAssigned,
                $"custom:{command.Trim().ToUpperInvariant()}",
                string.Empty)
            : Unsupported();
    }

    private static LauncherSessionResumeCapability Unsupported()
        => new(SessionIdSupply.None, null, string.Empty);

    private static bool ContainsSessionId(string template)
        => !string.IsNullOrWhiteSpace(template)
            && template.Contains(_sessionIdPlaceholder, StringComparison.Ordinal);

    private static string ResolveArguments(LauncherAgentDefinition definition, LauncherRole role)
        => role switch
        {
            LauncherRole.Reviewer => definition.ReviewerArgumentsTemplate,
            LauncherRole.Reviewed => definition.ReviewedArgumentsTemplate,
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, "未知の launcher role です。"),
        };

    private static string ResolveResumeArguments(LauncherAgentDefinition definition, LauncherRole role)
        => role switch
        {
            LauncherRole.Reviewer => definition.ReviewerResumeArgumentsTemplate,
            LauncherRole.Reviewed => definition.ReviewedResumeArgumentsTemplate,
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, "未知の launcher role です。"),
        };
}

/// <summary>session resume のログ・UI 文言を一元化する stateless formatter.</summary>
internal static class SessionResumeMessageFormatter
{
    public static string BuildApplied(Guid sessionId)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("Session ID は空 GUID にできません。", nameof(sessionId));
        }

        return $"resumed session {sessionId:D}"[..24] + "…";
    }

    public static string BuildUnavailable(SessionResumeUnavailableReason reason)
    {
        string detail = reason switch
        {
            SessionResumeUnavailableReason.NotFound => "保存済みエントリがありません",
            SessionResumeUnavailableReason.Expired => "保存済みエントリの TTL が切れています",
            SessionResumeUnavailableReason.UnsupportedPreset => "現在の launcher 設定は resume に対応していません",
            SessionResumeUnavailableReason.WorkingDirectoryMismatch => "working directory が保存時と一致しません",
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "未知の resume 不能理由です。"),
        };

        return $"セッション resume を利用できません（{detail}）。新規セッションで起動します。";
    }

    public static string BuildFailure()
        => "セッション resume に失敗したため保存情報を破棄しました。次回は新規セッションで起動します。";

    public static string BuildCapabilityLabel(LauncherSessionResumeCapability capability)
    {
        ArgumentNullException.ThrowIfNull(capability);
        return capability.IsSupported
            ? "resume 対応（ClientAssigned）"
            : "resume 非対応（resume と新規起動の両方で {sessionId} を指定できる設定が必要です）";
    }
}
