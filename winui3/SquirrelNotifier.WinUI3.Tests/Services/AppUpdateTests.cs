// <copyright file="AppUpdateTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Net;
using FluentAssertions;
using Moq;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public sealed class AppUpdateTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"AppUpdateTests_{Guid.NewGuid()}");
    private readonly PendingReviewStartQueue _queue = new();
    private readonly List<string> _operations = [];
    private readonly List<UpdateDialogPresentation> _dialogs = [];
    private SubscriptionState _subscriptionState = SubscriptionState.Running;
    private bool _isWindowVisible;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Check_ShouldOpenReleasePageWithoutAppInstaller(bool isPackaged)
    {
        // パッケージ化していない場合（MSI）と、.appinstaller 以外で導入した場合
        Mock<IPackageUpdateAccessor>? accessor = isPackaged ? new Mock<IPackageUpdateAccessor>() : null;
        using AppUpdate update = Create(accessor?.Object, "{\"tag_name\":\"v99.0.0\",\"html_url\":\"https://example.com/r\"}");

        await update.Check.CheckAsync(true, ShowAndCloseAsync);

        _dialogs.Should().ContainSingle().Which.PrimaryButtonText.Should().Be("ダウンロード");
        update.IsApplyPending.Should().BeFalse();
        await update.TryApplyAsync();
    }

    [Theory]
    [InlineData(true, "")]
    [InlineData(false, "--tray")]
    public async Task Check_ShouldApplyAppInstallerUpdateAndCarryHeldReviews(bool isWindowVisible, string restartArguments)
    {
        _isWindowVisible = isWindowVisible;
        _queue.AddOrReplace(new ReviewEvent { EventId = "held", Repository = "owner/repo", PrNumber = 1 });
        var processExit = new TaskCompletionSource();
        Mock<IPackageUpdateAccessor> accessor = CreateAccessor();
        accessor.Setup(a => a.ApplyAsync(It.IsAny<Uri>(), It.IsAny<string>())).Returns(() =>
        {
            _operations.Add("apply");
            return processExit.Task;
        });
        using AppUpdate update = Create(accessor.Object, "<AppInstaller><MainPackage Version=\"2.0.0.0\" /></AppInstaller>");

        Task check = update.Check.CheckAsync(true, dialog =>
        {
            _dialogs.Add(dialog);
            return Task.FromResult(UpdateDialogAction.Accept);
        });
        await WaitUntilAsync(() => _operations.Contains("apply"));

        check.IsCompleted.Should().BeFalse();
        update.IsApplyPending.Should().BeTrue();
        _dialogs.Should().ContainSingle().Which.PrimaryButtonText.Should().Be("更新して再起動");
        _operations.Should().Equal("stop", "apply");
        accessor.Verify(a => a.ApplyAsync(It.IsAny<Uri>(), restartArguments), Times.Once);
        update.TakeCarriedReviews().Should().ContainSingle().Which.EventId.Should().Be("held");
    }

    [Theory]
    [InlineData("Running", true)]
    [InlineData("Starting", true)]
    [InlineData("Error", true)]
    [InlineData("Stopped", false)]
    [InlineData("Stopping", false)]
    public async Task Check_ShouldRestoreSubscriptionWhenApplyFails(string state, bool expectsRestart)
    {
        _subscriptionState = Enum.Parse<SubscriptionState>(state);
        _queue.AddOrReplace(new ReviewEvent { EventId = "held", Repository = "owner/repo", PrNumber = 1 });
        Mock<IPackageUpdateAccessor> accessor = CreateAccessor();
        accessor.Setup(a => a.ApplyAsync(It.IsAny<Uri>(), It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("登録に失敗"));
        using AppUpdate update = Create(accessor.Object, "<AppInstaller><MainPackage Version=\"2.0.0.0\" /></AppInstaller>");

        await update.Check.CheckAsync(true, _ => Task.FromResult(UpdateDialogAction.Accept));

        List<string> expected = expectsRestart ? ["stop", "start", "failure:登録に失敗", "reevaluate"] : ["stop", "failure:登録に失敗", "reevaluate"];
        _operations.Should().Equal(expected);
        update.IsApplyPending.Should().BeFalse();
        update.TakeCarriedReviews().Should().BeEmpty();
    }

    [Fact]
    public void TakeCarriedReviews_ShouldLogUnreadableFile()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "update-carryover.json"), "broken");
        using AppUpdate update = Create(null, string.Empty);

        update.TakeCarriedReviews().Should().BeEmpty();
    }

    [Fact]
    public void Constructor_ShouldCreateOwnClientForAppInstaller()
    {
        var settings = new SettingsService(_directory, pnpmBinDir: string.Empty);
        var logging = new LoggingService(_directory);
        using var gitHub = new AutoUpdateService(logging);

        using var update = new AppUpdate(CreateAccessor().Object, gitHub, settings, logging, Mock.Of<IUrlOpener>(), CreateHost());

        update.IsApplyPending.Should().BeFalse();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static Mock<IPackageUpdateAccessor> CreateAccessor()
    {
        var accessor = new Mock<IPackageUpdateAccessor>();
        accessor.SetupGet(a => a.AppInstallerUri).Returns(new Uri("https://example.com/app.appinstaller"));
        accessor.SetupGet(a => a.CurrentVersion).Returns(new Version(1, 0, 0, 0));
        return accessor;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private AppUpdateHost CreateHost()
        => new(
            () => false,
            _queue,
            new UpdateCarryOverStore(_directory),
            () => _subscriptionState,
            () =>
            {
                _operations.Add("stop");
                return Task.CompletedTask;
            },
            () => _operations.Add("start"),
            () => _isWindowVisible,
            message => _operations.Add($"failure:{message}"),
            () => _operations.Add("reevaluate"));

    private AppUpdate Create(IPackageUpdateAccessor? accessor, string response)
    {
        var logging = new LoggingService(_directory);
        var client = new HttpClient(new StubHandler(response));
        return new AppUpdate(
            accessor,
            new AutoUpdateService(logging, client, new Version(1, 0, 0)),
            new SettingsService(_directory, pnpmBinDir: string.Empty),
            logging,
            Mock.Of<IUrlOpener>(),
            CreateHost(),
            client,
            () => TimeSpan.FromMinutes(10));
    }

    private Task<UpdateDialogAction> ShowAndCloseAsync(UpdateDialogPresentation dialog)
    {
        _dialogs.Add(dialog);
        return Task.FromResult(UpdateDialogAction.Close);
    }

    private sealed class StubHandler(string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) });
    }
}
