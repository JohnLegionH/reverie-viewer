// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Godot;
using Viewer.Core.Actions;
using Viewer.Core.Inventory;
using Viewer.Shell;
using Viewer.Shell.Inventory;

namespace Viewer.Godot;

/// <summary>
/// Notecard windows, after the official viewer's notecard window (indra/newview/llpreviewnotecard.cpp, floater_preview_notecard.xml): the
/// card's name as the title, its description, and the text, which the user edits when the card's permissions allow and reads otherwise
/// (Save hidden, a "read only" note shown, and the text selectable only when the card may be copied). Undo, Redo and Find work in the text;
/// the line and column of the caret and the text's size against the 65,536-byte limit show under it. Inventory items dropped on the text
/// become embedded items; a click on one opens it or offers a copy (llviewertexteditor.cpp). Close with unsaved changes asks Save,
/// Don't Save or Cancel. Under the text a row for each embedded item of the saved card: a landmark shows where it leads and offers a
/// teleport, and "Copy to inventory" is enabled where the rules allow. Several cards can be open at once; opening one already open brings
/// its window to the front. Windows are a small pool of floaters that are reused, oldest first. No layout file or artwork of any viewer is used.
/// </summary>
public sealed class NotecardFeature
{
    /// <summary>How many notecard windows can be open at once; opening one more reuses the window at the back.</summary>
    public const int MaxWindows = 8;

    private readonly ShellRoot _shell;
    private readonly StringTable _strings;
    private readonly NotecardWindowSet _set = new();
    private readonly Dictionary<Guid, int> _slotOf = new();
    private readonly NotecardPanel?[] _panels = new NotecardPanel?[MaxWindows];
    private readonly Guid[] _itemIn = new Guid[MaxWindows];
    private NotecardReader? _reader;
    private INotecardSaveTransport? _saves;
    private Func<ActionIntent, ActionResult>? _submit;
    private CancellationTokenSource _cancel = new();
    private readonly Action<string, string?>? _log;

    public NotecardFeature(ShellRoot shell, Action<string, string?>? log = null)
    {
        _log = log;
        _shell = shell;
        _strings = shell.Shell.Strings;
        shell.Shell.Floaters.Closed += OnClosed;
    }

    public static string FloaterId(int slot) => "notecard." + (slot + 1);

    /// <summary>The cards open, front last.</summary>
    public IReadOnlyList<Guid> Open => _set.Open;

    /// <summary>The window showing an item, for the development automation.</summary>
    /// <summary>Shows the grid's confirmation of a save in the status line of every open notecard window.</summary>
    public void SayStatus(string text)
    {
        foreach (var p in _panels)
            if (p is not null && p.IsVisibleInTree())
                p.StatusLabel.Text = text;
    }

    public NotecardPanel? PanelOf(Guid itemId) => _slotOf.TryGetValue(itemId, out int slot) ? _panels[slot] : null;

    /// <summary>The window in front, or null.</summary>
    public NotecardPanel? Front => _set.Open.Count == 0 ? null : PanelOf(_set.Open[^1]);

    /// <summary>Opens an embedded landmark, texture or sound (set by the viewer).</summary>
    public Action<NotecardEmbeddedItem>? OpenEmbedded { get; set; }

    /// <summary>A card was saved: its access as it stands after the save (the object it is in, and the asset the save stored).</summary>
    public event Action<NotecardAccess>? Saved;

    /// <summary>A session's inventory and pipeline (null: logged out; every notecard window closes).</summary>
    public void Bind(InventoryModel? model, INotecardTransport? transport, Func<ActionIntent, ActionResult>? submit, INotecardSaveTransport? saves = null)
    {
        _cancel.Cancel();
        _cancel = new CancellationTokenSource();
        foreach (var item in _set.Open.ToArray())
            Close(item);
        _reader = model is null ? null : new NotecardReader(model, transport);
        _submit = submit;
        _saves = saves;
    }

