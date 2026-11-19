// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Inventory;

/// <summary>
/// What a region offers for choosing how a script runs: the script engines it lists (OpenSimExtras "ScriptEngines"), whether it runs Lua
/// scripts (the simulator feature "LuaScriptsEnabled", which the official viewer reads to enable its two Lua entries), whether it saves a
/// person's engine choice ("ScriptEngineChoice") and whether an owner may set one on a no-modify script ("ScriptEngineChoiceNoMod").
/// </summary>
public sealed record ScriptRegion(IReadOnlyList<string> Engines, bool LuaEnabled, bool ChoiceSaved, bool ChoiceNoMod)
{
    /// <summary>A region that offers nothing: an older grid, or none yet.</summary>
    public static ScriptRegion None { get; } = new([], false, false, false);

    /// <summary>The region's offer from its OpenSim extras (null when it sent none) and its Lua switch. The engine list may arrive as a list of
    /// text or as one comma-separated text; a flag counts only when it is the boolean true.</summary>
    public static ScriptRegion From(IReadOnlyDictionary<string, object?>? extras, bool luaEnabled)
    {
        IReadOnlyList<string> engines = [];
        if (extras is not null && extras.TryGetValue("ScriptEngines", out var list))
            engines = list switch
            {
                IReadOnlyList<string> l => l.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).ToArray(),
                string s => s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                _ => [],
            };
        bool Flag(string key) => extras is not null && extras.TryGetValue(key, out var v) && v is true;
        return new ScriptRegion(engines, luaEnabled, Flag("ScriptEngineChoice"), Flag("ScriptEngineChoiceNoMod"));
    }
}

/// <summary>One entry of the engine or compiler list: its string-table key (and the engine name it is said with), the target it saves, and
/// whether it can be used now or the key of why it cannot.</summary>
public sealed record ScriptChoice(string LabelKey, string LabelArg, ScriptTarget Target, bool Enabled, string? DisabledKey);

/// <summary>Why a script runs in the engine it does, for the greyed drop-down: the engine and the string-table key of the reason.</summary>
public sealed record ScriptEngineWhy(string Engine, string ReasonKey);

/// <summary>Whether the contents menu's Engine entry is offered and usable, and the string-table key of the reason when it is greyed.</summary>
public sealed record ContentsEngineMenu(bool Offered, bool Enabled, string? DisabledKey);

/// <summary>The choices a script editor and a contents menu offer, by kind of grid.</summary>
public static class ScriptEngineChoices
{
    private const string LuaOff = "script.target.lua_off", NoChoice = "script.engine.no_choice";

    /// <summary>Second Life: the four-way compiler choice of the official viewer's Lua editor (indra/newview/llpreviewscript.cpp and its
    /// panel_script_ed.xml "compile_target": LSL Legacy "lsl2", LSL on Mono "mono", LSL on Lua "lsl-luau", Lua "luau"), the two Lua entries
    /// off unless the region sends LuaScriptsEnabled.</summary>
    public static IReadOnlyList<ScriptChoice> SecondLife(ScriptRegion region) =>
    [
        new("script.target.lsl2", "", ScriptTarget.Lsl2, true, null),
        new("script.target.mono", "", ScriptTarget.Mono, true, null),
        new("script.target.lsl_luau", "", ScriptTarget.LslLuau, region.LuaEnabled, region.LuaEnabled ? null : LuaOff),
        new("script.target.luau", "", ScriptTarget.Luau, region.LuaEnabled, region.LuaEnabled ? null : LuaOff),
    ];

    /// <summary>OpenSim and NGC grids with "Choose engine myself" ticked: an entry per LSL engine the region lists (off where the region does
    /// not save a choice), then SLua (LSL) and SLua (Lua) where Lua is on.</summary>
    public static IReadOnlyList<ScriptChoice> OpenSim(ScriptRegion region)
    {
        var all = new List<ScriptChoice>();
        foreach (var engine in region.Engines)
            all.Add(new ScriptChoice("script.engine.lsl", engine, ScriptTarget.Engine(engine), region.ChoiceSaved, region.ChoiceSaved ? null : NoChoice));
        if (region.LuaEnabled)
        {
            all.Add(new ScriptChoice("script.engine.slua_lsl", "", ScriptTarget.LslLuau, true, null));
            all.Add(new ScriptChoice("script.engine.slua_lua", "", ScriptTarget.Luau, true, null));
        }
        return all;
    }

    /// <summary>The engine a first line asks for: "//&lt;Name&gt;:" naming a listed engine (the colon is required; a short name such as
    /// Alpha matches Example.Alpha), or "--!slua"; null when the line asks for none.</summary>
    public static string? HeaderEngine(string text, ScriptRegion region)
    {
        if (string.IsNullOrEmpty(text))
            return null;
        int end = text.IndexOfAny(['\r', '\n']);
        string line = (end < 0 ? text : text[..end]).Trim();
        if (line.StartsWith("--!slua", StringComparison.OrdinalIgnoreCase))
            return region.Engines.FirstOrDefault(e => e.Equals("SLua", StringComparison.OrdinalIgnoreCase)) ?? "SLua";
        if (!line.StartsWith("//", StringComparison.Ordinal))
            return null;
        int colon = line.IndexOf(':');
        if (colon < 3)
            return null;
        string name = line[2..colon].Trim();
        return name.Length == 0 ? null
            : region.Engines.FirstOrDefault(e => e.Equals(name, StringComparison.OrdinalIgnoreCase))
              ?? region.Engines.FirstOrDefault(e => e.EndsWith("." + name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>What runs the script and why, for an unticked drop-down: the reply's engine when it has one, otherwise worked out from the saved
    /// choice, the first line and the region default (the first engine the region lists, which is the order its engines are loaded in). The
    /// reason is the saved choice, then the first line, then the region default. Null when nothing names an engine.</summary>
    public static ScriptEngineWhy? Why(ScriptRegion region, ScriptRuntime? runtime, string text)
    {
        string choice = runtime?.EngineChoice ?? "";
        string? header = HeaderEngine(text, region);
        string reason = choice.Length > 0 ? "script.engine.why.saved" : header is not null ? "script.engine.why.first_line" : "script.engine.why.default";
        string engine = runtime?.Engine is { Length: > 0 } running ? running
            : choice.Length > 0 ? choice
            : header ?? (runtime is { Luau: true } ? "SLua" : region.Engines.FirstOrDefault() ?? "");
        return engine.Length == 0 ? null : new ScriptEngineWhy(engine, reason);
    }

    /// <summary>The right-click Engine menu of a script in an object's contents: offered where the region saves a choice; for a script that may
    /// not be modified, usable only where the region allows an owner to set it.</summary>
    public static ContentsEngineMenu ContentsMenu(ScriptRegion region, bool modify) =>
        !region.ChoiceSaved ? new ContentsEngineMenu(false, false, null)
        : modify || region.ChoiceNoMod ? new ContentsEngineMenu(true, true, null)
        : new ContentsEngineMenu(true, false, "script.engine.no_mod");
}
