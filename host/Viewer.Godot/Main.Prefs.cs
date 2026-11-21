// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.IO;
using System.Linq;
using Godot;
using Viewer.Core.Diagnostics;
using Viewer.Shell;
using Viewer.Shell.Chat;
using Viewer.Shell.Social;

namespace Viewer.Godot;

/// <summary>
/// Putting the Preferences settings in force. The settings service raises <c>Changed</c> for a preview as well as for a saved value, so each
/// setting that applies live is applied here the moment it changes, and Cancel (which rolls the preview back) puts the saved value back
/// through the same path. A setting that needs a restart is read where the thing it controls is made: the language and the backend at
/// start, the cache when the content store is made. Display settings (frame-rate limit, vertical sync, window mode) are applied only in a
/// normal run, never in a test, render or check run, whose own guards read the engine's settings.
/// </summary>
public partial class Main
{
    private bool _interactive;
    private OnlineStatusSharing? _sharing;
    private string? _sharingAccount;

    /// <summary>Begins applying Preferences (a normal run only).</summary>
    private void StartPreferences(SettingsService settings)
    {
        _interactive = true;
        DisplayPlan.Migrate(settings);   // before the Changed handler: a value of an earlier version becomes the choice it stands for
        MouseLookSetting.Source = settings;
        StartFrameTimeLog(settings);
        settings.Changed += OnPreferenceChanged;
        ApplyDisplay(settings);
        ApplyStartupWindow(settings);
    }

    private DeviceReport? _deviceReport;
    private BackendDecision? _backendDecision;

    /// <summary>
    /// Chooses the rendering backend and starts the viewer again once when it is not the one running. The choice is the saved one (or --backend=
    /// for this run); each backend is held to the hardware floor first, and a choice that fails it goes to the other backend, with one log line
    /// and no question. True when a second start was made (this copy then quits). Development builds read a made-up device report from
    /// NV_TEST_DEVICE_REPORT (for example "vulkan=1.2.175;d3d12=12_0").
    /// </summary>
    private bool RestartForBackend(SettingsService settings)
    {
        string running = RenderingServer.GetCurrentRenderingDriverName();
        var user = OS.GetCmdlineUserArgs();
        string requested = BackendChoice.Requested(settings.GetText(Settings.GraphicsBackendKey), user);
        var made = OS.IsDebugBuild() ? DeviceReport.Parse(System.Environment.GetEnvironmentVariable("NV_TEST_DEVICE_REPORT")) : null;
        bool fake = made is not null;
        _deviceReport = made ?? DeviceProbe.Read();
        string? target = requested is "vulkan" or "d3d12" ? requested : null;
        if (_deviceReport is { } report)
        {
            _backendDecision = BackendChoice.Decide(requested, report);
            if (BackendRelaunch.Tried(user).Count == 0)   // said once: the second start does not repeat it
            {
                if (_backendDecision.FellBack || _backendDecision.Driver is null)
                    Log.Warn("backend.choice", CoreLog.F("detail", _backendDecision.LogLine));
                else
                    Log.Info("backend.choice", CoreLog.F("detail", _backendDecision.LogLine));
            }
            target = _backendDecision.Driver;   // null: neither passes by the probe; the engine's own check has the last word
            if (target is null && fake)
            {
                // a made-up report is the whole truth for a development run: show the plain message now
                Console.Error.WriteLine(_strings!.Get(_backendDecision.MessageKey!, _backendDecision.MessageArgs));
                Log.Warn("startup.refused", CoreLog.F("verdict", "NeitherBackend"), CoreLog.F("exit", RenderFloor.ExitBelowFloor));
                GetTree().Quit(RenderFloor.ExitBelowFloor);
                return true;
            }
        }
        GraphicsBackendInfo.Status = BackendStatus.From(requested, running, BackendRelaunch.Tried(user), _backendDecision);
        return Relaunch(target ?? "", running);
    }

