using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using GamingHub.App.Services;
using GamingHub.App.ViewModels;
using GamingHub.Core.Displays;
using GamingHub.Infrastructure.Displays;
using GamingHub.Infrastructure.Games;

namespace GamingHub.App;

public partial class MainWindow : Window
{
    readonly MainViewModel _vm;
    readonly ControllerService _pad;
    readonly GameLaunchService _games;
    readonly DisplayWatcher _watcher;
    static readonly PathToImage Images = new();
    IntPtr _hwnd;
    bool _heroOnA;
    readonly HashSet<int> _lowBatteryWarned = [];
    bool _homeMoveMode;
    GameTile? _selBefore;
    (string Path, ICommand? Command)? _refocusDisplay;

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    public MainWindow(MainViewModel vm, DisplayWatcher watcher, LauncherPlacementService placement, ControllerService pad, GameLaunchService games)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        if (App.IsDev) Title = "Gaming Hub (Dev)";
        _pad = pad;
        (_games, _watcher) = (games, watcher);
        // Place before first paint so the window never flashes on the wrong monitor.
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            watcher.Attach(_hwnd);
            placement.Attach(this, _hwnd);
        };
        placement.SuppressActivation = () => games.Running is not null;
        placement.RestoreInputFocus += () =>
        {
            if (QuickMenuWindow.Current is not null || App.ShuttingDown) return;
            var previous = FocusManager.GetFocusedElement(this) as UIElement;
            BringToFront();
            if (IsKeyboardFocusWithin) return;
            if (previous is { IsVisible: true, IsEnabled: true } && previous.Focus()) return;
            FocusFirst();
        };
        games.Exited += _ => BringToFront();   // return to the launcher only after the game is gone
        Loaded += async (_, _) =>
        {
            if (_vm.IsHome && !_vm.HomeHasSelection && _vm.HomeGames.Count > 0) _vm.SelectedGame = _vm.HomeGames[0];
            FocusFirst();
            UpdateHero();
            _ = Dispatcher.BeginInvoke(UpdateHero, System.Windows.Threading.DispatcherPriority.ContextIdle);   // once more after the first layout
            await vm.Rescan(quiet: vm.AppSettings.LastScanUtc is not null);   // every start: picks up installs/shortcuts added while closed
        };
        vm.PropertyChanged += OnVmChanged;
        SettingsLayer.PreviewMouseWheel += SmoothWheel;
        SizeChanged += (_, e) => FitLibrary(e.NewSize);
        vm.Displays.CollectionChanged += (_, _) => Dispatcher.BeginInvoke(RefocusDisplayButton, System.Windows.Threading.DispatcherPriority.Loaded);
        Carousel.SelectionChanged += (_, _) => AnimateTiles(Carousel, 1.18);
        LibraryGrid.SelectionChanged += (_, _) => AnimateTiles(LibraryGrid, 1.06);
        LibraryGrid.MouseRightButtonUp += (_, e) =>   // right-click a game: its settings
        {
            if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(LibraryGrid, d) is ListBoxItem item) { item.IsSelected = true; _vm.BeginEditCommand.Execute(null); }
        };
        Carousel.ItemContainerGenerator.StatusChanged += (_, _) => AnimateTiles(Carousel, 1.18);
        pad.Action += a => Dispatcher.BeginInvoke(() => OnPad(a));
        ImageSearchWindow.CurrentChanged += UpdateStickMode;
        vm.MinimizeRequested += () => { CloseOsk(); WindowState = WindowState.Minimized; };
        pad.Analog += (lx, ly, ry) => Dispatcher.BeginInvoke(() => OnAnalog(lx, ly, ry));
        pad.StatusChanged += s => Dispatcher.BeginInvoke(() =>
        {
            vm.ControllerStatus = s;
            vm.Pads = pad.Pads;
            foreach (var p in pad.Pads)   // one warning per pad each time it drops to 15% or below (reset once charging / above 20%)
            {
                if (p.HasBattery && p.Battery <= 15 && !p.Charging && _lowBatteryWarned.Add(p.Player))
                    vm.ShowToast($"🔋 Controller {p.Player} battery low ({p.Battery}%) — plug it in soon");
                else if (p.Charging || p.Battery > 20) _lowBatteryWarned.Remove(p.Player);
            }
        });
        vm.ControllerStatus = pad.Status;
        vm.Pads = pad.Pads;
        Activated += (_, _) => pad.HubFocused = true;     // light bars / takeover feedback only while the hub is in front
        Deactivated += (_, _) => { pad.HubFocused = false; Topmost = false; };
        MouseMove += (_, _) => { if (Mouse.OverrideCursor is not null) Mouse.OverrideCursor = null; };
        // Button hints follow the input in use. Mouse moves count only when no controller pointer moved it (touchpad / stick).
        PreviewMouseDown += (_, _) => { if (Environment.TickCount64 - _padPointerAt > 300) _vm.UsingController = false; };
        pad.Touchpad += (dx, dy) => Dispatcher.BeginInvoke(() => OnTouchpad(dx, dy));
    }


    // ---- Smooth mouse-wheel scrolling (Settings and other long pages) ----
    double _scrollTarget = -1;
    ScrollViewer? _scrolling;

    void SmoothWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        e.Handled = true;
        double from = _scrollTarget >= 0 && _scrolling == sv ? _scrollTarget : sv.VerticalOffset;
        _scrollTarget = Math.Clamp(from - e.Delta * 1.2, 0, sv.ScrollableHeight);
        if (_vm.AppSettings.ReducedMotion) { sv.ScrollToVerticalOffset(_scrollTarget); _scrollTarget = -1; return; }
        if (_scrolling is null) CompositionTarget.Rendering += ScrollStep;   // one frame handler while gliding
        _scrolling = sv;
    }

    void ScrollStep(object? s, EventArgs e)
    {
        if (_scrolling is not { } sv) return;
        double d = _scrollTarget - sv.VerticalOffset;
        if (Math.Abs(d) < 0.5)
        {
            sv.ScrollToVerticalOffset(_scrollTarget);
            _scrollTarget = -1; _scrolling = null;
            CompositionTarget.Rendering -= ScrollStep;
            return;
        }
        sv.ScrollToVerticalOffset(sv.VerticalOffset + d * 0.2);   // ease out
    }
    void OnVmChanged(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.SelectedLightBar) or nameof(MainViewModel.SelectedPlayerTwoLightBar)) _pad.RefreshLights();
        if (e.PropertyName is nameof(MainViewModel.CurrentPage) or nameof(MainViewModel.IsEditing) or nameof(MainViewModel.Testing)
            or nameof(MainViewModel.ChoosingFor) or nameof(MainViewModel.IsFirstRun) or nameof(MainViewModel.Launching) or nameof(MainViewModel.PowerMenuOpen))
            Dispatcher.BeginInvoke(FocusFirst, System.Windows.Threading.DispatcherPriority.Input);
        UpdateStickMode();
        if (e.PropertyName == nameof(MainViewModel.CurrentPage))
        {
            _homeMoveMode = false;
            if (_vm.IsLibrary) RevealContent(LibraryGrid);
            else if (_vm.IsSettings) RevealContent(SettingsPage, enteringSettings: true);
        }
        if (e.PropertyName == nameof(MainViewModel.SettingsSection) && _vm.IsSettings)
            RevealContent(SettingsLayer);
        if (e.PropertyName == nameof(MainViewModel.SettingsSection))   // new page: start at its top, focus stays in the rail
            Dispatcher.BeginInvoke(() => { SettingsLayer.ScrollToTop(); if (!SettingsRail.IsKeyboardFocusWithin && Keyboard.FocusedElement is UIElement { IsVisible: false }) FocusFirst(); }, System.Windows.Threading.DispatcherPriority.Loaded);
        if (e.PropertyName is nameof(MainViewModel.SelectedGame) or nameof(MainViewModel.CurrentPage)) UpdateHero();
    }


    void RevealContent(FrameworkElement content, bool enteringSettings = false)
    {
        if (!IsLoaded || _vm.AppSettings.ReducedMotion)
        {
            content.BeginAnimation(OpacityProperty, null);
            return;
        }

        content.BeginAnimation(OpacityProperty, new DoubleAnimation(enteringSettings ? 0 : 0.65, 1, Motion(enteringSettings ? 240 : 160))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        });
    }

    /// <summary>Top-bar quick settings button: the quick menu on Gaming Hub's screen.</summary>
    void OpenQuickSettings(object s, RoutedEventArgs e) =>
        OpenQuickMenu(WindowPlacement.GetHostMonitorBounds(_hwnd), (uint)VisualTreeHelper.GetDpi(this).PixelsPerInchX);

    void OpenQuickMenu(PixelRect screen, uint dpi, IntPtr gameWindow = default)
    {
        CloseOsk();
        QuickMenuWindow.Open(this, _vm, _games, screen, dpi, gameWindow);
        if (_vm.AppSettings.NavigationSounds) UiSounds.Play(UiSounds.Kind.Select);
    }

    /// <summary>Capture the foreground game before showing the menu, including games launched outside the hub.</summary>
    void OpenQuickMenuOverGame()
    {
        // Always the game's own screen, measured live: a game may have changed that screen's resolution, so the saved
        // display list can be out of date. Exclusive full-screen games usually step aside (minimize) when the menu takes focus.
        var fg = GameWindows.Foreground();
        var screen = WindowPlacement.GetHostMonitorBounds(fg);
        uint dpi = WindowPlacement.GetHostMonitorDpi(fg);
        Serilog.Log.Information("Quick menu over game: screen {Screen} at {Dpi} dpi, exclusive full screen={Exclusive}",
            screen, dpi, GameWindows.ExclusiveFullscreenInFront());
        if (screen.Width == 0) { screen = WindowPlacement.GetHostMonitorBounds(_hwnd); dpi = (uint)VisualTreeHelper.GetDpi(this).PixelsPerInchX; }
        _vm.UsingController = true;
        OpenQuickMenu(screen, dpi, fg);
    }

    bool _steamHintShown;

    /// <summary>If Steam grabs the PS press (Big Picture / overlay comes to the front), explain the one-time fix — once per run.</summary>
    void WatchForSteamTakeover()
    {
        if (_steamHintShown) return;
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            if (GameWindows.ForegroundProcessName() is "steamwebhelper" or "steam" && !_steamHintShown)
            {
                _steamHintShown = true;
                _vm.ShowToast("Steam took the PS button. Fix it once: Settings › Controller › Open Steam controller settings, then turn off “Guide Button Focuses Steam”.");
            }
        };
        t.Start();
    }
    /// <summary>Shows the window without stealing focus from games (only called on user request or after a game exits).</summary>
    public void BringToFront()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        // A running game's full-screen window is often "always on top": come forward above it, and drop that again as soon
        // as Gaming Hub stops being the active window (Return to game, Play, or clicking the game).
        if (_games.Running is not null) Topmost = true;
        Activate();
        GameWindows.ForceForeground(_hwnd);   // also works when triggered by a background controller press
    }

    /// <summary>Creates the window handle (display watching, placement) without showing it — tray start.</summary>
    public void InitializeHidden() => new WindowInteropHelper(this).EnsureHandle();

    protected override void OnClosing(CancelEventArgs e)
    {
        // Closing hides to the tray; exit via tray menu or Settings › Exit.
        if (!App.ShuttingDown) { e.Cancel = true; Hide(); }
        base.OnClosing(e);
    }

    /// <summary>Keeps keyboard/controller focus inside the visible layer.</summary>
    /// <summary>The overlay on top (power menu › Starting… › display test › display chooser › game settings › first run), or null.</summary>
    UIElement? ActiveOverlay() =>
        _vm.PowerMenuOpen ? PowerLayer : _vm.Launching is not null ? LaunchLayer : _vm.Testing is not null ? TestLayer
        : _vm.ChoosingFor is not null ? ChooseLayer : _vm.IsEditing ? AddLayer : _vm.IsFirstRun ? FirstRunLayer : null;

    /// <summary>Inside the overlay — an open dropdown's items live in a popup, so they count via their dropdown.</summary>
    static bool InOverlay(UIElement ov, DependencyObject? d)
    {
        if (d is ComboBoxItem item && ItemsControl.ItemsControlFromItemContainer(item) is { } owner) d = owner;
        return d is not null && (d == ov || ov.IsAncestorOf(d));
    }

    /// <summary>After a move: if focus left the open overlay, put it back where it was (or on the overlay's first control).</summary>
    void KeepInOverlay(IInputElement? before)
    {
        if (ActiveOverlay() is not { } ov || InOverlay(ov, Keyboard.FocusedElement as DependencyObject)) return;
        if (before is UIElement { IsVisible: true } b && InOverlay(ov, b)) b.Focus(); else FocusFirst();
    }

    void FocusSettingsDetail() => FirstFocusable(SettingsLayer)?.Focus();

    void SettingsCategoryClicked(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject hit && ItemsControl.ContainerFromElement(SettingsRail, hit) is ListBoxItem { DataContext: SettingsCategory section })
        {
            _vm.SettingsSection = section;
            FocusSettingsDetail();
            e.Handled = true;
        }
    }

    public void FocusFirst()
    {
        // Safe default: a stray Select must never confirm a display test.
        if (_vm.Testing is not null) { RevertButton.Focus(); return; }
        if (_oskTarget is not null) { OskKeys.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)); return; }
        if (_vm.Launching is not null) { LaunchLayer.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)); return; }
        if (_vm.PowerMenuOpen) { PowerLayer.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)); return; }
        FrameworkElement scope = _vm.ChoosingFor is not null ? ChooseLayer : _vm.IsEditing ? AddLayer : _vm.IsFirstRun ? FirstRunLayer
            : _vm.IsSettings ? SettingsRail : _vm.IsLibrary ? LibraryGrid : Carousel;
        if (scope == LibraryGrid && !LibraryGrid.HasItems) { LibrarySearch.Focus(); return; }
        if (scope is ListBox lb && lb.SelectedItem is { } item && lb.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem c) c.Focus();
        else scope.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    // ---- Visuals: hero crossfade + selected tile scale (instant with Reduced motion) ----
    Duration Motion(int ms) => _vm.AppSettings.ReducedMotion ? new Duration(TimeSpan.Zero) : new Duration(TimeSpan.FromMilliseconds(ms));


    void UpdateHero()
    {
        if (!_vm.IsHome) { MissingHero.Visibility = Visibility.Collapsed; return; }
        // Decode by the screen's physical height (1080, or 2160 on a 4K TV): wide art then fills the screen without upscaling.
        int px = Math.Clamp((int)(ActualHeight * VisualTreeHelper.GetDpi(this).DpiScaleY), 1080, 2160);
        var src = Images.Convert(_vm.SelectedGame?.Hero, typeof(BitmapSource), "h" + px, System.Globalization.CultureInfo.InvariantCulture) as BitmapSource;
        if (src is null && _vm.SelectedGame is { Cover: { } cover } tile && cover != tile.Hero)
            src = Images.Convert(cover, typeof(BitmapSource), "h" + px, System.Globalization.CultureInfo.InvariantCulture) as BitmapSource;
        MissingHero.Visibility = src is null && _vm.SelectedGame is not null ? Visibility.Visible : Visibility.Collapsed;
        Serilog.Log.Debug("Hero {Path} -> {W}x{H}", _vm.SelectedGame?.Hero, src?.PixelWidth, src?.PixelHeight);
        var (show, hide) = _heroOnA ? (HeroB, HeroA) : (HeroA, HeroB);
        if (hide.Tag == src)
        {
            // Same art as the layer that is (or was just) showing — e.g. the selection bounced back during start-up.
            // Make sure that layer ends fully visible instead of being left mid-fade-out.
            hide.BeginAnimation(OpacityProperty, new DoubleAnimation(src is null ? 0 : 1, Motion(250)));
            show.BeginAnimation(OpacityProperty, new DoubleAnimation(0, Motion(250)));
            return;
        }
        _heroOnA = !_heroOnA;
        show.Tag = src;
        foreach (var img in show.Children.OfType<Image>()) img.Source = src;
        if (show.Children[1] is Image sharp)
        {
            // Wide art: fills the area above the game's name, faded into the blur. Tall posters: whole image on the right.
            bool wide = src is null || src.PixelWidth >= src.PixelHeight * 1.2;
            Grid.SetRowSpan(sharp, wide ? 1 : 2);
            sharp.Stretch = wide ? Stretch.UniformToFill : Stretch.Uniform;
            sharp.HorizontalAlignment = wide ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
            sharp.OpacityMask = wide ? (Brush)FindResource("HeroFade") : null;
        }
        show.BeginAnimation(OpacityProperty, new DoubleAnimation(src is null ? 0 : 1, Motion(450)));
        if (!_vm.AppSettings.ReducedMotion)   // slow cinematic zoom-out as the new art arrives
        {
            var zoom = new ScaleTransform();
            show.RenderTransform = zoom;
            var anim = new DoubleAnimation(1.07, 1.0, new Duration(TimeSpan.FromMilliseconds(1800))) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            zoom.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
            zoom.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        }
        if (_vm.IsHome && HomeDetails.RenderTransform is TranslateTransform slide)   // title, buttons and cards glide in
        {
            HomeDetails.BeginAnimation(OpacityProperty, new DoubleAnimation(0.4, 1.0, Motion(300)));   // from 40%: stays readable while scrolling fast
            slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(_vm.AppSettings.ReducedMotion ? 0 : 14, 0, Motion(300)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
        hide.BeginAnimation(OpacityProperty, new DoubleAnimation(0, Motion(450)));
    }

    /// <summary>Library tile scrolled to: keep a margin around it, so the selected tile's growth and glow aren't cut at the
    /// list's edge. Runs on the tile itself — the list's scroller would otherwise handle the plain request first.</summary>
    void LibraryTile_RequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        if (sender is not ListBoxItem it || e.TargetObject != it || e.TargetRect.X < 0) return;   // our own widened request: let it through
        e.Handled = true;
        it.BringIntoView(new Rect(-20, -30, it.ActualWidth + 40, it.ActualHeight + 60));
    }

    void AnimateTiles(ListBox list, double selectedScale)
    {
        foreach (var item in list.Items)
        {
            if (list.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem c || c.Template?.FindName("t", c) is not Border b) continue;
            if (b.RenderTransform is not ScaleTransform st || st.IsFrozen) b.RenderTransform = st = new ScaleTransform();
            double to = c.IsSelected ? selectedScale : 1.0;
            if (Math.Abs(st.ScaleX - to) < 0.001) continue;
            var anim = new DoubleAnimation(to, Motion(210)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        }
        if (list.SelectedItem is not null) list.ScrollIntoView(list.SelectedItem);
    }

    // ---- Controller routing: only while Gaming Hub is the foreground window (never hijack games) ----
    bool _secondTapClosedGameOverlay;
    void OnPad(NavAction a)
    {
        if (a == NavAction.BackReleased)
        {
            if (QuickMenuWindow.Current is { WaitingForCircleRelease: true } menu) menu.Back();
            return;
        }
        if (a == NavAction.GuideSecondTap)
        {
            var menu = QuickMenuWindow.Current;
            _secondTapClosedGameOverlay = menu?.OverGame == true;
            menu?.DismissFromGuide();
            return;
        }
        if (a == NavAction.GuideTriple)   // triple PS: extend to the TV (if enabled); never over a fullscreen game unless allowed
        {
            // In a game? Either the quick menu the first press opened is over a game (the menu is in front then, so the
            // foreground alone would hide the game), or a full-screen app other than Gaming Hub is in front.
            GetWindowThreadProcessId(GetForegroundWindow(), out uint fgPid);
            bool ours = fgPid == (uint)Environment.ProcessId;
            bool inGame = _secondTapClosedGameOverlay || QuickMenuWindow.Current?.OverGame == true || (!ours && GameWindows.FullscreenAppInFront());
            QuickMenuWindow.Current?.CloseMenu();
            if (!inGame || _vm.ControllerSettings.PsButtonOverGames) _vm.ExtendToPreferredDisplay(inGame);
            else Serilog.Log.Information("PS triple press ignored: a game is running full screen (allow it in Settings › Controller)");
            return;
        }
        if (QuickMenuWindow.Current is { } qm)   // quick menu open: it gets the controller
        {
            _vm.UsingController = true;
            switch (a)
            {
                case NavAction.Up: qm.Navigate(FocusNavigationDirection.Up); break;
                case NavAction.Down: qm.Navigate(FocusNavigationDirection.Down); break;
                case NavAction.Left: qm.Navigate(FocusNavigationDirection.Left); break;
                case NavAction.Right: qm.Navigate(FocusNavigationDirection.Right); break;
                case NavAction.Accept: qm.Press(); break;
                case NavAction.Back:
                    if (qm.OverGame) qm.WaitingForCircleRelease = true; // restore focus after Circle is up
                    else qm.Back();
                    break;
                case NavAction.Guide or NavAction.GuideHold: qm.DismissFromGuide(); break;
            }
            if (_vm.AppSettings.NavigationSounds)
                UiSounds.Play(a switch { NavAction.Accept => UiSounds.Kind.Select, NavAction.Back or NavAction.Guide => UiSounds.Kind.Back, _ => UiSounds.Kind.Move });
            return;
        }
        if (a == NavAction.GuideHold) { if (IsVisible) { CloseOsk(); Hide(); } return; }   // hold PS: hide to tray
        if (ImageSearchWindow.Current is { } web && GetForegroundWindow() == web.Handle)
        {
            switch (a)
            {
                case NavAction.Accept or NavAction.TouchClick: ClickAtCursor(); break;
                case NavAction.Square: ClickAtCursor(right: true); break;
                case NavAction.Back: web.Close(); break;
            }
            return;
        }
        if (GetForegroundWindow() != _hwnd)
        {
            var c = _vm.ControllerSettings;
            if (a == NavAction.TouchClick && c.TouchpadPointer && TouchpadOutsideAllowed) { ClickAtCursor(outside: true); return; }   // e.g. click Gaming Hub to bring it back
            if (a == NavAction.Share) Serilog.Log.Information("Create pressed while Gaming Hub isn't in front: ignored");
            if (a != NavAction.Guide) return;
            bool inGame = GameWindows.FullscreenAppInFront() || (_games.Running is not null && GameWindows.Foreground() == _games.RunningWindow);
            if (inGame) { if (c.QuickMenuOverGames) OpenQuickMenuOverGame(); return; }
            if (c.PsButtonOpensHub) { BringToFront(); WatchForSteamTakeover(); }
            return;
        }
        _vm.UsingController = true;
        // Controls list: Create toggles it; while it shows, ○ closes it and nothing else acts.
        if (a == NavAction.Share || _vm.ShowControls)
        {
            if (a == NavAction.Share) Serilog.Log.Information("Create pressed: Controls list {State}", _vm.ShowControls ? "closed" : "opened");
            if (a is NavAction.Share or NavAction.Back) { _vm.ShowControls = !_vm.ShowControls && a == NavAction.Share; Sound(a == NavAction.Share ? NavAction.Accept : NavAction.Back, null); }
            return;
        }
        if (a == NavAction.TouchClick) { if (_vm.ControllerSettings.TouchpadPointer) ClickAtCursor(outside: SecureDesktopActive()); return; }
        var focusBefore = Keyboard.FocusedElement;
        _selBefore = _vm.SelectedGame;
        if (_oskTarget is not null) { OskPad(a); Sound(a, focusBefore); return; }
        if (a is >= NavAction.Up and <= NavAction.Right) _cursorMode = false;
        if (a == NavAction.Accept && _cursorMode && _vm.IsSettings) { ClickAtCursor(); Sound(a, null); return; }
        if (!_cursorMode && _vm.AppSettings.HidePointerWithController) Mouse.OverrideCursor = Cursors.None;   // controller in use: hide the pointer
        bool overlay = _vm.IsEditing || _vm.Testing is not null || _vm.ChoosingFor is not null || _vm.IsFirstRun || _vm.Launching is not null || _vm.PowerMenuOpen;
        // An open overlay owns the controller: if focus slipped behind it, bring it back first and don't act on the page behind.
        if (ActiveOverlay() is { } ov && !InOverlay(ov, Keyboard.FocusedElement as DependencyObject))
        {
            FocusFirst();
            if (a is not NavAction.Back) { Sound(a, focusBefore); return; }
        }
        if (_vm.IsHome && !overlay && Keyboard.FocusedElement is ListBoxItem homeTile and not ComboBoxItem && Carousel.IsAncestorOf(homeTile))
        {
            // □ on a Home game: move mode (← → moves it, □ / ✕ / ○ finishes). Switches Home to "My own order".
            if (a == NavAction.Square || (_homeMoveMode && a is NavAction.Accept or NavAction.Back))
            {
                _homeMoveMode = !_homeMoveMode && a == NavAction.Square;
                _vm.ShowToast(_homeMoveMode ? "Moving this game: ← → to move it, □ when done" : "Home order saved");
                Sound(NavAction.Accept, null);
                return;
            }
            if (_homeMoveMode && a is NavAction.Left or NavAction.Right)
            {
                _vm.MoveHomeGame(a == NavAction.Left ? -1 : 1);
                Carousel.UpdateLayout();
                FocusFirst();
                Sound(NavAction.TabNext, null);
                return;
            }
        }
        switch (a)
        {
            case NavAction.Up: Navigate(Key.Up, FocusNavigationDirection.Up); KeepInOverlay(focusBefore); break;
            case NavAction.Down: Navigate(Key.Down, FocusNavigationDirection.Down); KeepInOverlay(focusBefore); break;
            case NavAction.Left: Navigate(Key.Left, FocusNavigationDirection.Left); KeepInOverlay(focusBefore); break;
            case NavAction.Right: Navigate(Key.Right, FocusNavigationDirection.Right); KeepInOverlay(focusBefore); break;
            case NavAction.Accept: Activate(Keyboard.FocusedElement); _pad.Rumble(); break;
            case NavAction.Back:
                if (Keyboard.FocusedElement is ComboBoxItem open && ItemsControl.ItemsControlFromItemContainer(open) is ComboBox owner)
                { owner.IsDropDownOpen = false; owner.Focus(); }   // ○ closes the dropdown without changing it
                else if (!overlay && _vm.IsSettings && SettingsLayer.IsKeyboardFocusWithin) FocusFirst();
                else _vm.Back();
                break;
            case NavAction.TabPrev: if (!overlay && !(_vm.IsSettings && _vm.StepSettingsCategory(-1))) _vm.CycleTab(-1); break;
            case NavAction.TabNext: if (!overlay && !(_vm.IsSettings && _vm.StepSettingsCategory(+1))) _vm.CycleTab(+1); break;
            case NavAction.Context: if (!overlay) _vm.ContextAction(); break;
            case NavAction.Menu when !overlay && Keyboard.FocusedElement is ListBoxItem { DataContext: GameTile }:
                _vm.BeginEditCommand.Execute(null);   // Options on a game: its settings
                break;
            case NavAction.Menu: if (overlay) break; if (_vm.IsSettings) _vm.ShowHomeCommand.Execute(null); else _vm.ShowSettingsCommand.Execute(null); break;
            case NavAction.Guide: OpenQuickMenu(WindowPlacement.GetHostMonitorBounds(_hwnd), (uint)VisualTreeHelper.GetDpi(this).PixelsPerInchX); WatchForSteamTakeover(); break;
        }
        Sound(a, focusBefore);
    }

    /// <summary>Soft UI sound: a tick when focus actually moves (or the tab changes), a rise on ✕, a fall on ○.</summary>
    void Sound(NavAction a, IInputElement? focusBefore)
    {
        if (!_vm.AppSettings.NavigationSounds) return;
        if (a is >= NavAction.Up and <= NavAction.Right)
        {
            // Lists can move focus a moment later than the selection: decide once both have settled.
            var sel = _selBefore;
            Dispatcher.BeginInvoke(() =>
            {
                if (Keyboard.FocusedElement != focusBefore || _vm.SelectedGame != sel) UiSounds.Play(UiSounds.Kind.Move);
            }, System.Windows.Threading.DispatcherPriority.Input);
            return;
        }
        UiSounds.Kind? k = a switch
        {
            NavAction.TabPrev or NavAction.TabNext => UiSounds.Kind.Move,
            NavAction.Accept => UiSounds.Kind.Select,
            NavAction.Back => UiSounds.Kind.Back,
            _ => null,
        };
        if (k is { } kind) UiSounds.Play(kind);
    }

    void Navigate(Key key, FocusNavigationDirection dir)
    {
        if (Keyboard.FocusedElement is not UIElement f || !f.IsVisible) { FocusFirst(); return; }
        if (Jump(f, key)) return;
        // Only lists and open dropdowns get the arrow key itself; for everything else (text boxes, closed dropdowns,
        // controls inside scroll areas) the key would be eaten by caret movement, value changes or scrolling.
        // Dropdowns never change value while you move: an open list only moves a highlight, ✕ picks, ○ cancels.
        if (f is ComboBoxItem item && ItemsControl.ItemsControlFromItemContainer(item) is ComboBox owner)
        {
            if (key is Key.Up or Key.Down)
            {
                int i = owner.ItemContainerGenerator.IndexFromContainer(item) + (key == Key.Down ? 1 : -1);
                if (owner.ItemContainerGenerator.ContainerFromIndex(Math.Clamp(i, 0, owner.Items.Count - 1)) is ComboBoxItem next)
                { next.Focus(); next.BringIntoView(); }
            }
            return;
        }
        if (f is ComboBox { IsDropDownOpen: true } open)
        {
            ((open.ItemContainerGenerator.ContainerFromItem(open.SelectedItem) ?? open.ItemContainerGenerator.ContainerFromIndex(0)) as UIElement)?.Focus();
            return;
        }
        if (f is ListBoxItem)
        {
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this)!, 0, key) { RoutedEvent = Keyboard.KeyDownEvent };
            f.RaiseEvent(args);
            if (args.Handled) return;
        }
        f.MoveFocus(new TraversalRequest(dir));
        // ← at the left edge of a Settings page goes back to the category rail.
        if (key == Key.Left && Keyboard.FocusedElement == f && SettingsLayer.IsAncestorOf(f)) { FocusFirst(); return; }
        // Nothing above inside the page (e.g. the first display card in Settings): ↑ goes to the top menu.
        if (key == Key.Up && Keyboard.FocusedElement == f && !TopNav.IsAncestorOf(f) && _oskTarget is null && !_vm.IsEditing && _vm.Testing is null
            && _vm.ChoosingFor is null && !_vm.IsFirstRun && _vm.Launching is null && !_vm.PowerMenuOpen)
        { CurrentNav().Focus(); return; }
        (Keyboard.FocusedElement as FrameworkElement)?.BringIntoView();
    }

    /// <summary>Vertical moves between the page rows that lists would otherwise trap:
    /// top menu ⇅ carousel ⇅ Play row (Home), top menu ⇅ toolbar ⇅ grid (Library).</summary>
    bool Jump(UIElement f, Key key)
    {
        if (key is Key.Left or Key.Right && _vm.IsHome && f is ListBoxItem && Carousel.IsAncestorOf(f))
            return StepCarousel(key == Key.Left ? -1 : 1);
        if (f is ComboBoxItem or ComboBox { IsDropDownOpen: true }) return false;   // let open dropdowns use the arrows
        // Top menu: → from the last tab goes to the power button, ← comes back.
        if (key == Key.Right && f == NavSettings) { NavPower.Focus(); return true; }
        if (key == Key.Left && f == NavPower) { NavSettings.Focus(); return true; }
        if (_vm.IsSettings && f is ListBoxItem && SettingsRail.IsAncestorOf(f) && _oskTarget is null && !_vm.IsEditing && _vm.Testing is null && !_vm.PowerMenuOpen)
        {
            if (key == Key.Right) { FocusSettingsDetail(); return true; }
            if (key == Key.Up && SettingsRail.SelectedIndex == 0) { CurrentNav().Focus(); return true; }   // top of rail → menu
        }
        if (key is not (Key.Up or Key.Down) || _oskTarget is not null || _vm.Launching is not null || _vm.PowerMenuOpen || _vm.IsEditing || _vm.Testing is not null || _vm.ChoosingFor is not null || _vm.IsFirstRun) return false;
        bool up = key == Key.Up;
        bool In(UIElement scope) => scope == f || scope.IsAncestorOf(f);
        UIElement? target = null;
        if (In(TopNav) || In(NavPower)) { if (up) return true; FocusFirst(); return true; }
        if (_vm.IsSettings && In(DisplayMap))
            target = up ? CurrentNav() : FirstFocusable(DisplayActions) ?? FallbackToPrimary;
        else if (_vm.IsSettings && up && In(DisplayActions) && DisplayMap.SelectedItem is { } display)
            target = DisplayMap.ItemContainerGenerator.ContainerFromItem(display) as UIElement;
        else if (_vm.IsHome)
        {
            if (In(Carousel)) target = up ? CurrentNav() : PlayButton.IsVisible ? PlayButton : null;
            else if (In(PlayRow) && up) { FocusFirst(); return true; }
        }
        else if (_vm.IsLibrary)
        {
            if (In(LibraryBar) && up) target = CurrentNav();
            else if (In(LibraryBar) && !LibraryGrid.HasItems) return true;
            else if (In(LibraryBar)) target = (UIElement?)LibraryGrid.ItemContainerGenerator.ContainerFromIndex(Math.Max(0, LibraryGrid.SelectedIndex)) ?? LibraryGrid;
            else if (In(LibraryGrid) && up && f is ListBoxItem item && LibraryGrid.ItemContainerGenerator.ContainerFromIndex(0) is UIElement first
                     && Math.Abs(item.TranslatePoint(default, LibraryGrid).Y - first.TranslatePoint(default, LibraryGrid).Y) < 5)
                target = LibrarySearch;   // grid top row ↑ → search box
        }
        if (target is null) return false;
        target.Focus();
        return true;
    }

    /// <summary>Moves the carousel selection one tile, realizing and scrolling to off-screen tiles first
    /// (the virtualized ListBox's own arrow handling can get stuck on tiles that are scrolled out of view).</summary>
    bool StepCarousel(int delta)
    {
        if (!Carousel.HasItems) return false;
        int i = Math.Clamp(Carousel.SelectedIndex + delta, 0, Carousel.Items.Count - 1);
        if (i == Carousel.SelectedIndex) return true;
        Carousel.SelectedIndex = i;
        Carousel.ScrollIntoView(Carousel.Items[i]);
        Carousel.UpdateLayout();
        (Carousel.ItemContainerGenerator.ContainerFromIndex(i) as UIElement)?.Focus();
        return true;
    }


    /// <summary>First visible, enabled, focusable control inside a container (visual-tree order).</summary>
    static Control? FirstFocusable(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is Control { Focusable: true, IsVisible: true, IsEnabled: true } ctl && c is not ScrollViewer && c is not ItemsControl) return ctl;
            if (c is UIElement { IsVisible: false }) continue;
            if (FirstFocusable(c) is { } found) return found;
        }
        return null;
    }

    void FitLibrary(Size size)
    {
        const double gap = 12, panelInsets = 42;
        double available = Math.Max(160, size.Width - 96);
        double idealWidth = Math.Clamp(size.Height * 0.16, 116, 164);
        int columns = Math.Max(1, (int)((available - panelInsets) / (idealWidth + gap)));
        double width = Math.Floor((available - panelInsets) / columns - gap);
        LibraryLayer.Width = available;
        LibraryGrid.Resources["LibraryTileWidth"] = width;
        LibraryGrid.Resources["LibraryTileHeight"] = Math.Round((width - 6) * 1.5) + 50;
        foreach (var filter in LibraryBar.Children.OfType<ComboBox>())
            filter.MinWidth = available < 1100 ? 105 : 140;
    }
    Button CurrentNav() => _vm.IsSettings ? NavSettings : _vm.IsLibrary ? NavLibrary : NavHome;


    /// <summary>After Set Preferred / Set Fallback the display list is rebuilt; put focus back on the same button.</summary>
    void RefocusDisplayButton()
    {
        if (_refocusDisplay is not { } want) return;
        _refocusDisplay = null;
        static IEnumerable<DependencyObject> All(DependencyObject root)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var c = VisualTreeHelper.GetChild(root, i);
                yield return c;
                foreach (var d in All(c)) yield return d;
            }
        }
        All(SettingsLayer).OfType<Button>()
            .FirstOrDefault(b => b.DataContext is DisplayItem di && di.Info.Identity.DevicePath == want.Path && b.Command == want.Command)
            ?.Focus();
    }
    void Activate(IInputElement? focused)
    {
        switch (focused)
        {
            case ComboBoxItem ci:   // before ListBoxItem: ComboBoxItem derives from it
                ci.IsSelected = true;
                if (ItemsControl.ItemsControlFromItemContainer(ci) is ComboBox owner) { owner.IsDropDownOpen = false; owner.Focus(); }
                break;
            case ListBoxItem { DataContext: SettingsCategory }:
                FocusSettingsDetail();
                break;
            case ListBoxItem { DataContext: DisplayItem }:
                FirstFocusable(DisplayActions)?.Focus();
                break;
            case ListBoxItem { DataContext: GameTile }: _vm.PlayCommand.Execute(null); break;
            case CheckBox cb:
                cb.IsChecked = cb.IsChecked != true;
                cb.Command?.Execute(cb.CommandParameter);
                break;
            case Button b:
                if (b.DataContext is DisplayItem di) _refocusDisplay = (di.Info.Identity.DevicePath, b.Command);   // list rebuilds: come back here
                ((IInvokeProvider)new ButtonAutomationPeer(b)).Invoke();
                break;
            case ComboBox c:
                c.IsDropDownOpen = true;
                Dispatcher.BeginInvoke(() =>   // put focus on the current choice so the D-pad moves through the list
                    ((c.ItemContainerGenerator.ContainerFromItem(c.SelectedItem) ?? c.ItemContainerGenerator.ContainerFromIndex(0)) as UIElement)?.Focus(),
                    System.Windows.Threading.DispatcherPriority.Loaded);
                break;
            case TextBox t: OpenOsk(t); break;
        }
    }

    // ---- Controller on-screen keyboard ----
    TextBox? _oskTarget;
    static readonly string[] OskRows = ["1234567890", "qwertyuiop", "asdfghjkl'", "zxcvbnm,.-"];

    void OpenOsk(TextBox t)
    {
        if (OskKeys.Children.Count == 0)
        {
            foreach (var row in OskRows)
                OskKeys.Children.Add(OskRow(row.Select(ch => OskKey(ch.ToString(), 72, () => OskSet(_oskTarget!.Text + ch))).ToArray()));
            OskKeys.Children.Add(OskRow(
                OskKey("Space", 300, () => OskSet(_oskTarget!.Text + " ")),
                OskKey("⌫", 110, OskBackspace),
                OskKey("Clear", 140, () => OskSet("")),
                OskKey("Done", 140, CloseOsk)));
        }
        _oskTarget = t;
        OskText.Text = t.Text;
        OskOverlay.Visibility = Visibility.Visible;
        UpdateStickMode();
        FocusFirst();
    }

    static StackPanel OskRow(params UIElement[] keys)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var k in keys) p.Children.Add(k);
        return p;
    }

    static Button OskKey(string label, double width, Action press)
    {
        var b = new Button { Content = label, Width = width, Height = 64, Margin = new Thickness(4), Padding = new Thickness(0), FontSize = 22 };
        b.Click += (_, _) => press();
        return b;
    }

    void OskSet(string text)
    {
        _oskTarget!.Text = text;
        _oskTarget.CaretIndex = text.Length;
        OskText.Text = text;
    }

    void OskBackspace() { if (_oskTarget!.Text.Length > 0) OskSet(_oskTarget.Text[..^1]); }

    void CloseOsk()
    {
        if (_oskTarget is not { } t) return;
        t.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        _oskTarget = null;
        OskOverlay.Visibility = Visibility.Collapsed;
        UpdateStickMode();
        t.Focus();
    }

    void OskPad(NavAction a)
    {
        switch (a)
        {
            case NavAction.Up: Navigate(Key.Up, FocusNavigationDirection.Up); break;
            case NavAction.Down: Navigate(Key.Down, FocusNavigationDirection.Down); break;
            case NavAction.Left: Navigate(Key.Left, FocusNavigationDirection.Left); break;
            case NavAction.Right: Navigate(Key.Right, FocusNavigationDirection.Right); break;
            case NavAction.Accept: Activate(Keyboard.FocusedElement); _pad.Rumble(0x1800, 30); break;
            case NavAction.Square: OskBackspace(); break;
            case NavAction.Context: OskSet(_oskTarget!.Text + " "); break;
            case NavAction.Back or NavAction.Menu: CloseOsk(); break;
        }
    }

    // ---- Settings: right stick scrolls, left stick drives the mouse cursor ----
    bool _cursorMode;
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extra);
    struct POINT { public int X, Y; }
    struct RECT { public int L, T, R, B; }

    bool StickCursorAllowed => _vm.IsSettings && _oskTarget is null && !_vm.IsEditing && _vm.Testing is null && _vm.ChoosingFor is null && !_vm.IsFirstRun;

    /// <summary>The image search browser is mouse-driven, so the stick drives the pointer there too.</summary>
    void UpdateStickMode()
    {
        _pad.LeftStickNavigates = !StickCursorAllowed && ImageSearchWindow.Current is null;
        if (_pad.LeftStickNavigates) _cursorMode = false;   // left Settings: back to focus navigation, pointer hidden on next press
    }

    void OnAnalog(float lx, float ly, float ry)
    {
        var fg = GetForegroundWindow();
        bool web = ImageSearchWindow.Current is { } w0 && fg == w0.Handle;
        if (!web && (fg != _hwnd || !StickCursorAllowed)) return;
        static double Curve(float v) => Math.Abs(v) < 0.18f ? 0 : Math.Sign(v) * Math.Pow((Math.Abs(v) - 0.18) / 0.82, 2);
        if (Curve(ry) is var s and not 0)
        {
            if (web) mouse_event(0x0800, 0, 0, unchecked((uint)(int)(-s * 60)), IntPtr.Zero);   // mouse wheel
            else SettingsLayer.ScrollToVerticalOffset(SettingsLayer.VerticalOffset + s * 45);
        }
        double dx = Curve(lx) * 32, dy = Curve(ly) * 32;   // up to ~1900 px/s
        if (dx == 0 && dy == 0) return;
        _cursorMode = true;
        MovePointer(fg, dx, dy);
    }

    long _padPointerAt;   // last time a controller moved or clicked the pointer (so it doesn't count as mouse use)
    public bool PadPointerRecent => Environment.TickCount64 - _padPointerAt < 300;
    double _touchRestX, _touchRestY;   // sub-pixel remainders, so slow finger moves still move the pointer

    [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);

    /// <summary>FakerInput virtual mouse (if the user installed the driver): real-hardware-like input that also works on
    /// UAC prompts and administrator windows. Used for the pointer outside Gaming Hub; inside it the normal pointer is fine.</summary>
    readonly GamingHub.Infrastructure.Input.FakerInputMouse _faker = new();
    bool UseFaker => _vm.ControllerSettings.UseFakerInput && _faker.TryConnect();

    [DllImport("user32.dll")] static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr d);

    /// <summary>A UAC prompt (secure desktop) is showing: normal apps can't open the input desktop then.</summary>
    static bool SecureDesktopActive()
    {
        var d = OpenInputDesktop(0, false, 0x0001 /* DESKTOP_READOBJECTS */);
        if (d == IntPtr.Zero) return true;
        CloseDesktop(d);
        return false;
    }

    /// <param name="fg">Window to keep the pointer inside, or zero for the whole desktop (all screens).</param>
    void MovePointer(IntPtr fg, double dx, double dy)
    {
        _padPointerAt = Environment.TickCount64;
        _vm.UsingController = true;
        Mouse.OverrideCursor = null;
        if (fg == IntPtr.Zero && UseFaker) { _faker.Move((int)dx, (int)dy); return; }
        GetCursorPos(out var p);
        RECT w;
        if (fg != IntPtr.Zero) GetWindowRect(fg, out w);
        else { int x = GetSystemMetrics(76), y = GetSystemMetrics(77); w = new RECT { L = x, T = y, R = x + GetSystemMetrics(78), B = y + GetSystemMetrics(79) }; }
        SetCursorPos(Math.Clamp(p.X + (int)dx, w.L, w.R - 1), Math.Clamp(p.Y + (int)dy, w.T, w.B - 1));
    }

    /// <summary>Touchpad pointer outside Gaming Hub (desktop, dialogs, other apps) — never while a game runs or a
    /// full-screen app is in front. Clicks can't reach UAC prompts or administrator windows: Windows blocks them.</summary>
    bool TouchpadOutsideAllowed => SecureDesktopActive()   // UAC prompt: only reachable through FakerInput
        || (!GameWindows.ExclusiveFullscreenInFront() && !GameInFront());   // not "busy": Gaming Hub's own full-screen window reports that too

    /// <summary>The running game's window is in front. Error/message boxes never count, and before the game window
    /// is found (launcher, admin prompt, error) nothing does.</summary>
    bool GameInFront()
    {
        if (_games.Running is null || _games.RunningWindow == IntPtr.Zero) return false;
        var fg = GameWindows.Foreground();
        return fg == _games.RunningWindow && !GameWindows.IsDialog(fg);
    }

    /// <summary>DualSense touchpad as a laptop-style touchpad: finger movement moves the pointer and pressing the pad clicks —
    /// in Gaming Hub, and on the desktop/other apps when no game is running.</summary>
    void OnTouchpad(float dx, float dy)
    {
        if (!_vm.ControllerSettings.TouchpadPointer) return;
        var fg = GetForegroundWindow();
        bool ours = !SecureDesktopActive() && (fg == _hwnd || (ImageSearchWindow.Current is { } web && fg == web.Handle));
        if (!ours && !TouchpadOutsideAllowed) return;
        if (!ours) fg = IntPtr.Zero;   // anywhere on the desktop
        if (Environment.TickCount64 - _touchClickAt < 350) { _touchRestX = _touchRestY = 0; return; }   // pointer holds still around a click (double-click)
        // Pointer acceleration like a laptop touchpad: slow finger = precise, quick swipe = far.
        double speed = Math.Sqrt(dx * dx + dy * dy * 0.3);                  // per touch report, in pad widths
        double gain = Math.Clamp(speed / 0.008, 0.25, 1.8);
        double[] speeds = [0.5, 0.75, 1.0, 1.4, 2.0];
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX * 650 * gain * speeds[Math.Clamp(_vm.ControllerSettings.TouchpadSpeed, 1, 5) - 1];
        _touchRestX += dx * scale; _touchRestY += dy * scale * 0.55;   // the pad is about twice as wide as it is tall
        int mx = (int)_touchRestX, my = (int)_touchRestY;
        _touchRestX -= mx; _touchRestY -= my;
        if (mx == 0 && my == 0) return;
        _cursorMode = true;
        MovePointer(fg, mx, my);
    }

    long _touchClickAt;
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int max);
    static string ClassOf(IntPtr h) { var sb = new System.Text.StringBuilder(64); GetClassName(h, sb, sb.Capacity); return sb.ToString(); }

    void ClickAtCursor(bool right = false, bool outside = false)
    {
        _padPointerAt = _touchClickAt = Environment.TickCount64;
        // FakerInput (when installed) clicks like a real mouse everywhere — dialogs, other apps, UAC — so prefer it.
        bool faker = UseFaker && _faker.Click(right);
        GetCursorPos(out var p);
        var under = WindowFromPoint(p);
        GetWindowThreadProcessId(under, out uint pid);
        string proc; try { proc = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { proc = "?"; }
        Serilog.Log.Information("Controller click at {X},{Y} on {Proc} ({Class}) via {Path}; outside={Outside}, secure desktop={Secure}",
            p.X, p.Y, proc, ClassOf(under), faker ? "FakerInput" : "mouse_event", outside, SecureDesktopActive());
        if (faker) { _pad.Rumble(); return; }
        mouse_event(right ? 0x08u : 0x02u, 0, 0, 0, IntPtr.Zero);   // button down
        mouse_event(right ? 0x10u : 0x04u, 0, 0, 0, IntPtr.Zero);   // button up
        _pad.Rumble();
    }

    /// <summary>Same UI sounds for the keyboard: tick when focus moves, rise on Enter/Space, fall on Esc.</summary>
    void KeySound(KeyEventArgs e)
    {
        if (!_vm.AppSettings.NavigationSounds || Keyboard.FocusedElement is TextBox) return;   // no clicks while typing
        switch (e.Key)
        {
            case Key.Enter or Key.Space: UiSounds.Play(UiSounds.Kind.Select); break;
            case Key.Escape or Key.BrowserBack: UiSounds.Play(UiSounds.Kind.Back); break;
            case Key.F1 or Key.F2: UiSounds.Play(UiSounds.Kind.Move); break;
            case Key.Up or Key.Down or Key.Left or Key.Right or Key.Tab:
                var before = Keyboard.FocusedElement; var selBefore = _vm.SelectedGame;   // compare after the key is handled
                Dispatcher.BeginInvoke(() => { if (Keyboard.FocusedElement != before || _vm.SelectedGame != selBefore) UiSounds.Play(UiSounds.Kind.Move); },
                    System.Windows.Threading.DispatcherPriority.Input);
                break;
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key is not (Key.System or Key.LeftAlt or Key.RightAlt)) _vm.UsingController = false;   // Alt: the focus helper's synthetic tap
        KeySound(e);
        if (e.Key == Key.F4 && Keyboard.Modifiers == ModifierKeys.None) { _vm.ShowControls = !_vm.ShowControls; e.Handled = true; return; }   // Controls list
        if (e.Key == Key.F5 && Keyboard.Modifiers == ModifierKeys.None) { _vm.ShowControls = false; _vm.MinimizeCommand.Execute(null); e.Handled = true; return; }   // minimize to the taskbar
        if (_vm.ShowControls) { if (e.Key is Key.Escape or Key.BrowserBack) _vm.ShowControls = false; e.Handled = true; return; }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.Left or Key.Right && _vm.IsHome && e.OriginalSource is ListBoxItem k && Carousel.IsAncestorOf(k))
        {   // Ctrl+← / Ctrl+→ moves the selected Home game
            _vm.MoveHomeGame(e.Key == Key.Left ? -1 : 1);
            Carousel.UpdateLayout(); FocusFirst();
            e.Handled = true; return;
        }
        if (_oskTarget is not null && e.Key is Key.Escape or Key.BrowserBack) { CloseOsk(); e.Handled = true; }
        else if (_vm.IsSettings && !_vm.IsEditing && _vm.Testing is null && !_vm.PowerMenuOpen && _oskTarget is null
                 && e.Key is Key.Up or Key.Down or Key.Left or Key.Right && e.OriginalSource is not TextBox && e.OriginalSource is not ComboBoxItem
                 && e.OriginalSource is not ComboBox { IsDropDownOpen: true })
        {   // Settings: keyboard arrows use the same navigation as the D-pad (incl. ↑ from the top into the menu)
            Navigate(e.Key, e.Key switch { Key.Up => FocusNavigationDirection.Up, Key.Down => FocusNavigationDirection.Down, Key.Left => FocusNavigationDirection.Left, _ => FocusNavigationDirection.Right });
            e.Handled = true;
        }
        else if (e.Key == Key.Right && Keyboard.Modifiers == ModifierKeys.None && LibrarySearch.IsKeyboardFocused
                 && LibrarySearch.SelectionLength == 0 && LibrarySearch.CaretIndex == LibrarySearch.Text.Length)
        {
            e.Handled = LibraryFilter.Focus();
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None
                 && e.OriginalSource is ComboBox { IsDropDownOpen: false } list && LibraryBar.IsAncestorOf(list))
        {
            list.IsDropDownOpen = true;
            e.Handled = true;
        }
        else if (e.OriginalSource is ComboBox { IsDropDownOpen: false } closed && e.Key is Key.Up or Key.Down or Key.Left or Key.Right)
        {   // arrows pass over a closed dropdown instead of changing its value (Enter / Alt+↓ opens it)
            closed.MoveFocus(new TraversalRequest(e.Key switch
            {
                Key.Up => FocusNavigationDirection.Up, Key.Down => FocusNavigationDirection.Down,
                Key.Left => FocusNavigationDirection.Left, _ => FocusNavigationDirection.Right,
            }));
            e.Handled = true;
        }
        else if (e.OriginalSource is ComboBoxItem or ComboBox { IsDropDownOpen: true }) { }   // dropdown handles its own keys
        else if (e.Key == Key.Escape || e.Key == Key.BrowserBack) { if (_vm.IsSettings && SettingsLayer.IsKeyboardFocusWithin) FocusFirst(); else _vm.Back(); e.Handled = true; }
        else if (e.Key == Key.F1) { _vm.ShowHomeCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.F2) { _vm.ShowSettingsCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.F3) { OpenQuickMenu(WindowPlacement.GetHostMonitorBounds(_hwnd), (uint)VisualTreeHelper.GetDpi(this).PixelsPerInchX); e.Handled = true; }
        else if (e.Key == Key.Enter && e.OriginalSource is ListBoxItem { DataContext: SettingsCategory }) { FocusSettingsDetail(); e.Handled = true; }
        else if (e.Key == Key.Enter && e.OriginalSource is ListBoxItem { DataContext: DisplayItem }) { FirstFocusable(DisplayActions)?.Focus(); e.Handled = true; }
        else if (e.Key == Key.Enter && e.OriginalSource is ListBoxItem { DataContext: GameTile }) { _vm.PlayCommand.Execute(null); e.Handled = true; }
        else if (e.OriginalSource is UIElement f && Jump(f, e.Key)) e.Handled = true;
        base.OnPreviewKeyDown(e);
    }

    protected override void OnDrop(DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files &&
            files.FirstOrDefault(f => Path.GetExtension(f).Equals(".exe", StringComparison.OrdinalIgnoreCase)) is { } exe)
            _vm.BeginAdd(exe);
        base.OnDrop(e);
    }
}
