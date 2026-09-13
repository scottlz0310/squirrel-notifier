// <copyright file="SessionResumeStoreTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public sealed class SessionResumeStoreTests : IDisposable
{
    private static readonly DateTimeOffset _initialTime = new(2026, 9, 13, 0, 0, 0, TimeSpan.Zero);
    private readonly string _sessionsDirectory = Path.Combine(
        Path.GetTempPath(),
        $"SessionResumeStoreTests_{Guid.NewGuid():D}");

    public void Dispose()
    {
        if (Directory.Exists(_sessionsDirectory))
        {
            Directory.Delete(_sessionsDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAsync_ThenTryGetAsync_ShouldRoundtripEntry()
    {
        var timeProvider = new MutableTimeProvider(_initialTime);
        var store = new SessionResumeStore(_sessionsDirectory, timeProvider);
        ReviewEvent reviewEvent = CreateReviewEvent();
        string workingDirectory = CreateWorkingDirectory("checkout");
        Guid sessionId = Guid.NewGuid();

        await store.SaveAsync(reviewEvent, LauncherRole.Reviewer, "claude", workingDirectory, sessionId);
        SessionResumeLookupResult result = await store.TryGetAsync(
            reviewEvent,
            LauncherRole.Reviewer,
            "claude",
            workingDirectory);

        result.Status.Should().Be(SessionResumeLookupStatus.Found);
        result.Entry.Should().NotBeNull();
        result.Entry!.SessionId.Should().Be(sessionId);
        result.Entry.AgentId.Should().Be("claude");
        result.Entry.WorkingDirectory.Should().Be(Path.GetFullPath(workingDirectory));
        result.Entry.CreatedAt.Should().Be(_initialTime);
        result.Entry.LastUsedAt.Should().Be(_initialTime);

        string json = await File.ReadAllTextAsync(GetSessionsPath());
        json.Should().Contain(sessionId.ToString("D"));
    }

    [Theory]
    [InlineData("other/repo", 42, "Reviewer", "claude")]
    [InlineData("owner/repo", 43, "Reviewer", "claude")]
    [InlineData("owner/repo", 42, "Reviewed", "claude")]
    [InlineData("owner/repo", 42, "Reviewer", "copilot")]
    public async Task TryGetAsync_ShouldSeparateCompositeKey(
        string repository,
        int prNumber,
        string roleName,
        string agentId)
    {
        var store = new SessionResumeStore(_sessionsDirectory, new MutableTimeProvider(_initialTime));
        ReviewEvent savedEvent = CreateReviewEvent();
        string workingDirectory = CreateWorkingDirectory("checkout");
        await store.SaveAsync(savedEvent, LauncherRole.Reviewer, "claude", workingDirectory, Guid.NewGuid());

        var lookupEvent = new ReviewEvent { Repository = repository, PrNumber = prNumber };
        LauncherRole role = Enum.Parse<LauncherRole>(roleName);
        SessionResumeLookupResult result = await store.TryGetAsync(
            lookupEvent,
            role,
            agentId,
            workingDirectory);

        result.Status.Should().Be(SessionResumeLookupStatus.NotFound);
        result.Entry.Should().BeNull();
    }

    [Theory]
    [InlineData(-1, "Found")]
    [InlineData(0, "Expired")]
    [InlineData(1, "Expired")]
    public async Task TryGetAsync_ShouldApplyTtlBoundary(int offsetSeconds, string expectedStatus)
    {
        var timeProvider = new MutableTimeProvider(_initialTime);
        var store = new SessionResumeStore(_sessionsDirectory, timeProvider);
        ReviewEvent reviewEvent = CreateReviewEvent();
        string workingDirectory = CreateWorkingDirectory("checkout");
        await store.SaveAsync(reviewEvent, LauncherRole.Reviewer, "claude", workingDirectory, Guid.NewGuid());

        timeProvider.UtcNow = _initialTime + SessionResumeStore.DefaultTtl + TimeSpan.FromSeconds(offsetSeconds);
        SessionResumeLookupResult result = await store.TryGetAsync(
            reviewEvent,
            LauncherRole.Reviewer,
            "claude",
            workingDirectory);

        SessionResumeLookupStatus expected = Enum.Parse<SessionResumeLookupStatus>(expectedStatus);
        result.Status.Should().Be(expected);
        if (expected == SessionResumeLookupStatus.Found)
        {
            result.Entry.Should().NotBeNull();
        }
        else
        {
            result.Entry.Should().BeNull();
        }

        if (expected == SessionResumeLookupStatus.Expired)
        {
            SessionResumeLookupResult secondLookup = await store.TryGetAsync(
                reviewEvent,
                LauncherRole.Reviewer,
                "claude",
                workingDirectory);
            secondLookup.Status.Should().Be(SessionResumeLookupStatus.NotFound);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TryGetAsync_ShouldNormalizeWorkingDirectoryCaseAndTrailingSeparator(bool changeCase)
    {
        var store = new SessionResumeStore(_sessionsDirectory, new MutableTimeProvider(_initialTime));
        ReviewEvent reviewEvent = CreateReviewEvent();
        string workingDirectory = CreateWorkingDirectory("CaseSensitiveName");
        await store.SaveAsync(reviewEvent, LauncherRole.Reviewer, "claude", workingDirectory, Guid.NewGuid());

        string lookupDirectory = changeCase ? workingDirectory.ToUpperInvariant() : workingDirectory;
        lookupDirectory += Path.DirectorySeparatorChar;
        SessionResumeLookupResult result = await store.TryGetAsync(
            reviewEvent,
            LauncherRole.Reviewer,
            "claude",
            lookupDirectory);

        result.Status.Should().Be(SessionResumeLookupStatus.Found);
    }

    [Fact]
    public async Task TryGetAsync_ShouldRemoveEntry_WhenWorkingDirectoryDiffers()
    {
        var store = new SessionResumeStore(_sessionsDirectory, new MutableTimeProvider(_initialTime));
        ReviewEvent reviewEvent = CreateReviewEvent();
        string savedDirectory = CreateWorkingDirectory("first-checkout");
        string currentDirectory = CreateWorkingDirectory("second-checkout");
        await store.SaveAsync(reviewEvent, LauncherRole.Reviewer, "claude", savedDirectory, Guid.NewGuid());

        SessionResumeLookupResult result = await store.TryGetAsync(
            reviewEvent,
            LauncherRole.Reviewer,
            "claude",
            currentDirectory);
        SessionResumeLookupResult secondLookup = await store.TryGetAsync(
            reviewEvent,
            LauncherRole.Reviewer,
            "claude",
            savedDirectory);

        result.Status.Should().Be(SessionResumeLookupStatus.WorkingDirectoryMismatch);
        result.Entry.Should().BeNull();
        secondLookup.Status.Should().Be(SessionResumeLookupStatus.NotFound);
    }

    [Fact]
    public async Task SaveAsync_ShouldKeepNewestOneHundredEntries()
    {
        var timeProvider = new MutableTimeProvider(_initialTime);
        var store = new SessionResumeStore(_sessionsDirectory, timeProvider);
        ReviewEvent reviewEvent = CreateReviewEvent();
        string workingDirectory = CreateWorkingDirectory("checkout");

        for (int index = 0; index <= SessionResumeStore.DefaultMaxEntries; index++)
        {
            await store.SaveAsync(
                reviewEvent,
                LauncherRole.Reviewer,
                $"agent-{index:D3}",
                workingDirectory,
                Guid.NewGuid());
            timeProvider.UtcNow += TimeSpan.FromMinutes(1);
        }

        SessionResumeLookupResult oldest = await store.TryGetAsync(
            reviewEvent,
            LauncherRole.Reviewer,
            "agent-000",
            workingDirectory);
        SessionResumeLookupResult next = await store.TryGetAsync(
            reviewEvent,
            LauncherRole.Reviewer,
            "agent-001",
            workingDirectory);

        oldest.Status.Should().Be(SessionResumeLookupStatus.NotFound);
        next.Status.Should().Be(SessionResumeLookupStatus.Found);
    }

    [Fact]
    public async Task SaveAsync_ShouldUseKeyAsBreak_WhenLastUsedAtMatches()
    {
        var store = new SessionResumeStore(_sessionsDirectory, new MutableTimeProvider(_initialTime));
        ReviewEvent reviewEvent = CreateReviewEvent();
        string workingDirectory = CreateWorkingDirectory("checkout");

        for (int index = SessionResumeStore.DefaultMaxEntries; index >= 0; index--)
        {
            await store.SaveAsync(
                reviewEvent,
                LauncherRole.Reviewer,
                $"agent-{index:D3}",
                workingDirectory,
                Guid.NewGuid());
        }

        SessionResumeLookupResult first = await store.TryGetAsync(
            reviewEvent,
            LauncherRole.Reviewer,
            "agent-000",
            workingDirectory);
        SessionResumeLookupResult second = await store.TryGetAsync(
            reviewEvent,
            LauncherRole.Reviewer,
            "agent-001",
            workingDirectory);

        first.Status.Should().Be(SessionResumeLookupStatus.NotFound);
        second.Status.Should().Be(SessionResumeLookupStatus.Found);
    }

    [Fact]
    public async Task TryGetAsync_ShouldTrimPreexistingEntriesToLimitDuringLoad()
    {
        var timeProvider = new MutableTimeProvider(_initialTime);
        var store = new SessionResumeStore(_sessionsDirectory, timeProvider);
        ReviewEvent reviewEvent = CreateReviewEvent();
        string workingDirectory = CreateWorkingDirectory("checkout");
        for (int index = 0; index < SessionResumeStore.DefaultMaxEntries; index++)
        {
            await store.SaveAsync(
                reviewEvent,
                LauncherRole.Reviewer,
                $"agent-{index:D3}",
                workingDirectory,
                Guid.NewGuid());
            timeProvider.UtcNow += TimeSpan.FromMinutes(1);
        }

        JsonObject root = JsonNode.Parse(await File.ReadAllTextAsync(GetSessionsPath()))!.AsObject();
        JsonObject entries = root["entries"]!.AsObject();
        JsonObject extraEntry = entries["owner/repo#42|Reviewer|agent-099"]!.DeepClone().AsObject();
        extraEntry["agentId"] = "agent-100";
        extraEntry["lastUsedAt"] = timeProvider.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        entries["owner/repo#42|Reviewer|agent-100"] = extraEntry;
        await File.WriteAllTextAsync(GetSessionsPath(), root.ToJsonString());

        SessionResumeLookupResult newest = await store.TryGetAsync(
            reviewEvent,
            LauncherRole.Reviewer,
            "agent-100",
            workingDirectory);
        SessionResumeLookupResult oldest = await store.TryGetAsync(
            reviewEvent,
            LauncherRole.Reviewer,
            "agent-000",
            workingDirectory);

        newest.Status.Should().Be(SessionResumeLookupStatus.Found);
        oldest.Status.Should().Be(SessionResumeLookupStatus.NotFound);
        JsonObject persistedRoot = JsonNode.Parse(await File.ReadAllTextAsync(GetSessionsPath()))!.AsObject();
        persistedRoot["entries"]!.AsObject().Count.Should().Be(SessionResumeStore.DefaultMaxEntries);
    }

    [Fact]
    public async Task SaveAsync_ShouldUpdateTimestampsAccordingToSessionIdentity()
    {
        var timeProvider = new MutableTimeProvider(_initialTime);
        var store = new SessionResumeStore(_sessionsDirectory, timeProvider);
        ReviewEvent reviewEvent = CreateReviewEvent();
        string workingDirectory = CreateWorkingDirectory("checkout");
        Guid firstSessionId = Guid.NewGuid();
        await store.SaveAsync(reviewEvent, LauncherRole.Reviewer, "claude", workingDirectory, firstSessionId);

        timeProvider.UtcNow += TimeSpan.FromDays(1);
        await store.SaveAsync(reviewEvent, LauncherRole.Reviewer, "claude", workingDirectory, firstSessionId);
        SessionResumeEntry updated = (await store.TryGetAsync(
            reviewEvent,
            LauncherRole.Reviewer,
            "claude",
            workingDirectory)).Entry!;

        updated.CreatedAt.Should().Be(_initialTime);
        updated.LastUsedAt.Should().Be(timeProvider.UtcNow);

        timeProvider.UtcNow += TimeSpan.FromDays(1);
        Guid replacementSessionId = Guid.NewGuid();
        await store.SaveAsync(reviewEvent, LauncherRole.Reviewer, "claude", workingDirectory, replacementSessionId);
        SessionResumeEntry replaced = (await store.TryGetAsync(
            reviewEvent,
            LauncherRole.Reviewer,
            "claude",
            workingDirectory)).Entry!;

        replaced.SessionId.Should().Be(replacementSessionId);
        replaced.CreatedAt.Should().Be(timeProvider.UtcNow);
        replaced.LastUsedAt.Should().Be(timeProvider.UtcNow);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RemoveAsync_ShouldLeaveEntryAbsent(bool saveEntryFirst)
    {
        var store = new SessionResumeStore(_sessionsDirectory, new MutableTimeProvider(_initialTime));
        ReviewEvent reviewEvent = CreateReviewEvent();
        string workingDirectory = CreateWorkingDirectory("checkout");
        if (saveEntryFirst)
        {
            await store.SaveAsync(reviewEvent, LauncherRole.Reviewer, "claude", workingDirectory, Guid.NewGuid());
        }

        await store.RemoveAsync(reviewEvent, LauncherRole.Reviewer, "claude");
        SessionResumeLookupResult result = await store.TryGetAsync(
            reviewEvent,
            LauncherRole.Reviewer,
            "claude",
            workingDirectory);

        result.Status.Should().Be(SessionResumeLookupStatus.NotFound);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{ corrupted json %%%")]
    public async Task TryGetAsync_ShouldRecoverAsEmpty_WhenJsonIsInvalid(string json)
    {
        Directory.CreateDirectory(_sessionsDirectory);
        await File.WriteAllTextAsync(GetSessionsPath(), json);
        var store = new SessionResumeStore(_sessionsDirectory, new MutableTimeProvider(_initialTime));

        SessionResumeLookupResult result = await store.TryGetAsync(
            CreateReviewEvent(),
            LauncherRole.Reviewer,
            "claude",
            CreateWorkingDirectory("checkout"));

        result.Status.Should().Be(SessionResumeLookupStatus.NotFound);
    }

    [Fact]
    public async Task TryGetAsync_ShouldIgnoreUnknownJsonFields()
    {
        var store = new SessionResumeStore(_sessionsDirectory, new MutableTimeProvider(_initialTime));
        ReviewEvent reviewEvent = CreateReviewEvent();
        string workingDirectory = CreateWorkingDirectory("checkout");
        await store.SaveAsync(reviewEvent, LauncherRole.Reviewer, "claude", workingDirectory, Guid.NewGuid());
        string json = await File.ReadAllTextAsync(GetSessionsPath());
        json = json.Replace(
            "\"entries\": {",
            "\"futureRootField\": 1,\n  \"entries\": {",
            StringComparison.Ordinal);
        json = json.Replace(
            "\"sessionId\":",
            "\"futureEntryField\": true,\n      \"sessionId\":",
            StringComparison.Ordinal);
        await File.WriteAllTextAsync(GetSessionsPath(), json);

        SessionResumeLookupResult result = await store.TryGetAsync(
            reviewEvent,
            LauncherRole.Reviewer,
            "claude",
            workingDirectory);

        result.Status.Should().Be(SessionResumeLookupStatus.Found);
    }

    [Fact]
    public async Task TryGetAsync_ShouldRemoveInvalidEntry()
    {
        var store = new SessionResumeStore(_sessionsDirectory, new MutableTimeProvider(_initialTime));
        ReviewEvent reviewEvent = CreateReviewEvent();
        string workingDirectory = CreateWorkingDirectory("checkout");
        Guid sessionId = Guid.NewGuid();
        await store.SaveAsync(reviewEvent, LauncherRole.Reviewer, "claude", workingDirectory, sessionId);
        string json = await File.ReadAllTextAsync(GetSessionsPath());
        json = json.Replace(sessionId.ToString("D"), Guid.Empty.ToString("D"), StringComparison.Ordinal);
        await File.WriteAllTextAsync(GetSessionsPath(), json);

        SessionResumeLookupResult result = await store.TryGetAsync(
            reviewEvent,
            LauncherRole.Reviewer,
            "claude",
            workingDirectory);

        result.Status.Should().Be(SessionResumeLookupStatus.NotFound);
        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(GetSessionsPath()));
        document.RootElement.GetProperty("entries").EnumerateObject().Should().BeEmpty();
    }

    [Fact]
    public async Task SaveAsync_ShouldWriteAtomicallyWithoutLeavingTempFile()
    {
        var store = new SessionResumeStore(_sessionsDirectory, new MutableTimeProvider(_initialTime));

        await store.SaveAsync(
            CreateReviewEvent(),
            LauncherRole.Reviewer,
            "claude",
            CreateWorkingDirectory("checkout"),
            Guid.NewGuid());

        File.Exists(GetSessionsPath()).Should().BeTrue();
        File.Exists(GetSessionsPath() + ".tmp").Should().BeFalse();
    }

    [Fact]
    public async Task SaveAsync_ShouldSerializeConcurrentWritesWithoutLosingEntries()
    {
        var store = new SessionResumeStore(_sessionsDirectory, new MutableTimeProvider(_initialTime));
        ReviewEvent reviewEvent = CreateReviewEvent();
        string workingDirectory = CreateWorkingDirectory("checkout");
        Task[] saves = Enumerable.Range(0, 20)
            .Select(index => store.SaveAsync(
                reviewEvent,
                LauncherRole.Reviewer,
                $"agent-{index:D2}",
                workingDirectory,
                Guid.NewGuid()))
            .ToArray();

        await Task.WhenAll(saves);

        for (int index = 0; index < saves.Length; index++)
        {
            SessionResumeLookupResult result = await store.TryGetAsync(
                reviewEvent,
                LauncherRole.Reviewer,
                $"agent-{index:D2}",
                workingDirectory);
            result.Status.Should().Be(SessionResumeLookupStatus.Found);
        }
    }

    [Theory]
    [InlineData("", 42, "claude")]
    [InlineData("owner/repo", 0, "claude")]
    [InlineData("owner/repo", 42, "")]
    public async Task SaveAsync_ShouldRejectInvalidKeyInputs(string repository, int prNumber, string agentId)
    {
        var store = new SessionResumeStore(_sessionsDirectory, new MutableTimeProvider(_initialTime));
        var reviewEvent = new ReviewEvent { Repository = repository, PrNumber = prNumber };

        Func<Task> act = () => store.SaveAsync(
            reviewEvent,
            LauncherRole.Reviewer,
            agentId,
            CreateWorkingDirectory("checkout"),
            Guid.NewGuid());

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task SaveAsync_ShouldRejectRelativeWorkingDirectoryAndEmptySessionId()
    {
        var store = new SessionResumeStore(_sessionsDirectory, new MutableTimeProvider(_initialTime));
        ReviewEvent reviewEvent = CreateReviewEvent();

        Func<Task> relativePath = () => store.SaveAsync(
            reviewEvent,
            LauncherRole.Reviewer,
            "claude",
            "relative-path",
            Guid.NewGuid());
        Func<Task> emptySessionId = () => store.SaveAsync(
            reviewEvent,
            LauncherRole.Reviewer,
            "claude",
            CreateWorkingDirectory("checkout"),
            Guid.Empty);

        await relativePath.Should().ThrowAsync<ArgumentException>();
        await emptySessionId.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public void Constructor_ShouldRejectEmptyDirectory()
    {
        Action act = () => _ = new SessionResumeStore(string.Empty, new MutableTimeProvider(_initialTime));

        act.Should().Throw<ArgumentException>();
    }

    private static ReviewEvent CreateReviewEvent() => new()
    {
        Repository = "owner/repo",
        PrNumber = 42,
    };

    private string CreateWorkingDirectory(string name)
    {
        string path = Path.Combine(_sessionsDirectory, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private string GetSessionsPath() => Path.Combine(_sessionsDirectory, "sessions.json");

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
