using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;
using Clipboard = System.Windows.Clipboard;
using RadioButton = System.Windows.Controls.RadioButton;

namespace NexusModsDownloader;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<global::DownloadTask> _tasks = [];
    private readonly ObservableCollection<global::HistoryItem> _history = new(global::AppData.History);
    private readonly Queue<global::DownloadTask> _queue = new();
    private global::Downloader _downloader = new();
    private readonly CancellationTokenSource _pipeCancellation = new();
    private readonly Dictionary<string, string> _themeColors = new()
    {
        ["#3898fc"] = "#276ab0",
        ["#4fc3f7"] = "#0288d1",
        ["#009688"] = "#00695f",
        ["#18a96e"] = "#10764d",
        ["#f50057"] = "#ab003c"
    };
    private readonly string _pipeName;
    private readonly JsonSerializerOptions _json;
    private readonly System.Windows.Threading.DispatcherTimer _settingsSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private bool _processing;
    private bool _multiSelectMode;
    private bool _loadingSettings = true;
    private bool _settingsDialogOpen;

    public MainWindow(string pipeName, JsonSerializerOptions json)
    {
        InitializeComponent();
        _pipeName = pipeName;
        _json = json;
        _settingsSaveTimer.Tick += (_, _) =>
        {
            _settingsSaveTimer.Stop();
            if (!_loadingSettings) TrySaveSettings(false);
        };

        DownloadsGrid.ItemsSource = _tasks;
        HistoryGrid.ItemsSource = _history;
        LoadLogText(global::AppData.ReadLog());

        ApplyTheme(global::AppData.Settings.ThemeColor, global::AppData.Settings.ThemeDarkColor);
        LoadSettings();
        VersionTextBlock.Text = $"版本 {typeof(global::Downloader).Assembly.GetName().Version}";
        RefreshAboutText();
        UpdateBulkPauseButton();

        global::AppData.Logged += AppendLog;
        global::AppData.LogStartupSeparator();
        Loaded += (_, _) =>
        {
            _ = ListenAsync();
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                await CheckUpdateInteractiveAsync(true);
                PromptBrowserExtensionInstallIfNeeded();
            }));
            ScrollLogToEnd();
        };
        Closed += (_, _) =>
        {
            _pipeCancellation.Cancel();
            global::AppData.Logged -= AppendLog;
        };
    }

    internal async Task EnqueueAsync(global::DirectDownload download)
    {
        if (!Dispatcher.CheckAccess())
        {
            await await Dispatcher.InvokeAsync(() => EnqueueAsync(download));
            return;
        }

        BringToFront();

        var initialName = await GuessFileNameAsync(download);
        var directory = global::AppData.Settings.DownloadDirectory;
        var nameOverride = "";

        if (global::AppData.Settings.AskBeforeDownload)
        {
            var options = new DownloadOptionsWindow(initialName, directory, global::AppData.Settings.AskBeforeDownload) { Owner = this };
            if (options.ShowDialog() != true) return;
            directory = options.DirectoryPath;
            nameOverride = options.FileNameValue;
            global::AppData.Settings.AskBeforeDownload = options.AskEveryTime;
            global::AppData.SaveSettings();
            LoadSettings();
        }

        if (string.IsNullOrWhiteSpace(directory)) directory = global::Downloader.GetDownloadDirectory();
        var displayName = string.IsNullOrWhiteSpace(nameOverride) ? initialName : nameOverride;
        var task = new global::DownloadTask(download)
        {
            TargetDirectory = directory,
            NameOverride = string.IsNullOrWhiteSpace(nameOverride) ? null : nameOverride,
            Name = string.IsNullOrWhiteSpace(displayName) ? download.Url.Host : displayName,
            Destination = directory,
            Status = "等待中",
            Progress = "-",
            Speed = "-",
            ProgressPercent = 0,
            Mode = global::DownloadMode.Queued
        };

        task.PropertyChanged += (_, _) => UpdateBulkPauseButton();
        _tasks.Add(task);
        _queue.Enqueue(task);
        global::AppData.Log($"已加入队列: {task.Name}");
        UpdateBulkPauseButton();
        if (!_processing) _ = ProcessQueueAsync();
    }

    private async Task<string> GuessFileNameAsync(global::DirectDownload download)
    {
        var suggested = global::FileName.Safe(download.SuggestedFileName, "");
        if (!string.IsNullOrWhiteSpace(suggested) && Path.HasExtension(suggested)) return suggested;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, download.Url);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
            request.Headers.UserAgent.ParseAdd("NexusModsDownloader/0.4");
            if (download.Referrer is not null) request.Headers.Referrer = download.Referrer;
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if (response.IsSuccessStatusCode)
            {
                var headerName = response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName;
                var name = global::FileName.Safe(headerName, "");
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
        }
        catch (Exception error)
        {
            global::AppData.Log($"读取下载文件名失败: {error.Message}", global::AppLogLevel.Warning);
        }

        return global::FileName.Safe(suggested, global::FileName.Safe(Path.GetFileName(download.Url.AbsolutePath), "nexus-download.bin"));
    }

    private async Task ProcessQueueAsync()
    {
        _processing = true;
        while (_queue.TryDequeue(out var task))
        {
            if (task.Mode != global::DownloadMode.Queued || task.CancelRequested) continue;
            task.Mode = global::DownloadMode.Downloading;
            task.HasStarted = true;
            task.Status = "下载中";
            task.Cancellation = new CancellationTokenSource();
            try
            {
                var progress = new Progress<global::DownloadProgress>(value =>
                {
                    if (!string.IsNullOrWhiteSpace(value.Status)) task.Status = value.Status;
                    task.Name = task.NameOverride ?? value.Name;
                    if (value.Total is long total && total > 0)
                    {
                        var percent = Math.Min(100, value.Received * 100d / total);
                        var received = Math.Min(value.Received, total);
                        task.ProgressPercent = percent;
                        task.Progress = $"{FormatBytes(received)} / {FormatBytes(total)} ({percent:F1}%)";
                    }
                    else
                    {
                        task.Progress = FormatBytes(value.Received);
                    }
                    task.Speed = value.SpeedBytesPerSecond > 0 ? $"{FormatBytes((long)value.SpeedBytesPerSecond)}/s" : "-";
                });

                var result = await _downloader.DownloadAsync(task.Download, task.TargetDirectory, task.NameOverride, progress, task.Cancellation.Token);
                task.Name = result.Name;
                task.Mode = global::DownloadMode.Completed;
                task.Status = "完成";
                task.Progress = "100%";
                task.ProgressPercent = 100;
                task.Speed = "-";
                task.Destination = result.Destination;

                var historyItem = new global::HistoryItem(DateTime.Now, result.Name, result.Destination, "完成");
                global::AppData.AddHistory(historyItem);
                _history.Insert(0, historyItem);
                global::AppData.Log($"已完成: {result.Destination}", global::AppLogLevel.Success);
            }
            catch (OperationCanceledException) when (task.Cancellation.IsCancellationRequested)
            {
                task.Speed = "-";
                if (task.CancelRequested)
                {
                    SafeDeletePartial(task);
                    task.Mode = global::DownloadMode.Cancelled;
                    task.Status = "已取消";
                    task.ProgressPercent = 0;
                }
                else
                {
                    task.Mode = global::DownloadMode.Paused;
                    task.Status = "已暂停";
                }
            }
            catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            {
                task.Mode = global::DownloadMode.Failed;
                task.Speed = "-";
                task.Status = "链接已过期，请在 Edge 重新下载";
                global::AppData.Log($"下载失败: {task.Name} - 链接已过期", global::AppLogLevel.Error);
            }
            catch (Exception error)
            {
                task.Mode = global::DownloadMode.Failed;
                task.Speed = "-";
                task.Status = ToChineseStatus(error.Message);
                global::AppData.Log($"下载失败: {task.Name} - {task.Status}", global::AppLogLevel.Error);
            }
            finally
            {
                task.Cancellation?.Dispose();
                task.Cancellation = null;
                UpdateBulkPauseButton();
            }
        }
        _processing = false;
        UpdateBulkPauseButton();
    }

    private void ToggleAllPause_Click(object sender, RoutedEventArgs e)
    {
        if (_tasks.Any(task => task.Mode == global::DownloadMode.Paused))
        {
            foreach (var task in _tasks.Where(task => task.Mode == global::DownloadMode.Paused).ToArray())
                ResumeTask(task);
        }
        else
        {
            foreach (var task in _tasks.Where(task => task.Mode is global::DownloadMode.Downloading or global::DownloadMode.Queued).ToArray())
                PauseTask(task);
        }
        UpdateBulkPauseButton();
    }

    private void CancelAll_Click(object sender, RoutedEventArgs e)
    {
        var result = ConfirmWindow.Ask(this, "全部取消", "确定取消所有未完成的下载吗？未完成的分片文件会被删除。", "全部取消");
        if (!result.Confirmed) return;
        foreach (var task in _tasks.Where(task => task.Mode is not (global::DownloadMode.Completed or global::DownloadMode.Cancelled)).ToArray())
            CancelTask(task);
    }

    private void ToggleRowPause_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not global::DownloadTask task) return;
        if (task.Mode is global::DownloadMode.Paused or global::DownloadMode.Failed) ResumeTask(task);
        else PauseTask(task);
    }

    private void CancelRow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is global::DownloadTask task) CancelTask(task);
    }

    private void StatusText_Click(object sender, MouseButtonEventArgs e)
    {
        LogsNavButton.IsChecked = true;
        ShowPage("Logs");
    }

    private void ManualUrlTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        _ = StartManualDownloadAsync();
    }

    private async void ManualDownload_Click(object sender, RoutedEventArgs e) => await StartManualDownloadAsync();

    private async Task StartManualDownloadAsync()
    {
        try
        {
            var download = global::DirectDownload.Parse(ManualUrlTextBox.Text.Trim());
            ManualUrlTextBox.Clear();
            await EnqueueAsync(download);
        }
        catch (Exception ex)
        {
            global::AppData.Log($"手动下载链接无效: {ex.Message}", global::AppLogLevel.Warning);
            ConfirmWindow.Ask(this, "下载链接无效", "请输入 Nexus Mods 或 nexus-cdn.com 的 HTTPS 下载链接。", "知道了");
        }
    }

    private void OpenRowLocation_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is global::DownloadTask task) OpenLocation(task);
    }

    private void OpenDownloadPath_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is global::DownloadTask task) OpenLocation(task);
    }

    private void PauseTask(global::DownloadTask task)
    {
        if (task.Mode == global::DownloadMode.Downloading)
        {
            task.Status = "正在暂停";
            task.Cancellation?.Cancel();
        }
        else if (task.Mode == global::DownloadMode.Queued)
        {
            task.Mode = global::DownloadMode.Paused;
            task.Status = "已暂停";
        }
    }

    private void ResumeTask(global::DownloadTask task)
    {
        if (task.Mode is not (global::DownloadMode.Paused or global::DownloadMode.Failed)) return;
        task.CancelRequested = false;
        task.Mode = global::DownloadMode.Queued;
        task.Status = "等待中";
        task.Speed = "-";
        _queue.Enqueue(task);
        if (!_processing) _ = ProcessQueueAsync();
    }

    private void CancelTask(global::DownloadTask task)
    {
        if (task.Mode is global::DownloadMode.Completed or global::DownloadMode.Cancelled) return;
        task.CancelRequested = true;
        if (task.Mode == global::DownloadMode.Downloading)
        {
            task.Status = "正在取消";
            task.Cancellation?.Cancel();
            return;
        }
        if (task.HasStarted) SafeDeletePartial(task);
        task.Mode = global::DownloadMode.Cancelled;
        task.Status = "已取消";
        task.Speed = "-";
        task.ProgressPercent = 0;
    }

    private void DeleteRecord_Click(object sender, RoutedEventArgs e)
    {
        var task = SelectedTask();
        if (task is null) return;
        var result = ConfirmWindow.Ask(this, "删除下载记录", $"确定删除记录“{task.Name}”吗？", "删除记录", "同时删除文件");
        if (!result.Confirmed) return;
        if (result.OptionChecked) DeleteFileFor(task);
        CancelTask(task);
        _tasks.Remove(task);
    }

    private void DeleteFile_Click(object sender, RoutedEventArgs e)
    {
        var task = SelectedTask();
        if (task is null) return;
        var result = ConfirmWindow.Ask(this, "删除文件", $"确定删除“{task.Name}”对应的文件吗？", "删除文件", "同时删除下载记录");
        if (!result.Confirmed) return;
        DeleteFileFor(task);
        if (result.OptionChecked) _tasks.Remove(task);
    }

    private void DeleteFileFor(global::DownloadTask task)
    {
        if (task.Mode == global::DownloadMode.Downloading)
        {
            task.CancelRequested = true;
            task.Status = "正在删除";
            task.Cancellation?.Cancel();
            return;
        }
        CancelTask(task);
        if (File.Exists(task.Destination))
            File.Delete(task.Destination);
        else
            if (task.HasStarted) SafeDeletePartial(task);
        task.Status = "文件已删除";
        task.Speed = "-";
        global::AppData.Log($"已删除文件: {task.Name}", global::AppLogLevel.Warning);
    }

    private void SafeDeletePartial(global::DownloadTask task)
    {
        try { _downloader.DeletePartial(task.Download, task.TargetDirectory); }
        catch (Exception ex) { global::AppData.Log($"清理分片失败: {task.Name} - {ex.Message}", global::AppLogLevel.Warning); }
    }

    private void OpenSelectedLocation_Click(object sender, RoutedEventArgs e)
    {
        var task = SelectedTask();
        if (task is not null) OpenLocation(task);
    }

    private void ToggleMultiSelect_Click(object sender, RoutedEventArgs e)
    {
        _multiSelectMode = !_multiSelectMode;
        MultiSelectColumn.Visibility = _multiSelectMode ? Visibility.Visible : Visibility.Collapsed;
        DeleteSelectedButton.Visibility = _multiSelectMode ? Visibility.Visible : Visibility.Collapsed;
        MultiSelectText.Text = _multiSelectMode ? "退出多选" : "多选模式";
        if (!_multiSelectMode)
        {
            foreach (var task in _tasks) task.IsSelected = false;
        }
    }

    private void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = _tasks.Where(task => task.IsSelected).ToArray();
        if (selected.Length == 0)
        {
            ConfirmWindow.Ask(this, "删除选中", "还没有选择要删除的下载记录。", "知道了");
            return;
        }

        var result = ConfirmWindow.Ask(this, "删除选中", $"确定删除选中的 {selected.Length} 条下载记录吗？", "删除", "同时删除文件");
        if (!result.Confirmed) return;
        foreach (var task in selected)
        {
            if (result.OptionChecked) DeleteFileFor(task);
            else CancelTask(task);
            _tasks.Remove(task);
        }
    }

    private void CopyDownloadUrl_Click(object sender, RoutedEventArgs e)
    {
        var task = SelectedTask();
        if (task is not null) Clipboard.SetText(task.Download.Url.OriginalString);
    }

    private void CopyFilePath_Click(object sender, RoutedEventArgs e)
    {
        var task = SelectedTask();
        if (task is not null) Clipboard.SetText(task.Destination);
    }

    private void OpenDownloadFolder_Click(object sender, RoutedEventArgs e)
    {
        var directory = global::Downloader.GetDownloadDirectory();
        Directory.CreateDirectory(directory);
        OpenPath(directory);
    }

    private void OpenLocation(global::DownloadTask task)
    {
        if (task.Mode == global::DownloadMode.Completed && !File.Exists(task.Destination)) return;
        if (File.Exists(task.Destination))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{task.Destination}\"") { UseShellExecute = true });
            return;
        }
        var directory = Directory.Exists(task.Destination) ? task.Destination : task.TargetDirectory;
        Directory.CreateDirectory(directory);
        OpenPath(directory);
    }

    private void OpenHistoryPath_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is global::HistoryItem item) OpenHistoryLocation(item);
    }

    private void OpenHistoryLocation_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedHistoryItem() is { } item) OpenHistoryLocation(item);
    }

    private void CopyHistoryPath_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedHistoryItem() is { } item) Clipboard.SetText(item.Destination);
    }

    private void DeleteHistoryRecord_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedHistoryItem() is not { } item) return;
        var result = ConfirmWindow.Ask(this, "删除历史记录", $"确定删除历史记录“{item.Name}”吗？", "删除");
        if (!result.Confirmed) return;
        global::AppData.RemoveHistory(item);
        _history.Remove(item);
    }

    private void OpenHistoryLocation(global::HistoryItem item)
    {
        if (!File.Exists(item.Destination)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.Destination}\"") { UseShellExecute = true });
    }

    private void DownloadsGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var row = FindParent<DataGridRow>((DependencyObject)e.OriginalSource);
        if (row is null) return;
        row.IsSelected = true;
        DownloadsGrid.SelectedItem = row.Item;
    }

    private global::DownloadTask? SelectedTask() => DownloadsGrid.SelectedItem as global::DownloadTask;

    private void HistoryGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var row = FindParent<DataGridRow>((DependencyObject)e.OriginalSource);
        if (row is null) return;
        row.IsSelected = true;
        HistoryGrid.SelectedItem = row.Item;
    }

    private global::HistoryItem? SelectedHistoryItem() => HistoryGrid.SelectedItem as global::HistoryItem;

    private async void BrowseDefaultDirectory_Click(object sender, RoutedEventArgs e)
    {
        var selected = await global::FolderPicker.PickAsync(DefaultDirectoryTextBox.Text);
        if (!string.IsNullOrWhiteSpace(selected)) DefaultDirectoryTextBox.Text = selected;
    }

    private void SettingsTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingSettings) return;
        _settingsSaveTimer.Stop();
        _settingsSaveTimer.Start();
    }

    private void SettingsField_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _settingsSaveTimer.Stop();
        TrySaveSettings(true);
    }

    private void SettingsField_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _settingsSaveTimer.Stop();
        TrySaveSettings(true);
    }

    private bool TrySaveSettings(bool showAlert)
    {
        var portValid = int.TryParse(ProxyPortTextBox.Text.Trim(), out var port) && port is >= 1 and <= 65535;
        var segmentValid = int.TryParse(SegmentCountTextBox.Text.Trim(), out var segmentCount) && segmentCount is >= 1 and <= 128;
        MarkSettingInvalid(ProxyPortTextBox, !portValid);
        MarkSettingInvalid(SegmentCountTextBox, !segmentValid);

        if (!portValid || !segmentValid)
        {
            if (showAlert && !_settingsDialogOpen)
            {
                _settingsDialogOpen = true;
                var message = string.Join("\n", new[]
                {
                    portValid ? "" : "代理端口需要是 1 到 65535 之间的数字。",
                    segmentValid ? "" : "下载分片数需要是 1 到 128 之间的数字。"
                }.Where(item => item.Length > 0));
                ConfirmWindow.Ask(this, "设置无效", message, "知道了");
                _settingsDialogOpen = false;
            }
            return false;
        }

        var directory = string.IsNullOrWhiteSpace(DefaultDirectoryTextBox.Text)
            ? global::Downloader.GetDownloadDirectory()
            : DefaultDirectoryTextBox.Text.Trim();
        var askBeforeDownload = AskBeforeCheckBox.IsChecked == true;
        var useProxy = UseProxyCheckBox.IsChecked == true;
        var proxyHost = string.IsNullOrWhiteSpace(ProxyHostTextBox.Text) ? "127.0.0.1" : ProxyHostTextBox.Text.Trim();
        var settings = global::AppData.Settings;
        var changes = new List<string>();
        AddChange("下载目录", settings.DownloadDirectory, directory);
        AddChange("下载前确认窗口", BoolText(settings.AskBeforeDownload), BoolText(askBeforeDownload));
        AddChange("代理下载", BoolText(settings.UseProxy), BoolText(useProxy));
        AddChange("代理地址", settings.ProxyHost, proxyHost);
        AddChange("代理端口", settings.ProxyPort.ToString(), port.ToString());
        AddChange("下载分片数", settings.SegmentCount.ToString(), segmentCount.ToString());
        if (settings.DownloadDirectory == directory
            && settings.AskBeforeDownload == askBeforeDownload
            && settings.UseProxy == useProxy
            && settings.ProxyHost == proxyHost
            && settings.ProxyPort == port
            && settings.SegmentCount == segmentCount)
            return true;

        settings.DownloadDirectory = directory;
        settings.AskBeforeDownload = askBeforeDownload;
        settings.UseProxy = useProxy;
        settings.ProxyHost = proxyHost;
        settings.ProxyPort = port;
        settings.SegmentCount = segmentCount;
        global::AppData.SaveSettings();
        _downloader = new global::Downloader();
        RefreshAboutText();
        global::AppData.Log($"设置已变更：{string.Join("；", changes)}", global::AppLogLevel.System);
        return true;

        void AddChange(string name, string oldValue, string newValue)
        {
            if (oldValue != newValue) changes.Add($"{name}：{DisplayValue(oldValue)} -> {DisplayValue(newValue)}");
        }
    }

    private static string BoolText(bool value) => value ? "开启" : "关闭";
    private static string DisplayValue(string value) => string.IsNullOrWhiteSpace(value) ? "空" : value;

    private static void MarkSettingInvalid(System.Windows.Controls.TextBox textBox, bool invalid)
    {
        if (invalid)
        {
            textBox.BorderBrush = System.Windows.Media.Brushes.IndianRed;
            textBox.Foreground = System.Windows.Media.Brushes.IndianRed;
            return;
        }
        textBox.ClearValue(System.Windows.Controls.Control.BorderBrushProperty);
        textBox.ClearValue(System.Windows.Controls.Control.ForegroundProperty);
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e) => await CheckUpdateInteractiveAsync(false);

    private async void CheckUpdatePlaceholder_Click(object sender, RoutedEventArgs e) => await CheckUpdateInteractiveAsync(false);

    private async Task CheckUpdateInteractiveAsync(bool startup)
    {
        try
        {
            global::AppData.Log("正在检查更新", global::AppLogLevel.System);
            var info = await global::UpdateChecker.CheckForUpdatesAsync();
            if (!global::UpdateChecker.IsNewVersionAvailable(global::UpdateChecker.CurrentVersion, info.Version))
            {
                if (!startup) ConfirmWindow.Ask(this, "检查更新", "当前已是最新版本。", "知道了");
                return;
            }
            if (startup && info.Version == global::AppData.Settings.IgnoredUpdateVersion)
            {
                global::AppData.Log($"已忽略版本 {info.Version}", global::AppLogLevel.System);
                return;
            }

            if (startup) BringToFront();
            var changelog = info.Changelog.Length == 0 ? "暂无更新说明" : string.Join(Environment.NewLine, info.Changelog.Select(item => "• " + item));
            var result = ConfirmWindow.Ask(
                this,
                "发现新版本",
                $"发现新版本 {info.Version}\n发布日期：{info.ReleaseDate}\n\n更新内容：\n{changelog}\n\n是否下载并准备更新？",
                "下载并更新");
            if (!result.Confirmed) return;

            var script = await DownloadUpdateWithProgressAsync(info);
            var restart = ConfirmWindow.Ask(this, "更新准备就绪", "更新文件已准备就绪，需要重启软件完成覆盖。是否立即重启？", "立即重启");
            if (!restart.Confirmed) return;

            Process.Start(new ProcessStartInfo(script) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            global::AppData.Log($"检查更新失败: {ex.Message}", global::AppLogLevel.Error);
            if (!startup) ConfirmWindow.Ask(this, "检查更新失败", ex.Message, "知道了");
        }
    }

    private async Task<string> DownloadUpdateWithProgressAsync(global::UpdateInfo info)
    {
        var progressWindow = new UpdateProgressWindow(info.Version) { Owner = this };
        var progress = new Progress<double>(progressWindow.SetProgress);
        progressWindow.Show();
        try
        {
            return await global::UpdateChecker.DownloadAndPrepareUpdateAsync(info, progress);
        }
        finally
        {
            progressWindow.Close();
        }
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        var result = ConfirmWindow.Ask(this, "清理日志", "确定清理所有操作日志吗？", "清理");
        if (!result.Confirmed) return;
        global::AppData.ClearLog();
        LogTextBox.Document.Blocks.Clear();
        global::AppData.Log("已清理历史日志", global::AppLogLevel.System);
    }

    private void OpenOfficialSite_Click(object sender, RoutedEventArgs e) => OpenPath("https://n.yzyyz.top/");

    private void InstallBrowserExtension_Click(object sender, RoutedEventArgs e) => InstallBrowserExtension();

    private void PromptBrowserExtensionInstallIfNeeded()
    {
        if (global::AppData.BrowserExtensionInstalled) return;

        var result = ConfirmWindow.Ask(
            this,
            "安装浏览器插件",
            "检测到还没有安装《N网下载器-NYPD》的附属浏览器插件。\n\n需要安装插件后，浏览器里的 N 网下载请求才能自动交给下载器。",
            "开始安装");
        if (result.Confirmed) InstallBrowserExtension();
    }

    private void InstallBrowserExtension()
    {
        try
        {
            var directory = ReleaseBrowserExtension();
            Clipboard.SetText(directory);
            global::AppData.MarkBrowserExtensionInstalled();
            global::AppData.Log($"浏览器插件已释放到：{directory}");
            ConfirmWindow.Ask(
                this,
                "安装浏览器插件",
                $"插件文件已释放到：\n{directory}\n\n目录路径已复制到剪贴板。\n\n安装步骤：\n1. 打开浏览器扩展页面：Edge 输入 edge://extensions/，Chrome 输入 chrome://extensions/。\n2. 打开“开发人员模式”。\n3. 点击“加载解压缩的扩展”。\n4. 选择上面的目录即可。",
                "知道了");
        }
        catch (Exception ex)
        {
            global::AppData.Log($"浏览器插件释放失败：{ex.Message}");
            ConfirmWindow.Ask(this, "安装浏览器插件", $"插件文件释放失败：\n{ex.Message}", "知道了");
        }
    }

    private static string ReleaseBrowserExtension()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "extension");
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException($"找不到内置插件目录：{source}");

        Directory.CreateDirectory(global::AppData.BrowserExtensionDirectory);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var destination = Path.Combine(global::AppData.BrowserExtensionDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, true);
        }

        return global::AppData.BrowserExtensionDirectory;
    }

    private void OpenLink_Click(object sender, RequestNavigateEventArgs e)
    {
        OpenPath(e.Uri.ToString());
        e.Handled = true;
    }

    private void LoadSettings()
    {
        _loadingSettings = true;
        try
        {
            DefaultDirectoryTextBox.Text = global::AppData.Settings.DownloadDirectory;
            AskBeforeCheckBox.IsChecked = global::AppData.Settings.AskBeforeDownload;
            UseProxyCheckBox.IsChecked = global::AppData.Settings.UseProxy;
            ProxyHostTextBox.Text = string.IsNullOrWhiteSpace(global::AppData.Settings.ProxyHost) ? "127.0.0.1" : global::AppData.Settings.ProxyHost;
            ProxyPortTextBox.Text = global::AppData.Settings.ProxyPort.ToString();
            SegmentCountTextBox.Text = global::AppData.Settings.SegmentCount.ToString();
        }
        finally
        {
            _loadingSettings = false;
        }
    }

    private void RefreshAboutText()
    {
        AboutBodyTextBlock.Text = "浏览器插件捕捉到 Nexus Mods 下载请求后，会交给本机下载器进行管理。";
    }

    private void AppendLog(string line)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => AppendLog(line));
            return;
        }
        AddLogLine(line);
        ScrollLogToEnd();
    }

    private void LoadLogText(string text)
    {
        LogTextBox.Document.Blocks.Clear();
        foreach (var line in text.Split([Environment.NewLine], StringSplitOptions.RemoveEmptyEntries))
            AddLogLine(line);
        ScrollLogToEnd();
    }

    private void AddLogLine(string line)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 3) };
        if (line.StartsWith("-----此次启动", StringComparison.Ordinal))
        {
            paragraph.TextAlignment = TextAlignment.Center;
            paragraph.Margin = new Thickness(0, 8, 0, 8);
            paragraph.Inlines.Add(new Run(line) { Foreground = System.Windows.Media.Brushes.DodgerBlue, FontWeight = FontWeights.SemiBold });
            LogTextBox.Document.Blocks.Add(paragraph);
            return;
        }

        var brush = LogBrush(line);
        var offset = 0;
        foreach (Match match in WindowsPathRegex().Matches(line))
        {
            if (match.Index > offset)
                paragraph.Inlines.Add(new Run(line[offset..match.Index]) { Foreground = brush });

            var path = match.Value.TrimEnd('。', '.', ',', '，', ';', '；');
            var link = new Hyperlink(new Run(path)) { Tag = path, Foreground = brush };
            link.ToolTip = "按住 Ctrl 点击打开目录";
            link.PreviewMouseLeftButtonUp += LogPath_MouseLeftButtonUp;
            paragraph.Inlines.Add(link);
            offset = match.Index + match.Length;
        }
        if (offset < line.Length)
            paragraph.Inlines.Add(new Run(line[offset..]) { Foreground = brush });
        LogTextBox.Document.Blocks.Add(paragraph);
    }

    private void LogPath_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (IsCtrlDown() && sender is Hyperlink { Tag: string path })
            OpenDirectoryForPath(path);
    }

    private void LogTextBox_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!IsCtrlDown()) return;
        var pointer = LogTextBox.GetPositionFromPoint(e.GetPosition(LogTextBox), true);
        var link = FindParentHyperlink(pointer?.Parent as DependencyObject);
        if (link?.Tag is not string path) return;
        e.Handled = true;
        OpenDirectoryForPath(path);
    }

    private static bool IsCtrlDown() => Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl);

    private static Hyperlink? FindParentHyperlink(DependencyObject? current)
    {
        while (current is not null)
        {
            if (current is Hyperlink link) return link;
            current = LogicalTreeHelper.GetParent(current);
        }
        return null;
    }

    private void ScrollLogToEnd() =>
        Dispatcher.BeginInvoke(new Action(() => LogTextBox.ScrollToEnd()), System.Windows.Threading.DispatcherPriority.ContextIdle);

    private static System.Windows.Media.Brush LogBrush(string line)
    {
        if (line.Contains("[ERROR]") || line.Contains("失败") || line.Contains("错误")) return System.Windows.Media.Brushes.IndianRed;
        if (line.Contains("[SUCCESS]") || line.Contains("成功") || line.Contains("已完成")) return System.Windows.Media.Brushes.LightGreen;
        if (line.Contains("[WARNING]") || line.Contains("警告") || line.Contains("取消") || line.Contains("删除")) return System.Windows.Media.Brushes.Orange;
        if (line.Contains("[SYSTEM]")) return System.Windows.Media.Brushes.DeepSkyBlue;
        return System.Windows.Media.Brushes.LightGray;
    }

    [GeneratedRegex(@"[A-Za-z]:\\[^\r\n]+")]
    private static partial Regex WindowsPathRegex();

    private async Task ListenAsync()
    {
        while (!_pipeCancellation.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(_pipeCancellation.Token);
                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                var line = await reader.ReadLineAsync(_pipeCancellation.Token);
                var message = line is null ? null : JsonSerializer.Deserialize<global::DownloadManager.QueueMessage>(line, _json);
                if (message is null) continue;
                var download = global::DirectDownload.Parse(message.DownloadUrl, message.Referrer, message.Filename);
                await await Dispatcher.InvokeAsync(() => EnqueueAsync(download));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception error)
            {
                Debug.WriteLine(error);
            }
        }
    }

    private void Navigation_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string page }) ShowPage(page);
    }

    private void ShowPage(string page)
    {
        DownloadsPage.Visibility = page == "Downloads" ? Visibility.Visible : Visibility.Collapsed;
        HistoryPage.Visibility = page == "History" ? Visibility.Visible : Visibility.Collapsed;
        LogsPage.Visibility = page == "Logs" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        AboutPage.Visibility = page == "About" ? Visibility.Visible : Visibility.Collapsed;
        if (page == "Logs") ScrollLogToEnd();
    }

    private void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button || button.ContextMenu is null) return;
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.IsOpen = true;
    }

    private void ThemeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string color }) return;
        var dark = _themeColors.TryGetValue(color, out var value) ? value : color;
        global::AppData.Settings.ThemeColor = color;
        global::AppData.Settings.ThemeDarkColor = dark;
        global::AppData.SaveSettings();
        ApplyTheme(color, dark);
    }

    private void ApplyTheme(string color, string darkColor)
    {
        WindowTheme.Apply(this, color, darkColor);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }
        DragMove();
    }

    private void BringToFront()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeButton_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        MaximizeIcon.Kind = WindowState == WindowState.Maximized
            ? MaterialDesignThemes.Wpf.PackIconKind.FullscreenExit
            : MaterialDesignThemes.Wpf.PackIconKind.Fullscreen;
    }

    private void UpdateBulkPauseButton()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(UpdateBulkPauseButton);
            return;
        }
        var shouldResume = _tasks.Any(task => task.Mode == global::DownloadMode.Paused);
        BulkPauseText.Text = shouldResume ? "全部继续" : "全部暂停";
        BulkPauseIcon.Kind = shouldResume ? MaterialDesignThemes.Wpf.PackIconKind.Play : MaterialDesignThemes.Wpf.PackIconKind.Pause;
    }

    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T match) return match;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }

    private static void OpenPath(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    private static void OpenDirectoryForPath(string path)
    {
        if (File.Exists(path))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            return;
        }

        var directory = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory)) OpenPath(directory);
    }

    private static string FormatBytes(long value)
        => global::FileName.FormatBytes(value);

    private static string ToChineseStatus(string message) => message switch
    {
        "Merged file length does not match the download length." => "合并后的文件大小与预期不一致",
        "Download segment ended early." => "分段下载提前结束",
        "Segment made no progress." => "分段下载没有继续收到数据",
        "The CDN does not support a safe resume for this file." => "服务器不支持安全续传",
        "Download ended before the expected file size was reached." => "下载提前结束，文件大小不足",
        _ => message
    };
}
