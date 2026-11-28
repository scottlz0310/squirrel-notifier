// <copyright file="StartupTaskAutoStartServiceTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using Moq;
using SquirrelNotifier.WinUI3.Services;
using Windows.ApplicationModel;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public class StartupTaskAutoStartServiceTests
{
    private readonly Mock<IStartupTaskAccessor> _accessor = new();

    [Theory]
    [InlineData((int)StartupTaskState.Enabled, nameof(AutoStartStatus.Enabled))]
    [InlineData((int)StartupTaskState.EnabledByPolicy, nameof(AutoStartStatus.EnabledByPolicy))]
    [InlineData((int)StartupTaskState.Disabled, nameof(AutoStartStatus.Disabled))]
    [InlineData((int)StartupTaskState.DisabledByUser, nameof(AutoStartStatus.DisabledByUser))]
    [InlineData((int)StartupTaskState.DisabledByPolicy, nameof(AutoStartStatus.DisabledByPolicy))]
    [InlineData(999, nameof(AutoStartStatus.CheckFailed))]
    public async Task GetStatusAsync_ShouldMapStartupTaskState(int state, string expected)
    {
        _accessor.Setup(accessor => accessor.GetStateAsync()).ReturnsAsync((StartupTaskState)state);

        AutoStartStatus status = await CreateService().GetStatusAsync();

        status.Should().Be(Enum.Parse<AutoStartStatus>(expected));
    }

    [Theory]
    [InlineData((int)StartupTaskState.Enabled)]
    [InlineData((int)StartupTaskState.EnabledByPolicy)]
    public async Task EnableAsync_ShouldSucceedWhenTaskBecomesEnabled(int state)
    {
        _accessor.Setup(accessor => accessor.RequestEnableAsync()).ReturnsAsync((StartupTaskState)state);

        Func<Task> act = () => CreateService().EnableAsync();

        await act.Should().NotThrowAsync();
    }

    [Theory]
    [InlineData((int)StartupTaskState.DisabledByUser, "*設定 > アプリ > スタートアップ*")]
    [InlineData((int)StartupTaskState.DisabledByPolicy, "*管理者のポリシー*")]
    [InlineData((int)StartupTaskState.Disabled, "*有効にできませんでした*")]
    public async Task EnableAsync_ShouldExplainWhyTaskStaysDisabled(int state, string expectedMessage)
    {
        _accessor.Setup(accessor => accessor.RequestEnableAsync()).ReturnsAsync((StartupTaskState)state);

        Func<Task> act = () => CreateService().EnableAsync();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage(expectedMessage);
    }

    [Theory]
    [InlineData((int)StartupTaskState.Disabled)]
    [InlineData((int)StartupTaskState.DisabledByUser)]
    public async Task DisableAsync_ShouldDisableTaskAndConfirmState(int state)
    {
        _accessor.Setup(accessor => accessor.GetStateAsync()).ReturnsAsync((StartupTaskState)state);

        await CreateService().DisableAsync();

        _accessor.Verify(accessor => accessor.DisableAsync(), Times.Once);
    }

    [Theory]
    [InlineData((int)StartupTaskState.EnabledByPolicy, "*管理者のポリシー*")]
    [InlineData((int)StartupTaskState.Enabled, "*無効にできませんでした*")]
    public async Task DisableAsync_ShouldFailWhenTaskStaysEnabled(int state, string expectedMessage)
    {
        _accessor.Setup(accessor => accessor.GetStateAsync()).ReturnsAsync((StartupTaskState)state);

        Func<Task> act = () => CreateService().DisableAsync();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage(expectedMessage);
    }

    [Fact]
    public void ConfirmationMessages_ShouldNotMentionTaskScheduler()
    {
        StartupTaskAutoStartService service = CreateService();

        service.EnableConfirmationMessage.Should().Contain("トレイ").And.NotContain("タスクスケジューラ");
        service.DisableConfirmationMessage.Should().NotContain("タスク");
    }

    private StartupTaskAutoStartService CreateService() => new(_accessor.Object);
}