    /// <summary>Opens an inventory notecard, or brings its window to the front.</summary>
    public void OpenItem(Guid itemId, string name)
    {
        if (_reader is not { } reader)
            return;
        if (!Show(itemId, name, out var panel))
            return;
        panel.ShowLoading();
        var token = _cancel.Token;
        _ = reader.OpenAsync(itemId, token).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully && !token.IsCancellationRequested)
                Callable.From(() => Shown(itemId, t.Result, objectModify: true)).CallDeferred();
        }, TaskScheduler.Default);
    }

    /// <summary>Opens a notecard inside an object (llpanelobjectinventory.cpp LLTaskNotecardBridge::openItem), editable when the card and the
    /// object may be modified, and saved back into the object; or brings its window to the front.</summary>
    public void OpenTask(Viewer.Core.Session.TaskItem item, Guid objectId, Guid agent, bool objectModify, INotecardTransport? transport)
    {
        if (_reader is null)
            return;
        if (!Show(item.ItemId, item.Name, out var panel))
            return;
        panel.ShowLoading();
        var token = _cancel.Token;
        _ = NotecardReader.OpenTaskAsync(transport, item, objectId, agent, objectModify, token).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully && !token.IsCancellationRequested)
                Callable.From(() => Shown(item.ItemId, t.Result, objectModify)).CallDeferred();
        }, TaskScheduler.Default);
    }

    /// <summary>Closes an item's window when one is open (the item was deleted).</summary>
    public void CloseItem(Guid itemId)
    {
        if (_slotOf.ContainsKey(itemId))
            Close(itemId);
    }

    /// <summary>Shows a card already read (the development automation's fixtures and the pictures), in its own window.</summary>
    public NotecardPanel? ShowOpened(NotecardOpenResult opened, bool objectModify = true)
    {
        if (Show(opened.ItemId, opened.Name, out _) || _slotOf.ContainsKey(opened.ItemId))
            Shown(opened.ItemId, opened, objectModify);
        return PanelOf(opened.ItemId);
    }

    private void Shown(Guid itemId, NotecardOpenResult opened, bool objectModify)
    {
        if (opened.Status != NotecardOpenStatus.Ok)
            _log?.Invoke(opened.Status.ToString(), opened.Error);
        if (PanelOf(itemId) is { } panel)
            panel.Show(new NotecardWindowModel(opened), Submit, objectModify, _saves, _cancel.Token);
    }

    private ActionResult? Submit(ActionIntent intent) => _submit?.Invoke(intent);

    /// <summary>Opens a window for the item (true) or brings its window to the front (false).</summary>
    private bool Show(Guid itemId, string name, out NotecardPanel panel)
    {
        bool fresh = _set.Show(itemId);
        if (!fresh && _slotOf.TryGetValue(itemId, out int open))
        {
            panel = _panels[open]!;
            _shell.Shell.Floaters.Focus(FloaterId(open));
            return false;
        }
        int slot = FreeSlot();
        if (_itemIn[slot] != Guid.Empty && _itemIn[slot] != itemId)
        {
            _set.Close(_itemIn[slot]);
            _slotOf.Remove(_itemIn[slot]);
        }
        if (_panels[slot] is null)
        {
            var made = new NotecardPanel(_strings, slot);
            _panels[slot] = made;
            string id = FloaterId(slot);
            // the official window is 400 by 361, at least 330 by 243 (floater_preview_notecard.xml); this one is a little larger for its foot
            _shell.AddFloater(new FloaterSpec(id, "notecard.title", new UiSize(440, 480), new UiSize(330, 260)), made);
            _shell.Shell.Floaters.SetCloseGuard(id, () => made.MayClose());
            made.Closing = () => _shell.Shell.Floaters.Close(id, force: true);
            made.OpenEmbedded = item => OpenEmbedded?.Invoke(item);
            made.SavedItem = item => Saved?.Invoke(item);
        }
        _itemIn[slot] = itemId;
        _slotOf[itemId] = slot;
        panel = _panels[slot]!;
        _shell.SetFloaterTitle(FloaterId(slot), string.IsNullOrEmpty(name) ? _strings.Get("notecard.title") : name);
        _shell.Shell.Floaters.Open(FloaterId(slot));
        _shell.Shell.Floaters.Focus(FloaterId(slot));
        return true;
    }

    /// <summary>A slot with no window open, else the one at the back.</summary>
    private int FreeSlot()
    {
        for (int i = 0; i < MaxWindows; i++)
            if (_itemIn[i] == Guid.Empty || !_set.Open.Contains(_itemIn[i]))
                return i;
        Guid back = _set.Open.First(id => _slotOf.ContainsKey(id));
        return _slotOf[back];
    }

    private void Close(Guid itemId)
    {
        if (_slotOf.TryGetValue(itemId, out int slot))
            _shell.Shell.Floaters.Close(FloaterId(slot), force: true);
        _set.Close(itemId);
    }

    private void OnClosed(string floaterId)
    {
        for (int i = 0; i < MaxWindows; i++)
        {
            if (FloaterId(i) != floaterId || _itemIn[i] == Guid.Empty)
                continue;
            _set.Close(_itemIn[i]);
            _slotOf.Remove(_itemIn[i]);
            _itemIn[i] = Guid.Empty;
            _panels[i]?.Clear();
        }
    }
}

/// <summary>The text field of the notecard editor: an embedded item is deleted whole by Backspace or Delete, inventory items dropped on it
/// become embedded items, and a click on an item asks the window to open it.</summary>
public partial class NotecardTextEdit : TextEdit
{
    /// <summary>The item ids dropped (with the character offset of the drop).</summary>
    public Action<IReadOnlyList<Guid>, int>? ItemsDropped;
    /// <summary>Whether a drag may be dropped here.</summary>
    public Func<IReadOnlyList<Guid>, bool>? CanTake;
    /// <summary>A click at a character offset.</summary>
    public Action<int>? Clicked;

    /// <summary>The embedded items in the text, as spans over their names (set by the panel).</summary>
    public Func<IReadOnlyList<(int Start, int Length, NotecardEmbeddedItem Item)>>? Items;

    private Vector2 _pressAt;
    private readonly Control _links = new() { Name = "item_links", MouseFilter = MouseFilterEnum.Ignore };

