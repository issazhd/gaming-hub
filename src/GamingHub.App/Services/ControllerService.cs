using System.Collections.Concurrent;
using GamingHub.Core.Settings;
using Microsoft.Extensions.Logging;
using SDL;
using static SDL.SDL3;

namespace GamingHub.App.Services;

public enum NavAction { Up, Down, Left, Right, Accept, Back, TabPrev, TabNext, Context, Menu, Guide, GuideHold, GuideSecondTap, GuideTriple, Square, TouchClick, Share, BackReleased }

/// <summary>One connected controller as shown in the top bar.</summary>
public sealed record PadState(int Player, string Name, string Connection, int Battery, bool Charging, bool Active)
{
    public bool HasBattery => Battery >= 0;
    public string BatteryText => HasBattery ? $"{Battery}%" : "Unavailable";
    public double BatteryFraction => Math.Clamp(Battery, 8, 100) / 100.0;   // keep a visible sliver when nearly empty
    public string Tooltip => $"Controller {Player}: {Name} · {Connection}{(HasBattery ? $" · {Battery}%" : "")}{(Charging ? " (charging)" : "")}{(Active ? " · controlling Gaming Hub" : "")}";
}

/// <summary>
/// Gamepad input via SDL3 (DualSense over USB and Bluetooth through SDL's HIDAPI driver).
/// Runs SDL on a dedicated thread; raises navigation actions — the UI decides whether it is foreground.
/// </summary>
public sealed unsafe class ControllerService(SettingsService settings, ILogger<ControllerService> log) : IDisposable
{
    const ushort SteamVirtualVendor = 0x28DE;   // Steam Input virtual pad: ignore to avoid duplicate input
    readonly Dictionary<SDL_JoystickID, IntPtr> _pads = new();
    readonly Dictionary<SDL_JoystickID, int> _players = new();
    readonly ConcurrentQueue<Action> _work = new();
    Thread? _thread;
    volatile bool _stop;
    NavAction? _held;
    long _nextRepeat;
    long _nextStatus;
    SDL_JoystickID _activePad;
    long _activeUntil;

    long _guideDown;                               // >0 while PS is held; -1 once the hold fired
    long _lastGuideTap;
    int _guideTaps;
    SDL_JoystickID _guidePad;
    float _ry;
    long _nextAnalog;

    /// <summary>False while the UI uses the left stick as a mouse cursor (Settings).</summary>
    public volatile bool LeftStickNavigates = true;
    public event Action<NavAction>? Action;       // raised on the SDL thread
    /// <summary>Stick values (left x/y, right y) at ~60 Hz while a stick is used as an analog control.</summary>
    public event Action<float, float, float>? Analog;
    public event Action<string>? StatusChanged;   // raised on the SDL thread
    public event Action<string>? LowBattery;
    readonly HashSet<SDL_JoystickID> _lowBatteryReported = [];
    /// <summary>First finger's movement on the DualSense touchpad (fractions of the pad's width/height).</summary>
    public event Action<float, float>? Touchpad;
    float _tx = -1, _ty;
    public string Status { get; private set; } = "No controller";
    public IReadOnlyList<PadState> Pads { get; private set; } = [];

    bool _hubFocused;
    /// <summary>Set by the UI. Light bars and takeover rumble only change while Gaming Hub is in front (never during games).</summary>
    public bool HubFocused
    {
        get => _hubFocused;
        set { _hubFocused = value; if (value) _work.Enqueue(ApplyLights); }
    }

    ControllerSettings S => settings.Current.Controller;

    public void Start()
    {
        if (!S.Enabled) { Status = "Controller input disabled"; return; }
        _thread = new Thread(Loop) { IsBackground = true, Name = "SDL gamepad" };
        _thread.Start();
    }

    public void Rumble(ushort strength = 0x3000, uint ms = 60)
    {
        if (S.Vibration) _work.Enqueue(() => { if (_pads.TryGetValue(_activePad, out var p)) SDL_RumbleGamepad((SDL_Gamepad*)p, strength, strength, ms); });
    }

