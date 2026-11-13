using System.Diagnostics;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;

namespace NexusModsDownloader;

public sealed record ConfirmResult(bool Confirmed, bool OptionChecked);

public partial class ConfirmWindow : Window
{
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    private ConfirmWindow(string title, string message, string confirmText, string? optionText)
    {
        InitializeComponent();
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            CopyToast.Visibility = Visibility.Collapsed;
        };
        WindowTheme.Apply(this);
        TitleTextBlock.Text = title;
        MessageTextBlock.Text = message;
        ConfirmButton.Content = confirmText;
        if (!string.IsNullOrWhiteSpace(optionText))
        {
            OptionCheckBox.Content = optionText;
            OptionCheckBox.Visibility = Visibility.Visible;
        }
    }

    public ConfirmResult Result { get; private set; } = new(false, false);

    public static ConfirmResult Ask(Window owner, string title, string message, string confirmText = "确认", string? optionText = null)
    {
        var dialog = new ConfirmWindow(title, message, confirmText, optionText) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.Result : new ConfirmResult(false, false);
    }

    public static void ShowBrowserExtensionInstructions(Window owner, string directory, bool copied)
    {
        var dialog = new ConfirmWindow(
            "安装浏览器插件",
            "",
            "完成",
            null)
        {
            Owner = owner
        };
        dialog.CancelButton.Visibility = Visibility.Collapsed;
        dialog.ExtensionLinksTextBlock.Visibility = Visibility.Visible;
        dialog.MessageTextBlock.Visibility = Visibility.Collapsed;
        dialog.ExtensionPathPanel.Visibility = Visibility.Visible;
        dialog.ExtensionPathTextBox.Text = directory;
        dialog.SetClipboardStatus(copied);
        dialog.Loaded += (_, _) =>
        {
            dialog.ExtensionPathTextBox.Focus();
            dialog.ExtensionPathTextBox.SelectAll();
        };
        dialog.ShowDialog();
    }

    private void SetClipboardStatus(bool copied)
    {
        ClipboardStatusTextBlock.Text = copied
            ? "插件目录路径已复制到系统剪贴板。"
            : "无法访问系统剪贴板，可能是第三方剪贴板软件接管了系统剪贴板。请手动复制下方路径，或直接打开插件目录。";
        ClipboardStatusTextBlock.Visibility = Visibility.Visible;
    }

    private void CopyExtensionPath_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Forms.Clipboard.SetDataObject(ExtensionPathTextBox.Text, true, 10, 100);
            ShowToast("插件目录已复制");
        }
        catch
        {
            ExtensionPathTextBox.Focus();
            ExtensionPathTextBox.SelectAll();
            ShowToast("复制失败，已选中路径，请按 Ctrl+C 复制");
        }
    }

    private void CopyBrowserExtensionUrl_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Hyperlink { Tag: string url }) return;
        try
        {
            System.Windows.Forms.Clipboard.SetDataObject(url, true, 10, 100);
            ShowToast("浏览器扩展页地址已复制");
        }
        catch
        {
            ShowToast("复制失败，请手动输入浏览器扩展页地址");
        }
    }

    private void ShowToast(string message)
    {
        CopyToastText.Text = message;
        CopyToast.Background = (System.Windows.Media.Brush)FindResource("PrimaryHueDarkBrush");
        CopyToast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void OpenExtensionPath_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(ExtensionPathTextBox.Text) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ClipboardStatusTextBlock.Text = $"无法打开插件目录：{ex.Message}";
            ClipboardStatusTextBlock.Visibility = Visibility.Visible;
        }
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        Result = new ConfirmResult(true, OptionCheckBox.IsChecked == true);
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
