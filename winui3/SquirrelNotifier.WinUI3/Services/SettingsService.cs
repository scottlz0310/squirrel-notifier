// <copyright file="SettingsService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Diagnostics;
using System.Text.Json;

namespace SquirrelNotifier.WinUI3.Services;

internal sealed class SettingsService
{
    private readonly string _settingsDirectory;
    private readonly string _settingsPath;
    private readonly AppSettings _settings;

    public AppSettings Settings => _settings;

    public string SettingsDirectory => _settingsDirectory;

    public event EventHandler? SettingsChanged;

    public SettingsService()
        : this(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SquirrelNotifier"),
            pnpmBinDir: null)
    {
    }

    internal SettingsService(string settingsDirectory, string? pnpmBinDir = null)
    {
        if (string.IsNullOrWhiteSpace(settingsDirectory))
        {
            throw new ArgumentException("設定保存先のディレクトリが不正です。", nameof(settingsDirectory));
        }

        _settingsDirectory = settingsDirectory;
        Directory.CreateDirectory(_settingsDirectory);
        _settingsPath = Path.Combine(_settingsDirectory, "settings.json");

        _settings = LoadSettings();

        // 旧単一 ResourceUri を ResourceUris リストに移行する
        if (_settings.ResourceUris.Count == 0)
        {
            _settings.ResourceUris.Add(_settings.ResourceUri);
            SaveSettings();
        }

        // 旧単一スロット設定（path または arguments がカスタマイズ済み）を reviewer スロットに移行する
        if (!_settings.LauncherSlotsMigrated)
        {
            const string legacyDefaultLauncherPath = "review-raven";
            const string legacyDefaultLauncherArgs = "review --interactive --repo {owner}/{repo} --pr {prNumber}";
            bool pathCustomized = _settings.LauncherCommandPath != legacyDefaultLauncherPath;
            bool argsCustomized = _settings.LauncherArguments != legacyDefaultLauncherArgs;

            if (pathCustomized || argsCustomized)
            {
                // どちらかがカスタマイズされていれば path/args を組として reviewer スロットへ移行する
                _settings.ReviewerLauncherCommandPath = _settings.LauncherCommandPath;
                _settings.ReviewerLauncherArguments = _settings.LauncherArguments;
            }

            _settings.LauncherSlotsMigrated = true;
            SaveSettings();
        }

        if (_settings.SubscriberCommandPath == "mcp-resource-subscriber")
        {
            // Lazy pnpm probe: only runs when the default command name needs resolution.
            // Tests pass pnpmBinDir: string.Empty to skip the probe entirely.
            string? binDir = pnpmBinDir ?? GetPnpmGlobalBinDir();
            string resolved = ResolveCommandPath(_settings.SubscriberCommandPath, binDir);
            if (resolved != "mcp-resource-subscriber")
            {
                _settings.SubscriberCommandPath = resolved;
                SaveSettings();
            }
        }
    }

    internal static string ResolveCommandPath(string command, string? pnpmBinDir = null, string? pathEnv = null)
    {
        if (File.Exists(command))
        {
            return Path.GetFullPath(command);
        }

        string[] extensions = OperatingSystem.IsWindows()
            ? [".exe", ".cmd", ".bat", ".ps1"]
            : [];

        pathEnv ??= Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
        {
            char separator = OperatingSystem.IsWindows() ? ';' : ':';
            foreach (string dir in pathEnv.Split(separator, StringSplitOptions.RemoveEmptyEntries))
            {
                string? found = FindInDirectory(dir, command, extensions);
                if (found is not null)
                {
                    return found;
                }
            }
        }

        if (!string.IsNullOrEmpty(pnpmBinDir))
        {
            string? found = FindInDirectory(pnpmBinDir, command, extensions);
            if (found is not null)
            {
                return found;
            }
        }

        return command;
    }

    // Runs `pnpm bin -g` to locate the global bin directory; returns null on any failure or timeout.
    internal static string? GetPnpmGlobalBinDir()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "pnpm",
                Arguments = "bin -g",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            using Process? proc = Process.Start(psi);
            if (proc is null)
            {
                return null;
            }

            Task<string> readTask = proc.StandardOutput.ReadToEndAsync();
            if (!readTask.Wait(3000))
            {
                proc.Kill();
                return null;
            }

            proc.WaitForExit(1000);
            string output = readTask.Result.Trim();
            return proc.ExitCode == 0 && Directory.Exists(output) ? output : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? FindInDirectory(string dir, string command, string[] extensions)
    {
        string fullPath = Path.Combine(dir, command);
        foreach (string ext in extensions)
        {
            string extPath = fullPath + ext;
            if (File.Exists(extPath))
            {
                return Path.GetFullPath(extPath);
            }
        }

        if (File.Exists(fullPath))
        {
            return Path.GetFullPath(fullPath);
        }

        return null;
    }