    /// <summary>Starts again with <paramref name="target"/> unless the plan says not to. True when it did.</summary>
    private bool Relaunch(string target, string running)
    {
        // a driver named on the command line (--rendering-driver) always wins; the engine does not list it among the arguments it hands over, so the
        // process's own command line is read too
        var engine = OS.GetCmdlineArgs().ToList();
        if (System.Environment.GetCommandLineArgs().TakeWhile(a => a != "--").Any(a => a == "--rendering-driver" || a.StartsWith("--rendering-driver=", StringComparison.Ordinal)) && !engine.Contains("--rendering-driver"))
            engine.Add("--rendering-driver");
        var plan = BackendRelaunch.Plan(target, running, engine, OS.GetCmdlineUserArgs());
        if (plan is null)
            return false;
        long pid = OS.CreateProcess(OS.GetExecutablePath(), plan.ToArray());
        if (pid <= 0)
        {
            Log.Warn("backend.restart-failed", CoreLog.F("running", running));
            return false;
        }
        Log.Info("backend.restart", CoreLog.F("from", running), CoreLog.F("to", target));
        GetTree().Quit(0);
        return true;
    }

    /// <summary>The engine's own check found the running backend below the floor although the probe expected it to pass (or the probe could not
    /// say): go to the other backend once if the probe says it passes. True when a second start was made.</summary>
    private bool FallBackAfterRefusal(StartupDecision decision)
    {
        if (decision.Verdict is not (StartupVerdict.BelowFloor or StartupVerdict.UnknownVersion) || _deviceReport is not { } report)
            return false;
        string running = decision.Backend, other = BackendChoice.Other(running);
        if (BackendChoice.Decide(other, report).Driver != other)
            return false;
        Log.Warn("backend.choice", CoreLog.F("detail", $"backend.fallback: the engine found {running} below the floor ({decision.ApiVersion}), using {other}"));
        return Relaunch(other, running);
    }

    /// <summary>The content store, made where and as large as Preferences says; a folder that cannot be used is logged and the default one used.</summary>
    private Viewer.Core.Assets.ContentStore MakeContentStore()
    {
        var choice = ContentCacheSetting.Resolve(UserSettings(null));
        try
        {
            return new Viewer.Core.Assets.ContentStore(new() { Log = Log, Root = choice.Root, CapBytes = choice.CapBytes });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Log.Warn("cache.folder-unusable", CoreLog.F("reason", ex.GetType().Name));
            return new Viewer.Core.Assets.ContentStore(new() { Log = Log, CapBytes = choice.CapBytes });
        }
    }

    /// <summary>Where a login starts, from Preferences: the person's home or the last location.</summary>
    private static string StartLocationChoice(SettingsService settings) =>
        settings.GetText(Settings.LoginStartLocationKey) == "home" ? "home" : "last";

    /// <summary>Empties the content store. The Cache page asks first; the count is of entries removed.</summary>
    internal int ClearContentCache()
    {
        int removed = ContentStore().Clear();
        Log.Info("cache.cleared", CoreLog.F("entries", removed));
        return removed;
    }

    /// <summary>Forgets the saved login names (the Privacy page asks first).</summary>
    internal bool ClearSavedLoginNames()
    {
        bool had = LoginNames.Clear(UserSettings(null));
        had |= _savedNames?.ClearAll() ?? false;   // and the names saved for each grid
        Log.Info("login.names-cleared", CoreLog.F("had", had));
        return had;
    }

    private void OnPreferenceChanged(string key)
    {
        var s = UserSettings(null);
        if (key == Settings.DrawDistance.Key) { ApplyDrawDistance(s); if (Model is { } dm) dm.DrawDistance = s.Get(Settings.DrawDistance); }
        else if (key == Settings.MaxTextureResolution.Key) ApplyTextureCap(s);
        else if (key == Settings.CloudsEnabled.Key) ApplyClouds(s);
        else if (key == Viewer.Shell.Sound.AudioLevels.Mono.Key) ApplyMono(s);
        else if (key == Settings.FpsLimit.Key || key == Settings.Vsync.Key) ApplyDisplay(s);
        else if (key == Settings.WindowModeKey) ApplyWindowChoice(s);
        else if (key == Settings.DoubleTapRun.Key || key == Settings.AutomaticFly.Key) ApplyMovement(s);
        else if (key == Settings.ArrowKeysAlwaysMove.Key) ApplyArrowKeys(s);
        else if (key == Settings.CameraSmoothing.Key) _follow.Smoothing = (float)s.Get(Settings.CameraSmoothing);
        else if (key == Settings.DisableCameraConstraints.Key) _follow.ConstraintsOff = s.Get(Settings.DisableCameraConstraints);
        else if (key is "mouse_sensitivity" or "invert_mouse" or "enable_mouselook" or "show_crosshairs" or "mouse_smooth") ApplyMouseLook();
        else if (key == Settings.LoginStartLocationKey) { if (Model is { } m) m.StartLocation = StartLocationChoice(s); }
        else if (key == Settings.NeighbourRegions.Key) { if (Model is { } m) m.Neighbours = s.Get(Settings.NeighbourRegions); }   // read at the next login
        else if (key.StartsWith("share_online_status_", StringComparison.Ordinal)) ApplySharing();
    }

