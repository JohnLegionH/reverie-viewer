// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Viewer.Core.Actions;
using Viewer.Core.Inventory;
using Viewer.Shell;
using Viewer.Shell.Inventory;
using Viewer.Shell.Outfit;

namespace Viewer.Godot;

/// <summary>
/// The inventory as a floater, built by hand in code. It only draws <see cref="InventoryViewModel"/> (tested without the
/// engine) and hands clicks, keys and drops back to it and to <see cref="InventoryActions"/>. Behaviour follows the official
/// viewer's inventory window (a tree with My Inventory open and the Library closed, folders fetched as they are opened, a name
/// search, recent items, sorting by name or date, system folders on top, drag and drop to move, Trash by move); no layout file
/// or artwork of any viewer is used and the icons are small shapes drawn here. Every text comes from the string table and every
/// control has an accessible name.
/// </summary>
public static class InventoryFeature
{
    public const string FloaterId = "inventory";

    /// <summary>Adds the floater, the Content menu, a toolbar button and their commands to a shell that is already drawn.</summary>
    /// <param name="settings">Where the view options (the sort) are kept between runs; null keeps them for this window's life only.</param>
    public static InventoryPanel Install(ShellRoot shell, long redrawGapMs = 250, SettingsService? settings = null)
    {
        var s = shell.Shell;
        var panel = new InventoryPanel(s.Strings, s.Focus, redrawGapMs, settings);
        shell.AddFloater(new FloaterSpec(FloaterId, "inventory.title", new UiSize(420, 520), new UiSize(300, 320)), panel);
        s.Commands.Register("floater.inventory", () => s.Floaters.Toggle(FloaterId));
        if (!s.Menus.Menus.Any(m => m.Id == "content"))
            s.Menus.AddMenu("content", "menu.content");
        s.Menus.AddEntry("content", new MenuEntry("content.inventory", "menu.content.inventory", "floater.inventory"));
        s.Toolbar.Add(new ToolbarButton("toolbar_inventory", "toolbar.inventory", "floater.inventory", UiIcons.Inventory, FloaterId));
        return panel;
    }
}

/// <summary>
/// The tree: rows can be dragged and dropped on a folder (or on an item, which means its folder). Folders open and close on a click
/// only (<see cref="FolderTreeInput"/>): the engine's own unfolding of folders under a drag is off, because it also unfolded them
/// under a pointer that merely passed over while the engine still thought a drag was going on. Nothing opens a folder during a drag:
/// the folder under the pointer gets the drop-target outline and the text "Drop to move here", the dragged row a dashed outline
/// (<see cref="DragFeedback"/>), and a drop on a closed folder moves the item into it. A drag whose button is no longer held is ended. A right press is taken here before the engine sees it, so it selects its row and asks for the menu
/// without toggling the folder. Left and Right keys follow the official folder view.
/// </summary>
public partial class InventoryTree : Tree
{
    /// <summary>Raised when rows were dropped: the dragged ids (the selection, when the drag began on a selected row) and the id
    /// of the row they were dropped on.</summary>
    public event Action<IReadOnlyList<Guid>, Guid>? Dropped;

    /// <summary>A right press selected this row; its menu is to open at the pointer.</summary>
    public event Action<Guid>? MenuRequested;

    /// <summary>A double-click on a row's name, or Enter on it: a folder is toggled, an item opened.</summary>
    public event Action<Guid>? RowActivated;

    /// <summary>Whether a row is a folder (the panel answers from the model; an empty folder has no child rows).</summary>
    public Func<Guid, bool>? IsFolder;

    /// <summary>The text drawn on the drop-target folder.</summary>
    public string DropText { get; set; } = "";

    public InventoryTree()
    {
        EnableDragUnfolding = false;
    }

    /// <summary>What the tree marks for the drag going on (the automation reads it).</summary>
    public DropCue Cue { get; private set; }

    // a cue held in place by a render case (no drag is going on in a still picture); null: from the drag
    private DropCue? _heldCue;

    /// <summary>Shows a drag's marks without a drag, for a still picture of the window (null: back to the drag's own).</summary>
    internal void HoldCue(DropCue? cue) { _heldCue = cue; Cue = cue ?? default; QueueRedraw(); }

    /// <summary>What the last frame saw of a drag (the automation's figures): dragging, button held, over the tree, a folder under the pointer.</summary>
    public (bool Dragging, bool Held, Vector2? At, bool Folder) DragSeen { get; private set; }

    // a pointer place given by the automation's check (its injected pointer is not the system cursor); null: the system cursor
    private Vector2? _dragAt;

    /// <summary>The place over the tree a drag going on is held at, for the automation's check (local coordinates; null: the system cursor).</summary>
    internal void HoldDragAt(Vector2? at) => _dragAt = at;

    /// <summary>Rows added to the selection with Ctrl+click, besides the selected row (the engine's own multi-select does not
    /// report a selection made from code, which the panel and its checks rely on, so the tree stays in single mode).</summary>
    public HashSet<Guid> Added { get; } = new();

