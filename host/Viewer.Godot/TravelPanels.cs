// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Viewer.Core.Map;
using Viewer.Shell;
using Viewer.Shell.Chat;
using Viewer.Shell.Social;
using Viewer.Shell.Travel;

namespace Viewer.Godot;

/// <summary>
/// Teleport, the world map and the mini-map in the running viewer: a World menu, toolbar buttons, three floaters and the progress
/// panel that covers a teleport. They only draw the models in <see cref="TravelHub"/> (tested without the engine) and hand clicks
/// back to them; every text comes from the string table and every control is named for assistive technology. Behaviour follows the
/// official viewer's teleport, world map and mini-map; no layout file of any viewer is used.
/// </summary>
public static class TravelFeature
{
    public const string TeleportId = "teleport";
    public const string MapId = "world_map";
    public const string MiniMapId = "minimap";

    /// <summary>The pieces of the feature once installed.</summary>
    public sealed record Panels(TeleportPanel Teleport, TeleportOverlay Overlay, WorldMapPanel Map, MiniMapPanel Mini);

    /// <summary>Adds the World menu, the floaters, the toolbar buttons, the progress panel and their commands to a shell that is already
    /// drawn (the menus and toolbar draw themselves again). Returns the panels, to be bound to a session's hub.</summary>
    public static Panels Install(ShellRoot shell, SettingsService settings)
    {
        var s = shell.Shell;
        var teleport = new TeleportPanel(s.Strings);
        var overlay = new TeleportOverlay(s.Strings);
        var map = new WorldMapPanel(s.Strings);
        var mini = new MiniMapPanel(s.Strings);
        shell.MainView.AddChild(overlay);
        // the Teleport window holds more than its default height shows (destination, requests, offers, an offer to a friend): it scrolls
        var teleportScroll = new ScrollContainer { Name = "teleport_scroll", HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        teleport.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        teleportScroll.AddChild(teleport);
        shell.AddFloater(new FloaterSpec(TeleportId, "teleport.title", new UiSize(420, 460), new UiSize(320, 300)), teleportScroll);
        shell.AddFloater(new FloaterSpec(MapId, "map.title", new UiSize(720, 560), new UiSize(460, 360)), map);
        shell.AddFloater(new FloaterSpec(MiniMapId, "minimap.title", new UiSize(360, 300), new UiSize(200, 200)), mini);
        s.Commands.Register("floater.teleport", () => s.Floaters.Toggle(TeleportId));
        s.Commands.Register("floater.map", () => s.Floaters.Toggle(MapId));
        s.Commands.Register("floater.minimap", () => s.Floaters.Toggle(MiniMapId));
        s.Commands.Register("travel.home", () => overlay.GoHome());
        s.Commands.Register("travel.set_home", () => overlay.SetHome());
        s.Menus.AddMenu("world", "menu.world");
        s.Menus.AddEntry("world", new MenuEntry("world.teleport", "menu.world.teleport", "floater.teleport"));
        s.Menus.AddEntry("world", new MenuEntry("world.home", "menu.world.home", "travel.home"));
        s.Menus.AddEntry("world", new MenuEntry("world.set_home", "menu.world.set_home", "travel.set_home"));
        s.Menus.AddEntry("world", new MenuEntry("world.map", "menu.world.map", "floater.map"));
        s.Menus.AddEntry("world", new MenuEntry("world.minimap", "menu.world.minimap", "floater.minimap"));
        // World > About land, as the official menu has it (menu_viewer.xml "About land"); its command comes with the interaction windows
        s.Menus.AddEntry("world", new MenuEntry("world.about_land", "menu.world.about_land", "floater.about_land"));
        s.Toolbar.Add(new ToolbarButton("toolbar_teleport", "toolbar.teleport", "floater.teleport", UiIcons.Teleport, TeleportId));
        s.Toolbar.Add(new ToolbarButton("toolbar_map", "toolbar.map", "floater.map", UiIcons.Map, MapId));
        s.Toolbar.Add(new ToolbarButton("toolbar_minimap", "toolbar.minimap", "floater.minimap", UiIcons.MiniMap, MiniMapId));
        return new Panels(teleport, overlay, map, mini);
    }
}

/// <summary>Words for the travel models' (key, argument) pairs.</summary>
internal static class TravelText
{
    /// <summary>The text for a key and its argument. A text with a place for an argument and none given says "that region" (the
    /// grid names none for some refusals).</summary>
    public static string Words(StringTable strings, (string Key, string? Arg) t)
    {
        string key = strings.Has(t.Key) ? t.Key : "status.invalid";
        string arg = t.Arg ?? (strings.Get(key).Contains("{0}") ? strings.Get("teleport.arg.fallback") : "");
        return strings.Get(key, arg);
    }