    /// <summary>Puts every live setting in force: a world was just shown.</summary>
    private void ApplyAllPreferences()
    {
        var s = UserSettings(null);
        ApplyDrawDistance(s);
        ApplyTextureCap(s);
        ApplyMovement(s);
        ApplyArrowKeys(s);
        ApplyMouseLook();
        _follow.Smoothing = (float)s.Get(Settings.CameraSmoothing);
        _follow.ConstraintsOff = s.Get(Settings.DisableCameraConstraints);
        ApplySharing();
    }

    /// <summary>The draw distance goes to the region (the agent update's far distance, stepped as the official viewer does), to the camera's far
    /// plane (terrain, water and everything else end there, as in the official viewer) and to the presentation (objects beyond it are not built).</summary>
    private void ApplyDrawDistance(SettingsService s)
    {
        int metres = s.Get(Settings.DrawDistance);
        _presented?.SetDrawDistance(metres);
        if (_camera is not null)
            _camera.Far = DrawDistanceView.FarPlane(metres, _camera.Far);
        if (_presenter?.Prims is { } prims)
            prims.Hub.DrawDistance = metres;
    }

    private void ApplyTextureCap(SettingsService s)
    {
        if (_presenter?.Prims?.Residency is { } residency)
            residency.MaxTextureResolution = s.Get(Settings.MaxTextureResolution);
    }

    private void ApplyClouds(SettingsService s)
    {
        if (_presenter?.Environment is { } environment)
            environment.CloudsEnabled = s.Get(Settings.CloudsEnabled);
    }

    private void ApplyMovement(SettingsService s)
    {
        if (_driver is null)
            return;
        _driver.Input.AllowDoubleTapRun = s.Get(Settings.DoubleTapRun);
        _driver.Input.AutomaticFly = s.Get(Settings.AutomaticFly);
    }

    private void ApplyArrowKeys(SettingsService s)
    {
        if (_shell is not null)
            _shell.Shell.Focus.ArrowKeysAlwaysMove = s.Get(Settings.ArrowKeysAlwaysMove);
    }

    private void ApplyMouseLook()
    {
        _mouseLook.Allowed = MouseLookSetting.Enabled();
        if (!_mouseLook.Allowed)
            _mouseLook.Leave();
        if (_driver is not null)
            MouseLookSetting.Apply(_driver.Input);
        if (_crosshair is not null)
            _crosshair.Visible = _mouseLook.Active && MouseLookSetting.ShowCrosshairs();
    }

    private static readonly string[] DisplayFlags = ["--disable-vsync", "--fixed-fps", "--max-fps", "--fullscreen", "--windowed", "--maximized", "--position", "--resolution"];

    /// <summary>Whether the command line already chose how the window is shown (an engine flag, or a harness placing the window).</summary>
    private static bool DisplayChosenOnCommandLine() =>
        TestWindow.FullSize(OS.GetCmdlineUserArgs()) is not null
        || OS.GetCmdlineArgs().Any(a => DisplayFlags.Any(f => a == f || a.StartsWith(f + "=", StringComparison.Ordinal)));

    /// <summary>Frame-rate limit and vertical sync, in a normal run: only what differs from the window as it is, and nothing when
    /// the command line chose the display itself. The window's own mode is <see cref="ApplyStartupWindow"/> and <see cref="ApplyWindowChoice"/>.</summary>
    private void ApplyDisplay(SettingsService s)
    {
        if (!_interactive || DisplayChosenOnCommandLine())
            return;
        int fps = s.Get(Settings.FpsLimit);
        if (Engine.MaxFps != fps)
            Engine.MaxFps = fps;
        var vsync = s.Get(Settings.Vsync) ? DisplayServer.VSyncMode.Enabled : DisplayServer.VSyncMode.Disabled;
        if (DisplayServer.WindowGetVsyncMode() != vsync)
            DisplayServer.WindowSetVsyncMode(vsync);
    }

