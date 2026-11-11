using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using GamingHub.App.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace GamingHub.App;

/// <summary>
/// Small browser on Google Images for the game's name. Right-click any image (□ with the controller) ›
/// "Use as cover" / "Use as background" downloads it into the art cache and hands the path back.
/// </summary>
public sealed class ImageSearchWindow : Window
{
    public static ImageSearchWindow? Current { get; private set { field = value; CurrentChanged?.Invoke(); } }
    public static event Action? CurrentChanged;
    public IntPtr Handle { get; private set; }

    readonly WebView2 _web = new();
    readonly TextBlock _status = new() { FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
    readonly Action<string, string> _picked;   // (cover|background, path)
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public ImageSearchWindow(string query, Action<string, string> picked)
    {
        _picked = picked;
        Title = $"Find artwork – {query}";
        Width = 1280; Height = 860;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Foreground = (System.Windows.Media.Brush)Application.Current.Resources["Fg"];
        Background = (System.Windows.Media.Brush)Application.Current.Resources["Card"] ?? System.Windows.Media.Brushes.Black;
        var close = new Button { Content = "Done", Margin = new Thickness(0) };
        close.Click += (_, _) => Close();
        _status.Text = "Right-click an image (□ on the controller) › Use as cover / Use as background.  Left stick moves the pointer, ✕ clicks, right stick scrolls, ○ closes.";
        var bar = new DockPanel { Margin = new Thickness(16, 10, 16, 10) };
        DockPanel.SetDock(close, Dock.Right);
        bar.Children.Add(close);
        bar.Children.Add(_status);
        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(_web);
        Content = root;
        SourceInitialized += (_, _) => Handle = new WindowInteropHelper(this).Handle;
        Loaded += async (_, _) => await InitAsync(query);
        Closed += (_, _) => Current = null;
        Current = this;
    }

    async Task InitAsync(string query)
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(App.DataDir, "webview"));
            await _web.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            _status.Text = $"The built-in browser (Microsoft Edge WebView2) isn't available: {ex.Message}";
            return;
        }
        var core = _web.CoreWebView2;
        core.ContextMenuRequested += (_, e) =>
        {
            if (!e.ContextMenuTarget.HasSourceUri) return;
            var src = e.ContextMenuTarget.SourceUri;
            e.MenuItems.Insert(0, Item(core, "Use as cover", () => Use("cover", src)));
            e.MenuItems.Insert(1, Item(core, "Use as background", () => Use("background", src)));
        };
        core.NewWindowRequested += (_, e) => { e.Handled = true; core.Navigate(e.Uri); };   // keep links in this window
        core.Navigate($"https://www.google.com/search?udm=2&q={Uri.EscapeDataString(query + " game")}");
    }

    static CoreWebView2ContextMenuItem Item(CoreWebView2 core, string label, Action run)
    {
        var item = core.Environment.CreateContextMenuItem(label, null, CoreWebView2ContextMenuItemKind.Command);
        item.CustomItemSelected += (_, _) => run();
        return item;
    }

    async void Use(string kind, string src)
    {
        _status.Text = $"Downloading {kind}…";
        try
        {
            byte[] data = src.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                ? Convert.FromBase64String(src[(src.IndexOf(',') + 1)..])
                : await Http.GetByteArrayAsync(src);
            var path = ArtworkService.SaveImage(data, $"{Guid.NewGuid().ToString("N")[..10]}-{kind}");
            _picked(kind, path);
            _status.Text = $"✓ Set as {kind}. Pick another, or press Done.";
        }
        catch (Exception ex)
        {
            _status.Text = $"Couldn't use that image ({ex.Message}). Open the image first for full size, then try again.";
        }
    }
}
