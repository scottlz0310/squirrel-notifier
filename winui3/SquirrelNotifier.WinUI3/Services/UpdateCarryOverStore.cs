// <copyright file="UpdateCarryOverStore.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Text.Json;
using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>
/// 更新の再起動をまたいで、保留中のレビューイベントを引き継ぐ（#569）。
/// 保留はメモリだけにあり、受信済みのイベントは再起動後に配送されないため、保存しないと失われる.
/// </summary>
/// <param name="directory">設定ディレクトリ.</param>
internal sealed class UpdateCarryOverStore(string directory)
{
    private readonly string _path = Path.Combine(directory, "update-carryover.json");

    public void Save(IReadOnlyList<ReviewEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0)
        {
            Delete();
            return;
        }

        Directory.CreateDirectory(directory);
        string temporaryPath = _path + ".tmp";
        File.WriteAllText(
            temporaryPath,
            JsonSerializer.Serialize(events.Select(reviewEvent => new CarriedReviewEvent(reviewEvent, reviewEvent.ReceivedTime))));
        File.Move(temporaryPath, _path, overwrite: true);
    }

    /// <summary>保存したイベントを読み、ファイルを削除する.</summary>
    /// <returns>保存した順のイベント。保存が無い場合は空.</returns>
    public IReadOnlyList<ReviewEvent> Take()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        string content = File.ReadAllText(_path);

        // 読めない内容でも削除する。残すと起動のたびに同じ失敗を繰り返す
        Delete();
        List<CarriedReviewEvent> carried = JsonSerializer.Deserialize<List<CarriedReviewEvent>>(content) ?? [];
        foreach (CarriedReviewEvent item in carried)
        {
            item.Event.ReceivedTime = item.ReceivedTime;
        }

        return [.. carried.Select(item => item.Event)];
    }

    public void Delete() => File.Delete(_path);

    // ReceivedTime は ReviewEvent の JSON に含まれないため、並べて保存する
    private sealed record CarriedReviewEvent(ReviewEvent Event, DateTime ReceivedTime);
}
