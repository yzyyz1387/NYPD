using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

AppData.Initialize();

if (args is ["--self-check"])
{
    SelfCheck();
    return;
}

if (args.Length == 0)
{
    if (!await NativeHost.RunAsync()) DownloadManager.Run(null);
    return;
}

if (args is ["--show"])
{
    DownloadManager.Run(null);
    return;
}

if (args is ["--native-host"])
{
    await NativeHost.RunAsync();
    return;
}

if (args.Length is 2 or 3 && args[0] == "--download")
{
    DownloadManager.Run(DirectDownload.Parse(args[1], args.ElementAtOrDefault(2)));
    return;
}

await NativeHost.RunAsync();

static void SelfCheck()
{
    var download = DirectDownload.Parse("https://files.nexus-cdn.com/mods/1704/file.zip?token=temporary", "https://www.nexusmods.com/skyrimspecialedition");
    Assert(download.Url.Host == "files.nexus-cdn.com" && download.Referrer is not null);
    Assert(Rejects("https://nexusmods.com.evil.example/file.zip"));
    Assert(FileName.Safe("../bad:name?.zip", "fallback.bin") == "bad_name_.zip");
    var segments = RangeSegment.Create(10, 4);
    Assert(segments.Count == 4 && segments[0] == new RangeSegment(0, 1) && segments[3] == new RangeSegment(7, 9));
    Assert(!Downloader.ShouldUseSegments(5 * 1024 * 1024 - 1, true, 1));
    Assert(Downloader.ShouldUseSegments(64L * 1024 * 1024, true, 64));
    Console.WriteLine("self-check passed");
}

static bool Rejects(string value)
{
    try { _ = DirectDownload.Parse(value); return false; }
    catch (ArgumentException) { return true; }
}

static void Assert(bool condition)
{
    if (!condition) throw new InvalidOperationException("self-check failed");
}