    /// <summary>Marks the added rows again (after the tree was drawn again).</summary>
    public void MarkAdded()
    {
        var shade = ShellTheme.C(UiTheme.Tokens.Accent) with { A = 0.28f };
        for (var item = GetRoot(); item is not null; item = item.GetNextInTree())
        {
            if (Added.Contains(IdOf(item)))
                item.SetCustomBgColor(0, shade);
            else
                item.ClearCustomBgColor(0);
        }
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true, DoubleClick: false } lp)
        {
            var row = GetItemAtPosition(lp.Position);
            var rowId = IdOf(row);
            if (lp.CtrlPressed && rowId != Guid.Empty && GetSelected() is { } current && IdOf(current) != rowId)
            {
                // Ctrl+click: the row joins the set (or leaves it), the selected row stays
                if (!Added.Remove(rowId))
                    Added.Add(rowId);
                MarkAdded();
                AcceptEvent();
                return;
            }
            if (!lp.CtrlPressed && Added.Count > 0 && rowId != Guid.Empty && !Added.Contains(rowId) && (GetSelected() is not { } sel || IdOf(sel) != rowId))
            {
                Added.Clear();   // a plain click on a row outside the set starts the selection again
                MarkAdded();
            }
        }
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Right } mb:
                AcceptEvent();   // the engine would also fold a folder whose arrow is under a right press
                if (!mb.Pressed)
                    return;
                var item = GetItemAtPosition(mb.Position);
                var id = IdOf(item);
                if (id == Guid.Empty || FolderTreeInput.Press(RowButton.Right, RowPart.Name, IsFolder?.Invoke(id) ?? false, mb.DoubleClick) != TreeAction.SelectAndMenu)
                    return;
                item!.Select(0);
                MenuRequested?.Invoke(id);
                return;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true, DoubleClick: true } dc:
                // the first click already did what a click does (the arrow toggled the folder, the name selected the row); the second
                // toggles the folder (back, on the arrow) or opens the item, as the official folder view's double-click does
                if (IdOf(GetItemAtPosition(dc.Position)) is var hit && hit != Guid.Empty)
                {
                    AcceptEvent();
                    GetItemAtPosition(dc.Position)!.Select(0);
                    RowActivated?.Invoke(hit);
                }
                return;
            case InputEventKey { Pressed: true } k when GetSelected() is { } sel && KeyOf(k) is { } key:
                bool isFolder = IsFolder?.Invoke(IdOf(sel)) ?? sel.GetFirstChild() is not null;
                bool hasParentRow = sel.GetParent() is { } parent && parent != GetRoot();
                switch (FolderTreeInput.Key(key, isFolder, isFolder && !sel.Collapsed, hasParentRow))
                {
                    case TreeAction.Expand: sel.Collapsed = false; AcceptEvent(); break;
                    case TreeAction.Collapse: sel.Collapsed = true; AcceptEvent(); break;
                    case TreeAction.SelectParent: sel.GetParent()!.Select(0); ScrollToItem(sel.GetParent()!); AcceptEvent(); break;
                    case TreeAction.None when key is TreeKey.Left or TreeKey.Right: AcceptEvent(); break;
                    default: break;   // Up and Down: the engine moves the selection
                }
                return;
            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.Enter or Key.KpEnter } when GetSelected() is { } chosen && IdOf(chosen) is var cid && cid != Guid.Empty:
                AcceptEvent();
                RowActivated?.Invoke(cid);
                return;
        }
    }

    private static TreeKey? KeyOf(InputEventKey k) =>
        k.IsAction("ui_left", true) ? TreeKey.Left : k.IsAction("ui_right", true) ? TreeKey.Right : null;

    public override void _Process(double delta)
    {
        var vp = GetViewport();
        bool dragging = vp.GuiIsDragging();
        bool held = Input.IsMouseButtonPressed(MouseButton.Left);
        if (DragFeedback.IsStaleDrag(dragging, held))
        {
            vp.GuiCancelDrag();   // a drag the engine never ended: hovering must not drag or open anything
            dragging = false;
        }
        Guid under = Guid.Empty, dragged = Guid.Empty;
        Vector2? pointer = _dragAt ?? (GetGlobalRect().HasPoint(GetGlobalMousePosition()) ? GetLocalMousePosition() : null);
        if (dragging)
        {
            var data = vp.GuiGetDragData();
            if (data.VariantType == Variant.Type.String && Guid.TryParse(data.AsString(), out var g))
                dragged = g;
        }
        if (dragging && IsVisibleInTree() && pointer is { } at)
            under = FolderRowAt(at);
        DragSeen = (dragging, held, pointer, under != Guid.Empty);
        if (_heldCue is not null)
            return;
        bool canDrop = dragged != Guid.Empty && under != Guid.Empty && (CanDropOn?.Invoke(dragged, under) ?? false);
        var cue = DragFeedback.During(dragged, under, canDrop, dragging, held);
        if (cue != Cue)
        {
            Cue = cue;
            QueueRedraw();
        }
    }

    // the folder a drop at this place goes into: the folder row under it, or the folder of the item row under it
    private Guid FolderRowAt(Vector2 at)
    {
        var item = GetItemAtPosition(at);
        var id = IdOf(item);
        if (id == Guid.Empty)
            return Guid.Empty;
        if (IsFolder?.Invoke(id) ?? item!.GetFirstChild() is not null)
            return id;
        var parent = item!.GetParent();
        return parent is not null && parent != GetRoot() ? IdOf(parent) : Guid.Empty;
    }

    private TreeItem? RowOf(Guid id)
    {
        for (var item = GetRoot(); item is not null; item = item.GetNextVisible())
            if (IdOf(item) == id)
                return item;
        return null;
    }

    /// <summary>Where a shown row's name stands in the viewport, scrolled into view first; null when the row is not shown (for the live check's pointer).</summary>
    public Vector2? PointOf(Guid id)
    {
        if (RowOf(id) is not { } item)
            return null;
        ScrollToItem(item, false);
        var r = GetItemAreaRect(item, 0);
        if (r.Size.Y <= 0)
            return null;
        return GetGlobalTransformWithCanvas() * new Vector2(r.Position.X + r.Size.X * 0.6f, r.Position.Y + r.Size.Y / 2);
    }

    // a row's rectangle across the whole tree, in the tree's own coordinates; null when it is not drawn (scrolled away, or in a closed folder)
    private Rect2? RowArea(Guid id)
    {
        if (id == Guid.Empty || RowOf(id) is not { } item)
            return null;
        var r = GetItemAreaRect(item, -1);
        if (r.Size.Y <= 0)
            return null;
        return new Rect2(new Vector2(1, r.Position.Y), new Vector2(Size.X - 2, r.Size.Y));
    }

    public override void _Draw()
    {
        // drawn over the rows: the drop target's outline and its text, and a dashed outline on the dragged row (the spec's drag look)
        var accent = GetThemeColor("font_selected_color", "Tree");
        if (RowArea(Cue.Source) is { } src)
        {
            var a = src.Position; var b = src.End;
            DrawDashedLine(a, new Vector2(b.X, a.Y), accent, 1.5f, 4f);
            DrawDashedLine(new Vector2(b.X, a.Y), b, accent, 1.5f, 4f);
            DrawDashedLine(b, new Vector2(a.X, b.Y), accent, 1.5f, 4f);
            DrawDashedLine(new Vector2(a.X, b.Y), a, accent, 1.5f, 4f);
        }
        if (RowArea(Cue.Target) is { } dst)
        {
            DrawRect(dst, accent, filled: false, width: 2f);
            var font = GetThemeFont("font", "Tree");
            int size = GetThemeFontSize("font_size", "Tree");
            string text = DropText.Length > 0 ? DropText : DragFeedback.TextKey;
            float w = font.GetStringSize(text, HorizontalAlignment.Left, -1, size).X;
            float y = dst.Position.Y + (dst.Size.Y + font.GetAscent(size) - font.GetDescent(size)) / 2;
            DrawString(font, new Vector2(dst.End.X - w - 8, y), text, HorizontalAlignment.Left, -1, size, accent);
        }
    }

    /// <summary>The dragged row's id may be moved onto the row (the panel decides by the rules).</summary>
    public Func<Guid, Guid, bool>? CanDropOn;

    public static Guid IdOf(TreeItem? item) => item is not null && item.GetMetadata(0).VariantType == Variant.Type.String && Guid.TryParse(item.GetMetadata(0).AsString(), out var g) ? g : Guid.Empty;

    /// <summary>The ids of the selected rows: the selected row and the rows Ctrl+click added, in tree order.</summary>
    public List<Guid> SelectedIds()
    {
        var selected = IdOf(GetSelected());
        var ids = new List<Guid>();
        for (var item = GetRoot(); item is not null; item = item.GetNextInTree())
            if (IdOf(item) is var id && id != Guid.Empty && (id == selected || Added.Contains(id)) && !ids.Contains(id))
                ids.Add(id);
        return ids;
    }

    public override Variant _GetDragData(Vector2 atPosition)
    {
        var item = GetItemAtPosition(atPosition);
        var ids = InventoryDragSet.For(IdOf(item), SelectedIds());
        if (ids.Count == 0)
            return default;
        SetDragPreview(new Label { Text = ids.Count == 1 ? item!.GetText(0) : $"{item!.GetText(0)} (+{ids.Count - 1})" });
        return InventoryDragSet.Encode(ids);
    }

    private static IReadOnlyList<Guid> DraggedIds(Variant data) =>
        data.VariantType == Variant.Type.String ? InventoryDragSet.Decode(data.AsString()) : Array.Empty<Guid>();

    /// <summary>An item dragged out of an object's contents dropped on a row: the drag and the row's id (the panel finds the folder).</summary>
    public Func<Viewer.Shell.Build.TaskDrag, Guid, bool>? CanTakeTaskItem;
    public event Action<Viewer.Shell.Build.TaskDrag, Guid>? TaskItemDropped;

    private static Viewer.Shell.Build.TaskDrag? TaskDragOf(Variant data) =>
        data.VariantType == Variant.Type.String ? Viewer.Shell.Build.TaskDrag.Decode(data.AsString()) : null;

    // where a drop lands: the point the automation holds for its injected pointer (development builds only), else the engine's own
    private Vector2 DropAt(Vector2 atPosition) => _dragAt ?? atPosition;

    public override bool _CanDropData(Vector2 atPosition, Variant data)
    {
        var at = DropAt(atPosition);
        if (TaskDragOf(data) is { } task)
            return IdOf(GetItemAtPosition(at)) is var row && row != Guid.Empty && (CanTakeTaskItem?.Invoke(task, row) ?? false);
        var dragged = DraggedIds(data);
        var target = IdOf(GetItemAtPosition(at));
        // the drop is offered when any dragged row may go there; each is checked again when it lands
        return target != Guid.Empty && dragged.Any(d => CanDropOn?.Invoke(d, target) ?? false);
    }

    public override void _DropData(Vector2 atPosition, Variant data)
    {
        var at = DropAt(atPosition);
        if (TaskDragOf(data) is { } task)
        {
            if (IdOf(GetItemAtPosition(at)) is var row && row != Guid.Empty)
                TaskItemDropped?.Invoke(task, row);
            return;
        }
        var dragged = DraggedIds(data);
        var target = IdOf(GetItemAtPosition(at));
        if (dragged.Count > 0 && target != Guid.Empty)
            Dropped?.Invoke(dragged, target);
    }
}

