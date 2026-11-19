// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Globalization;
using Viewer.Core.Assets;

namespace Viewer.Core.Inventory;

/// <summary>
/// What a script is compiled to, as the "target" a save or a recompile sends: "lsl2", "mono", "lsl-luau" and "luau" (the official viewer's
/// four, indra/newview/llviewerassetupload.h), or "engine:&lt;Name&gt;" where a region saves a person's choice of script engine.
/// </summary>
public readonly record struct ScriptTarget(string Value)
{
    public static readonly ScriptTarget Mono = new("mono"), Lsl2 = new("lsl2"), LslLuau = new("lsl-luau"), Luau = new("luau");

    /// <summary>No target known: a save then sends none, which a region keeps the script's own target for.</summary>
    public static readonly ScriptTarget None = new("");

    public bool IsNone => string.IsNullOrEmpty(Value);

    /// <summary>The target a runtime name stands for ("lsl2", "mono", "lsl-luau", "luau"); null for any other text.</summary>
    public static ScriptTarget? FromName(string? name) =>
        name?.Trim().ToLowerInvariant() switch { "lsl2" => Lsl2, "mono" => Mono, "lsl-luau" => LslLuau, "luau" => Luau, _ => null };

    private const string EnginePrefix = "engine:";

    /// <summary>The target that names one engine, as a region lists it in its script engines.</summary>
    public static ScriptTarget Engine(string name) => new(EnginePrefix + name);

    public bool IsEngine => Value?.StartsWith(EnginePrefix, StringComparison.Ordinal) == true;

    /// <summary>The engine an "engine:&lt;Name&gt;" target names; empty for any other target.</summary>
    public string EngineName => IsEngine ? Value![EnginePrefix.Length..] : "";

    public override string ToString() => Value ?? "";
}

/// <summary>
/// What a region says about a script it runs (the ScriptRunningReply on the event queue). <paramref name="Mono"/>, <paramref name="Luau"/> and
/// <paramref name="LuauLanguage"/> are the official viewer's three flags; <paramref name="Engine"/> is the engine running the script and
/// <paramref name="EngineChoice"/> the engine a person saved for it, both empty on a region that does not send them.
/// </summary>
public sealed record ScriptRuntime(bool Running, bool Mono, bool Luau = false, bool LuauLanguage = false, string Engine = "", string EngineChoice = "")
{
    /// <summary>The target the script has now, which a save sends back unless the person changes it (the official editor reads the same flags,
    /// indra/newview/llpreviewscript.cpp): Lua when both Luau flags are set, LSL on Lua for Luau alone, a saved engine choice, then Mono or LSL2.</summary>
    public ScriptTarget Current =>
        Luau ? (LuauLanguage ? ScriptTarget.Luau : ScriptTarget.LslLuau)
        : EngineChoice.Length > 0 ? ScriptTarget.Engine(EngineChoice)
        : Mono ? ScriptTarget.Mono : ScriptTarget.Lsl2;
}

/// <summary>A script the editor works on: in the agent's inventory (no object), or inside an object (its task inventory).</summary>
/// <param name="AssetId">The text's asset; empty for a new script with no text yet.</param>
/// <param name="ObjectId">The object the script is in; empty for a script in the inventory.</param>
/// <param name="LocalId">The object's region-local id (the region's handle on it); 0 for a script in the inventory.</param>
/// <param name="LuaSubtype">The item's subtype says it is a Lua script (the low byte of its flags is 1); the grid sends that for a script in the inventory.</param>
/// <param name="LslOnLuau">The item's flags carry the LSL-on-Lua bit (0x01000000), where a grid sends it.</param>
/// <param name="Runtime">The runtime the item's own metadata names ("lsl2", "mono", "lsl-luau", "luau"), as the official viewer's inventory reads it;
/// empty when the grid sent none.</param>
public sealed record ScriptAccess(Guid ItemId, Guid AssetId, Guid AgentId, NotecardPermissions Permissions, bool InLibrary,
    Guid ObjectId = default, uint LocalId = 0, bool LuaSubtype = false, bool LslOnLuau = false, string Runtime = "")
{
    public const uint LslOnLuauFlag = 0x01000000;

    public bool InObject => ObjectId != Guid.Empty;

    /// <summary>The target the item itself says it has: its runtime, else the Lua subtype or the LSL-on-Lua bit; null when it says none.</summary>
    public ScriptTarget? KnownTarget => ScriptTarget.FromName(Runtime) ?? (LuaSubtype ? ScriptTarget.Luau : LslOnLuau ? ScriptTarget.LslLuau : null);
}

/// <summary>How a script may be shown.</summary>
public enum ScriptView { Editable, ReadOnly, NotAllowed }

