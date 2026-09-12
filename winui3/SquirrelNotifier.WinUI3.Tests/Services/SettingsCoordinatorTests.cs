// <copyright file="SettingsCoordinatorTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using FluentAssertions;
using Moq;
using SquirrelNotifier.WinUI3.Helpers;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public sealed class SettingsCoordinatorTests : IDisposable
{
    private readonly string _settingsDirectory = Path.Combine(
        Path.GetTempPath(),
        $"SettingsCoordinatorTests_{Guid.NewGuid()}");
    private readonly SettingsService _settingsService;

    public SettingsCoordinatorTests()
    {
        _settingsService = new SettingsService(_settingsDirectory, pnpmBinDir: string.Empty);
    }

    [Fact]
    public void Save_ShouldPersistNormalizedSettingsAndMappings()
    {
        string checkoutPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "squirrel-notifier-checkout"));
        SettingsCoordinator coordinator = new(_settingsService);

        SettingsSaveResult result = coordinator.Save(CreateInput(
            repositoryMappings: $"owner/repo={checkoutPath}"));

        result.IsSaved.Should().BeTrue();
        result.ReviewerPresetId.Should().Be("claude");
        _settingsService.Settings.ResourceUris.Should().Equal("queue://review/queue");
        _settingsService.Settings.NotificationTimeoutMs.Should().Be(30000);
        _settingsService.Settings.RepositoryCheckoutMappings["owner/repo"].Should().Be(checkoutPath);
    }

    [Fact]
    public void Save_ShouldReturnNotReadyWithoutChangingSettings_WhenInputIsIncomplete()
    {
        SettingsCoordinator coordinator = new(_settingsService);
        string originalGatewayUrl = _settingsService.Settings.GatewayUrl;

        SettingsSaveResult result = coordinator.Save(CreateInput(
            resourceUrisText: " ",
            gatewayUrl: "not a url"));

        result.Status.Should().Be(SettingsSaveStatus.NotReady);
        _settingsService.Settings.GatewayUrl.Should().Be(originalGatewayUrl);
    }

    [Fact]
    public void Save_ShouldReturnNotReadyWhenSettingsValidationFails()
    {
        SettingsCoordinator coordinator = new(_settingsService);

        SettingsSaveResult result = coordinator.Save(CreateInput(commandPath: ""));

        result.Status.Should().Be(SettingsSaveStatus.NotReady);
        result.ReviewerPresetId.Should().BeNull();
    }

    [Fact]
    public async Task DetectGatewayUrlsAsync_ShouldReturnParsedBaseUrls()
    {
        Mock<IProcessInstance> process = CreateProcess("0.0.0.0:8080->8080/tcp", string.Empty, 0);
        var runner = new Mock<IProcessRunner>();
        ProcessStartInfo? actualStartInfo = null;
        runner.Setup(runner => runner.Start(It.IsAny<ProcessStartInfo>()))
            .Callback<ProcessStartInfo>(startInfo => actualStartInfo = startInfo)
            .Returns(process.Object);
        SettingsCoordinator coordinator = new(_settingsService, runner.Object);

        GatewayDetectionResult result = await coordinator.DetectGatewayUrlsAsync(CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.BaseUrls.Should().ContainSingle().Which.Should().Be("http://localhost:8080");
        actualStartInfo!.FileName.Should().Be("docker");
        actualStartInfo.ArgumentList.Should().Equal(
            "ps",
            "--filter",
            "name=mcp-gateway",
            "--format",
            "{{.Ports}}");
    }

    [Fact]
    public async Task DetectGatewayUrlsAsync_ShouldReturnError_WhenDockerReturnsFailure()
    {
        Mock<IProcessInstance> process = CreateProcess(string.Empty, "daemon unavailable", 1);
        var runner = new Mock<IProcessRunner>();
        runner.Setup(runner => runner.Start(It.IsAny<ProcessStartInfo>())).Returns(process.Object);
        SettingsCoordinator coordinator = new(_settingsService, runner.Object);

        GatewayDetectionResult result = await coordinator.DetectGatewayUrlsAsync(CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.ErrorTitle.Should().Be("Docker エラー");
        result.ErrorMessage.Should().Contain("daemon unavailable");
    }

    [Fact]
    public async Task DetectGatewayUrlsAsync_ShouldReturnNoContainerError_WhenNoPortMatches()
    {
        Mock<IProcessInstance> process = CreateProcess("mcp-gateway", string.Empty, 0);
        var runner = new Mock<IProcessRunner>();
        runner.Setup(runner => runner.Start(It.IsAny<ProcessStartInfo>())).Returns(process.Object);
        SettingsCoordinator coordinator = new(_settingsService, runner.Object);

        GatewayDetectionResult result = await coordinator.DetectGatewayUrlsAsync(CancellationToken.None);

        result.ErrorTitle.Should().Be("コンテナが見つかりませんでした");
        result.BaseUrls.Should().BeEmpty();
    }

    [Fact]
    public async Task DetectGatewayUrlsAsync_ShouldReturnDockerMissingError_WhenProcessCannotStart()
    {
        var runner = new Mock<IProcessRunner>();
        runner.Setup(runner => runner.Start(It.IsAny<ProcessStartInfo>()))
            .Throws(new Win32Exception("docker not found"));
        SettingsCoordinator coordinator = new(_settingsService, runner.Object);

        GatewayDetectionResult result = await coordinator.DetectGatewayUrlsAsync(CancellationToken.None);

        result.ErrorTitle.Should().Be("Docker が見つかりませんでした");
    }

    [Fact]
    public void KnownResourceUris_ShouldExposeSupportedReviewQueues()
    {
        SettingsCoordinator.KnownResourceUris.Should().Equal(
            "queue://review/queue",
            "queue://review/re-review-requests");
    }

    [Fact]
    public async Task AutoDetectGatewayUrlAsync_ShouldReturnSelectedGatewayUrl()
    {
        Mock<IProcessInstance> process = CreateProcess("0.0.0.0:8080->8080/tcp", string.Empty, 0);
        var runner = new Mock<IProcessRunner>();
        runner.Setup(value => value.Start(It.IsAny<ProcessStartInfo>())).Returns(process.Object);
        SettingsCoordinator coordinator = new(_settingsService, runner.Object);
        GatewayUrlSelectionRequest? actualRequest = null;

        SettingsInputPresentation presentation = await coordinator.AutoDetectGatewayUrlAsync(
            request =>
            {
                actualRequest = request;
                return Task.FromResult(new GatewayUrlSelectionResult(
                    IsConfirmed: true,
                    SelectedBaseUrl: "http://localhost:8080",
                    Route: "mcp/custom"));
            },
            CancellationToken.None);

        actualRequest!.BaseUrls.Should().Equal("http://localhost:8080");
        actualRequest.DefaultRoute.Should().Be(DockerPortParser.DefaultMcpRoute);
        presentation.GatewayUrl.Should().Be("http://localhost:8080/mcp/custom");
        presentation.ResourceUrisText.Should().BeNull();
        presentation.HasError.Should().BeFalse();
    }

    [Theory]
    [InlineData(false, "http://localhost:8080")]
    [InlineData(true, "")]
    public async Task AutoDetectGatewayUrlAsync_ShouldLeaveInputUnchanged_WhenSelectionCannotApply(
        bool isConfirmed,
        string selectedBaseUrl)
    {
        Mock<IProcessInstance> process = CreateProcess("0.0.0.0:8080->8080/tcp", string.Empty, 0);
        var runner = new Mock<IProcessRunner>();
        runner.Setup(value => value.Start(It.IsAny<ProcessStartInfo>())).Returns(process.Object);
        SettingsCoordinator coordinator = new(_settingsService, runner.Object);

        SettingsInputPresentation presentation = await coordinator.AutoDetectGatewayUrlAsync(
            _ => Task.FromResult(new GatewayUrlSelectionResult(isConfirmed, selectedBaseUrl, "/mcp/thread-owl")),
            CancellationToken.None);

        presentation.GatewayUrl.Should().BeNull();
        presentation.ResourceUrisText.Should().BeNull();
        presentation.HasError.Should().BeFalse();
    }

    [Fact]
    public async Task AutoDetectGatewayUrlAsync_ShouldReturnDetectionErrorWithoutOpeningSelector()
    {
        Mock<IProcessInstance> process = CreateProcess(string.Empty, "daemon unavailable", 1);
        var runner = new Mock<IProcessRunner>();
        runner.Setup(value => value.Start(It.IsAny<ProcessStartInfo>())).Returns(process.Object);
        SettingsCoordinator coordinator = new(_settingsService, runner.Object);
        int selectorCalls = 0;

        SettingsInputPresentation presentation = await coordinator.AutoDetectGatewayUrlAsync(
            _ =>
            {
                selectorCalls++;
                return Task.FromResult(new GatewayUrlSelectionResult(false, null, string.Empty));
            },
            CancellationToken.None);

        presentation.HasError.Should().BeTrue();
        presentation.ErrorTitle.Should().Be("Docker エラー");
        selectorCalls.Should().Be(0);
    }

    [Fact]
    public async Task AddKnownResourceUrisAsync_ShouldMergeConfirmedSelection()
    {
        SettingsCoordinator coordinator = new(_settingsService);
        ResourceUriSelectionRequest? actualRequest = null;

        SettingsInputPresentation presentation = await coordinator.AddKnownResourceUrisAsync(
            "queue://review/queue",
            request =>
            {
                actualRequest = request;
                return Task.FromResult(new ResourceUriSelectionResult(
                    IsConfirmed: true,
                    SelectedResourceUris: ["queue://review/queue", "queue://review/re-review-requests"]));
            });

        actualRequest!.Title.Should().Be("Resource URI を追加");
        actualRequest.ResourceUris.Should().Equal(SettingsCoordinator.KnownResourceUris);
        presentation.ResourceUrisText.Should().Be(
            "queue://review/queue\nqueue://review/re-review-requests");
        presentation.GatewayUrl.Should().BeNull();
        presentation.HasError.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddKnownResourceUrisAsync_ShouldLeaveInputUnchanged_WhenSelectionHasNoResult(bool isConfirmed)
    {
        SettingsCoordinator coordinator = new(_settingsService);
        IReadOnlyList<string> selectedResourceUris = isConfirmed
            ? []
            : ["queue://review/queue"];

        SettingsInputPresentation presentation = await coordinator.AddKnownResourceUrisAsync(
            "queue://review/queue",
            _ => Task.FromResult(new ResourceUriSelectionResult(isConfirmed, selectedResourceUris)));

        presentation.ResourceUrisText.Should().BeNull();
        presentation.HasError.Should().BeFalse();
    }

    [Fact]
    public async Task FetchAndAddResourceUrisAsync_ShouldReturnFetchErrorWithoutOpeningSelector()
    {
        SettingsCoordinator coordinator = new(
            _settingsService,
            mcpResourceUriReader: (_, _, _) => Task.FromResult<IReadOnlyList<string>>([]));
        int selectorCalls = 0;

        SettingsInputPresentation presentation = await coordinator.FetchAndAddResourceUrisAsync(
            "queue://review/queue",
            "not a url",
            _ =>
            {
                selectorCalls++;
                return Task.FromResult(new ResourceUriSelectionResult(false, []));
            },
            CancellationToken.None);

        presentation.HasError.Should().BeTrue();
        presentation.ErrorTitle.Should().Be("設定エラー");
        selectorCalls.Should().Be(0);
    }

    [Fact]
    public async Task FetchAndAddResourceUrisAsync_ShouldMergeSelectedResourceUris()
    {
        SettingsCoordinator coordinator = new(
            _settingsService,
            mcpResourceUriReader: (_, _, _) => Task.FromResult<IReadOnlyList<string>>(
                ["queue://review/queue", "queue://review/re-review-requests"]));
        ResourceUriSelectionRequest? actualRequest = null;

        SettingsInputPresentation presentation = await coordinator.FetchAndAddResourceUrisAsync(
            "queue://review/queue",
            "http://localhost:3000/mcp",
            request =>
            {
                actualRequest = request;
                return Task.FromResult(new ResourceUriSelectionResult(
                    IsConfirmed: true,
                    SelectedResourceUris: request.ResourceUris));
            },
            CancellationToken.None);

        actualRequest!.Title.Should().Be("追加する Resource URI を選択");
        presentation.ResourceUrisText.Should().Be(
            "queue://review/queue\nqueue://review/re-review-requests");
        presentation.HasError.Should().BeFalse();
    }

    [Fact]
    public async Task FetchResourceUrisAsync_ShouldReturnUrisAndPassEndpoint()
    {
        Uri? actualEndpoint = null;
        SettingsCoordinator coordinator = new(
            _settingsService,
            mcpResourceUriReader: (endpoint, _, _) =>
            {
                actualEndpoint = endpoint;
                return Task.FromResult<IReadOnlyList<string>>(["queue://review/queue"]);
            });

        ResourceUriFetchResult result = await coordinator.FetchResourceUrisAsync(
            "http://localhost:3000/mcp",
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.ResourceUris.Should().ContainSingle().Which.Should().Be("queue://review/queue");
        actualEndpoint.Should().Be(new Uri("http://localhost:3000/mcp"));
    }

    [Fact]
    public async Task FetchResourceUrisAsync_ShouldReturnErrorForInvalidGatewayUrl()
    {
        bool readerCalled = false;
        SettingsCoordinator coordinator = new(
            _settingsService,
            mcpResourceUriReader: (_, _, _) =>
            {
                readerCalled = true;
                return Task.FromResult<IReadOnlyList<string>>([]);
            });

        ResourceUriFetchResult result = await coordinator.FetchResourceUrisAsync(
            "not a url",
            CancellationToken.None);

        result.ErrorTitle.Should().Be("設定エラー");
        readerCalled.Should().BeFalse();
    }

    [Fact]
    public async Task FetchResourceUrisAsync_ShouldReturnEmptyResultError()
    {
        SettingsCoordinator coordinator = new(
            _settingsService,
            mcpResourceUriReader: (_, _, _) => Task.FromResult<IReadOnlyList<string>>([]));

        ResourceUriFetchResult result = await coordinator.FetchResourceUrisAsync(
            "http://localhost:3000/mcp",
            CancellationToken.None);

        result.ErrorTitle.Should().Be("リソースが見つかりません");
    }

    [Fact]
    public async Task FetchResourceUrisAsync_ShouldMapProbeFailureToUserMessage()
    {
        SettingsCoordinator coordinator = new(
            _settingsService,
            mcpResourceUriReader: (_, _, _) =>
                Task.FromException<IReadOnlyList<string>>(
                    new HttpRequestException("connection refused")));

        ResourceUriFetchResult result = await coordinator.FetchResourceUrisAsync(
            "http://localhost:3000/mcp",
            CancellationToken.None);

        result.ErrorTitle.Should().Be("取得エラー");
        result.ErrorMessage.Should().Contain("接続に失敗");
    }

    public void Dispose()
    {
        if (Directory.Exists(_settingsDirectory))
        {
            Directory.Delete(_settingsDirectory, recursive: true);
        }
    }

    private static SettingsInput CreateInput(
        string commandPath = "mcp-resource-subscriber",
        string gatewayUrl = "http://localhost:3000/mcp",
        string resourceUrisText = "queue://review/queue",
        string repositoryMappings = "")
    {
        LauncherAgentDefinition claude = LauncherAgentCatalog.Find("claude")!;
        return new SettingsInput(
            commandPath,
            "--skip-resource-list-check",
            gatewayUrl,
            resourceUrisText,
            30000,
            claude.Command,
            claude.ReviewerArgumentsTemplate,
            claude.Command,
            claude.ReviewedArgumentsTemplate,
            300000,
            repositoryMappings);
    }

    private static Mock<IProcessInstance> CreateProcess(string stdout, string stderr, int exitCode)
    {
        var process = new Mock<IProcessInstance>();
        process.SetupGet(value => value.ExitCode).Returns(exitCode);
        process.SetupGet(value => value.StandardOutput).Returns(
            new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(stdout))));
        process.SetupGet(value => value.StandardError).Returns(
            new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(stderr))));
        process.Setup(value => value.WaitForExitAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return process;
    }
}
