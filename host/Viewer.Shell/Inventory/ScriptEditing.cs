// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Globalization;
using System.Text;
using Viewer.Core.Assets;
using Viewer.Core.Inventory;

namespace Viewer.Shell.Inventory;

/// <summary>What the close of an editor window with unsaved changes is answered with (the official "SaveChanges" question:
/// Save, Don't Save, Cancel; indra/newview/skins/default/xui/en/notifications.xml).</summary>
public enum ScriptCloseAnswer { Save, DontSave, Cancel }

/// <summary>What the window does after the question is answered: save and close once saved, close at once, or stay open.</summary>
public enum ScriptCloseStep { SaveThenClose, Close, Stay }

/// <summary>
/// One script editor window as data, after the official viewer's script editors (indra/newview/llpreviewscript.cpp: LLScriptEdCore,
/// LLPreviewLSL for the inventory, LLLiveLSLEditor for a script inside an object): the text and whether it changed, whether it may be
/// edited, the save and its answer (the status line and the compiler's messages), the running state of a script inside an object, the
/// question asked when a changed script is closed, and the find, replace and go-to-line steps. No engine types; main thread only.
/// </summary>
public sealed class ScriptEditing
{
    private bool _closeWhenSaved;

    public ScriptEditing(ScriptAccess access, string name)
    {
        Access = access;
        Name = name;
        View = ScriptRules.ViewOf(access);
        if (View == ScriptView.NotAllowed)
            StatusKey = "script.not_allowed";
    }

    public ScriptAccess Access { get; private set; }
    public string Name { get; }
    public ScriptView View { get; }
    public bool InObject => Access.InObject;

    private string Pristine { get; set; } = "";

    /// <summary>The text as the window holds it now.</summary>
    public string Text { get; set; } = "";

    /// <summary>The text has been read from the grid (or the script is new and empty).</summary>
    public bool Loaded { get; private set; }

    /// <summary>The window's status line, as a string-table key and its argument; null key: nothing to say.</summary>
    public string? StatusKey { get; private set; }
    public string StatusArg { get; private set; } = "";

    public IReadOnlyList<ScriptMessage> Messages { get; private set; } = [];

    public bool Saving { get; private set; }

    /// <summary>The compile target sent with a save: what the person chose, else what the region said of the script, else what the item itself says
    /// (its runtime, Lua subtype or LSL-on-Lua bit). Where none is known, Second Life gets Mono (what its editor assumes) and any other grid gets
    /// none, so the region keeps the script as it is.</summary>
    public ScriptTarget Target
    {
        get => _target ?? Access.KnownTarget ?? (SecondLife ? ScriptTarget.Mono : ScriptTarget.None);
        set => _target = value;
    }

    private ScriptTarget? _target;

    /// <summary>The running state of a script inside an object, once the region has answered; null until then.</summary>
    public bool? Running { get; private set; }

    /// <summary>The changes since the last load or save. A window that cannot edit has none.</summary>
    public bool IsDirty => CanEdit && !string.Equals(Text, Pristine, StringComparison.Ordinal);

    /// <summary>The text can be typed into.</summary>
    public bool CanEdit => Loaded && View == ScriptView.Editable;

    /// <summary>Save is offered: there is something to save and nothing is being saved.</summary>
    public bool CanSave => IsDirty && !Saving;

    /// <summary>The Running tick may be used: a script inside an object whose state the region has told, and not while saving
    /// (llpreviewscript.cpp LLLiveLSLEditor::draw).</summary>
    public bool CanSetRunning => InObject && Running is not null && !Saving && View == ScriptView.Editable;

    /// <summary>The fetched text, or why there is none (the official ScriptMissing, ScriptNoPermissions and UnableToLoadScript).</summary>
    public void TextFetched(AssetFetchResult fetched)
    {
        if (View == ScriptView.NotAllowed)
            return;
        switch (fetched.Status)
        {
            case AssetFetchStatus.Ok:
                Text = Pristine = Encoding.UTF8.GetString(fetched.Bytes.Span).TrimEnd('\0');
                Loaded = true;
                StatusKey = View == ScriptView.ReadOnly ? "script.read_only" : null;
                break;
            case AssetFetchStatus.NotFound:
                StatusKey = "script.missing";
                break;
            case AssetFetchStatus.Denied:
                StatusKey = "script.no_permission";
                break;
            default:
                StatusKey = "script.unable_to_load";
                break;
        }
    }

    /// <summary>The script has no text yet (a new script with no asset): an empty editor.</summary>
    public void LoadedEmpty()
    {
        if (View == ScriptView.NotAllowed)
            return;
        Text = Pristine = "";
        Loaded = true;
        StatusKey = View == ScriptView.ReadOnly ? "script.read_only" : null;
    }

    /// <summary>The script's text as bytes to send, and the window marked as saving; null when it may not be saved now.</summary>
    public byte[]? BeginSave()
    {
        if (!CanEdit || Saving)
            return null;
        Saving = true;
        StatusKey = "script.saving";
        StatusArg = "";
        return Encoding.UTF8.GetBytes(Text);
    }