sealed record DirectDownload(Uri Url, Uri? Referrer)
{
    public static DirectDownload Parse(string value, string? referrer = null)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps || !IsNexusHost(url.Host))
            throw new ArgumentException("Expected an HTTPS Nexus download URL.");

        var source = Uri.TryCreate(referrer, UriKind.Absolute, out var candidate) && candidate.Scheme == Uri.UriSchemeHttps && IsNexusHost(candidate.Host)
            ? candidate
            : null;
        return new DirectDownload(url, source);
    }

    private static bool IsNexusHost(string host) =>
        host.Equals("nexusmods.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".nexusmods.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("nexus-cdn.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".nexus-cdn.com", StringComparison.OrdinalIgnoreCase);
}

sealed class Downloader
{
    private const long BrowserSmallDownloadBytes = 5 * 1024 * 1024;
    private const long MinSegmentBytes = 1024 * 1024;
    private readonly HttpClient _http;

    public Downloader()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = true };
        if (AppData.Settings.UseProxy && !string.IsNullOrWhiteSpace(AppData.Settings.ProxyHost) && AppData.Settings.ProxyPort is > 0 and < 65536)
        {
            handler.Proxy = new WebProxy(AppData.Settings.ProxyHost, AppData.Settings.ProxyPort);
            handler.UseProxy = true;
        }
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public static string GetDownloadDirectory() => string.IsNullOrWhiteSpace(AppData.Settings.DownloadDirectory)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Nexus Mods")
        : AppData.Settings.DownloadDirectory;

    public async Task<DownloadResult> DownloadAsync(DirectDownload download, string directory, string? nameOverride, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        var paths = DownloadPaths.Create(download, directory);
        var metadata = await ProbeAsync(download, cancellationToken);
        var segmentCount = Math.Clamp(AppData.Settings.SegmentCount, 1, 128);
        var useSegments = ShouldUseSegments(metadata.Total, metadata.RangeSupported, segmentCount);
        // 分片下载合并时会同时存在分片文件和合并文件，磁盘空间按峰值估算。
        if (metadata.Total is long total) EnsureDiskSpace(paths, total, useSegments);
        if (useSegments)
            return await DownloadParallelAsync(download, paths, metadata, nameOverride, progress, cancellationToken);
        return await DownloadSingleAsync(download, paths, metadata, nameOverride, progress, cancellationToken);
    }

    public void DeletePartial(DirectDownload download, string directory)
    {
        var paths = DownloadPaths.Create(download, directory);
        if (!Directory.Exists(paths.Directory)) return;
        foreach (var path in Directory.GetFiles(paths.Directory, $"{Path.GetFileName(paths.PartPath)}*"))
            File.Delete(path);
    }

    public static bool ShouldUseSegments(long? total, bool rangeSupported, int segmentCount)
    {
        if (!rangeSupported || total is not long bytes || bytes < BrowserSmallDownloadBytes) return false;
        return bytes >= Math.Clamp(segmentCount, 1, 128) * MinSegmentBytes;
    }

    private async Task<DownloadMetadata> ProbeAsync(DirectDownload download, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(download);
        request.Headers.Range = new RangeHeaderValue(0, 0);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        ThrowIfExpired(response);
        response.EnsureSuccessStatusCode();
        var fallback = Path.GetFileName(download.Url.AbsolutePath);
        if (string.IsNullOrWhiteSpace(fallback)) fallback = "nexus-download.bin";
        var name = FileName.Safe(response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName, fallback);
        var total = response.Content.Headers.ContentRange?.Length;
        return new DownloadMetadata(name, total, response.StatusCode == HttpStatusCode.PartialContent && total is > 0);
    }

    private async Task<DownloadResult> DownloadSingleAsync(DirectDownload download, DownloadPaths paths, DownloadMetadata metadata, string? nameOverride, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        foreach (var chunk in Directory.GetFiles(paths.Directory, $"{Path.GetFileName(paths.PartPath)}.*")) File.Delete(chunk);
        var restarted = false;

        while (true)
        {
            var offset = File.Exists(paths.PartPath) ? new FileInfo(paths.PartPath).Length : 0;
            using var request = CreateRequest(download);
            if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            ThrowIfExpired(response);
            response.EnsureSuccessStatusCode();

            if (offset > 0 && (response.StatusCode != HttpStatusCode.PartialContent || response.Content.Headers.ContentRange?.From != offset))
            {
                if (restarted) throw new InvalidOperationException("The CDN does not support a safe resume for this file.");
                File.Delete(paths.PartPath);
                restarted = true;
                continue;
            }

            var total = response.Content.Headers.ContentRange?.Length;
            if (total is null && response.Content.Headers.ContentLength is long length) total = length + offset;
            if (total is null && offset > 0) total = metadata.Total;
            var reporter = new ProgressReporter(metadata.Name, total, offset, progress);
            await using var output = new FileStream(paths.PartPath, offset > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 128, useAsync: true);
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await CopyAsync(input, output, reporter, cancellationToken);
            if (total is long expected && new FileInfo(paths.PartPath).Length != expected)
                throw new IOException("Download ended before the expected file size was reached.");
            reporter.Complete();
            return Finish(paths.PartPath, paths.Directory, nameOverride ?? metadata.Name);
        }
    }

    private async Task<DownloadResult> DownloadParallelAsync(DirectDownload download, DownloadPaths paths, DownloadMetadata metadata, string? nameOverride, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        var segments = RangeSegment.Create(metadata.Total!.Value, Math.Clamp(AppData.Settings.SegmentCount, 1, 128));
        var existing = segments.Select((segment, index) => ExistingLength(paths.SegmentPath(index), segment.Length)).Sum();
        var reporter = new ProgressReporter(metadata.Name, metadata.Total, existing, progress);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var tasks = segments.Select((segment, index) => DownloadSegmentAsync(download, paths.SegmentPath(index), segment, reporter, linkedCancellation.Token)).ToArray();
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (RangeNotSupportedException)
        {
            linkedCancellation.Cancel();
            try { await Task.WhenAll(tasks); } catch { }
            foreach (var chunk in Directory.GetFiles(paths.Directory, $"{Path.GetFileName(paths.PartPath)}.*")) File.Delete(chunk);
            return await DownloadSingleAsync(download, paths, metadata, nameOverride, progress, cancellationToken);
        }
        catch
        {
            linkedCancellation.Cancel();
            try { await Task.WhenAll(tasks); } catch { }
            throw;
        }

        reporter.Status("合并中");
        await using (var output = new FileStream(paths.PartPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 128, useAsync: true))
        {
            // 分片文件依赖实际长度判断续传，只在最终合并文件上预设长度。
            output.SetLength(metadata.Total.Value);
            output.Position = 0;
            for (var index = 0; index < segments.Count; index++)
            {
                await using var input = new FileStream(paths.SegmentPath(index), FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, useAsync: true);
                await CopyExactlyAsync(input, output, segments[index].Length, cancellationToken);
            }
        }
        if (new FileInfo(paths.PartPath).Length != metadata.Total) throw new IOException("合并后的文件大小与预期不一致。");
        foreach (var segment in segments.Select((_, index) => paths.SegmentPath(index))) File.Delete(segment);
        reporter.Complete();
        return Finish(paths.PartPath, paths.Directory, nameOverride ?? metadata.Name);
    }

    private async Task DownloadSegmentAsync(DirectDownload download, string path, RangeSegment segment, ProgressReporter reporter, CancellationToken cancellationToken)
    {
        var existing = ExistingLength(path, segment.Length);
        var retries = 0;
        while (existing < segment.Length)
        {
            try
            {
                using var request = CreateRequest(download);
                request.Headers.Range = new RangeHeaderValue(segment.Start + existing, segment.End);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                ThrowIfExpired(response);
                if (response.StatusCode != HttpStatusCode.PartialContent || response.Content.Headers.ContentRange?.From != segment.Start + existing)
                    throw new RangeNotSupportedException();
                await using var output = new FileStream(path, existing > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 128, useAsync: true);
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                await CopyAsync(input, output, reporter, cancellationToken, segment.Length - existing);
                var updated = new FileInfo(path).Length;
                if (updated == existing) throw new IOException("Segment made no progress.");
                existing = updated;
                retries = 0;
            }
            catch (IOException) when (retries++ < 3)
            {
                await Task.Delay(300, cancellationToken);
                existing = ExistingLength(path, segment.Length);
            }
        }
        if (existing != segment.Length) throw new IOException("分段下载提前结束。");
    }

    private static long ExistingLength(string path, long maximum)
    {
        if (!File.Exists(path)) return 0;
        var length = new FileInfo(path).Length;
        if (length <= maximum) return length;
        File.Delete(path);
        return 0;
    }

    private static void EnsureDiskSpace(DownloadPaths paths, long total, bool rangeDownload)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(paths.Directory));
        if (string.IsNullOrWhiteSpace(root)) return;
        var existing = ExistingBytes(paths);
        var peakBytes = (rangeDownload ? total * 2 : total) - existing;
        if (peakBytes <= 0) return;
        var available = new DriveInfo(root).AvailableFreeSpace;
        if (available < peakBytes)
            throw new IOException($"磁盘剩余空间不足。需要至少 {FileName.FormatBytes(peakBytes)}，当前可用 {FileName.FormatBytes(available)}。");
    }

    private static long ExistingBytes(DownloadPaths paths)
    {
        if (!Directory.Exists(paths.Directory)) return 0;
        return Directory.GetFiles(paths.Directory, $"{Path.GetFileName(paths.PartPath)}*").Sum(path => new FileInfo(path).Length);
    }

    private static async Task CopyAsync(Stream input, Stream output, ProgressReporter reporter, CancellationToken cancellationToken, long? maximumBytes = null)
    {
        var buffer = GC.AllocateUninitializedArray<byte>(1024 * 128);
        long written = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            var bytes = maximumBytes is long max ? (int)Math.Min(count, max - written) : count;
            if (bytes <= 0) return;
            await output.WriteAsync(buffer.AsMemory(0, bytes), cancellationToken);
            reporter.Add(bytes);
            written += bytes;
        }
    }

    private static async Task CopyExactlyAsync(Stream input, Stream output, long bytes, CancellationToken cancellationToken)
    {
        var buffer = GC.AllocateUninitializedArray<byte>(1024 * 128);
        var remaining = bytes;
        while (remaining > 0)
        {
            var count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
            if (count == 0) throw new IOException("分片文件大小不足，无法完成合并。");
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            remaining -= count;
        }
    }

    private static DownloadResult Finish(string partPath, string directory, string name)
    {
        var destination = FileName.UniquePath(directory, name);
        File.Move(partPath, destination);
        return new DownloadResult(destination, name);
    }

    private static HttpRequestMessage CreateRequest(DirectDownload download)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, download.Url);
        request.Headers.UserAgent.ParseAdd("NexusModsDownloader/0.4");
        if (download.Referrer is not null) request.Headers.Referrer = download.Referrer;
        return request;
    }

    private static void ThrowIfExpired(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            throw new HttpRequestException("The captured download link expired. Start the download again in Edge.", null, response.StatusCode);
    }

    private sealed record DownloadMetadata(string Name, long? Total, bool RangeSupported);
}

