// <copyright file="WindowsPackageUpdateAccessor.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace SquirrelNotifier.WinUI3.Services;

// パッケージ ID を持つプロセスでだけ動くため、テストプロセスからは実行できない
[ExcludeFromCodeCoverage]
internal sealed class WindowsPackageUpdateAccessor : IPackageUpdateAccessor
{
    public Version CurrentVersion
    {
        get
        {
            PackageVersion version = Package.Current.Id.Version;
            return new Version(version.Major, version.Minor, version.Build, version.Revision);
        }
    }

    public Uri? AppInstallerUri => Package.Current.GetAppInstallerInfo()?.Uri;

    public async Task ApplyAsync(Uri appInstallerUri, string restartArguments)
    {
        // 適用で終了させられた後に起動し直すには、適用の前に予約する。起動から 60 秒未満では再起動されない
        int result = RegisterApplicationRestart(restartArguments.Length == 0 ? null : restartArguments, 0);
        if (result != 0)
        {
            throw new InvalidOperationException($"再起動の予約に失敗しました（0x{result:X8}）。");
        }

        try
        {
            // .appinstaller の URI を指定すると、.appinstaller の関連付けが維持される
            DeploymentResult deployment = await new PackageManager().AddPackageByUriAsync(
                appInstallerUri,
                new AddPackageOptions { ForceTargetAppShutdown = true });
            if (!deployment.IsRegistered)
            {
                throw new InvalidOperationException(
                    $"更新を登録できませんでした（0x{deployment.ExtendedErrorCode?.HResult ?? 0:X8}）: {deployment.ErrorText}");
            }
        }
        finally
        {
            // ここへ戻るのは終了させられなかった場合だけ。後の異常終了で起動し直さないように取り消す
            _ = UnregisterApplicationRestart();
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterApplicationRestart(string? commandLine, int flags);

    [DllImport("kernel32.dll")]
    private static extern int UnregisterApplicationRestart();
}
