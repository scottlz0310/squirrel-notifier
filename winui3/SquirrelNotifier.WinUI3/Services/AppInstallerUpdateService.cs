// <copyright file="AppInstallerUpdateService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Xml;
using System.Xml.Linq;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>導入に使った .appinstaller を取得し、公開されている版と実行中の版を比べる（#569）.</summary>
/// <param name="appInstallerUri">導入に使った .appinstaller の URI.</param>
/// <param name="currentVersion">実行中のパッケージの版.</param>
/// <param name="loggingService">確認の失敗を記録するログ.</param>
/// <param name="httpClient">.appinstaller の取得に使うクライアント.</param>
internal sealed class AppInstallerUpdateService(
    Uri appInstallerUri,
    Version currentVersion,
    LoggingService loggingService,
    HttpClient httpClient)
{
    public async Task<AutoUpdateResult> CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        Version current = ToDisplayVersion(currentVersion);
        try
        {
            string content = await httpClient.GetStringAsync(appInstallerUri, cancellationToken).ConfigureAwait(false);
            Version published = ParsePackageVersion(content);
            Version latest = ToDisplayVersion(published);
            return new AutoUpdateResult(current, latest, published > currentVersion, $"v{latest}", appInstallerUri.AbsoluteUri);
        }
        catch (Exception ex) when (ex is HttpRequestException or XmlException or InvalidDataException)
        {
            await loggingService.WriteAsync($"自動更新チェックに失敗しました: {ex.Message}").ConfigureAwait(false);
            return new AutoUpdateResult(current, current, false, null, string.Empty, ex.Message);
        }
    }

    /// <summary>.appinstaller の MainPackage（または MainBundle）の版を読む.</summary>
    /// <param name="content">.appinstaller の XML.</param>
    /// <returns>4 つの数値からなる版.</returns>
    /// <exception cref="InvalidDataException">版を読めない場合.</exception>
    internal static Version ParsePackageVersion(string content)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using var text = new StringReader(content);
        using var reader = XmlReader.Create(text, settings);

        // 名前空間は .appinstaller のスキーマの年ごとに異なるため、要素名で探す
        string? value = XDocument.Load(reader).Root?.Elements()
            .FirstOrDefault(element => element.Name.LocalName is "MainPackage" or "MainBundle")
            ?.Attribute("Version")?.Value;
        return Version.TryParse(value, out Version? version) && version.Revision >= 0
            ? version
            : throw new InvalidDataException(".appinstaller からパッケージの版を読めません。");
    }

    // GitHub のリリースタグと同じ 3 桁で表示・記録する。4 桁目が 0 でない版は省略しない
    private static Version ToDisplayVersion(Version version)
        => version.Revision == 0 ? new Version(version.Major, version.Minor, version.Build) : version;
}
