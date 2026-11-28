// <copyright file="StartupTaskAutoStartService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using Windows.ApplicationModel;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>
/// MSIX パッケージ版の自動起動。マニフェストで宣言したスタートアップタスクを操作する（#569）.
/// </summary>
internal sealed class StartupTaskAutoStartService : IAutoStartService
{
    /// <summary>マニフェストの desktop:StartupTask の TaskId.</summary>
    internal const string TaskId = "SquirrelNotifierStartup";

    private const string _disabledByUserMessage =
        "Windows の「設定 > アプリ > スタートアップ」で無効にされています。有効にするには、同じ画面で Squirrel Notifier をオンにしてください。";

    private const string _disabledByPolicyMessage = "管理者のポリシーにより、自動起動を有効にできません。";
    private const string _enabledByPolicyMessage = "管理者のポリシーにより有効になっているため、無効にできません。";

    private readonly IStartupTaskAccessor _accessor;

    public StartupTaskAutoStartService(IStartupTaskAccessor accessor)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        _accessor = accessor;
    }

    public string EnableConfirmationMessage
        => "Windows のスタートアップに登録します。ログオン時にトレイへ常駐します。\n\n　Windows の「設定 > アプリ > スタートアップ」でも切り替えられます。";

    public string DisableConfirmationMessage => "自動起動を無効にします。よろしいですか？";

    public async Task<AutoStartStatus> GetStatusAsync()
    {
        StartupTaskState state = await _accessor.GetStateAsync().ConfigureAwait(false);
        return state switch
        {
            StartupTaskState.Enabled => AutoStartStatus.Enabled,
            StartupTaskState.EnabledByPolicy => AutoStartStatus.EnabledByPolicy,
            StartupTaskState.Disabled => AutoStartStatus.Disabled,
            StartupTaskState.DisabledByUser => AutoStartStatus.DisabledByUser,
            StartupTaskState.DisabledByPolicy => AutoStartStatus.DisabledByPolicy,
            _ => AutoStartStatus.CheckFailed,
        };
    }

    public async Task EnableAsync()
    {
        StartupTaskState state = await _accessor.RequestEnableAsync().ConfigureAwait(false);
        if (state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy)
        {
            return;
        }

        throw new InvalidOperationException(state switch
        {
            StartupTaskState.DisabledByUser => _disabledByUserMessage,
            StartupTaskState.DisabledByPolicy => _disabledByPolicyMessage,
            _ => $"自動起動を有効にできませんでした（状態: {state}）。",
        });
    }

    public async Task DisableAsync()
    {
        await _accessor.DisableAsync().ConfigureAwait(false);

        // Disable は戻り値を持たないため、反映されたかを状態で確認する
        StartupTaskState state = await _accessor.GetStateAsync().ConfigureAwait(false);
        if (state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy)
        {
            throw new InvalidOperationException(state == StartupTaskState.EnabledByPolicy
                ? _enabledByPolicyMessage
                : $"自動起動を無効にできませんでした（状態: {state}）。");
        }
    }
}
