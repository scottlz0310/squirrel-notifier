// <copyright file="IProcessRunner.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Diagnostics;

namespace SquirrelNotifier.WinUI3.Services;

internal interface IProcessRunner
{
    IProcessInstance Start(ProcessStartInfo startInfo);
}

internal interface IProcessInstance : IDisposable
{
    int ExitCode { get; }

    StreamReader StandardOutput { get; }

    StreamReader StandardError { get; }

    StreamWriter StandardInput { get; }

    void Kill(bool entireProcessTree);

    Task WaitForExitAsync(CancellationToken cancellationToken);
}