    /// <summary>A session's friend list is now watched so that "show my online status to friends" holds for every friend, also those who arrive later.</summary>
    private void AttachSharing(CommsHub hub, string account)
    {
        _sharingAccount = account;
        _sharing = new OnlineStatusSharing(hub.Friends, UserSettings(null), account);
        hub.Friends.Changed += ApplySharing;
    }

    private void ApplySharing()
    {
        if (_sharing is null || _sharingAccount is null)
            return;
        _sharing.Apply(UserSettings(null).Get(Settings.ShareOnlineStatusOf(_sharingAccount)));
    }

#if NV_AUTOMATION
    /// <summary>The preferences command (development builds only): set a value or choose a preset as the window's controls do (a preview),
    /// save, cancel or reset a page, and report what the window holds and what is in force. Answers values, never text.</summary>
    public object? PrefsCall(string action, string? key, System.Text.Json.JsonElement value, string? page)
    {
        var model = _preferences?.Model;
        if (model is null)
            return null;
        bool ok = true;
        string reason = "";
        PrefPage? target = Enum.TryParse<PrefPage>(page, ignoreCase: true, out var parsed) ? parsed : null;
        switch (action)
        {
            case "set":
            {
                var def = PreferenceCatalog.Find(key ?? "");
                object? v = def is null ? null : def.Kind switch
                {
                    PrefKind.Switch => value.ValueKind is System.Text.Json.JsonValueKind.True ? true : value.ValueKind is System.Text.Json.JsonValueKind.False ? false : null,
                    PrefKind.Whole => value.ValueKind == System.Text.Json.JsonValueKind.Number && value.TryGetInt32(out var i) ? i : null,
                    PrefKind.Real => value.ValueKind == System.Text.Json.JsonValueKind.Number && value.TryGetDouble(out var d) ? d : null,
                    _ => value.ValueKind == System.Text.Json.JsonValueKind.String ? value.GetString() : null,
                };
                ok = def is not null && v is not null && model.Edit(def, v);
                if (!ok) reason = def is null ? "no such setting" : v is null ? "the value does not fit the setting" : "the setting refused the value";
                break;
            }
            case "preset": ok = model.SelectPreset(key ?? ""); break;
            case "save": ok = target is { } s && model.Save(s); break;
            case "cancel": if (target is { } c) model.Cancel(c); else ok = false; break;
            case "reset": if (target is { } r) model.Reset(r); else ok = false; break;
        }
        var set = UserSettings(null);
        return new
        {
            ok,
            reason,
            preset = model.Preset,
            pending = set.PendingKeys.OrderBy(k => k).ToArray(),
            held = new
            {
                draw_distance = set.Get(Settings.DrawDistance),
                max_texture_resolution = set.Get(Settings.MaxTextureResolution),
                clouds = set.Get(Settings.CloudsEnabled),
                chat_font_size = set.Get(Settings.ChatFontSize),
                chat_overlay_seconds = set.Get(Settings.ChatOverlaySeconds),
                share_online_status = _sharingAccount is { } a ? set.Get(Settings.ShareOnlineStatusOf(a)) : (bool?)null,
            },
            in_force = new
            {
                draw_distance = _presented?.DrawDistance,
                camera_far = _camera?.Far,
                max_texture_resolution = _presenter?.Prims?.Residency?.MaxTextureResolution,
                clouds = _presenter?.Environment?.CloudsEnabled,
                max_fps = Engine.MaxFps,
                vsync = DisplayServer.WindowGetVsyncMode().ToString(),
                window_mode = GetWindow().Mode.ToString(),
                double_tap_run = _driver?.Input.AllowDoubleTapRun,
                automatic_fly = _driver?.Input.AutomaticFly,
                camera_smoothing = _follow.Smoothing,
                disable_camera_constraints = _follow.ConstraintsOff,
                overlay_lines = _nearbyLines?.ShownCount,
                overlay_font_px = _nearbyLines?.GetThemeFontSize("normal_font_size"),
                world_objects = Model?.Session?.World.Snapshot().Objects.Count,
            },
        };
    }

    private Vector2 _injectedAt;
    private bool _injectedLeftDown;

