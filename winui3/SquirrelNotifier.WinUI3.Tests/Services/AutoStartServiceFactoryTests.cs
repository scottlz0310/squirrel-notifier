// <copyright file="AutoStartServiceFactoryTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Services;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public class AutoStartServiceFactoryTests
{
    [Fact]
    public void Create_ShouldUseTaskSchedulerWithoutPackageIdentity()
    {
        // テストプロセスはパッケージ化していない
        AutoStartServiceFactory.HasPackageIdentity().Should().BeFalse();
        AutoStartServiceFactory.Create().Should().BeOfType<TaskSchedulerService>();
    }

    [Fact]
    public void Create_ShouldUseStartupTaskWithPackageIdentity()
    {
        AutoStartServiceFactory.Create(hasPackageIdentity: true).Should().BeOfType<StartupTaskAutoStartService>();
    }

    [Fact]
    public void TaskSchedulerConfirmationMessages_ShouldDescribeRegisteredTask()
    {
        var service = new TaskSchedulerService();

        service.EnableConfirmationMessage.Should().Contain("Squirrel Notifier").And.Contain("--tray");
        service.DisableConfirmationMessage.Should().Contain("削除");
    }
}
