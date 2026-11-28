// <copyright file="IPackageUpdateAccessor.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>導入済みのパッケージの更新に関わる Windows API への境界（#569）.</summary>
internal interface IPackageUpdateAccessor
{
    /// <summary>Gets 実行中のパッケージの版.</summary>
    Version CurrentVersion { get; }

    /// <summary>Gets 導入に使った .appinstaller の URI。.appinstaller 以外で導入した場合は <see langword="null"/>.</summary>
    Uri? AppInstallerUri { get; }

    /// <summary>
    /// 更新後の再起動を予約して、更新を適用する。適用できる場合は、このプロセスが終了するため戻らない.
    /// </summary>
    /// <param name="appInstallerUri">適用する .appinstaller の URI.</param>
    /// <param name="restartArguments">再起動するプロセスに渡す引数。引数が無い場合は空文字.</param>
    /// <returns>プロセスが終了しなかった場合に完了する Task.</returns>
    Task ApplyAsync(Uri appInstallerUri, string restartArguments);
}
