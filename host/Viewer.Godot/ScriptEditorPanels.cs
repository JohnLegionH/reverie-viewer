// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Viewer.Core.Inventory;
using Viewer.Shell;
using Viewer.Shell.Inventory;

namespace Viewer.Godot;

/// <summary>
/// Script editor windows, after the official viewer's script editors (indra/newview/llpreviewscript.cpp): LLPreviewLSL for a script in the
/// inventory (Save; no running state) and LLLiveLSLEditor for a script inside an object (Save, Running, Reset and Mono, as
/// floater_live_lsleditor.xml has them). Each window holds the code with line numbers, LSL's words coloured and explained on hover (and
/// OSSL's on OpenSim grids, as Firestorm does), find and replace, go to line, the text size, undo and redo, the compiler's errors under the
/// code (a click puts the caret on the place), and the official unsaved-changes question when a changed script is closed. Several scripts
/// can be open; opening one already open brings its window to the front. Windows are a small pool of floaters, reused oldest first.
/// Built from the shared theme only. Main thread only.
/// </summary>
public sealed class ScriptEditorFeature
{
    /// <summary>How many script windows can be open at once; opening one more reuses the window at the back.</summary>
    public const int MaxWindows = 8;

    private readonly ShellRoot _shell;
    private readonly StringTable _strings;
    private readonly List<(Guid Object, Guid Item)> _open = new();
    private readonly ScriptEditorPanel?[] _panels = new ScriptEditorPanel?[MaxWindows];
    private readonly (Guid Object, Guid Item)[] _keyIn = new (Guid, Guid)[MaxWindows];
    private IScriptTransport? _transport;
    private bool _openSim = true;
    private CancellationTokenSource _cancel = new();
    private readonly Action<string, string?>? _log;

    public ScriptEditorFeature(ShellRoot shell, Action<string, string?>? log = null)
    {
        _shell = shell;
        _strings = shell.Shell.Strings;
        _log = log;
        shell.Shell.Floaters.Closed += OnClosed;
    }

    public static string FloaterId(int slot) => "script." + (slot + 1);

    /// <summary>The windows open, front last.</summary>
    public IReadOnlyList<ScriptEditorPanel> OpenPanels =>
        _open.Select(k => SlotOf(k) is int s ? _panels[s] : null).OfType<ScriptEditorPanel>().ToList();

    public ScriptEditorPanel? Front => OpenPanels.LastOrDefault();

    /// <summary>A session's script transport and its kind of grid (null: logged out; every window closes).</summary>
    public void Bind(IScriptTransport? transport, bool openSim)
    {
        _cancel.Cancel();
        _cancel = new CancellationTokenSource();
        for (int i = 0; i < MaxWindows; i++)
            if (_keyIn[i] != default)
                _shell.Shell.Floaters.Close(FloaterId(i), force: true);
        _transport = transport;
        _openSim = openSim;
    }

    /// <summary>Opens an inventory script by its item id, or brings its window to the front. False when it is not a script the grid knows.</summary>
    public bool OpenInventory(Guid itemId, string name)
    {
        if (_transport?.ScriptAccessOf(itemId) is not { } access)
            return false;
        Open(access, name);
        return true;
    }

    /// <summary>Closes the window of a script inside an object, unsaved changes and all (the script was deleted; llpanelobjectinventory.cpp
    /// LLTaskLSLBridge::removeItem hides its editor).</summary>
    public void CloseItem(Guid objectId, Guid itemId)
    {
        if (SlotOf((objectId, itemId)) is int open)
            _shell.Shell.Floaters.Close(FloaterId(open), force: true);
    }

