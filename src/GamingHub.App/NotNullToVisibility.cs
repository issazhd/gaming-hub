using System.Collections.Concurrent;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace GamingHub.App;

public sealed class NotNullToVisibility : IValueConverter
{
    public object Convert(object? v, Type t, object? p, CultureInfo c) =>
        v is null || (v is string s && s.Length == 0) ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class InverseBoolToVisibility : IValueConverter
{
    public object Convert(object? v, Type t, object? p, CultureInfo c) => v is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Loads an image file decoded at a bounded width (parameter, px), frozen and cached. Use with Binding IsAsync=True.</summary>
public sealed class PathToImage : IValueConverter
{
    static readonly ConcurrentDictionary<string, BitmapSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public object? Convert(object? v, Type t, object? p, CultureInfo c)
    {
        if (v is not string path || path.Length == 0) return null;
        // "600" = decode width; "h1080" = decode height (full-screen backgrounds: sharp however wide the art is)
        var key = p as string ?? "600";
        bool byHeight = key.StartsWith('h');
        int size = int.TryParse(byHeight ? key[1..] : key, out var n) ? n : 600;
        return Cache.GetOrAdd($"{key}|{path}", _ => Load(path, size, byHeight));
    }

    static BitmapSource? Load(string path, int size, bool byHeight = false)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var b = new BitmapImage();
            b.BeginInit();
            b.UriSource = new Uri(path);
            if (byHeight) b.DecodePixelHeight = size; else b.DecodePixelWidth = size;
            b.CacheOption = BitmapCacheOption.OnLoad;   // release the file handle
            b.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            b.EndInit();
            b.Freeze();
            return b;
        }
        catch { return null; }   // corrupt/unsupported image → placeholder
    }

    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Visible when the bound value's text equals the converter parameter (Settings pages).</summary>
public sealed class EqualsToVisibility : IValueConverter
{
    public object Convert(object? v, Type t, object? p, CultureInfo c) => Equals(v?.ToString(), p?.ToString()) ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}