    /// <summary>The input command (development builds only): sends a key or a mouse event through the engine's own input path, the way the
    /// operating system's arrives (the panels first, then the world), with the modifier keys held, and reports what the controls did: the
    /// avatar's heading, the keys held, the steering state and which panel has the keyboard. Answers values, never text.</summary>
    /// <summary>Types the text into whatever has the keyboard, one key press and release per character through the engine's key input, as
    /// the operating system's key events carry them (letters and digits with their key, every character with its text). Answers only how
    /// many characters were typed.</summary>
    public object TypeText(string text)
    {
        foreach (char c in text)
        {
            Key code = c switch
            {
                >= 'a' and <= 'z' => (Key)char.ToUpperInvariant(c),
                >= 'A' and <= 'Z' or >= '0' and <= '9' => (Key)c,
                ' ' => Key.Space,
                '\n' => Key.Enter,   // a new line is the Enter key, which carries no character
                _ => Key.None,
            };
            bool shift = c is >= 'A' and <= 'Z';
            global::Godot.Input.ParseInputEvent(new InputEventKey { Keycode = code, PhysicalKeycode = code, Unicode = c == '\n' ? 0 : c, Pressed = true, ShiftPressed = shift });
            global::Godot.Input.ParseInputEvent(new InputEventKey { Keycode = code, PhysicalKeycode = code, Unicode = 0, Pressed = false, ShiftPressed = shift });
        }
        return new { typed = text.Length };
    }