    /// <summary>Opens a script: its window is shown at once and the text and running state are asked for.</summary>
    public ScriptEditorPanel Open(ScriptAccess access, string name)
    {
        var key = (access.ObjectId, access.ItemId);
        if (SlotOf(key) is int open)
        {
            _open.Remove(key);
            _open.Add(key);
            _shell.Shell.Floaters.Open(FloaterId(open));
            _shell.Shell.Floaters.Focus(FloaterId(open));
            return _panels[open]!;
        }
        int slot = FreeSlot();
        if (_keyIn[slot] != default)
        {
            _open.Remove(_keyIn[slot]);
            _shell.Shell.Floaters.Close(FloaterId(slot), force: true);
        }
        if (_panels[slot] is null)
        {
            var panel = new ScriptEditorPanel(_strings, slot);
            _panels[slot] = panel;
            string id = FloaterId(slot);
            _shell.AddFloater(new FloaterSpec(id, "script.title", new UiSize(640, 560), new UiSize(420, 320)), panel);
            _shell.Shell.Floaters.SetCloseGuard(id, () => panel.MayClose(() => _shell.Shell.Floaters.Close(id, force: true)));
            panel.Closing = () => _shell.Shell.Floaters.Close(id, force: true);
        }
        _keyIn[slot] = key;
        _open.Add(key);
        var editing = new ScriptEditing(access, name);
        var p = _panels[slot]!;
        p.Show(editing, _transport, LslKeywords.Shipped(_openSim), _cancel.Token);
        _shell.SetFloaterTitle(FloaterId(slot), string.IsNullOrEmpty(name) ? _strings.Get("script.title") : name);
        _shell.Shell.Floaters.Open(FloaterId(slot));
        _shell.Shell.Floaters.Focus(FloaterId(slot));
        _log?.Invoke(access.InObject ? "object" : "inventory", editing.View.ToString());
        if (editing.View != ScriptView.NotAllowed && _transport is { } t)
            p.Load(t, _cancel.Token);
        else if (_transport is null)
            p.ShowFixture();
        return p;
    }

    private int? SlotOf((Guid, Guid) key)
    {
        for (int i = 0; i < MaxWindows; i++)
            if (_keyIn[i] == key && key != default)
                return i;
        return null;
    }

    private int FreeSlot()
    {
        for (int i = 0; i < MaxWindows; i++)
            if (_keyIn[i] == default)
                return i;
        return SlotOf(_open[0]) ?? 0;
    }

    private void OnClosed(string floaterId)
    {
        for (int i = 0; i < MaxWindows; i++)
        {
            if (FloaterId(i) != floaterId || _keyIn[i] == default)
                continue;
            _open.Remove(_keyIn[i]);
            _keyIn[i] = default;
            _panels[i]?.Clear();
        }
    }
}

/// <summary>The code field: a code editor whose tooltip is the hovered word's own (the official keyword file's tooltip).</summary>
public partial class ScriptCodeEdit : CodeEdit
{
    public LslKeywords? Words;

    public override string _GetTooltip(Vector2 atPosition)
    {
        string word = GetWordAtPos(atPosition);
        return word.Length > 0 && Words?.Find(word) is { } w && w.Tooltip.Length > 0 ? w.Tooltip : "";
    }
}

/// <summary>One script editor window's content.</summary>
public partial class ScriptEditorPanel : VBoxContainer
{
    private readonly StringTable _s;
    private ScriptEditing? _editing;
    private IScriptTransport? _transport;
    private CancellationToken _token;
    private bool _syncing;
    private int _fontStep;

    public Button SaveButton { get; }
    public CheckBox RunningCheck { get; }
    public Button ResetButton { get; }
    public CheckBox MonoCheck { get; }
    public Button FindButton { get; }
    public Button GoToButton { get; }
    public Button SmallerButton { get; }
    public Button LargerButton { get; }
    public HBoxContainer FindBar { get; }
    public LineEdit FindField { get; }
    public LineEdit ReplaceField { get; }
    public CheckBox MatchCase { get; }
    public Button FindNextButton { get; }
    public Button ReplaceButton { get; }
    public Button ReplaceAllButton { get; }
    public HBoxContainer GoToBar { get; }
    public LineEdit GoToField { get; }
    public Button GoButton { get; }
    public ScriptCodeEdit Code { get; }
    public ItemList Errors { get; }
    public Label PositionLabel { get; }
    public Label StatusLabel { get; }
    public HBoxContainer UnsavedPrompt { get; }
    public Button PromptSave { get; }
    public Button PromptDontSave { get; }
    public Button PromptCancel { get; }

    /// <summary>Closes the window without asking again (set by the feature).</summary>
    public Action? Closing;

    public ScriptEditing? Editing => _editing;

