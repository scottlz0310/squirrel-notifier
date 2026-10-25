// <copyright file="ReviewObservation.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Text.Json.Serialization;

namespace SquirrelNotifier.WinUI3.Models;

/// <summary>
/// review-raven へ送る実行状態の観測（review-raven#144 の契約 v1 の <c>publish_review_observation</c> の入力）.
/// 契約の全項目を常に出力する（値が無い項目は <see langword="null"/>）。許可項目だけで、ローカルパス・コマンドライン・
/// ログ本文・秘密値は含まない。公開状態（<c>review-status.json</c>）とは別の契約で、そちらの形式は変えない.
/// </summary>
/// <param name="SchemaVersion">契約の版（1）.</param>
/// <param name="CycleId">依頼 cycle の ID.</param>
/// <param name="CandidateId">観測する候補の ID.</param>
/// <param name="HeadSha">レビュー対象の HEAD.</param>
/// <param name="ProducerId">観測の送信主体の ID。review-raven の保護設定で、サービス主体に割り当てられた ID.</param>
/// <param name="ProducerEpoch">送信主体の世代。プロセスの起動ごとに増える.</param>
/// <param name="Sequence">同じ cycle・世代の中で増える連番.</param>
/// <param name="ObservedAt">観測した時刻（UTC）.</param>
/// <param name="State">段階（<c>waiting</c> / <c>running</c> / <c>finished</c>）.</param>
/// <param name="HoldReason">起動待ちの理由（<c>waiting</c> のときだけ）.</param>
/// <param name="Outcome">プロセスの終了結果（<c>finished</c> のときだけ）。レビューの結果（Verdict）ではない.</param>
/// <param name="ExitCode">プロセスの終了コード（<c>finished</c> のときだけ）.</param>
/// <param name="Stopped">送信主体が止まる（アプリを終了する）ことを知らせる.</param>
internal sealed record ReviewObservation(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("cycleId")] string CycleId,
    [property: JsonPropertyName("candidateId")] string CandidateId,
    [property: JsonPropertyName("headSha")] string HeadSha,
    [property: JsonPropertyName("producerId")] string ProducerId,
    [property: JsonPropertyName("producerEpoch")] long ProducerEpoch,
    [property: JsonPropertyName("sequence")] long Sequence,
    [property: JsonPropertyName("observedAt")] string ObservedAt,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("holdReason")] string? HoldReason,
    [property: JsonPropertyName("outcome")] string? Outcome,
    [property: JsonPropertyName("exitCode")] int? ExitCode,
    [property: JsonPropertyName("stopped")] bool Stopped);