    public object? InputCall(string kind, string? key, string? button, bool down, bool shift, bool ctrl, bool alt, float x, float y, bool doubleClick = false)
    {
        switch (kind)
        {
            case "key" when key is not null:
            {
                var code = key.ToUpperInvariant() switch
                {
                    "LEFT" => Key.Left, "RIGHT" => Key.Right, "UP" => Key.Up, "DOWN" => Key.Down, "PGUP" => Key.Pageup, "PGDN" => Key.Pagedown,
                    "HOME" => Key.Home, "END" => Key.End, "TAB" => Key.Tab, "DELETE" => Key.Delete, "SPACE" => Key.Space, "ENTER" => Key.Enter, "ESC" => Key.Escape, "BACKSPACE" => Key.Backspace,
                    "." => Key.Period, "," => Key.Comma, "-" => Key.Minus,
                    { Length: 1 } letter when letter[0] is >= 'A' and <= 'Z' or >= '0' and <= '9' => (Key)letter[0],
                    _ => Key.None,
                };
                if (code == Key.None) return null;
                // a letter, digit, full stop, comma or minus without Ctrl or Alt carries its character, as the operating system's key event does
                // (a text field types it)
                char typed = !down || ctrl || alt || code is not (>= Key.A and <= Key.Z or >= Key.Key0 and <= Key.Key9 or Key.Period or Key.Comma or Key.Minus) ? '\0'
                    : code is >= Key.A and <= Key.Z && !shift ? char.ToLowerInvariant((char)(long)code) : (char)(long)code;
                global::Godot.Input.ParseInputEvent(new InputEventKey { Keycode = code, PhysicalKeycode = code, Unicode = typed, Pressed = down, ShiftPressed = shift, CtrlPressed = ctrl, AltPressed = alt });
                break;
            }
            case "mouse":
            {
                var win = GetWindow().Size; var vis = GetViewport().GetVisibleRect().Size;
                var at = new Vector2(x * win.X / vis.X, y * win.Y / vis.Y);   // the script gives viewport coordinates; an operating-system event carries window pixels
                var index = button switch { "left" => MouseButton.Left, "right" => MouseButton.Right, "middle" => MouseButton.Middle, _ => MouseButton.None };
                // while the engine drags it places the drop from the system cursor, which an injected pointer is not: the inventory tree is
                // told where the injected pointer is (as the S-1 pointer check tells it), up to and including the release that drops
                if (_inventoryPanel?.TreeView is { } dragTree)
                    dragTree.HoldDragAt(GetViewport().GuiIsDragging() || (_injectedLeftDown && index == MouseButton.None)
                        ? dragTree.GetGlobalTransformWithCanvas().AffineInverse() * new Vector2(x, y) : null);   // the tree's transform is in viewport coordinates, the script's point
                if (index == MouseButton.None)
                {
                    var mask = (MouseButtonMask)(_injectedLeftDown ? 1 : 0);   // the buttons held, as the operating system's motion event carries them
                    global::Godot.Input.ParseInputEvent(new InputEventMouseMotion { Position = at, GlobalPosition = at, Relative = at - _injectedAt, ButtonMask = mask, ShiftPressed = shift, CtrlPressed = ctrl, AltPressed = alt });
                }
                else
                {
                    if (index == MouseButton.Left) _injectedLeftDown = down;
                    global::Godot.Input.ParseInputEvent(new InputEventMouseButton { Position = at, GlobalPosition = at, ButtonIndex = index, Pressed = down, ShiftPressed = shift, CtrlPressed = ctrl, AltPressed = alt, ButtonMask = down ? (MouseButtonMask)1 : 0, DoubleClick = doubleClick && down });
                }
                _injectedAt = at;
                break;
            }
            case "focus_button":   // a button of the panels takes the keyboard, as a click on it would leave it
            {
                static BaseButton? FirstButton(Node n)
                {
                    foreach (var child in n.GetChildren())
                    {
                        if (child is BaseButton { FocusMode: not Control.FocusModeEnum.None } b && b.IsVisibleInTree()) return b;
                        if (FirstButton(child) is { } found) return found;
                    }
                    return null;
                }
                FirstButton(GetTree().Root)?.GrabFocus();
                break;
            }
            case "release_focus":   // nothing holds the keyboard, as after a click on the world
                GetViewport().GuiReleaseFocus();
                break;
            case "state":
                break;
            case "buttons":
            {
                // the visible buttons of the interface with where they are, so a script can click one the way a person does
                var found = new List<object>();
                void Walk(Node n, string path)
                {
                    foreach (var child in n.GetChildren())
                    {
                        string here = path + "/" + child.Name;
                        if (child is Control { Visible: true, FocusMode: not Control.FocusModeEnum.None } b && b.IsVisibleInTree())
                        {
                            var rect = b.GetGlobalRect();
                            found.Add(new { path = here, type = b.GetType().Name, text = (b as Button)?.Text ?? "", x = rect.GetCenter().X, y = rect.GetCenter().Y, w = rect.Size.X, h = rect.Size.Y, focus = b.FocusMode.ToString() });
                        }
                        if (found.Count < 80) Walk(child, here);
                    }
                }
                Walk(GetTree().Root, "");
                return new { buttons = found };
            }
            default:
                return null;
        }
        return new
        {
            view = new[] { GetViewport().GetVisibleRect().Size.X, GetViewport().GetVisibleRect().Size.Y },
            window = new[] { GetWindow().Size.X, GetWindow().Size.Y },
            heading_deg = _driver?.Input.Heading is float h ? System.Math.Round(h * 180 / System.Math.PI, 3) : (double?)null,
            keys_held = _keysDown.Keys.ToArray(),
            camera_keys_held = _cameraKeysDown.Values.Select(k => k.ToString()).ToArray(),
            steer_pressed = _steer.Pressed,
            steering = _steer.Steering,
            camera_tool = _cameraTool,
            focus_owner = GetViewport().GuiGetFocusOwner()?.GetType().Name,
            hovered = GetViewport().GuiGetHoveredControl()?.GetPath().ToString(),
            mouse_at = new[] { GetViewport().GetMousePosition().X, GetViewport().GetMousePosition().Y },
            focus_on_avatar = _follow.FocusOnAvatar,
            sitting = _interact?.Sitting,
        };
    }

