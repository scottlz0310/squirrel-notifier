// <copyright file="CopilotQuotaRateLimitClient.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Globalization;
using System.Text.Json;
using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>
/// GitHub Copilot の premium request 枠の使用状況を <c>gh api /copilot_internal/user</c> から取得し、
/// 共通スキーマの snapshot へ正規化する。Copilot CLI には statusline も App Server も無いため、
/// GitHub 側の quota を読み取り専用で 1 回だけ照会する。このエンドポイントは非公開で、
/// 仕様が変わりうる。応答の形が想定と違う場合・失敗した場合は「取得不可」として扱い、
/// launcher 起動やレビュー実行を妨げない。認証は <c>gh</c> のログイン状態に従い、トークンは扱わない.
/// </summary>
internal sealed class CopilotQuotaRateLimitClient
{
    internal const string PremiumLimitId = "copilot:premium_interactions";

    private const string _requestPath = "copilot_internal/user";

    // 応答全体（メールアドレス等を含む）を読み込まず、必要な項目だけに絞る
    private const string _jq = "{reset: .quota_reset_date_utc, unlimited: .quota_snapshots.premium_interactions.unlimited, percentRemaining: .quota_snapshots.premium_interactions.percent_remaining}";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IGhApiClient _ghApiClient;
    private readonly TimeProvider _timeProvider;

    public CopilotQuotaRateLimitClient(IGhApiClient? ghApiClient = null, TimeProvider? timeProvider = null)
    {
        _ghApiClient = ghApiClient ?? new GhApiClient();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<RateLimitSnapshot?> CaptureAsync(string agentId, CancellationToken cancellationToken)
    {
        (RateLimitSnapshot? snapshot, _) = await CaptureWithFailureDetailAsync(agentId, cancellationToken).ConfigureAwait(false);
        return snapshot;
    }

    /// <summary>
    /// <see cref="CaptureAsync"/> と同じ取得処理を行い、取得不可時は UI に出せる原因の説明も返す.
    /// </summary>
    /// <param name="agentId">snapshot に記録する agentId.</param>
    /// <param name="cancellationToken">キャンセル用トークン.</param>
    /// <returns>取得できた場合は snapshot と <see langword="null"/>。取得不可の場合は <see langword="null"/> と原因.</returns>
    public async Task<(RateLimitSnapshot? Snapshot, string? FailureDetail)> CaptureWithFailureDetailAsync(string agentId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        GhApiResult response = await _ghApiClient.GetAsync(_requestPath, _jq, paginate: false, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            return (null, response.Error ?? "gh api の実行に失敗しました。");
        }

        return Normalize(agentId, response.Output, _timeProvider.GetUtcNow());
    }

    internal static (RateLimitSnapshot? Snapshot, string? FailureDetail) Normalize(string agentId, string json, DateTimeOffset observedAt)
    {
        CopilotQuotaPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<CopilotQuotaPayload>(json, _jsonOptions);
        }
        catch (JsonException)
        {
            return (null, "応答を解釈できませんでした。");
        }

        if (payload is null)
        {
            return (null, "応答を解釈できませんでした。");
        }

        if (payload.Unlimited == true)
        {
            return (null, "premium request 枠が無制限のプランです。");
        }

        if (payload.PercentRemaining is not double percentRemaining
            || !DateTimeOffset.TryParse(payload.Reset, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset resetAt))
        {
            return (null, "応答に premium request 枠の使用率またはリセット日がありません。");
        }

        RateLimitInfo info = new()
        {
            Id = PremiumLimitId,
            Label = "Premium requests（月間）",
            UsedPercentage = Math.Clamp(100 - percentRemaining, 0, 100),
            ResetAt = resetAt,
        };
        return (new RateLimitSnapshot(agentId, observedAt, [info]), null);
    }

    private sealed class CopilotQuotaPayload
    {
        public string? Reset { get; set; }

        public bool? Unlimited { get; set; }

        public double? PercentRemaining { get; set; }
    }
}
