using System.Windows;

namespace NexusModsDownloader;

static class WindowTheme
{
    public static void Apply(Window window) => Apply(window, global::AppData.Settings.ThemeColor, global::AppData.Settings.ThemeDarkColor);

    public static void Apply(Window window, string color, string darkColor)
    {
        window.Resources["PrimaryHueMidBrush"] = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color)!);
        window.Resources["PrimaryHueDarkBrush"] = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(darkColor)!);
    }
}
