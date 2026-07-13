using System.IO;
using System.IO.Compression;
using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

sealed class AppSettings
{
    public string DownloadDirectory { get; set; } = "";
    public bool AskBeforeDownload { get; set; } = true;
    public string AboutText { get; set; } = "N网下载器";
    public string UpdateManifestUrl { get; set; } = "";
    public bool UseProxy { get; set; }
    public string ProxyHost { get; set; } = "127.0.0.1";
    public int ProxyPort { get; set; } = 7890;
    public int SegmentCount { get; set; } = 64;
    public string ThemeColor { get; set; } = "#3898fc";
    public string ThemeDarkColor { get; set; } = "#276ab0";
    public string IgnoredUpdateVersion { get; set; } = "";
}

sealed record HistoryItem(DateTime Time, string Name, string Destination, string Status)
{
    public string TimeDisplay
    {
        get
        {
            var hour = Time.Hour % 12;
            if (hour == 0) hour = 12;
            return $"{Time:yyyy-MM-dd} {(Time.Hour < 12 ? "上午" : "下午")} {hour:00}:{Time:mm:ss}";
        }
    }

    public bool DestinationExists => File.Exists(Destination);
    public string MissingTip => DestinationExists ? "打开文件所在位置" : "该文件被移动或已删除";
}

enum AppLogLevel { Info, Success, Warning, Error, System }

static class AppData
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly object Sync = new();
    public static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexusModsDownloader");
    public static readonly string BrowserExtensionDirectory = Path.Combine(Root, "browser-extension");
    public static readonly string BrowserExtensionMarkerPath = Path.Combine(BrowserExtensionDirectory, "installed.marker");
    private static readonly string SettingsPath = Path.Combine(Root, "settings.json");
    private static readonly string HistoryPath = Path.Combine(Root, "history.json");
    private static readonly string LogPath = Path.Combine(Root, "activity.log");
    public static AppSettings Settings { get; private set; } = new();
    public static List<HistoryItem> History { get; private set; } = [];
    public static event Action<string>? Logged;

    public static void Initialize()
    {
        Directory.CreateDirectory(Root);
        Settings = Load(SettingsPath, new AppSettings
        {
            DownloadDirectory = Environment.GetEnvironmentVariable("NEXUSMODS_DOWNLOAD_DIR")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Nexus Mods")
        });
        History = Load(HistoryPath, new List<HistoryItem>());
    }

    public static void SaveSettings() => Save(SettingsPath, Settings);

    public static bool BrowserExtensionInstalled => File.Exists(BrowserExtensionMarkerPath);

    public static void MarkBrowserExtensionInstalled()
    {
        Directory.CreateDirectory(BrowserExtensionDirectory);
        File.WriteAllText(BrowserExtensionMarkerPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
    }

    public static void AddHistory(HistoryItem item)
    {
        History.Insert(0, item);
        if (History.Count > 200) History.RemoveRange(200, History.Count - 200);
        Save(HistoryPath, History);
    }

    public static void RemoveHistory(HistoryItem item)
    {
        History.Remove(item);
        Save(HistoryPath, History);
    }

    public static void Log(string text, AppLogLevel level = AppLogLevel.Info)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level.ToString().ToUpperInvariant()}] {text}";
        lock (Sync) File.AppendAllText(LogPath, line + Environment.NewLine);
        Logged?.Invoke(line);
    }

    public static void LogStartupSeparator()
    {
        var line = $"-----此次启动 {DateTime.Now:yyyy-MM-dd HH:mm:ss}-----";
        lock (Sync) File.AppendAllText(LogPath, line + Environment.NewLine);
        Logged?.Invoke(line);
    }

    public static string ReadLog() => File.Exists(LogPath) ? File.ReadAllText(LogPath) : "";

    public static void ClearLog()
    {
        lock (Sync) File.WriteAllText(LogPath, "");
    }

    private static T Load<T>(string path, T fallback)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? fallback : fallback; }
        catch { return fallback; }
    }

    private static void Save<T>(string path, T value)
    {
        lock (Sync) File.WriteAllText(path, JsonSerializer.Serialize(value, Json));
    }
}

static class UpdateChecker
{
    private const string DefaultUpdateUrl = "https://n.yzyyz.top/latest.json";

