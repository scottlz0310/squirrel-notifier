// <copyright file="ReviewCycleRef.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SquirrelNotifier.WinUI3.Models;

/// <summary>
/// review-raven の依頼 cycle に結び付いた queue event の同一性（review-raven#144 の契約 v1 の
/// <c>cycleId</c> / <c>candidateId</c> / <c>headSha</c> / <c>round</c>）.
/// review-raven 経由（<c>accept_review_candidate</c>）の候補にだけ付き、旧経路（<c>enqueue_review</c>・webhook）の
/// 候補には無い。観測を review-raven へ送るときの相関にだけ使い、公開状態（<c>review-status.json</c>）には載せない.
/// </summary>
/// <param name="CandidateId">reviewer が結果の投稿まで束縛する候補の ID（<c>cand_</c> + 26 桁）.</param>
/// <param name="CycleId">依頼 cycle の ID（<c>cyc_</c> + 26 桁）.</param>
/// <param name="Round">cycle のラウンド（1 以上）.</param>
/// <param name="HeadSha">レビュー対象の HEAD（40 桁の小文字 16 進）.</param>
internal sealed record ReviewCycleRef(
    [property: JsonPropertyName("candidateId")] string CandidateId,
    [property: JsonPropertyName("cycleId")] string CycleId,
    [property: JsonPropertyName("round")] int Round,
    [property: JsonPropertyName("headSha")] string HeadSha)
{
    private static readonly Regex _cycleIdPattern = new("^cyc_[0-9a-z]{26}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex _candidateIdPattern = new("^cand_[0-9a-z]{26}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex _headShaPattern = new("^[0-9a-f]{40}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// queue の候補の <c>cycle</c> を読む。形式が契約に合わない場合は、観測を相関できないため <see langword="null"/> を返す
    /// （候補全体は壊さない。旧経路の候補と同じく、観測を送らないだけ）.
    /// </summary>
    /// <param name="element">候補の <c>cycle</c>。無ければ <see langword="null"/>.</param>
    /// <returns>契約に合う場合の同一性。それ以外は <see langword="null"/>.</returns>
    internal static ReviewCycleRef? TryRead(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Object } cycle)
        {
            return null;
        }

        string? candidateId = ReadString(cycle, "candidateId");
        string? cycleId = ReadString(cycle, "cycleId");
        string? headSha = ReadString(cycle, "headSha");
        if (candidateId is null
            || cycleId is null
            || headSha is null
            || !cycle.TryGetProperty("round", out JsonElement round)
            || round.ValueKind != JsonValueKind.Number
            || !round.TryGetInt32(out int roundValue)
            || roundValue < 1)
        {
            return null;
        }

        ReviewCycleRef candidate = new(candidateId, cycleId, roundValue, headSha);
        return candidate.IsValid ? candidate : null;
    }

    // 契約の ID と HEAD の形式を満たすか
    internal bool IsValid
        => _candidateIdPattern.IsMatch(CandidateId)
            && _cycleIdPattern.IsMatch(CycleId)
            && _headShaPattern.IsMatch(HeadSha)
            && Round >= 1;

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
