using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using GamingHub.App.Services;
using GamingHub.App.ViewModels;
using GamingHub.Core.Displays;
using GamingHub.Infrastructure.Audio;
using GamingHub.Infrastructure.Displays;
using GamingHub.Infrastructure.Games;

namespace GamingHub.App;

/// <summary>PS controls for the foreground game or the launcher. The original game window is captured before activation.</summary>
public partial class QuickMenuWindow : Window
{
    public static QuickMenuWindow? Current { get; private set; }

    readonly MainWindow _main;
    readonly MainViewModel _vm;
    readonly GameLaunchService _games;
    readonly IntPtr _gameWindow;
    readonly int _gamePid;
    readonly GamingHub.Core.Games.Game? _openingGame;
    IntPtr _hwnd;
    bool _closeArmed, _closing, _returning;
    /// <summary>Opened during a game (over its window), rather than from Gaming Hub.</summary>
    public bool OverGame => _gameWindow != IntPtr.Zero;
    bool ControlsTrackedGame => _openingGame is not null && ReferenceEquals(_games.Running, _openingGame);
    public bool WaitingForCircleRelease { get; set; }
    string? _pauseNote;

    /// <summary>Close unless already closing (Deactivated fires during Close).</summary>
    public void CloseMenu() { if (!_closing) Close(); }