/// <summary>The inventory floater's content.</summary>
public partial class InventoryPanel : VBoxContainer
{
    private readonly StringTable _strings;
    private static ThemeSpec _theme => UiTheme.Spec;
    private readonly Dictionary<string, ImageTexture> _icons = new();
    private InventoryModel? _model;
    private InventoryViewModel? _view;
    private InventoryActions? _actions;
    private OutfitActions? _outfit;
    private int _shownRevision = -1;
    private bool _dirty = true, _modelDirty, _building;
    private readonly RedrawGate _gate;
    private readonly InventoryTreeRedraw _treeRedraw = new();
    private readonly SettingsService? _settings;
    private bool _drawn;
    private Guid _selected;
    private Guid _pendingTrash;

    public LineEdit Search { get; }
    public OptionButton SortChoice { get; }
    public TabBar Tabs { get; }
    public InventoryTree TreeView { get; }
    public HBoxContainer ConfirmBar { get; }
    public Label ConfirmText { get; }
    public Button ConfirmYes { get; }
    public Button ConfirmNo { get; }
    public Button NewFolderButton { get; }
    public Button RenameButton { get; }
    public Button TrashButton { get; }
    public Button RetryButton { get; }
    public Button WearButton { get; }
    public Button AddButton { get; }
    public Button TakeOffButton { get; }
    public Button DetachButton { get; }
    public Button AttachButton { get; }
    public PopupMenu AttachMenu { get; }
    public Label NoticeLabel { get; }
    public Label StatusLabel { get; }
    public PopupMenu Context { get; }

