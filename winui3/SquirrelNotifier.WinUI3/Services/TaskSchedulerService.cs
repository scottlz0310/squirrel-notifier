// <copyright file="TaskSchedulerService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Diagnostics;

namespace SquirrelNotifier.WinUI3.Services;

internal sealed class TaskSchedulerService : ITaskSchedulerService
{
    private const string _taskName = "Squirrel Notifier";
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
        string command = $"if (Get-ScheduledTask -TaskName '{EscapePs(_taskName)}' -ErrorAction SilentlyContinue) {{ exit 0 }} else {{ exit 1 }}";
        ProcessStartInfo psi = BuildPsi(command);

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
        string exePath = EscapePs(GetExePath());
        string command =
            $"$a = New-ScheduledTaskAction -Execute '{exePath}' -Argument '--tray'; " +
            $"$t = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME; " +
            $"$s = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -RunOnlyIfNetworkAvailable:$false -DontStopOnIdleEnd; " +
            $"$p = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Limited; " +
            $"Register-ScheduledTask -TaskName '{EscapePs(_taskName)}' -Action $a -Trigger $t -Settings $s -Principal $p -Force";

        ProcessStartInfo psi = BuildPsi(command);

        using IProcessInstance proc = _processRunner.Start(psi);
        Task<string> errorTask = proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);

        if (proc.ExitCode != 0)
        {
            string error = await errorTask.ConfigureAwait(false);
            throw new InvalidOperationException($"タスクスケジューラへの登録に失敗しました: {error}");
        }
    }

    public async Task UnregisterAsync()
    {
        string command = $"Unregister-ScheduledTask -TaskName '{EscapePs(_taskName)}' -Confirm:$false";
        ProcessStartInfo psi = BuildPsi(command);

        using IProcessInstance proc = _processRunner.Start(psi);
        Task<string> errorTask = proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);

        if (proc.ExitCode != 0)
        {
            string error = await errorTask.ConfigureAwait(false);
            throw new InvalidOperationException($"タスクスケジューラからの削除に失敗しました: {error}");
        }
    }

    public async Task RepairAsync()
    {
        TaskRegistrationStatus status = await GetStatusAsync().ConfigureAwait(false);
        if (status == TaskRegistrationStatus.Registered)
        {
            await UnregisterAsync().ConfigureAwait(false);
        }

        await RegisterAsync().ConfigureAwait(false);
    }

    internal static string GetExePath()
        => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "SquirrelNotifier.WinUI3.exe");

    private static ProcessStartInfo BuildPsi(string command)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(command);
        return psi;
    }

    // PowerShell single-quoted string escaping: ' → ''
    private static string EscapePs(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}