    public ScriptEditorPanel(StringTable strings, int slot)
    {
        _s = strings;
        Name = "script_panel_" + (slot + 1);
        AccessibilityName = strings.Get("script.panel");

        var bar = new HBoxContainer { Name = "toolbar" };
        SaveButton = Btn("save", "script.save", Save);
        SaveButton.ThemeTypeVariation = UiTheme.PrimaryButton;
        bar.AddChild(SaveButton);
        RunningCheck = new CheckBox { Name = "running", Text = strings.Get("script.running") };
        RunningCheck.AccessibilityName = RunningCheck.Text;
        RunningCheck.Toggled += OnRunningToggled;
        bar.AddChild(RunningCheck);
        ResetButton = Btn("reset", "script.reset", Reset);
        bar.AddChild(ResetButton);
        MonoCheck = new CheckBox { Name = "mono", Text = strings.Get("script.mono"), ButtonPressed = true };
        MonoCheck.AccessibilityName = MonoCheck.Text;
        MonoCheck.Toggled += on => { if (!_syncing && _editing is { } e) e.Target = on ? ScriptTarget.Mono : ScriptTarget.Lsl2; };
        bar.AddChild(MonoCheck);
        bar.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        FindButton = Btn("find", "script.find", () => { FindBar!.Visible = !FindBar.Visible; if (FindBar.Visible) FindField!.GrabFocus(); });
        bar.AddChild(FindButton);
        GoToButton = Btn("goto", "script.goto", () => { GoToBar!.Visible = !GoToBar.Visible; if (GoToBar.Visible) GoToField!.GrabFocus(); });
        bar.AddChild(GoToButton);
        SmallerButton = Btn("font_smaller", "script.font_smaller", () => StepFont(-1));
        SmallerButton.AccessibilityName = strings.Get("script.font_smaller.name");
        bar.AddChild(SmallerButton);
        LargerButton = Btn("font_larger", "script.font_larger", () => StepFont(1));
        LargerButton.AccessibilityName = strings.Get("script.font_larger.name");
        bar.AddChild(LargerButton);
        AddChild(bar);

        FindBar = new HBoxContainer { Name = "find_bar", Visible = false };
        FindField = new LineEdit { Name = "find_field", PlaceholderText = strings.Get("script.find.field"), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        FindField.AccessibilityName = FindField.PlaceholderText;
        FindField.TextSubmitted += _ => FindNext();
        FindBar.AddChild(FindField);
        ReplaceField = new LineEdit { Name = "replace_field", PlaceholderText = strings.Get("script.replace.field"), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        ReplaceField.AccessibilityName = ReplaceField.PlaceholderText;
        FindBar.AddChild(ReplaceField);
        MatchCase = new CheckBox { Name = "match_case", Text = strings.Get("script.match_case") };
        MatchCase.AccessibilityName = MatchCase.Text;
        FindBar.AddChild(MatchCase);
        FindNextButton = Btn("find_next", "script.find_next", () => FindNext());
        FindBar.AddChild(FindNextButton);
        ReplaceButton = Btn("replace_one", "script.replace_one", ReplaceOne);
        FindBar.AddChild(ReplaceButton);
        ReplaceAllButton = Btn("replace_all", "script.replace_all", ReplaceAll);
        FindBar.AddChild(ReplaceAllButton);
        AddChild(FindBar);

        GoToBar = new HBoxContainer { Name = "goto_bar", Visible = false };
        GoToField = new LineEdit { Name = "goto_field", PlaceholderText = strings.Get("script.goto.field"), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        GoToField.AccessibilityName = GoToField.PlaceholderText;
        GoToField.TextSubmitted += _ => GoTo();
        GoToBar.AddChild(GoToField);
        GoButton = Btn("goto_go", "script.goto.go", GoTo);
        GoToBar.AddChild(GoButton);
        AddChild(GoToBar);

        Code = new ScriptCodeEdit
        {
            Name = "code", SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill,
            GuttersDrawLineNumbers = true, AutoBraceCompletionEnabled = false, HighlightCurrentLine = true,
            ScrollPastEndOfFile = false, CaretBlink = false, WrapMode = TextEdit.LineWrappingMode.None,
        };
        Code.AccessibilityName = strings.Get("script.code");
        Code.TextChanged += OnTextChanged;
        Code.CaretChanged += ShowPosition;
        AddChild(Code);

        Errors = new ItemList { Name = "errors", CustomMinimumSize = new Vector2(0, 72), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        Errors.AccessibilityName = strings.Get("script.errors");
        Errors.ItemSelected += i => ErrorClicked((int)i);
        AddChild(Errors);

        var foot = new HBoxContainer { Name = "foot" };
        StatusLabel = new Label { Name = "status", SizeFlagsHorizontal = SizeFlags.ExpandFill, AutowrapMode = TextServer.AutowrapMode.WordSmart, ThemeTypeVariation = UiTheme.MutedLabel, CustomMinimumSize = new Vector2(120, 0) };
        foot.AddChild(StatusLabel);
        PositionLabel = new Label { Name = "position", ThemeTypeVariation = UiTheme.MutedLabel };
        foot.AddChild(PositionLabel);
        AddChild(foot);

        UnsavedPrompt = new HBoxContainer { Name = "unsaved", Visible = false };
        UnsavedPrompt.AddChild(new Label { Name = "question", Text = strings.Get("script.unsaved"), SizeFlagsHorizontal = SizeFlags.ExpandFill });
        PromptSave = Btn("prompt_save", "script.unsaved.save", () => Answer(ScriptCloseAnswer.Save));
        PromptSave.ThemeTypeVariation = UiTheme.PrimaryButton;
        UnsavedPrompt.AddChild(PromptSave);
        PromptDontSave = Btn("prompt_dont_save", "script.unsaved.dont_save", () => Answer(ScriptCloseAnswer.DontSave));
        UnsavedPrompt.AddChild(PromptDontSave);
        PromptCancel = Btn("prompt_cancel", "script.unsaved.cancel", () => Answer(ScriptCloseAnswer.Cancel));
        UnsavedPrompt.AddChild(PromptCancel);
        AddChild(UnsavedPrompt);

        UiTheme.Changed += ApplyTheme;
        TreeExiting += () => UiTheme.Changed -= ApplyTheme;
        Clear();
    }

    private Button Btn(string name, string key, Action pressed)
    {
        var b = new Button { Name = name, Text = _s.Get(key) };
        b.AccessibilityName = b.Text;
        b.Pressed += pressed;
        return b;
    }

    /// <summary>Shows a script in this window (its text arrives with <see cref="Load"/>).</summary>
    public void Show(ScriptEditing editing, IScriptTransport? transport, LslKeywords words, CancellationToken token)
    {
        Clear();
        _editing = editing;
        _transport = transport;
        _token = token;
        Code.Words = words;
        ApplyTheme();
        if (editing.View != ScriptView.NotAllowed)
            StatusLabel.Text = _s.Get("script.loading");
        Sync();
    }

    /// <summary>Asks for the text, and for a script inside an object its running state.</summary>
    public void Load(IScriptTransport transport, CancellationToken token)
    {
        if (_editing is not { } e)
            return;
        if (e.Access.AssetId == Guid.Empty)
        {
            e.LoadedEmpty();
            ShowText();
        }
        else
        {
            _ = transport.FetchScriptAsync(e.Access, token).ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully && !token.IsCancellationRequested)
                    Callable.From(() => { if (_editing == e) { e.TextFetched(t.Result); ShowText(); } }).CallDeferred();
            }, TaskScheduler.Default);
        }
        if (e.InObject)
            AskRunning(transport, e, token);
    }

    /// <summary>A script with no transport (the pictures and the automation's fixtures): its text is set by the caller.</summary>
    public void ShowFixture() => Sync();

    /// <summary>Sets the text as read, for the pictures and the automation's fixtures.</summary>
    public void ShowText(string text)
    {
        if (_editing is null)
            return;
        _editing.TextFetched(Viewer.Core.Assets.AssetFetchResult.Ok(System.Text.Encoding.UTF8.GetBytes(text)));
        ShowText();
    }

    private void AskRunning(IScriptTransport transport, ScriptEditing e, CancellationToken token)
    {
        _ = transport.GetScriptRunningAsync(e.Access, token).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully && t.Result is { } r && !token.IsCancellationRequested)
                Callable.From(() => { if (_editing == e) { e.RunningKnown(r.Running, r.Mono); Sync(); } }).CallDeferred();
        }, TaskScheduler.Default);
    }

    private void ShowText()
    {
        if (_editing is not { } e)
            return;
        _syncing = true;
        Code.Text = e.Text;
        Code.ClearUndoHistory();
        _syncing = false;
        Sync();
    }

    public void Clear()
    {
        _editing = null;
        _syncing = true;
        Code.Text = string.Empty;
        Code.ClearUndoHistory();
        _syncing = false;
        Errors.Clear();
        StatusLabel.Text = string.Empty;
        PositionLabel.Text = string.Empty;
        UnsavedPrompt.Visible = false;
        FindBar.Visible = GoToBar.Visible = false;
    }

    /// <summary>The colours of the words, comments and strings from the theme, and the text size.</summary>
    private void ApplyTheme()
    {
        var t = UiTheme.Tokens;
        var h = new CodeHighlighter
        {
            NumberColor = ShellTheme.C(t.Text), SymbolColor = ShellTheme.C(t.Text), FunctionColor = ShellTheme.C(t.Text),
            MemberVariableColor = ShellTheme.C(t.Text),
        };
        if (Code.Words is { } words)
            foreach (var w in words.Words.Values)
                h.AddKeywordColor(w.Word, ShellTheme.Syntax(t, w.Kind));
        h.AddColorRegion("//", "", ShellTheme.SyntaxComment(t), lineOnly: true);
        h.AddColorRegion("/*", "*/", ShellTheme.SyntaxComment(t));
        h.AddColorRegion("\"", "\"", ShellTheme.SyntaxString(t));
        Code.SyntaxHighlighter = h;
        ApplyFont();
    }

    /// <summary>The text size: the theme's body size, moved by the person's steps (two points a step, as a font-size choice moves it).</summary>
    private void ApplyFont()
    {
        int size = Math.Max(UiTheme.Tokens.FontCaption, UiTheme.Tokens.FontBody + _fontStep * 2);
        Code.AddThemeFontSizeOverride("font_size", size);
    }

    /// <summary>One step smaller or larger, at most four either way.</summary>
    public void StepFont(int step)
    {
        _fontStep = Math.Clamp(_fontStep + step, -2, 4);
        ApplyFont();
    }

    public int FontStep => _fontStep;

    /// <summary>The controls as the model says: editable or not, Save, the object-only controls, the status line and the errors.</summary>
    private void Sync()
    {
        if (_editing is not { } e)
            return;
        _syncing = true;
        Code.Editable = e.CanEdit;
        SaveButton.Disabled = !e.CanSave;
        RunningCheck.Visible = ResetButton.Visible = MonoCheck.Visible = e.InObject;
        RunningCheck.Disabled = !e.CanSetRunning;
        RunningCheck.ButtonPressed = e.Running ?? false;
        ResetButton.Disabled = e.View != ScriptView.Editable || e.Saving;
        MonoCheck.ButtonPressed = e.Target == ScriptTarget.Mono;
        MonoCheck.Disabled = !e.CanEdit || e.Saving;
        ReplaceButton.Disabled = ReplaceAllButton.Disabled = !e.CanEdit;
        _syncing = false;
        StatusLabel.Text = e.StatusKey is { } key ? _s.Get(key, e.StatusArg) : "";
        if (Errors.ItemCount != e.Messages.Count || Enumerable.Range(0, Errors.ItemCount).Any(i => Errors.GetItemText(i) != e.Messages[i].Text))
        {
            Errors.Clear();
            foreach (var m in e.Messages)
                Errors.AddItem(m.Text);
        }
        ShowPosition();
    }

    private void ShowPosition() => PositionLabel.Text = _s.Get("script.position", Code.GetCaretLine() + 1, Code.GetCaretColumn() + 1);

    private void OnTextChanged()
    {
        if (_syncing || _editing is not { } e)
            return;
        e.Text = Code.Text;
        SaveButton.Disabled = !e.CanSave;
    }

    /// <summary>Save (the button, and Ctrl+S as the official editor has it).</summary>
    public void Save()
    {
        if (_editing is not { } e || _transport is not { } t || e.BeginSave() is not { } bytes)
            return;
        Sync();
        var token = _token;
        bool running = e.Running ?? true;
        _ = t.SaveScriptAsync(e.Access, bytes, e.Target, running, token).ContinueWith(r =>
        {
            var result = r.IsCompletedSuccessfully ? r.Result : ScriptSaveResult.Of(ScriptSaveStatus.Failed, "failed");
            if (!token.IsCancellationRequested)
                Callable.From(() => Saved(e, result)).CallDeferred();
        }, TaskScheduler.Default);
    }

    private void Saved(ScriptEditing e, ScriptSaveResult result)
    {
        if (_editing != e)
            return;
        e.SaveFinished(result);
        Sync();
        if (e.CloseAfterSave)
            Closing?.Invoke();
        else if (e.InObject && result.Status == ScriptSaveStatus.Saved && _transport is { } t)
            AskRunning(t, e, _token);   // the region restarts a saved script as the Running tick says
    }

    private void OnRunningToggled(bool on)
    {
        if (_syncing || _editing is not { } e)
            return;
        if (e.SetRunning(on))
            _transport?.SetScriptRunning(e.Access, on);
        Sync();
    }

    private void Reset()
    {
        if (_editing is { View: ScriptView.Editable } e && _transport?.ResetScript(e.Access) == true)
            StatusLabel.Text = _s.Get("script.reset_sent");
    }

    /// <summary>A click on an error: the caret goes to its line and column (llpreviewscript.cpp onErrorList).</summary>
    public void ErrorClicked(int index)
    {
        if (_editing?.CaretFor(index) is not { } at)
            return;
        Code.Deselect();
        Code.SetCaretLine(at.Line);
        Code.SetCaretColumn(at.Column);
        Code.GrabFocus();
        ShowPosition();
    }

    private void GoTo()
    {
        if (_editing?.GoToLine(GoToField.Text) is not { } line)
            return;
        Code.Deselect();
        Code.SetCaretLine(line);
        Code.SetCaretColumn(0);
        Code.GrabFocus();
        ShowPosition();
    }

    private int CaretIndex()
    {
        int line = Code.GetCaretLine(), index = 0;
        for (int i = 0; i < line; i++)
            index += Code.GetLine(i).Length + 1;
        return index + Code.GetCaretColumn();
    }

    private (int Line, int Column) PlaceOf(int index)
    {
        string text = Code.Text;
        int line = 0, start = 0;
        for (int i = 0; i < index && i < text.Length; i++)
            if (text[i] == '\n') { line++; start = i + 1; }
        return (line, index - start);
    }

    /// <summary>Find next: from the caret on, round to the start; the match is selected. False when nothing matched.</summary>
    public bool FindNext()
    {
        int at = ScriptEditing.FindNext(Code.Text, FindField.Text, CaretIndex(), MatchCase.ButtonPressed);
        if (at < 0)
        {
            StatusLabel.Text = _s.Get("script.not_found");
            return false;
        }
        var from = PlaceOf(at);
        var to = PlaceOf(at + FindField.Text.Length);
        Code.Select(from.Line, from.Column, to.Line, to.Column);
        Code.SetCaretLine(to.Line);
        Code.SetCaretColumn(to.Column);
        ShowPosition();
        return true;
    }

    private void ReplaceOne()
    {
        if (_editing is not { CanEdit: true })
            return;
        string sel = Code.GetSelectedText();
        bool matches = sel.Length > 0 && string.Equals(sel, FindField.Text, MatchCase.ButtonPressed ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
        if (matches)
            Code.InsertTextAtCaret(ReplaceField.Text);   // replaces the selection, as one undoable step
        FindNext();
    }

    private void ReplaceAll()
    {
        if (_editing is not { CanEdit: true })
            return;
        var (text, n) = ScriptEditing.ReplaceAll(Code.Text, FindField.Text, ReplaceField.Text, MatchCase.ButtonPressed);
        if (n > 0)
        {
            Code.BeginComplexOperation();
            Code.SelectAll();
            Code.InsertTextAtCaret(text);
            Code.EndComplexOperation();
        }
        StatusLabel.Text = _s.Get("script.replaced", n);
    }

    /// <summary>Ctrl+S saves and Ctrl+F opens find while the window has the focus (llpreviewscript.cpp handleKeyHere).</summary>
    public override void _ShortcutInput(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true, Echo: false, CtrlPressed: true } k || !IsVisibleInTree() || GetViewport().GuiGetFocusOwner() is not { } focus || !IsAncestorOf(focus))
            return;
        if (k.Keycode == Key.S)
        {
            Save();
            AcceptEvent();
        }
        else if (k.Keycode == Key.F)
        {
            FindBar.Visible = true;
            FindField.GrabFocus();
            AcceptEvent();
        }
    }

    /// <summary>The close guard: true when the window may close; otherwise the question is shown and the window stays.</summary>
    public bool MayClose(Action close)
    {
        if (_editing is not { } e || e.MayClose())
            return true;
        UnsavedPrompt.Visible = true;
        PromptSave.GrabFocus();
        return false;
    }

    /// <summary>The answer to the unsaved-changes question.</summary>
    public void Answer(ScriptCloseAnswer answer)
    {
        if (_editing is not { } e)
            return;
        UnsavedPrompt.Visible = false;
        switch (e.CloseAnswered(answer))
        {
            case ScriptCloseStep.Close:
                Closing?.Invoke();
                break;
            case ScriptCloseStep.SaveThenClose:
                SendSave(e);
                break;
        }
    }

    /// <summary>The save the question started (the model already marked it as saving).</summary>
    private void SendSave(ScriptEditing e)
    {
        Sync();
        if (_transport is not { } t)
            return;
        var token = _token;
        var bytes = System.Text.Encoding.UTF8.GetBytes(e.Text);
        _ = t.SaveScriptAsync(e.Access, bytes, e.Target, e.Running ?? true, token).ContinueWith(r =>
        {
            var result = r.IsCompletedSuccessfully ? r.Result : ScriptSaveResult.Of(ScriptSaveStatus.Failed, "failed");
            if (!token.IsCancellationRequested)
                Callable.From(() => Saved(e, result)).CallDeferred();
        }, TaskScheduler.Default);
    }

    /// <summary>Shows a running state without a grid (the pictures).</summary>
    public void ShowRunning(bool running, bool mono)
    {
        _editing?.RunningKnown(running, mono);
        Sync();
    }

    /// <summary>Shows a save's answer without a grid (the pictures).</summary>
    public void ShowAnswer(ScriptSaveResult result)
    {
        if (_editing is not { } e)
            return;
        e.Text = Code.Text;
        if (e.BeginSave() is null && !e.Saving)
            return;
        e.SaveFinished(result);
        Sync();
    }
}

