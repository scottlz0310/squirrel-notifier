// <copyright file="CodexAppServerRateLimitClient.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>
/// Codex App Server（<c>codex app-server</c>、stdio JSON-RPC）からレートリミット snapshot を
/// 取得する（#163）。statusline フックと異なり任意タイミングで呼べるため、observedAt は常に
/// 取得時刻（= fresh）になる。呼び出しごとにプロセスを起動し、initialize →
/// account/rateLimits/read の round-trip 後に終了する（spike #157 実測でレイテンシ約 1 秒）。
/// 未ログイン・起動失敗・タイムアウト・JSON-RPC error は「取得不可」の正常系として
/// <see langword="null"/> を返し、launcher 起動やレビュー実行を妨げない。
/// 認証ファイルの読み取り・TUI 出力のパース・consume 系 API の呼び出しは行わない.
/// </summary>
internal sealed class CodexAppServerRateLimitClient
{
    private const string _commandFileName = "codex";
    private const string _commandArguments = "app-server";
    private const int _initializeRequestId = 1;
    private const int _readRequestId = 2;
    private static readonly TimeSpan _defaultRoundTripTimeout = TimeSpan.FromSeconds(15);

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IProcessRunner _processRunner;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _roundTripTimeout;

    public CodexAppServerRateLimitClient(
        IProcessRunner processRunner,
        TimeProvider? timeProvider = null,
        TimeSpan? roundTripTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(processRunner);

        _processRunner = processRunner;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _roundTripTimeout = roundTripTimeout ?? _defaultRoundTripTimeout;
    }

