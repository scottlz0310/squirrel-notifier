// <copyright file="ProcessRunner.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace SquirrelNotifier.WinUI3.Services;

[ExcludeFromCodeCoverage]
internal sealed class ProcessRunner : IProcessRunner
{
    public IProcessInstance Start(ProcessStartInfo startInfo)
    {
        var process = new Process { StartInfo = startInfo };
        process.Start();
        return new ProcessInstance(process);
    }
}

[ExcludeFromCodeCoverage]
internal sealed class ProcessInstance : IProcessInstance
{
    private readonly Process _process;

    public ProcessInstance(Process process)
    {
        _process = process;
    }

    public int ExitCode => _process.ExitCode;

    public StreamReader StandardOutput => _process.StandardOutput;

    public StreamReader StandardError => _process.StandardError;

    public void Kill(bool entireProcessTree)
    {
        _process.Kill(entireProcessTree);
    }

    public Task WaitForExitAsync(CancellationToken cancellationToken)
    {
        return _process.WaitForExitAsync(cancellationToken);
    }

    public void Dispose()
    {
        _process.Dispose();
    }
}
