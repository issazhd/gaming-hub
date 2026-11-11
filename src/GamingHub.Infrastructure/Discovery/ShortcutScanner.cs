using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using GamingHub.Core.Discovery;

namespace GamingHub.Infrastructure.Discovery;

/// <summary>
/// Adds the games in the user's own shortcut folders (e.g. Desktop\Games): .lnk shortcuts to an .exe/.bat and
/// .url internet shortcuts (steam://, com.epicgames.launcher://, …). Launchers/tools are skipped, and entries
/// another source already has are de-duplicated by the merger. Shortcuts whose target is gone are not listed.
/// </summary>
public sealed class ShortcutScanner(Func<IReadOnlyList<string>> folders) : IGameScanner
{
    public string Source => LibraryMerger.ShortcutSource;

    public IReadOnlyList<DiscoveredGame>? Scan()
    {
        var configured = folders();
        if (configured.Count == 0) return [];          // no folders: earlier shortcut games become "not installed"
        var roots = configured.Where(Directory.Exists).ToList();
        if (roots.Count == 0) return null;             // folder offline/missing: keep entries as they are
        var result = new List<DiscoveredGame>();
        foreach (var file in roots.SelectMany(r => Directory.EnumerateFiles(r, "*.*", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true })))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext == ".lnk" && ReadLink(file) is var (target, args, wd) && File.Exists(target) && !ShortcutRules.IsNotAGame(target))
                result.Add(new DiscoveredGame(Source, target.ToLowerInvariant(), ShortcutRules.Name(file, target), ShortcutRules.GameRoot(target),
                    Executable: target, Arguments: args, WorkingDirectory: string.IsNullOrWhiteSpace(wd) ? null : wd));
            else if (ext == ".url" && ReadUrl(file) is { } uri && !uri.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !ShortcutRules.IsStoreUri(uri))
                result.Add(new DiscoveredGame(Source, uri, ShortcutRules.Name(file, null), null, LaunchUri: uri));
        }
        // A helper shortcut with only an exe-like name ("GenLauncher") is dropped when a properly named
        // shortcut ("Command & Conquer Generals Zero Hour") points into the same game folder.
        var named = result.Where(d => d.InstallDir is not null && !NameFromFolder(d))
            .Select(d => d.InstallDir!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return result.Where(d => !(NameFromFolder(d) && named.Contains(d.InstallDir!))).ToList();
    }

    static bool NameFromFolder(DiscoveredGame d) =>
        d.InstallDir is not null && d.Name.Equals(Path.GetFileName(d.InstallDir), StringComparison.OrdinalIgnoreCase)
        && d.Executable is not null && !Path.GetFileNameWithoutExtension(d.Executable).Contains(' ');

    static string? ReadUrl(string file) =>
        File.ReadLines(file).FirstOrDefault(l => l.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))?[4..].Trim();

    static (string Target, string Args, string WorkDir)? ReadLink(string file)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            ((IPersistFile)link).Load(file, 0);
            var sb = new StringBuilder(1024);
            link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            var target = sb.ToString();
            sb.Clear(); link.GetArguments(sb, sb.Capacity);
            var args = sb.ToString();
            sb.Clear(); link.GetWorkingDirectory(sb, sb.Capacity);
            return target.Length == 0 ? null : (target, args, sb.ToString());
        }
        catch (COMException) { return null; }
        finally { Marshal.ReleaseComObject(link); }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")] class ShellLink;

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, IntPtr findData, uint flags);
        void GetIDList(out IntPtr pidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int cch);
    }
}
