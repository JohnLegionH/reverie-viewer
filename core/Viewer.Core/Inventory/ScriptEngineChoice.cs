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
    /// <summary>The engines the region marks as early access (OpenSimExtras "ScriptEngineEarlyAccess"); empty when it marks none.</summary>
    public IReadOnlyList<string> EarlyAccess { get; init; } = [];

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
        IReadOnlyList<string> early = [];
        if (extras is not null && extras.TryGetValue("ScriptEngineEarlyAccess", out var marked))
            early = marked switch
            {
                IReadOnlyList<string> l => l.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).ToArray(),
                string s => s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                _ => [],
            };
        return new ScriptRegion(engines, luaEnabled, Flag("ScriptEngineChoice"), Flag("ScriptEngineChoiceNoMod")) { EarlyAccess = early };
    }
}

/// <summary>One entry of the engine or compiler list: its string-table key (and the engine name it is said with), the target it saves, and
/// whether it can be used now or the key of why it cannot.</summary>
public sealed record ScriptChoice(string LabelKey, string LabelArg, ScriptTarget Target, bool Enabled, string? DisabledKey, string DisabledArg = "", bool EarlyAccess = false);

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
        {
            bool early = IsEarlyAccess(region, engine);
            all.Add(new ScriptChoice(early ? "script.engine.lsl_early" : "script.engine.lsl", engine, ScriptTarget.Engine(engine), region.ChoiceSaved, region.ChoiceSaved ? null : NoChoice, FirstLslEngine(region), early));
        }
        if (region.LuaEnabled)
        {
            bool early = IsEarlyAccess(region, "SLua");   // the two Lua entries belong to the engine named SLua
            all.Add(new ScriptChoice(early ? "script.engine.slua_lsl_early" : "script.engine.slua_lsl", "", ScriptTarget.LslLuau, true, null, "", early));
            all.Add(new ScriptChoice(early ? "script.engine.slua_lua_early" : "script.engine.slua_lua", "", ScriptTarget.Luau, true, null, "", early));
        }
        return all;
    }

    /// <summary>Whether the region lists this engine, by its full name, as early access.</summary>
    public static bool IsEarlyAccess(ScriptRegion region, string engine) =>
        region.EarlyAccess.Any(e => e.Equals(engine, StringComparison.OrdinalIgnoreCase));

    /// <summary>The first engine the region lists that runs LSL (not SLua), which the tooltip uses as its first-line example; empty when none.</summary>
    public static string FirstLslEngine(ScriptRegion region) =>
        region.Engines.FirstOrDefault(e => !e.Equals("SLua", StringComparison.OrdinalIgnoreCase)) ?? "";

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

    /// <summary>What runs the script and why, for an unticked drop-down: the reply's engine when it has one, otherwise the saved choice, the
    /// first line's engine, or an SLua entry for a Lua script. An engine nothing names is not guessed: the region default comes back with an
    /// empty engine, and the drop-down says only "Region default".</summary>
    public static ScriptEngineWhy? Why(ScriptRegion region, ScriptRuntime? runtime, string text)
    {
        string choice = runtime?.EngineChoice ?? "";
        string? header = HeaderEngine(text, region);
        string? luaEngine = runtime is { Luau: true } ? region.Engines.FirstOrDefault(e => e.Contains("SLua", StringComparison.OrdinalIgnoreCase)) : null;
        string engine = runtime?.Engine is { Length: > 0 } running ? running
            : choice.Length > 0 ? choice
            : header ?? luaEngine ?? "";
        string reason = choice.Length > 0 ? "script.engine.why.saved" : header is not null ? "script.engine.why.first_line"
            : engine.Length > 0 ? "script.engine.why.default" : "script.engine.why.default_unnamed";
        return new ScriptEngineWhy(engine, reason);
    }

    /// <summary>The right-click Engine menu of a script in an object's contents: offered where the region saves a choice; for a script that may
    /// not be modified, usable only where the region allows an owner to set it.</summary>
    public static ContentsEngineMenu ContentsMenu(ScriptRegion region, bool modify) =>
        !region.ChoiceSaved ? new ContentsEngineMenu(false, false, null)
        : modify || region.ChoiceNoMod ? new ContentsEngineMenu(true, true, null)
        : new ContentsEngineMenu(true, false, "script.engine.no_mod");
}
