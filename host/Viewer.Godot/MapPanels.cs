// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Viewer.Core.Map;
using Viewer.Shell;
using Viewer.Shell.Social;
using Viewer.Shell.Travel;

namespace Viewer.Godot;

/// <summary>
/// The world map drawn: the tiles the model asked for (pictures decoded on the main thread, a few a frame, kept in a bounded set),
/// the regions' borders and names, and the markers; the pointer drags, zooms (wheel) and chooses a spot. All decisions are the model's.
/// </summary>
public partial class WorldMapView : Control
{
    private const int MaxTextures = 192, DecodesPerFrame = 2;

    private readonly WorldMapModel _map;
    private readonly Func<MapTileCache?> _tiles;
    private static ThemeSpec _theme => UiTheme.Spec;
    private readonly Dictionary<MapTileKey, (ImageTexture? Texture, long Used)> _textures = new();
    private long _frame;
    private int _version = -1;
    private bool _dirty = true, _pressed, _dragging;
    private Vector2 _pressAt;

    public WorldMapView(WorldMapModel map, Func<MapTileCache?> tiles, string accessibleName)
    {
        _map = map;
        _tiles = tiles;
        Name = "map_view";
        ClipContents = true;
        FocusMode = FocusModeEnum.Click;
        MouseFilter = MouseFilterEnum.Stop;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        CustomMinimumSize = new Vector2(240, 160);
        AccessibilityName = accessibleName;
        map.Changed += () => _dirty = true;
    }

    public override void _Notification(int what)
    {
        if (what == NotificationResized)
            _dirty = true;
    }

    public override void _Process(double delta)
    {
        if (!IsVisibleInTree())
            return;
        _frame++;
        _map.Update(Size.X, Size.Y);
        int v = _tiles()?.Version ?? 0;
        if (v != _version)
        {
            _version = v;
            _dirty = true;
        }
        if (_dirty)
        {
            _dirty = false;
            QueueRedraw();
        }
    }

