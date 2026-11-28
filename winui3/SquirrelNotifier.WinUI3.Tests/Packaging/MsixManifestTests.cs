// <copyright file="MsixManifestTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Xml.Linq;
using FluentAssertions;
using SquirrelNotifier.WinUI3.Helpers;
using Xunit;

namespace SquirrelNotifier.WinUI3.Tests.Packaging;

/// <summary>
/// MSIX マニフェスト（#569）と、アプリのコードとの整合を固定する.
/// </summary>
public class MsixManifestTests
{
    private static readonly XNamespace _foundation = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
    private static readonly XNamespace _rescap = "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities";
    private static readonly XNamespace _virtualization = "http://schemas.microsoft.com/appx/manifest/virtualization/windows10";

    private readonly XElement _package = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Packaging", "AppxManifest.xml")).Root!;

    [Fact]
    public void Manifest_ShouldExcludeDataDirectoryFromVirtualization()
    {
        // 公開契約のファイルをパッケージ外から読めるよう、アプリの既定のデータディレクトリを除外する
        string directoryName = Path.GetFileName(AppDataPaths.ResolveRoot(overrideRoot: null, @"C:\local"));

        _package.Descendants(_virtualization + "ExcludedDirectory").Select(element => element.Value)
            .Should().Equal($@"$(KnownFolder:LocalAppData)\{directoryName}");
        _package.Descendants(_rescap + "Capability").Select(element => (string?)element.Attribute("Name"))
            .Should().BeEquivalentTo("runFullTrust", "unvirtualizedResources");
    }

    [Fact]
    public void Manifest_ShouldKeepPackageIdentity()
    {
        // Name と Publisher を変えると、導入済みの環境を更新できなくなる
        XElement identity = _package.Element(_foundation + "Identity")!;

        identity.Attribute("Name")!.Value.Should().Be("scottlz0310.SquirrelNotifier");
        identity.Attribute("Publisher")!.Value.Should().Be("CN=scottlz0310");
        identity.Attribute("ProcessorArchitecture")!.Value.Should().Be("x64");
    }

    [Fact]
    public void Manifest_ShouldLaunchApplicationExecutable()
    {
        XElement application = _package.Descendants(_foundation + "Application").Single();

        application.Attribute("Executable")!.Value.Should().Be($"{typeof(AppDataPaths).Assembly.GetName().Name}.exe");
    }
}
