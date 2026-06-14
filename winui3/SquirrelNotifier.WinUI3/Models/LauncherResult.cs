// <copyright file="LauncherResult.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SquirrelNotifier.WinUI3.Models;

internal sealed class LauncherResult
{
    public bool Success { get; set; }

    public int? ExitCode { get; set; }

    public string Stdout { get; set; } = string.Empty;

    public string Stderr { get; set; } = string.Empty;

    public string ErrorMessage { get; set; } = string.Empty;
}
