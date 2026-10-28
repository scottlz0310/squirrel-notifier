// <copyright file="RunnerEscalationReporterTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Moq;
using SquirrelNotifier.WinUI3.Helpers;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public sealed class RunnerEscalationReporterTests : IDisposable
{
    private const string _head = "0123456789abcdef0123456789abcdef01234567";
    private const string _url = "https://localhost:8080/mcp/review-raven";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"RunnerEscalationTests_{Guid.NewGuid():N}");
    private readonly SettingsService _settings;

    public RunnerEscalationReporterTests()
    {
        _settings = new SettingsService(_directory);
        _settings.Settings.RunnerEscalationUrl = _url;
        _settings.Settings.SubscriberCommandPath = "resource-bridge-cli.cmd";
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task ReportAsync_ShouldDeliverJsonThroughRealBatchShim()
    {
        string script = Path.Combine(_directory, "receive.ps1");
        string captured = Path.Combine(_directory, "received.json");
        string shim = Path.Combine(_directory, "bridge.cmd");
        string response = Convert.ToBase64String(Encoding.UTF8.GetBytes(Response()));
        await File.WriteAllTextAsync(script, $"[IO.File]::WriteAllText('{captured.Replace("'", "''", StringComparison.Ordinal)}', [Console]::In.ReadToEnd())\n[Console]::Out.Write([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{response}')))\n");
        await File.WriteAllTextAsync(shim, $"@echo off\r\n\"{SettingsService.ResolveCommandPath("pwsh")}\" -NoProfile -NonInteractive -File \"{script}\"\r\n");
        _settings.Settings.SubscriberCommandPath = shim;

        string result = await new RunnerEscalationReporter(_settings).ReportAsync(Event(), _head, CancellationToken.None);

        result.Should().Contain("投稿しました");
        using JsonDocument payload = JsonDocument.Parse(await File.ReadAllTextAsync(captured));
        payload.RootElement.GetProperty("owner").GetString().Should().Be("owner");
        payload.RootElement.GetProperty("headSha").GetString().Should().Be(_head);
    }

    [Fact]
    public async Task ReportAsync_ShouldPreserveAmbiguousAttemptWhenProcessCannotStart()
    {
        var runner = new Mock<IProcessRunner>();
        runner.Setup(p => p.Start(It.IsAny<ProcessStartInfo>())).Throws(new System.ComponentModel.Win32Exception("secret"));
        var reporter = new RunnerEscalationReporter(_settings, runner.Object);

        (await reporter.ReportAsync(Event(), _head, CancellationToken.None)).Should().Contain("確認できません").And.NotContain("secret");
        (await reporter.ReportAsync(Event(), _head, CancellationToken.None)).Should().Contain("試行済み");
        runner.Verify(p => p.Start(It.IsAny<ProcessStartInfo>()), Times.Once);
    }

    [Fact]
    public async Task ReportAsync_ShouldNotReserveOrStartWhenAlreadyCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var reporter = new RunnerEscalationReporter(_settings, Mock.Of<IProcessRunner>());

        Func<Task> act = () => reporter.ReportAsync(Event(), _head, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        Directory.Exists(Path.Combine(_directory, "runner-escalations")).Should().BeFalse();
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    public async Task ReportAsync_ShouldUseStdinAndNeverRepeatAttempt(int exitCode, bool malformed)
    {
        var process = new Mock<IProcessInstance>();
        var input = new MemoryStream();
        process.SetupGet(p => p.StandardInput).Returns(new StreamWriter(input, new UTF8Encoding(false), leaveOpen: true));
        process.SetupGet(p => p.StandardOutput).Returns(new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(malformed ? "invalid" : Response()))));
        process.SetupGet(p => p.StandardError).Returns(new StreamReader(new MemoryStream()));
        process.SetupGet(p => p.ExitCode).Returns(exitCode);
        process.Setup(p => p.WaitForExitAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var runner = new Mock<IProcessRunner>();
        ProcessStartInfo? launched = null;
        runner.Setup(p => p.Start(It.IsAny<ProcessStartInfo>())).Callback<ProcessStartInfo>(info => launched = info).Returns(process.Object);
        var reporter = new RunnerEscalationReporter(_settings, runner.Object);

        string result = await reporter.ReportAsync(Event(), _head, CancellationToken.None);
        result.Should().Contain(exitCode == 0 && !malformed ? "投稿しました" : "確認できません");
        string duplicate = await new RunnerEscalationReporter(_settings, runner.Object).ReportAsync(Event(), _head, CancellationToken.None);
        duplicate.Should().Contain("試行済み");
        runner.Verify(p => p.Start(It.IsAny<ProcessStartInfo>()), Times.Once);
        process.Verify(p => p.Kill(true), Times.Never);
        launched.Should().NotBeNull();
        launched!.Environment[ExternalProcessStartInfoFactory.ArgumentEnvironmentVariablePrefix + "5"].Should().Be("--args-stdin");
        launched.Arguments.Should().NotContain(_head);
        string json = Encoding.UTF8.GetString(input.ToArray());
        using JsonDocument payload = JsonDocument.Parse(json);
        payload.RootElement.GetProperty("headSha").GetString().Should().Be(_head);
        payload.RootElement.GetProperty("reason").GetString().Should().Be("resume_unresponsive");
        launched.StandardInputEncoding!.GetPreamble().Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://example.com/mcp/review-raven")]
    [InlineData("https://user:secret@example.com/mcp")]
    [InlineData("https://example.com/mcp?token=secret")]
    [InlineData("https://example.com/mcp#token")]
    public async Task ReportAsync_ShouldRejectUnsafeOrMissingUrlWithoutReserving(string url)
    {
        _settings.Settings.RunnerEscalationUrl = url;
        var runner = new Mock<IProcessRunner>(MockBehavior.Strict);
        string result = await new RunnerEscalationReporter(_settings, runner.Object).ReportAsync(Event(), _head, CancellationToken.None);
        result.Should().Contain("未投稿").And.NotContain("secret");
        Directory.Exists(Path.Combine(_directory, "runner-escalations")).Should().BeFalse();
    }

    [Fact]
    public async Task ReportAsync_ShouldKeepMarkerOnCancellationAndKillProcess()
    {
        using var cts = new CancellationTokenSource();
        var process = new Mock<IProcessInstance>();
        process.SetupGet(p => p.StandardInput).Returns(new StreamWriter(new MemoryStream()));
        process.SetupGet(p => p.StandardOutput).Returns(new StreamReader(new MemoryStream()));
        process.SetupGet(p => p.StandardError).Returns(new StreamReader(new MemoryStream()));
        process.Setup(p => p.WaitForExitAsync(It.IsAny<CancellationToken>())).Returns((CancellationToken token) =>
        {
            cts.Cancel();
            return Task.FromCanceled(token);
        });
        var runner = new Mock<IProcessRunner>();
        runner.Setup(p => p.Start(It.IsAny<ProcessStartInfo>())).Returns(process.Object);
        var reporter = new RunnerEscalationReporter(_settings, runner.Object);

        Func<Task> act = () => reporter.ReportAsync(Event(), _head, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
        process.Verify(p => p.Kill(true), Times.Once);
        (await reporter.ReportAsync(Event(), _head, CancellationToken.None)).Should().Contain("試行済み");
    }

    [Fact]
    public async Task ReserveAttempt_ShouldBeAtomicAcrossConcurrentCallersAndSeparateHeads()
    {
        var reference = new PrReference("owner", "repo", 12);
        bool[] attempts = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() => RunnerEscalationReporter.ReserveAttempt(_directory, reference, _head))));
        attempts.Count(success => success).Should().Be(1);
        RunnerEscalationReporter.ReserveAttempt(_directory, new PrReference("OWNER", "REPO", 12), _head).Should().BeFalse();
        RunnerEscalationReporter.ReserveAttempt(_directory, reference, new string('a', 40)).Should().BeTrue();
    }

    [Theory]
    [InlineData("wrong-tool", "https://localhost:8080/mcp/review-raven", "https://github.com/owner/repo/pull/12#issuecomment-123")]
    [InlineData("post_runner_escalation", "https://example.com/mcp", "https://github.com/owner/repo/pull/12#issuecomment-123")]
    [InlineData("post_runner_escalation", "https://localhost:8080/mcp/review-raven", "https://github.com/owner/repo/pull/13#issuecomment-123")]
    public void ReadResult_ShouldRejectMismatchedResponses(string tool, string serverUrl, string link)
    {
        string response = JsonSerializer.Serialize(new { serverUrl, tool, isError = false, content = new[] { new { type = "text", text = JsonSerializer.Serialize(new { commentId = "123", url = link }) } } });
        RunnerEscalationReporter.ReadResult(0, response, _url, new PrReference("owner", "repo", 12)).Should().Contain("確認できません");
    }

    private static ReviewEvent Event() => new() { Repository = "owner/repo", PrNumber = 12 };

    private static string Response() => JsonSerializer.Serialize(new
    {
        serverUrl = _url,
        tool = "post_runner_escalation",
        isError = false,
        content = new[] { new { type = "text", text = "{\"commentId\":\"123\",\"url\":\"https://github.com/owner/repo/pull/12#issuecomment-123\"}" } },
    });
}
