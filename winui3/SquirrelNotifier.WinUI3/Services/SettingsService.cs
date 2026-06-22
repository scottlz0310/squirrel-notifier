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

    public event EventHandler? SettingsChanged;

    public SettingsService()
        : this(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SquirrelNotifier"),
            GetPnpmGlobalBinDir())
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

        if (_settings.SubscriberCommandPath == "mcp-resource-subscriber")
        {
            string resolved = ResolveCommandPath(_settings.SubscriberCommandPath, pnpmBinDir);
            if (resolved != "mcp-resource-subscriber")
            {
                _settings.SubscriberCommandPath = resolved;
                SaveSettings();
            }
        }
    }

    internal static string ResolveCommandPath(string command, string? pnpmBinDir = null)
    {
        if (File.Exists(command))
        {
            return Path.GetFullPath(command);
        }

        string[] extensions = OperatingSystem.IsWindows()
            ? [".exe", ".cmd", ".bat", ".ps1"]
            : [];

        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
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

    // Runs `pnpm bin -g` to locate the global bin directory; returns null on any failure.
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

            string output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(3000);

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

    public void UpdateSettings(
        string commandPath,
        string arguments,
        string gatewayUrl,
        string resourceUri,
        int timeoutMs,
        string launcherCommandPath,
        string launcherArguments,
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

        if (string.IsNullOrWhiteSpace(resourceUri))
        {
            throw new ArgumentException("Resource URI cannot be empty", nameof(resourceUri));
        }

        if (timeoutMs <= 0 || timeoutMs > 300000)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutMs), "Timeout must be between 1 and 300000 ms");
        }

        if (string.IsNullOrWhiteSpace(launcherCommandPath))
        {
            throw new ArgumentException("Launcher command path cannot be empty", nameof(launcherCommandPath));
        }

        if (launcherTimeoutMs <= 0 || launcherTimeoutMs > 300000)
        {
            throw new ArgumentOutOfRangeException(nameof(launcherTimeoutMs), "Launcher timeout must be between 1 and 300000 ms");
        }

        _settings.SubscriberCommandPath = commandPath;
        _settings.SubscriberArguments = arguments;
        _settings.GatewayUrl = gatewayUrl;
        _settings.ResourceUri = resourceUri;
        _settings.NotificationTimeoutMs = timeoutMs;
        _settings.LauncherCommandPath = launcherCommandPath;
        _settings.LauncherArguments = launcherArguments;
        _settings.LauncherTimeoutMs = launcherTimeoutMs;
        SaveSettings();
    }
}

internal sealed class AppSettings
{
    public string SubscriberCommandPath { get; set; } = "mcp-resource-subscriber";

    public string SubscriberArguments { get; set; } = string.Empty;

    public string GatewayUrl { get; set; } = "http://localhost:3000";

    public string ResourceUri { get; set; } = "queue://review/queue";

    public int NotificationTimeoutMs { get; set; } = 60000;

    public string LauncherCommandPath { get; set; } = "review-raven";

    public string LauncherArguments { get; set; } = "review --interactive --repo {owner}/{repo} --pr {prNumber}";

    public int LauncherTimeoutMs { get; set; } = 300000;

    public string LastSkippedVersion { get; set; } = string.Empty;
}
