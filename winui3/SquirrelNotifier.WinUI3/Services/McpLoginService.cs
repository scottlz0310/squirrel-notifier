// <copyright file="McpLoginService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SquirrelNotifier.WinUI3.Helpers;
using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>
/// 設定済みの mcp-resource-subscriber を <c>--login --url &lt;gateway&gt;</c> で起動し、
/// mcp-gateway の初回認証（RFC 8628 device flow）をアプリ内から開始する（#183）。
/// OAuth / device flow / token refresh 自体は subscriber が担当し、Squirrel Notifier は
/// 「設定済み外部 CLI の安全な起動・状態表示・ブラウザ導線・再購読」に責務を限定する。
/// アクセストークン・リフレッシュトークン・Authorization ヘッダーを設定・ログ・
/// コマンドライン引数へ出力しない.
/// </summary>
internal sealed class McpLoginService
{
    // 承認待ちはユーザーがブラウザ操作を終えるまで数分かかりうるため、通知 timeout とは
    // 別に既定 5 分を確保する。テストは短い値を注入する.
    private const int _defaultLoginTimeoutMs = 300000;

    // --login は subscriber v0.3.0（#102）で追加された。これより古いバージョンは --login を
    // 未知フラグとして無視し、既定の subscribe モードへフォールバックしてしまい失敗が分かりにくい。
    private static readonly Version _minimumSubscriberVersion = new(0, 3, 0);
    private static readonly Regex _versionRegex = new(@"v(\d+)\.(\d+)\.(\d+)", RegexOptions.Compiled);

    private readonly SettingsService _settingsService;
    private readonly LoggingService _loggingService;
    private readonly IProcessRunner _processRunner;
    private readonly IUrlOpener _urlOpener;
    private readonly int _loginTimeoutMs;

    /// <summary>進行状況を表す短いテキスト（UI 表示用）。トークン・URL・code は含まない.</summary>
    public event EventHandler<string>? StatusChanged;

    /// <summary>verification URI / user code を受信したときに発火。ブラウザ起動可否も伝える.</summary>
    public event EventHandler<DeviceVerificationInfo>? VerificationReceived;

    public McpLoginService(
        SettingsService settingsService,
        LoggingService loggingService,
        IProcessRunner? processRunner = null,
        IUrlOpener? urlOpener = null,
        int loginTimeoutMs = _defaultLoginTimeoutMs)
    {
        _settingsService = settingsService;
        _loggingService = loggingService;
        _processRunner = processRunner ?? new ProcessRunner();
        _urlOpener = urlOpener ?? new UrlOpener();
        _loginTimeoutMs = loginTimeoutMs;
    }

    public async Task<McpLoginResult> LoginAsync(CancellationToken cancellationToken)
    {
        AppSettings settings = _settingsService.Settings;
        string resolvedPath = SettingsService.ResolveCommandPath(settings.SubscriberCommandPath);

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_loginTimeoutMs);

        IProcessInstance? process = null;
        try
        {
            string? versionError = await CheckSubscriberVersionAsync(resolvedPath, cts.Token).ConfigureAwait(false);
            if (versionError != null)
            {
                return new McpLoginResult { Outcome = McpLoginOutcome.Failed, ErrorMessage = versionError };
            }

            var psi = new ProcessStartInfo
            {
                FileName = resolvedPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add("--login");
            psi.ArgumentList.Add("--url");
            psi.ArgumentList.Add(settings.GatewayUrl);

            RaiseStatus("mcp-gateway に接続し、device flow 認証を開始しています...");
            await LogAsync("mcp-gateway login started.").ConfigureAwait(false);

            process = _processRunner.Start(psi);

            Task<string> stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

            (bool sawSuccess, bool sawFailed, string? errorCode) = await PumpLoginStdoutAsync(process, cts.Token).ConfigureAwait(false);

            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            string stderr = await stderrTask.ConfigureAwait(false);
            int exitCode = process.ExitCode;

            await LogAsync($"mcp-gateway login finished. ExitCode={exitCode}.").ConfigureAwait(false);

            return BuildResult(exitCode, sawSuccess, sawFailed, errorCode, stderr);
        }
        catch (OperationCanceledException)
        {
            KillProcess(process);

            if (cancellationToken.IsCancellationRequested)
            {
                await LogAsync("mcp-gateway login was cancelled by user.").ConfigureAwait(false);
                return new McpLoginResult { Outcome = McpLoginOutcome.Cancelled, ErrorMessage = "認証がキャンセルされました。" };
            }

            await LogAsync("mcp-gateway login timed out while waiting for approval.").ConfigureAwait(false);
            return new McpLoginResult
            {
                Outcome = McpLoginOutcome.TimedOut,
                ErrorMessage = "認証が時間内に完了しませんでした。ブラウザで承認を完了してから、もう一度お試しください。",
            };
        }
        catch (Exception ex)
        {
            KillProcess(process);
            await LogAsync($"mcp-gateway login failed to start: {ex.Message}").ConfigureAwait(false);
            return new McpLoginResult
            {
                Outcome = McpLoginOutcome.Failed,
                ErrorMessage = $"mcp-resource-subscriber の起動に失敗しました。インストール状況と Command Path 設定を確認してください: {ex.Message}",
            };
        }
        finally
        {
            process?.Dispose();
        }
    }

