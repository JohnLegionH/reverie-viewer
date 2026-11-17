// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Linq;
using Godot;
using Viewer.Core.Session;
using Viewer.Shell;
using Viewer.Shell.Sound;

namespace Viewer.Godot;

/// <summary>
/// The interface sounds in the running viewer: the service and its engine player are made with the shell, the controls' clicks and the
/// windows are connected at once, and each session's notifications, chat and travel models are connected when they exist. The grid's
/// sounds are fetched through the session's asset capability, with the content cache in front, and only while a session is in the world.
/// </summary>
public partial class Main
{
    private UiSounds? _uiSounds;
    private UiSoundPlayer? _uiSoundPlayer;
    private Viewer.Core.Assets.ISoundTransport? _soundTransport;
    private IDisposable? _soundNotes, _soundChat, _soundTravel;
    private object? _soundNotesFor, _soundChatFor, _soundTravelFor;
    private ViewerSession? _soundGridFor;
    private bool _soundDimHooked;
    private SoundTunerPanel? _soundTuner;

    /// <summary>The interface sounds service (null until the shell is made).</summary>
    public UiSounds? UiSounds => _uiSounds;

    private AudioEffectStereoEnhance? _monoEffect;

    /// <summary>Mono audio: a stereo spread effect on the master bus, at 0 (both channels folded into one mix) when the setting is on and
    /// 1 (stereo as it is) when off, so every sound the viewer plays follows it.</summary>
    private void ApplyMono(SettingsService settings)
    {
        float spread = Viewer.Shell.Sound.AudioLevels.StereoSpread(settings);
        if (_monoEffect is null)
        {
            if (spread >= 1f)
                return;   // nothing to fold until the setting is first switched on
            _monoEffect = new AudioEffectStereoEnhance();
            AudioServer.AddBusEffect(0, _monoEffect);
        }
        _monoEffect.PanPullout = spread;
        Log.Info("audio.mono", Viewer.Core.Diagnostics.CoreLog.F("on", spread < 1f));
    }

    private void InstallSounds(SettingsService settings)
    {
        if (_shell is null)
            return;
        InstallWorldSounds(settings);
        ApplyMono(settings);
        _uiSoundPlayer = new UiSoundPlayer(settings) { Tuning = _tuning };
        AddChild(_uiSoundPlayer);
        _uiSounds = new UiSounds(settings, _uiSoundPlayer, null,
            () => DisplayServer.WindowGetMode() == DisplayServer.WindowMode.Minimized, () => Time.GetTicksMsec() / 1000.0);
        AddChild(new UiSoundClicks(() => _uiSounds));
        UiSoundWiring.Floaters(_shell.Shell.Floaters, _uiSounds);   // for the viewer's whole life
        if (_loginScreen is not null)
            _loginScreen.Dialogs.Shown += d => _uiSounds?.Play(UiSoundRules.LoginDialog(d.Kind));
        // the Sound Tuner: a window of its own, opened from Preferences > Sound > Tune; it plays our file for the slot (tuned or shipped)
        var shell = _shell.Shell;
        var tuner = new SoundTunerPanel(shell.Strings, _tuning!, file =>
        {
            if (UiSoundCatalog.All.FirstOrDefault(i => i.File == file) is { } info)
                _uiSoundPlayer?.PlayOurs(info.Slot, _uiSounds?.GainOf(info.Slot) ?? 0.5f);
            else
                _uiSoundPlayer?.PlayFile(file, 0.5f);
        });
        _shell.AddFloater(new FloaterSpec(SoundTunerPanel.FloaterId, "tuner.title", new UiSize(560, 600), new UiSize(400, 320)), tuner);
        _soundTuner = tuner;
        if (_preferences is not null)
        {
            _preferences.Sounds = _uiSounds;
            _preferences.OpenTuner = () => _shell?.Shell.Floaters.Open(SoundTunerPanel.FloaterId);
        }
    }

