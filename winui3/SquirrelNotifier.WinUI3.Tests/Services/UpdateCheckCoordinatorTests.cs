// <copyright file="UpdateCheckCoordinatorTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using Moq;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public sealed class UpdateCheckCoordinatorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"UpdateCheckTests_{Guid.NewGuid()}");
    private readonly SettingsService _settings;
    private readonly LoggingService _logging;
    private readonly Mock<IUrlOpener> _urlOpener = new(MockBehavior.Strict);
    private readonly List<UpdateDialogPresentation> _dialogs = [];

    public UpdateCheckCoordinatorTests()
    {
        _settings = new SettingsService(_directory, pnpmBinDir: string.Empty);
        _logging = new LoggingService(_directory);
    }

    [Theory]
    [InlineData(false, false, null, 0)]
    [InlineData(true, false, null, 1)]
    [InlineData(false, true, null, 1)]
    [InlineData(true, true, null, 1)]
    [InlineData(false, true, "v2.0.0", 0)]
    [InlineData(true, true, "v2.0.0", 1)]
    [InlineData(false, true, "v1.5.0", 1)]
    public async Task CheckAsync_ShouldPresentResultAccordingToTriggerAndSkippedVersion(
        bool isManual, bool hasUpdate, string? skippedVersion, int dialogCount)
    {
        _settings.UpdateLastSkippedVersion(skippedVersion ?? string.Empty);
        UpdateCheckCoordinator coordinator = Create(_ => Task.FromResult(Result(hasUpdate)));

        await coordinator.CheckAsync(isManual, ShowDialogAsync);

        _dialogs.Should().HaveCount(dialogCount);
        if (dialogCount > 0)
        {
            UpdateDialogPresentation dialog = _dialogs.Single();
            dialog.IsUpdateAvailable.Should().Be(hasUpdate);
            dialog.Title.Should().Be(hasUpdate ? "新しいバージョンがあります" : "最新バージョンを利用中です");
            dialog.PrimaryButtonText.Should().Be(hasUpdate ? "ダウンロード" : string.Empty);
            dialog.SecondaryButtonText.Should().Be(hasUpdate ? "このバージョンをスキップ" : string.Empty);
            dialog.CloseButtonText.Should().Be(hasUpdate ? "後で" : "閉じる");
            dialog.Message.Should().Contain(hasUpdate ? "2.0.0" : "新しいバージョンは見つかりませんでした。");
        }

        _settings.Settings.LastSkippedVersion.Should().Be(skippedVersion ?? string.Empty);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckAsync_ShouldNeverPresentFailureAsUpToDate(bool isManual)
    {
        UpdateCheckCoordinator coordinator = Create(_ => Task.FromResult(Result(false) with { ErrorMessage = "HTTP 503" }));

        await coordinator.CheckAsync(isManual, ShowDialogAsync);

        _dialogs.Should().HaveCount(isManual ? 1 : 0);
        if (isManual)
        {
            _dialogs.Single().Title.Should().Be("更新チェックに失敗しました");
            _dialogs.Single().Message.Should().Be("HTTP 503");
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CheckAsync_ShouldReportExceptionsAndReleaseGuard(bool isManual, bool isTimeout)
    {
        int calls = 0;
        UpdateCheckCoordinator coordinator = Create(_ =>
        {
            if (++calls == 1)
            {
                return Task.FromException<AutoUpdateResult>(isTimeout
                    ? new OperationCanceledException()
                    : new HttpRequestException("接続できません"));
            }

            return Task.FromResult(Result(false));
        });

        await coordinator.CheckAsync(isManual, ShowDialogAsync);

        string expected = isTimeout ? "更新チェックがタイムアウトしました。" : "接続できません";
        (await File.ReadAllTextAsync(Path.Combine(_directory, "winui3.log"))).Should().Contain(expected);
        _dialogs.Should().HaveCount(isManual ? 1 : 0);
        if (isManual)
        {
            _dialogs.Single().Message.Should().Be(expected);
        }

        await coordinator.CheckAsync(false, ShowDialogAsync);
        calls.Should().Be(2);
    }

    [Theory]
    [InlineData("v2.0.0", "v2.0.0")]
    [InlineData(null, "2.0.0")]
    public async Task CheckAsync_ShouldPersistSkippedVersion(string? tag, string expected)
    {
        UpdateCheckCoordinator coordinator = Create(_ => Task.FromResult(Result(true) with { Tag = tag }));

        await coordinator.CheckAsync(true, _ => Task.FromResult(UpdateDialogAction.Skip));

        new SettingsService(_directory, pnpmBinDir: string.Empty).Settings.LastSkippedVersion.Should().Be(expected);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CheckAsync_ShouldOpenDownloadAndReportLaunchFailure(bool succeeds)
    {
        _urlOpener.Setup(opener => opener.TryOpen("https://example.com/releases/v2.0.0")).Returns(succeeds);
        UpdateCheckCoordinator coordinator = Create(_ => Task.FromResult(Result(true)));

        await coordinator.CheckAsync(true, dialog =>
        {
            _dialogs.Add(dialog);
            return Task.FromResult(dialog.IsUpdateAvailable ? UpdateDialogAction.Accept : UpdateDialogAction.Close);
        });

        _urlOpener.Verify(opener => opener.TryOpen("https://example.com/releases/v2.0.0"), Times.Once);
        _dialogs.Should().HaveCount(succeeds ? 1 : 2);
        if (!succeeds)
        {
            _dialogs.Last().Message.Should().Be("ダウンロードページを開けませんでした。");
        }
    }

    [Fact]
    public async Task CheckAsync_ShouldSuppressReentryDuringCheckAndDialog()
    {
        var checkCompletion = new TaskCompletionSource<AutoUpdateResult>();
        var dialogCompletion = new TaskCompletionSource<UpdateDialogAction>();
        var dialogShown = new TaskCompletionSource();
        int calls = 0;
        UpdateCheckCoordinator coordinator = Create(_ =>
        {
            calls++;
            return checkCompletion.Task;
        });

        Task first = coordinator.CheckAsync(true, _ =>
        {
            dialogShown.SetResult();
            return dialogCompletion.Task;
        });
        await coordinator.CheckAsync(true, ShowDialogAsync);
        calls.Should().Be(1);
        checkCompletion.SetResult(Result(true));
        await dialogShown.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await coordinator.CheckAsync(false, ShowDialogAsync);
        calls.Should().Be(1);
        _dialogs.Should().BeEmpty();

        dialogCompletion.SetResult(UpdateDialogAction.Close);
        await first;
        await coordinator.CheckAsync(true, ShowDialogAsync);
        calls.Should().Be(2);
        _dialogs.Should().ContainSingle();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CheckAsync_ShouldRequestApplyAndOfferCancelWhileWaiting(bool staysWaiting, bool cancels)
    {
        var applier = new Mock<IUpdateApplier>();
        applier.Setup(a => a.RequestAsync()).Callback(() =>
        {
            applier.SetupGet(a => a.IsWaiting).Returns(staysWaiting);
        }).Returns(Task.CompletedTask);
        UpdateCheckCoordinator coordinator = Create(_ => Task.FromResult(Result(true)), applier.Object);

        await coordinator.CheckAsync(true, dialog =>
        {
            _dialogs.Add(dialog);
            bool accepts = dialog.IsUpdateAvailable || cancels;
            return Task.FromResult(accepts ? UpdateDialogAction.Accept : UpdateDialogAction.Close);
        });

        _dialogs.First().PrimaryButtonText.Should().Be("更新して再起動");
        _dialogs.First().Message.Should().Contain("終了を待って適用します");
        _dialogs.Should().HaveCount(staysWaiting ? 2 : 1);
        if (staysWaiting)
        {
            _dialogs.Last().Title.Should().Be("更新の適用を待っています");
            _dialogs.Last().IsUpdateAvailable.Should().BeFalse("取りやめを既定のボタンにしない");
        }

        applier.Verify(a => a.RequestAsync(), Times.Once);
        applier.Verify(a => a.Cancel(), cancels ? Times.Once() : Times.Never());
        _urlOpener.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(true, true, 1)]
    [InlineData(false, true, 0)]
    [InlineData(true, false, 1)]
    [InlineData(false, false, 0)]
    public async Task CheckAsync_ShouldNotCheckAgainWhileApplyIsPending(bool isManual, bool isWaiting, int dialogCount)
    {
        var applier = new Mock<IUpdateApplier>();
        applier.SetupGet(a => a.IsPending).Returns(true);
        applier.SetupGet(a => a.IsWaiting).Returns(isWaiting);
        int calls = 0;
        UpdateCheckCoordinator coordinator = Create(
            _ =>
            {
                calls++;
                return Task.FromResult(Result(true));
            },
            applier.Object);

        await coordinator.CheckAsync(isManual, ShowDialogAsync);

        calls.Should().Be(0);
        _dialogs.Should().HaveCount(dialogCount);
        applier.Verify(a => a.Cancel(), Times.Never);
    }

    [Fact]
    public async Task CheckAsync_ShouldPropagateErrorDialogFailureAndReleaseGuard()
    {
        UpdateCheckCoordinator coordinator = Create(_ => Task.FromResult(Result(false) with { ErrorMessage = "HTTP 503" }));
        int calls = 0;

        Func<Task> act = () => coordinator.CheckAsync(true, _ =>
        {
            calls++;
            throw new InvalidOperationException("ダイアログ表示に失敗しました");
        });

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("ダイアログ表示に失敗しました");
        calls.Should().Be(1);
        await coordinator.CheckAsync(true, ShowDialogAsync);
        _dialogs.Should().ContainSingle();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task CheckAsync_ShouldShowAutomaticUpdateWhenTagIsAbsent(string? tag)
    {
        UpdateCheckCoordinator coordinator = Create(_ => Task.FromResult(Result(true) with { Tag = tag }));

        await coordinator.CheckAsync(false, ShowDialogAsync);

        _dialogs.Should().ContainSingle();
    }

    [Fact]
    public async Task CheckAsync_ShouldPassTimeoutCancellationToService()
    {
        UpdateCheckCoordinator coordinator = Create(async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Result(false);
        }, TimeSpan.FromMilliseconds(10));

        await coordinator.CheckAsync(true, ShowDialogAsync).WaitAsync(TimeSpan.FromSeconds(5));

        _dialogs.Single().Message.Should().Be("更新チェックがタイムアウトしました。");
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    private static AutoUpdateResult Result(bool hasUpdate) => new(
        new Version(1, 0, 0), new Version(2, 0, 0), hasUpdate, "v2.0.0", "https://example.com/releases/v2.0.0");

    private UpdateCheckCoordinator Create(Func<CancellationToken, Task<AutoUpdateResult>> check, TimeSpan? timeout = null)
        => new(check, _settings, _logging, _urlOpener.Object, timeout);

    private UpdateCheckCoordinator Create(Func<CancellationToken, Task<AutoUpdateResult>> check, IUpdateApplier applier)
        => new(check, _settings, _logging, _urlOpener.Object, applier: applier);

    private Task<UpdateDialogAction> ShowDialogAsync(UpdateDialogPresentation dialog)
    {
        _dialogs.Add(dialog);
        return Task.FromResult(UpdateDialogAction.Close);
    }
}
