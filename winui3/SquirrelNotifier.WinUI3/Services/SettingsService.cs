// <copyright file="SettingsService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Diagnostics;
using System.Text.Json;
using SquirrelNotifier.WinUI3.Helpers;
using SquirrelNotifier.WinUI3.Models;

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

        // reviewed launcher の旧既定値は実在しないスキル名（/thread-owl-review-cycle）を呼んでいた（#150）。
        // 旧既定値のまま使っているユーザーのみ修正後の既定値へ一回だけ移行する（カスタマイズ済みの
        // 値は変更しない）。後続の LauncherPresetsMigrated 判定が新しいカタログ値と一致するよう、
        // この migration はプリセット判定より先に実行する.
        if (!_settings.ReviewedLauncherSkillMigrated)
        {
            const string legacyReviewedLauncherArgs = "-p \"/thread-owl-review-cycle {owner}/{repo}#{prNumber} のレビュー指摘に対応してください\"";
            if (_settings.ReviewedLauncherCommandPath == "claude" && _settings.ReviewedLauncherArguments == legacyReviewedLauncherArgs)
            {
                _settings.ReviewedLauncherArguments = LauncherAgentCatalog.Find("claude")!.ReviewedArgumentsTemplate;
            }

            _settings.ReviewedLauncherSkillMigrated = true;
            SaveSettings();
        }

        // agy の print mode は launcher 側とは別に既定 5 分で timeout する（#180）。
        // 旧プリセットと完全一致する未変更の設定だけを 30 分指定へ移行し、自由編集された値は保持する。
        // 後続の LauncherPresetsMigrated 判定が新しいカタログ値と一致するよう、この migration は先に実行する.
        if (!_settings.AgyPrintTimeoutMigrated)
        {
            const string legacyReviewerArguments = "-p \"thread-owl MCP のツールを使って {owner}/{repo}#{prNumber} を {reason} モードでレビューしてください\"";
            const string legacyReviewedArguments = "-p \"thread-owl MCP のツールを使って {owner}/{repo}#{prNumber} のレビュー指摘に対応し、修正・返信・resolve を行ってください\"";
            LauncherAgentDefinition agy = LauncherAgentCatalog.Find("agy")!;

            if (_settings.ReviewerLauncherCommandPath == agy.Command && _settings.ReviewerLauncherArguments == legacyReviewerArguments)
            {
                _settings.ReviewerLauncherArguments = agy.ReviewerArgumentsTemplate;
            }

            if (_settings.ReviewedLauncherCommandPath == agy.Command && _settings.ReviewedLauncherArguments == legacyReviewedArguments)
            {
                _settings.ReviewedLauncherArguments = agy.ReviewedArgumentsTemplate;
            }

            _settings.AgyPrintTimeoutMigrated = true;
            SaveSettings();
        }

        // reviewer は対象 checkout を直接操作せず専用ディレクトリから起動するため、Codex の
        // Git repository check を明示的に無効化する（#186）。旧プリセットと完全一致する設定のみ移行する.
        if (!_settings.CodexReviewerWorkingDirectoryMigrated)
        {
            const string legacyReviewerArguments = "exec \"thread-owl MCP のツールを使って {owner}/{repo}#{prNumber} を {reason} モードでレビューしてください\"";
            LauncherAgentDefinition codex = LauncherAgentCatalog.Find("codex")!;
            if (_settings.ReviewerLauncherCommandPath == codex.Command && _settings.ReviewerLauncherArguments == legacyReviewerArguments)
            {
                _settings.ReviewerLauncherArguments = codex.ReviewerArgumentsTemplate;
            }

            _settings.CodexReviewerWorkingDirectoryMigrated = true;
            SaveSettings();
        }

        // claude の print mode 既定（text）では progress marker を実行中に取得できないため、
        // stream-json 出力へ移行する（#187）。旧プリセットと完全一致する未変更の設定だけを移行し、
        // 自由編集された値は保持する。後続の LauncherPresetsMigrated 判定が新しいカタログ値と
        // 一致するよう、この migration は先に実行する.
        if (!_settings.ClaudeStreamJsonMigrated)
        {
            const string legacyReviewerArguments = "-p \"/thread-owl-pr-reviewer {owner}/{repo}#{prNumber} を {reason} モードでレビューしてください\"";
            const string legacyReviewedArguments = "-p \"/review-raven-thread-owl-cycle {owner}/{repo}#{prNumber} のレビュー指摘に対応してください\"";
            LauncherAgentDefinition claude = LauncherAgentCatalog.Find("claude")!;

            if (_settings.ReviewerLauncherCommandPath == claude.Command && _settings.ReviewerLauncherArguments == legacyReviewerArguments)
            {
                _settings.ReviewerLauncherArguments = claude.ReviewerArgumentsTemplate;
            }

            if (_settings.ReviewedLauncherCommandPath == claude.Command && _settings.ReviewedLauncherArguments == legacyReviewedArguments)
            {
                _settings.ReviewedLauncherArguments = claude.ReviewedArgumentsTemplate;
            }

            _settings.ClaudeStreamJsonMigrated = true;
            SaveSettings();
        }

        // launcher スロットの command / arguments がどのエージェントプリセットと一致するかを
        // 一回だけ判定して記録する（#149）。一致しない場合は「カスタム」として扱う.
        if (!_settings.LauncherPresetsMigrated)
        {
            _settings.ReviewerLauncherPresetId = LauncherAgentCatalog.ResolvePresetId(
                _settings.ReviewerLauncherCommandPath, _settings.ReviewerLauncherArguments, LauncherRole.Reviewer);
            _settings.ReviewedLauncherPresetId = LauncherAgentCatalog.ResolvePresetId(
                _settings.ReviewedLauncherCommandPath, _settings.ReviewedLauncherArguments, LauncherRole.Reviewed);

            _settings.LauncherPresetsMigrated = true;
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

    // PATH / PATHEXT の探索は共通 resolver（#186）へ委譲する。解決できない場合は
    // 設定値をそのまま返し、起動時の Win32 エラー（ファイル未検出）として表面化させる
    internal static string ResolveCommandPath(string command, string? pnpmBinDir = null, string? pathEnv = null)
    {
        return CommandPathResolver.Resolve(command, pnpmBinDir, pathEnv) ?? command;
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

    /// <summary>
    /// レートリミットスナップショットを fresh とみなす経過時間の閾値（分）を更新する（#145）.
    /// </summary>
    /// <param name="minutes">1 分以上の閾値.</param>
    public void UpdateRateLimitFreshnessThresholdMinutes(int minutes)
    {
        if (minutes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minutes), "Freshness threshold must be at least 1 minute");
        }

        _settings.RateLimitFreshnessThresholdMinutes = minutes;
        SaveSettings();
    }

    public void UpdateLiveLogAutoCloseEnabled(bool enabled)
    {
        _settings.LiveLogAutoCloseEnabled = enabled;
        SaveSettings();
    }

    /// <summary>
    /// 指定した launcher スロットに現在選択されているプリセットの rateLimitAgentId を解決する（#149）。
    /// 「カスタム」設定、またはレートリミット取得手段が無いプリセット（copilot 等）の場合は
    /// <see langword="null"/> を返す（Auto-Pause の gate 対象外として扱う #147）.
    /// </summary>
    /// <param name="role">解決対象の launcher スロット.</param>
    /// <returns>対応する rateLimitAgentId。無い場合は <see langword="null"/>.</returns>
    public string? ResolveLauncherRateLimitAgentId(LauncherRole role)
    {
        string presetId = role == LauncherRole.Reviewer
            ? _settings.ReviewerLauncherPresetId
            : _settings.ReviewedLauncherPresetId;

        return LauncherAgentCatalog.Find(presetId)?.RateLimitAgentId;
    }

    /// <summary>
    /// 指定した launcher スロットに現在選択されているプリセットの progress event 対応度を解決する（#151）。
    /// 「カスタム」設定など不明なプリセットは <see cref="ProgressEventSupport.None"/> として扱う.
    /// </summary>
    /// <param name="role">解決対象の launcher スロット.</param>
    /// <returns>対応する progress event 対応度.</returns>
    public ProgressEventSupport ResolveLauncherProgressEventSupport(LauncherRole role)
    {
        string presetId = role == LauncherRole.Reviewer
            ? _settings.ReviewerLauncherPresetId
            : _settings.ReviewedLauncherPresetId;

        return LauncherAgentCatalog.Find(presetId)?.ProgressEventSupport ?? ProgressEventSupport.None;
    }

    public string? ResolveRepositoryCheckoutPath(string repository)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        return _settings.RepositoryCheckoutMappings
            .FirstOrDefault(pair => string.Equals(pair.Key, repository, StringComparison.OrdinalIgnoreCase))
            .Value;
    }

    public void UpdateRepositoryCheckoutMappings(IReadOnlyDictionary<string, string> mappings)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string repository, string path) in mappings)
        {
            if (string.IsNullOrWhiteSpace(repository) || string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            {
                throw new ArgumentException("Repository checkout mapping には owner/repo と絶対パスが必要です。", nameof(mappings));
            }

            normalized.Add(repository, Path.GetFullPath(path));
        }

        _settings.RepositoryCheckoutMappings = normalized;
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
        int launcherTimeoutMs,
        string reviewerLauncherPresetId,
        string reviewedLauncherPresetId)
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

        // レビューサイクルは 5 分を大きく超えるため、通知タイムアウト（上限 300000ms）とは
        // 別に上限 2 時間まで許容する（#143）
        if (launcherTimeoutMs <= 0 || launcherTimeoutMs > 7200000)
        {
            throw new ArgumentOutOfRangeException(nameof(launcherTimeoutMs), "Launcher timeout must be between 1 and 7200000 ms");
        }

        if (string.IsNullOrWhiteSpace(reviewerLauncherPresetId))
        {
            throw new ArgumentException("Reviewer launcher preset id cannot be empty", nameof(reviewerLauncherPresetId));
        }

        if (string.IsNullOrWhiteSpace(reviewedLauncherPresetId))
        {
            throw new ArgumentException("Reviewed launcher preset id cannot be empty", nameof(reviewedLauncherPresetId));
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
        _settings.ReviewerLauncherPresetId = reviewerLauncherPresetId;
        _settings.ReviewedLauncherPresetId = reviewedLauncherPresetId;
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

    public string ReviewerLauncherArguments { get; set; } = "-p \"/thread-owl-pr-reviewer {owner}/{repo}#{prNumber} を {reason} モードでレビューしてください\" --verbose --output-format stream-json";

    // reviewed-side スロット
    public string ReviewedLauncherCommandPath { get; set; } = "claude";

    public string ReviewedLauncherArguments { get; set; } = "-p \"/review-raven-thread-owl-cycle {owner}/{repo}#{prNumber} のレビュー指摘に対応してください\" --verbose --output-format stream-json";

    public bool LauncherSlotsMigrated { get; set; }

    // reviewed launcher 既定値のスキル名修正（#150）を既存ユーザーへ適用する一回限り migration のフラグ
    public bool ReviewedLauncherSkillMigrated { get; set; }

    // agy の print timeout 修正（#180）を既存の未変更プリセットへ適用する一回限り migration のフラグ
    public bool AgyPrintTimeoutMigrated { get; set; }

    // Codex reviewer の専用 working directory 対応（#186）を既存の未変更プリセットへ適用する migration
    public bool CodexReviewerWorkingDirectoryMigrated { get; set; }

    // claude の progress event 逐次取得対応（#187、stream-json 化）を既存の未変更プリセットへ適用する migration
    public bool ClaudeStreamJsonMigrated { get; set; }

    // launcher スロットに選択されているエージェントプリセット ID（LauncherAgentCatalog 参照）。
    // 自由編集でどのプリセットとも一致しなくなった場合は LauncherAgentCatalog.CustomPresetId になる.
    public string ReviewerLauncherPresetId { get; set; } = "claude";

    public string ReviewedLauncherPresetId { get; set; } = "claude";

    public bool LauncherPresetsMigrated { get; set; }

    // reviewed-side launcher が対象 repository の checkout を一意に解決するための明示 mapping（#186）
    public Dictionary<string, string> RepositoryCheckoutMappings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // 長時間のレビューサイクル（30 分超もありうる）を 5 分で強制終了しないよう既定 30 分（#143）
    public int LauncherTimeoutMs { get; set; } = 1800000;

    public string LastSkippedVersion { get; set; } = string.Empty;

    // ライブログウィンドウ（#144）: 成功終了時に短い猶予の後で自動クローズするか。
    // 失敗・キャンセル・タイムアウト時は設定に関わらず診断のため保持する
    public bool LiveLogAutoCloseEnabled { get; set; } = true;

    // ローカルの statusline スクリプトがレートリミット状態を書き出すエージェント ID
    // （RateLimitAgentCatalog 参照）のうち、監視対象として選択されているもの
    public List<string> RateLimitMonitoredAgentIds { get; set; } = new();

    // レートリミットスナップショットを fresh とみなす経過時間の閾値（分）。Delta 算出と
    // Auto-Pause（#147）の両方が参照する（#145）
    public int RateLimitFreshnessThresholdMinutes { get; set; } = RateLimitFreshnessPolicy.DefaultThresholdMinutes;
}