    void Loop()
    {
        SDL_SetHint("SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS", "1");   // we gate on foreground ourselves
        if (!SDL_Init(SDL_InitFlags.SDL_INIT_GAMEPAD))
        {
            SetStatus($"Controller unavailable: {SDL_GetError()}");
            log.LogError("SDL_Init failed: {Err}", SDL_GetError());
            return;
        }
        SDL_Event e;
        while (!_stop)
        {
            while (_work.TryDequeue(out var w)) w();
            if (SDL_WaitEventTimeout(&e, AnalogActive ? 16 : _held is null && _guideDown <= 0 ? 250 : 30)) Handle(ref e);
            long now = Environment.TickCount64;
            if (_held is { } h && now >= _nextRepeat) { _nextRepeat = now + 130; Action?.Invoke(h); }
            if (_guideDown > 0 && now - _guideDown >= 700) { _guideDown = -1; _guideTaps = 0; Action?.Invoke(NavAction.GuideHold); }
            if (AnalogActive && now >= _nextAnalog) { _nextAnalog = now + 16; Analog?.Invoke(_lx, _ly, _ry); }
            if (now >= _nextStatus) { _nextStatus = now + 10_000; UpdateStatus(); }
        }
        foreach (var p in _pads.Values) SDL_CloseGamepad((SDL_Gamepad*)p);
        SDL_Quit();
    }

    void Handle(ref SDL_Event e)
    {
        switch ((SDL_EventType)e.type)
        {
            case SDL_EventType.SDL_EVENT_GAMEPAD_ADDED:
                if (_pads.ContainsKey(e.gdevice.which)) break;
                var pad = SDL_OpenGamepad(e.gdevice.which);
                if (pad is null) break;
                if (SDL_GetGamepadVendor(pad) == SteamVirtualVendor && _pads.Count > 0)
                {
                    log.LogInformation("Ignoring Steam Input virtual controller (a physical controller is already open)");
                    SDL_CloseGamepad(pad);
                    break;
                }
                _pads[e.gdevice.which] = (IntPtr)pad;
                int player = 1;
                while (_players.ContainsValue(player)) player++;
                _players.Add(e.gdevice.which, player);
                SDL_SetGamepadPlayerIndex(pad, player - 1);
                log.LogInformation("Controller connected: {Name} ({Conn}) id={Serial}", SDL_GetGamepadName(pad), SDL_GetGamepadConnectionState(pad), SDL_GetGamepadSerial(pad));
                _nextStatus = Environment.TickCount64 + 1500;   // Bluetooth battery level arrives shortly after connect
                if (!_pads.ContainsKey(_activePad)) _activePad = e.gdevice.which;
                PadsChanged();
                break;
            case SDL_EventType.SDL_EVENT_GAMEPAD_REMOVED:
                if (_pads.Remove(e.gdevice.which, out var gone))
                {
                    _players.Remove(e.gdevice.which);
                    _lowBatteryReported.Remove(e.gdevice.which);
                    if (e.gdevice.which == _guidePad) { _guideDown = 0; _guideTaps = 0; }
                    var serial = SDL_GetGamepadSerial((SDL_Gamepad*)gone);
                    SDL_CloseGamepad((SDL_Gamepad*)gone);
                    log.LogInformation("Controller disconnected: id={Serial}", serial);
                    _held = null;
                    if (_activePad == e.gdevice.which) _activePad = _pads.Keys.FirstOrDefault();
                    PadsChanged();
                }
                break;
            case SDL_EventType.SDL_EVENT_GAMEPAD_BUTTON_DOWN:
                bool psPressed = (SDL_GamepadButton)e.gbutton.button == SDL_GamepadButton.SDL_GAMEPAD_BUTTON_GUIDE;
                bool switchingWithPs = psPressed && e.gbutton.which != _activePad;
                if (!Accept(e.gbutton.which, psPressed) || switchingWithPs) break;
                if (MapButton((SDL_GamepadButton)e.gbutton.button) is { } a)
                {
                    if (a == NavAction.Guide)   // tap vs hold decided later
                    {
                        _guideDown = Environment.TickCount64;
                        if (_guidePad != e.gbutton.which || _guideDown - _lastGuideTap > 1500)
                            _guideTaps = 0;
                        _guidePad = e.gbutton.which;
                        break;
                    }
                    Action?.Invoke(a);
                    if (a <= NavAction.Right) Hold(a);
                }
                break;
            case SDL_EventType.SDL_EVENT_GAMEPAD_BUTTON_UP:
                if ((SDL_GamepadButton)e.gbutton.button == SDL_GamepadButton.SDL_GAMEPAD_BUTTON_EAST && e.gbutton.which == _activePad)
                    Action?.Invoke(NavAction.BackReleased);
                if ((SDL_GamepadButton)e.gbutton.button == SDL_GamepadButton.SDL_GAMEPAD_BUTTON_GUIDE)
                {
                    if (e.gbutton.which != _guidePad) break;
                    if (_guideDown > 0)
                    {
                        _lastGuideTap = Environment.TickCount64;
                        _guideTaps = S.PsDoubleExtendsDisplay || S.PsTripleStopsExtending ? _guideTaps + 1 : 1;
                        if (_guideTaps == 1) Action?.Invoke(NavAction.Guide);
                        if (_guideTaps == 2) Action?.Invoke(NavAction.GuideSecondTap);
                        if (_guideTaps == 3)
                        {
                            _guideTaps = 0;
                            Action?.Invoke(NavAction.GuideTriple);
                        }
                    }
                    _guideDown = 0;
                    break;
                }
                if (e.gbutton.which == _activePad && MapButton((SDL_GamepadButton)e.gbutton.button) is { } up && up == _held) _held = null;
                break;
            case SDL_EventType.SDL_EVENT_GAMEPAD_TOUCHPAD_DOWN or SDL_EventType.SDL_EVENT_GAMEPAD_TOUCHPAD_MOTION or SDL_EventType.SDL_EVENT_GAMEPAD_TOUCHPAD_UP:
                if (e.gtouchpad.finger != 0 || !Accept(e.gtouchpad.which)) break;   // first finger moves the pointer
                if ((SDL_EventType)e.type == SDL_EventType.SDL_EVENT_GAMEPAD_TOUCHPAD_UP) { _tx = -1; break; }
                if (_tx >= 0 && (SDL_EventType)e.type == SDL_EventType.SDL_EVENT_GAMEPAD_TOUCHPAD_MOTION)
                    Touchpad?.Invoke(e.gtouchpad.x - _tx, e.gtouchpad.y - _ty);
                (_tx, _ty) = (e.gtouchpad.x, e.gtouchpad.y);
                break;
            case SDL_EventType.SDL_EVENT_GAMEPAD_AXIS_MOTION:
                if (Math.Abs((int)e.gaxis.value) < 8000 && e.gaxis.which != _activePad) break;   // idle noise from other pads
                if (!Accept(e.gaxis.which)) break;
                OnAxis((SDL_GamepadAxis)e.gaxis.axis, e.gaxis.value / 32767f);
                break;
        }
    }

