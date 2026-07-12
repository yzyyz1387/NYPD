using System.Windows;
using System.Windows.Input;

namespace NexusModsDownloader;

public sealed record ConfirmResult(bool Confirmed, bool OptionChecked);

public partial class ConfirmWindow : Window
{
    private ConfirmWindow(string title, string message, string confirmText, string? optionText)
    {
        InitializeComponent();
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
