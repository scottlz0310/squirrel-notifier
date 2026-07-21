// <copyright file="ReviewNotificationPolicyTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public sealed class ReviewNotificationPolicyTests
{
    [Theory]
    [InlineData("opened", true)]
    [InlineData("synchronized", true)]
    [InlineData("re-review-requested", true)]
    [InlineData("review-posted", false)]
    [InlineData("", false)]
    public void ShouldOfferReviewerAction_ShouldMatchQueuePolicy(string reason, bool expected)
    {
        bool result = ReviewNotificationPolicy.ShouldOfferReviewerAction(reason);

        result.Should().Be(expected);
    }
}