    // stdout を 1 行ずつ読み、device flow の進行を UI へ反映する。verification URI を受信した
    // 時点でブラウザを起動する（承認待ちで stdout がブロックする前に確実に開くため）。
    private async Task<(bool SawSuccess, bool SawFailed, string? ErrorCode)> PumpLoginStdoutAsync(
        IProcessInstance process, CancellationToken cancellationToken)
    {
        bool sawSuccess = false;
        bool sawFailed = false;
        string? errorCode = null;

        string userCode = string.Empty;
        string verificationUri = string.Empty;
        string? verificationUriComplete = null;
        bool browserOpened = false;
        bool browserOpenAttempted = false;

        while (await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false) is string line)
        {
            DeviceLoginSignal signal = DeviceLoginOutputParser.Parse(line);
            switch (signal.Kind)
            {
                case DeviceLoginSignalKind.UserCode:
                    userCode = signal.Value;
                    break;

                case DeviceLoginSignalKind.VerificationUri:
                    verificationUri = signal.Value;

                    // 悪意ある／侵害された gateway は verification_uri に ms-settings: 等の任意 scheme を
                    // 返しうる。UseShellExecute=true の自動起動で OS protocol handler を誤起動しないよう、
                    // http / https の absolute URI に限って自動でブラウザを開く（#183 セキュリティレビュー）。
                    bool schemeAllowed = UrlValidator.IsHttpOrHttpsAbsoluteUrl(verificationUri);

                    // verification-uri は complete より先に出力されるため、ここで一度だけ
                    // ブラウザを起動する（承認待ちのブロック前に開く）。complete が後続しても
                    // 二重にタブを開かない。UI には complete 受信時に更新情報を再送する.
                    browserOpened = schemeAllowed && _urlOpener.TryOpen(verificationUri);
                    browserOpenAttempted = true;

                    if (!schemeAllowed)
                    {
                        await LogAsync("Rejected non-http(s) verification URI scheme; browser auto-open skipped.").ConfigureAwait(false);
                        RaiseStatus("gateway が予期しない形式の URL を返したため、自動でブラウザを開きませんでした。承認 URL をご確認ください。");
                    }
                    else
                    {
                        await LogAsync($"Device authorization received; browser open attempted (opened={browserOpened}).").ConfigureAwait(false);
                        RaiseStatus(browserOpened
                            ? "ブラウザで認証ページを開きました。表示されたコードで承認してください。承認待ち..."
                            : "ブラウザを自動で開けませんでした。表示された URL とコードで手動承認してください。承認待ち...");
                    }

                    RaiseVerification(verificationUri, verificationUriComplete, userCode, browserOpened);
                    break;

                case DeviceLoginSignalKind.VerificationUriComplete:
                    verificationUriComplete = signal.Value;
                    if (browserOpenAttempted)
                    {
                        RaiseVerification(verificationUri, verificationUriComplete, userCode, browserOpened);
                    }

                    break;

                case DeviceLoginSignalKind.StatusSuccess:
                    sawSuccess = true;
                    break;

                case DeviceLoginSignalKind.StatusFailed:
                    sawFailed = true;
                    break;

                case DeviceLoginSignalKind.ErrorCode:
                    errorCode = signal.Value;
                    break;

                case DeviceLoginSignalKind.Ignored:
                default:
                    break;
            }
        }

