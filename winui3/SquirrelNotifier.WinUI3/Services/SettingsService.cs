// <copyright file="SettingsService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

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
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SquirrelNotifier"))
    {
    }

    internal SettingsService(string settingsDirectory)
    {
        if (string.IsNullOrWhiteSpace(settingsDirectory))
        {
            throw new ArgumentException("設定保存先のディレクトリが不正です。", nameof(settingsDirectory));
        }

        _settingsDirectory = settingsDirectory;
        Directory.CreateDirectory(_settingsDirectory);
        _settingsPath = Path.Combine(_settingsDirectory, "settings.json");

        _settings = LoadSettings();
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

    public void UpdateCheckInterval(int hours)
    {
        if (hours < 1 || hours > 24)
        {
            throw new ArgumentOutOfRangeException(nameof(hours), "Check interval must be between 1 and 24 hours");
        }

        _settings.CheckIntervalHours = hours;
        SaveSettings();
    }
}

internal sealed class AppSettings
{
    public int CheckIntervalHours { get; set; } = 2;
}
