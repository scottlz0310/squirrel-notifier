// <copyright file="AutoStartCoordinator.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using System.Threading.Tasks;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>自動起動の操作結果.</summary>
internal enum AutoStartOperationStatus
{
    /// <summary>操作が完了した.</summary>
    Completed,

    /// <summary>別の操作が進行中のため見送った.</summary>
    SkippedBusy,

    /// <summary>確認ダイアログでキャンセルされた.</summary>
    CancelledByUser,

    /// <summary>操作に失敗した.</summary>
    Failed,
}

/// <summary>自動起動の変更確認に必要な表示内容.</summary>
internal sealed record AutoStartConfirmation(string Title, string Message);

/// <summary>自動起動の操作結果と UI への反映内容.</summary>
internal sealed record AutoStartOperationResult(
    AutoStartOperationStatus Status,
    bool? ToggleIsOn,
    string? ErrorTitle,
    string? ErrorMessage)
{
    /// <summary>Gets a value indicating whether 完了または失敗の後に状態を再取得すべきかを示す.</summary>
    public bool ShouldRefresh => Status is AutoStartOperationStatus.Completed or AutoStartOperationStatus.Failed;
}

/// <summary>自動起動の状態を UI へ反映するための値.</summary>
internal sealed record AutoStartStatusPresentation(
    bool? ToggleIsOn,
    string StatusText,
    bool IsRepairEnabled,
    bool IsOnboardingOpen);

/// <summary>
/// 自動起動の操作の進行状態と、取得結果から UI 状態への変換を担当する（#268）。
/// ContentDialog や ToggleSwitch そのものは持たず、表示・反映は呼び出し側へ残す.
/// </summary>
/// <remarks>UI スレッドからの利用を前提とするためスレッドセーフではない.</remarks>
internal sealed class AutoStartCoordinator
{
    private const string _operationFailureTitle = "自動起動の設定に失敗しました";
    private const string _invalidStatusText = "要修復";
    private const string _statusCheckFailedText = "確認に失敗しました";

    private readonly IAutoStartService _autoStartService;
    private bool _isOperationPending;
    private bool _isStatusRefreshPending;
    private bool _isApplyingUiState;

    public AutoStartCoordinator(IAutoStartService autoStartService)
    {
        ArgumentNullException.ThrowIfNull(autoStartService);
        _autoStartService = autoStartService;
    }

    /// <summary>Gets a value indicating whether toggleSwitch のイベントを無視すべきかを示す.</summary>
    public bool IsToggleSuppressed => _isOperationPending || _isStatusRefreshPending || _isApplyingUiState;

    /// <summary>
    /// 自動起動の有効化または無効化を実行する。確認待ちの間も操作中として扱い、再入を防ぐ.
    /// </summary>
    /// <param name="isEnabled">有効化する場合は <see langword="true"/>.</param>
    /// <param name="confirmAsync">確認ダイアログを表示する呼び出し側の処理.</param>
    /// <returns>操作結果.</returns>
    public async Task<AutoStartOperationResult> ToggleAsync(
        bool isEnabled,
        Func<AutoStartConfirmation, Task<bool>> confirmAsync)
    {
        ArgumentNullException.ThrowIfNull(confirmAsync);

        if (IsToggleSuppressed)
        {
            return new AutoStartOperationResult(AutoStartOperationStatus.SkippedBusy, null, null, null);
        }

        _isOperationPending = true;
        try
        {
            AutoStartConfirmation confirmation = BuildConfirmation(isEnabled);
            if (!await confirmAsync(confirmation))
            {
                return new AutoStartOperationResult(
                    AutoStartOperationStatus.CancelledByUser,
                    !isEnabled,
                    null,
                    null);
            }

            if (isEnabled)
            {
                await _autoStartService.EnableAsync();
            }
            else
            {
                await _autoStartService.DisableAsync();
            }

            return new AutoStartOperationResult(AutoStartOperationStatus.Completed, null, null, null);
        }
        catch (Exception ex)
        {
            return new AutoStartOperationResult(
                AutoStartOperationStatus.Failed,
                null,
                _operationFailureTitle,
                ex.Message);
        }
        finally
        {
            _isOperationPending = false;
        }
    }

