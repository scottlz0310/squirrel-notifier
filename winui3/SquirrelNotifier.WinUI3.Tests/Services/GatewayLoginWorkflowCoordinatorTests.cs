// <copyright file="GatewayLoginWorkflowCoordinatorTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using FluentAssertions;
using SquirrelNotifier.WinUI3.Models;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public sealed class GatewayLoginWorkflowCoordinatorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"GatewayLoginWorkflowTests_{Guid.NewGuid()}");
    private readonly LoggingService _logging;

    public GatewayLoginWorkflowCoordinatorTests()
    {
        _logging = new LoggingService(_directory);
    }

    [Fact]
    public async Task StartAsync_ShouldShowValidationErrorWithoutCreatingLoginSession()
    {
        var loginService = new FakeGatewayLoginService();
        var ui = new UiRecorder();
        int factoryCalls = 0;
        int dialogCalls = 0;
        var coordinator = new GatewayLoginWorkflowCoordinator(
            new GatewayLoginCoordinator(),
            () =>
            {
                factoryCalls++;
                return loginService;
            },
            _logging);

        await coordinator.StartAsync(
            "not-a-gateway-url",
            SubscriptionState.Stopped,
            _ =>
            {
                dialogCalls++;
                throw new InvalidOperationException("無効な URL ではダイアログを作成しません。");
            },
            ui.Actions);

        factoryCalls.Should().Be(0);
        dialogCalls.Should().Be(0);
        loginService.LoginCalls.Should().Be(0);
        ui.ButtonStates.Should().BeEmpty();
        ui.Alerts.Should().ContainSingle();
        ui.Alerts.Single().Title.Should().Be("設定エラー");
    }

    [Fact]
    public async Task StartAsync_ShouldApplySuccessAndRestartStoppedSubscription()
    {
        var loginService = new FakeGatewayLoginService();
        var dialog = new FakeGatewayLoginDialog();
        var ui = new UiRecorder();
        GatewayLoginWorkflowCoordinator coordinator = Create(loginService);

        Task operation = coordinator.StartAsync(
            "https://gateway.example.com/mcp",
            SubscriptionState.Stopped,
            dialog.CreatePort,
            ui.Actions);
        await dialog.ShowStarted.WaitAsync(TimeSpan.FromSeconds(5));

        dialog.Open();
        loginService.Complete(McpLoginOutcome.Succeeded);
        await operation.WaitAsync(TimeSpan.FromSeconds(5));

        loginService.LoginCalls.Should().Be(1);
        dialog.HideCalls.Should().Be(1);
        ui.ButtonStates.Should().Equal(false, true);
        ui.AuthRequiredInfoBarCloseCalls.Should().Be(1);
        ui.RestartSubscriptionCalls.Should().Be(1);
        ui.Alerts.Should().ContainSingle();
        ui.Alerts.Single().Title.Should().Be("ログイン成功");
    }

    [Fact]
    public async Task StartAsync_ShouldCancelLoginWhenUserClosesDialog()
    {
        var loginService = new FakeGatewayLoginService();
        loginService.LoginHandler = cancellationToken =>
        {
            cancellationToken.Register(() => loginService.Complete(McpLoginOutcome.Cancelled));
            return loginService.ResultTask;
        };
        var dialog = new FakeGatewayLoginDialog();
        var ui = new UiRecorder();
        GatewayLoginWorkflowCoordinator coordinator = Create(loginService);

        Task operation = coordinator.StartAsync(
            "https://gateway.example.com/mcp",
            SubscriptionState.Running,
            dialog.CreatePort,
            ui.Actions);
        await dialog.ShowStarted.WaitAsync(TimeSpan.FromSeconds(5));

        dialog.Open();
        dialog.CloseByUser();
        await operation.WaitAsync(TimeSpan.FromSeconds(5));

        loginService.LoginCancellationToken.IsCancellationRequested.Should().BeTrue();
        dialog.HideCalls.Should().Be(0);
        ui.ButtonStates.Should().Equal(false, true);
        ui.Alerts.Should().BeEmpty();
        ui.AuthRequiredInfoBarCloseCalls.Should().Be(0);
        ui.RestartSubscriptionCalls.Should().Be(0);
    }

    [Fact]
    public async Task StartAsync_ShouldDeferCloseUntilDialogHasOpened()
    {
        var loginService = new FakeGatewayLoginService();
        var dialog = new FakeGatewayLoginDialog();
        var ui = new UiRecorder();
        GatewayLoginWorkflowCoordinator coordinator = Create(loginService);

        Task operation = coordinator.StartAsync(
            "https://gateway.example.com/mcp",
            SubscriptionState.Running,
            dialog.CreatePort,
            ui.Actions);
        await dialog.ShowStarted.WaitAsync(TimeSpan.FromSeconds(5));

        loginService.Complete(McpLoginOutcome.Succeeded);
        await dialog.DispatchStarted.WaitAsync(TimeSpan.FromSeconds(5));
        dialog.HideCalls.Should().Be(0);

        dialog.Open();
        await operation.WaitAsync(TimeSpan.FromSeconds(5));

        dialog.HideCalls.Should().Be(1);
    }

    [Fact]
    public async Task StartAsync_ShouldDispatchProgressAndCopyLatestVerificationValues()
    {
        var loginService = new FakeGatewayLoginService();
        var dialog = new FakeGatewayLoginDialog();
        var ui = new UiRecorder();
        GatewayLoginWorkflowCoordinator coordinator = Create(loginService);
        var copied = new List<string>();

        Task operation = coordinator.StartAsync(
            "https://gateway.example.com/mcp",
            SubscriptionState.Running,
            dialog.CreatePort,
            ui.Actions);
        await dialog.ShowStarted.WaitAsync(TimeSpan.FromSeconds(5));

        dialog.Open();
        dialog.CopyVerificationUrl(copied.Add);
        dialog.CopyUserCode(copied.Add);
        loginService.RaiseStatus("ブラウザで承認を待っています。");
        loginService.RaiseVerification(
            "https://example.com/device",
            null,
            string.Empty);
        dialog.CopyUserCode(copied.Add);
        loginService.RaiseVerification(
            "https://example.com/device",
            "https://example.com/device?user_code=ABCD-EFGH",
            "ABCD-EFGH");
        dialog.CopyVerificationUrl(copied.Add);
        dialog.CopyUserCode(copied.Add);
        loginService.Complete(McpLoginOutcome.Cancelled);
        await operation.WaitAsync(TimeSpan.FromSeconds(5));

        dialog.Statuses.Should().Equal("ブラウザで承認を待っています。");
        dialog.Verifications.Should().HaveCount(2);
        dialog.Verifications.Last().Url.Should().Be("https://example.com/device?user_code=ABCD-EFGH");
        copied.Should().Equal("https://example.com/device?user_code=ABCD-EFGH", "ABCD-EFGH");
    }

    [Fact]
    public async Task StartAsync_ShouldIgnoreReentryUntilFirstLoginCompletes()
    {
        var loginService = new FakeGatewayLoginService();
        loginService.LoginHandler = cancellationToken =>
        {
            cancellationToken.Register(() => loginService.Complete(McpLoginOutcome.Cancelled));
            return loginService.ResultTask;
        };
        var firstDialog = new FakeGatewayLoginDialog();
        var ui = new UiRecorder();
        int factoryCalls = 0;
        int secondDialogCalls = 0;
        var coordinator = new GatewayLoginWorkflowCoordinator(
            new GatewayLoginCoordinator(),
            () =>
            {
                factoryCalls++;
                return loginService;
            },
            _logging);

        Task first = coordinator.StartAsync(
            "https://gateway.example.com/mcp",
            SubscriptionState.Running,
            firstDialog.CreatePort,
            ui.Actions);
        await firstDialog.ShowStarted.WaitAsync(TimeSpan.FromSeconds(5));

        await coordinator.StartAsync(
            "https://gateway.example.com/mcp",
            SubscriptionState.Running,
            _ =>
            {
                secondDialogCalls++;
                throw new InvalidOperationException("再入ではダイアログを作成しません。");
            },
            ui.Actions);

        factoryCalls.Should().Be(1);
        secondDialogCalls.Should().Be(0);
        ui.ButtonStates.Should().Equal(false);

        firstDialog.Open();
        firstDialog.CloseByUser();
        await first.WaitAsync(TimeSpan.FromSeconds(5));

        ui.ButtonStates.Should().Equal(false, true);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    private GatewayLoginWorkflowCoordinator Create(FakeGatewayLoginService loginService)
        => new(new GatewayLoginCoordinator(), () => loginService, _logging);

    private sealed class UiRecorder
    {
        public List<bool> ButtonStates { get; } = [];

        public List<(string Title, string Message)> Alerts { get; } = [];

        public int AuthRequiredInfoBarCloseCalls { get; private set; }

        public int RestartSubscriptionCalls { get; private set; }

        public GatewayLoginUiActions Actions => new(
            ButtonStates.Add,
            () => AuthRequiredInfoBarCloseCalls++,
            () => RestartSubscriptionCalls++,
            (title, message) =>
            {
                Alerts.Add((title, message));
                return Task.CompletedTask;
            });
    }

    private sealed class FakeGatewayLoginDialog
    {
        private readonly TaskCompletionSource _showStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _dispatchStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _showCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private GatewayLoginDialogSession? _session;

        public int HideCalls { get; private set; }

        public List<string> Statuses { get; } = [];

        public List<DeviceVerificationView> Verifications { get; } = [];

        public Task ShowStarted => _showStarted.Task;

        public Task DispatchStarted => _dispatchStarted.Task;

        public GatewayLoginDialogPort CreatePort(GatewayLoginDialogSession session)
        {
            _session = session;
            return new GatewayLoginDialogPort(
                ShowAsync,
                Hide,
                Dispatch,
                Statuses.Add,
                Verifications.Add);
        }

        public void Open() => _session!.OnDialogOpened();

        public void CloseByUser() => _showCompletion.TrySetResult();

        public void CopyVerificationUrl(Action<string> copyText) => _session!.CopyVerificationUrl(copyText);

        public void CopyUserCode(Action<string> copyText) => _session!.CopyUserCode(copyText);

        private Task ShowAsync()
        {
            _showStarted.TrySetResult();
            return _showCompletion.Task;
        }

        private void Hide()
        {
            HideCalls++;
            _showCompletion.TrySetResult();
        }

        private bool Dispatch(Action action)
        {
            _dispatchStarted.TrySetResult();
            action();
            return true;
        }
    }

    private sealed class FakeGatewayLoginService : IGatewayLoginService
    {
        private readonly TaskCompletionSource<McpLoginResult> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event EventHandler<string>? StatusChanged;

        public event EventHandler<DeviceVerificationInfo>? VerificationReceived;

        public int LoginCalls { get; private set; }

        public CancellationToken LoginCancellationToken { get; private set; }

        public Func<CancellationToken, Task<McpLoginResult>> LoginHandler { get; set; }

        public Task<McpLoginResult> ResultTask => _result.Task;

        public FakeGatewayLoginService()
        {
            LoginHandler = _ => ResultTask;
        }

        public Task<McpLoginResult> LoginAsync(CancellationToken cancellationToken)
        {
            LoginCalls++;
            LoginCancellationToken = cancellationToken;
            return LoginHandler(cancellationToken);
        }

        public void RaiseStatus(string status) => StatusChanged?.Invoke(this, status);

        public void RaiseVerification(string verificationUri, string? verificationUriComplete, string userCode)
            => VerificationReceived?.Invoke(this, new DeviceVerificationInfo
            {
                VerificationUri = verificationUri,
                VerificationUriComplete = verificationUriComplete,
                UserCode = userCode,
            });

        public void Complete(McpLoginOutcome outcome)
            => _result.TrySetResult(new McpLoginResult { Outcome = outcome });
    }
}