    public override void _GuiInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelUp, Pressed: true } up:
                _map.ZoomBy(1.15, up.Position.X, up.Position.Y, Size.X, Size.Y);
                AcceptEvent();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelDown, Pressed: true } down:
                _map.ZoomBy(1 / 1.15, down.Position.X, down.Position.Y, Size.X, Size.Y);
                AcceptEvent();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } b:
                if (b.Pressed)
                {
                    _pressed = true;
                    _dragging = false;
                    _pressAt = b.Position;
                    GrabFocus();
                }
                else
                {
                    // a press that did not move is a click: it chooses a spot
                    if (_pressed && !_dragging)
                        _map.Click(b.Position.X, b.Position.Y, Size.X, Size.Y);
                    _pressed = _dragging = false;
                }
                AcceptEvent();
                break;
            case InputEventMouseMotion m when _pressed:
                if (!_dragging && m.Position.DistanceTo(_pressAt) > 4)
                    _dragging = true;
                if (_dragging)
                    _map.Pan(m.Relative.X, m.Relative.Y);
                AcceptEvent();
                break;
        }
    }

    private ImageTexture? TextureOf(MapTileKey key, ref int decodes)
    {
        if (_textures.TryGetValue(key, out var have))
        {
            _textures[key] = (have.Texture, _frame);
            return have.Texture;
        }
        var cache = _tiles();
        if (cache is null || decodes >= DecodesPerFrame || !cache.TryGet(key, out var bytes))
            return null;
        decodes++;
        var image = new Image();
        ImageTexture? texture = null;
        if (image.LoadJpgFromBuffer(bytes) == Error.Ok || image.LoadPngFromBuffer(bytes) == Error.Ok)
            texture = ImageTexture.CreateFromImage(image);
        _textures[key] = (texture, _frame);   // a picture that will not decode is not tried again
        if (_textures.Count > MaxTextures)
            foreach (var old in _textures.Where(t => t.Value.Used < _frame - 1).Select(t => t.Key).ToList())
                _textures.Remove(old);
        return texture;
    }

    public override void _Draw()
    {
        var area = new Rect2(Vector2.Zero, Size);
        DrawRect(area, ShellTheme.C(_theme.Field));
        int decodes = 0;
        foreach (var t in _map.Tiles)
        {
            var rect = new Rect2(t.X, t.Y, t.Size, t.Size);
            if (!rect.Intersects(area))
                continue;
            var texture = TextureOf(t.Key, ref decodes);
            if (texture is not null)
                DrawTextureRect(texture, rect, false);
            else
                DrawRect(rect.Grow(-1), ShellTheme.C(_theme.Panel));
        }
        var font = ThemeDB.FallbackFont;
        var border = ShellTheme.C(_theme.PanelBorder) with { A = 0.7f };
        foreach (var r in _map.Regions)
        {
            var topLeft = _map.ToScreen(r.GridX * 256.0, (r.GridY + r.SizeY / 256.0) * 256.0, Size.X, Size.Y);
            var size = new Vector2((float)(r.SizeX / 256.0 * _map.PixelsPerRegion), (float)(r.SizeY / 256.0 * _map.PixelsPerRegion));
            var rect = new Rect2(topLeft.X, topLeft.Y, size.X, size.Y);
            if (rect.Intersects(area) && _map.Level <= WorldMapModel.DetailLevel)
                DrawRect(rect, border, false, 1f);
        }
        foreach (var (region, x, y) in _map.Labels(Size.X, Size.Y))
        {
            var at = new Vector2(x, y);
            var width = font.GetStringSize(region.Name, HorizontalAlignment.Left, -1, UiTheme.Tokens.FontBody).X;
            DrawString(font, at + new Vector2(-width / 2 + 1, 1), region.Name, HorizontalAlignment.Left, -1, UiTheme.Tokens.FontBody, ShellTheme.C(UiTheme.Tokens.Outline));
            DrawString(font, at + new Vector2(-width / 2, 0), region.Name, HorizontalAlignment.Left, -1, UiTheme.Tokens.FontBody, ShellTheme.C(_theme.Text));
        }
        foreach (var m in _map.Markers(Size.X, Size.Y))
        {
            var p = new Vector2(m.X, m.Y);
            switch (m.Kind)
            {
                case MapMarkerKind.Own:
                    DrawCircle(p, 6, ShellTheme.C(UiTheme.Tokens.Outline));
                    DrawCircle(p, 4.5f, ShellTheme.C(UiTheme.Tokens.Text));
                    break;
                case MapMarkerKind.Selected:
                    DrawLine(p + new Vector2(-8, 0), p + new Vector2(8, 0), ShellTheme.C(_theme.Error), 2);
                    DrawLine(p + new Vector2(0, -8), p + new Vector2(0, 8), ShellTheme.C(_theme.Error), 2);
                    break;
                default:
                    DrawCircle(p, 6, ShellTheme.C(UiTheme.Tokens.Outline));
                    DrawCircle(p, 4.5f, ShellTheme.C(_theme.Accent));
                    break;
            }
        }
    }
}

/// <summary>The world map floater: search by name, the map, the chosen place with a Teleport button, home, and finding a friend who shares their place.</summary>
public partial class WorldMapPanel : VBoxContainer
{
    private readonly StringTable _strings;
    private static ThemeSpec _theme => UiTheme.Spec;
    private TravelHub? _hub;
    private Func<IReadOnlyList<FriendRow>> _friends = () => [];
    private bool _dirty = true;
    private string _friendsSignature = "";
    private List<Guid> _friendIds = new();
    private List<MapRegion> _resultRows = new();
    private bool _syncing;

    public LineEdit SearchEdit { get; }
    public Button SearchButton { get; }
    public ItemList Results { get; }
    public Label Selected { get; }
    public Button TeleportButton { get; }
    public Button HomeButton { get; }
    public Button OwnButton { get; }
    public Button ZoomInButton { get; }
    public Button ZoomOutButton { get; }
    public OptionButton FriendChoice { get; }
    public Button FriendButton { get; }
    public Label FriendNote { get; }
    public Label Status { get; }
    public WorldMapView? View { get; private set; }

    private MapTileCache? _tiles;