    public static Button Button(string name, string text, Action pressed)
    {
        var b = new Button { Name = name, Text = text };
        b.AccessibilityName = text;
        b.Pressed += pressed;
        return b;
    }

    public static Label Note(string name, string accessibleName, ThemeSpec theme) => ChatFeature.Note(name, accessibleName, theme);
}

/// <summary>
/// The progress panel over the world while a teleport goes on: what the grid last said, a Cancel button while the grid allows it,
/// and, after a failure, the reason with a Close button. It sits in the main view and lets the pointer through everywhere else.
/// </summary>
public partial class TeleportOverlay : Control
{
    private readonly StringTable _strings;
    private static ThemeSpec _theme => UiTheme.Spec;
    private TravelHub? _hub;
    private bool _dirty = true;

    public PanelContainer Box { get; }
    public Label Title { get; }
    public Label Message { get; }
    public Button CancelButton { get; }
    public Button CloseButton { get; }
    public PanelContainer NoticeBox { get; }
    public Label NoticeText { get; }
    public Button NoticeClose { get; }

    /// <summary>Whether the region's notices are shown here as a strip at the top of the view. Off once the notification toasts show them.</summary>
    public bool NoticesShownHere { get; set; } = true;

    public TeleportOverlay(StringTable strings)
    {
        _strings = strings;
        Name = "teleport_overlay";
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        var centre = new CenterContainer { Name = "centre", MouseFilter = MouseFilterEnum.Ignore };
        centre.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(centre);
        Box = new PanelContainer { Name = "box", MouseFilter = MouseFilterEnum.Stop, Visible = false };
        Box.AccessibilityName = strings.Get("teleport.panel.label");
        centre.AddChild(Box);
        var v = new VBoxContainer { Name = "content" };
        v.AddThemeConstantOverride("separation", 8);
        Box.AddChild(v);
        Title = TravelText.Note("title", strings.Get("teleport.panel.title"), _theme);
        Title.Text = strings.Get("teleport.panel.title");
        Message = TravelText.Note("message", strings.Get("teleport.panel.label"), _theme);
        Message.CustomMinimumSize = new Vector2(320, 0);
        Message.ThemeTypeVariation = "";
        v.AddChild(Title);
        v.AddChild(Message);
        var row = new HBoxContainer { Name = "buttons" };
        CancelButton = TravelText.Button("cancel", strings.Get("teleport.panel.cancel"), () => { _hub?.Teleport.Cancel(); _dirty = true; });
        CloseButton = TravelText.Button("close", strings.Get("teleport.panel.close"), () => { _hub?.Teleport.Dismiss(); _dirty = true; });
        row.AddChild(CancelButton);
        row.AddChild(CloseButton);
        v.AddChild(row);

        // the region's notices (the answer to set home, a parcel that will not let the agent in) as a strip at the top of the view
        var top = new MarginContainer { Name = "notice_margin", MouseFilter = MouseFilterEnum.Ignore };
        top.SetAnchorsPreset(LayoutPreset.TopWide);
        top.AddThemeConstantOverride("margin_top", 40);
        top.AddThemeConstantOverride("margin_left", 120);
        top.AddThemeConstantOverride("margin_right", 120);
        AddChild(top);
        NoticeBox = new PanelContainer { Name = "notice", MouseFilter = MouseFilterEnum.Stop, Visible = false, SizeFlagsVertical = SizeFlags.ShrinkBegin };
        NoticeBox.AccessibilityName = strings.Get("notice.label");
        top.AddChild(NoticeBox);
        var nrow = new HBoxContainer { Name = "notice_row" };
        NoticeBox.AddChild(nrow);
        NoticeText = TravelText.Note("notice_text", strings.Get("notice.label"), _theme);
        NoticeText.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        NoticeText.ThemeTypeVariation = "";
        NoticeClose = TravelText.Button("notice_close", strings.Get("notice.close"), () => { _hub?.Notices.Dismiss(); _dirty = true; });
        nrow.AddChild(NoticeText);
        nrow.AddChild(NoticeClose);
    }