    public override void _Ready()
    {
        // embedded items are drawn as links over the text, underlined in the link colour (the official editor draws each one's
        // name underlined in TextEmbeddedItemColor, llviewertexteditor.cpp LLEmbeddedItemSegment::draw); the engine's code
        // highlighter cannot mark them, as it takes only ASCII symbols to start a colour region
        _links.SetAnchorsPreset(LayoutPreset.FullRect);
        _links.Draw += DrawItemLinks;
        AddChild(_links, false, InternalMode.Front);
        TextChanged += _links.QueueRedraw;
        CaretChanged += _links.QueueRedraw;
        Resized += _links.QueueRedraw;
        GetVScrollBar().ValueChanged += _ => _links.QueueRedraw();
        GetHScrollBar().ValueChanged += _ => _links.QueueRedraw();
        // the item's name and description as its tooltip, as the official segment's handleToolTip shows them
        SetTooltipRequestFunc(Callable.From((string _) => TooltipAt(GetLocalMousePosition())));
    }

    private (int Start, int Length, NotecardEmbeddedItem Item)? SpanAt(Vector2 position)
    {
        var at = GetLineColumnAtPos((Vector2I)position, false);
        if (at.X < 0 || at.Y < 0 || Items is null)
            return null;
        int index = IndexOf(at.Y, at.X);
        foreach (var span in Items())
            if (index >= span.Start && index < span.Start + span.Length)
                return span;
        return null;
    }

    private string TooltipAt(Vector2 position) =>
        SpanAt(position) is { } s ? (string.IsNullOrEmpty(s.Item.Description) ? s.Item.Name : s.Item.Name + "\n" + s.Item.Description) : "";

    /// <summary>One screen row of an embedded item's name as the overlay draws it: the row's rectangle, the text on it and where it lies
    /// in the editor's text.</summary>
    public readonly record struct LinkRun(Rect2 Row, string Text, int Start, int End);

    private void DrawItemLinks()
    {
        if (Items is null)
            return;
        var font = GetThemeFont("font");
        int size = GetThemeFontSize("font_size");
        var link = ShellTheme.C(UiTheme.Tokens.Accent);
        var cover = GetThemeStylebox(Editable ? "normal" : "read_only") is StyleBoxFlat box ? box.BgColor : (Color?)null;
        string text = Text;
        int caret = NotecardEditBuffer.IndexOf(text, GetCaretLine(), GetCaretColumn());
        int selFrom = -1, selTo = -1;
        if (HasSelection())
        {
            selFrom = NotecardEditBuffer.IndexOf(text, GetSelectionFromLine(), GetSelectionFromColumn());
            selTo = NotecardEditBuffer.IndexOf(text, GetSelectionToLine(), GetSelectionToColumn());
        }
        foreach (var run in LinkRuns(text))
        {
            var row = run.Row;
            bool selected = selFrom >= 0 && selFrom < run.End && selTo > run.Start;
            // the caret on this run's edge or inside it stays visible: the run keeps the text box's own drawing, underlined
            bool caretHere = caret >= run.Start - 1 && caret <= run.End + 1;
            if (cover is { } bg && font is not null && !selected && !caretHere)
            {
                _links.DrawRect(row, bg);
                float baseline = row.Position.Y + (row.Size.Y - font.GetHeight(size)) / 2 + font.GetAscent(size);
                _links.DrawString(font, new Vector2(row.Position.X, baseline), run.Text, HorizontalAlignment.Left, -1, size, link);
            }
            float under = row.End.Y - 2;
            _links.DrawLine(new Vector2(row.Position.X, under), new Vector2(row.End.X, under), link, 1);
        }
    }

    /// <summary>Where the overlay draws each embedded item's name, one run per screen row (a long name can wrap); runs scrolled out
    /// of view are left out.</summary>
    public List<LinkRun> LinkRuns() => LinkRuns(Text);

    private List<LinkRun> LinkRuns(string text)
    {
        var runs = new List<LinkRun>();
        if (Items is null)
            return runs;
        var font = GetThemeFont("font");
        int size = GetThemeFontSize("font_size");
        // the text is walked forward once (the spans come in text order), so a long card costs one pass
        int at = 0, line = 0, column = 0;
        void WalkTo(int target)
        {
            for (; at < target && at < text.Length; at++)
            {
                if (text[at] == '\n') { line++; column = 0; continue; }
                if (char.IsHighSurrogate(text[at]) && at + 1 < text.Length)
                    at++;
                column++;
            }
        }
        foreach (var (start, length, _) in Items())
        {
            // one run per screen row: a long name can wrap
            int i = start, end = Math.Min(text.Length, start + length);
            while (i < end)
            {
                WalkTo(i);
                var first = GetRectAtLineColumn(line, column);
                int runStart = i;
                Rect2I last = first;
                int next = i + (char.IsHighSurrogate(text[i]) && i + 1 < end ? 2 : 1);
                while (next < end)
                {
                    WalkTo(next);
                    var r = GetRectAtLineColumn(line, column);
                    if (r.Position.Y != first.Position.Y)
                        break;
                    last = r;
                    next += char.IsHighSurrogate(text[next]) && next + 1 < end ? 2 : 1;
                }
                i = next;
                if (first.Position.X < 0 || first.Position.Y < 0)
                    continue;   // scrolled out of view
                // the run is as wide as its text drawn in the editor's font: the engine's rectangles for the name's characters can end
                // short of it, which left the name's last character outside the link
                float width = last.End.X - first.Position.X;
                if (font is not null)
                    width = Math.Max(width, font.GetStringSize(text[runStart..next], HorizontalAlignment.Left, -1, size).X);
                var row = new Rect2(first.Position, new Vector2(width, first.Size.Y));
                runs.Add(new LinkRun(row, text[runStart..next], runStart, next));
            }
        }
        return runs;
    }

