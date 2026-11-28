// <copyright file="TaskSchedulerService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Diagnostics;

namespace SquirrelNotifier.WinUI3.Services;

internal sealed class TaskSchedulerService : IRepairableAutoStartService
{
    private const string _taskName = "Squirrel Notifier";
    private const int _notRegisteredExitCode = 1;
    private const int _invalidExitCode = 2;
    private const int _checkFailedExitCode = 3;
    private readonly IProcessRunner _processRunner;

    public TaskSchedulerService()
        : this(new ProcessRunner())
    {
    }

    internal TaskSchedulerService(IProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }

    public string EnableConfirmationMessage
        => $"以下の内容でタスクスケジューラへ登録します\n\n　タスク名: {_taskName}\n　実行ファイル: {GetExePath()}\n　引数: --tray\n　トリガー: ログオン時（現在のユーザー）";

    public string DisableConfirmationMessage => "自動起動タスクを削除します。よろしいですか？";

    public async Task<AutoStartStatus> GetStatusAsync()
    {
        string expectedExePath = EscapePs(GetExePath());
        string command =
            $"try {{ $task = Get-ScheduledTask -TaskName '{EscapePs(_taskName)}' -ErrorAction SilentlyContinue; " +
            $"if ($null -eq $task) {{ exit {_notRegisteredExitCode} }}; " +
            $"$action = $task.Actions | Select-Object -First 1; " +
            $"if ($null -ne $action -and $action.Execute -eq '{expectedExePath}' -and $action.Arguments -eq '--tray') " +
            $"{{ exit 0 }} else {{ exit {_invalidExitCode} }} }} catch {{ exit {_checkFailedExitCode} }}";
        ProcessStartInfo psi = BuildPsi(command);

        try
        {
            using IProcessInstance proc = _processRunner.Start(psi);
            Task<string> errorTask = proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await errorTask.ConfigureAwait(false);
            return proc.ExitCode switch
            {
                0 => AutoStartStatus.Enabled,
                _notRegisteredExitCode => AutoStartStatus.Disabled,
                _invalidExitCode => AutoStartStatus.Invalid,
                _ => AutoStartStatus.CheckFailed,
            };
        }
        catch
        {
            return AutoStartStatus.CheckFailed;
        }
    }

    public async Task EnableAsync()
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

    public async Task DisableAsync()
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
        AutoStartStatus status = await GetStatusAsync().ConfigureAwait(false);
        if (status is AutoStartStatus.Enabled or AutoStartStatus.Invalid)
        {
            await DisableAsync().ConfigureAwait(false);
        }

        await EnableAsync().ConfigureAwait(false);
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
