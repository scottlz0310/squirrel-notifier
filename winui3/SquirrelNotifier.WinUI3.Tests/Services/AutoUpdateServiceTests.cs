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
}
