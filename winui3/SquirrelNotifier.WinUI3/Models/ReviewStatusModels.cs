// <copyright file="ReviewStatusModels.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Text.Json.Serialization;

namespace SquirrelNotifier.WinUI3.Models;

/// <summary>reviewer の起動を見送って保留した理由（#462）.</summary>
internal enum ReviewHoldKind
{
    /// <summary>別のレビューを実行中のため、実行終了後の再評価まで保留している.</summary>
    Busy,

    /// <summary>required checks が確定していないため、確定または上限まで保留している（#456）.</summary>
    CiPending,

    /// <summary>Auto-Pause 中のため、解除後の再評価まで保留している（#340）.</summary>
    AutoPause,
}

/// <summary>公開状態でのレビューの段階（#462）.</summary>
internal enum ReviewStatusState
{
    /// <summary>reviewer の起動を待っている.</summary>
    Waiting,

    /// <summary>reviewer が実行中.</summary>
    Running,

    /// <summary>reviewer のプロセスが終了した.</summary>
    Finished,
}

/// <summary>reviewer のプロセスの終了結果。レビューの結果（Verdict）ではない（#462）.</summary>
internal enum ReviewerOutcome
{
    /// <summary>プロセスが正常に終了した.</summary>
    Completed,

    /// <summary>プロセスが失敗した.</summary>
    Failed,
}

/// <summary>
/// 公開状態（<c>review-status.json</c>）の元になる、PR ごとのレビューの 1 回分の観測（#462）.
/// 起動待ち・実行中・終了のいずれか 1 つの段階を表す.
/// </summary>
/// <param name="Key">小文字の <c>owner/repo#N</c>。thread-owl の <c>review://status</c> の URI と同じ表記.</param>
/// <param name="Repository">元の大文字小文字のままの <c>owner/repo</c>.</param>
/// <param name="PrNumber">PR 番号.</param>
/// <param name="Round">レビューのラウンド.</param>
/// <param name="EventId">この回の queue event の ID.</param>
/// <param name="Reason">queue event の reason.</param>
/// <param name="State">段階.</param>
/// <param name="ReceivedAt">queue event を受信した時刻（UTC）.</param>
/// <param name="StartedAt">reviewer を起動した時刻（UTC）.</param>
/// <param name="FinishedAt">reviewer のプロセスが終了した時刻（UTC）.</param>
/// <param name="Agent">reviewer スロットのプリセット ID.</param>
/// <param name="Hold">保留の理由。保留を観測していない場合は <see langword="null"/>.</param>
/// <param name="HoldSince">保留し始めた時刻（UTC）.</param>
/// <param name="Outcome">プロセスの終了結果.</param>
/// <param name="ExitCode">プロセスの終了コード.</param>
internal sealed record ReviewStatusEntry(
    string Key,
    string Repository,
    int PrNumber,
    int Round,
    string EventId,
    string Reason,
    ReviewStatusState State,
    DateTimeOffset ReceivedAt,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? FinishedAt = null,
    string? Agent = null,
    ReviewHoldKind? Hold = null,
    DateTimeOffset? HoldSince = null,
    ReviewerOutcome? Outcome = null,
    int? ExitCode = null);

/// <summary>アプリ全体の観測（購読の状態など）。<c>review-status.json</c> の元になる.</summary>
/// <param name="AppVersion">アプリの版.</param>
/// <param name="AppStartedAt">アプリを起動した時刻（UTC）.</param>
/// <param name="SubscriptionState">購読の状態（<c>running</c> / <c>starting</c> / <c>stopping</c> / <c>stopped</c> / <c>error</c> / <c>authRequired</c>）.</param>
/// <param name="SubscriptionSince">購読の状態が最後に変わった時刻（UTC）.</param>
/// <param name="AutoStartEnabled">「レビュー自動開始」が on か.</param>
internal sealed record ReviewStatusEnvironment(
    string AppVersion,
    DateTimeOffset AppStartedAt,
    string SubscriptionState,
    DateTimeOffset SubscriptionSince,
    bool AutoStartEnabled);

/// <summary><c>review-status.json</c> の文書（公開契約。schemaVersion 1。#462）.</summary>
internal sealed record ReviewStatusDocument(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("updatedAt")] DateTime UpdatedAt,
    [property: JsonPropertyName("app")] ReviewStatusApp App,
    [property: JsonPropertyName("concurrency")] ReviewStatusConcurrency Concurrency,
    [property: JsonPropertyName("subscription")] ReviewStatusSubscription Subscription,
    [property: JsonPropertyName("items")] IReadOnlyList<ReviewStatusItem> Items,
    [property: JsonPropertyName("recent")] IReadOnlyList<ReviewStatusItem> Recent);

internal sealed record ReviewStatusApp(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("startedAt")] DateTime StartedAt);

internal sealed record ReviewStatusConcurrency(
    [property: JsonPropertyName("active")] int Active,
    [property: JsonPropertyName("max")] int Max);

internal sealed record ReviewStatusSubscription(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("since")] DateTime Since);

/// <summary><c>items[]</c>（起動待ち・実行中）と <c>recent[]</c>（終了）の 1 件.</summary>
internal sealed record ReviewStatusItem(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("repository")] string Repository,
    [property: JsonPropertyName("prNumber")] int PrNumber,
    [property: JsonPropertyName("round")] int Round,
    [property: JsonPropertyName("eventId")] string EventId,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("receivedAt")] DateTime ReceivedAt,
    [property: JsonPropertyName("startedAt"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTime? StartedAt,
    [property: JsonPropertyName("finishedAt"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTime? FinishedAt,
    [property: JsonPropertyName("agent"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Agent,
    [property: JsonPropertyName("holdReason"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? HoldReason,
    [property: JsonPropertyName("holdSince"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTime? HoldSince,
    [property: JsonPropertyName("queuePosition"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? QueuePosition,
    [property: JsonPropertyName("outcome"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Outcome,
    [property: JsonPropertyName("exitCode"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ExitCode);
