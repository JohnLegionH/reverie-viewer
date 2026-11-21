// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Viewer.Shell;
using Viewer.Shell.Ui;
using Viewer.Shell.Notifications;

namespace Viewer.Godot;

/// <summary>
/// Notifications in the running viewer, built by hand in code: toasts at the top right, a modal layer for alerts, a floater that lists
/// what waits for the user, and the window an object's name opens. They only draw the models in <see cref="NotificationHub"/> (tested
/// without the engine) and hand clicks back to them. Behaviour follows the official viewer's notifications and script dialogs (button
/// placement, Ignore, long messages that scroll); no layout file of any viewer is used.
/// </summary>
public static class NotificationFeature
{
    public const string ListId = "notifications";
    public const string ObjectId = "object_info";

    /// <summary>The pieces of the feature once installed.</summary>
    public sealed record Panels(ToastStack Toasts, NotificationListPanel List, ObjectInfoPanel Object);

    /// <summary>Adds the toast layer, the list floater, the object window, a Communicate menu entry, a toolbar button and their commands to
    /// a shell that is already drawn. The Communicate menu must exist (the chat feature adds it).</summary>
    public static Panels Install(ShellRoot shell)
    {
        var s = shell.Shell;
        var toasts = new ToastStack(s.Strings);
        var list = new NotificationListPanel(s.Strings);
        var obj = new ObjectInfoPanel(s.Strings);
        shell.MainView.AddChild(toasts);
        ReverieRestyle.Apply(toasts);   // notices, script dialogs and permission questions in the Reverie look
        shell.AddFloater(new FloaterSpec(ListId, "notifications.title", new UiSize(380, 440), new UiSize(320, 220)), list);
        shell.AddFloater(new FloaterSpec(ObjectId, "object.info.title", new UiSize(360, 220), new UiSize(280, 160)), obj);
        s.Commands.Register("floater.notifications", () => s.Floaters.Toggle(ListId));
        s.Menus.AddEntry("communicate", new MenuEntry("communicate.notifications", "menu.communicate.notifications", "floater.notifications"));
        s.Toolbar.Add(new ToolbarButton("toolbar_notifications", "toolbar.notifications", "floater.notifications", UiIcons.Notifications, ListId));
        toasts.OpenList = () => s.Floaters.Open(ListId);
        return new Panels(toasts, list, obj);
    }
}

/// <summary>Words and widgets shared by the notification windows.</summary>
internal static class NotificationView
{
    public static string Words(NoticeText? text, StringTable strings)
    {
        if (text is null)
            return "";
        if (text.Literal is { } literal)
            return literal;
        string key = text.Key ?? "";
        if (!strings.Has(key))
            return text.Arg ?? key;
        return text.Arg is null ? strings.Get(key) : strings.Get(key, text.Arg);
    }

