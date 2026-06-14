using System.Net;
using System.Net.Http;
using System.Text;
using FluentAssertions;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public class AutoUpdateServiceTests : IDisposable
{
    private readonly string _logDir;

    public AutoUpdateServiceTests()
    {
        _logDir = Path.Combine(Path.GetTempPath(), $"AutoUpdateTests_{Guid.NewGuid()}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_logDir))
        {
            Directory.Delete(_logDir, true);
        }
    }

    [Fact]
    public async Task CheckForUpdatesAsync_ShouldDetectNewerVersion()
    {
        // Arrange
        string json = """{"tag_name":"v3.1.0","html_url":"https://example/releases/v3.1.0"}""";
        var handler = new FakeHandler(json, HttpStatusCode.OK);
        using var httpClient = new HttpClient(handler);
        var logging = new LoggingService(_logDir);
        var service = new AutoUpdateService(logging, httpClient, new Version(3, 0, 0, 0));

        // Act
        AutoUpdateResult result = await service.CheckForUpdatesAsync(CancellationToken.None);

        // Assert
        result.HasUpdate.Should().BeTrue();
        result.LatestVersion.Should().Be(new Version(3, 1, 0));
        result.ReleaseUrl.Should().Be("https://example/releases/v3.1.0");
    }

    [Fact]
    public async Task CheckForUpdatesAsync_ShouldReturnNoUpdateWhenUpToDate()
    {
        // Arrange
        string json = """{"tag_name":"v3.0.0","html_url":"https://example/releases/v3.0.0"}""";
        var handler = new FakeHandler(json, HttpStatusCode.OK);
        using var httpClient = new HttpClient(handler);
        var logging = new LoggingService(_logDir);
        var service = new AutoUpdateService(logging, httpClient, new Version(3, 0, 0, 0));

        // Act
        AutoUpdateResult result = await service.CheckForUpdatesAsync(CancellationToken.None);

        // Assert
        result.HasUpdate.Should().BeFalse();
    }

    [Fact]
    public async Task CheckForUpdatesAsync_ShouldHandleHttpError()
    {
        // Arrange
        var handler = new FakeHandler(string.Empty, HttpStatusCode.InternalServerError);
        using var httpClient = new HttpClient(handler);
        var logging = new LoggingService(_logDir);
        var service = new AutoUpdateService(logging, httpClient, new Version(3, 0, 0, 0));

        // Act
        AutoUpdateResult result = await service.CheckForUpdatesAsync(CancellationToken.None);

        // Assert
        result.HasUpdate.Should().BeFalse();
    }

    [Fact]
    public async Task CheckForUpdatesAsync_ShouldReturnNoUpdateWhenTagMissing()
    {
        // Arrange
        string json = """{"name":"release without tag"}""";
        var handler = new FakeHandler(json, HttpStatusCode.OK);
        using var httpClient = new HttpClient(handler);
        var logging = new LoggingService(_logDir);
        var service = new AutoUpdateService(logging, httpClient, new Version(3, 0, 0, 0));

        // Act
        AutoUpdateResult result = await service.CheckForUpdatesAsync(CancellationToken.None);

        // Assert
        result.HasUpdate.Should().BeFalse();
    }

    [Fact]
    public async Task CheckForUpdatesAsync_ShouldReturnNoUpdateWhenTagIsWhitespace()
    {
        // Arrange
        string json = """{"tag_name":"   ","html_url":"https://example/releases/v3.1.0"}""";
        var handler = new FakeHandler(json, HttpStatusCode.OK);
        using var httpClient = new HttpClient(handler);
        var logging = new LoggingService(_logDir);
        var service = new AutoUpdateService(logging, httpClient, new Version(3, 0, 0, 0));

        // Act
        AutoUpdateResult result = await service.CheckForUpdatesAsync(CancellationToken.None);

        // Assert
        result.HasUpdate.Should().BeFalse();
    }

    [Fact]
    public async Task CheckForUpdatesAsync_ShouldHandleInvalidVersion()
    {
        // Arrange
        string json = """{"tag_name":"v-next","html_url":"https://example/releases/v-next"}""";
        var handler = new FakeHandler(json, HttpStatusCode.OK);
        using var httpClient = new HttpClient(handler);
        var logging = new LoggingService(_logDir);
        var service = new AutoUpdateService(logging, httpClient, new Version(3, 0, 0, 0));

        // Act
        AutoUpdateResult result = await service.CheckForUpdatesAsync(CancellationToken.None);

        // Assert
        result.HasUpdate.Should().BeFalse();
        result.ReleaseUrl.Should().BeEmpty();
    }

    [Fact]
    public async Task CheckForUpdatesAsync_ShouldHandleInvalidJson()
    {
        // Arrange
        const string json = "not-json";
        var handler = new FakeHandler(json, HttpStatusCode.OK);
        using var httpClient = new HttpClient(handler);
        var logging = new LoggingService(_logDir);
        var service = new AutoUpdateService(logging, httpClient, new Version(3, 0, 0, 0));

        // Act
        AutoUpdateResult result = await service.CheckForUpdatesAsync(CancellationToken.None);

        // Assert
        result.HasUpdate.Should().BeFalse();
    }

    [Fact]
    public async Task CheckForUpdatesAsync_ShouldHandleHttpException()
    {
        // Arrange
        var handler = new ThrowingHandler();
        using var httpClient = new HttpClient(handler);
        var logging = new LoggingService(_logDir);
        var service = new AutoUpdateService(logging, httpClient, new Version(3, 0, 0, 0));

        // Act
        AutoUpdateResult result = await service.CheckForUpdatesAsync(CancellationToken.None);

        // Assert
        result.HasUpdate.Should().BeFalse();
    }

    [Fact]
    public async Task CheckForUpdatesAsync_ShouldRespectExistingUserAgent()
    {
        // Arrange
        string json = """{"tag_name":"v3.1.0","html_url":"https://example/releases/v3.1.0"}""";
        var handler = new FakeHandler(json, HttpStatusCode.OK);
        using var httpClient = new HttpClient(handler);
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("CustomAgent/1.0");
        var logging = new LoggingService(_logDir);
        var service = new AutoUpdateService(logging, httpClient, new Version(3, 0, 0, 0));

        // Act
        AutoUpdateResult result = await service.CheckForUpdatesAsync(CancellationToken.None);

        // Assert
        result.HasUpdate.Should().BeTrue();
    }

    [Fact]
    public async Task CheckForUpdatesAsync_ShouldHandleMissingReleaseUrl()
    {
        // Arrange
        string json = """{"tag_name":"v3.1.0"}""";
        var handler = new FakeHandler(json, HttpStatusCode.OK);
        using var httpClient = new HttpClient(handler);
        var logging = new LoggingService(_logDir);
        var service = new AutoUpdateService(logging, httpClient, new Version(3, 0, 0, 0));

        // Act
        AutoUpdateResult result = await service.CheckForUpdatesAsync(CancellationToken.None);

        // Assert
        result.ReleaseUrl.Should().BeEmpty();
    }

    [Fact]
    public async Task CheckForUpdatesAsync_ShouldRetryOnTransientHttpErrorAndSucceed()
    {
        // Arrange
        var responses = new List<Func<Task<HttpResponseMessage>>>
        {
            () => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)),
            () => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"tag_name":"v3.1.0","html_url":"https://example/releases/v3.1.0"}""", Encoding.UTF8, "application/json")
            })
        };
        var handler = new SequenceHandler(responses);
        using var httpClient = new HttpClient(handler);
        var logging = new LoggingService(_logDir);
        var service = new AutoUpdateService(logging, httpClient, new Version(3, 0, 0, 0));

        // Act
        AutoUpdateResult result = await service.CheckForUpdatesAsync(CancellationToken.None);

        // Assert
        result.HasUpdate.Should().BeTrue();
        result.LatestVersion.Should().Be(new Version(3, 1, 0));
        handler.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_ShouldRetryOnHttpExceptionAndSucceed()
    {
        // Arrange
        var responses = new List<Func<Task<HttpResponseMessage>>>
        {
            () => throw new HttpRequestException("transient network error"),
            () => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"tag_name":"v3.1.0","html_url":"https://example/releases/v3.1.0"}""", Encoding.UTF8, "application/json")
            })
        };
        var handler = new SequenceHandler(responses);
        using var httpClient = new HttpClient(handler);
        var logging = new LoggingService(_logDir);
        var service = new AutoUpdateService(logging, httpClient, new Version(3, 0, 0, 0));

        // Act
        AutoUpdateResult result = await service.CheckForUpdatesAsync(CancellationToken.None);

        // Assert
        result.HasUpdate.Should().BeTrue();
        result.LatestVersion.Should().Be(new Version(3, 1, 0));
        handler.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_ShouldGiveUpAfterMaxRetries()
    {
        // Arrange
        var responses = new List<Func<Task<HttpResponseMessage>>>
        {
            () => throw new HttpRequestException("transient network error"),
            () => throw new HttpRequestException("transient network error"),
            () => throw new HttpRequestException("transient network error")
        };
        var handler = new SequenceHandler(responses);
        using var httpClient = new HttpClient(handler);
        var logging = new LoggingService(_logDir);
        var service = new AutoUpdateService(logging, httpClient, new Version(3, 0, 0, 0));

        // Act
        AutoUpdateResult result = await service.CheckForUpdatesAsync(CancellationToken.None);

        // Assert
        result.HasUpdate.Should().BeFalse();
        handler.CallCount.Should().Be(3);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_ShouldRespectCancellationAndAbortImmediately()
    {
        // Arrange
        var responses = new List<Func<Task<HttpResponseMessage>>>
        {
            () => throw new HttpRequestException("transient network error")
        };
        var handler = new SequenceHandler(responses);
        using var httpClient = new HttpClient(handler);
        var logging = new LoggingService(_logDir);
        var service = new AutoUpdateService(logging, httpClient, new Version(3, 0, 0, 0));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act
        Func<Task> act = async () => await service.CheckForUpdatesAsync(cts.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
        handler.CallCount.Should().Be(0);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly string _content;
        private readonly HttpStatusCode _statusCode;

        public FakeHandler(string content, HttpStatusCode statusCode)
        {
            _content = content;
            _statusCode = statusCode;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_content, Encoding.UTF8, "application/json"),
            };

            return Task.FromResult(response);
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new HttpRequestException("network failed");
        }
    }

    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly Queue<Func<Task<HttpResponseMessage>>> _responses;

        public int CallCount { get; private set; }

        public SequenceHandler(IEnumerable<Func<Task<HttpResponseMessage>>> responses)
        {
            _responses = new Queue<Func<Task<HttpResponseMessage>>>(responses);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            if (_responses.Count > 0)
            {
                return await _responses.Dequeue()();
            }
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        }
    }
}