    /// <summary>The grid's answer to a save: compiled, "Save complete." (llpreviewscript.cpp: CompileSuccessful then SaveComplete); errors,
    /// each listed; an upload that failed, "File upload failed: " and the reason, with the changes kept.</summary>
    public void SaveFinished(ScriptSaveResult result)
    {
        Saving = false;
        StatusArg = "";
        if (result.Status != ScriptSaveStatus.Saved)
        {
            StatusKey = result.Status == ScriptSaveStatus.NoService ? "script.no_service" : "script.upload_failed";
            StatusArg = result.Reason ?? "";
            _closeWhenSaved = false;
            return;
        }
        Pristine = Text;
        if (result.StoredAssetFor(Access) is var stored && stored != Guid.Empty)
            Access = Access with { AssetId = stored };
        Messages = result.Messages;
        StatusKey = result.Compiled == false || result.Messages.Count > 0 ? null : "script.save_complete";
        if (_closeWhenSaved)
            CloseAfterSave = true;
    }

    /// <summary>The region's answer about the running state (ScriptRunningReply: Running and Mono).</summary>
    public void RunningKnown(ScriptRuntime runtime)
    {
        Running = runtime.Running;
        Runtime = runtime;
        if (!_targetChosen)
            Target = runtime.Current;   // a save sends back the target the script has, unless the person changed it
    }

    /// <summary>Whether the grid is Second Life's: its four-way compiler choice is offered. Elsewhere the engine row is.</summary>
    public bool SecondLife { get; set; }

    /// <summary>What the agent's region offers for choosing an engine.</summary>
    public ScriptRegion Region { get; set; } = ScriptRegion.None;

    /// <summary>What the region last said of the script; null until it has.</summary>
    public ScriptRuntime? Runtime { get; private set; }

    /// <summary>"Choose engine myself" is ticked (not on Second Life).</summary>
    public bool ChooseEngine { get; private set; }

    /// <summary>The entries of the compiler or engine drop-down now: Second Life's four, or on other grids the engine entries once
    /// "Choose engine myself" is ticked (none before).</summary>
    public IReadOnlyList<ScriptChoice> Choices =>
        SecondLife ? ScriptEngineChoices.SecondLife(Region) : ChooseEngine ? ScriptEngineChoices.OpenSim(Region) : [];

    /// <summary>The engine row (the tick box and its drop-down) is shown: not on Second Life, and only where the region lists an engine or runs Lua.</summary>
    public bool EngineRowVisible => !SecondLife && (Region.Engines.Count > 0 || Region.LuaEnabled);

    /// <summary>The engine row is shown but nothing in it can be chosen: the region lists engines yet does not keep a choice, and has no Lua
    /// entries to offer either. The tick is then greyed, as Second Life's greyed entries are, and its tooltip says why.</summary>
    public bool EngineChoiceGreyed => !SecondLife && EngineRowVisible && !Region.ChoiceSaved && !Region.LuaEnabled;

    /// <summary>The engine a first line can name in the tooltip of a greyed tick: the region's first LSL engine.</summary>
    public string EngineChoiceExample => ScriptEngineChoices.FirstLslEngine(Region);

    /// <summary>What runs the script and why, for the greyed drop-down; null when there is nothing to say.</summary>
    public ScriptEngineWhy? Why => SecondLife || Region.Engines.Count == 0 && Runtime is not { Luau: true } ? null : ScriptEngineChoices.Why(Region, Runtime, Text);

    /// <summary>The script is Lua (SLua): it will be compiled as Lua, or its first line says so (--!slua). The editor then colours Lua's words and
    /// the ll, ossl, iw and bot functions instead of LSL's.</summary>
    public bool IsLua => Target == ScriptTarget.Luau || Text.StartsWith("--!slua", StringComparison.OrdinalIgnoreCase);

    /// <summary>The person ticks or unticks "Choose engine myself". Unticking goes back to the script's own target; true when a saved choice
    /// is to be cleared with SetScriptEngine.</summary>
    public bool SetChooseEngine(bool on)
    {
        if (SecondLife || on && (!EngineRowVisible || EngineChoiceGreyed))
            return false;
        ChooseEngine = on;
        if (on)
            return false;
        bool hadSaved = Runtime is { EngineChoice.Length: > 0 };
        if (hadSaved && Runtime is { } r)
            Runtime = r with { EngineChoice = "" };
        _targetChosen = false;
        _target = Runtime?.Current;   // back to what the region said, else what the item itself says
        return hadSaved && Region.ChoiceSaved;
    }

    /// <summary>The person picks an entry of the drop-down; an entry that is greyed, or a pick on another grid's unticked row, changes nothing.</summary>
    public void Choose(ScriptTarget target)
    {
        if (!SecondLife && !ChooseEngine)
            return;
        if (Choices.FirstOrDefault(c => c.Target == target) is { Enabled: true })
        {
            Target = target;
            _targetChosen = true;
        }
    }

