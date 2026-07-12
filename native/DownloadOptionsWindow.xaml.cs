using System.IO;
using System.Windows;
using System.Windows.Input;

namespace NexusModsDownloader;

public partial class DownloadOptionsWindow : Window
{
    public DownloadOptionsWindow(string fileName, string directory, bool askEveryTime)
    {
        InitializeComponent();
        WindowTheme.Apply(this);
        var safeName = global::FileName.Safe(fileName, "nexus-download.bin");
        FileNameTextBox.Text = Path.GetFileNameWithoutExtension(safeName);
        ExtensionTextBox.Text = Path.GetExtension(safeName).TrimStart('.');
        DirectoryTextBox.Text = string.IsNullOrWhiteSpace(directory) ? global::Downloader.GetDownloadDirectory() : directory;
        AskEveryTimeCheckBox.IsChecked = askEveryTime;
        FileNameTextBox.SelectAll();
        FileNameTextBox.Focus();
    }

    public string FileNameValue { get; private set; } = "";
    public string DirectoryPath { get; private set; } = "";
    public bool AskEveryTime { get; private set; }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var selected = await global::FolderPicker.PickAsync(DirectoryTextBox.Text);
        if (!string.IsNullOrWhiteSpace(selected)) DirectoryTextBox.Text = selected;
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        var stem = FileNameTextBox.Text.Trim();
        var extension = ExtensionTextBox.Text.Trim().TrimStart('.');
        var fileName = global::FileName.Safe(string.IsNullOrWhiteSpace(extension) ? stem : $"{stem}.{extension}", "nexus-download.bin");
        var directory = string.IsNullOrWhiteSpace(DirectoryTextBox.Text) ? global::Downloader.GetDownloadDirectory() : DirectoryTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(stem) || string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(directory))
        {
            ConfirmWindow.Ask(this, "下载选项", "请填写文件名和保存位置。", "知道了");
            return;
        }
        if (string.IsNullOrWhiteSpace(extension))
        {
            var result = ConfirmWindow.Ask(this, "扩展名为空", "未填写扩展名，下载后的文件可能无法被系统识别。\n\n确定不填写扩展名继续下载吗？", "继续下载");
            if (!result.Confirmed)
            {
                ExtensionTextBox.Focus();
                return;
            }
        }

        FileNameValue = fileName;
        DirectoryPath = directory;
        AskEveryTime = AskEveryTimeCheckBox.IsChecked == true;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();
}