sealed class ProgressReporter
{
    private readonly string _name;
    private readonly long? _total;
    private readonly IProgress<DownloadProgress>? _progress;
    private readonly object _sync = new();
    private long _received;
    private long _lastReceived;
    private long _lastTimestamp;
    private double _lastSpeed;

    public ProgressReporter(string name, long? total, long received, IProgress<DownloadProgress>? progress)
    {
        _name = name;
        _total = total;
        _received = received;
        _lastReceived = received;
        _lastTimestamp = Stopwatch.GetTimestamp();
        _progress = progress;
            _progress?.Report(new DownloadProgress(_name, _received, _total, 0));
    }

    public void Add(int bytes)
    {
        Interlocked.Add(ref _received, bytes);
        lock (_sync)
        {
            var now = Stopwatch.GetTimestamp();
            var elapsed = Stopwatch.GetElapsedTime(_lastTimestamp, now);
            if (elapsed < TimeSpan.FromMilliseconds(200)) return;
            var received = Interlocked.Read(ref _received);
            _lastSpeed = (received - _lastReceived) / elapsed.TotalSeconds;
            _lastReceived = received;
            _lastTimestamp = now;
            _progress?.Report(new DownloadProgress(_name, received, _total, _lastSpeed));
        }
    }

    public void Complete() => _progress?.Report(new DownloadProgress(_name, Interlocked.Read(ref _received), _total, _lastSpeed));