    /// <summary>The user asked to open an item (a double-click, Enter, or Open on its menu): the item's id, a link's own id included.</summary>
    public event Action<Guid>? OpenRequested;

    public InventoryPanel(StringTable strings, InputFocus focus, long redrawGapMs = 250, SettingsService? settings = null)
    {
        _strings = strings;
        _settings = settings;
        _gate = new RedrawGate(redrawGapMs);
        Name = "inventory_panel";

        var top = new HBoxContainer { Name = "search_row" };
        Search = new LineEdit { Name = "search", PlaceholderText = strings.Get("inventory.search.hint"), SizeFlagsHorizontal = SizeFlags.ExpandFill, ClearButtonEnabled = true };
        Search.AccessibilityName = strings.Get("inventory.search.label");
        Search.TextChanged += t => { if (_view is not null) { _view.SearchText = t; _dirty = true; } };
        top.AddChild(Search);
        SortChoice = new OptionButton { Name = "sort" };
        SortChoice.AccessibilityName = strings.Get("inventory.sort.label");
        SortChoice.AddItem(strings.Get("inventory.sort.name"), (int)InventorySort.ByName);
        SortChoice.AddItem(strings.Get("inventory.sort.date"), (int)InventorySort.ByDate);
        if (settings is not null)
            SortChoice.Select(SortChoice.GetItemIndex((int)InventoryViewPrefs.Load(settings)));   // the sort chosen last, kept until changed
        SortChoice.ItemSelected += i =>
        {
            var sort = (InventorySort)SortChoice.GetItemId((int)i);
            if (_settings is not null)
                InventoryViewPrefs.Save(_settings, sort);
            if (_view is not null) { _view.Sort = sort; _dirty = true; }
        };
        top.AddChild(SortChoice);
        AddChild(top);

        Tabs = new TabBar { Name = "tabs" };
        Tabs.AccessibilityName = strings.Get("inventory.tabs");
        Tabs.AddTab(strings.Get("inventory.tab.all"));
        Tabs.AddTab(strings.Get("inventory.tab.recent"));
        Tabs.TabChanged += i => { if (_view is not null) { _view.Tab = i == 1 ? InventoryTab.Recent : InventoryTab.All; _dirty = true; } };
        AddChild(Tabs);

        TreeView = new InventoryTree
        {
            Name = "tree", HideRoot = true, SelectMode = global::Godot.Tree.SelectModeEnum.Single, DropModeFlags = (int)global::Godot.Tree.DropModeFlagsEnum.OnItem,
            SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, FocusMode = FocusModeEnum.All,
        };
        TreeView.AccessibilityName = strings.Get("inventory.tree");
        TreeView.DropText = strings.Get(DragFeedback.TextKey);
        TreeView.ItemCollapsed += OnItemCollapsed;
        TreeView.ItemSelected += OnItemSelected;
        TreeView.ItemEdited += OnItemEdited;
        TreeView.MenuRequested += OnRowMenu;
        TreeView.IsFolder = id => _model?.Folder(id) is not null;
        TreeView.RowActivated += OnRowActivated;
        TreeView.CanDropOn = (dragged, target) => _actions is not null && _actions.WhyNotMove(dragged, DropFolder(target)) is null;
        TreeView.Dropped += OnDropped;
        AddChild(TreeView);

        ConfirmBar = new HBoxContainer { Name = "confirm_bar", Visible = false };
        ConfirmBar.AccessibilityName = strings.Get("inventory.confirm.bar");
        ConfirmText = new Label { Name = "confirm_text", SizeFlagsHorizontal = SizeFlags.ExpandFill, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(160, 0) };
        ConfirmText.AccessibilityName = strings.Get("inventory.confirm.bar");
        ConfirmBar.AddChild(ConfirmText);
        ConfirmYes = Button("confirm_yes", "inventory.confirm.yes", OnConfirmYes);
        ConfirmNo = Button("confirm_no", "inventory.confirm.no", OnConfirmNo);
        ConfirmBar.AddChild(ConfirmYes);
        ConfirmBar.AddChild(ConfirmNo);
        AddChild(ConfirmBar);

        var buttons = new HBoxContainer { Name = "buttons" };
        NewFolderButton = Button("new_folder", "inventory.button.new_folder", OnNewFolder);
        RenameButton = Button("rename", "inventory.button.rename", OnRename);
        TrashButton = Button("trash", "inventory.button.trash", OnTrash);
        RetryButton = Button("retry", "inventory.button.retry", OnRetry);
        foreach (var b in new[] { NewFolderButton, RenameButton, TrashButton, RetryButton })
            buttons.AddChild(b);
        AddChild(buttons);

        var wearRow = new HBoxContainer { Name = "wear_buttons" };
        WearButton = Button("wear", "inventory.button.wear", OnWear);
        AddButton = Button("add", "inventory.button.add", OnAdd);
        TakeOffButton = Button("take_off", "inventory.button.take_off", OnTakeOff);
        AttachButton = Button("attach", "inventory.button.attach", OnAttachMenu);
        DetachButton = Button("detach", "inventory.button.detach", OnDetach);
        foreach (var b in new[] { WearButton, AddButton, TakeOffButton, AttachButton, DetachButton })
            wearRow.AddChild(b);
        AddChild(wearRow);
        AttachMenu = new PopupMenu { Name = "attach_menu" };
        AttachMenu.AccessibilityName = strings.Get("inventory.attach.menu");
        for (int point = 0; point <= OutfitPlanner.MaxAttachmentPoint; point++)
        {
            string label = strings.Get("outfit.point." + point);
            AttachMenu.AddItem(point is >= 31 and <= 38 ? strings.Get("inventory.attach.hud", label) : label, point);
        }
        AttachMenu.IdPressed += id => OnAttach((int)id);
        AddChild(AttachMenu);

        NoticeLabel = ChatFeature.Note("notice", strings.Get("inventory.notice"), _theme);
        NoticeLabel.ThemeTypeVariation = UiTheme.ErrorLabel;
        AddChild(NoticeLabel);
        StatusLabel = ChatFeature.Note("status", strings.Get("inventory.status"), _theme);
        AddChild(StatusLabel);

        TreeView.AllowRmbSelect = true;   // a right-click selects its row first, so the menu acts on the row clicked
        Context = new PopupMenu { Name = "context" };
        Context.AboutToPopup += () => _contextToggle.Opened("context");
        Context.PopupHide += () => _contextToggle.Closed("context", (long)Time.GetTicksMsec());
        Context.AddItem(strings.Get("inventory.button.new_folder"), 0);
        Context.AddItem(strings.Get("inventory.button.rename"), 1);
        Context.AddItem(strings.Get("inventory.button.trash"), 2);
        Context.AddItem(strings.Get("inventory.button.retry"), 3);
        Context.AddItem(strings.Get("inventory.button.wear"), 4);
        Context.AddItem(strings.Get("inventory.button.add"), 5);
        Context.AddItem(strings.Get("inventory.button.take_off"), 6);
        Context.AddItem(strings.Get("inventory.button.attach"), 7);
        Context.AddItem(strings.Get("inventory.button.detach"), 8);
        Context.AddItem(strings.Get("inventory.button.open"), 9);
        Context.IdPressed += id => { switch (id) { case 0: OnNewFolder(); break; case 1: OnRename(); break; case 2: OnTrash(); break; case 3: OnRetry(); break;
            case 4: OnWear(); break; case 5: OnAdd(); break; case 6: OnTakeOff(); break; case 7: OnAttachMenu(); break; case 8: OnDetach(); break;
            case 9: if (CanOpen(_selected)) OpenRequested?.Invoke(_selected); break; } };
        AddChild(Context);
        Bind(null, null);
    }

