// <copyright file="ExternalProcessStartInfoFactory.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Diagnostics;
using System.Text;

namespace SquirrelNotifier.WinUI3.Helpers;

/// <summary>
/// 外部 CLI を安全に起動するための <see cref="ProcessStartInfo"/> を組み立てる.
/// </summary>
/// <remarks>
/// ネイティブ実行形式は <see cref="ProcessStartInfo.ArgumentList"/> を使う。
/// <c>.cmd</c> / <c>.bat</c> は <c>cmd.exe</c> へ明示的に委譲し、引数を環境変数経由で
/// 一度だけ展開する。これにより、shell script shim のメタ文字を command line の再解釈へ
/// 渡さずに、subscriber と launcher で同じ起動契約を共有できる.
/// </remarks>
internal static class ExternalProcessStartInfoFactory
{
    internal const string CommandEnvironmentVariable = "SQUIRREL_NOTIFIER_LAUNCHER_COMMAND";
    internal const string ArgumentEnvironmentVariablePrefix = "SQUIRREL_NOTIFIER_LAUNCHER_ARG_";

    private static readonly string[] _shellScriptExtensions = [".cmd", ".bat"];

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
            startInfo.Environment[variableName] = EscapeTrailingBackslashes(argument);
            command.Append(" \"").Append('%').Append(variableName).Append("%\"");
        }

        startInfo.Arguments = $"/d /s /v:off /c \"{command}\"";
        return startInfo;
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
