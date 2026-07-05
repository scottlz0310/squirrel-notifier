// <copyright file="CommandLineFormatter.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.Text;

namespace SquirrelNotifier.WinUI3.Helpers;

/// <summary>
/// コマンドパスと展開済み引数リストを、ターミナルへ貼り付けて実行できる 1 行のコマンド文字列へ整形する.
/// 表示・コピー用途のみで、実際のプロセス起動には <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/> を使う.
/// </summary>
internal static class CommandLineFormatter
{
    private static readonly char[] _charsRequiringQuotes = [' ', '\t', '"'];

    public static string Format(string commandPath, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var builder = new StringBuilder();
        builder.Append(QuoteIfNeeded(commandPath));

        foreach (string argument in arguments)
        {
            builder.Append(' ').Append(QuoteIfNeeded(argument));
        }

        return builder.ToString();
    }

    private static string QuoteIfNeeded(string value)
    {
        if (value.Length > 0 && value.IndexOfAny(_charsRequiringQuotes) < 0)
        {
            return value;
        }

        // cmd.exe / PowerShell の双方で、貼り付け後に単一引数として再解釈されるのは
        // "" (ダブルクォート二重化) のみ。CRT 形式の \" エスケープは PowerShell の
        // トークナイザーでは認識されず、引数が分裂する（実機検証済み）。
        string escaped = value.Replace("\"", "\"\"", StringComparison.Ordinal);
        return $"\"{escaped}\"";
    }
}
