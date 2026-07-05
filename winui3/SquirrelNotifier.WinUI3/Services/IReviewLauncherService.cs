// <copyright file="IReviewLauncherService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Threading;
using System.Threading.Tasks;
using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

internal interface IReviewLauncherService
{
    bool IsRunning { get; }

    Task<LauncherResult> LaunchAsync(ReviewEvent reviewEvent, LauncherRole role, CancellationToken cancellationToken);

    void Cancel();

    string BuildCommandLine(ReviewEvent reviewEvent, LauncherRole role);
}