        return (sawSuccess, sawFailed, errorCode);
    }

    private McpLoginResult BuildResult(int exitCode, bool sawSuccess, bool sawFailed, string? errorCode, string stderr)
    {
        if (exitCode == 0 && sawSuccess)
        {
            return new McpLoginResult { Outcome = McpLoginOutcome.Succeeded };
        }

        // subscriber は失敗理由を stderr の "login failed: <message>" に出力する。
        // トークンは含まれないが、gateway 到達不可・device flow 拒否等の診断に有用.
        string detail = ExtractFailureDetail(stderr);

        string message = errorCode switch
        {
            "SERVER_URL_UNKNOWN" => "Gateway URL が設定されていません。先に Gateway URL を設定してください。",
            _ when !string.IsNullOrWhiteSpace(detail) => $"mcp-gateway へのログインに失敗しました: {detail}",
            _ => "mcp-gateway へのログインに失敗しました。Gateway URL や mcp-gateway コンテナの起動状態を確認してください。",
        };

        _ = sawFailed;
        return new McpLoginResult
        {
            Outcome = McpLoginOutcome.Failed,
            ErrorMessage = message,
            ErrorCode = errorCode,
        };
    }

    private static string ExtractFailureDetail(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
        {
            return string.Empty;
        }

        foreach (string rawLine in stderr.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            const string prefix = "login failed: ";
            if (rawLine.StartsWith(prefix, StringComparison.Ordinal))
            {
                return rawLine[prefix.Length..].Trim();
            }
        }

        return string.Empty;
    }

    // mcp-resource-subscriber の --version を確認し、--login 非対応の古いバージョンを事前に検出する。
    // 問題なければ null、問題があれば案内メッセージを返す（EnqueueReviewService と同方針）。
    private async Task<string?> CheckSubscriberVersionAsync(string resolvedPath, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = resolvedPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("--version");

        IProcessInstance? process = null;
        try
        {
            process = _processRunner.Start(psi);

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            string stdout = await stdoutTask.ConfigureAwait(false);

            Match match = _versionRegex.Match(stdout);
            if (!match.Success)
            {
                return $"mcp-resource-subscriber のバージョンを確認できませんでした（--version の出力: \"{stdout.Trim()}\"）。--login には v{_minimumSubscriberVersion} 以上が必要です。";
            }

            var detected = new Version(
                int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture));

            if (detected < _minimumSubscriberVersion)
            {
                return $"mcp-resource-subscriber のバージョンが古いため、--login を実行できません（検出: v{detected}, 必要: v{_minimumSubscriberVersion} 以上）。mcp-resource-subscriber を更新してください。";
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            KillProcess(process);
            throw;
        }
        catch (Exception ex)
        {
            return $"mcp-resource-subscriber のバージョン確認に失敗しました。インストール状況と Command Path 設定を確認してください: {ex.Message}";
        }
        finally
        {
            process?.Dispose();
        }
    }

    private void RaiseStatus(string message)
    {
        StatusChanged?.Invoke(this, message);
    }

    private void RaiseVerification(string verificationUri, string? verificationUriComplete, string userCode, bool browserOpened)
    {
        VerificationReceived?.Invoke(this, new DeviceVerificationInfo
        {
            VerificationUri = verificationUri,
            VerificationUriComplete = verificationUriComplete,
            UserCode = userCode,
            BrowserOpened = browserOpened,
        });
    }

    private static void KillProcess(IProcessInstance? process)
    {
        if (process == null)
        {
            return;
        }

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Process already exited before Kill() could run
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // OS-level failure to terminate the process; nothing more we can do here
        }
    }

    private async Task LogAsync(string message)
    {
        await _loggingService.WriteAsync(message).ConfigureAwait(false);
    }
}
