// <copyright file="ExternalProcessStartInfoFactory.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Diagnostics;
using System.Text;

namespace SquirrelNotifier.WinUI3.Helpers;

/// <summary>
/// shell script shim に渡す引数の引用ポリシー.
/// </summary>
internal enum ShellScriptArgumentQuotingPolicy
{
    /// <summary>
    /// 既存の agent/Codex 起動契約どおり、空でない引数を引用符で囲む.
    /// </summary>
    QuoteAllArguments,

    /// <summary>
    /// 安全なトークンだけ引用符なしで渡し、batch の %1 契約を維持する.
    /// </summary>
    PreserveUnquotedSafeArguments,
}

/// <summary>
/// 外部 CLI を安全に起動するための <see cref="ProcessStartInfo"/> を組み立てる.
/// </summary>
/// <remarks>
/// ネイティブ実行形式は <see cref="ProcessStartInfo.ArgumentList"/> を使う。
/// <c>.cmd</c> / <c>.bat</c> は <c>cmd.exe</c> へ明示的に委譲し、引数を環境変数経由で
/// 一度だけ展開する。既定では引数を引用符内で渡し、呼び出し元が選択した場合だけ
/// 引用符なしで安全に渡せる引数を通常の batch shim 契約に合わせて展開する.
/// </remarks>
internal static class ExternalProcessStartInfoFactory
{
    internal const string CommandEnvironmentVariable = "SQUIRREL_NOTIFIER_LAUNCHER_COMMAND";
    internal const string ArgumentEnvironmentVariablePrefix = "SQUIRREL_NOTIFIER_LAUNCHER_ARG_";

    private static readonly string[] _shellScriptExtensions = [".cmd", ".bat"];

    public static ProcessStartInfo Create(
        string resolvedPath,
        IReadOnlyList<string> arguments,
        bool redirectStandardInput = true,
        ShellScriptArgumentQuotingPolicy quotingPolicy = ShellScriptArgumentQuotingPolicy.QuoteAllArguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resolvedPath);
        ArgumentNullException.ThrowIfNull(arguments);

        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = redirectStandardInput,
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

        startInfo.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        startInfo.Environment[CommandEnvironmentVariable] = resolvedPath;

        var command = new StringBuilder($"%{CommandEnvironmentVariable}%");
        command.Insert(0, '"');
        command.Append('"');

        for (int index = 0; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if (argument.AsSpan().ContainsAny('"', '\r', '\n'))
            {
                throw new ArgumentException(
                    $"Argument #{index} cannot be passed safely to a .cmd/.bat shim because it contains a double quote or a line break.",
                    nameof(arguments));
            }

            if (argument.Length == 0)
            {
                command.Append(" \"\"");
                continue;
            }

            string variableName = ArgumentEnvironmentVariablePrefix + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            bool canPassWithoutQuotes = quotingPolicy == ShellScriptArgumentQuotingPolicy.PreserveUnquotedSafeArguments
                && CanPassWithoutQuotes(argument);
            startInfo.Environment[variableName] = canPassWithoutQuotes
                ? argument
                : EscapeTrailingBackslashes(argument);
            if (canPassWithoutQuotes)
            {
                command.Append(' ').Append('%').Append(variableName).Append('%');
            }
            else
            {
                command.Append(" \"").Append('%').Append(variableName).Append("%\"");
            }
        }

        startInfo.Arguments = $"/d /s /v:off /c \"{command}\"";
        return startInfo;
    }

    private static bool CanPassWithoutQuotes(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        foreach (char character in value)
        {
            if ((character >= 'a' && character <= 'z')
                || (character >= 'A' && character <= 'Z')
                || (character >= '0' && character <= '9')
                || character is '-' or '_' or '.' or ':' or '/' or '\\')
            {
                continue;
            }

            return false;
        }

        return true;
    }

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
