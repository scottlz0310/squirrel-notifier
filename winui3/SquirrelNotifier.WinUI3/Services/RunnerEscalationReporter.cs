// <copyright file="RunnerEscalationReporter.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SquirrelNotifier.WinUI3.Helpers;
using SquirrelNotifier.WinUI3.Models;

namespace SquirrelNotifier.WinUI3.Services;

internal interface IRunnerEscalationReporter
{
    Task<string> ReportAsync(ReviewEvent reviewEvent, string headSha, CancellationToken cancellationToken);
}

internal sealed class RunnerEscalationReporter(SettingsService settingsService, IProcessRunner? processRunner = null) : IRunnerEscalationReporter
{
    private const string _tool = "post_runner_escalation";
    private readonly IProcessRunner _processRunner = processRunner ?? new ProcessRunner();

    public async Task<string> ReportAsync(ReviewEvent reviewEvent, string headSha, CancellationToken cancellationToken)
    {
        string url = settingsService.Settings.RunnerEscalationUrl;
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? endpoint)
            || !(endpoint.Scheme == Uri.UriSchemeHttps || (endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback))
            || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
        {
            return "PR コメント未投稿: RunnerEscalationUrl に認証済み review-raven の URL を設定してください。";
        }

        if (!PrReferenceParser.TryParse($"{reviewEvent.Repository}#{reviewEvent.PrNumber.ToString(CultureInfo.InvariantCulture)}", out PrReference? reference)
            || reference is null || reference.Repo is "." or ".." || headSha.Length != 40 || headSha.Any(c => !char.IsAsciiHexDigitLower(c)))
        {
            throw new InvalidOperationException("エスカレーション対象の PR または HEAD が不正です。");
        }

        string command = SettingsService.ResolveCommandPath(settingsService.Settings.SubscriberCommandPath);
        ProcessStartInfo startInfo = ExternalProcessStartInfoFactory.Create(command, ["call", "--url", url, "--tool", _tool, "--args-stdin", "--json"]);
        startInfo.StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        startInfo.StandardOutputEncoding = Encoding.UTF8;
        startInfo.StandardErrorEncoding = Encoding.UTF8;
        string payload = JsonSerializer.Serialize(new { owner = reference.Owner, repo = reference.Repo, pr = reference.PrNumber, headSha, reason = "resume_unresponsive" });
        cancellationToken.ThrowIfCancellationRequested();
        if (!ReserveAttempt(settingsService.SettingsDirectory, reference, headSha))
        {
            return "同じ HEAD の PR コメントは投稿試行済みです。結果不明を含め、再投稿せず GitHub を確認してください。";
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var pumps = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        IProcessInstance? process = null;
        Task<string>? stdout = null;
        Task<string>? stderr = null;
        bool exited = false;
        try
        {
            timeout.Token.ThrowIfCancellationRequested();
            process = _processRunner.Start(startInfo);
            stdout = process.StandardOutput.ReadToEndAsync(pumps.Token);
            stderr = process.StandardError.ReadToEndAsync(pumps.Token);
            await process.StandardInput.WriteAsync(payload.AsMemory(), timeout.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            exited = true;
            string output = await stdout.ConfigureAwait(false);
            await stderr.ConfigureAwait(false);
            return ReadResult(process.ExitCode, output, url, reference);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // 例外本文には資格情報やサーバー応答が含まれ得るため、表示しない。
            return "PR コメントの投稿結果を確認できません。自動再試行せず GitHub を確認してください。";
        }
        finally
        {
            pumps.Cancel();
            if (process is not null && !exited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // 終了済みのプロセスは停止不要。
                }
            }

            foreach (Task<string>? pump in new[] { stdout, stderr })
            {
                if (pump is null)
                {
                    continue;
                }

                try
                {
                    await pump.ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
                {
                    // 停止したパイプの後始末。
                }
            }

            process?.Dispose();
        }
    }

    internal static bool ReserveAttempt(string settingsDirectory, PrReference reference, string headSha)
    {
        string key = $"{reference.Owner}/{reference.Repo}#{reference.PrNumber.ToString(CultureInfo.InvariantCulture)}@{headSha}".ToUpperInvariant();
        string directory = Path.Combine(settingsDirectory, "runner-escalations");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".attempt");
        try
        {
            // 空のマーカーも投稿試行として扱う。書き込み途中の終了でも再投稿しない。
            using var marker = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            marker.Flush(flushToDisk: true);
            return true;
        }
        catch (IOException) when (File.Exists(path))
        {
            return false;
        }
    }

    internal static string ReadResult(int exitCode, string output, string url, PrReference reference)
    {
        try
        {
            CallToolResult? result = JsonSerializer.Deserialize<CallToolResult>(output);
            if (exitCode == 0 && result is { IsError: false, ErrorCode: null, Tool: _tool } && result.ServerUrl == url)
            {
                using JsonDocument body = JsonDocument.Parse(result.ExtractText() ?? "null");
                if (body.RootElement.ValueKind == JsonValueKind.Object
                    && body.RootElement.TryGetProperty("commentId", out JsonElement id)
                    && id.ValueKind == JsonValueKind.String && long.TryParse(id.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out long number) && number > 0
                    && body.RootElement.TryGetProperty("url", out JsonElement link) && link.ValueKind == JsonValueKind.String
                    && link.GetString() == $"https://github.com/{reference.Owner}/{reference.Repo}/pull/{reference.PrNumber.ToString(CultureInfo.InvariantCulture)}#issuecomment-{number.ToString(CultureInfo.InvariantCulture)}")
                {
                    return $"PR に対応依頼を投稿しました: {link.GetString()}";
                }
            }
        }
        catch (JsonException)
        {
            // 不正な応答から投稿成功を推測しない。
        }

        return "PR コメントの投稿結果を確認できません。自動再試行せず GitHub を確認してください。";
    }
}
