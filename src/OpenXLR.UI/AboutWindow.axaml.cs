using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OpenXLR.UI;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = $"v{AppVersion.Current}";
    }

    private static void OpenUrl(string url) => ExternalLink.Open(url);

    private void OnRepo(object? sender, RoutedEventArgs e) => OpenUrl("https://github.com/emaspa/openxlr");
    private void OnDiscord(object? sender, RoutedEventArgs e) => OpenUrl("https://discord.gg/4bswtnGPW4");
    private void OnReddit(object? sender, RoutedEventArgs e) => OpenUrl("https://www.reddit.com/r/OpenXLR/");
    private void OnCoffee(object? sender, RoutedEventArgs e) => OpenUrl("https://buymeacoffee.com/emaspa");
    private void OnKoFi(object? sender, RoutedEventArgs e) => OpenUrl("https://ko-fi.com/emaspa");
    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}

/// <summary>The app version, from the assembly (set once in Directory.Build.props).</summary>
public static class AppVersion
{
    public static readonly string BuildVersion =
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "0.0.0";
    public static readonly string Current = BuildVersion.Split('+')[0];
}
