// <copyright file="ReviewNotificationPopup.xaml.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Diagnostics.CodeAnalysis;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SquirrelNotifier.WinUI3.Helpers;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3;

[SuppressMessage("Design", "CA1515", Justification = "WinUI XAML から生成するため public が必要です")]
public sealed partial class ReviewNotificationPopup : UserControl
{
    private ReviewEvent? _reviewEvent;

    public ReviewNotificationPopup()
    {
        InitializeComponent();
    }

    internal event EventHandler<ReviewEvent>? OpenPrRequested;

    internal event EventHandler<ReviewEvent>? LaunchReviewRequested;

    internal event EventHandler? OpenAppRequested;

    internal event EventHandler? DismissRequested;

    /// <summary>
    /// 表示するレビューイベントを設定する.
    /// </summary>
    /// <param name="reviewEvent">表示対象のイベント.</param>
    /// <param name="isAutoStarted">
    /// このイベントで reviewer を自動起動したか（#254）。自動起動済みの場合は事後報告に徹し、
    /// 押しても同時実行抑止で弾かれるだけの「レビューする」を出さない.
    /// </param>
    internal void SetReviewEvent(ReviewEvent reviewEvent, bool isAutoStarted = false)
    {
        ArgumentNullException.ThrowIfNull(reviewEvent);
        _reviewEvent = reviewEvent;
        TitleText.Text = ReviewNotificationFormatter.BuildSummary(reviewEvent, isAutoStarted);
        MessageText.Text = reviewEvent.Message;
        OpenPrButton.Visibility = UrlValidator.IsSafeGitHubUrl(
            reviewEvent.PrUrl,
            reviewEvent.Repository,
            reviewEvent.PrNumber)
            ? Visibility.Visible
            : Visibility.Collapsed;
        LaunchReviewButton.Visibility = !isAutoStarted && ReviewNotificationPolicy.ShouldOfferReviewerAction(reviewEvent.Reason)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnOpenPrClick(object sender, RoutedEventArgs e)
    {
        if (_reviewEvent != null)
        {
            OpenPrRequested?.Invoke(this, _reviewEvent);
        }
    }

    private void OnLaunchReviewClick(object sender, RoutedEventArgs e)
    {
        if (_reviewEvent != null)
        {
            LaunchReviewRequested?.Invoke(this, _reviewEvent);
        }
    }

    private void OnOpenAppClick(object sender, RoutedEventArgs e)
    {
        OpenAppRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnDismissClick(object sender, RoutedEventArgs e)
    {
        DismissRequested?.Invoke(this, EventArgs.Empty);
    }
}
