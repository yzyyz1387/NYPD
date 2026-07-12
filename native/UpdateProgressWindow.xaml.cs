using System.Windows;
using System.Windows.Input;

namespace NexusModsDownloader;

public partial class UpdateProgressWindow : Window
{
    public UpdateProgressWindow(string version)
    {
        InitializeComponent();
        WindowTheme.Apply(this);
        StatusTextBlock.Text = $"正在下载更新 {version}...";
        SetProgress(0);
    }

    public void SetProgress(double value)
    {
        var percent = Math.Clamp(value, 0, 1) * 100;
        UpdateProgressBar.Value = percent;
        PercentTextBlock.Text = $"{percent:0}%";
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();
}