    private Button Button(string name, string key, Action pressed)
    {
        var b = new Button { Name = name, Text = _strings.Get(key) };
        b.AccessibilityName = _strings.Get(key);
        b.Pressed += pressed;
        return b;
    }

    /// <summary>Whether a worn object may be taken off now (restraints may lock one); null: every one may.</summary>
    public Func<Guid, bool>? CanDetachItem { get; set; }

    /// <summary>Shows a session's inventory (null: no session; the window is empty and its buttons are off).</summary>
    public void Bind(InventoryModel? model, Func<ActionIntent, ActionResult>? submit, DateTimeOffset? recentSince = null, Func<IReadOnlySet<Guid>>? wornObjects = null)
    {
        if (_model is not null)
            _model.Changed -= MarkDirty;
        _model = model;
        if (model is null)
        {
            _view = null;
            _actions = null;
            _outfit = null;
        }
        else
        {
            _view = new InventoryViewModel(model, _strings.Get("inventory.root.mine"), _strings.Get("inventory.root.library"))
            {
                Sort = (InventorySort)SortChoice.GetSelectedId(),
                Tab = Tabs.CurrentTab == 1 ? InventoryTab.Recent : InventoryTab.All,
                SearchText = Search.Text,
                RecentSince = recentSince,
            };
            _actions = submit is null ? null : new InventoryActions(model, submit);
            _outfit = submit is null ? null : new OutfitActions(model, submit, wornObjects, id => CanDetachItem?.Invoke(id) ?? true);
            model.Changed += MarkDirty;
        }
        _drawn = false;
        _selected = Guid.Empty;
        _pendingTrash = Guid.Empty;
        ConfirmBar.Visible = false;
        _shownRevision = -1;
        _treeRedraw.Reset();
        _dirty = true;
    }

    /// <summary>The model changed on another thread; the next frame redraws.</summary>
    private void MarkDirty() => _modelDirty = true;

    /// <summary>The model, for the development-build automation command.</summary>
    internal InventoryModel? Model => _model;
    internal InventoryViewModel? View => _view;
    internal InventoryActions? Actions => _actions;
    internal OutfitActions? Outfit => _outfit;

    // ---------------- drawing ----------------

    public override void _Process(double delta)
    {
        // what the user did redraws at once; changes from the network (a background fetch sends several a second) at most
        // four times a second, and nothing is drawn while the window is hidden
        long now = (long)Time.GetTicksMsec();
        if (!_gate.Due(now, _dirty, _modelDirty, IsVisibleInTree() || !_drawn))   // the first draw after a bind happens even hidden: it asks for the folders
            return;
        _dirty = _modelDirty = false;
        _gate.Drew(now);
        _drawn = true;
        Rebuild();
    }

    /// <summary>How many times the tree was cleared and drawn again (the automation's check that repeated reads of a folder do not redraw it).</summary>
    internal int TreeDraws { get; private set; }

