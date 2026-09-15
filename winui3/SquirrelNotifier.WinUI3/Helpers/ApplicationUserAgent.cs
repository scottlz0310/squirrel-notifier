// <copyright file="ApplicationUserAgent.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Net.Http.Headers;

namespace SquirrelNotifier.WinUI3.Helpers;

/// <summary>
/// HTTP クライアントが送る既定の User-Agent を、実行中アセンブリの version から組み立てる.
/// </summary>
internal static class ApplicationUserAgent
{
    private const string _productName = "Squirrel-Notifier-WinUI3";

    /// <summary>
    /// 呼出元が User-Agent を設定していない場合だけ、既定の User-Agent を追加する.
    /// </summary>
    /// <param name="httpClient">User-Agent を設定する HTTP クライアント.</param>
    public static void AddDefaultIfMissing(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        if (httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            httpClient.DefaultRequestHeaders.UserAgent.Add(Create(GetAssemblyVersion()));
        }
    }

    /// <summary>
    /// 指定した version の major.minor.build を持つ User-Agent を作る.
    /// </summary>
    /// <param name="version">User-Agent に載せるアセンブリの version.</param>
    /// <returns>製品名と version を持つ User-Agent.</returns>
    internal static ProductInfoHeaderValue Create(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return new ProductInfoHeaderValue(_productName, version.ToString(3));
    }

    private static Version GetAssemblyVersion()
    {
        return typeof(ApplicationUserAgent).Assembly.GetName().Version
            ?? throw new InvalidOperationException("アセンブリの version を取得できないため、User-Agent を組み立てられません。");
    }
}
