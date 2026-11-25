// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Globalization;
using Viewer.Addons;

namespace WorldSample;

/// <summary>
/// A sample add-on that reads the world (interface 0.6): a menu item opens a window with the region, the avatar's position, the
/// water height, the wind where the avatar is, and the teleports counted since it started. A slider chooses how many decimals the
/// numbers show. It asks for "window" and "region.read"; region.read is for add-ons that come with the viewer for now.
/// </summary>
public sealed class WorldSampleAddon : IAddon, IAddonDiagnostics
{
    private IAddonContext? _context;
    private IAddonWorld? _world;
    private AddonWindow? _window;
    private AddonLabel? _region, _position, _water, _wind, _teleports;
    private AddonSlider? _decimals;
    private int _teleported, _regionChanges;
    private object? _atTeleport;   // what a read inside the last teleport event answered

    public void Start(IAddonContext context)
    {
        _context = context;
        _world = context.World;
        _window = context.AddWindow("main", "World Sample");
        _region = _window.Add(new AddonLabel("region") { Text = "Region: not logged in" });
        _position = _window.Add(new AddonLabel("position"));
        _water = _window.Add(new AddonLabel("water"));
        _wind = _window.Add(new AddonLabel("wind"));
        _teleports = _window.Add(new AddonLabel("teleports") { Text = "Teleports: 0" });
        _decimals = _window.Add(new AddonSlider("decimals", 0, 3) { Text = "Decimals" });
        _decimals.Value = 1;
        _decimals.Moved += _ => Show();
        context.AddMenuItem("World Sample", _window.Open);
        if (_world is { } world)
        {
            world.RegionChanged += _ => _regionChanges++;
            world.Teleported += region =>
            {
                var at = world.Snapshot();
                _atTeleport = new { told = region, read = at?.RegionName, agent = at is null ? null : new[] { at.AgentPosition.X, at.AgentPosition.Y, at.AgentPosition.Z } };
                Set(_teleports, "Teleports: " + ++_teleported);
            };
        }
        context.Tick += Show;
    }

    // The viewer removes the window, the menu item and the event handlers itself.
    public void Stop() => _context = null;

    private void Show()
    {
        if (_world?.Snapshot() is not { } now)
        {
            Set(_region, "Region: not available");
            Set(_position, "");
            Set(_water, "");
            Set(_wind, "");
            return;
        }
        string format = "F" + (int)_decimals!.Value;
        string Number(float v) => v.ToString(format, CultureInfo.InvariantCulture);
        Set(_region, $"Region: {now.RegionName}, {now.RegionSizeX} x {now.RegionSizeY} m");
        Set(_position, $"Position: {Number(now.AgentPosition.X)}, {Number(now.AgentPosition.Y)}, {Number(now.AgentPosition.Z)}");
        Set(_water, now.WaterHeight is { } water
            ? $"Water: {Number(water)} m (camera {(now.CameraUnderWater ? "below" : "above")})"
            : "Water: unknown");
        Set(_wind, _world.WindAt(now.AgentPosition.X, now.AgentPosition.Y) is { } wind
            ? $"Wind: {Number(wind.X)}, {Number(wind.Y)} m/s"
            : "Wind: unknown");
    }

    // a control redraws whenever its text is set, so only a change is set
    private static void Set(AddonControl? control, string text)
    {
        if (control is not null && control.Text != text)
            control.Text = text;
    }

    /// <summary>For the viewer's development builds: "open" opens the window; any call answers what the window shows.</summary>
    public object? Call(string action, string? text)
    {
        if (action == "open")
            _window?.Open();
        var now = _world?.Snapshot();
        return new
        {
            region = _region?.Text,
            position = _position?.Text,
            water = _water?.Text,
            wind = _wind?.Text,
            teleports = _teleported,
            region_changes = _regionChanges,
            at_teleport = _atTeleport,
            decimals = _decimals?.Value,
            agent = now is null ? null : new[] { now.AgentPosition.X, now.AgentPosition.Y, now.AgentPosition.Z },
            camera = now is null ? null : new[] { now.Camera.Position.X, now.Camera.Position.Y, now.Camera.Position.Z },
            forward = now is null ? null : new[] { now.Camera.Forward.X, now.Camera.Forward.Y, now.Camera.Forward.Z },
            ground = now is null ? null : _world?.GroundHeight(now.AgentPosition.X, now.AgentPosition.Y),
            water_height = now?.WaterHeight,
            sun = now?.SunDirection is { } s ? new[] { s.X, s.Y, s.Z } : null,
            sky = now?.Environment?.ToString(),
        };
    }
}