    /// <summary>Draws the tree again from the view model, keeping the selection. When the rows are the ones drawn last the tree is left as it
    /// is: clearing it drops the pointer's place, the scroll position and a click in progress, which made a folder read again and again
    /// (a hypergrid suitcase, for one) jump under the pointer.</summary>
    public void Rebuild()
    {
        var rows = _view?.Rows() ?? (IReadOnlyList<InventoryRow>)Array.Empty<InventoryRow>();
        string worn = string.Join(",", rows.Where(IsWornRow).Select(r => r.Id));   // shown in a row's text, not in its data
        if (worn != _wornDrawn)
        {
            _wornDrawn = worn;
            _treeRedraw.Reset();
        }
        if (!_treeRedraw.Needs(rows) && !_scrollToSelected)
        {
            if (_view is not null)
                _shownRevision = _model!.Revision;
            RefreshControls();
            return;
        }
        TreeDraws++;
        _building = true;
        try
        {
            TreeView.Clear();
            var root = TreeView.CreateItem();
            TreeItem? selected = null;
            if (_view is not null)
            {
                var stack = new List<TreeItem> { root };
                foreach (var row in rows)
                {
                    while (stack.Count > row.Depth + 1)
                        stack.RemoveAt(stack.Count - 1);
                    var item = TreeView.CreateItem(stack[^1]);
                    item.SetText(0, RowText(row));
                    item.SetIcon(0, IconFor(row.IconKey));
                    item.SetMetadata(0, row.Id.ToString());
                    item.SetEditable(0, false);
                    if (row.IsFolder)
                    {
                        item.Collapsed = !row.Expanded;
                        if (!row.Expanded && row.CanExpand)
                            TreeView.CreateItem(item);   // a placeholder child, so the arrow shows and a click can open it
                        stack.Add(item);
                    }
                    if (row.Id == _selected)
                        selected = item;

                }
            }
            if (selected is not null)
            {
                selected.Select(0);
                if (_scrollToSelected)
                    TreeView.ScrollToItem(selected, true);
                _scrollToSelected = false;
            }
            else if (!_scrollToSelected)
                _selected = Guid.Empty;   // a row being revealed keeps its place until its folder has arrived
            TreeView.MarkAdded();
        }
        finally { _building = false; }
        if (_view is not null)
            _shownRevision = _model!.Revision;
        RefreshControls();
    }

    private string _wornDrawn = "";

    private bool IsWornRow(InventoryRow row) =>
        !row.IsFolder && _outfit is not null && (_outfit.IsWorn(row.Id) || _outfit.IsWorn(_model?.Item(row.Id)?.LinkedId ?? Guid.Empty));

    private string RowText(InventoryRow row) => IsWornRow(row)
        ? row.Name + "  " + _strings.Get("inventory.worn")
        : row.Load switch
    {
        FolderLoad.Loading when row.IsFolder && row.Expanded => row.Name + "  " + _strings.Get("inventory.loading"),
        FolderLoad.Failed when row.IsFolder => row.Name + "  " + _strings.Get("inventory.failed"),
        _ => row.Name,
    };

    private void RefreshControls()
    {
        bool bound = _view is not null;
        Search.Editable = bound;
        NewFolderButton.Disabled = !bound || _actions is null || _actions.WhyNotCreate(_selected) is not null;
        RenameButton.Disabled = !bound || _actions is null || _selected == Guid.Empty || _actions.WhyNotRename(_selected) is not null;
        TrashButton.Disabled = !bound || _actions is null || _selected == Guid.Empty || _actions.WhyNotTrash(_selected) is not null;
        RetryButton.Disabled = !bound || _selected == Guid.Empty || _model?.LoadState(_selected) != FolderLoad.Failed;
        bool picked = bound && _outfit is not null && _selected != Guid.Empty;
        bool folder = picked && _model!.Folder(_selected) is not null;
        WearButton.Disabled = !picked || !(folder ? _outfit!.CanWearOutfit(_selected) : _outfit!.CanWear(_selected));
        AddButton.Disabled = !picked || folder || !_outfit!.CanAdd(_selected);
        TakeOffButton.Disabled = !picked || folder || !_outfit!.CanTakeOff(_selected);
        AttachButton.Disabled = !picked || folder || !_outfit!.CanAttach(_selected);
        DetachButton.Disabled = !picked || folder || !_outfit!.CanDetach(_selected);
        if (_view is null)
        {
            StatusLabel.Text = _strings.Get("inventory.status.waiting");
            return;
        }
        var st = _view.Status();
        StatusLabel.Text = _strings.Get(st.Key, st.Args);
    }

    // ---------------- icons ----------------

    private ImageTexture IconFor(string key)
    {
        if (_icons.TryGetValue(key, out var have))
            return have;
        var img = Image.CreateEmpty(16, 16, false, Image.Format.Rgba8);
        var (body, accent) = Palette(key);
        bool folder = key.StartsWith("inv.folder") || key.StartsWith("inv.system") || key.StartsWith("inv.root") || key.StartsWith("inv.trash") || key.StartsWith("inv.lost") || key == "inv.link.folder";
        if (folder)
        {
            img.FillRect(new Rect2I(1, 3, 6, 2), accent);                   // the tab
            img.FillRect(new Rect2I(1, 5, 14, 9), body);
            if (key.EndsWith(".open"))
                img.FillRect(new Rect2I(3, 8, 12, 6), accent);                // the front flap of an open folder
            if (key.StartsWith("inv.trash"))
                img.FillRect(new Rect2I(5, 7, 6, 5), accent);
        }
        else
        {
            img.FillRect(new Rect2I(3, 1, 10, 14), body);                      // a sheet
            img.FillRect(new Rect2I(5, 4, 6, 2), accent);
            img.FillRect(new Rect2I(5, 8, 6, 2), accent);
            if (key == "inv.link")
                img.FillRect(new Rect2I(9, 10, 6, 5), accent);
        }
        var tex = ImageTexture.CreateFromImage(img);
        _icons[key] = tex;
        return tex;
    }