    public static async Task<UpdateInfo> CheckForUpdatesAsync()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var json = await client.GetStringAsync(UpdateEndpoint());
        var info = JsonSerializer.Deserialize<UpdateInfo>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true, AllowTrailingCommas = true })
            ?? throw new InvalidOperationException("无法解析更新信息。");
        if (string.IsNullOrWhiteSpace(info.Version)) throw new InvalidOperationException("更新信息缺少版本号。");
        return info;
    }

    public static async Task CheckAsync()
    {
        try
        {
            var info = await CheckForUpdatesAsync();
            if (IsNewVersionAvailable(CurrentVersion, info.Version) && info.Version != AppData.Settings.IgnoredUpdateVersion)
                AppData.Log($"发现新版本 {info.Version}", AppLogLevel.System);
        }
        catch (Exception error)
        {
            AppData.Log($"自动检查更新失败: {error.Message}", AppLogLevel.Warning);
        }
    }

    public static bool IsNewVersionAvailable(string currentVersion, string latestVersion)
    {
        return TryVersion(currentVersion, out var current) && TryVersion(latestVersion, out var latest) && latest > current;
    }

    public static async Task<string> DownloadAndPrepareUpdateAsync(UpdateInfo info, IProgress<double>? progress = null)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "NYPDUpdate");
        Directory.CreateDirectory(tempDir);
        var zipPath = Path.Combine(tempDir, "update.zip");
        if (File.Exists(zipPath)) File.Delete(zipPath);

        Exception? lastError = null;
        foreach (var (source, url) in DownloadSources(info))
        {
            try
            {
                AppData.Log($"使用 {source} 下载更新：{url}", AppLogLevel.System);
                await DownloadFileAsync(url, zipPath, progress);
                lastError = null;
                break;
            }
            catch (Exception error)
            {
                lastError = error;
                AppData.Log($"{source} 更新源下载失败: {error.Message}", AppLogLevel.Warning);
            }
        }
        if (lastError is not null) throw lastError;

        VerifySha256(zipPath, info.Sha256);
        var updateDir = Path.Combine(Path.GetTempPath(), "NYPDUpdate_" + DateTime.Now.ToString("yyyyMMddHHmmss"));
        ZipFile.ExtractToDirectory(zipPath, updateDir, true);
        return CreateUpdateScript(zipPath, updateDir, AppContext.BaseDirectory);
    }

    public static string CurrentVersion => typeof(UpdateChecker).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    private static Uri UpdateEndpoint()
    {
        var value = string.IsNullOrWhiteSpace(AppData.Settings.UpdateManifestUrl) ? DefaultUpdateUrl : AppData.Settings.UpdateManifestUrl;
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : new Uri(DefaultUpdateUrl);
    }

    private static IEnumerable<(string Source, string Url)> DownloadSources(UpdateInfo info)
    {
        if (info.DownloadUrls is not null)
        {
            if (info.DownloadUrls.TryGetValue("github", out var github)) yield return ("GitHub", github);
            if (info.DownloadUrls.TryGetValue("gitee", out var gitee)) yield return ("Gitee", gitee);
        }
        if (!string.IsNullOrWhiteSpace(info.DownloadUrl)) yield return ("默认", info.DownloadUrl);
    }

    private static async Task DownloadFileAsync(string url, string destination, IProgress<double>? progress)
    {
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        await using var input = await response.Content.ReadAsStreamAsync();
        await using var output = File.Create(destination);
        var buffer = new byte[1024 * 128];
        long received = 0;
        progress?.Report(0);
        int count;
        while ((count = await input.ReadAsync(buffer)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, count));
            received += count;
            if (total is > 0) progress?.Report((double)received / total.Value);
        }
        progress?.Report(1);
    }

    private static void VerifySha256(string path, string expected)
    {
        if (string.IsNullOrWhiteSpace(expected))
        {
            AppData.Log("更新清单未提供 SHA256，已跳过文件校验。", AppLogLevel.Warning);
            return;
        }
        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(path);
            throw new InvalidOperationException("更新文件校验失败，文件可能已损坏。");
        }
    }

    private static string CreateUpdateScript(string zipPath, string updateDir, string targetDir)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), "NYPDUpdate.bat");
        var exePath = Process.GetCurrentProcess().MainModule!.FileName!;
        var script = $"""
@echo off
chcp 65001 > nul
timeout /t 2 /nobreak > nul
xcopy /Y /E /I "{updateDir}\*" "{targetDir}" > nul
start "" "{exePath}" --show
del /F /Q "{zipPath}" > nul 2>&1
rmdir /S /Q "{updateDir}" > nul 2>&1
del "%~f0"
""";
        File.WriteAllText(scriptPath, script);
        return scriptPath;
    }

    private static bool TryVersion(string value, out Version version)
    {
        var normalized = value.Trim().TrimStart('v', 'V').Split('-', 2)[0];
        return Version.TryParse(normalized, out version!);
    }
}

sealed class UpdateInfo
{
    public string Version { get; set; } = "";
    public string ReleaseDate { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public Dictionary<string, string>? DownloadUrls { get; set; }
    public string[] Changelog { get; set; } = [];
    public string Sha256 { get; set; } = "";
}
