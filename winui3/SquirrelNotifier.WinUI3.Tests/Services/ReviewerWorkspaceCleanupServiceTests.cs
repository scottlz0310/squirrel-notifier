// <copyright file="ReviewerWorkspaceCleanupServiceTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Security.AccessControl;
using System.Security.Principal;
using FluentAssertions;
using SquirrelNotifier.WinUI3.Services;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public class ReviewerWorkspaceCleanupServiceTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _reviewerRoot;
    private readonly LoggingService _loggingService;
    private readonly List<Action> _restoreAccess = new();

    public ReviewerWorkspaceCleanupServiceTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"ReviewerWorkspaceCleanupServiceTests_{Guid.NewGuid()}");
        _reviewerRoot = Path.Combine(_tempDirectory, "launcher-workspace", "reviewer");
        Directory.CreateDirectory(_reviewerRoot);
        _loggingService = new LoggingService(Path.Combine(_tempDirectory, "logs"));
    }

    public void Dispose()
    {
        foreach (Action restore in _restoreAccess)
        {
            restore();
        }

        if (!Directory.Exists(_tempDirectory))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(_tempDirectory, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_tempDirectory, recursive: true);
    }

    [Fact]
    public void Cleanup_ShouldDeleteWorkspaceIncludingReadOnlyFiles()
    {
        string workspace = CreateWorkspace("owner", "repo", 1);
        string packFile = Path.Combine(workspace, "tmp", "clone", ".git", "objects", "pack", "pack-1.pack");
        Directory.CreateDirectory(Path.GetDirectoryName(packFile)!);
        File.WriteAllText(packFile, "pack");
        File.SetAttributes(packFile, FileAttributes.ReadOnly);

        ReviewerWorkspaceCleanupResult result = CreateService().Cleanup("Owner/Repo", 1);

        result.Should().Be(ReviewerWorkspaceCleanupResult.Deleted);
        Directory.Exists(workspace).Should().BeFalse();
    }

    [Fact]
    public void Cleanup_ShouldKeepOtherPullRequestsAndUnmanagedDirectories()
    {
        string target = CreateWorkspace("owner", "repo", 1);
        string otherPr = CreateWorkspace("owner", "repo", 2);
        string otherRepo = CreateWorkspace("owner", "other", 1);
        string legacyFile = Path.Combine(_reviewerRoot, "legacy.txt");
        File.WriteAllText(legacyFile, "shared reviewer directory before #403");
        string outside = Path.Combine(_tempDirectory, "outside");
        Directory.CreateDirectory(outside);

        CreateService().Cleanup("owner/repo", 1);

        Directory.Exists(target).Should().BeFalse();
        Directory.Exists(otherPr).Should().BeTrue();
        Directory.Exists(otherRepo).Should().BeTrue();
        File.Exists(legacyFile).Should().BeTrue();
        Directory.Exists(outside).Should().BeTrue();
    }

    [Fact]
    public void Cleanup_ShouldNotFollowLinkOutsideWorkspace()
    {
        string workspace = CreateWorkspace("owner", "repo", 1);
        string outside = Path.Combine(_tempDirectory, "outside");
        Directory.CreateDirectory(outside);
        string outsideFile = Path.Combine(outside, "keep.txt");
        File.WriteAllText(outsideFile, "keep");
        Directory.CreateSymbolicLink(Path.Combine(workspace, "tmp", "link"), outside);

        CreateService().Cleanup("owner/repo", 1);

        Directory.Exists(workspace).Should().BeFalse();
        File.Exists(outsideFile).Should().BeTrue();
    }

    [Fact]
    public void Cleanup_ShouldDeleteOnlyLink_WhenWorkspaceItselfIsLink()
    {
        string outside = CreateOutsideDirectoryWithFile(out string outsideFile);
        string repoDirectory = Path.Combine(_reviewerRoot, "owner", "repo");
        Directory.CreateDirectory(repoDirectory);
        string workspace = Path.Combine(repoDirectory, "1");
        Directory.CreateSymbolicLink(workspace, outside);

        ReviewerWorkspaceCleanupResult result = CreateService().Cleanup("owner/repo", 1);

        result.Should().Be(ReviewerWorkspaceCleanupResult.Deleted);
        Directory.Exists(workspace).Should().BeFalse();
        File.Exists(outsideFile).Should().BeTrue();
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("repo")]
    public void Cleanup_ShouldRefuse_WhenParentDirectoryIsLink(string linkedLevel)
    {
        // 親がリンクだと、PR 単位のディレクトリの実体が管理外の場所になる
        string outside = CreateOutsideDirectoryWithFile(out string outsideFile);
        Directory.CreateDirectory(Path.Combine(outside, "repo", "1"));
        Directory.CreateDirectory(Path.Combine(outside, "1"));
        if (linkedLevel == "owner")
        {
            Directory.CreateSymbolicLink(Path.Combine(_reviewerRoot, "owner"), outside);
        }
        else
        {
            Directory.CreateDirectory(Path.Combine(_reviewerRoot, "owner"));
            Directory.CreateSymbolicLink(Path.Combine(_reviewerRoot, "owner", "repo"), outside);
        }

        Action act = () => CreateService().Cleanup("owner/repo", 1);

        act.Should().Throw<IOException>().WithMessage("*リンク*");
        File.Exists(outsideFile).Should().BeTrue();
        Directory.Exists(Path.Combine(outside, "repo", "1")).Should().BeTrue();
        Directory.Exists(Path.Combine(outside, "1")).Should().BeTrue();
    }

    [Fact]
    public void Cleanup_ShouldRefuse_WhenReviewerRootIsLink()
    {
        string outside = CreateOutsideDirectoryWithFile(out string outsideFile);
        string outsideWorkspace = Path.Combine(outside, "owner", "repo", "1");
        Directory.CreateDirectory(outsideWorkspace);
        string linkedRoot = Path.Combine(_tempDirectory, "linked-reviewer-root");
        Directory.CreateSymbolicLink(linkedRoot, outside);
        var service = new ReviewerWorkspaceCleanupService(linkedRoot, (_, _) => false, _loggingService);

        Action act = () => service.Cleanup("owner/repo", 1);

        act.Should().Throw<IOException>().WithMessage("*リンク*");
        Directory.Exists(outsideWorkspace).Should().BeTrue();
        File.Exists(outsideFile).Should().BeTrue();
    }

    [Fact]
    public async Task CleanupAndLogAsync_ShouldNotLoseRetry_WhenReviewerFinishesRightAfterRunningCheck()
    {
        // 実行中と判定した直後（保留の処理より前）に reviewer が終了し、終了時の再試行が走る順序を固定する
        string workspace = CreateWorkspace("owner", "repo", 1);
        ReviewerWorkspaceCleanupService? service = null;
        int calls = 0;
        service = new ReviewerWorkspaceCleanupService(
            _reviewerRoot,
            (_, _) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    service!.RetryPendingAsync().GetAwaiter().GetResult();
                    return true;
                }

                return false;
            },
            _loggingService);

        await service.CleanupAndLogAsync("owner/repo", 1);

        Directory.Exists(workspace).Should().BeFalse();
        service.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task RetryPendingAsync_ShouldDeleteWorkspaceSkippedWhileReviewerWasRunning()
    {
        string workspace = CreateWorkspace("owner", "repo", 1);
        bool running = true;
        var service = new ReviewerWorkspaceCleanupService(_reviewerRoot, (_, _) => running, _loggingService);
        await service.CleanupAndLogAsync("owner/repo", 1);
        service.PendingCount.Should().Be(1);
        Directory.Exists(workspace).Should().BeTrue();

        running = false;
        await service.RetryPendingAsync();

        Directory.Exists(workspace).Should().BeFalse();
        service.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task RetryPendingAsync_ShouldDeleteWorkspaceThatFailedEarlier()
    {
        string workspace = CreateWorkspace("owner", "repo", 1);
        string lockedFile = Path.Combine(workspace, "tmp", "locked.txt");
        File.WriteAllText(lockedFile, "locked");
        ReviewerWorkspaceCleanupService service = CreateService();
        using (new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await service.CleanupAndLogAsync("owner/repo", 1);
        }

        service.PendingCount.Should().Be(1);

        await service.RetryPendingAsync();

        Directory.Exists(workspace).Should().BeFalse();
        service.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task OnReviewerRunCompleted_ShouldRetryPendingWorkspace()
    {
        string workspace = CreateWorkspace("owner", "repo", 1);
        bool running = true;
        var service = new ReviewerWorkspaceCleanupService(_reviewerRoot, (_, _) => running, _loggingService);
        await service.CleanupAndLogAsync("owner/repo", 1);

        running = false;
        service.OnReviewerRunCompleted(this, EventArgs.Empty);

        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (Directory.Exists(workspace) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Directory.Exists(workspace).Should().BeFalse();
        (await WaitForLogAsync("完了した PR の reviewer 作業領域を削除しました: owner/repo#1")).Should().BeTrue();
    }

    [Fact]
    public async Task CleanupAndLogAsync_ShouldLogAndNotRetry_WhenRepositoryIsInvalid()
    {
        ReviewerWorkspaceCleanupService service = CreateService();

        await service.CleanupAndLogAsync("owner", 1);

        service.PendingCount.Should().Be(0);
        (await ReadLogAsync()).Should().Contain("reviewer 作業領域の削除対象を特定できません (owner#1)");
    }

    [Fact]
    public async Task SweepExpiredAsync_ShouldDeleteOnlyWorkspacesUnusedForTimeToLive()
    {
        DateTimeOffset now = new(2026, 9, 26, 0, 0, 0, TimeSpan.Zero);
        string expired = CreateWorkspace("owner", "repo", 1, now - TimeSpan.FromDays(7));
        string recent = CreateWorkspace("owner", "repo", 2, now - TimeSpan.FromDays(7) + TimeSpan.FromMinutes(1));
        string otherRepoExpired = CreateWorkspace("other", "repo", 3, now - TimeSpan.FromDays(30));
        string notPrNumber = Path.Combine(_reviewerRoot, "owner", "repo", "tmp");
        Directory.CreateDirectory(notPrNumber);
        Directory.SetLastWriteTimeUtc(notPrNumber, (now - TimeSpan.FromDays(30)).UtcDateTime);
        ReviewerWorkspaceCleanupService service = CreateService(now);

        await service.SweepExpiredAsync();

        Directory.Exists(expired).Should().BeFalse();
        Directory.Exists(otherRepoExpired).Should().BeFalse();
        Directory.Exists(recent).Should().BeTrue();
        Directory.Exists(notPrNumber).Should().BeTrue();
    }

    [Fact]
    public async Task SweepExpiredAsync_ShouldNotQueueRetry_WhenReviewerIsRunning()
    {
        // 実行中なら起動時に更新時刻が新しくなるため、終了後に消すと直前に使った領域を消してしまう
        DateTimeOffset now = new(2026, 9, 26, 0, 0, 0, TimeSpan.Zero);
        string workspace = CreateWorkspace("owner", "repo", 1, now - TimeSpan.FromDays(8));
        bool running = true;
        var service = new ReviewerWorkspaceCleanupService(_reviewerRoot, (_, _) => running, _loggingService, new FixedTimeProvider(now));

        await service.SweepExpiredAsync();
        running = false;
        await service.RetryPendingAsync();

        Directory.Exists(workspace).Should().BeTrue();
        service.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task SweepExpiredAsync_ShouldKeepPendingFromClosedPullRequest_WhenReviewerIsRunning()
    {
        // PR 完了時に見送った保留は、回収で見送っても外さない
        DateTimeOffset now = new(2026, 9, 26, 0, 0, 0, TimeSpan.Zero);
        CreateWorkspace("owner", "repo", 1, now - TimeSpan.FromDays(8));
        var service = new ReviewerWorkspaceCleanupService(_reviewerRoot, (_, _) => true, _loggingService, new FixedTimeProvider(now));
        await service.CleanupAndLogAsync("owner/repo", 1);

        await service.SweepExpiredAsync();

        service.PendingCount.Should().Be(1);
    }

    [Fact]
    public async Task SweepExpiredAsync_ShouldLogFailureWithoutQueueingRetry_WhenFileIsLocked()
    {
        DateTimeOffset now = new(2026, 9, 26, 0, 0, 0, TimeSpan.Zero);
        string workspace = CreateWorkspace("owner", "repo", 1, now - TimeSpan.FromDays(8));
        string lockedFile = Path.Combine(workspace, "tmp", "locked.txt");
        File.WriteAllText(lockedFile, "locked");
        Directory.SetLastWriteTimeUtc(workspace, (now - TimeSpan.FromDays(8)).UtcDateTime);
        ReviewerWorkspaceCleanupService service = CreateService(now);
        using (new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await service.SweepExpiredAsync();
        }

        service.PendingCount.Should().Be(0);
        (await ReadLogAsync()).Should().Contain("reviewer 作業領域の削除に失敗しました。次回の回収で再試行します (owner/repo#1)");
    }

    [Fact]
    public async Task SweepExpiredAsync_ShouldRetryPendingWorkspace()
    {
        DateTimeOffset now = new(2026, 9, 26, 0, 0, 0, TimeSpan.Zero);
        string workspace = CreateWorkspace("owner", "repo", 1, now);
        bool running = true;
        var service = new ReviewerWorkspaceCleanupService(_reviewerRoot, (_, _) => running, _loggingService, new FixedTimeProvider(now));
        await service.CleanupAndLogAsync("owner/repo", 1);

        running = false;
        await service.SweepExpiredAsync();

        Directory.Exists(workspace).Should().BeFalse();
        service.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task SweepExpiredAsync_ShouldDoNothing_WhenRootDoesNotExist()
    {
        var service = new ReviewerWorkspaceCleanupService(
            Path.Combine(_tempDirectory, "missing"),
            (_, _) => false,
            _loggingService);

        Func<Task> act = () => service.SweepExpiredAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Start_ShouldSweepImmediatelyAndStopOnDispose()
    {
        DateTimeOffset now = new(2026, 9, 26, 0, 0, 0, TimeSpan.Zero);
        string workspace = CreateWorkspace("owner", "repo", 1, now - TimeSpan.FromDays(8));
        var service = new ReviewerWorkspaceCleanupService(_reviewerRoot, (_, _) => false, _loggingService, new FixedTimeProvider(now));

        service.Start();
        service.Start();
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (Directory.Exists(workspace) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Directory.Exists(workspace).Should().BeFalse();
        Func<Task> dispose = async () => await service.DisposeAsync();
        await dispose.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Attach_ShouldCleanupOnPullRequestClosedAndDetachOnDispose()
    {
        var settingsService = new SettingsService(_tempDirectory, pnpmBinDir: string.Empty);
        var launcherService = new ReviewLauncherService(settingsService, _loggingService);
        await using var coordinator = new ReviewEventCleanupCoordinator(new MergedStatusClient(), _loggingService);
        string workspace = CreateWorkspace("owner", "repo", 1);
        ReviewerWorkspaceCleanupService service = ReviewerWorkspaceCleanupService.Attach(
            _tempDirectory, launcherService, coordinator, _loggingService);
        coordinator.Track(new SquirrelNotifier.WinUI3.Models.ReviewEvent { EventId = "e1", Repository = "owner/repo", PrNumber = 1, PrUrl = "https://github.com/owner/repo/pull/1" });

        await coordinator.RefreshAsync();
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (Directory.Exists(workspace) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Directory.Exists(workspace).Should().BeFalse();
        (await WaitForLogAsync("完了した PR の reviewer 作業領域を削除しました: owner/repo#1")).Should().BeTrue();

        await service.DisposeAsync();
        string afterDispose = CreateWorkspace("owner", "repo", 2);
        coordinator.Track(new SquirrelNotifier.WinUI3.Models.ReviewEvent { EventId = "e2", Repository = "owner/repo", PrNumber = 2, PrUrl = "https://github.com/owner/repo/pull/2" });
        await coordinator.RefreshAsync();

        Directory.Exists(afterDispose).Should().BeTrue();
    }

    [Fact]
    public async Task DeleteAllAsync_ShouldDeleteAllWorkspacesExceptRunningAndKeepUnmanagedEntries()
    {
        string first = CreateWorkspace("owner", "repo", 1);
        string second = CreateWorkspace("other", "repo", 2);
        string running = CreateWorkspace("owner", "repo", 3);
        string legacyFile = Path.Combine(_reviewerRoot, "legacy.txt");
        File.WriteAllText(legacyFile, "shared reviewer directory before #403");
        string notPrNumber = Path.Combine(_reviewerRoot, "owner", "repo", "tmp");
        Directory.CreateDirectory(notPrNumber);
        var service = new ReviewerWorkspaceCleanupService(_reviewerRoot, (_, prNumber) => prNumber == 3, _loggingService);

        ReviewerWorkspaceBulkCleanupResult result = await service.DeleteAllAsync();

        result.Should().Be(new ReviewerWorkspaceBulkCleanupResult(Deleted: 2, SkippedRunning: 1, Failed: 0));
        Directory.Exists(first).Should().BeFalse();
        Directory.Exists(second).Should().BeFalse();
        Directory.Exists(running).Should().BeTrue();
        File.Exists(legacyFile).Should().BeTrue();
        Directory.Exists(notPrNumber).Should().BeTrue();
        (await ReadLogAsync()).Should().Contain("reviewer 作業領域を手動で削除しました: 削除 2 件、実行中のため残した 1 件、失敗 0 件");
    }

    [Fact]
    public async Task DeleteAllAsync_ShouldCountFailureAndClearPendingOfDeleted()
    {
        string locked = CreateWorkspace("owner", "repo", 1);
        string lockedFile = Path.Combine(locked, "tmp", "locked.txt");
        File.WriteAllText(lockedFile, "locked");
        string pendingWorkspace = CreateWorkspace("owner", "repo", 2);
        bool running = true;
        var service = new ReviewerWorkspaceCleanupService(_reviewerRoot, (_, prNumber) => running && prNumber == 2, _loggingService);
        await service.CleanupAndLogAsync("owner/repo", 2);
        service.PendingCount.Should().Be(1);
        running = false;

        ReviewerWorkspaceBulkCleanupResult result;
        using (new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = await service.DeleteAllAsync();
        }

        result.Should().Be(new ReviewerWorkspaceBulkCleanupResult(Deleted: 1, SkippedRunning: 0, Failed: 1));
        Directory.Exists(pendingWorkspace).Should().BeFalse();
        service.PendingCount.Should().Be(0);
        (await ReadLogAsync()).Should().Contain("reviewer 作業領域の削除に失敗しました (owner/repo#1)");
    }

    [Fact]
    public async Task DeleteAllAsync_ShouldContinueAndCountFailure_WhenDirectoryCannotBeListed()
    {
        // 読めないディレクトリがあっても例外で終わらず（呼び出し元は async void）、読めた分は削除する
        string deletable = CreateWorkspace("owner", "repo", 1);
        string unreadableOwner = Path.Combine(_reviewerRoot, "locked-owner");
        Directory.CreateDirectory(Path.Combine(unreadableOwner, "repo", "2"));
        DenyListDirectory(unreadableOwner);
        ReviewerWorkspaceCleanupService service = CreateService();

        ReviewerWorkspaceBulkCleanupResult result = await service.DeleteAllAsync();

        result.Should().Be(new ReviewerWorkspaceBulkCleanupResult(Deleted: 1, SkippedRunning: 0, Failed: 1));
        Directory.Exists(deletable).Should().BeFalse();
        (await ReadLogAsync()).Should().Contain("reviewer 作業領域の一覧を取得できませんでした:").And.Contain("locked-owner");
    }

    [Fact]
    public async Task SweepExpiredAsync_ShouldContinue_WhenDirectoryCannotBeListed()
    {
        DateTimeOffset now = new(2026, 9, 26, 0, 0, 0, TimeSpan.Zero);
        string expired = CreateWorkspace("owner", "repo", 1, now - TimeSpan.FromDays(8));
        string unreadableOwner = Path.Combine(_reviewerRoot, "locked-owner");
        Directory.CreateDirectory(unreadableOwner);
        DenyListDirectory(unreadableOwner);

        await CreateService(now).SweepExpiredAsync();

        Directory.Exists(expired).Should().BeFalse();
        (await ReadLogAsync()).Should().Contain("reviewer 作業領域の一覧を取得できませんでした:");
    }

    [Fact]
    public async Task DeleteAllAsync_ShouldReturnZero_WhenRootDoesNotExist()
    {
        var service = new ReviewerWorkspaceCleanupService(Path.Combine(_tempDirectory, "missing"), (_, _) => false, _loggingService);

        ReviewerWorkspaceBulkCleanupResult result = await service.DeleteAllAsync();

        result.Should().Be(new ReviewerWorkspaceBulkCleanupResult(0, 0, 0));
    }

    [Theory]
    [InlineData(3, 0, 0, "3 件の reviewer 作業領域を削除しました。")]
    [InlineData(1, 2, 0, "1 件の reviewer 作業領域を削除しました。\n2 件は reviewer の実行中のため残しました。")]
    [InlineData(0, 0, 1, "0 件の reviewer 作業領域を削除しました。\n1 件は削除に失敗しました。詳細はログを確認してください。")]
    [InlineData(2, 1, 1, "2 件の reviewer 作業領域を削除しました。\n1 件は reviewer の実行中のため残しました。\n1 件は削除に失敗しました。詳細はログを確認してください。")]
    public void BulkCleanupResultMessage_ShouldDescribeCounts(int deleted, int skipped, int failed, string expected)
    {
        new ReviewerWorkspaceBulkCleanupResult(deleted, skipped, failed).Message.Should().Be(expected);
    }

    [Fact]
    public void Cleanup_ShouldSkip_WhenReviewerIsRunningForPullRequest()
    {
        string workspace = CreateWorkspace("owner", "repo", 1);
        var service = new ReviewerWorkspaceCleanupService(
            _reviewerRoot,
            (repository, prNumber) => repository == "owner/repo" && prNumber == 1,
            _loggingService);

        ReviewerWorkspaceCleanupResult result = service.Cleanup("owner/repo", 1);

        result.Should().Be(ReviewerWorkspaceCleanupResult.SkippedReviewerRunning);
        Directory.Exists(workspace).Should().BeTrue();
    }

    [Fact]
    public void Cleanup_ShouldReturnNotFound_WhenWorkspaceDoesNotExist()
    {
        CreateService().Cleanup("owner/repo", 1).Should().Be(ReviewerWorkspaceCleanupResult.NotFound);
    }

    [Theory]
    [InlineData("../launcher-workspace")]
    [InlineData("owner/..")]
    [InlineData("owner")]
    public void Cleanup_ShouldRejectRepositoryOutsideManagedLayout(string repository)
    {
        CreateWorkspace("owner", "repo", 1);

        Action act = () => CreateService().Cleanup(repository, 1);

        act.Should().Throw<ArgumentException>();
        Directory.Exists(_reviewerRoot).Should().BeTrue();
        Directory.Exists(Path.Combine(_reviewerRoot, "owner", "repo", "1")).Should().BeTrue();
    }

    [Fact]
    public async Task OnPullRequestClosed_ShouldDeleteWorkspace()
    {
        string workspace = CreateWorkspace("owner", "repo", 1);

        CreateService().OnPullRequestClosed(this, new PullRequestClosedEventArgs("owner/repo", 1));

        // イベントからは fire-and-forget で実行されるため、削除の完了を待つ
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (Directory.Exists(workspace) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Directory.Exists(workspace).Should().BeFalse();

        // 削除後のログ書き込みが Dispose の後片付けと競合しないよう、書き込みの完了も待つ
        (await WaitForLogAsync("完了した PR の reviewer 作業領域を削除しました: owner/repo#1")).Should().BeTrue();
    }

    [Fact]
    public async Task CleanupAndLogAsync_ShouldLogDeletion()
    {
        CreateWorkspace("owner", "repo", 1);

        await CreateService().CleanupAndLogAsync("owner/repo", 1);

        (await ReadLogAsync()).Should().Contain("完了した PR の reviewer 作業領域を削除しました: owner/repo#1");
    }

    [Fact]
    public async Task CleanupAndLogAsync_ShouldLogSkip_WhenReviewerIsRunning()
    {
        CreateWorkspace("owner", "repo", 1);
        var service = new ReviewerWorkspaceCleanupService(_reviewerRoot, (_, _) => true, _loggingService);

        await service.CleanupAndLogAsync("owner/repo", 1);

        (await ReadLogAsync()).Should().Contain("reviewer の実行中のため、作業領域の削除を終了後へ見送りました: owner/repo#1");
    }

    [Fact]
    public async Task CleanupAndLogAsync_ShouldNotLog_WhenWorkspaceDoesNotExist()
    {
        await CreateService().CleanupAndLogAsync("owner/repo", 1);

        File.Exists(LogPath).Should().BeFalse();
    }

    [Fact]
    public async Task CleanupAndLogAsync_ShouldLogFailure_WhenFileIsLocked()
    {
        string workspace = CreateWorkspace("owner", "repo", 1);
        string lockedFile = Path.Combine(workspace, "tmp", "locked.txt");
        File.WriteAllText(lockedFile, "locked");
        using (new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Func<Task> act = () => CreateService().CleanupAndLogAsync("owner/repo", 1);

            await act.Should().NotThrowAsync();
        }

        File.Exists(lockedFile).Should().BeTrue();
        (await ReadLogAsync()).Should().Contain("reviewer 作業領域の削除に失敗しました。reviewer の終了時に再試行します (owner/repo#1)");
    }

    private string CreateOutsideDirectoryWithFile(out string outsideFile)
    {
        string outside = Path.Combine(_tempDirectory, "outside");
        Directory.CreateDirectory(outside);
        outsideFile = Path.Combine(outside, "keep.txt");
        File.WriteAllText(outsideFile, "keep");
        return outside;
    }

    private ReviewerWorkspaceCleanupService CreateService()
        => new(_reviewerRoot, (_, _) => false, _loggingService);

    // 現在のユーザーに対して一覧の取得を拒否し、テスト終了時に拒否を外す
    private void DenyListDirectory(string path)
    {
        var info = new DirectoryInfo(path);
        DirectorySecurity security = info.GetAccessControl();
        var rule = new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!,
            FileSystemRights.ListDirectory,
            AccessControlType.Deny);
        security.AddAccessRule(rule);
        info.SetAccessControl(security);
        _restoreAccess.Add(() =>
        {
            DirectorySecurity current = info.GetAccessControl();
            current.RemoveAccessRule(rule);
            info.SetAccessControl(current);
        });
    }

    private ReviewerWorkspaceCleanupService CreateService(DateTimeOffset now)
        => new(_reviewerRoot, (_, _) => false, _loggingService, new FixedTimeProvider(now));

    private string CreateWorkspace(string owner, string repo, int prNumber)
    {
        string workspace = Path.Combine(_reviewerRoot, owner, repo, prNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(Path.Combine(workspace, "tmp"));
        return workspace;
    }

    private string CreateWorkspace(string owner, string repo, int prNumber, DateTimeOffset lastUsed)
    {
        string workspace = CreateWorkspace(owner, repo, prNumber);
        Directory.SetLastWriteTimeUtc(workspace, lastUsed.UtcDateTime);
        return workspace;
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class MergedStatusClient : IPullRequestStatusClient
    {
        public Task<PullRequestLifecycleState> GetStateAsync(string repository, int prNumber, CancellationToken cancellationToken)
            => Task.FromResult(PullRequestLifecycleState.Merged);
    }

    private string LogPath => Path.Combine(_tempDirectory, "logs", "winui3.log");

    private Task<string> ReadLogAsync() => File.ReadAllTextAsync(LogPath);

    // fire-and-forget のログ書き込みと読み取りが重なると IOException になるため、読めるまで再試行する
    private async Task<bool> WaitForLogAsync(string expected)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (File.Exists(LogPath) && (await ReadLogAsync()).Contains(expected, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            catch (IOException)
            {
            }

            await Task.Delay(50);
        }

        return false;
    }
}
