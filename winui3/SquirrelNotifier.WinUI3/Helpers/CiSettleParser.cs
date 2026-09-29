// <copyright file="CiSettleParser.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Text.Json;
using System.Text.RegularExpressions;

namespace SquirrelNotifier.WinUI3.Helpers;

/// <summary>PR の状態と、CI の確定判定に必要な head の情報（#456）.</summary>
/// <param name="IsClosed">PR が merge または close されているか.</param>
/// <param name="HeadSha">PR の現在の head SHA.</param>
/// <param name="BaseRef">PR の base ブランチ名.</param>
internal sealed record CiPullRequestInfo(bool IsClosed, string HeadSha, string BaseRef);

/// <summary>required な status check（check run の名前、または commit status の context）.</summary>
/// <param name="Context">check の名前.</param>
/// <param name="IntegrationId">check を報告すべき GitHub App の ID。指定が無い（任意の App）場合は <see langword="null"/>.</param>
internal sealed record RequiredCheck(string Context, long? IntegrationId);

/// <summary>SHA に対して報告された check run（名前と状態だけを持つ）.</summary>
/// <param name="Id">check run の ID。再実行で同じ名前の run が増えるため、新旧の判定に使う.</param>
/// <param name="Name">check の名前.</param>
/// <param name="Status">run の進行状態（<c>completed</c> 以外は未完了）.</param>
/// <param name="Conclusion">完了した run の結論。未完了の場合は <see langword="null"/>.</param>
/// <param name="AppId">run を報告した GitHub App の ID.</param>
internal sealed record CheckRunInfo(long Id, string Name, string Status, string? Conclusion, long? AppId);

/// <summary>SHA に対して報告された commit status（Status API）.</summary>
/// <param name="Context">status の context.</param>
/// <param name="State"><c>success</c> / <c>pending</c> / <c>failure</c> / <c>error</c>.</param>
internal sealed record CommitStatusInfo(string Context, string State);

/// <summary>
/// <c>gh api --jq</c> が出力した、check の名前と状態だけを絞り込んだ JSON を解釈する（#456）。
/// jq 式の出力形式（<c>GhCiSettleSource</c> が指定する）と対で保守する.
/// 想定外の形は <see cref="JsonException"/> を送出する.
/// </summary>
internal static class CiSettleParser
{
    private static readonly Regex _shaPattern = new(@"^[0-9a-f]{40}([0-9a-f]{24})?$", RegexOptions.Compiled);

    public static CiPullRequestInfo ParsePullRequest(string output)
    {
        using JsonDocument document = JsonDocument.Parse(output);
        JsonElement root = document.RootElement;

        string state = GetString(root, "state");
        bool isClosed = state switch
        {
            "open" => false,
            "closed" => true,
            _ => throw new JsonException($"PR の state が想定外です: {state}"),
        };

        string headSha = GetString(root, "headSha");
        if (!_shaPattern.IsMatch(headSha))
        {
            throw new JsonException("PR の head SHA の形式が不正です。");
        }

        return new CiPullRequestInfo(isClosed, headSha, GetString(root, "baseRef"));
    }

    /// <summary>
    /// ruleset の <c>rules/branches/&lt;branch&gt;</c> から絞り込んだ、行ごとに 1 件の JSON（<c>{context, integrationId}</c>）を解釈する。
    /// ページングされた応答を連結した出力を、そのまま読める形にしている.
    /// </summary>
    /// <param name="output">jq が required check ごとに 1 行で出力した JSON.</param>
    /// <returns>required な status check の一覧（重複は除く）.</returns>
    public static IReadOnlyList<RequiredCheck> ParseRulesetRequiredChecks(string output)
        => [.. ParseLines(output, element => new RequiredCheck(GetString(element, "context"), GetOptionalAppId(element, "integrationId"))).Distinct()];

    /// <summary>classic の branch protection から絞り込んだ <c>{contexts, checks: [{context, appId}]}</c> を解釈する.</summary>
    /// <param name="output">jq で絞り込んだ JSON オブジェクト.</param>
    /// <returns>required な status check の一覧.</returns>
    public static IReadOnlyList<RequiredCheck> ParseClassicRequiredChecks(string output)
    {
        using JsonDocument document = JsonDocument.Parse(output);
        JsonElement root = document.RootElement;

        List<RequiredCheck> checks = [.. ParseRequiredCheckArray(GetArray(root, "checks"), "appId")];

        // contexts は checks の旧形式の写し。App の指定が無い check は contexts にだけ現れる
        foreach (JsonElement context in GetArray(root, "contexts").EnumerateArray())
        {
            if (context.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(context.GetString()))
            {
                throw new JsonException("contexts に文字列以外が含まれています。");
            }

            string name = context.GetString()!;
            if (!checks.Exists(check => string.Equals(check.Context, name, StringComparison.Ordinal)))
            {
                checks.Add(new RequiredCheck(name, null));
            }
        }

        return checks;
    }

    /// <summary>行ごとに 1 件の JSON（<c>{id, name, status, conclusion, appId}</c>）を解釈する.</summary>
    /// <param name="output">jq が check run ごとに 1 行で出力した JSON.</param>
    /// <returns>check run の一覧.</returns>
    public static IReadOnlyList<CheckRunInfo> ParseCheckRuns(string output)
        => ParseLines(
            output,
            element => new CheckRunInfo(
                GetInt64(element, "id"),
                GetString(element, "name"),
                GetString(element, "status"),
                GetOptionalString(element, "conclusion"),
                GetOptionalAppId(element, "appId")));

    /// <summary>行ごとに 1 件の JSON（<c>{context, state}</c>）を解釈する.</summary>
    /// <param name="output">jq が commit status ごとに 1 行で出力した JSON.</param>
    /// <returns>commit status の一覧.</returns>
    public static IReadOnlyList<CommitStatusInfo> ParseCommitStatuses(string output)
        => ParseLines(
            output,
            element => new CommitStatusInfo(GetString(element, "context"), GetString(element, "state")));

    private static List<RequiredCheck> ParseRequiredCheckArray(JsonElement array, string appIdProperty)
    {
        if (array.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("required checks が配列ではありません。");
        }

        List<RequiredCheck> checks = [];
        foreach (JsonElement element in array.EnumerateArray())
        {
            RequiredCheck check = new(GetString(element, "context"), GetOptionalAppId(element, appIdProperty));
            if (!checks.Contains(check))
            {
                checks.Add(check);
            }
        }

        return checks;
    }

    private static List<T> ParseLines<T>(string output, Func<JsonElement, T> map)
    {
        List<T> items = [];
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using JsonDocument document = JsonDocument.Parse(line);
            items.Add(map(document.RootElement));
        }

        return items;
    }

    private static JsonElement GetArray(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.Array
                ? value
                : throw new JsonException($"'{name}' が配列として含まれていません。");

    private static string GetString(JsonElement element, string name)
        => GetOptionalString(element, name) is { Length: > 0 } value
            ? value
            : throw new JsonException($"'{name}' が文字列として含まれていません。");

    private static string? GetOptionalString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

    private static long GetInt64(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out long number)
                ? number
                : throw new JsonException($"'{name}' が整数として含まれていません。");

    // GitHub は「任意の App」を null または -1 で表す
    private static long? GetOptionalAppId(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out long number)
            && number > 0
                ? number
                : null;
}
