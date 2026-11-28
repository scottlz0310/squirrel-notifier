// <copyright file="AppInstallerUpdateServiceTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Net;
using FluentAssertions;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3.Tests.Services;

public sealed class AppInstallerUpdateServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"AppInstallerUpdateTests_{Guid.NewGuid()}");
    private static readonly Uri _uri = new("https://example.com/app.appinstaller");

    [Theory]
    [InlineData("0.27.0.0", true, "0.27.0", "v0.27.0")]
    [InlineData("0.26.1.0", false, "0.26.1", "v0.26.1")]
    [InlineData("0.26.0.0", false, "0.26.0", "v0.26.0")]
    [InlineData("0.26.1.1", true, "0.26.1.1", "v0.26.1.1")]
    public async Task CheckForUpdatesAsync_ShouldComparePublishedVersion(
        string published, bool hasUpdate, string latest, string tag)
    {
        AutoUpdateResult result = await Check(HttpStatusCode.OK, Document("MainPackage", published));

        result.HasUpdate.Should().Be(hasUpdate);
        result.CurrentVersion.Should().Be(new Version(0, 26, 1));
        result.LatestVersion.Should().Be(Version.Parse(latest));
        result.Tag.Should().Be(tag);
        result.ReleaseUrl.Should().Be(_uri.AbsoluteUri);
        result.ErrorMessage.Should().BeNull();
    }

    [Theory]
    [InlineData("MainPackage", "http://schemas.microsoft.com/appx/appinstaller/2017/2")]
    [InlineData("MainBundle", "http://schemas.microsoft.com/appx/appinstaller/2021")]
    public void ParsePackageVersion_ShouldReadMainElementOfAnySchema(string element, string schema)
    {
        AppInstallerUpdateService.ParsePackageVersion(Document(element, "1.2.3.4", schema))
            .Should().Be(new Version(1, 2, 3, 4));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "")]
    [InlineData(HttpStatusCode.OK, "not xml")]
    [InlineData(HttpStatusCode.OK, "<AppInstaller />")]
    [InlineData(HttpStatusCode.OK, "<AppInstaller><MainPackage Version=\"1.0\" /></AppInstaller>")]
    [InlineData(HttpStatusCode.OK, "<!DOCTYPE a [<!ENTITY v \"1.0.0.0\">]><AppInstaller><MainPackage Version=\"&v;\" /></AppInstaller>")]
    public async Task CheckForUpdatesAsync_ShouldReportUnreadableDocument(HttpStatusCode status, string content)
    {
        AutoUpdateResult result = await Check(status, content);

        result.HasUpdate.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrEmpty();
        (await File.ReadAllTextAsync(Path.Combine(_directory, "winui3.log"))).Should().Contain("自動更新チェックに失敗しました");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static string Document(string element, string version, string schema = "http://schemas.microsoft.com/appx/appinstaller/2018")
        => $"<?xml version=\"1.0\" encoding=\"utf-8\"?><AppInstaller xmlns=\"{schema}\" Version=\"9.9.9.9\"><{element} Version=\"{version}\" /></AppInstaller>";

    private async Task<AutoUpdateResult> Check(HttpStatusCode status, string content)
    {
        using var handler = new StubHandler(status, content);
        using var client = new HttpClient(handler);
        var service = new AppInstallerUpdateService(_uri, new Version(0, 26, 1, 0), new LoggingService(_directory), client);
        return await service.CheckForUpdatesAsync(CancellationToken.None);
    }

    private sealed class StubHandler(HttpStatusCode status, string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(content) });
    }
}