    public void Status(string value) => _progress?.Report(new DownloadProgress(_name, Interlocked.Read(ref _received), _total, _lastSpeed, value));
}

sealed record DownloadProgress(string Name, long Received, long? Total, double SpeedBytesPerSecond, string? Status = null);
sealed record DownloadResult(string Destination, string Name);

sealed record DownloadPaths(string Directory, string PartPath)
{
    public string SegmentPath(int index) => $"{PartPath}.{index}";

    public static DownloadPaths Create(DirectDownload download, string directory)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(download.Url.GetLeftPart(UriPartial.Path))))[..16].ToLowerInvariant();
        return new DownloadPaths(directory, Path.Combine(directory, $"nexus-{key}.part"));
    }
}

sealed record RangeSegment(long Start, long End)
{
    public long Length => End - Start + 1;

    public static IReadOnlyList<RangeSegment> Create(long total, int count)
    {
        var actual = (int)Math.Min(count, Math.Max(1, total));
        return Enumerable.Range(0, actual)
            .Select(index => new RangeSegment(total * index / actual, total * (index + 1) / actual - 1))
            .ToArray();
    }
}

sealed class RangeNotSupportedException : Exception;

static class FileName
{
    public static string Safe(string? value, string fallback)
    {
        var source = value?.Trim().Trim('"') ?? fallback;
        try { source = Uri.UnescapeDataString(source); } catch (UriFormatException) { }
        var name = Path.GetFileName(source);
        if (string.IsNullOrWhiteSpace(name)) name = fallback;
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(character => invalid.Contains(character) ? '_' : character));
    }

    public static string UniquePath(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path)) return path;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        return Path.Combine(directory, $"{stem} ({DateTime.Now:yyyyMMdd-HHmmss}){extension}");
    }

    public static string FormatBytes(long value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var size = (double)value;
        var index = 0;
        while (size >= 1024 && index < units.Length - 1) { size /= 1024; index++; }
        return $"{size:0.0} {units[index]}";
    }
}

static class DownloadManager
{
    private const string MutexName = "NexusModsDownloader.Manager";
    private const string PipeName = "NexusModsDownloader.Queue";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void Run(DirectDownload? initial)
    {
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var isPrimary);
        if (!isPrimary)
        {
            if (initial is not null) SendToRunningWindow(initial);
            return;
        }

        Exception? uiError = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new System.Windows.Application();
                var window = new NexusModsDownloader.MainWindow(PipeName, Json);
                if (initial is not null) window.Loaded += (_, _) => window.Enqueue(initial);
                app.Run(window);
            }
            catch (Exception error)
            {
                uiError = error;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (uiError is not null) throw uiError;
    }

    private static void SendToRunningWindow(DirectDownload download)
    {
        var message = JsonSerializer.Serialize(new QueueMessage(download.Url.OriginalString, download.Referrer?.OriginalString), Json);
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                pipe.Connect(250);
                using var writer = new StreamWriter(pipe, Encoding.UTF8, leaveOpen: true);
                writer.WriteLine(message);
                writer.Flush();
                return;
            }
            catch (TimeoutException)
            {
                Thread.Sleep(100);
            }
        }
        throw new InvalidOperationException("The download manager did not become ready in time.");
    }

    public sealed record QueueMessage(string DownloadUrl, string? Referrer);
}


enum DownloadMode { Queued, Downloading, Paused, Cancelled, Completed, Failed }

sealed class DownloadTask : INotifyPropertyChanged
{
    private string _name = "";
    private string _status = "";
    private string _progress = "";
    private string _speed = "";
    private string _destination = "";
    private double _progressPercent;
    private DownloadMode _mode;
    private bool _isSelected;