    /// <summary>Shows a session's teleport (null: no session; the panel is hidden).</summary>
    public void Bind(TravelHub? hub)
    {
        if (_hub is not null)
        {
            _hub.Teleport.Changed -= MarkDirty;
            _hub.Notices.Changed -= MarkDirty;
        }
        _hub = hub;
        if (hub is not null)
        {
            hub.Teleport.Changed += MarkDirty;
            hub.Notices.Changed += MarkDirty;
        }
        _dirty = true;
    }

    private void MarkDirty() => _dirty = true;

    /// <summary>The World menu's Set home to here: the request goes through the model; the region's answer shows as a notice.</summary>
    public void SetHome() => _hub?.Teleport.SetHome();

    /// <summary>The World menu's Go home: a teleport home through the model (a refusal shows in the teleport floater).</summary>
    public void GoHome() => _hub?.Teleport.Home();

    public override void _Process(double delta)
    {
        if (!_dirty)
            return;
        _dirty = false;
        var notice = _hub?.Notices.Latest;
        NoticeBox.Visible = NoticesShownHere && notice is not null;
        if (notice is not null)
            NoticeText.Text = TravelText.Words(_strings, (notice.Key, notice.Arg));
        var t = _hub?.Teleport;
        // the progress for a teleport to another region, or the failure notice; a teleport inside the region shows neither
        bool show = t is not null && (t.ProgressVisible || t.Stage == TeleportStage.Failed);
        Box.Visible = show;
        if (!show)
            return;
        bool failed = t!.Stage == TeleportStage.Failed;
        Title.Text = _strings.Get(failed ? "teleport.panel.failed" : "teleport.panel.title");
        Message.Text = failed
            ? TravelText.Words(_strings, t.Failure ?? ("teleport.error.unknown", null))
            : TravelText.Words(_strings, t.Progress ?? ("teleport.progress.requesting", null));
        CancelButton.Visible = !failed && t.CanCancel;
        CloseButton.Visible = failed;
    }
}

/// <summary>The teleport floater: go to a region and position, go home, the offers waiting (never answered by themselves), and an
/// offer of a teleport to a friend.</summary>
public partial class TeleportPanel : VBoxContainer
{
    private readonly StringTable _strings;
    private static ThemeSpec _theme => UiTheme.Spec;
    private TravelHub? _hub;
    private Func<IReadOnlyList<FriendRow>> _friends = () => [];
    private bool _dirty = true;
    private string _friendsSignature = "";
    private List<Guid> _friendIds = new();

    public LineEdit RegionEdit { get; }
    public SpinBox XSpin { get; }
    public SpinBox YSpin { get; }
    public SpinBox ZSpin { get; }
    public Button GoButton { get; }
    public Button HomeButton { get; }
    public Button SetHomeButton { get; }
    public Label Status { get; }
    public Label RequestsTitle { get; }
    public VBoxContainer RequestsBox { get; }
    public Label RequestsNone { get; }
    public Button AskButton { get; }
    public Label OffersTitle { get; }
    public VBoxContainer OffersBox { get; }
    public Label OffersNone { get; }
    public OptionButton FriendChoice { get; }
    public LineEdit MessageEdit { get; }
    public Button OfferButton { get; }
    public Label OfferNote { get; }