    /// <summary>One notification as a panel: title, owner line, message (a scroll area when long), the lines under it, then the answer
    /// controls. A script dialog's buttons are in rows of three with the first button bottom-left and Ignore on a row of its own at
    /// the bottom right; a text box is a field and Submit; any other prompt has its buttons in one row.</summary>
    public static PanelContainer Build(Notification n, StringTable strings, ThemeSpec theme, Action<long, int, string> answer, Action<long>? hide)
    {
        var panel = new PanelContainer { Name = "notification_" + n.Id, CustomMinimumSize = new Vector2(ToastWidth, 0), MouseFilter = Control.MouseFilterEnum.Stop };
        string title = Words(n.Title, strings);
        panel.AccessibilityName = title.Length > 0 ? title : Words(n.Body, strings);
        var box = new VBoxContainer { Name = "content" };
        box.AddThemeConstantOverride("separation", 6);
        panel.AddChild(box);

        var header = new HBoxContainer { Name = "header" };
        box.AddChild(header);
        var titleLabel = new Label
        {
            Name = "title", Text = title.Length > 0 ? title : strings.Get("notifications.toast"), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis, MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        titleLabel.AccessibilityName = titleLabel.Text;
        header.AddChild(titleLabel);
        if (hide is not null)
        {
            var close = new RvIconButton(LucideIcons.Close, strings.Get(NoticeClose.TooltipKey(n))) { Name = "hide" };
            long id = n.Id;
            close.Pressed += () => hide(id);
            header.AddChild(close);
        }

        if (n.Subtitle is not null && Words(n.Subtitle, strings) is { Length: > 0 } owner)
        {
            string shown = n.ScriptDialog || n.Source != Guid.Empty ? strings.Get("script.dialog.owner", owner) : owner;
            box.AddChild(Note("owner", shown, strings.Get("script.dialog.owner", owner), UiTheme.MutedLabel));
        }

        string body = Words(n.Body, strings);
        if (body.Length > 0)
        {
            // a long message scrolls rather than growing the window past the screen
            var scroll = new ScrollContainer
            {
                Name = "message_scroll", HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
                CustomMinimumSize = new Vector2(ToastWidth - 30, MessageHeight(body)),
            };
            var text = Note("message", body, body, n.Caution ? UiTheme.ErrorLabel : "");
            text.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            scroll.AddChild(text);
            box.AddChild(scroll);
        }
        for (int i = 0; i < n.Details.Count; i++)
            box.AddChild(Note("detail_" + i, "  • " + Words(n.Details[i], strings), Words(n.Details[i], strings), ""));

        if (n.TextEntry)
            BuildTextEntry(box, n, strings, answer);
        else if (n.ScriptDialog)
            BuildDialogButtons(box, n, strings, answer);
        else if (n.Buttons.Count > 0)
        {
            var row = new HBoxContainer { Name = "buttons", Alignment = BoxContainer.AlignmentMode.End };
            foreach (var b in n.Buttons)
                row.AddChild(AnswerButton(n.Id, b, strings, answer));
            if (n.CanBlock)
                row.AddChild(BlockButton(n.Id, strings, answer));
            box.AddChild(row);
        }
        return panel;
    }

    /// <summary>The width of a toast, and the tallest the message area grows before it scrolls.</summary>
    public const float ToastWidth = 330, MaxMessageHeight = 150;

    private static float MessageHeight(string body)
    {
        int lines = body.Split('\n').Sum(l => Math.Max(1, (l.Length + 38) / 39));
        return Math.Clamp(lines * 22f, 24f, MaxMessageHeight);
    }

    private static Label Note(string name, string text, string accessible, string variation)
    {
        var label = new Label { Name = name, Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(ToastWidth - 40, 0) };
        label.AccessibilityName = accessible;
        label.ThemeTypeVariation = variation;   // muted, error or the body text, from the theme
        return label;
    }

    private static Button AnswerButton(long id, NoticeButton b, StringTable strings, Action<long, int, string> answer, float width = 0)
    {
        string text = Words(b.Label, strings);
        var button = new Button { Name = "button_" + b.Index, Text = text, ClipText = true, CustomMinimumSize = new Vector2(width > 0 ? width : 72, 28) };   // clipped text would otherwise leave a button with no width
        button.AccessibilityName = text;
        int index = b.Index;
        button.Pressed += () => answer(id, index, "");
        return button;
    }

    /// <summary>The script's buttons in rows of three, the first at the bottom left (rows are added upward, so the partial row is on top),
    /// and Ignore on its own row below them, at the right.</summary>
    private static void BuildDialogButtons(VBoxContainer box, Notification n, StringTable strings, Action<long, int, string> answer)
    {
        var layout = DialogLayout.For(n.Buttons.Count);
        var grid = new VBoxContainer { Name = "button_rows" };
        grid.AddThemeConstantOverride("separation", 4);
        foreach (var rowIndices in layout.Rows)
        {
            var row = new HBoxContainer { Name = "button_row" };
            row.AddThemeConstantOverride("separation", 4);
            foreach (int i in rowIndices)
                row.AddChild(AnswerButton(n.Id, n.Buttons[i], strings, answer, width: 96));
            grid.AddChild(row);
        }
        box.AddChild(grid);
        box.AddChild(IgnoreRow(n.Id, strings, answer, n.CanBlock));
    }

    /// <summary>The Block button of a prompt: blocks the object (or the sender of an offer) the prompt came from.</summary>
    private static Button BlockButton(long id, StringTable strings, Action<long, int, string> answer)
    {
        var block = new Button { Name = "block", Text = strings.Get("button.block"), ClipText = true, CustomMinimumSize = new Vector2(72, 28) };
        block.AccessibilityName = strings.Get("button.block");
        block.Pressed += () => answer(id, NotificationAnswer.BlockIndex, "");
        return block;
    }

    private static HBoxContainer IgnoreRow(long id, StringTable strings, Action<long, int, string> answer, bool canBlock = false)
    {
        var row = new HBoxContainer { Name = "ignore_row", Alignment = BoxContainer.AlignmentMode.End };
        if (canBlock)
            row.AddChild(BlockButton(id, strings, answer));
        var ignore = new Button { Name = "ignore", Text = strings.Get("button.ignore"), CustomMinimumSize = new Vector2(96, 28) };
        ignore.AccessibilityName = strings.Get("button.ignore");
        ignore.Pressed += () => answer(id, -1, "");
        row.AddChild(ignore);
        return row;
    }

    private static void BuildTextEntry(VBoxContainer box, Notification n, StringTable strings, Action<long, int, string> answer)
    {
        var field = new LineEdit { Name = "reply", PlaceholderText = strings.Get("script.textbox.hint"), MaxLength = ScriptPrompts.MaxLabelBytes, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        field.AccessibilityName = strings.Get("script.textbox.label");
        box.AddChild(field);
        var row = new HBoxContainer { Name = "submit_row", Alignment = BoxContainer.AlignmentMode.End };
        var submit = new Button { Name = "submit", Text = strings.Get("button.submit"), CustomMinimumSize = new Vector2(96, 28) };
        submit.AccessibilityName = strings.Get("button.submit");
        long id = n.Id;
        submit.Pressed += () => answer(id, 0, field.Text);
        field.TextSubmitted += t => answer(id, 0, t);
        row.AddChild(submit);
        box.AddChild(row);
        box.AddChild(IgnoreRow(id, strings, answer));
    }
}

/// <summary>
/// The one dialog for alerts and confirmations: centred, modal (the layer behind it takes no clicks), as wide as its text wraps at
/// <see cref="TextWidth"/> and as tall as the text needs, with a title, and buttons sized to their labels at the bottom right in one row. The
/// default button is filled and holds the keyboard focus, so Enter answers it; Tab and the arrow keys move between the buttons; Esc is
/// handled by <see cref="NotificationModel.PressEscape"/> (after its one-second guard). Behaviour after the official viewer's alert panel
/// (lltoastalertpanel.cpp): its text width, default button and keys, written from the behaviour, not copied.
/// </summary>
internal static class AlertDialogView
{
    /// <summary>The width the message wraps at, as the official alert panel does.</summary>
    public const float TextWidth = 400;

    public static (PanelContainer Panel, Button? Default) Build(Notification n, StringTable strings, Action<long, int, string> answer)
    {
        string title = n.Title is null ? strings.Get("alert.title") : NotificationView.Words(n.Title, strings);
        var panel = new PanelContainer { Name = "alert_" + n.Id, MouseFilter = Control.MouseFilterEnum.Stop };
        panel.AccessibilityName = title;
        var box = new VBoxContainer { Name = "content" };
        box.AddThemeConstantOverride("separation", 14);
        panel.AddChild(box);
        var titleLabel = new Label { Name = "title", Text = title, ThemeTypeVariation = UiTheme.TitleLabel, MouseFilter = Control.MouseFilterEnum.Ignore };
        titleLabel.AccessibilityName = title;
        box.AddChild(titleLabel);
        string body = NotificationView.Words(n.Body, strings);
        var text = new Label
        {
            Name = "message", Text = body, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(TextWidth, 0),
            ThemeTypeVariation = n.Caution ? UiTheme.ErrorLabel : "",
        };
        text.AccessibilityName = body;
        box.AddChild(text);
        var row = new HBoxContainer { Name = "buttons", Alignment = BoxContainer.AlignmentMode.End };
        row.AddThemeConstantOverride("separation", 8);
        Button? def = null;
        foreach (var b in n.Buttons)
        {
            string label = NotificationView.Words(b.Label, strings);
            var button = new Button { Name = "button_" + b.Index, Text = label, CustomMinimumSize = new Vector2(0, 32) };   // sized to the label: no fixed width, nothing clipped
            button.AccessibilityName = label;
            if (n.DefaultButton == b.Index)
            {
                button.ThemeTypeVariation = UiTheme.PrimaryButton;   // the default button is filled
                def = button;
            }
            long id = n.Id;
            int index = b.Index;
            button.Pressed += () => answer(id, index, "");
            row.AddChild(button);
        }
        box.AddChild(row);
        return (panel, def);
    }
}

/// <summary>
/// The toasts at the top right (newest last, scrolling when they would run off the screen), the modal layer for an alert, and a small
/// button that opens the list while something waits. A mouse click anywhere else passes through.
/// </summary>
public partial class ToastStack : Control
{
    private readonly StringTable _strings;
    private static ThemeSpec _theme => UiTheme.Spec;
    private NotificationHub? _hub;
    private bool _dirty = true;

    public ScrollContainer Scroll { get; }
    public VBoxContainer Stack { get; }
    public Button Badge { get; }

    /// <summary>How many notifications in the list are unread (the toolbar's bell shows it).</summary>
    public int Unread { get; private set; }
    public ColorRect Dim { get; }
    public CenterContainer ModalCentre { get; }

    /// <summary>Opens the list of waiting notifications (the badge's action).</summary>
    public Action? OpenList { get; set; }

    public ToastStack(StringTable strings)
    {
        _strings = strings;
        Name = "toast_stack";
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;

        Scroll = new ScrollContainer { Name = "toast_scroll", HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, MouseFilter = MouseFilterEnum.Ignore };
        Scroll.AnchorLeft = 1; Scroll.AnchorRight = 1; Scroll.AnchorTop = 0; Scroll.AnchorBottom = 1;
        Scroll.OffsetLeft = -(NotificationView.ToastWidth + 40); Scroll.OffsetRight = -8; Scroll.OffsetTop = 70; Scroll.OffsetBottom = -90;
        AddChild(Scroll);
        Stack = new VBoxContainer { Name = "toasts", MouseFilter = MouseFilterEnum.Ignore, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        Stack.AddThemeConstantOverride("separation", 6);
        Scroll.AddChild(Stack);

        Badge = new Button { Name = "badge", Visible = false };
        Badge.AnchorLeft = 1; Badge.AnchorRight = 1;
        Badge.OffsetLeft = -(NotificationView.ToastWidth + 40); Badge.OffsetRight = -8; Badge.OffsetTop = 40; Badge.OffsetBottom = 66;
        Badge.Pressed += () => OpenList?.Invoke();
        AddChild(Badge);

        Dim = new ColorRect { Name = "modal_dim", Color = ShellTheme.C(UiTheme.Tokens.Scrim), Visible = false, MouseFilter = MouseFilterEnum.Stop };
        Dim.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(Dim);
        ModalCentre = new CenterContainer { Name = "modal_centre", Visible = false, MouseFilter = MouseFilterEnum.Ignore };
        ModalCentre.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(ModalCentre);
    }

    /// <summary>Shows a session's notifications (null: none; everything is hidden).</summary>
    public void Bind(NotificationHub? hub)
    {
        if (_hub is not null)
            _hub.Model.Changed -= MarkDirty;
        _hub = hub;
        if (hub is not null)
            hub.Model.Changed += MarkDirty;
        _dirty = true;
    }

    private void MarkDirty() => _dirty = true;

    public override void _Process(double delta)
    {
        if (!_dirty)
            return;
        _dirty = false;
        foreach (var child in Stack.GetChildren().Concat(ModalCentre.GetChildren()).ToArray())
        {
            child.GetParent().RemoveChild(child);
            child.QueueFree();
        }
        var model = _hub?.Model;
        if (model is null)
        {
            Badge.Visible = Dim.Visible = ModalCentre.Visible = false;
            Unread = 0;
            return;
        }
        foreach (var toast in model.Toasts)
        {
            var card = NotificationView.Build(toast, _strings, _theme, Answer, id => model.CloseToast(id));
            if (toast.Clicked is not null)
            {
                // pressing the card (not one of its buttons) does what the notice says: a friend notice opens the conversation with that friend
                long toastId = toast.Id;
                card.MouseDefaultCursorShape = Control.CursorShape.PointingHand;
                card.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) model.ClickToast(toastId); };
            }
            Stack.AddChild(card);
        }
        int unread = model.UnreadCount;
        Unread = unread;   // the toolbar's bell shows the count; this button stays for the list shortcut and is not drawn
        Badge.Visible = false;
        Badge.Text = _strings.Get("notifications.unread", unread);
        Badge.AccessibilityName = Badge.Text;
        var modal = model.Modal;
        Dim.Visible = ModalCentre.Visible = modal is not null;
        if (modal is not null)
        {
            var (alert, def) = AlertDialogView.Build(modal, _strings, Answer);
            ModalCentre.AddChild(alert);
            // the default button holds the keyboard, so Enter answers it, and a newly opened alert takes focus from whatever was being typed in
            def?.CallDeferred(Control.MethodName.GrabFocus);
        }
    }

    private void Answer(long id, int button, string text) => _hub?.Model.Answer(id, button, text);

    /// <summary>Enter with an alert up and the keyboard elsewhere (the focus was lost): the alert's default button answers. With the focus on one
    /// of the alert's buttons that button takes Enter itself, so Tab and the arrow keys can choose another one first.</summary>
    public override void _Input(InputEvent e)
    {
        if (_hub?.Model.Modal is null || e is not InputEventKey { Pressed: true, Echo: false, Keycode: Key.Enter or Key.KpEnter })
            return;
        var owner = GetViewport().GuiGetFocusOwner();
        if (owner is not null && ModalCentre.IsAncestorOf(owner))
            return;
        if (_hub.Model.PressEnter())
            GetViewport().SetInputAsHandled();
    }
}

/// <summary>The list floater: what waits for the user, each with its answer controls, and the last few that were answered or expired.</summary>
public partial class NotificationListPanel : VBoxContainer
{
    private readonly StringTable _strings;
    private static ThemeSpec _theme => UiTheme.Spec;
    private NotificationHub? _hub;
    private bool _dirty = true;

