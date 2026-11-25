// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Addons;

namespace SoundSample;

/// <summary>
/// A sample add-on that plays its own sound (interface 0.6): a menu item opens a window with buttons that play tone.wav around the
/// person as a loop, play it as a loop from a place 5 m north of the avatar, play it once after 3 seconds with a low-pass, and stop
/// everything. tone.wav was made for this sample from two sine tones. It asks for "window", "sound.play" and "region.read"; the last
/// two are for add-ons that come with the viewer for now.
/// </summary>
public sealed class SoundSampleAddon : IAddon, IAddonDiagnostics
{
    private const string File = "tone.wav";
    private const float Volume = 0.8f;

    private IAddonSounds? _sounds;
    private IAddonWorld? _world;
    private AddonWindow? _window;
    private AddonLabel? _state;
    private readonly List<IAddonSound> _playing = [];

    public void Start(IAddonContext context)
    {
        _sounds = context.Sounds;
        _world = context.World;
        _window = context.AddWindow("main", "Sound Sample");
        _state = _window.Add(new AddonLabel("state") { Text = "Nothing playing" });
        _window.Add(new AddonButton("around") { Text = "Play around me" }).Pressed += () => Around();
        _window.Add(new AddonButton("north") { Text = "Play 5 m north" }).Pressed += () => North();
        _window.Add(new AddonButton("later") { Text = "Muffled, in 3 s" }).Pressed += () => Later();
        _window.Add(new AddonButton("stop") { Text = "Stop" }).Pressed += () => _sounds?.StopAll();
        context.AddMenuItem("Sound Sample", _window.Open);
        context.Tick += Show;
    }

    // The viewer stops the add-on's sounds and removes its window, menu item and event handlers itself.
    public void Stop() => _playing.Clear();

    private bool Around() => Keep(_sounds?.Play(File, new AddonSoundOptions(Loop: true, Gain: Volume)));

    private bool North() => _world?.Snapshot() is { } now
        && Keep(_sounds?.Play(File, new AddonSoundOptions(Loop: true, Gain: Volume, Position: now.AgentPosition + new Vector3(0, 5, 0))));

    private bool Later() => Keep(_sounds?.Play(File, new AddonSoundOptions(Gain: Volume, DelaySeconds: 3, LowPassHz: 600)));

    private bool Keep(IAddonSound? sound)
    {
        if (sound is not null)
            _playing.Add(sound);
        return sound is not null;
    }

    // a control redraws whenever its text is set, so only a change is set
    private void Show()
    {
        _playing.RemoveAll(s => !s.IsActive);
        string text = _playing.Count == 0 ? "Nothing playing" : "Sounds waiting or playing: " + _playing.Count;
        if (_state is not null && _state.Text != text)
            _state.Text = text;
    }

    /// <summary>For the viewer's development builds: "open", "around", "north", "later", "stop" and "escape" (a name that leads
    /// outside the folder); any call answers what is waiting or playing.</summary>
    public object? Call(string action, string? text)
    {
        bool? started = action switch
        {
            "around" => Around(),
            "north" => North(),
            "later" => Later(),
            "escape" => Keep(_sounds?.Play("../" + File)),
            _ => null,
        };
        if (action == "open")
            _window?.Open();
        if (action == "stop")
            _sounds?.StopAll();
        _playing.RemoveAll(s => !s.IsActive);
        var at = _world?.Snapshot();
        return new
        {
            started,
            active = _playing.Count,
            agent = at is null ? null : new[] { at.AgentPosition.X, at.AgentPosition.Y, at.AgentPosition.Z },
            camera = at is null ? null : new[] { at.Camera.Position.X, at.Camera.Position.Y, at.Camera.Position.Z },
        };
    }
}