    private bool _targetChosen;

    /// <summary>The Running tick changed by the person: true when the change is to be sent.</summary>
    public bool SetRunning(bool running)
    {
        if (!CanSetRunning)
            return false;
        Running = running;
        return true;
    }

    /// <summary>Closing the window: true when it may close now; false when the question must be asked first.</summary>
    public bool MayClose() => !IsDirty;

    /// <summary>The answer to the unsaved-changes question (llpreviewscript.cpp handleSaveChangesDialog: Save saves and closes once saved,
    /// Don't Save closes, Cancel keeps the window).</summary>
    public ScriptCloseStep CloseAnswered(ScriptCloseAnswer answer)
    {
        switch (answer)
        {
            case ScriptCloseAnswer.DontSave:
                return ScriptCloseStep.Close;
            case ScriptCloseAnswer.Save when Saving || BeginSave() is not null:
                _closeWhenSaved = true;
                return ScriptCloseStep.SaveThenClose;
            default:
                return ScriptCloseStep.Stay;
        }
    }

    /// <summary>A save asked for by the question has finished well: the window closes now.</summary>
    public bool CloseAfterSave { get; private set; }

    private string[] Lines => Text.Split('\n');

    /// <summary>
    /// Where a click on message <paramref name="index"/> puts the caret: its line and column (counted from 0, as the official viewer's
    /// setCursor takes them), kept inside the text; null when the message names no place.
    /// </summary>
    public (int Line, int Column)? CaretFor(int index)
    {
        if (index < 0 || index >= Messages.Count || Messages[index] is not { Line: >= 0 } m)
            return null;
        var lines = Lines;
        if (m.Line >= lines.Length)
            return (lines.Length - 1, 0);   // a line past the end: the start of the last line
        return (m.Line, Math.Min(m.Column, lines[m.Line].TrimEnd('\r').Length));
    }

    /// <summary>The line a go-to-line entry asks for, counted from 0, kept inside the text; null when the entry is not a line number. The entry
    /// is the number shown beside the line (the editor numbers its lines from 1).</summary>
    public int? GoToLine(string entry)
    {
        if (!int.TryParse(entry.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
            return null;
        return Math.Clamp(n - 1, 0, Lines.Length - 1);
    }

    /// <summary>The next place <paramref name="find"/> stands at or after <paramref name="from"/>, going round to the start; -1 when nowhere
    /// (llpreviewscript.cpp LLFloaterScriptSearch: case-insensitive unless asked, searching on from the caret and round).</summary>
    public static int FindNext(string text, string find, int from, bool matchCase)
    {
        if (string.IsNullOrEmpty(find) || string.IsNullOrEmpty(text))
            return -1;
        var how = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        int at = text.IndexOf(find, Math.Clamp(from, 0, text.Length), how);
        return at >= 0 ? at : text.IndexOf(find, how);
    }

    /// <summary>Every <paramref name="find"/> replaced: the new text and how many were replaced.</summary>
    public static (string Text, int Count) ReplaceAll(string text, string find, string replacement, bool matchCase)
    {
        if (string.IsNullOrEmpty(find))
            return (text, 0);
        var how = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var sb = new StringBuilder(text.Length);
        int count = 0, pos = 0;
        while (true)
        {
            int at = text.IndexOf(find, pos, how);
            if (at < 0)
                break;
            sb.Append(text, pos, at - pos).Append(replacement);
            pos = at + find.Length;
            count++;
        }
        sb.Append(text, pos, text.Length - pos);
        return (sb.ToString(), count);
    }

    /// <summary>Whether a login address is Second Life's (by its login host, <see cref="Viewer.Core.Session.GridPolicy.KindOf"/>): there the
    /// editor shows LSL's words only; elsewhere, as Firestorm does on OpenSim grids (llpreviewscript.cpp, LLGridManager::isInOpenSim), OSSL's words too.</summary>
    public static bool IsSecondLifeGrid(string? loginUri) =>
        Viewer.Core.Session.GridPolicy.KindOf(loginUri) == Viewer.Core.Session.GridKind.SecondLife;

    /// <summary>A script listed inside an object as the editor's access to it; null when the item is not a script (asset type 10). The item's
    /// flags carry the Lua subtype (low byte 1) and the LSL-on-Lua bit, as an inventory item's do, so a save made before the region has said
    /// how the script runs keeps its Lua target.</summary>
    public static ScriptAccess? AccessFor(Viewer.Core.Session.TaskItem item, Guid objectId, uint localId, Guid agent) =>
        item.AssetType != 10 ? null
            : new ScriptAccess(item.ItemId, item.AssetId, agent,
                new NotecardPermissions(item.CreatorId, item.OwnerId, item.BaseMask, item.OwnerMask, item.GroupMask, item.EveryoneMask, item.NextOwnerMask),
                false, objectId, localId, LuaSubtype: (item.Flags & 0xFF) == 1, LslOnLuau: (item.Flags & ScriptAccess.LslOnLuauFlag) != 0);
}
