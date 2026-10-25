// <copyright file="ProducerEpochStore.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Text.Json;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>
/// 観測の送信主体の世代（<c>producerEpoch</c>）を、プロセスの起動ごとに増やして永続化する.
/// review-raven は、より大きい世代を最初に受けた観測で新しい世代を登録し、旧世代を失効させる。
/// 世代は、前回より必ず大きく、時計が巻き戻っても減らないようにする.
/// </summary>
internal sealed class ProducerEpochStore
{
    /// <summary>永続化するファイル名.</summary>
    internal const string FileName = "observation-producer.json";

    private readonly string _path;
    private readonly TimeProvider _timeProvider;

    public ProducerEpochStore(string directory, TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("世代の保存先ディレクトリが不正です。", nameof(directory));
        }

        _path = Path.Combine(directory, FileName);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>次の世代を決めて保存する。前回の世代 + 1 と、現在の UNIX 秒の大きいほう.</summary>
    /// <returns>この起動の世代（1 以上）.</returns>
    public long AcquireNext()
    {
        long next = Math.Max(ReadLast() + 1, Math.Max(1, _timeProvider.GetUtcNow().ToUnixTimeSeconds()));
        try
        {
            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new EpochFile(next)));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 保存できなくても、この起動の世代は有効。次の起動は、現在の UNIX 秒が前回の世代を超えるため、増加は保たれる
            // （時計が大きく巻き戻らない限り）。保存の失敗を理由に観測の送信を止めない
        }

        return next;
    }

    private long ReadLast()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<EpochFile>(File.ReadAllText(_path))?.LastEpoch ?? 0
                : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // 壊れた・読めない記録は 0 として扱う。現在の UNIX 秒が下限になる
            return 0;
        }
    }

    private sealed record EpochFile(long LastEpoch);
}
