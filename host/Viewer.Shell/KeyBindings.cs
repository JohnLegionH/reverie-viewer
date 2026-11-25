// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Text.Json;
using Viewer.Core.Avatars;
using Viewer.Core.Diagnostics;

namespace Viewer.Shell;

/// <summary>
/// Which key moves the user's own avatar how: data, read from a bindings file, with the official viewer's third-person
/// defaults (its key_bindings.xml) when there is none. A file is JSON:
/// <code>{ "bindings": [ { "key": "W", "mask": "NONE", "command": "push_forward" }, ... ] }</code>
/// Keys use the official file's names (letters, LEFT, RIGHT, UP, DOWN, PGUP, PGDN, HOME, SPACE); the mask is NONE or
/// SHIFT or CTL; the commands are <see cref="MoveCommands.Names"/>. A file that cannot be read, or has no usable binding,
/// leaves the defaults in force and says why. No engine type.
/// </summary>
public sealed class KeyBindings
{
    private readonly Dictionary<(string Key, bool Shift), MoveCommand> _map;
    private readonly Dictionary<string, MoveCommand> _control;

    private KeyBindings(Dictionary<(string, bool), MoveCommand> map, Dictionary<string, MoveCommand>? control = null)
    {
        _map = map;
        _control = control ?? new();
    }

    /// <summary>The bindings without the Control mask.</summary>
    public int Count => _map.Count;

    /// <summary>The bindings with the Control mask.</summary>
    public int ControlCount => _control.Count;

    /// <summary>The official viewer's third-person movement keys.</summary>
    public static KeyBindings Default { get; } = new(ThirdPersonMoves(), new() { ["R"] = MoveCommand.ToggleRun });   // R: the official menu's accelerator for Always Run

    /// <summary>The movement keys of the official default table (third_person, without a modifier or with Shift).</summary>
    private static Dictionary<(string, bool), MoveCommand> ThirdPersonMoves()
    {
        var map = new Dictionary<(string, bool), MoveCommand>();
        foreach (var e in KeyBindingTable.For(KeyMode.ThirdPerson))
            if (e.Mouse.Length == 0 && e.Mask is CameraMask.None or CameraMask.Shift && KeyBindingTable.Route(e.Command) is { Kind: KeyRouteKind.Move } route)
                map[(e.Key, e.Mask == CameraMask.Shift)] = route.Move;
        return map;
    }

    /// <summary>The official viewer's first-person (mouse-look) movement keys (key_bindings.xml, first_person): A and D and the left and
    /// right arrows slide where the third-person ones turn; Shift changes nothing. A bindings file does not change them.</summary>
    private static readonly Dictionary<string, MoveCommand> FirstPerson = KeyBindingTable.For(KeyMode.FirstPerson)
        .Where(e => e.Mouse.Length == 0 && KeyBindingTable.Route(e.Command) is { Kind: KeyRouteKind.Move })
        .ToDictionary(e => e.Key, e => KeyBindingTable.Route(e.Command).Move);

    /// <summary>The command a key (with or without Shift) is bound to. With <paramref name="control"/> held, a binding with
    /// the Control mask is looked for first, and a key bound without it still works as before. With Shift alone, a key with no
    /// Shift binding keeps its plain one, as in the official viewer, where a binding applies while extra modifier keys are held
    /// (llviewerinput.cpp, scanKey); not with <paramref name="alt"/> held too, whose chords are the camera's. In <paramref name="mouseLook"/> the first-person keys apply.</summary>
    public bool TryCommand(string key, bool shift, out MoveCommand command, bool control = false, bool mouseLook = false, bool alt = false)
    {
        string k = key.ToUpperInvariant();
        if (control && !shift && _control.TryGetValue(k, out command)) return true;
        if (mouseLook) return FirstPerson.TryGetValue(k, out command);
        if (_map.TryGetValue((k, shift), out command)) return true;
        return shift && !control && !alt && _map.TryGetValue((k, false), out command);
    }

    /// <summary>Reads bindings from JSON text; null and a reason when none can be used.</summary>
    public static KeyBindings? Parse(string json, out string? why)
    {
        why = null;
        var map = new Dictionary<(string, bool), MoveCommand>();
        var control = new Dictionary<string, MoveCommand>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("bindings", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                why = "no bindings array";
                return null;
            }
            foreach (var b in list.EnumerateArray())
            {
                string? key = Str(b, "key"), mask = Str(b, "mask") ?? "NONE", command = Str(b, "command");
                if (string.IsNullOrWhiteSpace(key) || command is null || !MoveCommands.Names.TryGetValue(command, out var cmd))
                    continue;
                string m = mask.ToUpperInvariant();
                if (m is "CTL" or "CONTROL")
                {
                    control[key.Trim().ToUpperInvariant()] = cmd;
                    continue;
                }
                bool? shift = m switch { "NONE" => false, "SHIFT" => true, _ => null };
                if (shift is null) continue;
                map[(key.Trim().ToUpperInvariant(), shift.Value)] = cmd;
            }
        }
        catch (JsonException)
        {
            why = "not JSON";
            return null;
        }
        if (map.Count + control.Count == 0)
        {
            why = "no usable binding";
            return null;
        }
        return new KeyBindings(map, control);
    }

    /// <summary>The bindings in <paramref name="path"/>, or the defaults (null path, a missing or unusable file), logged.</summary>
    public static KeyBindings Load(string? path, CoreLog log)
    {
        if (path is null)
            return Default;
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.Warn("keys.file_unreadable", CoreLog.F("why", e.GetType().Name), CoreLog.F("using", "defaults"));
            return Default;
        }
        if (Parse(text, out var why) is { } read)
        {
            log.Info("keys.loaded", CoreLog.F("bindings", read.Count));
            return read;
        }
        log.Warn("keys.file_unusable", CoreLog.F("why", why), CoreLog.F("using", "defaults"));
        return Default;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
