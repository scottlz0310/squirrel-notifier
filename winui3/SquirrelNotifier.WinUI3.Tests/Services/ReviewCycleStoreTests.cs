// <copyright file="ReviewCycleStoreTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Text.Json.Nodes;
using FluentAssertions;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public sealed class ReviewCycleStoreTests : IDisposable
{
    private static readonly DateTimeOffset _initialTime = new(2026, 9, 19, 0, 0, 0, TimeSpan.Zero);
    private readonly string _cyclesDirectory = Path.Combine(
        Path.GetTempPath(),
        $"ReviewCycleStoreTests_{Guid.NewGuid():D}");

    public void Dispose()
    {
        if (Directory.Exists(_cyclesDirectory))
        {
            Directory.Delete(_cyclesDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAsync_ThenTryGetAsync_ShouldRoundtripState()
    {
        var timeProvider = new MutableTimeProvider(_initialTime);
        var store = new ReviewCycleStore(_cyclesDirectory, timeProvider);
        ReviewCycleState state = CreateState();

        await store.SaveAsync(state);
        ReviewCycleState? result = await store.TryGetAsync("OWNER/REPO", 42);

        result.Should().NotBeNull();
        result.Should().BeEquivalentTo(state);
        File.Exists(GetCyclesPath()).Should().BeTrue();
        File.Exists(GetCyclesPath() + ".tmp").Should().BeFalse();
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    public async Task TryGetAsync_ShouldApplyTtlBoundary(int offsetSeconds, bool expectedFound)
    {
        var timeProvider = new MutableTimeProvider(_initialTime);
        var store = new ReviewCycleStore(_cyclesDirectory, timeProvider);
        await store.SaveAsync(CreateState());

        timeProvider.UtcNow = _initialTime + ReviewCycleStore.DefaultTtl + TimeSpan.FromSeconds(offsetSeconds);
        ReviewCycleState? result = await store.TryGetAsync("owner/repo", 42);

        (result is not null).Should().Be(expectedFound);
    }

    [Fact]
    public async Task TryGetAsync_ShouldRecoverAsEmpty_WhenJsonIsInvalid()
    {
        Directory.CreateDirectory(_cyclesDirectory);
        await File.WriteAllTextAsync(GetCyclesPath(), "{ corrupted json %%%");
        var store = new ReviewCycleStore(_cyclesDirectory, new MutableTimeProvider(_initialTime));

        ReviewCycleState? result = await store.TryGetAsync("owner/repo", 42);

        result.Should().BeNull();
    }

    [Fact]
    public async Task TryGetAsync_ShouldRemoveInvalidEntry()
    {
        var store = new ReviewCycleStore(_cyclesDirectory, new MutableTimeProvider(_initialTime));
        await store.SaveAsync(CreateState());
        JsonObject root = JsonNode.Parse(await File.ReadAllTextAsync(GetCyclesPath()))!.AsObject();
        root["cycles"]!["OWNER/REPO#42"]!["round"] = 0;
        await File.WriteAllTextAsync(GetCyclesPath(), root.ToJsonString());

        ReviewCycleState? result = await store.TryGetAsync("owner/repo", 42);

        result.Should().BeNull();
        JsonObject persistedRoot = JsonNode.Parse(await File.ReadAllTextAsync(GetCyclesPath()))!.AsObject();
        persistedRoot["cycles"]!.AsObject().Count.Should().Be(0);
    }

    [Fact]
    public async Task SaveAsync_ShouldKeepNewestOneHundredEntries()
    {
        var timeProvider = new MutableTimeProvider(_initialTime);
        var store = new ReviewCycleStore(_cyclesDirectory, timeProvider);

        for (int index = 0; index <= ReviewCycleStore.DefaultMaxEntries; index++)
        {
            await store.SaveAsync(CreateState($"owner/repo-{index:D3}"));
            timeProvider.UtcNow += TimeSpan.FromMinutes(1);
        }

        (await store.TryGetAsync("owner/repo-000", 42)).Should().BeNull();
        (await store.TryGetAsync("owner/repo-001", 42)).Should().NotBeNull();
    }

    [Fact]
    public async Task SaveAsync_ShouldSerializeConcurrentWritesWithoutLosingEntries()
    {
        var store = new ReviewCycleStore(_cyclesDirectory, new MutableTimeProvider(_initialTime));
        Task[] saves = Enumerable.Range(0, 20)
            .Select(index => store.SaveAsync(CreateState($"owner/repo-{index:D2}")))
            .ToArray();

        await Task.WhenAll(saves);

        for (int index = 0; index < saves.Length; index++)
        {
            (await store.TryGetAsync($"owner/repo-{index:D2}", 42)).Should().NotBeNull();
        }
    }

    [Theory]
    [InlineData("", 42)]
    [InlineData("owner/repo", 0)]
    public async Task SaveAsync_ShouldRejectInvalidKeyInputs(string repository, int prNumber)
    {
        var store = new ReviewCycleStore(_cyclesDirectory, new MutableTimeProvider(_initialTime));
        ReviewCycleState state = CreateState(repository, prNumber);

        Func<Task> act = () => store.SaveAsync(state);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public void Constructor_ShouldRejectEmptyDirectory()
    {
        Action act = () => _ = new ReviewCycleStore(string.Empty, new MutableTimeProvider(_initialTime));

        act.Should().Throw<ArgumentException>();
    }

    private ReviewCycleState CreateState(string repository = "owner/repo", int prNumber = 42)
        => new(
            repository,
            prNumber,
            1,
            "event-opened",
            "opened",
            ReviewCycleStatus.AwaitingReviewer,
            null,
            null,
            _initialTime,
            ["event-opened"],
            "claude");

    private string GetCyclesPath() => Path.Combine(_cyclesDirectory, "review-cycles.json");

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
