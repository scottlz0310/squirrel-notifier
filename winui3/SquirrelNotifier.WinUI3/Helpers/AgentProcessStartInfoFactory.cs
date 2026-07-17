// <copyright file="AgentProcessStartInfoFactory.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Diagnostics;
using System.Linq;
using System.Text;

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
    internal const string CommandEnvironmentVariable = "SQUIRREL_NOTIFIER_LAUNCHER_COMMAND";
    internal const string ArgumentEnvironmentVariablePrefix = "SQUIRREL_NOTIFIER_LAUNCHER_ARG_";

    private static readonly string[] _shellScriptExtensions = [".cmd", ".bat"];

    /// <summary>
    /// 解決済み実行パスと引数リストから、リダイレクト設定済みの <see cref="ProcessStartInfo"/> を作る。
    /// WorkingDirectory・エンコーディング等の呼び出し元固有の設定は戻り値に対して行う.
    /// </summary>
    /// <param name="resolvedPath">解決済みの実行ファイルパス、または未解決のコマンド名.</param>
    /// <param name="arguments">エージェントへ渡す引数（テンプレート展開済み）.</param>
    /// <returns>起動可能な <see cref="ProcessStartInfo"/>.</returns>
    /// <exception cref="ArgumentException">
    /// <c>.cmd</c> / <c>.bat</c> ラップ時に、引用符または改行を含む引数が指定された場合。
    /// これらは cmd.exe の引用状態を破壊しコマンド注入につながるため安全に渡せない.
    /// </exception>
    public static ProcessStartInfo Create(string resolvedPath, IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resolvedPath);
        ArgumentNullException.ThrowIfNull(arguments);

        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        if (!_shellScriptExtensions.Contains(Path.GetExtension(resolvedPath), StringComparer.OrdinalIgnoreCase))
        {
            startInfo.FileName = resolvedPath;
            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            return startInfo;
        }

        // /d: AutoRun 無効、/s: 先頭・末尾の引用符解釈を固定、/v:off: 遅延展開（!var!）を
        // レジストリ設定に関わらず無効化。展開結果の ! が再解釈されないことを保証する
        startInfo.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        startInfo.Environment[CommandEnvironmentVariable] = resolvedPath;

        var command = new StringBuilder($"\"%{CommandEnvironmentVariable}%\"");
        for (int i = 0; i < arguments.Count; i++)
        {
            string argument = arguments[i];
            if (argument.AsSpan().ContainsAny('"', '\r', '\n'))
            {
                throw new ArgumentException(
                    $"Argument #{i} cannot be passed safely to a .cmd/.bat shim because it contains a double quote or a line break: {argument}",
                    nameof(arguments));
            }

            if (argument.Length == 0)
            {
                command.Append(" \"\"");
                continue;
            }

            // cmd.exe は空の環境変数を保持できず、未定義変数の %NAME% は文字列のまま残る。
            // 空引数は上のリテラル "" で渡し、環境変数には非空値のみを入れる
            string variableName = ArgumentEnvironmentVariablePrefix + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            startInfo.Environment[variableName] = EscapeTrailingBackslashes(argument);
            command.Append(" \"%").Append(variableName).Append("%\"");
        }

        startInfo.Arguments = $"/d /s /v:off /c \"{command}\"";
        return startInfo;
    }

    // 展開後の値は引用符で囲まれるため、閉じ引用符直前のバックスラッシュ列は
    // CommandLineToArgvW 規約でエスケープ扱いになる。末尾のバックスラッシュを
    // 二重化して、受け側が元の値どおりに復元できるようにする
    private static string EscapeTrailingBackslashes(string value)
    {
        int trailing = 0;
        while (trailing < value.Length && value[value.Length - 1 - trailing] == '\\')
        {
            trailing++;
        }

        return trailing == 0 ? value : value + new string('\\', trailing);
    }
}
