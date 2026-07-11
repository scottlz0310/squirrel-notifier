// <copyright file="AgentExecutionViewModel.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using SquirrelNotifier.WinUI3.Helpers;
using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.ViewModels;

/// <summary>
/// ライブログウィンドウの表示ロジック（#144）。<see cref="Services.AgentExecutionSession"/> が配信する
/// 型付きイベントを表示状態へ変換する。DispatcherQueue には依存せず、呼び出し側（Window）が
/// UI スレッド上で <see cref="Apply"/> / <see cref="ApplyBatch"/> を呼ぶ契約とすることで単体テスト可能にする.
/// </summary>
internal sealed class AgentExecutionViewModel : INotifyPropertyChanged
{
    /// <summary>
    /// rolling buffer の行数上限。超過した古い行から破棄し、長時間実行でも UI メモリを無制限に増やさない.
    /// </summary>
    public const int MaxLogLines = 1000;

    private readonly SecretMasker _masker;
    private readonly bool _autoCloseEnabled;

    private bool _isRunning = true;
    private bool _isIndeterminate = true;
    private double _progressValue;
    private string _statusText = "実行中...";
    private string? _verdict;
    private AgentExecutionOutcome? _outcome;

    public AgentExecutionViewModel(string title, bool autoCloseEnabled, SecretMasker masker)
    {
        ArgumentNullException.ThrowIfNull(masker);
        Title = title ?? string.Empty;
        _autoCloseEnabled = autoCloseEnabled;
        _masker = masker;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Gets ウィンドウタイトル（例: "owner/repo#123（レビューする）"）.</summary>
    public string Title { get; }

    /// <summary>Gets 表示用ログ行（サニタイズ・マスキング適用済み、rolling buffer 上限あり）.</summary>
    public ObservableCollection<AgentLogLine> LogLines { get; } = new();

    public bool IsRunning
    {
        get => _isRunning;
        private set => SetField(ref _isRunning, value, nameof(IsRunning));
    }

    /// <summary>
    /// Gets a value indicating whether progress event を未受信の間は true（indeterminate 表示）。構造化イベント非対応の
    /// ランチャーでは実行終了までこのままになる（#143 の contract）.
    /// </summary>
    public bool IsIndeterminate
    {
        get => _isIndeterminate;
        private set => SetField(ref _isIndeterminate, value, nameof(IsIndeterminate));
    }

    /// <summary>Gets 進捗率（0〜100）。現在実行中の phase までを含めた割合.</summary>
    public double ProgressValue
    {
        get => _progressValue;
        private set => SetField(ref _progressValue, value, nameof(ProgressValue));
    }

    /// <summary>Gets 現在の状態表示（phase 表示または terminal 状態の文言）.</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value, nameof(StatusText));
    }

    /// <summary>Gets producer が報告した最新の Verdict（例: "APPROVED"）。未報告なら null.</summary>
    public string? Verdict
    {
        get => _verdict;
        private set => SetField(ref _verdict, value, nameof(Verdict));
    }

    /// <summary>Gets 実行終了時の結果分類。実行中は null.</summary>
    public AgentExecutionOutcome? Outcome => _outcome;

    /// <summary>Gets a value indicating whether 実行が終了したかどうか.</summary>
    public bool IsCompleted => _outcome != null;

    /// <summary>Gets a value indicating whether 成功終了かつ自動クローズが有効な場合のみ true。失敗時は診断のため保持する.</summary>
    public bool ShouldAutoClose => _outcome == AgentExecutionOutcome.Succeeded && _autoCloseEnabled;

    /// <summary>
    /// 実行イベントを 1 件適用する。UI スレッド上で呼ぶこと.
    /// </summary>
    /// <param name="executionEvent">セッションから受信したイベント.</param>
    public void Apply(AgentExecutionEvent executionEvent)
    {
        ArgumentNullException.ThrowIfNull(executionEvent);

        switch (executionEvent.Kind)
        {
            case AgentExecutionEventKind.Stdout:
            case AgentExecutionEventKind.Stderr:
                AppendLogLine(executionEvent.Kind, executionEvent.Text ?? string.Empty, executionEvent.Timestamp);
                break;

            case AgentExecutionEventKind.Progress:
                ApplyProgress(executionEvent);
                break;

            case AgentExecutionEventKind.Completed:
                ApplyCompleted(executionEvent);
                break;
        }
    }

    /// <summary>
    /// 実行イベントをまとめて適用する（UI 更新のバッチ化用）。UI スレッド上で呼ぶこと.
    /// </summary>
    /// <param name="events">セッションから受信したイベント列.</param>
    public void ApplyBatch(IReadOnlyList<AgentExecutionEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        foreach (AgentExecutionEvent executionEvent in events)
        {
            Apply(executionEvent);
        }
    }

    private void ApplyProgress(AgentExecutionEvent executionEvent)
    {
        AgentProgressEvent? progress = executionEvent.Progress;
        if (progress == null)
        {
            return;
        }

        IsIndeterminate = false;

        // phaseIndex は 0 始まり。現在実行中の phase を含めた進捗率として表示する
        int displayPhase = Math.Min(progress.PhaseIndex + 1, progress.TotalPhases);
        ProgressValue = Math.Clamp(displayPhase * 100.0 / progress.TotalPhases, 0, 100);

        string phaseText = string.Format(
            CultureInfo.InvariantCulture, "Phase {0}/{1}: {2}", displayPhase, progress.TotalPhases, progress.PhaseLabel);
        StatusText = phaseText;

        if (!string.IsNullOrWhiteSpace(progress.Verdict))
        {
            Verdict = progress.Verdict;
        }

        string logText = string.IsNullOrWhiteSpace(progress.Message)
            ? phaseText
            : $"{phaseText} — {progress.Message}";
        AppendLogLine(AgentExecutionEventKind.Progress, logText, executionEvent.Timestamp);
    }

    private void ApplyCompleted(AgentExecutionEvent executionEvent)
    {
        _outcome = executionEvent.Outcome;
        IsRunning = false;

        LauncherResult? result = executionEvent.Result;
        StatusText = executionEvent.Outcome switch
        {
            AgentExecutionOutcome.Succeeded => "完了しました",
            AgentExecutionOutcome.Cancelled => "キャンセルされました",
            AgentExecutionOutcome.TimedOut => "タイムアウトしました",
            _ => BuildFailedStatusText(result),
        };

        RaisePropertyChanged(nameof(Outcome));
        RaisePropertyChanged(nameof(IsCompleted));
        RaisePropertyChanged(nameof(ShouldAutoClose));
    }

    private static string BuildFailedStatusText(LauncherResult? result)
    {
        if (result != null && !string.IsNullOrWhiteSpace(result.ErrorMessage))
        {
            return $"失敗しました: {result.ErrorMessage}";
        }

        return result?.ExitCode is int exitCode
            ? string.Format(CultureInfo.InvariantCulture, "失敗しました（終了コード: {0}）", exitCode)
            : "失敗しました";
    }

    private void AppendLogLine(AgentExecutionEventKind kind, string rawText, DateTimeOffset timestamp)
    {
        string text = _masker.Mask(AnsiControlSanitizer.Sanitize(rawText));
        LogLines.Add(new AgentLogLine(kind, text, timestamp));

        while (LogLines.Count > MaxLogLines)
        {
            LogLines.RemoveAt(0);
        }
    }

    private void SetField<T>(ref T field, T value, string propertyName)
    {
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            RaisePropertyChanged(propertyName);
        }
    }

    private void RaisePropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
