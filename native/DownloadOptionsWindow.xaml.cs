using System.Windows;
using System.Windows.Input;
using MessageBox = System.Windows.MessageBox;

namespace NexusModsDownloader;

public partial class DownloadOptionsWindow : Window
{
    public DownloadOptionsWindow(string fileName, string directory, bool askEveryTime)
    {
        InitializeComponent();
        FileNameTextBox.Text = fileName;
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
        var fileName = global::FileName.Safe(FileNameTextBox.Text, "nexus-download.bin");
        var directory = string.IsNullOrWhiteSpace(DirectoryTextBox.Text) ? global::Downloader.GetDownloadDirectory() : DirectoryTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(directory))
        {
            MessageBox.Show(this, "请填写文件名和保存位置。", "下载选项");
            return;
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