    // the text box counts columns in characters, the text's offsets are UTF-16 units: an emoji is one column and two units
    public int IndexOf(int line, int column) => NotecardEditBuffer.IndexOf(Text, line, column);

    public (int Line, int Column) PlaceOf(int index) => NotecardEditBuffer.PlaceOf(Text, index);

    public int CaretIndex => IndexOf(GetCaretLine(), GetCaretColumn());

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true } k && Editable && !HasSelection())
        {
            int caret = CaretIndex;
            (int Start, int Length)? span = k.Keycode switch
            {
                Key.Backspace => NotecardEditBuffer.TokenSpanAt(Text, caret),
                Key.Delete => NotecardEditBuffer.TokenSpanAt(Text, caret + 1),
                _ => null,
            };
            if (span is { } t)
            {
                var from = PlaceOf(t.Start);
                var to = PlaceOf(t.Start + t.Length);
                BeginComplexOperation();
                Select(from.Line, from.Column, to.Line, to.Column);
                DeleteSelection();
                EndComplexOperation();
                AcceptEvent();
                return;
            }
        }
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } m)
        {
            if (m.Pressed)
                _pressAt = m.Position;
            else if (m.Position.DistanceTo(_pressAt) < 4 && !HasSelection())
            {
                var at = GetLineColumnAtPos((Vector2I)m.Position, false);
                if (at.X >= 0 && at.Y >= 0)
                    Clicked?.Invoke(IndexOf(at.Y, at.X));
            }
        }
    }

    public override bool _CanDropData(Vector2 atPosition, Variant data) =>
        Editable && data.VariantType == Variant.Type.String && InventoryDragSet.Decode(data.AsString()) is { Count: > 0 } ids && (CanTake?.Invoke(ids) ?? false);

    public override void _DropData(Vector2 atPosition, Variant data)
    {
        var ids = InventoryDragSet.Decode(data.AsString());
        var at = GetLineColumnAtPos((Vector2I)atPosition, true);
        ItemsDropped?.Invoke(ids, at.X < 0 ? Text.Length : IndexOf(at.Y, at.X));
    }
}

/// <summary>One notecard window's content.</summary>
public partial class NotecardPanel : VBoxContainer
{
    private readonly StringTable _strings;
    private NotecardWindowModel? _model;
    private NotecardEditSession? _session;
    private INotecardSaveTransport? _saves;
    private Func<ActionIntent, ActionResult?>? _submit;
    private CancellationToken _token;
    private bool _syncing, _closeAfterSave;
    private NotecardEmbeddedItem? _offered;

    public HBoxContainer Toolbar { get; }
    public Button SaveButton { get; }
    public Button UndoButton { get; }
    public Button RedoButton { get; }
    public Button FindButton { get; }
    public Label LockLabel { get; }
    public HBoxContainer FindBar { get; }
    public LineEdit FindField { get; }
    public CheckBox MatchCase { get; }
    public Button FindNextButton { get; }
    public Label DescriptionLabel { get; }
    public LineEdit DescriptionField { get; }
    public NotecardTextEdit Editor { get; }
    public RichTextLabel TextView { get; }
    public VBoxContainer ItemRows { get; }
    public Label StatusLabel { get; }
    public Label PositionLabel { get; }
    public Label CountLabel { get; }
    public HBoxContainer UnsavedPrompt { get; }
    public Button PromptSave { get; }
    public Button PromptDontSave { get; }
    public Button PromptCancel { get; }
    public HBoxContainer CopyPrompt { get; }
    public Label CopyQuestion { get; }
    public Button CopyYes { get; }
    public Button CopyNo { get; }

    /// <summary>Closes the window without asking again (set by the feature).</summary>
    public Action? Closing;
    /// <summary>Opens an embedded landmark, texture or sound (set by the feature).</summary>
    public Action<NotecardEmbeddedItem>? OpenEmbedded;
    /// <summary>The card was saved (its access after the save: the new asset, and the object for a card inside one).</summary>
    public Action<NotecardAccess>? SavedItem;

