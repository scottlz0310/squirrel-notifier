// <copyright file="LauncherWorkingDirectoryResolverTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public class LauncherWorkingDirectoryResolverTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly SettingsService _settingsService;
    private readonly LauncherWorkingDirectoryResolver _resolver;

    public LauncherWorkingDirectoryResolverTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"LauncherWorkingDirectoryResolverTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDirectory);
        _settingsService = new SettingsService(_tempDirectory, pnpmBinDir: string.Empty);
        _resolver = new LauncherWorkingDirectoryResolver(_settingsService);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, true);
        }
    }

    [Fact]
    public void Resolve_ShouldCreatePrScopedReviewerDirectoryWithScratch()
    {
        string result = _resolver.Resolve(CreateReviewEvent(), LauncherRole.Reviewer);

        result.Should().Be(Path.Combine(_tempDirectory, "launcher-workspace", "reviewer", "scottlz0310", "squirrel-notifier", "186"));
        Directory.Exists(Path.Combine(result, "tmp")).Should().BeTrue();
    }

    [Fact]
    public void Resolve_ShouldRecordLastUseOnReviewerDirectory()
    {
        // TTL による回収（#403）は、作業領域ディレクトリの更新時刻を最終起動として扱う
        string workspace = Path.Combine(_tempDirectory, "launcher-workspace", "reviewer", "scottlz0310", "squirrel-notifier", "186");
        Directory.CreateDirectory(workspace);
        Directory.SetLastWriteTimeUtc(workspace, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        DateTime before = DateTime.UtcNow.AddSeconds(-1);

        _resolver.Resolve(CreateReviewEvent(), LauncherRole.Reviewer);

        Directory.GetLastWriteTimeUtc(workspace).Should().BeOnOrAfter(before);
    }

    [Fact]
    public void Resolve_ShouldReturnSameReviewerDirectory_ForSamePrRegardlessOfRepositoryCase()
    {
        // session 再開は作業ディレクトリの一致を条件にするため、同じ PR の re-review では同じ場所を返す（#403）
        string first = _resolver.Resolve(CreateReviewEvent(), LauncherRole.Reviewer);
        string second = _resolver.Resolve(
            new ReviewEvent { Repository = "ScottLZ0310/Squirrel-Notifier", PrNumber = 186 },
            LauncherRole.Reviewer);

        second.Should().Be(first);
    }

    [Fact]
    public void Resolve_ShouldReturnMappedGitCheckoutIgnoringRepositoryCase()
    {
        string checkout = CreateGitCheckout();
        _settingsService.UpdateRepositoryCheckoutMappings(new Dictionary<string, string>
        {
            ["SCOTTLZ0310/SQUIRREL-NOTIFIER"] = checkout,
        });

        string result = _resolver.Resolve(CreateReviewEvent(), LauncherRole.Reviewed);

        result.Should().Be(checkout);
    }

    [Fact]
    public void Resolve_ShouldRejectMissingReviewedMapping()
    {
        Action act = () => _resolver.Resolve(CreateReviewEvent(), LauncherRole.Reviewed);

        act.Should().Throw<InvalidOperationException>().WithMessage("*checkout mapping*");
    }

    [Fact]
    public void Resolve_ShouldRejectNonGitDirectory()
    {
        string checkout = Path.Combine(_tempDirectory, "not-git");
        Directory.CreateDirectory(checkout);
        ConfigureMapping(checkout);

        Action act = () => _resolver.Resolve(CreateReviewEvent(), LauncherRole.Reviewed);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Git checkout*");
    }

    [Fact]
    public void Resolve_ShouldRejectProtectedDirectory()
    {
        ConfigureMapping(AppContext.BaseDirectory);

        Action act = () => _resolver.Resolve(CreateReviewEvent(), LauncherRole.Reviewed);

        act.Should().Throw<InvalidOperationException>().WithMessage("*システムまたはインストール先*");
    }

    private string CreateGitCheckout()
    {
        string checkout = Path.Combine(_tempDirectory, "checkout");
        Directory.CreateDirectory(Path.Combine(checkout, ".git"));
        return checkout;
    }

    private void ConfigureMapping(string path)
    {
        _settingsService.UpdateRepositoryCheckoutMappings(new Dictionary<string, string>
        {
            ["scottlz0310/squirrel-notifier"] = path,
        });
    }

    private static ReviewEvent CreateReviewEvent() => new()
    {
        Repository = "scottlz0310/squirrel-notifier",
        PrNumber = 186,
    };
}