    public WorldMapPanel(StringTable strings)
    {
        _strings = strings;
        Name = "world_map_panel";
        AddThemeConstantOverride("separation", 4);
        var search = new HBoxContainer { Name = "search" };
        SearchEdit = new LineEdit { Name = "search_text", SizeFlagsHorizontal = SizeFlags.ExpandFill, PlaceholderText = strings.Get("map.search.label") };
        SearchEdit.AccessibilityName = strings.Get("map.search.label");
        SearchEdit.TextSubmitted += _ => Search();
        SearchButton = TravelText.Button("search", strings.Get("map.search.button"), Search);
        search.AddChild(SearchEdit);
        search.AddChild(SearchButton);
        AddChild(search);
        Results = new ItemList { Name = "results", CustomMinimumSize = new Vector2(200, 56), SelectMode = ItemList.SelectModeEnum.Single, Visible = false };
        Results.AccessibilityName = strings.Get("map.results");
        Results.ItemSelected += i =>
        {
            if (!_syncing && _hub is not null && i >= 0 && i < _resultRows.Count)
                _hub.WorldMap.Choose(_resultRows[(int)i]);
        };
        AddChild(Results);
        Selected = TravelText.Note("selected", strings.Get("map.selected.none"), _theme);
        Selected.Text = strings.Get("map.selected.none");
        var buttons = new HBoxContainer { Name = "buttons" };
        TeleportButton = TravelText.Button("teleport", strings.Get("map.teleport"), () => { _hub?.WorldMap.TeleportToSelected(); _dirty = true; });
        HomeButton = TravelText.Button("home", strings.Get("map.home"), () => { _hub?.WorldMap.TeleportHome(); _dirty = true; });
        OwnButton = TravelText.Button("own", strings.Get("map.centre_own"), () => _hub?.WorldMap.CentreOnOwn());
        ZoomOutButton = TravelText.Button("zoom_out", strings.Get("map.zoom_out"), () => _hub?.WorldMap.ZoomBy(1 / 1.5, (View?.Size.X ?? 0) / 2, (View?.Size.Y ?? 0) / 2, View?.Size.X ?? 0, View?.Size.Y ?? 0));
        ZoomInButton = TravelText.Button("zoom_in", strings.Get("map.zoom_in"), () => _hub?.WorldMap.ZoomBy(1.5, (View?.Size.X ?? 0) / 2, (View?.Size.Y ?? 0) / 2, View?.Size.X ?? 0, View?.Size.Y ?? 0));
        foreach (var b in new[] { TeleportButton, HomeButton, OwnButton, ZoomOutButton, ZoomInButton })
            buttons.AddChild(b);
        var friend = new HBoxContainer { Name = "friend_row" };
        FriendChoice = new OptionButton { Name = "friend", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        FriendChoice.AccessibilityName = strings.Get("map.friend.label");
        FriendButton = TravelText.Button("find_friend", strings.Get("map.friend.show"), FindFriend);
        friend.AddChild(FriendChoice);
        friend.AddChild(FriendButton);
        FriendNote = TravelText.Note("friend_note", strings.Get("map.friends.none"), _theme);
        FriendNote.Text = strings.Get("map.friends.none");
        Status = TravelText.Note("status", strings.Get("map.status"), _theme);
        Status.ThemeTypeVariation = UiTheme.ErrorLabel;
        _holder = new Control { Name = "view_holder", SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        AddChild(_holder);
        AddChild(Selected);
        AddChild(buttons);
        AddChild(friend);
        AddChild(FriendNote);
        AddChild(Status);
    }

    private readonly Control _holder;

    /// <summary>Shows a session's map. <paramref name="tiles"/> is the session's tile cache (null: none); <paramref name="friends"/> lists the
    /// friends (those who have given the map right can be found).</summary>
    public void Bind(TravelHub? hub, MapTileCache? tiles, Func<IReadOnlyList<FriendRow>>? friends)
    {
        if (_hub is not null)
            _hub.WorldMap.Changed -= MarkDirty;
        _hub = hub;
        _tiles = tiles;
        _friends = friends ?? (() => []);
        _friendsSignature = "";
        if (View is not null)
        {
            _holder.RemoveChild(View);
            View.QueueFree();
            View = null;
        }
        if (hub is not null)
        {
            hub.WorldMap.Changed += MarkDirty;
            View = new WorldMapView(hub.WorldMap, () => _tiles, _strings.Get("map.view"));
            View.SetAnchorsPreset(LayoutPreset.FullRect);
            _holder.AddChild(View);
            _holder.CustomMinimumSize = new Vector2(240, 160);
        }
        _dirty = true;
    }

    private void MarkDirty() => _dirty = true;

    private void Search()
    {
        _hub?.WorldMap.Search(SearchEdit.Text);
        _dirty = true;
    }

    private void FindFriend()
    {
        int at = FriendChoice.Selected;
        if (_hub is not null && at >= 0 && at < _friendIds.Count)
            _hub.WorldMap.ShowFriend(_friendIds[at]);
        _dirty = true;
    }

    public override void _Process(double delta)
    {
        var all = _friends();
        var sharing = all.Where(f => f.Received.HasFlag(Viewer.Core.Session.FriendRightsFlags.SeeOnMap)).ToList();
        string signature = string.Join('|', sharing.Select(f => f.Id + f.Name));
        if (signature != _friendsSignature)
        {
            _friendsSignature = signature;
            _dirty = true;
        }
        if (!_dirty)
            return;
        _dirty = false;
        _syncing = true;
        try
        {
            var map = _hub?.WorldMap;
            _resultRows = map?.Results.ToList() ?? new();
            Results.Clear();
            foreach (var r in _resultRows)
                Results.AddItem(r.Name);
            Results.Visible = _resultRows.Count > 0;
            Selected.Text = map?.Selected is { } s
                ? _strings.Get("map.selected", s.RegionName, Math.Round(s.Local.X), Math.Round(s.Local.Y))
                : _strings.Get("map.selected.none");
            TeleportButton.Disabled = map?.Selected is null;
            Status.Text = map?.Status is { } st ? TravelText.Words(_strings, st) : "";
            var chosen = FriendChoice.Selected >= 0 && FriendChoice.Selected < _friendIds.Count ? _friendIds[FriendChoice.Selected] : Guid.Empty;
            FriendChoice.Clear();
            _friendIds = sharing.Select(f => f.Id).ToList();
            foreach (var f in sharing)
                FriendChoice.AddItem(f.Name);
            int again = _friendIds.IndexOf(chosen);
            if (again >= 0)
                FriendChoice.Select(again);
            else if (_friendIds.Count > 0)
                FriendChoice.Select(0);
            FriendChoice.Disabled = FriendButton.Disabled = _friendIds.Count == 0;
            FriendNote.Visible = _friendIds.Count == 0;
        }
        finally { _syncing = false; }
    }
}

/// <summary>The mini-map drawn from the model's frame: the region around the agent, other avatars as dots (friends in the accent
/// colour), the agent in the middle, the view wedge and the north marker. The wheel zooms.</summary>
public partial class MiniMapView : Control
{
    private readonly MiniMapModel _model;
    private static ThemeSpec _theme => UiTheme.Spec;
    private Func<MiniMapInput?> _source = () => null;
    private MiniMapFrame? _frame;

    public MiniMapView(MiniMapModel model, string accessibleName)
    {
        _model = model;
        Name = "minimap_view";
        ClipContents = true;
        FocusMode = FocusModeEnum.Click;
        MouseFilter = MouseFilterEnum.Stop;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        CustomMinimumSize = new Vector2(160, 160);
        AccessibilityName = accessibleName;
    }

    /// <summary>The model's input for this frame: where the agent is, where the camera looks and who the region lists (null: nothing to draw).</summary>
    public void SetSource(Func<MiniMapInput?> source) => _source = source;

    public MiniMapFrame? Frame => _frame;

    public override void _Process(double delta)
    {
        if (!IsVisibleInTree())
            return;
        var input = _source();
        _frame = input is null ? null : _model.Build(input, Size.X, Size.Y);
        QueueRedraw();
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp })
        {
            _model.ZoomClicks(1);
            AcceptEvent();
        }
        else if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelDown })
        {
            _model.ZoomClicks(-1);
            AcceptEvent();
        }
    }

    public override void _Draw()
    {
        var area = new Rect2(Vector2.Zero, Size);
        DrawRect(area, ShellTheme.C(_theme.Field));
        if (_frame is not { } f)
            return;
        if (f.RegionCorners.Count == 4)
        {
            var polygon = f.RegionCorners.Select(c => new Vector2(c.X, c.Y)).ToArray();
            DrawColoredPolygon(polygon, ShellTheme.C(UiTheme.Tokens.Input));
            DrawPolyline(polygon.Append(polygon[0]).ToArray(), ShellTheme.C(_theme.PanelBorder), 1f);
        }
        var centre = new Vector2(f.CentreX, f.CentreY);
        // the view wedge: the camera's field of view, drawn faintly from the agent outwards
        float reach = MathF.Min(Size.X, Size.Y) / 2f * 0.8f;
        var wedge = new List<Vector2> { centre };
        for (int i = 0; i <= 12; i++)
        {
            float a = f.FrustumCentre - f.FrustumHalf + 2 * f.FrustumHalf * i / 12f;
            wedge.Add(centre + new Vector2(MathF.Cos(a), -MathF.Sin(a)) * reach);
        }
        DrawColoredPolygon(wedge.ToArray(), ShellTheme.C(_theme.Accent) with { A = 0.18f });
        foreach (var d in f.Dots)
        {
            var p = new Vector2(d.X, d.Y);
            var colour = d.Friend ? ShellTheme.C(_theme.Accent) : ShellTheme.C(_theme.Text);
            DrawCircle(p, d.Friend ? 4.5f : 3.5f, ShellTheme.C(UiTheme.Tokens.Outline));
            if (d.UnknownHeight)
                DrawArc(p, 3f, 0, MathF.Tau, 12, colour, 1.5f);   // a ring: the region does not say how high
            else
                DrawCircle(p, d.Friend ? 3.5f : 2.5f, colour);
        }
        DrawCircle(centre, 6, ShellTheme.C(UiTheme.Tokens.Outline));
        DrawCircle(centre, 4.5f, ShellTheme.C(UiTheme.Tokens.Text));
        var font = ThemeDB.FallbackFont;
        // the eight compass labels turn with the map; north is picked out in the error colour, the others are quieter
        foreach (var c in f.Compass)
        {
            bool north = c.Label == "N";
            int size = north ? UiTheme.Tokens.FontBody : UiTheme.Tokens.FontCaption;
            var width = font.GetStringSize(c.Label, HorizontalAlignment.Left, -1, size).X;
            DrawString(font, new Vector2(c.X - width / 2f, c.Y + size * 0.35f), c.Label, HorizontalAlignment.Left, -1, size,
                ShellTheme.C(north ? _theme.Error : _theme.MutedText));
        }
    }
}

