// <copyright file="ReviewEventActionsViewModel.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using Microsoft.UI.Xaml;

namespace SquirrelNotifier.WinUI3.ViewModels;

/// <summary>
/// Recent review events の行アクションのうち、設定で表示を切り替えるものの状態（#256）.
/// </summary>
/// <remarks>
/// DataTemplate の中からは <c>x:Bind</c> でページ側のプロパティへ届かないため、共有リソースとして
/// 置き <c>Binding</c> の <c>Source</c> にする。値の変更は <see cref="INotifyPropertyChanged"/> で
/// 各行へ伝わるため、表示の切り替えに再起動もリストの作り直しも要らない.
/// </remarks>
[SuppressMessage("Design", "CA1515", Justification = "WinUI XAML のリソースとして生成するため public が必要です")]
public sealed class ReviewEventActionsViewModel : INotifyPropertyChanged
{
    private bool _isReviewedActionVisible;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Gets or sets a value indicating whether 「レビューに対応」を行に表示するか。
    /// 既定は非表示で、レビュー指摘への対応は PR を実装した CLI エージェントが同じセッションで行う.
    /// </summary>
    public bool IsReviewedActionVisible
    {
        get => _isReviewedActionVisible;

        set
        {
            if (_isReviewedActionVisible == value)
            {
                return;
            }

            _isReviewedActionVisible = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsReviewedActionVisible)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ReviewedActionVisibility)));
        }
    }

    /// <summary>Gets 「レビューに対応」の表示状態.</summary>
    public Visibility ReviewedActionVisibility
        => _isReviewedActionVisible ? Visibility.Visible : Visibility.Collapsed;
}
