// <copyright file="DeferredDialogCloseGate.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SquirrelNotifier.WinUI3.Helpers;

/// <summary>
/// <c>ContentDialog.Hide()</c> は <c>ShowAsync()</c> の開くアニメーションが完了する前に呼ぶと
/// 無視され、ダイアログが閉じられないまま残る（#200）。処理完了が表示完了より先になりうる
/// 非同期ダイアログのために、開き終わるまでクローズ要求を保留する.
/// </summary>
/// <remarks>
/// <see cref="MarkOpened"/> は <c>ContentDialog.Opened</c> から、<see cref="RequestClose"/> は
/// <c>DispatcherQueue</c> 経由でいずれも UI スレッドから呼ばれる前提のため、同期化は行わない.
/// </remarks>
internal sealed class DeferredDialogCloseGate
{
    private bool _opened;
    private bool _closeRequested;

    /// <summary>
    /// ダイアログが開き終わったことを記録する.
    /// </summary>
    /// <returns>保留中のクローズ要求があり、ここで閉じるべきなら <see langword="true"/>.</returns>
    public bool MarkOpened()
    {
        _opened = true;
        return _closeRequested;
    }

    /// <summary>
    /// クローズを要求する.
    /// </summary>
    /// <returns>既に開き終わっており、ここで閉じてよいなら <see langword="true"/>。
    /// まだ開いていない場合は保留され、<see cref="MarkOpened"/> 側で閉じる.</returns>
    public bool RequestClose()
    {
        _closeRequested = true;
        return _opened;
    }
}