    public NotecardPanel(StringTable strings, int slot)
    {
        _strings = strings;
        Name = "notecard_panel_" + (slot + 1);
        AccessibilityName = strings.Get("notecard.panel");

        Toolbar = new HBoxContainer { Name = "toolbar" };
        SaveButton = Btn("save", "notecard.save", () => Save());
        SaveButton.ThemeTypeVariation = UiTheme.PrimaryButton;
        Toolbar.AddChild(SaveButton);
        UndoButton = Btn("undo", "notecard.undo", () => { Editor!.Undo(); OnTextChanged(); });
        Toolbar.AddChild(UndoButton);
        RedoButton = Btn("redo", "notecard.redo", () => { Editor!.Redo(); OnTextChanged(); });
        Toolbar.AddChild(RedoButton);
        Toolbar.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        LockLabel = new Label { Name = "read_only", Text = strings.Get("notecard.read_only"), ThemeTypeVariation = UiTheme.MutedLabel, Visible = false };
        Toolbar.AddChild(LockLabel);
        FindButton = Btn("find", "notecard.find", () => { FindBar!.Visible = !FindBar.Visible; if (FindBar.Visible) FindField!.GrabFocus(); });
        Toolbar.AddChild(FindButton);
        AddChild(Toolbar);

        FindBar = new HBoxContainer { Name = "find_bar", Visible = false };
        FindField = new LineEdit { Name = "find_field", PlaceholderText = strings.Get("notecard.find.field"), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        FindField.AccessibilityName = FindField.PlaceholderText;
        FindField.TextSubmitted += _ => FindNext();
        FindBar.AddChild(FindField);
        MatchCase = new CheckBox { Name = "match_case", Text = strings.Get("script.match_case") };
        MatchCase.AccessibilityName = MatchCase.Text;
        FindBar.AddChild(MatchCase);
        FindNextButton = Btn("find_next", "script.find_next", () => FindNext());
        FindBar.AddChild(FindNextButton);
        AddChild(FindBar);

        var descRow = new HBoxContainer { Name = "description_row" };
        DescriptionLabel = new Label { Name = "description_label", Text = strings.Get("notecard.description") };
        descRow.AddChild(DescriptionLabel);
        DescriptionField = new LineEdit { Name = "description", SizeFlagsHorizontal = SizeFlags.ExpandFill, Editable = false };
        DescriptionField.AccessibilityName = strings.Get("notecard.description");
        DescriptionField.TextChanged += _ => { if (!_syncing) Sync(); };
        descRow.AddChild(DescriptionField);
        AddChild(descRow);

        Editor = new NotecardTextEdit
        {
            Name = "editor", SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill,
            WrapMode = TextEdit.LineWrappingMode.Boundary, CaretBlink = false, Visible = false,
        };
        Editor.AccessibilityName = strings.Get("notecard.text");
        Editor.TextChanged += OnTextChanged;
        Editor.CaretChanged += ShowPosition;
        Editor.CanTake = ids => _session is { CanEdit: true } && ids.Any(id => _saves?.EmbeddableItem(id) is { } i && NotecardEditRules.CanEmbed(i.Kind, i.Permissions.NextOwnerMask));
        Editor.ItemsDropped = DropItems;
        Editor.Clicked = ClickAt;
        AddChild(Editor);

        TextView = new RichTextLabel
        {
            Name = "text", BbcodeEnabled = true, SelectionEnabled = true, ContextMenuEnabled = true, ScrollActive = true,
            SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, FocusMode = FocusModeEnum.Click,
        };
        TextView.AccessibilityName = strings.Get("notecard.text");
        TextView.MetaClicked += OnMeta;
        AddChild(TextView);

        ItemRows = new VBoxContainer { Name = "items" };
        ItemRows.AccessibilityName = strings.Get("notecard.items");
        AddChild(ItemRows);

        CopyPrompt = new HBoxContainer { Name = "copy_prompt", Visible = false };
        CopyQuestion = new Label { Name = "question", Text = strings.Get("notecard.copy.ask"), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        CopyPrompt.AddChild(CopyQuestion);
        CopyYes = Btn("copy_yes", "notecard.copy.yes", () => AnswerCopy(true));
        CopyYes.ThemeTypeVariation = UiTheme.PrimaryButton;
        CopyPrompt.AddChild(CopyYes);
        CopyNo = Btn("copy_no", "notecard.copy.no", () => AnswerCopy(false));
        CopyPrompt.AddChild(CopyNo);
        AddChild(CopyPrompt);

        var foot = new HBoxContainer { Name = "foot" };
        StatusLabel = new Label { Name = "status", AutowrapMode = TextServer.AutowrapMode.WordSmart, ThemeTypeVariation = UiTheme.MutedLabel, CustomMinimumSize = new Vector2(120, 0), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        foot.AddChild(StatusLabel);
        PositionLabel = new Label { Name = "position", ThemeTypeVariation = UiTheme.MutedLabel };
        foot.AddChild(PositionLabel);
        CountLabel = new Label { Name = "count", ThemeTypeVariation = UiTheme.MutedLabel };
        foot.AddChild(CountLabel);
        AddChild(foot);

        UnsavedPrompt = new HBoxContainer { Name = "unsaved", Visible = false };
        UnsavedPrompt.AddChild(new Label { Name = "question", Text = strings.Get("script.unsaved"), SizeFlagsHorizontal = SizeFlags.ExpandFill, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(48, 0) });   // wraps, so the three buttons stay inside a narrow window
        PromptSave = Btn("prompt_save", "script.unsaved.save", () => Answer(0));
        PromptSave.ThemeTypeVariation = UiTheme.PrimaryButton;
        UnsavedPrompt.AddChild(PromptSave);
        PromptDontSave = Btn("prompt_dont_save", "script.unsaved.dont_save", () => Answer(1));
        UnsavedPrompt.AddChild(PromptDontSave);
        PromptCancel = Btn("prompt_cancel", "script.unsaved.cancel", () => Answer(2));
        UnsavedPrompt.AddChild(PromptCancel);
        AddChild(UnsavedPrompt);

        Editor.Items = () => _session is { } s ? s.Buffer.ItemSpans(Editor.Text) : [];
        Clear();
    }

    private Button Btn(string name, string key, Action pressed)
    {
        var b = new Button { Name = name, Text = _strings.Get(key) };
        b.AccessibilityName = b.Text;
        b.Pressed += pressed;
        return b;
    }


    /// <summary>The window's model, for the development automation.</summary>
    internal NotecardWindowModel? Model => _model;

    /// <summary>The edit session, for the development automation and the tests.</summary>
    public NotecardEditSession? Session => _session;

    public void ShowLoading()
    {
        Clear();
        StatusLabel.Text = _strings.Get("notecard.loading");
    }

    public void Clear()
    {
        _model = null;
        _session = null;
        _offered = null;
        _askSave = false;
        _closeAfterSave = false;
        _syncing = true;
        DescriptionField.Text = string.Empty;
        Editor.Text = string.Empty;
        Editor.ClearUndoHistory();
        _syncing = false;
        TextView.Clear();
        foreach (var child in ItemRows.GetChildren())
            child.QueueFree();
        StatusLabel.Text = string.Empty;
        PositionLabel.Text = CountLabel.Text = string.Empty;
        UnsavedPrompt.Visible = CopyPrompt.Visible = FindBar.Visible = false;
        Editor.Visible = false;
        TextView.Visible = true;
        SaveButton.Visible = false;
        UndoButton.Disabled = RedoButton.Disabled = true;
        LockLabel.Visible = false;
    }

    public void Show(NotecardWindowModel model, Func<ActionIntent, ActionResult?> submit, bool objectModify = true, INotecardSaveTransport? saves = null, CancellationToken token = default)
    {
        Clear();
        _model = model;
        _submit = submit;
        _saves = saves;
        _token = token;
        _syncing = true;
        DescriptionField.Text = model.Opened.Description;
        _syncing = false;
        if (model.Opened.MessageKey is { } key)
        {
            StatusLabel.Text = _strings.Get(key);
            return;
        }
        // the one link renderer every text place uses: web addresses, location and app links
        TextView.Text = model.RenderText(Escape, HostLinks.Router);
        foreach (var item in model.Opened.Document?.Items ?? [])
        {
            if (model.Offer(item.Index) is { } offer)
                ItemRows.AddChild(ItemRow(model, offer));
        }
        if (model.Opened.Access is { } access && model.Opened.Document is { } doc)
        {
            _session = new NotecardEditSession(access, objectModify, doc, saves);
            _syncing = true;
            Editor.Text = _session.Buffer.ToDisplay(doc.Text);
            Editor.ClearUndoHistory();
            _syncing = false;
            Editor.Editable = _session.CanEdit;
            // a card that may not be copied shows its text but lets none of it be selected or copied (it is not shown at all
            // without copy rights: llpreviewnotecard.cpp loadAsset)
            Editor.SelectingEnabled = _session.CanCopyText;
            Editor.ContextMenuEnabled = _session.CanCopyText;
            Editor.ShortcutKeysEnabled = _session.CanEdit || _session.CanCopyText;
            DescriptionField.Editable = _session.CanEdit && access.ObjectId == Guid.Empty;
            Editor.Visible = true;
            TextView.Visible = false;
        }
        Sync();
    }

    /// <summary>The controls as the session says.</summary>
    private void Sync()
    {
        if (_session is not { } s)
            return;
        SaveButton.Visible = s.CanEdit;
        SaveButton.Disabled = !(s.CanSave || DescriptionChanged && !s.Saving);
        UndoButton.Disabled = !s.CanEdit || !Editor.HasUndo();
        RedoButton.Disabled = !s.CanEdit || !Editor.HasRedo();
        LockLabel.Visible = !s.CanEdit;
        CountLabel.Text = _strings.Get("notecard.count", s.TextBytes.ToString("N0"), NotecardEditRules.MaxTextBytes.ToString("N0"));
        CountLabel.ThemeTypeVariation = s.OverLimit ? UiTheme.ErrorLabel : UiTheme.MutedLabel;
        ShowPosition();
    }

    private bool DescriptionChanged => _model is { } m && _session is { CanEdit: true } && DescriptionField.Text != m.Opened.Description
        && Encoding.UTF8.GetByteCount(DescriptionField.Text) <= NotecardEditRules.MaxDescriptionBytes;

    private void ShowPosition()
    {
        if (_session is null)
            return;
        PositionLabel.Text = _strings.Get("notecard.position", Editor.GetCaretLine() + 1, Editor.GetCaretColumn() + 1);
    }

    private void OnTextChanged()
    {
        if (_syncing || _session is not { } s)
            return;
        s.SetText(Editor.Text, []);
        Sync();
    }

    /// <summary>Inventory items dropped on the text at a character offset: those that may go into a card become embedded items.</summary>
    public void DropItems(IReadOnlyList<Guid> ids, int at)
    {
        if (_session is not { CanEdit: true } s || _saves is null)
            return;
        string text = Editor.Text;
        int put = Math.Clamp(at, 0, text.Length);
        // never inside another item
        if (NotecardEditBuffer.TokenSpanAt(text, put) is { } inside && put < inside.Start + inside.Length)
            put = inside.Start + inside.Length;
        int refused = 0;
        var accepted = new List<NotecardEmbeddedItem>();
        foreach (var id in ids)
        {
            if (_saves.EmbeddableItem(id) is not { } item || !NotecardEditRules.CanEmbed(item.Kind, item.Permissions.NextOwnerMask))
                refused++;
            else
                accepted.Add(item);
        }
        (text, put) = s.Buffer.InsertAll(text, put, accepted);   // a space keeps neighbouring items from running together
        Editor.BeginComplexOperation();
        Editor.SelectAll();
        Editor.InsertTextAtCaret(text);
        Editor.EndComplexOperation();
        var place = Editor.PlaceOf(put);
        Editor.SetCaretLine(place.Line);
        Editor.SetCaretColumn(place.Column);
        OnTextChanged();
        StatusLabel.Text = refused > 0 ? _strings.Get("notecard.drop.refused") : "";
    }

    /// <summary>A click on the text: on an embedded item it opens the item or offers a copy (llviewertexteditor.cpp openEmbeddedItem).</summary>
    public void ClickAt(int index)
    {
        if (_session is not { } s || s.Buffer.ItemAt(Editor.Text, index) is not { } item)
            return;
        if (!s.IsSaved(item))
        {
            // an item dropped in since the last save: the card must be saved first (the ConfirmNotecardSave question; OK saves)
            _offered = null;
            _askSave = true;
            CopyQuestion.Text = _strings.Get("notecard.save_first");
            CopyYes.Text = _strings.Get("notecard.save_first.yes");
            CopyPrompt.Visible = true;
            CopyYes.GrabFocus();
            return;
        }
        OpenItem(item);
    }

    private bool _askSave;

    /// <summary>An embedded item clicked, in the editor or in the read view: opened, or a copy offered.</summary>
    private void OpenItem(NotecardEmbeddedItem item)
    {
        switch (NotecardEditRules.ClickOf(item.Kind))
        {
            case EmbeddedClick.Open:
                OpenEmbedded?.Invoke(item);
                StatusLabel.Text = item.Kind switch
                {
                    ItemKind.Landmark => _strings.Get("notecard.landmark.row", item.Name),
                    ItemKind.Texture => "",
                    _ => _strings.Get("notecard.open.not_built", item.Name),
                };
                break;
            case EmbeddedClick.OfferCopy:
                _offered = item;
                _askSave = false;
                CopyQuestion.Text = _strings.Get("notecard.copy.ask");
                CopyYes.Text = _strings.Get("notecard.copy.yes");
                CopyPrompt.Visible = true;
                CopyYes.GrabFocus();
                break;
        }
    }

    /// <summary>The answer to "Copy this item to your inventory?".</summary>
    public void AnswerCopy(bool copy)
    {
        CopyPrompt.Visible = false;
        if (_askSave)
        {
            _askSave = false;
            if (copy)
                Save();
            return;
        }
        if (!copy || _offered is not { } item || _model is not { } m || m.Opened.Access is not { } access)
            return;
        _offered = null;
        if (!NotecardRules.CanCopyOut(access, item) || m.Opened.Document?.Items.Any(i => i.ItemId == item.ItemId) != true)
        {
            StatusLabel.Text = _strings.Get("notecard.copy.refused");
            return;
        }
        Send(new NotecardCopyItemIntent(m.Opened.ItemId, item.ItemId), _strings.Get("notecard.copy.sent", item.Name));
    }

    private Control ItemRow(NotecardWindowModel model, NotecardItemOffer offer)
    {
        var row = new HBoxContainer { Name = "item_" + offer.Item.Index };
        string label = offer.Teleport && offer.Place.Length > 0 ? _strings.Get("notecard.place", offer.Item.Name, offer.Place) : offer.Item.Name;
        var name = new Label { Name = "name", Text = label, SizeFlagsHorizontal = SizeFlags.ExpandFill, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
        name.AccessibilityName = label;
        row.AddChild(name);
        if (offer.Teleport)
        {
            var teleport = new Button { Name = "teleport", Text = _strings.Get("notecard.teleport") };
            teleport.AccessibilityName = _strings.Get("notecard.teleport");
            teleport.Pressed += () => Send(model.TeleportTo(offer.Item.Index), _strings.Get("notecard.teleport.sent", offer.Item.Name));
            row.AddChild(teleport);
        }
        var copy = new Button { Name = "copy", Text = _strings.Get("notecard.copy"), Disabled = !offer.Copy };
        copy.AccessibilityName = _strings.Get("notecard.copy");
        copy.Pressed += () => Send(model.CopyToInventory(offer.Item.Index), _strings.Get("notecard.copy.sent", offer.Item.Name));
        row.AddChild(copy);
        return row;
    }

    /// <summary>Copies an embedded item out (the automation's way in; the button does the same).</summary>
    internal bool CopyItem(int index) => _model is { } m && Send(m.CopyToInventory(index), _strings.Get("notecard.copy.sent", m.Offer(index)?.Item.Name ?? ""));

    private bool Send(ActionIntent? intent, string sent)
    {
        if (intent is null || _submit is null)
            return false;
        var result = _submit(intent);
        bool ok = result is { Sent: true };
        StatusLabel.Text = ok ? sent : _strings.Get("notecard.copy.refused");
        return ok;
    }

    /// <summary>Clicks a link target as the mouse does (the development automation's way in).</summary>
    internal void ClickTarget(string meta) => OnMeta(meta);

    private void OnMeta(Variant meta)
    {
        if (_model is null)
            return;
        if (NotecardWindowModel.ItemOfMeta(meta.AsString()) is { } index)
        {
            if (_model.Opened.Document?.Items.FirstOrDefault(i => i.Index == index) is { } item)
                OpenItem(item);
            return;
        }
        if (Viewer.Shell.Links.LinkText.LinkOf(meta.AsString()) is not { } link)
            return;
        StatusLabel.Text = HostLinks.Click(link) ?? "";
    }

    /// <summary>Find next: from the caret on, round to the start; the match is selected. False when nothing matched.</summary>
    public bool FindNext()
    {
        int at = ScriptEditing.FindNext(Editor.Text, FindField.Text, Editor.CaretIndex, MatchCase.ButtonPressed);
        if (at < 0)
        {
            StatusLabel.Text = _strings.Get("script.not_found");
            return false;
        }
        var from = Editor.PlaceOf(at);
        var to = Editor.PlaceOf(at + FindField.Text.Length);
        Editor.Select(from.Line, from.Column, to.Line, to.Column);
        Editor.SetCaretLine(to.Line);
        Editor.SetCaretColumn(to.Column);
        ShowPosition();
        return true;
    }

    /// <summary>Save (the button, and Ctrl+S): the text, and the description when it changed.</summary>
    public bool Save()
    {
        if (_session is not { CanEdit: true } s || _saves is not { } saves)
            return false;
        bool any = false;
        if (DescriptionChanged && _model is { } m && m.Opened.Access is { ObjectId: var o } && o == Guid.Empty)
        {
            if (saves.SetItemDescription(m.Opened.ItemId, DescriptionField.Text))
            {
                _model = new NotecardWindowModel(m.Opened with { Description = DescriptionField.Text });
                any = true;
            }
        }
        if (!s.CanSave)
        {
            Sync();
            if (any && _closeAfterSave)
                Closing?.Invoke();
            return any;
        }
        StatusLabel.Text = _strings.Get("notecard.saving");
        var token = _token;
        var task = s.SaveAsync(token);
        Sync();
        _ = task.ContinueWith(r =>
        {
            var result = r.IsCompletedSuccessfully ? r.Result : NotecardSaveResult.Of(NotecardSaveStatus.Failed, "failed");
            if (!token.IsCancellationRequested)
                Callable.From(() => SaveDone(s, result)).CallDeferred();
        }, TaskScheduler.Default);
        return true;
    }

    private void SaveDone(NotecardEditSession s, NotecardSaveResult result)
    {
        if (_session != s)
            return;
        StatusLabel.Text = _strings.Get(result.Status switch
        {
            NotecardSaveStatus.Saved => "notecard.saved",
            NotecardSaveStatus.NoService => "notecard.save.no_service",
            _ => "notecard.save.failed",
        });
        Sync();
        if (result.Status == NotecardSaveStatus.Saved)
        {
            SavedItem?.Invoke(s.Access);
            if (_closeAfterSave)
                Closing?.Invoke();
        }
        _closeAfterSave = false;
    }

    /// <summary>Shows a save's answer without a grid (the pictures).</summary>
    public void ShowAnswer(NotecardSaveResult result)
    {
        if (_session is { } s)
            SaveDone(s, result);
    }

    /// <summary>Ctrl+S saves and Ctrl+F opens find while the window has the focus (llpreviewnotecard.cpp handleKeyHere).</summary>
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

    /// <summary>The close guard: true when the window may close; otherwise "Save Changes?" is shown and the window stays.</summary>
    public bool MayClose()
    {
        if (_session is not { } s || (s.RequestClose() == NotecardCloseVerdict.Close && !DescriptionChanged))
            return true;
        UnsavedPrompt.Visible = true;
        PromptSave.GrabFocus();
        return false;
    }

    /// <summary>The answer to "Save Changes?": 0 Save (then close once saved), 1 Don't Save (close), 2 Cancel (stay).</summary>
    public void Answer(int option)
    {
        UnsavedPrompt.Visible = false;
        switch (option)
        {
            case 0:
                _closeAfterSave = true;
                if (!Save())
                    _closeAfterSave = false;
                break;
            case 1:
                Closing?.Invoke();
                break;
        }
    }

    /// <summary>The card's text is shown as it is: a "[" would start a tag.</summary>
    private static string Escape(string text) => text.Replace("[", "[lb]");
}