    /// <summary>自動起動を登録し直して修復する.</summary>
    /// <returns>操作結果.</returns>
    public async Task<AutoStartOperationResult> RepairAsync()
    {
        if (IsToggleSuppressed)
        {
            return new AutoStartOperationResult(AutoStartOperationStatus.SkippedBusy, null, null, null);
        }

        _isOperationPending = true;
        try
        {
            if (_autoStartService is not IRepairableAutoStartService repairable)
            {
                throw new InvalidOperationException("この配布方式の自動起動は、修復の対象ではありません。");
            }

            await repairable.RepairAsync();
            return new AutoStartOperationResult(AutoStartOperationStatus.Completed, null, null, null);
        }
        catch (Exception ex)
        {
            return new AutoStartOperationResult(
                AutoStartOperationStatus.Failed,
                null,
                "自動起動の修復に失敗しました",
                ex.Message);
        }
        finally
        {
            _isOperationPending = false;
        }
    }

    /// <summary>
    /// 自動起動の状態を取得して UI 反映を行う。反映中は ToggleSwitch のイベントを抑止する.
    /// </summary>
    /// <param name="apply">表示状態を UI へ反映する処理.</param>
    /// <returns>状態の取得と UI 反映が完了する Task.</returns>
    public async Task RefreshStatusAsync(Action<AutoStartStatusPresentation> apply)
    {
        ArgumentNullException.ThrowIfNull(apply);

        if (_isOperationPending || _isStatusRefreshPending)
        {
            return;
        }

        _isStatusRefreshPending = true;
        try
        {
            AutoStartStatus status;
            try
            {
                status = await _autoStartService.GetStatusAsync();
            }
            catch (Exception)
            {
                status = AutoStartStatus.CheckFailed;
            }

            AutoStartStatusPresentation presentation = DescribeStatus(status);
            if (_autoStartService is not IRepairableAutoStartService)
            {
                presentation = presentation with { IsRepairEnabled = false };
            }

            ApplyUiState(() => apply(presentation));
        }
        finally
        {
            _isStatusRefreshPending = false;
        }
    }

    /// <summary>自動起動の状態を表示状態へ変換する.</summary>
    /// <param name="status">取得した状態.</param>
    /// <returns>UI へ反映する値.</returns>
    public static AutoStartStatusPresentation DescribeStatus(AutoStartStatus status)
        => status switch
        {
            AutoStartStatus.Enabled => new(true, "有効", true, false),
            AutoStartStatus.EnabledByPolicy => new(true, "有効（管理者のポリシー）", false, false),
            AutoStartStatus.Invalid => new(false, _invalidStatusText, true, false),
            AutoStartStatus.CheckFailed => new(null, _statusCheckFailedText, false, false),
            AutoStartStatus.Disabled => new(false, "無効", false, true),

            // 利用者やポリシーが無効にした状態では、有効化を促す案内を出さない
            AutoStartStatus.DisabledByUser => new(false, "無効（Windows の設定）", false, false),
            AutoStartStatus.DisabledByPolicy => new(false, "無効（管理者のポリシー）", false, false),
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "未知の自動起動の状態です。"),
        };

    /// <summary>UI 反映中のイベントを抑止しながら処理を実行する.</summary>
    /// <param name="apply">UI へ値を設定する処理.</param>
    public void ApplyUiState(Action apply)
    {
        ArgumentNullException.ThrowIfNull(apply);

        _isApplyingUiState = true;
        try
        {
            apply();
        }
        finally
        {
            _isApplyingUiState = false;
        }
    }

    private AutoStartConfirmation BuildConfirmation(bool isEnabled)
        => isEnabled
            ? new AutoStartConfirmation("自動起動を設定します", _autoStartService.EnableConfirmationMessage)
            : new AutoStartConfirmation("自動起動を解除します", _autoStartService.DisableConfirmationMessage);
}