/// <summary>
/// The Build › Scripts progress window, as the official floater_script_queue.xml (read as behaviour): its title names the action
/// (Recompilation Progress, Reset Progress, Set Running Progress, Set Not Running Progress), a list of the queue's lines, and Close, which
/// is usable once the queue is done (the official close button is enabled at "Done."). Built from standard controls styled through the
/// Theme alone; the lines are read from the queue on a timer while the window shows. Main thread only.
/// </summary>
public sealed class ScriptQueueWindow
{
    public const string FloaterId = "script_queue";

    private readonly StringTable _s;
    private readonly ItemList _lines;
    private readonly Label _count;
    private int _shown;

    public ScriptQueueWindow(StringTable strings, Action close)
    {
        _s = strings;
        Root = new VBoxContainer { Name = "script_queue", SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        _lines = new ItemList { Name = "queue_output", SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _lines.AccessibilityName = strings.Get("queue.list");
        Root.AddChild(_lines);
        _count = new Label { Name = "queue_count", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        Root.AddChild(_count);
        Close = new Button { Name = "close", Text = strings.Get("queue.close"), SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd };
        Close.AccessibilityName = Close.Text;
        Close.Pressed += close;
        Root.AddChild(Close);
    }

    public VBoxContainer Root { get; }
    public Button Close { get; }

    /// <summary>The queue shown, or null for a refusal (then <see cref="ShowRefusal"/> gave the lines).</summary>
    public Viewer.Shell.Build.ScriptQueue? Queue { get; private set; }

    public void Show(Viewer.Shell.Build.ScriptQueue queue)
    {
        Queue = queue;
        _lines.Clear();
        _shown = 0;
        _count.Text = "";
        Close.Disabled = true;
        Refresh();
    }

    /// <summary>The official refusal (Cannot...SelectObjects...), shown as the window's only lines.</summary>
    public void ShowRefusal(string text)
    {
        Queue = null;
        _lines.Clear();
        foreach (var line in text.Split('\n'))
            if (line.Length > 0)
                _lines.AddItem(line);
        _shown = 0;
        _count.Text = "";
        Close.Disabled = false;
    }

    /// <summary>Adds the lines the queue has added since the last call.</summary>
    public void Refresh()
    {
        if (Queue is not { } q)
            return;
        var lines = q.Lines;
        for (; _shown < lines.Count; _shown++)
        {
            var line = lines[_shown];
            if (line.Key == "queue.summary")
                _count.Text = Viewer.Shell.Build.ScriptQueue.Format(line, _s);
            else
                _lines.AddItem(Viewer.Shell.Build.ScriptQueue.Format(line, _s));
        }
        Close.Disabled = !q.Done;
    }

    /// <summary>The lines shown and the count line, for the automation.</summary>
    public IReadOnlyList<string> Text => Enumerable.Range(0, _lines.ItemCount).Select(i => _lines.GetItemText(i)).Append(_count.Text).ToList();
}
