// <copyright file="ReviewEventActionsViewModelTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.ComponentModel;
using FluentAssertions;
using SquirrelNotifier.WinUI3.ViewModels;

namespace SquirrelNotifier.WinUI3.Tests.ViewModels;

public sealed class ReviewEventActionsViewModelTests
{
    [Fact]
    public void IsReviewedActionVisible_ShouldDefaultToHidden()
    {
        // 既定は非表示。指摘への対応は PR を実装した CLI エージェントが同じセッションで行う（#256）
        new ReviewEventActionsViewModel().IsReviewedActionVisible.Should().BeFalse();
    }

    // 表示状態は各行の Binding が購読するため、変更時に通知されないと再起動なしで反映されない
    [Theory]
    [InlineData(false, true, new[] { "IsReviewedActionVisible", "ReviewedActionVisibility" })]
    [InlineData(true, false, new[] { "IsReviewedActionVisible", "ReviewedActionVisibility" })]
    [InlineData(false, false, new string[0])]
    [InlineData(true, true, new string[0])]
    public void IsReviewedActionVisible_ShouldRaisePropertyChangedOnlyWhenValueChanges(
        bool initial,
        bool next,
        string[] expectedPropertyNames)
    {
        ReviewEventActionsViewModel viewModel = new() { IsReviewedActionVisible = initial };
        List<string?> raised = [];
        PropertyChangedEventHandler handler = (_, e) => raised.Add(e.PropertyName);
        viewModel.PropertyChanged += handler;

        viewModel.IsReviewedActionVisible = next;

        viewModel.PropertyChanged -= handler;
        raised.Should().Equal(expectedPropertyNames);
        viewModel.IsReviewedActionVisible.Should().Be(next);
    }
}
