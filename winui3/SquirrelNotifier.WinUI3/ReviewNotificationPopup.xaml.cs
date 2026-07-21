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

    internal void SetReviewEvent(ReviewEvent reviewEvent)
    {
        ArgumentNullException.ThrowIfNull(reviewEvent);
        _reviewEvent = reviewEvent;
        TitleText.Text = $"{reviewEvent.Reason}: {reviewEvent.Repository}#{reviewEvent.PrNumber}";
        MessageText.Text = reviewEvent.Message;
        OpenPrButton.Visibility = UrlValidator.IsSafeGitHubUrl(
            reviewEvent.PrUrl,
            reviewEvent.Repository,
            reviewEvent.PrNumber)
            ? Visibility.Visible
            : Visibility.Collapsed;
        LaunchReviewButton.Visibility = ReviewNotificationPolicy.ShouldOfferReviewerAction(reviewEvent.Reason)
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
