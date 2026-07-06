// <copyright file="RateLimitFileService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>
/// 各 CLI エージェントの statusline フックが書き出すレートリミット状態のローカル
/// JSON ファイル（<c>&lt;settingsDirectory&gt;/ratelimit-status/&lt;agentId&gt;.json</c>）を読み取る.
/// ファイルの中身は既存の <see cref="RateLimitStatusParser"/> が扱うスキーマ
/// （<c>{"limits":[{"id","label","resetAt"}]}</c>）と同一であることを前提とする（#139）.
/// </summary>
internal sealed class RateLimitFileService
{
    private readonly string _directory;

    public RateLimitFileService(string settingsDirectory)
    {
        if (string.IsNullOrWhiteSpace(settingsDirectory))
        {
            throw new ArgumentException("設定保存先のディレクトリが不正です。", nameof(settingsDirectory));
        }

        _directory = Path.Combine(settingsDirectory, "ratelimit-status");
    }

    // agentId に対応するファイルが存在しない場合は null を返す（statusline スクリプト未拡張、
    // またはまだ一度もレートリミット検知イベントが書き出されていないことを示す）.
    public async Task<string?> ReadAgentStatusAsync(string agentId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            throw new ArgumentException("agentId が空です。", nameof(agentId));
        }

        string path = Path.Combine(_directory, $"{agentId}.json");
        if (!File.Exists(path))
        {
            return null;
        }

        return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
    }

    // レートリミット表示に使う SourceUri（ReminderKey の一意化用）を組み立てる.
    public static string BuildSourceIdentifier(string agentId) => $"agent://{agentId}";
}