    public Label Count { get; }
    public Button MarkAll { get; }
    public VBoxContainer Rows { get; }

    public NotificationListPanel(StringTable strings)
    {
        _strings = strings;
        Name = "notification_list";
        var head = new HBoxContainer { Name = "head" };
        Count = new Label { Name = "count", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        MarkAll = new Button { Name = "mark_all", Text = strings.Get("notifications.mark_read") };
        MarkAll.AccessibilityName = strings.Get("notifications.mark_read");
        MarkAll.Pressed += () => _hub?.Model.MarkAllRead();
        head.AddChild(Count);
        head.AddChild(MarkAll);
        AddChild(head);
        var scroll = new ScrollContainer { Name = "scroll", SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        scroll.AccessibilityName = strings.Get("notifications.list");
        Rows = new VBoxContainer { Name = "rows", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        Rows.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(Rows);
        AddChild(scroll);
    }

    public void Bind(NotificationHub? hub)
    {
        if (_hub is not null)
            _hub.Model.Changed -= MarkDirty;
        _hub = hub;
        if (hub is not null)
            hub.Model.Changed += MarkDirty;
        _dirty = true;
    }

    private void MarkDirty() => _dirty = true;

    public override void _Process(double delta)
    {
        // opening the list reads everything in it
        if (_hub is not null && IsVisibleInTree() && _hub.Model.UnreadCount > 0)
            _hub.Model.MarkAllRead();
        if (!_dirty)
            return;
        _dirty = false;
        foreach (var child in Rows.GetChildren().ToArray())
        {
            Rows.RemoveChild(child);
            child.QueueFree();
        }
        var model = _hub?.Model;
        Count.Text = _strings.Get("notifications.unread", model?.List.Count ?? 0);
        Count.AccessibilityName = Count.Text;
        MarkAll.Disabled = !ButtonAvailability.MarkAllReadEnabled(model?.List.Count ?? 0);
        if (model is null)
            return;
        if (model.List.Count == 0)
            Rows.AddChild(new Label { Name = "none", Text = _strings.Get("notifications.none"), AutowrapMode = TextServer.AutowrapMode.WordSmart });
        foreach (var n in model.List)
            Rows.AddChild(NotificationView.Build(n, _strings, _theme, (id, b, t) => model.Answer(id, b, t), null));
        var earlier = model.History.TakeLast(10).Reverse().ToArray();
        if (earlier.Length == 0)
            return;
        var heading = new Label { Name = "earlier", Text = _strings.Get("notifications.earlier"), ThemeTypeVariation = UiTheme.MutedLabel };
        Rows.AddChild(heading);
        foreach (var n in earlier)
        {
            string text = NotificationView.Words(n.Title, _strings) is { Length: > 0 } t ? t + ": " + NotificationView.Words(n.Body, _strings) : NotificationView.Words(n.Body, _strings);
            var line = new Label { Name = "earlier_line", Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(240, 0) };
            line.AccessibilityName = text;
            line.ThemeTypeVariation = UiTheme.MutedLabel;
            Rows.AddChild(line);
        }
    }
}

/// <summary>The window an object's name in chat opens: its name, owner, where it said it was, and a button that shows the place on the world map.</summary>
public partial class ObjectInfoPanel : VBoxContainer
{
    private readonly StringTable _strings;

    public Label NameLabel { get; }
    public Label OwnerLabel { get; }
    public Label PlaceLabel { get; }
    public Button ShowOnMap { get; }

    /// <summary>The object shown now; null before anything was opened.</summary>
    public ObjectInfo? Shown { get; private set; }

    /// <summary>The Show on map button was pressed for this object.</summary>
    public event Action<ObjectInfo>? MapRequested;

    public ObjectInfoPanel(StringTable strings)
    {
        _strings = strings;
        Name = "object_info_panel";
        NameLabel = Line("name");
        OwnerLabel = Line("owner");
        PlaceLabel = Line("place");
        ShowOnMap = new Button { Name = "show_on_map", Text = strings.Get("object.info.show_map"), Disabled = true };
        ShowOnMap.AccessibilityName = strings.Get("object.info.show_map");
        ShowOnMap.Pressed += () => { if (Shown is { } info) MapRequested?.Invoke(info); };
        AddChild(ShowOnMap);
    }

    private Label Line(string name)
    {
        var label = new Label { Name = name, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(240, 0) };
        AddChild(label);
        return label;
    }

    /// <summary>Shows an object. <paramref name="ownerName"/> is the owner's name when the viewer knows it.</summary>
    public void Show(ObjectInfo info, string? ownerName)
    {
        Shown = info;
        NameLabel.Text = _strings.Get("object.info.name", info.Name);
        OwnerLabel.Text = ownerName is null ? _strings.Get("object.info.owner_unknown") : _strings.Get("object.info.owner", ownerName);
        if (info.Place.Length > 0)
            PlaceLabel.Text = _strings.Get("object.info.place", info.Place);
        else if (info.RegionPosition is { } p)
            PlaceLabel.Text = _strings.Get("object.info.position", (int)p.X, (int)p.Y, (int)p.Z);
        else
            PlaceLabel.Text = string.Empty;
        foreach (var l in new[] { NameLabel, OwnerLabel, PlaceLabel })
            l.AccessibilityName = l.Text;
        ShowOnMap.Disabled = info.Place.Length == 0 && info.RegionPosition is null;
    }
}