/// <summary>
/// The official viewer's rules for opening a script (indra/newview/llpreviewscript.cpp): a script in the inventory is read when the agent
/// may copy it and may modify it or it is in the library, and it can be edited only when it may be modified (LLPreviewLSL::loadAsset); a
/// script inside an object needs both copy and modify, or it is not shown at all (LLLiveLSLEditor::loadAsset). The agent's rights are the
/// owner's when the agent owns the item, otherwise everyone's.
/// </summary>
public static class ScriptRules
{
    private static uint Mask(ScriptAccess a) => a.Permissions.Owner == a.AgentId ? a.Permissions.OwnerMask : a.Permissions.EveryoneMask;

    public static ScriptView ViewOf(ScriptAccess access)
    {
        uint mask = Mask(access);
        bool copy = (mask & NotecardPermissions.Copy) != 0, modify = (mask & NotecardPermissions.Modify) != 0;
        if (access.InObject)
            return copy && modify ? ScriptView.Editable : ScriptView.NotAllowed;
        if (!copy || !(modify || access.InLibrary))
            return ScriptView.NotAllowed;
        return modify && !access.InLibrary ? ScriptView.Editable : ScriptView.ReadOnly;
    }
}

/// <summary>One line of the compiler's answer, with the place in the text it names (both counted from 0, as the grid sends them), or -1
/// when the line names no place.</summary>
public sealed record ScriptMessage(string Text, int Line, int Column)
{
    /// <summary>
    /// Reads the place the way the official viewer does when an error is clicked (indra/newview/llpreviewscript.cpp onErrorList): the
    /// first character "(" is dropped, commas and ")" become spaces, and the first two whole numbers are the line and the column, used as
    /// they are (both start at 0). Second Life sends "(12, 4) : ERROR : ..."; OpenSim's YEngine sends "(12,4) Error: ..." (its tokenizer
    /// counts lines from 0 too); Firestorm reads both with the same code and no adjustment.
    /// </summary>
    public static ScriptMessage Parse(string text)
    {
        text ??= "";
        string clean = new(text.Where(c => c == '\t' || !char.IsControl(c)).ToArray());   // stripNonprintable
        if (clean.Length < 2 || clean[0] != '(')
            return new ScriptMessage(clean, -1, -1);
        var rest = clean[1..].Replace(',', ' ').Replace(')', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (rest.Length >= 2 && int.TryParse(rest[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int line)
            && int.TryParse(rest[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int col) && line >= 0 && col >= 0)
            return new ScriptMessage(clean, line, col);
        return new ScriptMessage(clean, -1, -1);
    }
}

public enum ScriptSaveStatus { Saved, NoService, Failed }

/// <summary>The grid's answer to a save: whether the text was stored, whether the grid compiled it and what the compiler said.</summary>
/// <param name="Compiled">True: compiled with no errors; false: the compiler reported errors; null: the answer says nothing about compiling
/// (OpenSim stores a script in the inventory without compiling it, and its answer carries no "compiled").</param>
/// <param name="Reason">The failure's reason as the grid gave it (an upload failure); null otherwise.</param>
public sealed record ScriptSaveResult(ScriptSaveStatus Status, bool? Compiled, Guid NewAssetId, IReadOnlyList<ScriptMessage> Messages, string? Reason = null)
{
    public static ScriptSaveResult Of(ScriptSaveStatus status, string? reason = null) => new(status, null, Guid.Empty, [], reason);
}

/// <summary>Reads and saves scripts and their running state. Implemented by the protocol adapter.</summary>
public interface IScriptTransport
{
    /// <summary>What the grid's copy of an inventory item says about it as a script; null when it is unknown or not a script.</summary>
    ScriptAccess? ScriptAccessOf(Guid itemId);

    /// <summary>Fetches a script's text the way the official viewer asks for an item's asset (the inventory-item transfer, with the
    /// object's id for a script inside an object).</summary>
    Task<AssetFetchResult> FetchScriptAsync(ScriptAccess access, CancellationToken ct);

    /// <summary>Saves the text: UpdateScriptAgent for the inventory, UpdateScriptTask (with the running state and the target) for a
    /// script inside an object, then the uploader those name.</summary>
    Task<ScriptSaveResult> SaveScriptAsync(ScriptAccess access, byte[] text, ScriptTarget target, bool running, CancellationToken ct);

    /// <summary>Asks whether a script inside an object runs and what it is compiled for; null when no answer came.</summary>
    Task<ScriptRuntime?> GetScriptRunningAsync(ScriptAccess access, CancellationToken ct);

    /// <summary>What the agent's region offers for choosing a script's engine or compiler.</summary>
    ScriptRegion Region { get; }

    /// <summary>Sets a script's engine without saving its text (the SetScriptEngine capability); a null target clears the choice. False when
    /// the region refused or has no such capability.</summary>
    Task<bool> SetScriptEngineAsync(ScriptAccess access, ScriptTarget? target, CancellationToken ct);

    /// <summary>Starts or stops a script inside an object. False when it could not be sent.</summary>
    bool SetScriptRunning(ScriptAccess access, bool running);

    /// <summary>Resets a script inside an object. False when it could not be sent.</summary>
    bool ResetScript(ScriptAccess access);
}