    public async Task<RateLimitSnapshot?> CaptureAsync(string agentId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        try
        {
            return await CaptureCoreAsync(agentId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // 未ログイン・App Server 起動失敗・round-trip タイムアウト・プロトコル不整合は
            // すべて「取得不可」の正常系として扱う（#163。#146/#147 のパターンに準拠）
            return null;
        }
    }

    /// <summary>
    /// <c>account/rateLimits/read</c> の result を共通スキーマの snapshot へ正規化する。
    /// multi-bucket view（rateLimitsByLimitId）を優先し、無い場合は後方互換の single-bucket
    /// view（rateLimits）へ fallback する。ResetAt は既存モデル（<see cref="RateLimitInfo.Validate"/>）
    /// で必須のため、resetsAt を持たない window は取得不可として除外する.
    /// </summary>
    /// <param name="agentId">snapshot に記録する agentId.</param>
    /// <param name="result">read の result payload.</param>
    /// <param name="observedAt">読み取り実行時刻.</param>
    /// <returns>1 件以上の limit を正規化できた場合は snapshot。それ以外は <see langword="null"/>.</returns>
    internal static RateLimitSnapshot? Normalize(string agentId, CodexRateLimitsReadResult result, DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(result);

        List<RateLimitInfo> limits = new();
        if (result.RateLimitsByLimitId is { Count: > 0 } byLimitId)
        {
            foreach ((string key, CodexRateLimitSnapshotPayload snapshot) in byLimitId.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                AppendWindows(limits, snapshot, snapshot.LimitId ?? key);
            }
        }
        else if (result.RateLimits is not null)
        {
            AppendWindows(limits, result.RateLimits, result.RateLimits.LimitId ?? agentId);
        }

        return limits.Count == 0 ? null : new RateLimitSnapshot(agentId, observedAt, limits);
    }

    private static void AppendWindows(List<RateLimitInfo> limits, CodexRateLimitSnapshotPayload snapshot, string limitId)
    {
        AppendWindow(limits, snapshot.Primary, limitId, "primary");
        AppendWindow(limits, snapshot.Secondary, limitId, "secondary");
    }

    private static void AppendWindow(List<RateLimitInfo> limits, CodexRateLimitWindowPayload? window, string limitId, string slot)
    {
        if (window?.UsedPercent is not double usedPercent || window.ResetsAt is not long resetsAt)
        {
            return;
        }

        limits.Add(new RateLimitInfo
        {
            // window slot ベースの安定識別子。Delta 計算（RateLimitDeltaCalculator）が
            // limit Id でマッチングするため、duration 変更で切れる値を使わない
            Id = $"{limitId}:{slot}",
            Label = BuildLabel(window.WindowDurationMins, slot),
            UsedPercentage = Math.Clamp(usedPercent, 0, 100),
            ResetAt = DateTimeOffset.FromUnixTimeSeconds(resetsAt),
        });
    }

    private static string BuildLabel(long? windowDurationMins, string slot)
    {
        if (windowDurationMins is not long mins || mins <= 0)
        {
            return $"{slot} 枠";
        }

        if (mins % 1440 == 0)
        {
            return $"{mins / 1440}日枠";
        }

        return mins % 60 == 0 ? $"{mins / 60}時間枠" : $"{mins}分枠";
    }

    private async Task<RateLimitSnapshot?> CaptureCoreAsync(string agentId, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_roundTripTimeout);
        CancellationToken token = timeoutCts.Token;

        ProcessStartInfo startInfo = new()
        {
            FileName = _commandFileName,
            Arguments = _commandArguments,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using IProcessInstance process = _processRunner.Start(startInfo);
        try
        {
            await SendRequestAsync(process, _initializeRequestId, "initialize", new
            {
                clientInfo = new { name = "squirrel-notifier", version = "1.0" },
            }).WaitAsync(token).ConfigureAwait(false);
            if (await ReadResponseResultAsync(process, _initializeRequestId, token).ConfigureAwait(false) is null)
            {
                return null;
            }

            await SendRequestAsync(process, _readRequestId, "account/rateLimits/read", parameters: null).WaitAsync(token).ConfigureAwait(false);
            JsonElement? readResult = await ReadResponseResultAsync(process, _readRequestId, token).ConfigureAwait(false);
            if (readResult is null)
            {
                return null;
            }

            CodexRateLimitsReadResult? result = readResult.Value.Deserialize<CodexRateLimitsReadResult>(_jsonOptions);
            return result is null ? null : Normalize(agentId, result, _timeProvider.GetUtcNow());
        }
        finally
        {
            process.Kill(entireProcessTree: true);
        }
    }

    private static async Task SendRequestAsync(IProcessInstance process, int id, string method, object? parameters)
    {
        string payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters,
        });
        await process.StandardInput.WriteLineAsync(payload).ConfigureAwait(false);
        await process.StandardInput.FlushAsync().ConfigureAwait(false);
    }

    // 指定 id の応答が届くまで読み進める。通知・他 id の応答・非 JSON 行は読み飛ばす。
    // EOF（起動失敗・異常終了）と JSON-RPC error は null（取得不可）
    private static async Task<JsonElement?> ReadResponseResultAsync(IProcessInstance process, int id, CancellationToken token)
    {
        while (true)
        {
            string? line = await process.StandardOutput.ReadLineAsync(token).ConfigureAwait(false);
            if (line is null)
            {
                return null;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            using (document)
            {
                if (!document.RootElement.TryGetProperty("id", out JsonElement idElement)
                    || idElement.ValueKind != JsonValueKind.Number
                    || !idElement.TryGetInt32(out int responseId)
                    || responseId != id)
                {
                    continue;
                }

                return document.RootElement.TryGetProperty("result", out JsonElement result)
                    ? result.Clone()
                    : null;
            }
        }
    }
}

/// <summary>Codex App Server の <c>account/rateLimits/read</c> result payload（読み取り専用 DTO）.</summary>
internal sealed class CodexRateLimitsReadResult
{
    [JsonPropertyName("rateLimits")]
    public CodexRateLimitSnapshotPayload? RateLimits { get; set; }

    [JsonPropertyName("rateLimitsByLimitId")]
    public Dictionary<string, CodexRateLimitSnapshotPayload>? RateLimitsByLimitId { get; set; }
}

/// <summary>Codex App Server の RateLimitSnapshot payload（必要フィールドのみ）.</summary>
internal sealed class CodexRateLimitSnapshotPayload
{
    [JsonPropertyName("limitId")]
    public string? LimitId { get; set; }

    [JsonPropertyName("primary")]
    public CodexRateLimitWindowPayload? Primary { get; set; }

    [JsonPropertyName("secondary")]
    public CodexRateLimitWindowPayload? Secondary { get; set; }
}

/// <summary>Codex App Server の RateLimitWindow payload.</summary>
internal sealed class CodexRateLimitWindowPayload
{
    [JsonPropertyName("usedPercent")]
    public double? UsedPercent { get; set; }

    [JsonPropertyName("resetsAt")]
    public long? ResetsAt { get; set; }

    [JsonPropertyName("windowDurationMins")]
    public long? WindowDurationMins { get; set; }
}
