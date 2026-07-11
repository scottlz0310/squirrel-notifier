// <copyright file="RateLimitSnapshot.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;

namespace SquirrelNotifier.WinUI3.Models;

/// <summary>
/// ある時点で観測された、特定エージェントのレートリミット使用率スナップショット（#145）。
/// 新スキーマ（<see cref="RateLimitStatusPayload.IsUsageCapable"/> が true）の payload からのみ生成する.
/// </summary>
/// <param name="AgentId">エージェント ID（<see cref="RateLimitAgentDefinition.Id"/>）.</param>
/// <param name="ObservedAt">statusline / hook スクリプト側が記録した観測時刻.</param>
/// <param name="Limits">観測された各 limit（usedPercentage を含む）.</param>
internal sealed record RateLimitSnapshot(string AgentId, DateTimeOffset ObservedAt, IReadOnlyList<RateLimitInfo> Limits);