    /// <summary>Every frame: follows the session (in the world or not, its grid sounds, its models) and applies finished grid fetches.</summary>
    private void UpdateSounds()
    {
        if (_uiSounds is not { } sounds)
            return;
        var session = Model?.Session;
        bool inWorld = session is not null && !session.IsEnded && session.AgentId != Guid.Empty;
        if (sounds.InWorld != inWorld)
            sounds.InWorld = inWorld;
        // the per-grid switch: on Second Life the interface plays the grid's own sounds by their ids
        var gridKind = inWorld ? session!.Grid : Viewer.Core.Session.GridKind.OpenSim;
        if (sounds.Grid != gridKind)
            sounds.Grid = gridKind;
        if (_soundGridFor != (inWorld ? session : null))
        {
            _soundGridFor = inWorld ? session : null;
            var transport = _soundTransport;
            var fetch = transport is null ? null
                : _contentView is { } view ? view.Caching("sound", transport.FetchSoundAsync) : transport.FetchSoundAsync;
            sounds.UseGrid(_soundGridFor is not null && fetch is not null ? new GridSounds(fetch) : null);
            sounds.Preload();
        }
        Rewire(ref _soundNotes, ref _soundNotesFor, _notes, () => UiSoundWiring.Notifications(_notes!.Model, _travel?.Notices, sounds));
        Rewire(ref _soundChat, ref _soundChatFor, _comms, () => UiSoundWiring.Chat(_comms!.Nearby, _comms.Ims, sounds));
        Rewire(ref _soundTravel, ref _soundTravelFor, _travel, () => UiSoundWiring.Travel(_travel!.Teleport, _travel.WorldMap, sounds));
        if (!_soundDimHooked && _notePanels is { } panels)
        {
            // a click beside a modal notification, on the dimmed view behind it, is refused as the official modal dialogs refuse it
            _soundDimHooked = true;
            panels.Toasts.Dim.GuiInput += e =>
            {
                if (e is InputEventMouseButton { Pressed: true })
                    _uiSounds?.Play(UiSoundSlot.InvalidOp);
            };
        }
        sounds.Pump();
        UpdateWorldSounds();
    }

    // connects the models of a new hub (or none) once, and drops the last hub's connections
    private static void Rewire(ref IDisposable? wiring, ref object? wiredFor, object? hub, Func<IDisposable> connect)
    {
        if (ReferenceEquals(wiredFor, hub))
            return;
        wiring?.Dispose();
        wiring = hub is null ? null : connect();
        wiredFor = hub;
    }

#if NV_AUTOMATION
    /// <summary>The automation's ui_sounds command: the decisions so far, clearing them, playing a slot as its Play button does, where a
    /// control is (to press it through the input path), and closing a window. Answers values, never sound bytes.</summary>
    public object? UiSoundsCall(string action, string? slot, string? name)
    {
        if (_uiSounds is not { } sounds)
            return null;
        switch (action)
        {
            case "log":
                return new
                {
                    in_world = sounds.InWorld,
                    started = _uiSoundPlayer?.Started ?? 0,
                    events = sounds.Recent.Select(e => new { slot = e.Slot.ToString(), outcome = e.Outcome.ToString() }).ToArray(),
                };
            case "clear":
                sounds.ClearRecent();
                return new { cleared = true };
            case "play" when Enum.TryParse<UiSoundSlot>(slot, out var s):
                return new { slot = s.ToString(), outcome = sounds.Audition(s).ToString() };
            case "state":
                return new
                {
                    in_world = sounds.InWorld,
                    slots = UiSoundCatalog.All.Select(i => new
                    {
                        slot = i.Slot.ToString(), source = UiSoundSettings.IdOf(UiSoundSettings.SourceOn(UserSettings(null), i.Slot, sounds.Grid)),
                        status = sounds.StatusOf(i.Slot).ToString(),
                    }).ToArray(),
                };
            case "find" when name is not null:
            {
                var control = FindControl(GetTree().Root, name);
                if (control is null)
                    return new { found = false };
                var centre = control.GetGlobalRect().GetCenter();
                return new { found = true, x = centre.X, y = centre.Y, visible = control.IsVisibleInTree() };
            }
            case "close_window" when name is not null && _shell is not null:
                return new { closed = _shell.Shell.Floaters.Close(name), open = _shell.Shell.Floaters.IsOpen(name) };
            default:
                return new { error = "action must be log, clear, play (slot), state, find (name) or close_window (name)" };
        }

        static Control? FindControl(Node n, string wanted)
        {
            foreach (var child in n.GetChildren())
            {
                if (child is Control c && c.Name == wanted && c.IsVisibleInTree())
                    return c;
                if (FindControl(child, wanted) is { } found)
                    return found;
            }
            return null;
        }
    }
#endif
}