    QuickMenuWindow(MainWindow main, MainViewModel vm, GameLaunchService games, PixelRect screen, uint dpi, IntPtr gameWindow)
    {
        (_main, _vm, _games, _gameWindow) = (main, vm, games, gameWindow);
        InitializeComponent();
        DataContext = vm;
        _gamePid = GameWindows.WindowProcessId(gameWindow);
        _openingGame = !OverGame || games.RunningWindow == gameWindow ? games.Running : null;
        if (OverGame)
        {
            AllowsTransparency = true;
            Background = System.Windows.Media.Brushes.Transparent;
            OverlayCard.MaxWidth = 680;
            OverlayCard.Margin = new Thickness(18);
            OverlayCard.VerticalAlignment = VerticalAlignment.Bottom;
            Backdrop.Background = GameWindows.ExclusiveFullscreenActive(gameWindow)
                ? (System.Windows.Media.Brush)FindResource("ShellBackground")
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(48, 0, 0, 0));
        }
        // Opened over a game: optionally pause it, so it ignores the controller while the menu is open (always resumed on close).
        // A moment after the menu took focus, so an exclusive full-screen game can step aside first.
        if (OverGame && ControlsTrackedGame && _games.Running?.AllowPause == true)
            Loaded += async (_, _) =>
            {
                await Task.Delay(350);
                if (_closing || !ControlsTrackedGame) return;
                _pauseNote = _games.PauseGame();
                Refresh();
            };
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            var width = Math.Min(screen.Width, (int)(480 * dpi / 96.0));
            WindowPlacement.Move(_hwnd, OverGame ? screen : new PixelRect(screen.Right - width, screen.Y, width, screen.Height));
            if (OverGame)
                HwndSource.FromHwnd(_hwnd)?.AddHook(OverlayDisplayChanged);
        };
        var soundTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        soundTimer.Tick += (_, _) => RefreshSound();
        Loaded += (_, _) =>
        {
            Refresh();
            soundTimer.Start();
            GameWindows.ForceForeground(_hwnd);
            if (OverGame)
            {
                if (GameWindows.IsIconic(_gameWindow)) Backdrop.Background = (System.Windows.Media.Brush)FindResource("ShellBackground");
                VolumeSlider.Focus();
            }
            else FirstButton()?.Focus();
        };
        Closing += (_, _) => _closing = true;
        Deactivated += (_, _) => { if (!_returning) CloseMenu(); };   // clicking the game (or anything else) dismisses it
        Closed += (_, _) => { soundTimer.Stop(); _games.ResumeGame(); if (Current == this) Current = null; };
        KeyDown += (_, e) => { if (e.Key is Key.Escape or Key.BrowserBack) { e.Handled = true; Back(); } };
        // Hints follow the input in use here too (the hub doesn't see keys or clicks aimed at this window).
        PreviewKeyDown += (_, e) => { if (e.Key is not (Key.System or Key.LeftAlt or Key.RightAlt)) _vm.UsingController = false; };
        PreviewMouseDown += (_, _) => { if (!_main.PadPointerRecent) _vm.UsingController = false; };   // touchpad clicks are the controller
        VolumeSlider.PreviewKeyDown += (_, e) =>   // keyboard too: only ← → change the volume, ↑ ↓ move on
        {
            if (e.Key is not (Key.Up or Key.Down)) return;
            e.Handled = true;
            LeaveSlider(VolumeSlider, e.Key == Key.Up ? FocusNavigationDirection.Up : FocusNavigationDirection.Down);
        };
    }

    public static void Open(MainWindow main, MainViewModel vm, GameLaunchService games, PixelRect screen, uint dpi, IntPtr gameWindow = default)
    {
        Current?.CloseMenu();
        Current = new QuickMenuWindow(main, vm, games, screen, dpi, gameWindow);
        Current.Show();
    }

    // Exclusive games can release their display mode when the overlay gains focus.
    IntPtr OverlayDisplayChanged(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message is 0x007E or 0x02E0) // WM_DISPLAYCHANGE / WM_DPICHANGED
            Dispatcher.BeginInvoke(() =>
            {
                if (_closing) return;
                var bounds = WindowPlacement.GetHostMonitorBounds(hwnd);
                if (bounds.Width > 0 && WindowPlacement.GetBounds(hwnd) != bounds) WindowPlacement.Move(hwnd, bounds);
            });
        return IntPtr.Zero;
    }

    void Refresh()
    {
        RefreshSound();
        bool playing = ControlsTrackedGame;
        var selection = !OverGame && !playing ? _vm.SelectedGame : null;
        StateText.Text = playing ? (_games.IsPaused ? "Paused" : "Playing")
            : OverGame ? "In game" : selection is null ? "No game running" : "Selected game";
        GameTitle.Text = playing ? _openingGame!.Name : OverGame
            ? GameWindows.WindowTitle(_gameWindow) ?? "Current game" : selection?.Name ?? "Gaming Hub";
        var cover = playing ? _openingGame!.CoverPath : selection?.Cover;
        var images = (PathToImage)FindResource("Img");
        Cover.ImageSource = images.Convert(cover, typeof(System.Windows.Media.ImageSource), "160",
            System.Globalization.CultureInfo.InvariantCulture) as System.Windows.Media.ImageSource;
        CoverBox.Visibility = playing || OverGame || selection is not null ? Visibility.Visible : Visibility.Collapsed;
        var note = _closeArmed ? "Press Close game again to force it closed. Unsaved progress is lost." : _status ?? _pauseNote;
        StatusText.Text = note ?? "";
        StatusText.Visibility = note is null ? Visibility.Collapsed : Visibility.Visible;
        bool canReturn = playing || OverGame;
        bool hasRecent = !canReturn && _vm.RecentGames.Any();
        GameAction.Content = canReturn ? "Return to game" : hasRecent ? "Recent games" : "Open Library";
        GameAction.Tag = canReturn ? "\uE768" : hasRecent ? "\uE823" : "\uE8F1";
        CloseGameButton.IsEnabled = playing;   // kept in place (dimmed) so the grid doesn't jump
        CloseGameButton.Content = _closeArmed ? "Press again" : "Close game";

        _vm.RefreshDisplays();
        DisplayButtons.Children.Clear();
        foreach (var d in _vm.Displays)
        {
            var b = new Button
            {
                Content = d.CanUse ? $"{d.Info.Identity.FriendlyName}{(d.IsPreferred ? "  ★" : "")}" : $"Extend to {d.Info.Identity.FriendlyName}",
                Tag = d,
                Style = (Style)FindResource("Chip"),
            };
            if (d.IsPreferred) b.Background = (System.Windows.Media.Brush)FindResource("Accent");
            b.Click += (_, _) =>
            {
                if (d.CanUse) _vm.SetPreferredCommand.Execute(d);
                else _vm.ExtendDisplayCommand.Execute(d);
                Back();
            };
            DisplayButtons.Children.Add(b);
            if (d.CanMakeMain)
            {
                var main = new Button { Content = $"Make {d.Info.Identity.FriendlyName} main", Style = (Style)FindResource("Chip") };
                main.Click += (_, _) => { _vm.MakeMainDisplayCommand.Execute(d); Back(); };
                DisplayButtons.Children.Add(main);
            }
        }
    }

    // ---- controller routing (from MainWindow while the menu is open) ----
    public void Navigate(FocusNavigationDirection dir)
    {
        if (Keyboard.FocusedElement is not UIElement f || !IsAncestorOf(f)) { FirstButton()?.Focus(); return; }
        if (f is Slider sl)
        {
            if (dir is FocusNavigationDirection.Left or FocusNavigationDirection.Right)
            {   // only ← → change the volume
                sl.Value = Math.Clamp(sl.Value + (dir == FocusNavigationDirection.Right ? 5 : -5), 0, 100);
                return;
            }
            LeaveSlider(sl, dir);   // ↑ ↓ just move on — the volume stays as it is
            return;
        }
        f.MoveFocus(new TraversalRequest(dir));
        (Keyboard.FocusedElement as FrameworkElement)?.BringIntoView();
    }

    /// <summary>Moves focus up/down off the volume slider without letting it change the value.</summary>
    void LeaveSlider(Slider sl, FocusNavigationDirection dir)
    {
        var keep = sl.Value;
        sl.MoveFocus(new TraversalRequest(dir));
        if (sl.Value != keep) { _syncing = true; sl.Value = keep; _syncing = false; }
        (Keyboard.FocusedElement as FrameworkElement)?.BringIntoView();
    }

    public void Press()
    {
        if (Keyboard.FocusedElement is Button b && IsAncestorOf(b)) ((IInvokeProvider)new ButtonAutomationPeer(b).GetPattern(PatternInterface.Invoke)!).Invoke();
    }

    Button? FirstButton() => FindButtons(this).FirstOrDefault(b => b.IsVisible && b.IsEnabled);

    static IEnumerable<Button> FindButtons(DependencyObject root)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (c is Button b) yield return b;
            foreach (var x in FindButtons(c)) yield return x;
        }
    }

    // ---- actions ----
    // Bring Gaming Hub forward while this menu still has the focus (Windows always allows that), then close the menu.
    // The other way round, the game is in front again for a moment — and an administrator game blocks the switch.
    void GoToHub(object s, RoutedEventArgs e) { _vm.ShowHomeCommand.Execute(null); _main.BringToFront(); CloseMenu(); }

    public void Back()
    {
        if (OverGame) ReturnToGame();
        else if (RecentList.Visibility == Visibility.Visible)
        {
            RecentList.Visibility = Visibility.Collapsed;
            GameAction.Focus();
        }
        else CloseMenu();
    }

    void OpenGameAction(object sender, RoutedEventArgs e)
    {
        if (ControlsTrackedGame || OverGame) { ReturnToGame(); return; }
        if (RecentList.Visibility == Visibility.Visible) { Back(); return; }
        var recent = _vm.RecentGames.ToList();
        if (recent.Count == 0)
        {
            _vm.ShowLibraryCommand.Execute(null);
            _main.BringToFront();
            CloseMenu();
            return;
        }

        RecentList.Children.Clear();
        foreach (var game in recent)
        {
            var button = new Button { Content = game.Name, Style = (Style)FindResource("Chip") };
            button.Click += (_, _) =>
            {
                _main.BringToFront();
                CloseMenu();
                _vm.PlayCommand.Execute(game);
            };
            RecentList.Children.Add(button);
        }
        RecentList.Visibility = Visibility.Visible;
        RecentList.UpdateLayout();
        ((Button)RecentList.Children[0]).Focus();
    }

    public void DismissFromGuide()
    {
        if (OverGame) ReturnToGame();
        else CloseMenu();
    }

    void ReturnToGame()
    {
        if (_closing || _returning) return;
        _returning = true;
        try
        {
            // Restore the game behind the overlay before dismissing it. A minimized exclusive game may
            // still need Windows to switch display modes, but the launcher does not come forward.
            _games.ResumeGame();
            if (OverGame && _gamePid != 0 && GameWindows.WindowProcessId(_gameWindow) == _gamePid)
            {
                GameWindows.ShowAndForeground(_gameWindow);
                Serilog.Log.Information("PS overlay return: game is foreground={Restored}", GameWindows.Foreground() == _gameWindow);
            }
            else if (ControlsTrackedGame && _games.ReturnToGame() is { } error)
                _vm.ShowToast(error);
        }
        finally { CloseMenu(); }
    }
    void OpenPower(object s, RoutedEventArgs e) { _main.BringToFront(); _vm.OpenPowerCommand.Execute(null); CloseMenu(); }
    void MinimizeHub(object s, RoutedEventArgs e) { Close(); _vm.MinimizeCommand.Execute(null); }

    void OpenSettings(object s, RoutedEventArgs e) { _vm.ShowSettingsCommand.Execute(null); _main.BringToFront(); CloseMenu(); }

    void CloseGame(object s, RoutedEventArgs e)
    {
        if (!ControlsTrackedGame) { CloseMenu(); return; }
        if (!_closeArmed) { _closeArmed = true; Refresh(); return; }
        _vm.ShowToast(_games.ForceCloseGame());   // End task: works even when the game ignores a normal close request
        Close();
    }

    bool _syncing;
    string? _status;
    long _lastVolumeTick;

    void VolumeChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing) return;
        try { AudioOutputs.SetVolume((int)Math.Round(e.NewValue)); } catch { }
        VolumeText.Text = $"{(int)Math.Round(e.NewValue)}%";
        // A short tick at the new level, so you can hear how loud it is (like Windows' volume keys). Not faster than every 60 ms.
        if (Environment.TickCount64 - _lastVolumeTick > 60) { _lastVolumeTick = Environment.TickCount64; UiSounds.Play(UiSounds.Kind.Move); }
    }

    /// <summary>Speakers button: unfold / fold the list of sound outputs (the current one highlighted).</summary>
    void ToggleOutputs(object s, RoutedEventArgs e)
    {
        if (OutputPanel.Visibility == Visibility.Visible) { OutputPanel.Visibility = Visibility.Collapsed; return; }
        OutputList.Children.Clear();
        IReadOnlyList<AudioOutput> outputs;
        try { outputs = AudioOutputs.List(); }
        catch (Exception ex) { _status = $"Couldn't list sound outputs: {ex.Message}"; Refresh(); return; }
        Button? current = null;
        foreach (var o in outputs)
        {
            var b = new Button { Content = o.Name, Style = (Style)FindResource("Chip") };
            if (o.IsDefault) { b.Background = (System.Windows.Media.Brush)FindResource("Accent"); current = b; }
            b.Click += (_, _) =>
            {
                if (!o.IsDefault)
                    try { AudioOutputs.SetDefault(o.Id); _status = $"Sound now plays on {o.Name}"; }
                    catch (Exception ex) { _status = $"Couldn't switch sound output: {ex.Message}"; }
                OutputPanel.Visibility = Visibility.Collapsed;
                Refresh();   // volume and mute belong to the new output
                OutputButton.Focus();
            };
            OutputList.Children.Add(b);
        }
        OutputPanel.Visibility = Visibility.Visible;
        OutputPanel.UpdateLayout();
        (current ?? OutputList.Children.OfType<Button>().FirstOrDefault())?.Focus();
    }

    void RefreshSound()
    {
        try { OutputName.Text = AudioOutputs.DefaultName() ?? "No playback device"; }
        catch { OutputName.Text = "Output unavailable"; }
        _syncing = true;
        try
        {
            var (percent, muted) = AudioOutputs.GetVolume();
            VolumeSlider.Value = percent;
            VolumeText.Text = muted ? "Muted" : $"{percent}%";
            MuteButton.Tag = muted ? "\uE74F" : "\uE767";
            MuteButton.ToolTip = muted ? "Unmute" : "Mute";
        }
        catch { VolumeText.Text = "—"; }
        finally { _syncing = false; }
    }

    void ToggleMute(object s, RoutedEventArgs e)
    {
        try { var (_, muted) = AudioOutputs.GetVolume(); AudioOutputs.SetMute(!muted); } catch { }
        Refresh();
    }
}