    /// <summary>The typing command (development builds only): gives the chat bar's text field the keyboard, sets its text and caret, sends an arrow key
    /// through the engine's own input path (the way a pressed key arrives, to whatever has the keyboard) and reports where the caret is, whether the
    /// avatar's movement keys are allowed and which are held. Answers values, never text.</summary>
    public object? TypingCall(string action, string? text, int caret, string? key, bool down)
    {
        if (_shell is null)
            return null;
        var input = _shell.Chat.Input;
        switch (action)
        {
            case "focus":
                GetWindow().MoveToForeground();
                GetWindow().GrabFocus();
                input.GrabFocus();
                break;
            case "release": input.ReleaseFocus(); break;
            case "text":
                input.Text = text ?? "";
                input.CaretColumn = caret < 0 ? input.Text.Length : System.Math.Min(caret, input.Text.Length);
                input.EmitSignal(LineEdit.SignalName.TextChanged, input.Text);   // setting the text in code does not say it changed, as typing does
                break;
            case "key":
            {
                var code = key?.ToUpperInvariant() switch { "LEFT" => Key.Left, "RIGHT" => Key.Right, "UP" => Key.Up, "DOWN" => Key.Down, _ => Key.None };
                if (code != Key.None)
                    global::Godot.Input.ParseInputEvent(new InputEventKey { Keycode = code, PhysicalKeycode = code, Pressed = down });
                break;
            }
        }
        var focus = _shell.Shell.Focus;
        return new
        {
            text_entry = focus.TextEntryActive,
            movement_allowed = focus.MovementAllowedFor(arrowKey: true),
            window_focused = focus.WindowFocused,
            arrow_keys_always_move = focus.ArrowKeysAlwaysMove,
            caret = input.CaretColumn,
            length = input.Text.Length,
            keys_held = _keysDown.Keys.ToArray(),
            chat_bar_visible = _shell.Chat.Visible,
        };
    }
#endif

    private readonly Viewer.Motion.ArrowKeyTrace _arrowTrace = new();

    /// <summary>Records an arrow key going down (once per press, not for the repeats of a held key): the control that holds the keyboard now and the
    /// avatar's state. <see cref="MoveKey"/> marks it when the avatar's controls take the key; <see cref="FlushArrowTrace"/> logs it.</summary>
    private void TraceArrowKey(InputEventKey k)
    {
        var owner = GetViewport().GuiGetFocusOwner();
        var pose = Viewer.Motion.ArrowKeyTrace.PoseOf(_interact is { Sitting: true }, _driver?.Input.Flying ?? false);
        _arrowTrace.Press(KeyName(k.Keycode) ?? k.Keycode.ToString().ToUpperInvariant(), k.Echo, owner?.GetType().Name ?? "none", _shell?.Shell.Focus.TextEntryActive ?? false, pose, _mouseLook.Active);
    }

    /// <summary>Logs the arrow presses recorded since the last frame, after the frame's input has been handled.</summary>
    private void FlushArrowTrace()
    {
        while (_arrowTrace.Take() is { } r)
            Log.Info("input.arrow", CoreLog.F("key", r.Key), CoreLog.F("focus", r.FocusOwner), CoreLog.F("text-entry", r.TextEntry),
                CoreLog.F("world-received", r.WorldReceived), CoreLog.F("avatar", r.Pose.ToString().ToLowerInvariant()), CoreLog.F("mouselook", r.Mouselook));
    }

    /// <summary>Arrow keys keep moving the avatar while the chat bar has the keyboard, when Preferences says so: the key is taken before the text field sees it.</summary>
    public override void _Input(InputEvent e)
    {
        if (_shell is null || _driver is null || e is not InputEventKey k)
            return;
        var focus = _shell.Shell.Focus;
        bool arrow = k.Keycode is Key.Left or Key.Right or Key.Up or Key.Down;
        if (arrow && k.Pressed) TraceArrowKey(k);
        if (!arrow)
            return;
        // the engine's focus navigation moves the focus to a neighbouring control on an arrow key and swallows the key; a control with no
        // use for arrows (a button, a panel left holding the keyboard after a window closed) would take them from the avatar and the camera
        var owner = GetViewport().GuiGetFocusOwner();
        var kind = owner switch
        {
            null => ArrowFocusKind.None,
            BaseButton => ArrowFocusKind.Button,
            LineEdit or TextEdit => ArrowFocusKind.TextField,
            Tree or ItemList or Slider or SpinBox or TabBar or ScrollBar => ArrowFocusKind.List,
            _ => ArrowFocusKind.Other,
        };
        if (!ArrowKeyRouting.TakenByAvatar(kind, k.AltPressed, focus.TextEntryActive, focus.ArrowKeysAlwaysMove, focus.MovementAllowedFor(arrowKey: true)))
            return;
        bool typing = kind == ArrowFocusKind.TextField;
        if (k.Echo || (KeyName(k.Keycode) is { } name && MoveKey(name, k.Pressed, k.ShiftPressed, synthetic: typing, control: k.CtrlPressed)))
            GetViewport().SetInputAsHandled();
    }
}