/// <summary>The mini-map floater: the view, zoom buttons and the choice to turn with the camera.</summary>
public partial class MiniMapPanel : VBoxContainer
{
    private readonly StringTable _strings;
    private TravelHub? _hub;
    private bool _syncing;

    public Button ZoomInButton { get; }
    public Button ZoomOutButton { get; }
    public CheckBox RotateBox { get; }
    public MiniMapView? View { get; private set; }

    private readonly Control _holder;

    public MiniMapPanel(StringTable strings)
    {
        _strings = strings;
        Name = "minimap_panel";
        var top = new HBoxContainer { Name = "controls" };
        ZoomOutButton = TravelText.Button("zoom_out", strings.Get("minimap.zoom_out"), () => _hub?.MiniMap.ZoomClicks(-10));
        ZoomInButton = TravelText.Button("zoom_in", strings.Get("minimap.zoom_in"), () => _hub?.MiniMap.ZoomClicks(10));
        RotateBox = new CheckBox { Name = "rotate", Text = strings.Get("minimap.rotate"), ButtonPressed = true };
        RotateBox.AccessibilityName = RotateBox.Text;
        RotateBox.Toggled += on =>
        {
            if (!_syncing && _hub is not null)
                _hub.MiniMap.Rotate = on;
        };
        top.AddChild(ZoomOutButton);
        top.AddChild(ZoomInButton);
        top.AddChild(RotateBox);
        AddChild(top);
        _holder = new Control { Name = "view_holder", SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(160, 160) };
        AddChild(_holder);
    }

    /// <summary>Shows a session's mini-map; <paramref name="source"/> gives the model's input each frame (null: nothing to draw).</summary>
    public void Bind(TravelHub? hub, Func<MiniMapInput?>? source)
    {
        _hub = hub;
        if (View is not null)
        {
            _holder.RemoveChild(View);
            View.QueueFree();
            View = null;
        }
        _syncing = true;
        RotateBox.ButtonPressed = hub?.MiniMap.Rotate ?? true;
        _syncing = false;
        if (hub is not null)
        {
            View = new MiniMapView(hub.MiniMap, _strings.Get("minimap.view"));
            View.SetAnchorsPreset(LayoutPreset.FullRect);
            View.SetSource(source ?? (() => null));
            _holder.AddChild(View);
        }
    }
}
