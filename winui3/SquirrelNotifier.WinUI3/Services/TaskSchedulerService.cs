// <copyright file="TaskSchedulerService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Diagnostics;

namespace SquirrelNotifier.WinUI3.Services;

internal sealed class TaskSchedulerService : ITaskSchedulerService
{
    private const string _taskName = "SquirrelNotifier";
    private readonly IProcessRunner _processRunner;

    public TaskSchedulerService()
        : this(new ProcessRunner())
    {
    }

    internal TaskSchedulerService(IProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }

    public async Task<TaskRegistrationStatus> GetStatusAsync()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = $"/Query /TN \"{_taskName}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        try
        {
            using IProcessInstance proc = _processRunner.Start(psi);
            await proc.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            return proc.ExitCode == 0 ? TaskRegistrationStatus.Registered : TaskRegistrationStatus.NotRegistered;
        }
        catch
        {
            return TaskRegistrationStatus.NotRegistered;
        }
    }

    public async Task RegisterAsync()
    {
        string exePath = GetExePath();
        var psi = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = $"/Create /TN \"{_taskName}\" /TR \"\\\"{exePath}\\\" --tray\" /SC ONLOGON /RL LIMITED /F",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using IProcessInstance proc = _processRunner.Start(psi);
        await proc.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);

        if (proc.ExitCode != 0)
        {
            string error = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"タスクスケジューラへの登録に失敗しました: {error}");
        }
    }

    public async Task UnregisterAsync()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = $"/Delete /TN \"{_taskName}\" /F",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using IProcessInstance proc = _processRunner.Start(psi);
        await proc.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
    }

    public async Task RepairAsync()
    {
        await UnregisterAsync().ConfigureAwait(false);
        await RegisterAsync().ConfigureAwait(false);
    }

    private static string GetExePath()
        => Path.Combine(AppContext.BaseDirectory, "SquirrelNotifier.WinUI3.exe");
}
