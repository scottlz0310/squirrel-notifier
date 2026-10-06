// <copyright file="LauncherExecutionSettings.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace SquirrelNotifier.WinUI3.Models;

internal sealed record LauncherExecutionSettings(string? Model, string? Effort, string ModelSource, string EffortSource, IReadOnlyList<string> Errors);