    public TeleportPanel(StringTable strings)
    {
        _strings = strings;
        Name = "teleport_panel";
        AddThemeConstantOverride("separation", 6);
        AddChild(Label("region_label", strings.Get("teleport.region.label")));
        RegionEdit = new LineEdit { Name = "region", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        RegionEdit.AccessibilityName = strings.Get("teleport.region.label");
        RegionEdit.TextSubmitted += _ => Go();
        AddChild(RegionEdit);
        var coords = new HBoxContainer { Name = "coordinates" };
        XSpin = Spin("x", strings.Get("teleport.x.label"), 128);
        YSpin = Spin("y", strings.Get("teleport.y.label"), 128);
        ZSpin = Spin("z", strings.Get("teleport.z.label"), 30);
        foreach (var s in new[] { XSpin, YSpin, ZSpin })
            coords.AddChild(s);
        AddChild(coords);
        var go = new HBoxContainer { Name = "go_buttons" };
        GoButton = TravelText.Button("go", strings.Get("teleport.go"), Go);
        HomeButton = TravelText.Button("home", strings.Get("teleport.home"), () => { _hub?.Teleport.Home(); _dirty = true; });
        SetHomeButton = TravelText.Button("set_home", strings.Get("teleport.set_home"), () => { _hub?.Teleport.SetHome(); _dirty = true; });
        go.AddChild(GoButton);
        go.AddChild(HomeButton);
        go.AddChild(SetHomeButton);
        AddChild(go);
        Status = TravelText.Note("status", strings.Get("teleport.status"), _theme);
        Status.ThemeTypeVariation = UiTheme.ErrorLabel;
        AddChild(Status);

        RequestsTitle = Label("requests_title", strings.Get("teleport.requests.title"));
        AddChild(RequestsTitle);
        RequestsBox = new VBoxContainer { Name = "requests" };
        RequestsBox.AccessibilityName = strings.Get("teleport.requests.list");
        AddChild(RequestsBox);
        RequestsNone = TravelText.Note("requests_none", strings.Get("teleport.requests.none"), _theme);
        RequestsNone.Text = strings.Get("teleport.requests.none");
        AddChild(RequestsNone);

        OffersTitle = Label("offers_title", strings.Get("teleport.offers.title"));
        AddChild(OffersTitle);
        OffersBox = new VBoxContainer { Name = "offers" };
        OffersBox.AccessibilityName = strings.Get("teleport.offers.list");
        AddChild(OffersBox);
        OffersNone = TravelText.Note("offers_none", strings.Get("teleport.offers.none"), _theme);
        OffersNone.Text = strings.Get("teleport.offers.none");
        AddChild(OffersNone);

        AddChild(Label("offer_to_title", strings.Get("teleport.offer_to.title")));
        FriendChoice = new OptionButton { Name = "friend", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        FriendChoice.AccessibilityName = strings.Get("teleport.offer_to.friend");
        AddChild(FriendChoice);
        MessageEdit = new LineEdit { Name = "message", SizeFlagsHorizontal = SizeFlags.ExpandFill, PlaceholderText = strings.Get("teleport.offer_to.message.hint") };
        MessageEdit.AccessibilityName = strings.Get("teleport.offer_to.message");
        MessageEdit.TextSubmitted += _ => Offer();
        AddChild(MessageEdit);
        OfferButton = TravelText.Button("offer", strings.Get("teleport.offer_to.send"), Offer);
        AddChild(OfferButton);
        AskButton = TravelText.Button("ask", strings.Get("teleport.ask.send"), Ask);
        AddChild(AskButton);
        OfferNote = TravelText.Note("offer_note", strings.Get("teleport.offer_to.none"), _theme);
        OfferNote.Text = strings.Get("teleport.offer_to.none");
        AddChild(OfferNote);
    }

    private Label Label(string name, string text)
    {
        var l = new Label { Name = name, Text = text };
        l.AccessibilityName = text;
        return l;
    }

    private static SpinBox Spin(string name, string accessibleName, double value)
    {
        var s = new SpinBox { Name = name, MinValue = 0, MaxValue = 4096, Step = 1, Value = value, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        s.AccessibilityName = accessibleName;
        s.GetLineEdit().AccessibilityName = accessibleName;
        return s;
    }

    /// <summary>Shows a session's teleport; <paramref name="friends"/> lists the friends an offer can go to (null: none).</summary>
    public void Bind(TravelHub? hub, Func<IReadOnlyList<FriendRow>>? friends)
    {
        if (_hub is not null)
            _hub.Teleport.Changed -= MarkDirty;
        _hub = hub;
        _friends = friends ?? (() => []);
        _friendsSignature = "";
        if (hub is not null)
            hub.Teleport.Changed += MarkDirty;
        _dirty = true;
    }

    private void MarkDirty() => _dirty = true;

    private void Go()
    {
        _hub?.Teleport.ToRegion(RegionEdit.Text, new System.Numerics.Vector3((float)XSpin.Value, (float)YSpin.Value, (float)ZSpin.Value));
        _dirty = true;
    }

    private void Offer()
    {
        int at = FriendChoice.Selected;
        if (_hub is null || at < 0 || at >= _friendIds.Count)
            return;
        if (_hub.Teleport.OfferTo(_friendIds[at], MessageEdit.Text))
            MessageEdit.Clear();
        _dirty = true;
    }

    private void Ask()
    {
        int at = FriendChoice.Selected;
        if (_hub is null || at < 0 || at >= _friendIds.Count)
            return;
        if (_hub.Teleport.AskToBeTeleportedBy(_friendIds[at], MessageEdit.Text))
            MessageEdit.Clear();
        _dirty = true;
    }

    public override void _Process(double delta)
    {
        // the friends can change without the teleport model hearing of it: look at them a few times a second
        var friends = _friends();
        string signature = string.Join('|', friends.Select(f => f.Id + f.Name));
        if (signature != _friendsSignature)
        {
            _friendsSignature = signature;
            _dirty = true;
        }
        if (!_dirty)
            return;
        _dirty = false;
        var t = _hub?.Teleport;
        bool idle = t is not null && !t.Busy;
        DisabledTip.Set(GoButton, !idle, _strings.Get("disabled.teleport.busy"));
        DisabledTip.Set(HomeButton, !idle, _strings.Get("disabled.teleport.busy"));
        Status.Text = t?.Status is { } s ? TravelText.Words(_strings, s) : "";

        var requests = t?.Requests ?? [];
        RequestsNone.Visible = requests.Count == 0;
        foreach (var child in RequestsBox.GetChildren())
        {
            RequestsBox.RemoveChild(child);
            child.QueueFree();
        }
        foreach (var q in requests)
        {
            var row = new HBoxContainer { Name = "request_" + q.From.ToString("N") };
            var text = new Label { Text = _strings.Get("teleport.request.text", q.Name, q.Message), AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(160, 0) };
            text.AccessibilityName = text.Text;
            var from = q.From;
            var offer = TravelText.Button("offer", _strings.Get("teleport.request.offer"), () => { _hub?.Teleport.OfferToRequester(from); _dirty = true; });
            var decline = TravelText.Button("decline", _strings.Get("teleport.request.decline"), () => { _hub?.Teleport.DeclineRequest(from); _dirty = true; });
            row.AddChild(text);
            row.AddChild(offer);
            row.AddChild(decline);
            RequestsBox.AddChild(row);
        }

        var offers = t?.Offers ?? [];
        OffersNone.Visible = offers.Count == 0;
        foreach (var child in OffersBox.GetChildren())
        {
            OffersBox.RemoveChild(child);
            child.QueueFree();
        }
        foreach (var o in offers)
        {
            var row = new HBoxContainer { Name = "offer_" + o.From.ToString("N") };
            var text = new Label { Text = _strings.Get("teleport.offer.text", o.Name, o.Message), AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(160, 0) };
            text.AccessibilityName = text.Text;
            var from = o.From;
            var accept = TravelText.Button("accept", _strings.Get("teleport.offer.accept"), () => { _hub?.Teleport.Accept(from); _dirty = true; });
            var decline = TravelText.Button("decline", _strings.Get("teleport.offer.decline"), () => { _hub?.Teleport.Decline(from); _dirty = true; });
            accept.Disabled = !idle;
            row.AddChild(text);
            row.AddChild(accept);
            row.AddChild(decline);
            OffersBox.AddChild(row);
        }

        int keep = FriendChoice.Selected >= 0 && FriendChoice.Selected < _friendIds.Count ? 0 : -1;
        Guid chosen = keep == 0 ? _friendIds[FriendChoice.Selected] : Guid.Empty;
        FriendChoice.Clear();
        _friendIds = friends.Select(f => f.Id).ToList();
        foreach (var f in friends)
            FriendChoice.AddItem(f.Name);
        int again = _friendIds.IndexOf(chosen);
        if (again >= 0)
            FriendChoice.Select(again);
        else if (_friendIds.Count > 0)
            FriendChoice.Select(0);
        bool anyone = _friendIds.Count > 0;
        if (!anyone)
        {
            FriendChoice.AddItem(_strings.Get("teleport.offer_to.friend.placeholder"));   // nothing to choose: the select says so (the choice is disabled)
            FriendChoice.Select(0);
        }
        FriendChoice.Disabled = !anyone;
        MessageEdit.Editable = anyone;
        DisabledTip.Set(OfferButton, !anyone, _strings.Get("disabled.teleport.no_friends"));
        DisabledTip.Set(AskButton, !anyone, _strings.Get("disabled.teleport.no_friends"));
        OfferNote.Visible = !anyone;
    }
}
