// <copyright file="AgentProcessStartInfoFactory.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Diagnostics;

namespace SquirrelNotifier.WinUI3.Helpers;

/// <summary>
/// AI エージェント起動用の <see cref="ProcessStartInfo"/> を組み立てる共通 factory（#186）。
/// ネイティブ実行形式は <see cref="ProcessStartInfo.ArgumentList"/> でそのまま起動する。
/// <c>.cmd</c> / <c>.bat</c> はネイティブ実行形式ではなく、<c>CreateProcessW</c> へ直接渡すと
/// OS が暗黙に <c>cmd.exe</c> を挟むが、その経路は <see cref="ProcessStartInfo.ArgumentList"/> の
/// 引用規約と cmd.exe のパース規則が一致せず、引数内のメタ文字が再解釈されうる
/// （いわゆる BatBadBut パターン）。そのため明示的に <c>cmd.exe /d /s /v:off /c</c> でラップし、
/// 実行パスと各引数は環境変数を引用符内で一度だけ展開する方式（#177）で渡す。
/// cmd.exe の変数展開は単一パスであり展開結果を再解釈しないため、値に含まれる
/// <c>%</c> / <c>&amp;</c> / <c>|</c> 等のメタ文字が安全に素通しされる。
/// 引用符・改行のみは環境変数方式でも引用状態を破壊しうるため、明示エラーで拒否する.
/// </summary>
internal static class AgentProcessStartInfoFactory
{
    internal const string CommandEnvironmentVariable = ExternalProcessStartInfoFactory.CommandEnvironmentVariable;
    internal const string ArgumentEnvironmentVariablePrefix = ExternalProcessStartInfoFactory.ArgumentEnvironmentVariablePrefix;

    public static ProcessStartInfo Create(string resolvedPath, IReadOnlyList<string> arguments)
        => ExternalProcessStartInfoFactory.Create(resolvedPath, arguments);
}