    public DownloadTask(DirectDownload download) => Download = download;
    public DirectDownload Download { get; }
    public string TargetDirectory { get; set; } = Downloader.GetDownloadDirectory();
    public string? NameOverride { get; set; }
    public DownloadMode Mode { get => _mode; set => Set(ref _mode, value); }
    public bool CancelRequested { get; set; }
    public bool HasStarted { get; set; }
    public CancellationTokenSource? Cancellation { get; set; }
    public string Name { get => _name; set => Set(ref _name, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public string Progress { get => _progress; set => Set(ref _progress, value); }
    public string Speed { get => _speed; set => Set(ref _speed, value); }
    public string Destination { get => _destination; set => Set(ref _destination, value); }
    public double ProgressPercent { get => _progressPercent; set => Set(ref _progressPercent, value); }
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
    public string PauseText => Mode is DownloadMode.Paused or DownloadMode.Failed ? "继续" : "暂停";
    public string PauseIcon => Mode is DownloadMode.Paused or DownloadMode.Failed ? "Play" : "Pause";
    public bool CanPauseResume => Mode is DownloadMode.Queued or DownloadMode.Downloading or DownloadMode.Paused or DownloadMode.Failed;
    public bool CanCancel => Mode is DownloadMode.Queued or DownloadMode.Downloading or DownloadMode.Paused or DownloadMode.Failed;
    public string StatusDisplay => Mode switch
    {
        DownloadMode.Completed => "成功",
        DownloadMode.Failed => "失败",
        _ => Status
    };
    public bool IsCompleted => Mode == DownloadMode.Completed;
    public bool IsFailed => Mode == DownloadMode.Failed;
    public bool DestinationExists => Mode != DownloadMode.Completed || File.Exists(Destination);
    public string MissingTip => DestinationExists ? "打开文件所在位置" : "该文件被移动或已删除";
    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        if (property == nameof(Mode))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PauseText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PauseIcon)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanPauseResume)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanCancel)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusDisplay)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCompleted)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFailed)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DestinationExists)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MissingTip)));
        }
        if (property == nameof(Status)) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusDisplay)));
        if (property == nameof(Destination))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DestinationExists)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MissingTip)));
        }
    }
}

static class NativeHost
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    public static async Task<bool> RunAsync()
    {
        var handled = false;
        while (await ReadAsync(Console.OpenStandardInput()) is { } message)
        {
            handled = true;
            try
            {
                if (message.Type == "ping")
                {
                    await WriteAsync(new NativeResponse(true, null));
                    continue;
                }
                if (message.Type != "download" || string.IsNullOrWhiteSpace(message.DownloadUrl)) throw new ArgumentException("Invalid native message.");
                StartWorker(DirectDownload.Parse(message.DownloadUrl, message.Referrer));
                await WriteAsync(new NativeResponse(true, null));
            }
            catch (Exception error)
            {
                await WriteAsync(new NativeResponse(false, error.Message));
            }
        }
        return handled;
    }

    private static void StartWorker(DirectDownload download)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate the downloader executable.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
        start.ArgumentList.Add("--download");
        start.ArgumentList.Add(download.Url.OriginalString);
        if (download.Referrer is not null) start.ArgumentList.Add(download.Referrer.OriginalString);
        if (Process.Start(start) is null) throw new InvalidOperationException("Could not start the download manager.");
    }

    private static async Task<NativeRequest?> ReadAsync(Stream input)
    {
        var lengthBytes = await ReadExactlyAsync(input, 4);
        if (lengthBytes is null) return null;
        var length = BitConverter.ToInt32(lengthBytes);
        if (length is < 1 or > 1024 * 1024) throw new InvalidDataException("Invalid native message length.");
        var payload = await ReadExactlyAsync(input, length) ?? throw new EndOfStreamException();
        return JsonSerializer.Deserialize<NativeRequest>(payload, Json) ?? throw new InvalidDataException("Invalid native message JSON.");
    }

    private static async Task<byte[]?> ReadExactlyAsync(Stream stream, int length)
    {
        var buffer = new byte[length];
        var read = 0;
        while (read < length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(read, length - read));
            if (count == 0) return read == 0 ? null : throw new EndOfStreamException();
            read += count;
        }
        return buffer;
    }

    private static async Task WriteAsync(NativeResponse response)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(response, Json);
        var output = Console.OpenStandardOutput();
        await output.WriteAsync(BitConverter.GetBytes(payload.Length));
        await output.WriteAsync(payload);
        await output.FlushAsync();
    }

    private sealed record NativeRequest(string Type, string DownloadUrl, string? Referrer);
    private sealed record NativeResponse(bool Ok, string? Error);
}