    float _lx, _ly;
    NavAction? _stickDir;

    void OnAxis(SDL_GamepadAxis axis, float v)
    {
        if (axis == SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTX) _lx = v;
        else if (axis == SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTY) _ly = v;
        else if (axis == SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHTY) { _ry = v; return; }
        else return;
        if (!LeftStickNavigates) { _stickDir = null; _held = null; return; }
        float dz = Math.Clamp(S.StickDeadZone, 0.1f, 0.9f);
        NavAction? dir = Math.Max(Math.Abs(_lx), Math.Abs(_ly)) < dz ? null
            : Math.Abs(_lx) > Math.Abs(_ly) ? (_lx > 0 ? NavAction.Right : NavAction.Left)
            : (_ly > 0 ? NavAction.Down : NavAction.Up);
        if (dir == _stickDir) return;
        _stickDir = dir;
        if (dir is { } d) { Action?.Invoke(d); Hold(d); }
        else if (_held is not null) _held = null;
    }

    /// <summary>One active pad at a time: input from another pad within 500 ms is treated as a mirror
    /// (DSX/DS4Windows/Steam virtual controllers duplicating a physical DualSense) and ignored.</summary>
    bool Accept(SDL_JoystickID pad, bool psPressed = false)
    {
        if (!_players.TryGetValue(pad, out int player)) return false;
        long now = Environment.TickCount64;
        if (pad != _activePad && !psPressed && (now < _activeUntil || _guideDown != 0)) return false;
        bool takeover = pad != _activePad;
        _activePad = pad;
        _activeUntil = now + 500;
        if (takeover)
        {
            _held = _stickDir = null;
            _lx = _ly = _ry = 0;
            _tx = _ty = -1;
            _guideDown = _guideTaps = 0;
            log.LogInformation("Controller {Player} now controls Gaming Hub", player);
            if (_hubFocused && S.Vibration && _pads.TryGetValue(pad, out var p)) SDL_RumbleGamepad((SDL_Gamepad*)p, 0x2800, 0x2800, 90);
            UpdateStatus();
        }
        return true;
    }

