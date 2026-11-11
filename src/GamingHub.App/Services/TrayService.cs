using System.Drawing;
using System.Windows.Forms;

namespace GamingHub.App.Services;

/// <summary>Background tray companion. Closing the window hides it here; display monitoring keeps running.</summary>
public sealed class TrayService : IDisposable
{
    NotifyIcon? _icon;
    ToolStripMenuItem? _status;

    public void Start(MainWindow w, ControllerService pad, LibraryService library, ViewModels.MainViewModel vm)
    {
        var menu = new ContextMenuStrip();
        _status = new ToolStripMenuItem(pad.Status) { Enabled = false };
        menu.Items.Add("Open Gaming Hub", null, (_, _) => w.BringToFront());
        menu.Items.Add("Open on preferred display", null, (_, _) => { w.BringToFront(); vm.RescanDisplaysCommand.Execute(null); });
        menu.Items.Add("Scan for games", null, async (_, _) => vm.ShowToast(await library.ScanAsync()));
        menu.Items.Add("Settings", null, (_, _) => { w.BringToFront(); vm.ShowSettingsCommand.Execute(null); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_status);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => System.Windows.Application.Current.Shutdown());

        _icon = new NotifyIcon
        {
            Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application,
            Text = App.IsDev ? "Gaming Hub (Dev)" : "Gaming Hub",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => w.BringToFront();
        pad.LowBattery += message => w.Dispatcher.BeginInvoke(() =>
        {
            if (w.IsActive || QuickMenuWindow.Current?.IsActive == true)
                vm.ShowToast(message);
            else
                _icon?.ShowBalloonTip(5000, "Gaming Hub", message, ToolTipIcon.Warning);
        });
        pad.StatusChanged += s => w.Dispatcher.BeginInvoke(() => { if (_status is not null) _status.Text = s; });
    }

    public void Dispose()
    {
        if (_icon is null) return;
        _icon.Visible = false;
        _icon.Dispose();
    }
}
