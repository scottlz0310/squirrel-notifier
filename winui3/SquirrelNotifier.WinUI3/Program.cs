// <copyright file="Program.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Diagnostics.CodeAnalysis;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace SquirrelNotifier.WinUI3;

[ExcludeFromCodeCoverage]
internal static class Program
{
    // アプリの二重起動判定に使う固定キー。値そのものに意味はなく、他アプリと衝突しなければよい。
    private const string _singleInstanceKey = "SquirrelNotifier-SingleInstance-4F1B8C2E";

    internal static event EventHandler<AppActivationArguments>? Reactivated;

    [STAThread]
    private static void Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        if (DecideRedirection())
        {
            return;
        }

        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
    }

    private static bool DecideRedirection()
    {
        AppActivationArguments activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
        AppInstance mainInstance = AppInstance.FindOrRegisterForKey(_singleInstanceKey);

        if (mainInstance.IsCurrent)
        {
            mainInstance.Activated += (sender, args) => Reactivated?.Invoke(sender, args);
            return false;
        }

        // 既に起動済みのインスタンスへアクティブ化イベントをリダイレクトし、自身は起動せず終了する
        mainInstance.RedirectActivationToAsync(activationArgs).AsTask().GetAwaiter().GetResult();
        return true;
    }
}