    void PadsChanged()
    {
        if (_hubFocused) ApplyLights();
        UpdateStatus();
    }

    public void RefreshLights() => _work.Enqueue(ApplyLights);

    void ApplyLights()
    {
        if (!_hubFocused) return;
        foreach (var (id, p) in _pads)
        {
            var color = _players[id] == 2 ? S.PlayerTwoLightBar : S.ControllerOneLightBar;
            if (color == LightBarColor.Original) continue;
            var (r, g, b) = LightBarRgb(color);
            SDL_SetGamepadLED((SDL_Gamepad*)p, (byte)r, (byte)g, (byte)b);
        }
    }

    static (int, int, int) LightBarRgb(LightBarColor color) => color switch
        {
            LightBarColor.Cyan => (0, 190, 255),
            LightBarColor.Green => (25, 255, 65),
            LightBarColor.Amber => (255, 135, 10),
            LightBarColor.Red => (255, 20, 15),
            LightBarColor.Pink => (255, 50, 155),
            LightBarColor.Purple => (155, 40, 255),
            LightBarColor.White => (255, 255, 255),
            LightBarColor.Off => (0, 0, 0),
            _ => (30, 90, 255)
        };

    const float AnalogDeadZone = 0.18f;
    bool AnalogActive => Math.Abs(_ry) > AnalogDeadZone
        || (!LeftStickNavigates && Math.Max(Math.Abs(_lx), Math.Abs(_ly)) > AnalogDeadZone);

    void Hold(NavAction a) { _held = a; _nextRepeat = Environment.TickCount64 + 400; }

    static NavAction? MapButton(SDL_GamepadButton b) => b switch
    {
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_UP => NavAction.Up,
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_DOWN => NavAction.Down,
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_LEFT => NavAction.Left,
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_RIGHT => NavAction.Right,
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_SOUTH => NavAction.Accept,          // Cross
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_EAST => NavAction.Back,            // Circle
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_NORTH => NavAction.Context,        // Triangle
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_WEST => NavAction.Square,
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_SHOULDER => NavAction.TabPrev,
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_RIGHT_SHOULDER => NavAction.TabNext,
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_START => NavAction.Menu,           // Options
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_GUIDE => NavAction.Guide,          // PS
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_TOUCHPAD => NavAction.TouchClick,  // touchpad press = click
        SDL_GamepadButton.SDL_GAMEPAD_BUTTON_BACK => NavAction.Share,           // Create (Share): controls list
        _ => null,
    };

    void UpdateStatus()
    {
        var list = new List<PadState>();
        List<string>? warnings = null;
        foreach (var (id, ptr) in _pads)
        {
            var p = (SDL_Gamepad*)ptr;
            int pct;
            var power = SDL_GetGamepadPowerInfo(p, &pct);
            if (ShouldWarnLowBattery(id, power, pct))
                (warnings ??= []).Add($"P{_players[id]} battery low ({pct}%)");
            var conn = SDL_GetGamepadConnectionState(p) switch
            {
                SDL_JoystickConnectionState.SDL_JOYSTICK_CONNECTION_WIRED => "USB",
                SDL_JoystickConnectionState.SDL_JOYSTICK_CONNECTION_WIRELESS => "Bluetooth",
                _ => "connected",
            };
            list.Add(new PadState(_players[id], SDL_GetGamepadName(p) ?? "Controller", conn, pct,
                power is SDL_PowerState.SDL_POWERSTATE_CHARGING or SDL_PowerState.SDL_POWERSTATE_CHARGED, id == _activePad));
        }
        Pads = list;
        if (warnings is not null) LowBattery?.Invoke(string.Join(" · ", warnings));
        SetStatus(list.Count == 0 ? "No controller" : string.Join("   ", list.Select(x => x.Tooltip)));
    }

    bool ShouldWarnLowBattery(SDL_JoystickID id, SDL_PowerState power, int percent)
    {
        if (power is SDL_PowerState.SDL_POWERSTATE_CHARGING or SDL_PowerState.SDL_POWERSTATE_CHARGED || percent >= 25)
            _lowBatteryReported.Remove(id);
        return power == SDL_PowerState.SDL_POWERSTATE_ON_BATTERY && percent is >= 0 and <= 20 && _lowBatteryReported.Add(id);
    }

    void SetStatus(string s)
    {
        if (s == Status) return;
        Status = s;
        StatusChanged?.Invoke(s);
    }

    public void Dispose() { _stop = true; _thread?.Join(1000); }
}
