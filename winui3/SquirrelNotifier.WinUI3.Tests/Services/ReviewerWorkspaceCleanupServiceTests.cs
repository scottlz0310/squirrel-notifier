// <copyright file="ReviewerWorkspaceCleanupServiceTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Services;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public class ReviewerWorkspaceCleanupServiceTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _reviewerRoot;
    private readonly LoggingService _loggingService;

    public ReviewerWorkspaceCleanupServiceTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"ReviewerWorkspaceCleanupServiceTests_{Guid.NewGuid()}");
        _reviewerRoot = Path.Combine(_tempDirectory, "launcher-workspace", "reviewer");
        Directory.CreateDirectory(_reviewerRoot);
        _loggingService = new LoggingService(Path.Combine(_tempDirectory, "logs"));
    }

    public void Dispose()
    {
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
    public async Task OnPullRequestClosed_ShouldDeleteWorkspaceAndLog()
    {
        string workspace = CreateWorkspace("owner", "repo", 1);

        CreateService().OnPullRequestClosed(this, new PullRequestClosedEventArgs("owner/repo", 1));

        string log = await WaitForLogAsync("完了した PR の reviewer 作業領域を削除しました: owner/repo#1");
        log.Should().NotBeEmpty();
        Directory.Exists(workspace).Should().BeFalse();
    }

    [Fact]
    public async Task OnPullRequestClosed_ShouldLogSkip_WhenReviewerIsRunning()
    {
        CreateWorkspace("owner", "repo", 1);
        var service = new ReviewerWorkspaceCleanupService(_reviewerRoot, (_, _) => true, _loggingService);

        service.OnPullRequestClosed(this, new PullRequestClosedEventArgs("owner/repo", 1));

        (await WaitForLogAsync("reviewer の実行中のため、作業領域を削除しませんでした: owner/repo#1")).Should().NotBeEmpty();
    }

    [Fact]
    public async Task OnPullRequestClosed_ShouldLogFailure_WhenFileIsLocked()
    {
        string workspace = CreateWorkspace("owner", "repo", 1);
        string lockedFile = Path.Combine(workspace, "tmp", "locked.txt");
        File.WriteAllText(lockedFile, "locked");
        using (new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            CreateService().OnPullRequestClosed(this, new PullRequestClosedEventArgs("owner/repo", 1));

            (await WaitForLogAsync("reviewer 作業領域の削除に失敗しました (owner/repo#1)")).Should().NotBeEmpty();
        }

        File.Exists(lockedFile).Should().BeTrue();
    }

    private ReviewerWorkspaceCleanupService CreateService()
        => new(_reviewerRoot, (_, _) => false, _loggingService);

    private string CreateWorkspace(string owner, string repo, int prNumber)
    {
        string workspace = Path.Combine(_reviewerRoot, owner, repo, prNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(Path.Combine(workspace, "tmp"));
        return workspace;
    }

    // OnPullRequestClosed は fire-and-forget のため、ログへの書き込みを待って結果を確認する
    private async Task<string> WaitForLogAsync(string expected)
    {
        string logPath = Path.Combine(_tempDirectory, "logs", "winui3.log");
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(logPath))
            {
                string log = await File.ReadAllTextAsync(logPath);
                if (log.Contains(expected, StringComparison.Ordinal))
                {
                    return log;
                }
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"ログに期待した文言が出力されませんでした: {expected}");
    }
}
