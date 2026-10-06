// <copyright file="GhApiClient.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SquirrelNotifier.WinUI3.Helpers;

namespace SquirrelNotifier.WinUI3.Services;

/// <summary>
/// <c>gh api</c> を <see cref="IProcessRunner"/> 経由で実行し、GitHub REST API を読み取る（#456）。
/// 認証は <c>gh</c> のログイン状態に従う。タスクスケジューラから起動したトレイアプリでも、
/// 同じユーザーのログオンセッションで動くため、PATH と資格情報ストアをそのまま使える.
/// </summary>
internal sealed class GhApiClient : IGhApiClient
{
    private const string _command = "gh";
    private static readonly TimeSpan _defaultTimeout = TimeSpan.FromSeconds(20);

    // gh はエラー時に "gh: Not Found (HTTP 404)" のように stderr へ HTTP ステータスを出す
    private static readonly Regex _httpStatusPattern = new(@"\(HTTP (?<status>\d{3})\)", RegexOptions.Compiled);

    private readonly IProcessRunner _processRunner;
    private readonly Func<string, string?> _resolveCommand;
    private readonly TimeSpan _timeout;
    private readonly SecretMasker _secretMasker;
    private readonly bool _includeResponseHeaders;

    public GhApiClient(
        IProcessRunner? processRunner = null,
        Func<string, string?>? resolveCommand = null,
        TimeSpan? timeout = null,
        SecretMasker? secretMasker = null,
        bool includeResponseHeaders = false)
    {
        TimeSpan resolvedTimeout = timeout ?? _defaultTimeout;
        if (resolvedTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "gh のタイムアウトは正の値である必要があります。");
        }

        _processRunner = processRunner ?? new ProcessRunner();
        _resolveCommand = resolveCommand ?? (command => CommandPathResolver.Resolve(command));
        _timeout = resolvedTimeout;
        _secretMasker = secretMasker ?? SecretMasker.CreateDefault();
        _includeResponseHeaders = includeResponseHeaders;
    }

    public async Task<GhApiResult> GetAsync(string path, string jq, bool paginate, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(jq);

        string? resolvedPath = _resolveCommand(_command);
        if (resolvedPath is null)
        {
            return GhApiResult.Failure(null, "gh コマンドが見つかりません。GitHub CLI をインストールし、PATH を確認してください。");
        }

        ProcessStartInfo startInfo = BuildStartInfo(resolvedPath, path, jq, paginate, _includeResponseHeaders);
        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeout);

        IProcessInstance? process = null;
        try
        {
            process = _processRunner.Start(startInfo);

            // stderr も読み進めないと、パイプバッファを超える出力で子プロセスが書き込みにブロックする（#201）
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            string stdout = await stdoutTask.ConfigureAwait(false);
            string stderr = await stderrTask.ConfigureAwait(false);

            GhApiRateLimitHeaders? headers = null;
            if (_includeResponseHeaders)
            {
                (stdout, headers) = ReadResponseHeaders(stdout);
            }

            GhApiResult result = process.ExitCode == 0
                ? GhApiResult.Success(stdout)
                : GhApiResult.Failure(ParseHttpStatus(stderr), DescribeFailure(process.ExitCode, stderr));
            return result with { RateLimitHeaders = headers };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            KillProcess(process);
            return GhApiResult.Failure(null, $"gh の実行が {_timeout.TotalSeconds:0} 秒でタイムアウトしました。");
        }
        catch (OperationCanceledException)
        {
            KillProcess(process);
            throw;
        }
        catch (Exception ex)
        {
            KillProcess(process);
            return GhApiResult.Failure(null, $"gh を実行できません: {ex.Message}");
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static ProcessStartInfo BuildStartInfo(string resolvedPath, string path, string jq, bool paginate, bool includeResponseHeaders)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = resolvedPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // 無人で実行するため、対話プロンプトと更新通知でブロック・出力汚染しないようにする
        startInfo.Environment["GH_PROMPT_DISABLED"] = "1";
        startInfo.Environment["GH_NO_UPDATE_NOTIFIER"] = "1";

        startInfo.ArgumentList.Add("api");
        startInfo.ArgumentList.Add(path);
        if (includeResponseHeaders)
        {
            startInfo.ArgumentList.Add("--include");
        }

        if (paginate)
        {
            startInfo.ArgumentList.Add("--paginate");
        }

        startInfo.ArgumentList.Add("--jq");
        startInfo.ArgumentList.Add(jq);
        return startInfo;
    }

    private static (string Output, GhApiRateLimitHeaders Headers) ReadResponseHeaders(string output)
    {
        using StringReader reader = new(output);
        string? retryAfter = null;
        string? remaining = null;
        string? reset = null;
        string? line;
        while (!string.IsNullOrEmpty(line = reader.ReadLine()))
        {
            int separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator < 0)
            {
                continue;
            }

            string name = line[..separator];
            string value = line[(separator + 1)..].Trim();
            if (name.Equals("retry-after", StringComparison.OrdinalIgnoreCase))
            {
                retryAfter = value;
            }
            else if (name.Equals("x-ratelimit-remaining", StringComparison.OrdinalIgnoreCase))
            {
                remaining = value;
            }
            else if (name.Equals("x-ratelimit-reset", StringComparison.OrdinalIgnoreCase))
            {
                reset = value;
            }
        }

        return (reader.ReadToEnd(), new GhApiRateLimitHeaders(retryAfter, remaining, reset));
    }

    private static int? ParseHttpStatus(string stderr)
    {
        Match match = _httpStatusPattern.Match(stderr);
        return match.Success && int.TryParse(match.Groups["status"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int status)
            ? status
            : null;
    }

    private static void KillProcess(IProcessInstance? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Kill() が走る前にプロセスが終了していた
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // OS が終了させられなかった。ここでできることは無い
        }
    }

    private string DescribeFailure(int exitCode, string stderr)
    {
        string summary = ProcessOutputSummarizer.Summarize(stderr, _secretMasker);
        return summary.Length > 0 ? summary : $"gh が終了コード {exitCode} で終了しました。";
    }
}
