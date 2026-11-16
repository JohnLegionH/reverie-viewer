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
    private string Pristine { get; set; } = "";

    public ScriptEditing(ScriptAccess access, string name)
    {
        Access = access;
        Name = name;
        View = ScriptRules.ViewOf(access);
        Target = ScriptTarget.Mono;
    }

    public ScriptAccess Access { get; private set; }
    public string Name { get; }
    public ScriptView View { get; }
    public bool InObject => Access.InObject;

    /// <summary>The text as the window holds it now.</summary>
    public string Text { get; set; } = "";

    /// <summary>The text has been read from the grid (or the script is new and empty).</summary>
    public bool Loaded { get; private set; }

    /// <summary>The window's status line, as a string-table key and its argument; null key: nothing to say.</summary>
    public string? StatusKey { get; private set; }
    public string StatusArg { get; private set; } = "";

    public IReadOnlyList<ScriptMessage> Messages { get; private set; } = [];

    public bool Saving { get; private set; }

    /// <summary>The compile target sent with a save. The official viewer sends Mono for a script in the inventory and the Mono tick's choice
    /// for one inside an object (llviewerassetupload.cpp; floater_live_lsleditor.xml "mono", ticked to start with).</summary>
    public ScriptTarget Target { get; set; }

    /// <summary>The running state of a script inside an object, once the region has answered; null until then.</summary>
    public bool? Running { get; private set; }

    /// <summary>The changes since the last load or save. A window that cannot edit has none.</summary>
    public bool IsDirty => false;

    /// <summary>The text can be typed into.</summary>
    public bool CanEdit => false;

    /// <summary>Save is offered: there is something to save and nothing is being saved.</summary>
    public bool CanSave => false;

    /// <summary>The Running tick may be used: a script inside an object whose state the region has told, and not while saving
    /// (llpreviewscript.cpp LLLiveLSLEditor::draw).</summary>
    public bool CanSetRunning => false;

    /// <summary>The fetched text, or why there is none.</summary>
    public void TextFetched(AssetFetchResult fetched) { }

    /// <summary>The script has no text yet (a new script with no asset): an empty editor.</summary>
    public void LoadedEmpty() { }

    /// <summary>The script's text as bytes to send, and the window marked as saving; null when it may not be saved now.</summary>
    public byte[]? BeginSave() => null;

    /// <summary>The grid's answer to a save.</summary>
    public void SaveFinished(ScriptSaveResult result) { }

    /// <summary>The region's answer about the running state.</summary>
    public void RunningKnown(bool running, bool mono) { }

    /// <summary>The Running tick changed by the person: true when the change is to be sent.</summary>
    public bool SetRunning(bool running) => false;

    /// <summary>Closing the window: true when it may close now; false when the question must be asked first.</summary>
    public bool MayClose() => false;

    /// <summary>The answer to the unsaved-changes question (llpreviewscript.cpp handleSaveChangesDialog: Save saves and closes once saved,
    /// Don't Save closes, Cancel keeps the window).</summary>
    public ScriptCloseStep CloseAnswered(ScriptCloseAnswer answer) => ScriptCloseStep.Stay;

    /// <summary>A save asked for by the question has finished well: the window closes now.</summary>
    public bool CloseAfterSave { get; private set; }

    /// <summary>
    /// Where a click on message <paramref name="index"/> puts the caret: its line and column (counted from 0, as the official viewer's
    /// setCursor takes them), kept inside the text; null when the message names no place.
    /// </summary>
    public (int Line, int Column)? CaretFor(int index) => null;

    /// <summary>The line a go-to-line entry asks for, counted from 0, kept inside the text; null when the entry is not a line number. The entry
    /// is the number shown beside the line (the editor numbers its lines from 1).</summary>
    public int? GoToLine(string entry) => null;

    /// <summary>The next place <paramref name="find"/> stands at or after <paramref name="from"/>, going round to the start; -1 when nowhere
    /// (llpreviewscript.cpp LLFloaterScriptSearch: case-insensitive unless asked, searching on from the caret and round).</summary>
    public static int FindNext(string text, string find, int from, bool matchCase) => -1;

    /// <summary>Every <paramref name="find"/> replaced: the new text and how many were replaced.</summary>
    public static (string Text, int Count) ReplaceAll(string text, string find, string replacement, bool matchCase) => (text, 0);

    /// <summary>A script listed inside an object as the editor's access to it; null when the item is not a script (asset type 10).</summary>
    public static ScriptAccess? AccessFor(Viewer.Core.Session.TaskItem item, Guid objectId, uint localId, Guid agent) => null;
}
