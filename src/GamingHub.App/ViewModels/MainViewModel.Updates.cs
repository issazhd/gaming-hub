using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace GamingHub.App.ViewModels;

public sealed partial class MainViewModel
{
    const string ReleaseApi = "https://api.github.com/repos/issazhd/gaming-hub/releases/latest";
    static readonly HttpClient UpdateClient = new() { Timeout = TimeSpan.FromSeconds(12) };

    [ObservableProperty] string? _updateStatus;
    [ObservableProperty] Uri? _updateInstaller;

    [RelayCommand]
    async Task CheckForUpdatesAsync()
    {
        UpdateInstaller = null;
        UpdateStatus = "Checking GitHub for updates…";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ReleaseApi);
            request.Headers.UserAgent.ParseAdd($"GamingHub/{AppVersion}");
            using var response = await UpdateClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            using var release = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            (UpdateStatus, UpdateInstaller) = ReadUpdate(release.RootElement, Version.Parse(AppVersion));
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or FormatException or InvalidOperationException or KeyNotFoundException)
        {
            Serilog.Log.Warning(ex, "Could not check the public Gaming Hub release");
            UpdateStatus = "Couldn’t check for updates. Try again later.";
        }
    }

    internal static (string Status, Uri? Installer) ReadUpdate(JsonElement release, Version installed)
    {
        string tag = release.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v'), out var latest)) throw new FormatException("Invalid release version");
        if (installed >= latest) return ($"You’re up to date. The latest public version is {latest}.", null);

        string name = $"GamingHub-{latest}-Setup.exe";
        var url = new Uri($"https://github.com/issazhd/gaming-hub/releases/download/{tag}/{name}");
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
            if (asset.GetProperty("name").GetString() == name && asset.GetProperty("browser_download_url").GetString() == url.AbsoluteUri)
                return ($"Gaming Hub {latest} is available. Download and run the installer to update.", url);
        return ($"Gaming Hub {latest} is available. Its Windows installer isn’t ready yet.", null);
    }

    [RelayCommand]
    void DownloadUpdate()
    {
        if (UpdateInstaller is not { } installer) return;
        try { Process.Start(new ProcessStartInfo(installer.AbsoluteUri) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { UpdateStatus = "Couldn’t open your browser. Try downloading again."; }
    }
}