    private static (Color Body, Color Accent) Palette(string key)
    {
        // one body colour per kind, so kinds can be told apart without reading the name
        var hash = 17;
        foreach (char c in key.Replace(".open", ""))
            hash = hash * 31 + c;
        float hue = (Math.Abs(hash) % 360) / 360f;
        var t = UiTheme.Tokens;
        Color Light(UiColour c) => ShellTheme.C(c).Lerp(ShellTheme.C(t.Text), 0.4f);
        if (key.StartsWith("inv.folder")) return (ShellTheme.C(t.Warning), Light(t.Warning));
        if (key.StartsWith("inv.system")) return (ShellTheme.C(t.Accent), Light(t.Accent));
        if (key.StartsWith("inv.root")) return (ShellTheme.C(t.Success), Light(t.Success));
        if (key.StartsWith("inv.trash")) return (ShellTheme.C(t.TextSecondary), Light(t.TextSecondary));
        var other = ShellTheme.C(t.Accent);
        other.H = hue;   // the accent's saturation and value, a hue of the key's own
        return (other, ShellTheme.C(t.Text));
    }

    // ---------------- user input ----------------

    private void OnItemCollapsed(TreeItem item)
    {
        if (_building || _view is null)
            return;
        var id = InventoryTree.IdOf(item);
        if (id == Guid.Empty)
            return;
        // the tree already flipped; make the view model agree with it
        bool wantOpen = !item.Collapsed;
        if (wantOpen != _view.IsExpanded(id))
            _view.Toggle(id);
        _dirty = true;
    }

    private void OnItemSelected()
    {
        if (_building)
            return;
        _selected = InventoryTree.IdOf(TreeView.GetSelected());
        CancelConfirm();
        RefreshControls();
    }

    private readonly MenuToggle _contextToggle = new();

    /// <summary>The item or folder selected now (Guid.Empty for none).</summary>
    public Guid SelectedItem => _selected;

    /// <summary>Shows an item: every folder above it opened, the search and the Recent tab cleared so it is listed, and its row selected and
    /// scrolled into view once the tree is drawn.</summary>
    public void Reveal(Guid id)
    {
        if (_model is null || _view is null || id == Guid.Empty)
            return;
        Search.Clear();
        _view.SearchText = "";
        Tabs.CurrentTab = 0;
        _view.Tab = InventoryTab.All;
        Guid parent = _model.Item(id)?.ParentId ?? _model.Folder(id)?.ParentId ?? Guid.Empty;
        for (int guard = 0; parent != Guid.Empty && guard < 64; guard++)
        {
            _view.Expand(parent);
            parent = _model.Folder(parent)?.ParentId ?? Guid.Empty;
        }
        _selected = id;
        _scrollToSelected = true;
        _dirty = true;
    }

    private bool _scrollToSelected;

    /// <summary>Opens the selected row as a double-click or Enter does (the automation's check; also usable by a key binding).</summary>
    public bool OpenSelected()
    {
        if (_selected == Guid.Empty || !CanOpen(_selected))
            return false;
        OpenRequested?.Invoke(_selected);
        return true;
    }

    /// <summary>A double-click on a row's name or Enter: a folder opens or closes (the official folder view toggles it), an item that
    /// opens in a window of its own opens.</summary>
    private void OnRowActivated(Guid id)
    {
        _selected = id;
        if (_view is not null && _model?.Folder(id) is not null)
        {
            if (FolderTreeInput.Press(RowButton.Left, RowPart.Name, isFolder: true, doubleClick: true) == TreeAction.SelectAndToggle)
            {
                _view.Toggle(id);
                _dirty = true;
            }
            return;
        }
        if (CanOpen(id))
            OpenRequested?.Invoke(id);
    }

    /// <summary>Toggles a folder in the view as a double-click does (the automation's check).</summary>
    internal void Activate(Guid id) => OnRowActivated(id);

    private void OnRowMenu(Guid id)
    {
        if (!_contextToggle.PressOpens("context", (long)Time.GetTicksMsec()))
            return;   // the right-click that just closed it
        _selected = id;
        RefreshControls();
        Context.SetItemDisabled(0, NewFolderButton.Disabled);
        Context.SetItemDisabled(1, RenameButton.Disabled);
        Context.SetItemDisabled(2, TrashButton.Disabled);
        Context.SetItemDisabled(3, RetryButton.Disabled);
        Context.SetItemDisabled(4, WearButton.Disabled);
        Context.SetItemDisabled(5, AddButton.Disabled);
        Context.SetItemDisabled(6, TakeOffButton.Disabled);
        Context.SetItemDisabled(7, AttachButton.Disabled);
        Context.SetItemDisabled(8, DetachButton.Disabled);
        Context.SetItemDisabled(Context.GetItemIndex(9), !CanOpen(_selected));
        // at the pointer: in the window's own coordinates when popups are drawn inside it, in screen coordinates when they are windows of their own
        Context.Position = GetViewport().GuiEmbedSubwindows ? (Vector2I)GetGlobalMousePosition() : DisplayServer.MouseGetPosition();
        Context.Popup();
    }

    /// <summary>Whether an item opens in a window of its own: a notecard or a texture, or a link to one.</summary>
    public bool CanOpen(Guid id) =>
        _model?.Item(id) is { } item && (Opens(item.Kind) || (item.LinkedId is { } target && _model.Item(target) is { } linked && Opens(linked.Kind)));