    private AppSettings LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                string json = File.ReadAllText(_settingsPath);
                AppSettings? settings = JsonSerializer.Deserialize<AppSettings>(json);
                if (settings != null)
                {
                    return settings;
                }
            }
        }
        catch
        {
            // Ignore errors and use default settings
        }

        return new AppSettings();
    }

    public void SaveSettings()
    {
        try
        {
            string json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions
            {
                WriteIndented = true,
            });
            File.WriteAllText(_settingsPath, json);
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // Ignore save errors
        }
    }

    public void UpdateLastSkippedVersion(string version)
    {
        _settings.LastSkippedVersion = version ?? string.Empty;
        SaveSettings();
    }

    public void UpdateRateLimitMonitoredAgentIds(IReadOnlyList<string> agentIds)
    {
        ArgumentNullException.ThrowIfNull(agentIds);
        _settings.RateLimitMonitoredAgentIds = new List<string>(agentIds);
        SaveSettings();
    }

    public void UpdateSettings(
        string commandPath,
        string arguments,
        string gatewayUrl,
        IReadOnlyList<string> resourceUris,
        int timeoutMs,
        string reviewerLauncherCommandPath,
        string reviewerLauncherArguments,
        string reviewedLauncherCommandPath,
        string reviewedLauncherArguments,
        int launcherTimeoutMs)
    {
        if (string.IsNullOrWhiteSpace(commandPath))
        {
            throw new ArgumentException("Command path cannot be empty", nameof(commandPath));
        }

        if (string.IsNullOrWhiteSpace(gatewayUrl) || !Uri.TryCreate(gatewayUrl, UriKind.Absolute, out Uri? uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Gateway URL must be a valid http/https absolute URL", nameof(gatewayUrl));
        }

        ArgumentNullException.ThrowIfNull(resourceUris);
        if (resourceUris.Count == 0 || resourceUris.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Resource URIs must contain at least one non-empty URI", nameof(resourceUris));
        }

        if (timeoutMs <= 0 || timeoutMs > 300000)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutMs), "Timeout must be between 1 and 300000 ms");
        }

        if (string.IsNullOrWhiteSpace(reviewerLauncherCommandPath))
        {
            throw new ArgumentException("Reviewer launcher command path cannot be empty", nameof(reviewerLauncherCommandPath));
        }

        if (string.IsNullOrWhiteSpace(reviewedLauncherCommandPath))
        {
            throw new ArgumentException("Reviewed launcher command path cannot be empty", nameof(reviewedLauncherCommandPath));
        }

        if (launcherTimeoutMs <= 0 || launcherTimeoutMs > 300000)
        {
            throw new ArgumentOutOfRangeException(nameof(launcherTimeoutMs), "Launcher timeout must be between 1 and 300000 ms");
        }

        _settings.SubscriberCommandPath = commandPath;
        _settings.SubscriberArguments = arguments;
        _settings.GatewayUrl = gatewayUrl;
        _settings.ResourceUris = new List<string>(resourceUris);
        _settings.ResourceUri = resourceUris[0];
        _settings.NotificationTimeoutMs = timeoutMs;
        _settings.ReviewerLauncherCommandPath = reviewerLauncherCommandPath;
        _settings.ReviewerLauncherArguments = reviewerLauncherArguments;
        _settings.ReviewedLauncherCommandPath = reviewedLauncherCommandPath;
        _settings.ReviewedLauncherArguments = reviewedLauncherArguments;
        _settings.LauncherTimeoutMs = launcherTimeoutMs;
        SaveSettings();
    }
}

internal sealed class AppSettings
{
    public string SubscriberCommandPath { get; set; } = "mcp-resource-subscriber";

    public string SubscriberArguments { get; set; } = string.Empty;

    public string GatewayUrl { get; set; } = "http://localhost:3000";

    // 旧フィールド（後方互換; 新設定は ResourceUris を使用）
    public string ResourceUri { get; set; } = "queue://review/queue";

    public List<string> ResourceUris { get; set; } = new();

    public int NotificationTimeoutMs { get; set; } = 60000;

    // 旧フィールド（後方互換のため残す; UI からは削除済み）
    public string LauncherCommandPath { get; set; } = "review-raven";

    public string LauncherArguments { get; set; } = "review --interactive --repo {owner}/{repo} --pr {prNumber}";

    // reviewer-side スロット
    public string ReviewerLauncherCommandPath { get; set; } = "claude";

    public string ReviewerLauncherArguments { get; set; } = "-p \"/thread-owl-pr-reviewer {owner}/{repo}#{prNumber} を {reason} モードでレビューしてください\"";

    // reviewed-side スロット
    public string ReviewedLauncherCommandPath { get; set; } = "claude";

    public string ReviewedLauncherArguments { get; set; } = "-p \"/thread-owl-review-cycle {owner}/{repo}#{prNumber} のレビュー指摘に対応してください\"";

    public bool LauncherSlotsMigrated { get; set; }

    public int LauncherTimeoutMs { get; set; } = 300000;

    public string LastSkippedVersion { get; set; } = string.Empty;

    // ローカルの statusline スクリプトがレートリミット状態を書き出すエージェント ID
    // （RateLimitAgentCatalog 参照）のうち、監視対象として選択されているもの
    public List<string> RateLimitMonitoredAgentIds { get; set; } = new();
}
