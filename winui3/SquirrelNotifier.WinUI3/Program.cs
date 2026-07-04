// <copyright file="Program.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using SquirrelNotifier.WinUI3.Services;

namespace SquirrelNotifier.WinUI3;

[ExcludeFromCodeCoverage]
internal static class Program
{
    // アプリの二重起動判定に使う固定キー。値そのものに意味はなく、他アプリと衝突しなければよい。
    private const string _singleInstanceKey = "SquirrelNotifier-SingleInstance-4F1B8C2E";

    private static readonly object _reactivationLock = new();
    private static EventHandler<AppActivationArguments>? _reactivated;
    private static AppActivationArguments? _pendingActivation;

    internal static event EventHandler<AppActivationArguments>? Reactivated
    {
        add
        {
            AppActivationArguments? pending;
            lock (_reactivationLock)
            {
                _reactivated += value;
                pending = _pendingActivation;
                _pendingActivation = null;
            }

            // 購読開始前に届いていたリダイレクトを取りこぼさず再送する
            if (pending != null)
            {
                value?.Invoke(null, pending);
            }
        }

        remove
        {
            lock (_reactivationLock)
            {
                _reactivated -= value;
            }
        }
    }

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
            mainInstance.Activated += OnActivatedByRedirection;
            return false;
        }

        // 既に起動済みのインスタンスへアクティブ化イベントをリダイレクトし、自身は起動せず終了する
        RedirectActivationTo(activationArgs, mainInstance);
        return true;
    }

    private static void OnActivatedByRedirection(object? sender, AppActivationArguments args)
    {
        lock (_reactivationLock)
        {
            if (_reactivated == null)
            {
                // まだ購読者がいない（起動シーケンス完了前）場合は保持し、購読開始時に再送する
                _pendingActivation = args;
                return;
            }
        }

        _reactivated?.Invoke(sender, args);
    }

    // STA スレッドで RedirectActivationToAsync を直接ブロック待機すると、公式ガイダンス
    // (Redirecting but not blocking) の通りリダイレクトが失敗しうるため、実際の呼び出しは
    // 別スレッドで実行し、STA スレッドは COM を伴わない Semaphore でのみ完了を待つ。
    // https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/applifecycle/applifecycle-instancing#redirecting-but-not-blocking
    private static void RedirectActivationTo(AppActivationArguments activationArgs, AppInstance mainInstance)
    {
        using var redirectCompleted = new Semaphore(0, 1);

        _ = Task.Run(() =>
        {
            try
            {
                mainInstance.RedirectActivationToAsync(activationArgs).AsTask().Wait();
            }
            catch (Exception ex)
            {
                // リダイレクト失敗時も未処理例外で 2 個目のプロセスをクラッシュさせず終了させる
                _ = new LoggingService().WriteAsync($"[WARN] 既存インスタンスへのリダイレクトに失敗しました: {ex.Message}");
            }
            finally
            {
                redirectCompleted.Release();
            }
        });

        redirectCompleted.WaitOne();
    }
}
