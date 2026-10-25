// <copyright file="ResourceBridgeObservationSenderTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Moq;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public sealed class ResourceBridgeObservationSenderTests : IDisposable
{
    private const string _token = "svc-token-0123456789-secret";
    private readonly string _testDirectory = Path.Combine(Path.GetTempPath(), $"ResourceBridgeObservationSenderTests_{Guid.NewGuid():D}");
    private readonly SettingsService _settingsService;
    private ProcessStartInfo? _started;

    public ResourceBridgeObservationSenderTests()
    {
        Directory.CreateDirectory(_testDirectory);
        _settingsService = new SettingsService(_testDirectory, pnpmBinDir: string.Empty);

        // 実環境の .cmd shim を解決すると ArgumentList が使われないため、プロセスモックの判定を壊す
        _settingsService.Settings.SubscriberCommandPath = "squirrel-notifier-test-subscriber";
        _settingsService.Settings.ObservationPublishUrl = "https://gateway.example/mcp/review-raven-internal";
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task SendAsync_ShouldCallThePublishToolWithTheObservationAndPassTheTokenOnlyThroughTheEnvironment()
    {
        ResourceBridgeObservationSender sender = CreateSender(CreateProcess(0, "{\"isError\":false}", string.Empty));

        ObservationSendResult result = await sender.SendAsync(Observation(), CancellationToken.None);

        result.Should().Be(ObservationSendResult.Accepted);
        ProcessStartInfo psi = _started!;
        psi.ArgumentList.Should().StartWith("call");
        psi.ArgumentList.Should().ContainInOrder(
            "--url", "https://gateway.example/mcp/review-raven-internal",
            "--tool", "publish_review_observation",
            "--args");
        psi.ArgumentList.Should().Contain("--json");

        string argsJson = psi.ArgumentList[psi.ArgumentList.IndexOf("--args") + 1];
        JsonNode args = JsonNode.Parse(argsJson)!;
        args["cycleId"]!.GetValue<string>().Should().Be("cyc_01jbq3x8n5r7t9v2w4y6z8a0c1");
        args["producerEpoch"]!.GetValue<long>().Should().Be(3);
        args["sequence"]!.GetValue<long>().Should().Be(9);

        // トークンは子プロセスの環境変数にだけ渡し、引数（プロセス一覧に見える）には載せない
        psi.Environment["MCP_PROBE_AUTH_TOKEN"].Should().Be(_token);
        psi.ArgumentList.Should().NotContain(argument => argument.Contains(_token, StringComparison.Ordinal));
    }

    [Fact]
    public async Task SendAsync_ShouldPassTheFixedSubscriberArgumentsLikeTheOtherCalls()
    {
        _settingsService.Settings.SubscriberArguments = "--skip-resource-list-check";
        ResourceBridgeObservationSender sender = CreateSender(CreateProcess(0, "{}", string.Empty));

        await sender.SendAsync(Observation(), CancellationToken.None);

        _started!.ArgumentList.Should().ContainInOrder("call", "--skip-resource-list-check", "--url");
    }

    [Theory]
    [InlineData("{\"error\":{\"code\":\"PRODUCER_NOT_ASSIGNED\",\"message\":\"x\",\"retryable\":false}}", "PRODUCER_NOT_ASSIGNED", false)]
    [InlineData("{\"error\":{\"code\":\"OBSERVATION_STALE\",\"message\":\"x\",\"retryable\":false}}", "OBSERVATION_STALE", false)]
    [InlineData("{\"error\":{\"code\":\"UNAVAILABLE\",\"message\":\"x\",\"retryable\":true}}", "UNAVAILABLE", true)]
    [InlineData("not a contract error", "OBSERVATION_REJECTED", false)]
    [InlineData("", "OBSERVATION_REJECTED", false)]
    public async Task SendAsync_ShouldTakeTheRetryabilityFromTheContractError_WhenTheToolRejects(string errorText, string expectedCode, bool expectedRetryable)
    {
        string stdout = JsonNode.Parse("{\"isError\":true,\"content\":[]}")!.AsObject().Also(o =>
            o["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = errorText })).ToJsonString();
        ResourceBridgeObservationSender sender = CreateSender(CreateProcess(1, stdout, string.Empty));

        ObservationSendResult result = await sender.SendAsync(Observation(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(expectedCode);
        result.Retryable.Should().Be(expectedRetryable);
    }

    [Fact]
    public async Task SendAsync_ShouldTreatAnAuthenticationFailureAsRetryable_WithoutLeakingTheToken()
    {
        ResourceBridgeObservationSender sender = CreateSender(CreateProcess(2, "{\"errorCode\":\"AUTH_FAILED\"}", $"token {_token} rejected"));

        ObservationSendResult result = await sender.SendAsync(Observation(), CancellationToken.None);

        result.Should().Match<ObservationSendResult>(static r => !r.Success && r.Retryable && r.ErrorCode == "OBSERVATION_AUTH_FAILED");
        result.Message.Should().NotContain(_token);
    }

    [Theory]
    [InlineData("{\"errorCode\":\"TOOL_REQUEST_REJECTED\"}", "TOOL_REQUEST_REJECTED", false)]
    [InlineData("{\"errorCode\":\"PROTOCOL_UNSUPPORTED\"}", "PROTOCOL_UNSUPPORTED", false)]
    [InlineData("{\"errorCode\":\"CONNECTION_FAILED\"}", "OBSERVATION_COMMUNICATION_FAILED", true)]
    [InlineData("", "OBSERVATION_COMMUNICATION_FAILED", true)]
    public async Task SendAsync_ShouldDistinguishACommunicationErrorFromARejectionOfTheCall(string stdout, string expectedCode, bool expectedRetryable)
    {
        ResourceBridgeObservationSender sender = CreateSender(CreateProcess(3, stdout, "stderr"));

        ObservationSendResult result = await sender.SendAsync(Observation(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(expectedCode);
        result.Retryable.Should().Be(expectedRetryable);
    }

    [Fact]
    public async Task SendAsync_ShouldNotStartAProcess_WhenTheTokenIsNotSet()
    {
        ResourceBridgeObservationSender sender = CreateSender(CreateProcess(0, "{}", string.Empty), token: null);

        ObservationSendResult result = await sender.SendAsync(Observation(), CancellationToken.None);

        result.Should().Match<ObservationSendResult>(static r => !r.Success && r.Retryable && r.ErrorCode == "OBSERVATION_TOKEN_NOT_SET");
        result.Message.Should().Contain("SQUIRREL_OBSERVATION_TOKEN");
        _started.Should().BeNull();
    }

    [Fact]
    public async Task SendAsync_ShouldNotStartAProcess_WhenTheUrlIsNotSet()
    {
        _settingsService.Settings.ObservationPublishUrl = string.Empty;
        ResourceBridgeObservationSender sender = CreateSender(CreateProcess(0, "{}", string.Empty));

        ObservationSendResult result = await sender.SendAsync(Observation(), CancellationToken.None);

        result.Should().Match<ObservationSendResult>(static r => !r.Success && r.ErrorCode == "OBSERVATION_URL_NOT_SET");
        _started.Should().BeNull();
    }

    [Fact]
    public async Task SendAsync_ShouldReportATransientFailure_WhenTheCliCannotBeStarted()
    {
        Mock<IProcessRunner> runner = new();
        runner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>())).Throws(new System.ComponentModel.Win32Exception("not found"));
        ResourceBridgeObservationSender sender = new(_settingsService, runner.Object, () => _token);

        ObservationSendResult result = await sender.SendAsync(Observation(), CancellationToken.None);

        result.Should().Match<ObservationSendResult>(static r => !r.Success && r.Retryable && r.ErrorCode == "OBSERVATION_CALL_FAILED");
    }

    [Fact]
    public async Task SendAsync_ShouldPropagateTheCallersCancellation()
    {
        Mock<IProcessInstance> process = CreateProcess(0, "{}", string.Empty, delayMs: 5000);
        ResourceBridgeObservationSender sender = CreateSender(process);
        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(50));

        Func<Task> act = () => sender.SendAsync(Observation(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        process.Verify(p => p.Kill(true), Times.Once);
    }

    private static ReviewObservation Observation()
        => new(1, "cyc_01jbq3x8n5r7t9v2w4y6z8a0c1", "cand_01jbq3x8n5r7t9v2w4y6z8a0d1", new string('a', 40), "squirrel-notifier", 3, 9, "2026-10-25T03:00:00.000Z", "running", null, null, null, false);

    private static Mock<IProcessInstance> CreateProcess(int exitCode, string stdout, string stderr, int delayMs = 0)
    {
        Mock<IProcessInstance> process = new();
        process.SetupGet(p => p.ExitCode).Returns(exitCode);
        process.SetupGet(p => p.StandardOutput).Returns(new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(stdout))));
        process.SetupGet(p => p.StandardError).Returns(new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(stderr))));
        process.Setup(p => p.WaitForExitAsync(It.IsAny<CancellationToken>()))
            .Returns(delayMs > 0
                ? async (CancellationToken t) => await Task.Delay(delayMs, t)
                : (CancellationToken _) => Task.CompletedTask);
        return process;
    }

    private ResourceBridgeObservationSender CreateSender(Mock<IProcessInstance> process, string? token = _token)
    {
        Mock<IProcessRunner> runner = new();
        runner.Setup(r => r.Start(It.IsAny<ProcessStartInfo>()))
            .Callback<ProcessStartInfo>(psi => _started = psi)
            .Returns(process.Object);
        return new ResourceBridgeObservationSender(_settingsService, runner.Object, () => token);
    }
}

internal static class JsonObjectExtensions
{
    internal static JsonObject Also(this JsonObject value, Action<JsonObject> configure)
    {
        configure(value);
        return value;
    }
}