    private static bool Opens(ItemKind kind) => kind is ItemKind.Notecard or ItemKind.Texture or ItemKind.Script;

    /// <summary>The folder a drop on this row means: the row itself if a folder, otherwise the item's folder.</summary>
    private Guid DropFolder(Guid target) => _model?.Folder(target) is not null ? target : _model?.Item(target)?.ParentId ?? Guid.Empty;

    /// <summary>Opens a folder in the view (also the entry the start-up check uses).</summary>
    public void Expand(Guid folderId)
    {
        _view?.Expand(folderId);
        _dirty = true;
    }

    /// <summary>A row was dropped on another row (also the entry the start-up check uses).</summary>
    public void Drop(Guid dragged, Guid target) => OnDropped(new[] { dragged }, target);

    private void OnDropped(IReadOnlyList<Guid> dragged, Guid target)
    {
        if (_actions is null)
            return;
        var results = _actions.MoveMany(dragged, DropFolder(target));
        TreeView.Added.Clear();
        TreeView.MarkAdded();
        // a refusal is what the user needs to hear; otherwise the first result says what happened
        Show(results.FirstOrDefault(r => !r.Submitted) ?? results.FirstOrDefault() ?? new InventoryActionResult(false, null, null));
    }

    /// <summary>New folder under the selection (or My Inventory), named from the string table; it then waits to be named.</summary>
    public void OnNewFolder()
    {
        if (_actions is null)
            return;
        Show(_actions.NewFolder(_selected, _strings.Get("inventory.default_folder")));
    }

    /// <summary>Starts editing the selected row's name in place.</summary>
    public void OnRename()
    {
        if (_actions is null || TreeView.GetSelected() is not { } item)
            return;
        if (_actions.WhyNotRename(_selected) is { } why)
        {
            ShowRefusal(why);
            return;
        }
        item.SetEditable(0, true);
        item.SetText(0, _model?.Folder(_selected)?.Name ?? _model?.Item(_selected)?.Name ?? item.GetText(0));
        TreeView.EditSelected();
    }

    private void OnItemEdited()
    {
        if (_building || _actions is null)
            return;
        var item = TreeView.GetEdited();
        var id = InventoryTree.IdOf(item);
        string text = item?.GetText(0) ?? "";
        if (item is not null)
            item.SetEditable(0, false);
        if (id != Guid.Empty)
            Show(_actions.Rename(id, text));
        _dirty = true;
    }

    /// <summary>Asks to move the selection to the Trash: a bar under the tree asks first.</summary>
    public void OnTrash()
    {
        if (_actions is null)
            return;
        if (_actions.WhyNotTrash(_selected) is { } why)
        {
            ShowRefusal(why);
            return;
        }
        _pendingTrash = _selected;
        string name = _model?.Folder(_selected)?.Name ?? _model?.Item(_selected)?.Name ?? "";
        ConfirmText.Text = _strings.Get("inventory.confirm.question", name);
        ConfirmBar.Visible = true;
    }

    private void OnConfirmYes()
    {
        var id = _pendingTrash;
        CancelConfirm();
        if (_actions is not null && id != Guid.Empty)
            Show(_actions.Trash(id, confirmed: true));
    }

    private void OnConfirmNo() => CancelConfirm();

    private void CancelConfirm()
    {
        _pendingTrash = Guid.Empty;
        ConfirmBar.Visible = false;
    }

    /// <summary>Wears the selected item (replacing what it replaces), or the selected outfit folder.</summary>
    public void OnWear()
    {
        if (_outfit is null || _selected == Guid.Empty)
            return;
        ShowOutfit(_model?.Folder(_selected) is not null ? _outfit.WearOutfit(_selected) : _outfit.Wear(_selected));
    }

    public void OnAdd()
    {
        if (_outfit is not null && _selected != Guid.Empty)
            ShowOutfit(_outfit.Add(_selected));
    }

    public void OnTakeOff()
    {
        if (_outfit is not null && _selected != Guid.Empty)
            ShowOutfit(_outfit.TakeOff(_selected));
    }

    /// <summary>Takes the selected worn object off ("Detach From Yourself" in the official viewer's inventory menu).</summary>
    public void OnDetach()
    {
        if (_outfit is not null && _selected != Guid.Empty)
            ShowOutfit(_outfit.Detach(_selected));
    }

    private void OnAttachMenu()
    {
        AttachMenu.Position = new Vector2I((int)GetScreenPosition().X + 40, (int)GetScreenPosition().Y + 80);
        AttachMenu.Popup();
    }

    /// <summary>Puts the selected object on a point of the avatar (0: the point it was last worn at).</summary>
    public void OnAttach(int point)
    {
        if (_outfit is not null && _selected != Guid.Empty)
            ShowOutfit(_outfit.Attach(_selected, point, add: false));
    }

    private void ShowOutfit(OutfitActionResult result)
    {
        _dirty = true;
        if (result.Submitted)
        {
            NoticeLabel.Text = string.Empty;
            return;
        }
        NoticeLabel.Text = OutfitFeature.RefusalText(_strings, result.Code);
    }

    private void OnRetry()
    {
        if (_model is not null && _selected != Guid.Empty && _model.Retry(_selected))
            _dirty = true;
    }

    // ---------------- messages ----------------

    private void Show(InventoryActionResult result)
    {
        if (result.Submitted)
        {
            NoticeLabel.Text = string.Empty;
            return;
        }
        ShowRefusal(result.Code ?? result.Pipeline?.Status.ToString() ?? "other");
    }

    private void ShowRefusal(string code)
    {
        string key = "inventory.refused." + (code.StartsWith("inventory.") ? code["inventory.".Length..] : code);
        NoticeLabel.Text = _strings.Has(key) ? _strings.Get(key) : _strings.Get("inventory.refused.other");
    }
}
