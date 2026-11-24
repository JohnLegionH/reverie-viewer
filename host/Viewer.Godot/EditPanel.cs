// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using Viewer.Core.Building;
using Viewer.Core.Meshing;
using Viewer.Shell;
using Viewer.Shell.Build;

namespace Viewer.Godot;

/// <summary>
/// The Build window, one column top to bottom: a settings search; the tools as one segmented row (Focus, Grab, Edit, Create, Land); the
/// Edit modes as a second row (Move, Rotate, Stretch, Face) with Undo and Redo beside it; a hint line saying what the tool does and what
/// Ctrl and Ctrl+Shift change; a card naming the selection; and collapsible sections (Placement, Shape, Look, Object, Behaviour,
/// Contents) that each show a one-line summary while closed and are remembered open or closed. The controls and their behaviour are the
/// official build floater's (floater_tools.xml, read as behaviour), as before; only the layout changed. What the layout decides (rows,
/// summaries, the hint, the search, the remembered sections) is in <see cref="BuildLayout"/>, tested without the engine. It only shows what
/// <see cref="EditController"/> holds and hands every change to it. Main thread only.
/// </summary>
public partial class EditPanel : VBoxContainer
{
    public const string FloaterId = "build";

    /// <summary>Whether the Land tool exists. It is not built, so the window has no segment for it (UI-STANDARD U9: an unbuilt feature is
    /// absent, not shown greyed). Set when the land tools are built.</summary>
    private static bool LandToolBuilt => EditController.LandToolBuilt;

    /// <summary>The width of the label column: every label of Placement, Shape, Look and Object stands in a column this wide with its control
    /// beside it (UI-STANDARD U1).</summary>
    internal const float LabelColumn = 150;

    private static Label LabelCell(string name, string text) => new() { Name = name, Text = text, CustomMinimumSize = new Vector2(LabelColumn, 0), VerticalAlignment = VerticalAlignment.Center };

    /// <summary>A label in the label column with its control beside it; the line is named "&lt;row&gt;_line" so a search hides it with the row.</summary>
    private HBoxContainer Line(string name, string key, Control control)
    {
        var line = new HBoxContainer { Name = name + "_line" };
        line.AddChild(LabelCell(name + "_label", _s.Get(key)));
        control.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        line.AddChild(control);
        return line;
    }

    private sealed record SectionParts(BuildSection Id, Button Header, Label Summary, VBoxContainer Body, Control Card);

    private readonly StringTable _s;
    private readonly Func<EditController?> _edit;
    private readonly Dictionary<BuildTool, Button> _tools = new();
    private readonly Dictionary<ManipMode, Button> _manips = new();
    private readonly HFlowContainer _manipRow;
    private readonly PanelContainer _manipTrack;
    private readonly RvCard _card;
    private readonly Button _faceMode, _undo, _redo;
    private readonly CheckBox _linked, _snap, _both, _stretchTex, _ownedOnly;
    private readonly Label _status, _hint, _cardName, _cardPicked, _cardOwner, _searchStatus;
    private readonly LineEdit _search;
    private readonly Dictionary<BuildSection, SectionParts> _sections = new();
    private readonly BuildSectionMemory _memory;
    private readonly HashSet<Control> _hiddenBySearch = new();
    private bool _ctrlHeld, _shiftHeld;
    public Button Link { get; }
    public Button Unlink { get; }

    // Object (the official General tab)
    private readonly LineEdit _name, _desc;
    private readonly Label _creator, _owner;
    private readonly CheckBox _shareGroup, _anyMove, _anyCopy, _nextModify, _nextCopy, _nextTransfer;
    // Placement and Shape (the official Object tab)
    private readonly SpinBox[] _pos = new SpinBox[3], _size = new SpinBox[3], _rot = new SpinBox[3], _pivotOffset = new SpinBox[3];
    private readonly OptionButton _pivotMode, _pivotUnit;
    private readonly CheckBox _locked, _physical, _temporary, _phantom;
    private readonly OptionButton _type, _hole;
    private readonly Dictionary<string, (Control Row, SpinBox A, SpinBox? B)> _shape = new();

    private bool _filling;
    public string? LastRefusal { get; private set; }

    private readonly EditTextureTab _textureTab;
    private readonly Container _createRow;
    private readonly CheckBox _keepTool, _copySelection, _copyCenters, _copyRotates;
    private readonly Dictionary<Viewer.Core.Building.CreateShape, Button> _shapeButtons = new();
    // For sale, when left-clicked and the land impact (the official General tab and status line)
    private readonly CheckBox _forSale;
    private readonly SpinBox _price;
    private readonly Label _priceLabel;
    private readonly OptionButton _saleType, _clickAction;
    private readonly Label _impact;
    /// <summary>Raised when Keep Tool selected is ticked or cleared (the viewer remembers it).</summary>
    public event Action<bool>? KeepToolChanged;
    private readonly Dictionary<ObjectClip, Button> _pasteButtons = new();

    public EditTextureTab TextureTab => _textureTab;
    private readonly EditFeaturesTab _featuresTab;
    private readonly EditContentTab _contentTab;
    private readonly Func<Guid, string?>? _names;
    public EditFeaturesTab FeaturesTab => _featuresTab;
    public EditContentTab ContentTab => _contentTab;

    public EditPanel(StringTable strings, Func<EditController?> edit, Func<Viewer.Core.Inventory.InventoryModel?>? inventory = null, SettingsService? settings = null,
        Func<Guid>? agent = null, Func<Guid, string?>? names = null)
    {
        _s = strings;
        _edit = edit;
        _names = names;
        _memory = new BuildSectionMemory(settings ?? new SettingsService(null));
        Name = "edit_panel";
        SizeFlagsVertical = SizeFlags.ExpandFill;
        AddThemeConstantOverride("separation", 6);

        // ---------------- the settings search ----------------
        _search = new LineEdit
        {
            Name = "build_search", PlaceholderText = strings.Get("edit.search"), TooltipText = strings.Get("edit.search.tip"), ClearButtonEnabled = true,
            RightIcon = ReverieIcons.Get(LucideIcons.Search),
        };
        _search.AccessibilityName = strings.Get("edit.search");
        _search.TextChanged += ApplySearch;
        AddChild(_search);
        _searchStatus = new Label { Name = "search_status", Visible = false, AutowrapMode = TextServer.AutowrapMode.WordSmart, ThemeTypeVariation = ReverieStyle.Caption };
        AddChild(_searchStatus);

        // ---------------- the tools: one segmented row ----------------
        var toolTrack = new PanelContainer { Name = "tools_track", ThemeTypeVariation = ReverieStyle.Track };
        var tools = new HBoxContainer { Name = "tools" };
        tools.AddThemeConstantOverride("separation", 2);
        foreach (var (tool, key) in new[] { (BuildTool.Focus, "edit.tool.focus"), (BuildTool.Move, "edit.tool.move"), (BuildTool.Edit, "edit.tool.edit"),
                     (BuildTool.Create, "edit.tool.create"), (BuildTool.Land, "edit.tool.land") }.Where(t => t.Item1 != BuildTool.Land || LandToolBuilt))
        {
            // each tool's tooltip names it with its shortcut (the Build menu's Ctrl+1 to Ctrl+5)
            var b = new Button
            {
                Name = "tool_" + tool.ToString().ToLowerInvariant(), Text = strings.Get(key), ToggleMode = true, TooltipText = strings.Get(key + ".tip"),
                SizeFlagsHorizontal = SizeFlags.ExpandFill, ThemeTypeVariation = ReverieStyle.Segment,
            };
            b.AccessibilityName = b.Text;
            b.Pressed += () => ToolChosen?.Invoke(tool);
            tools.AddChild(b);
            _tools[tool] = b;
        }
        toolTrack.AddChild(tools);
        AddChild(toolTrack);

        // ---------------- the Edit modes, with Undo and Redo beside them ----------------
        // Move, Rotate, Stretch or Face: which handles the Edit and Grab tools show (floater_tools.xml's radio group; Ctrl and Ctrl+Shift
        // switch while held); Face is the official Select Face
        _manipRow = new HFlowContainer { Name = "manip" };   // Undo and Redo wrap under the modes when the window is at its narrowest
        _manipTrack = new PanelContainer { Name = "manip_track", ThemeTypeVariation = ReverieStyle.Track, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _manipTrack.AccessibilityName = strings.Get("edit.modes");
        var manips = new HBoxContainer();
        manips.AddThemeConstantOverride("separation", 2);
        foreach (var (mode, key) in new[] { (ManipMode.Move, "edit.manip.move"), (ManipMode.Rotate, "edit.manip.rotate"), (ManipMode.Scale, "edit.manip.scale") })
        {
            var b = Segment("manip_" + mode.ToString().ToLowerInvariant(), key);
            b.Pressed += () => { if (_edit() is { } e) { e.SelectFaces = false; e.Manip = mode; } Refresh(); };
            manips.AddChild(b);
            _manips[mode] = b;
        }
        _faceMode = Segment("manip_face", "edit.manip.face");
        _faceMode.Pressed += () => { if (_edit() is { } e) e.SelectFaces = true; Refresh(); };
        manips.AddChild(_faceMode);
        _manipTrack.AddChild(manips);
        _manipRow.AddChild(_manipTrack);
        _undo = new Button { Name = "undo", Text = strings.Get("edit.undo"), TooltipText = strings.Get("edit.undo.tip"), Icon = ReverieIcons.Get(LucideIcons.Return) };
        _undo.AccessibilityName = strings.Get("edit.undo.tip");
        _undo.Pressed += () => { _edit()?.Undo(); Refresh(); };
        _redo = new Button { Name = "redo", Text = strings.Get("edit.redo"), TooltipText = strings.Get("edit.redo.tip") };
        _redo.AccessibilityName = strings.Get("edit.redo.tip");
        _redo.Pressed += () => { _edit()?.Redo(); Refresh(); };
        AddChild(_manipRow);
        // Undo and Redo stay beside the modes, and stay shown with every tool (the modes row hides for Focus and Create)
        _manipRow.AddChild(_undo);
        _manipRow.AddChild(_redo);

        // the Create tool's option: after a placement the tool stays Create instead of handing back to Edit (floater_tools.xml "checkbox sticky",
        // CreateToolKeepSelected); shown only while Create is the tool, as the official window shows it
        _createRow = new VBoxContainer { Name = "create_options" };
        // the fifteen shape buttons in the official order (floater_tools.xml ToolCube to ToolGrass); pressing one also turns Copy selection off
        // three equal columns, so the rows line up (the names are up to twelve letters; the window's smallest width holds three)
        var shapeRow = new GridContainer { Name = "create_shapes", Columns = 3 };
        foreach (var kind in Viewer.Core.Building.CreateShapes.All)
        {
            string key = "edit.shape." + kind.ToString().ToLowerInvariant();
            var b = new Button { Name = "shape_" + kind.ToString().ToLowerInvariant(), Text = strings.Get(key), TooltipText = strings.Get(key), ToggleMode = true, ThemeTypeVariation = ReverieStyle.Segment, CustomMinimumSize = new Vector2(112, 0),
                SizeFlagsHorizontal = SizeFlags.ExpandFill };
            b.AccessibilityName = strings.Get(key);
            var chosen = kind;
            b.Pressed += () => { _edit()?.ChooseShape(chosen); Refresh(); };
            shapeRow.AddChild(b);
            _shapeButtons[kind] = b;
        }
        _createRow.AddChild(shapeRow);
        var createChecks = new HFlowContainer { Name = "create_checks" };
        _keepTool = Tick(createChecks, "keep_tool", "edit.keep_tool", on => { if (_edit() is { } e) e.KeepToolSelected = on; KeepToolChanged?.Invoke(on); });
        _copySelection = Tick(createChecks, "copy_selection", "edit.copy_selection", on => { if (_edit() is { } e) e.CopySelection = on; Refresh(); });
        _copyCenters = Tick(createChecks, "copy_centers", "edit.copy_centers", on => { if (_edit() is { } e) e.CopyCenters = on; });
        _copyRotates = Tick(createChecks, "copy_rotates", "edit.copy_rotates", on => { if (_edit() is { } e) e.CopyRotates = on; });
        _createRow.AddChild(createChecks);
        AddChild(_createRow);

        // ---------------- the hint line: what the tool does, and its keys ----------------
        _hint = new Label { Name = "hint", AutowrapMode = TextServer.AutowrapMode.WordSmart, ThemeTypeVariation = ReverieStyle.Caption };
        AddChild(_hint);

        // ---------------- the selection card ----------------
        // the card is the first thing in the scrolled column, so the sections get the height below the tools (it is not shown for Create)
        var card = _card = new RvCard(null, null) { Name = "selection_card" };
        _cardName = new Label { Name = "card_name", ThemeTypeVariation = ReverieStyle.CardHeading, ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
        _cardPicked = new Label { Name = "card_picked", ThemeTypeVariation = ReverieStyle.Caption };
        _cardOwner = new Label { Name = "card_owner", ThemeTypeVariation = ReverieStyle.Caption };
        card.Body.AddChild(_cardName);
        // what is picked and who owns it share a line (they wrap only when the window is at its narrowest)
        var cardFacts = new HFlowContainer { Name = "card_facts" };
        cardFacts.AddThemeConstantOverride("h_separation", 16);
        cardFacts.AddChild(_cardPicked);
        cardFacts.AddChild(_cardOwner);
        card.Body.AddChild(cardFacts);
        // "N objects selected, land impact X" (the official status line), from the region's GetObjectCost answer
        _impact = new Label { Name = "land_impact", ThemeTypeVariation = ReverieStyle.Caption };
        _impact.AccessibilityName = strings.Get("edit.card.land_impact");
        card.Body.AddChild(_impact);
        _status = new Label { Name = "status", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _status.AccessibilityName = strings.Get("edit.title");
        card.Body.AddChild(_status);

        // ---------------- the sections, in one scrolled column ----------------
        var scroll = new ScrollContainer { Name = "sections_scroll", SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        // the cards stop a little before the scroll bar, so they do not touch it
        var gutter = new MarginContainer { Name = "sections_gutter", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        gutter.AddThemeConstantOverride("margin_right", 10);
        var column = new VBoxContainer { Name = "sections", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        column.AddThemeConstantOverride("separation", 4);
        gutter.AddChild(column);
        scroll.AddChild(gutter);
        AddChild(scroll);
        column.AddChild(card);
        foreach (var section in BuildLayout.Sections)
            _sections[section] = MakeSection(column, section);

        // ---------------- Placement ----------------
        var place = Body(BuildSection.Placement);
        Triple(place, "position", "edit.position", _pos, -256, 8192, 0.001, v => _edit()?.SetPosition(v));
        Triple(place, "size", "edit.size", _size, 0.001, 256, 0.001, v => _edit()?.SetSize(v));
        Triple(place, "rotation", "edit.rotation", _rot, -9999, 9999, 0.05, v => _edit()?.SetRotation(v));
        // copy and paste (llpanelobject.cpp: position, size, rotation, all three, and the object's parameters)
        var clip = new HFlowContainer { Name = "object_clipboard" };
        foreach (var (what, name) in new[] { (ObjectClip.Position, "position"), (ObjectClip.Size, "size"), (ObjectClip.Rotation, "rotation"), (ObjectClip.All, "all"), (ObjectClip.Parameters, "params") })
        {
            var c = new Button { Name = "copy_" + name, Text = strings.Get("edit.copy_" + name) };
            c.AccessibilityName = c.Text;
            c.Pressed += () => { if (_edit()?.Copy(what) is { } text) DisplayServer.ClipboardSet(text); Refresh(); };
            clip.AddChild(c);
            var p = new Button { Name = "paste_" + name, Text = strings.Get("edit.paste_" + name) };
            p.AccessibilityName = p.Text;
            p.Pressed += () => _edit()?.Paste(what);
            clip.AddChild(p);
            _pasteButtons[what] = p;
        }
        place.AddChild(clip);
        var placeTicks = new HFlowContainer { Name = "placement_options" };
        _snap = Tick(placeTicks, "snap", "edit.snap", on => { if (_edit() is { } e) e.SnapEnabled = on; });
        _both = Tick(placeTicks, "stretch_both", "edit.stretch_both", on => { if (_edit() is { } e) e.StretchBothSides = on; });
        _stretchTex = Tick(placeTicks, "stretch_textures", "edit.stretch_textures", on => { if (_edit() is { } e) e.StretchTextures = on; });
        place.AddChild(placeTicks);
        // the pivot the handles turn and stretch about (Firestorm's pivot settings: Actual Root, and an offset in percent or metres from
        // the box's minimum corner); the numbers are open to typing only while Custom is chosen
        _pivotMode = new OptionButton { Name = "pivot_mode", AccessibilityName = _s.Get("edit.pivot") };
        foreach (var (mode, key) in new[] { (PivotMode.Centre, "edit.pivot.centre"), (PivotMode.Root, "edit.pivot.root"), (PivotMode.Custom, "edit.pivot.custom") })
            _pivotMode.AddItem(_s.Get(key), (int)mode);
        _pivotMode.ItemSelected += i =>
        {
            if (_filling || _edit() is not { } e)
                return;
            var mode = (PivotMode)_pivotMode.GetItemId((int)i);
            if (mode == PivotMode.Custom)
                e.SetCustomPivot(PivotNumbers(), PivotUnitNow());
            else
                e.SetPivot(mode);
        };
        place.AddChild(Line("pivot", "edit.pivot", _pivotMode));
        Triple(place, "pivot_offset", "edit.pivot.custom", _pivotOffset, -1000, 1000, 0.1, v => _edit()?.SetCustomPivot(v, PivotUnitNow()));
        _pivotUnit = new OptionButton { Name = "pivot_unit", AccessibilityName = _s.Get("edit.pivot.unit") };
        _pivotUnit.AddItem(_s.Get("edit.pivot.percent"), (int)PivotUnit.Percent);
        _pivotUnit.AddItem(_s.Get("edit.pivot.metres"), (int)PivotUnit.Metres);
        _pivotUnit.ItemSelected += _ => { if (!_filling) _edit()?.SetCustomPivot(PivotNumbers(), PivotUnitNow()); };
        place.AddChild(Line("pivot_unit", "edit.pivot.unit", _pivotUnit));

        // ---------------- Shape ----------------
        var shape = Body(BuildSection.Shape);
        _type = new OptionButton { Name = "prim_type" };
        _type.AccessibilityName = strings.Get("edit.prim_type");
        foreach (var k in Enum.GetValues<PrimKind>())
            _type.AddItem(strings.Get("edit.type." + k.ToString().ToLowerInvariant()), (int)k);
        _type.ItemSelected += i => { if (!_filling && (PrimKind)_type.GetItemId((int)i) is var k && k != PrimKind.Sculpt) _edit()?.SetShape(ShapeRules.OfKind(k)); };
        shape.AddChild(Line("prim_type", "edit.prim_type", _type));
        ShapeRow(shape, "path_cut", "edit.path_cut", 0, 1, 0.025, true);
        ShapeRow(shape, "hollow", "edit.hollow", 0, 95, 5, false);
        _hole = new OptionButton { Name = "hollow_shape" };
        _hole.AccessibilityName = strings.Get("edit.hollow_shape");
        foreach (var (key, id) in new[] { ("same", 0x00), ("circle", 0x10), ("square", 0x20), ("triangle", 0x30) })
            _hole.AddItem(strings.Get("edit.hole." + key), id);
        _hole.ItemSelected += _ => SendShape();
        shape.AddChild(Line("hollow_shape", "edit.hollow_shape", _hole));
        ShapeRow(shape, "twist", "edit.twist", -360, 360, 1, true);
        ShapeRow(shape, "taper", "edit.taper", -1, 1, 0.01, true);
        ShapeRow(shape, "top_shear", "edit.top_shear", -0.5, 0.5, 0.01, true);
        ShapeRow(shape, "slice", "edit.slice", 0, 1, 0.02, true);
        ShapeRow(shape, "dimple", "edit.dimple", 0, 1, 0.02, true);
        ShapeRow(shape, "profile_cut", "edit.profile_cut", 0, 1, 0.02, true);
        ShapeRow(shape, "hole_size", "edit.hole_size", 0.05, 1, 0.01, true);
        ShapeRow(shape, "radius", "edit.radius", -1, 1, 0.01, false);
        ShapeRow(shape, "revolutions", "edit.revolutions", 1, 4, 0.1, false);
        ShapeRow(shape, "skew", "edit.skew", -0.95, 0.95, 0.05, false);

        // ---------------- Look (the official Texture tab) ----------------
        _textureTab = new EditTextureTab(strings, edit, inventory ?? (() => null), Body(BuildSection.Look), this);

        // ---------------- Object (the official General tab, the object's flags and its links) ----------------
        var general = Body(BuildSection.Object);
        _name = Field(general, "name", "edit.name", t => _edit()?.SetName(t));
        _name.MaxLength = Viewer.Core.Actions.EditText.MaxName;
        _desc = Field(general, "description", "edit.description", t => _edit()?.SetDescription(t));
        _desc.MaxLength = Viewer.Core.Actions.EditText.MaxDescription;
        _creator = Info(general, "creator", "edit.creator");
        _owner = Info(general, "owner", "edit.owner");
        const uint copy = 1u << 15, modify = 1u << 14, transfer = 1u << 13, move = 1u << 19;
        _shareGroup = Tick(general, "share_group", "edit.share_group", on => _edit()?.SetPermission(0x04, modify | move | copy, on));
        _anyMove = Tick(general, "anyone_move", "edit.anyone_move", on => _edit()?.SetPermission(0x08, move, on));
        _anyCopy = Tick(general, "anyone_copy", "edit.anyone_copy", on => _edit()?.SetPermission(0x08, copy, on));
        // the label stands in the label column like its neighbours; the three ticks wrap as whole controls in a narrow window
        var nextRow = new HFlowContainer { Name = "next_row" };
        nextRow.AddChild(LabelCell("next_owner", strings.Get("edit.next_owner")));
        _nextModify = Tick(nextRow, "next_modify", "edit.perm.modify", on => _edit()?.SetPermission(0x10, modify, on));
        _nextCopy = Tick(nextRow, "next_copy", "edit.perm.copy", on => _edit()?.SetPermission(0x10, copy, on));
        _nextTransfer = Tick(nextRow, "next_transfer", "edit.perm.transfer", on => _edit()?.SetPermission(0x10, transfer, on));
        general.AddChild(nextRow);
        var saleRow = new HBoxContainer { Name = "sale_row" };
        saleRow.AddThemeConstantOverride("h_separation", 8);   // the price unit and the price box do not touch
        // "For sale": its label in the label column, the tick box (no text of its own) beside it, then the price; the sale type has its own
        // labelled row below, so no line of controls stands without a label
        var saleLabel = LabelCell("for_sale_label", strings.Get("edit.for_sale"));
        saleRow.AddChild(saleLabel);
        _forSale = Tick(saleRow, "for_sale", "edit.for_sale", _ => SendSale(true), showText: false);
        saleLabel.MouseFilter = MouseFilterEnum.Stop;
        saleLabel.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } && !_forSale.Disabled) _forSale.ButtonPressed = !_forSale.ButtonPressed; };
        _priceLabel = new Label { Name = "sale_price_unit", Text = strings.Get("edit.sale_price") };
        saleRow.AddChild(_priceLabel);
        _price = new SpinBox { Name = "sale_price", MinValue = 0, MaxValue = 999999999, Step = 1, Rounded = true, AllowGreater = false, CustomMinimumSize = new Vector2(120, 0) };
        _price.AccessibilityName = strings.Get("edit.sale_price");
        _price.ValueChanged += _ => { if (!_filling && _forSale.ButtonPressed) SendSale(false); };
        saleRow.AddChild(_price);
        _saleType = new OptionButton { Name = "sale_type" };
        _saleType.AccessibilityName = strings.Get("edit.sale_type");
        foreach (var (type, key) in new[] { (Viewer.Core.Building.SaleKind.Copy, "copy"), (Viewer.Core.Building.SaleKind.Contents, "contents"), (Viewer.Core.Building.SaleKind.Original, "original") })
            _saleType.AddItem(strings.Get("edit.sale_type." + key), (int)type);
        _saleType.ItemSelected += _ => { if (!_filling) SendSale(false); };
        general.AddChild(saleRow);
        general.AddChild(Line("sale_type", "edit.sale_type", _saleType));
        var clickRow = new HBoxContainer { Name = "click_row" };
        clickRow.AddChild(LabelCell("click_action_label", strings.Get("edit.click_action")));
        _clickAction = new OptionButton { Name = "click_action", TooltipText = strings.Get("edit.click_action.tip") };
        _clickAction.AccessibilityName = strings.Get("edit.click_action");
        foreach (var choice in Viewer.Core.Interaction.ClickChoices.All)
            _clickAction.AddItem(strings.Get("edit.click." + choice.ToString().ToLowerInvariant()), (int)choice);
        _clickAction.ItemSelected += i => ClickChosen((Viewer.Core.Interaction.ClickChoice)_clickAction.GetItemId((int)i));
        clickRow.AddChild(_clickAction);
        general.AddChild(clickRow);
        var flags = new HFlowContainer { Name = "flags" };
        _locked = Tick(flags, "locked", "edit.locked", on => _edit()?.SetLocked(on));
        _physical = Tick(flags, "physical", "edit.physical", _ => SendFlags());
        _temporary = Tick(flags, "temporary", "edit.temporary", _ => SendFlags());
        _phantom = Tick(flags, "phantom", "edit.phantom", _ => SendFlags());
        general.AddChild(flags);
        var links = new HFlowContainer { Name = "link_options" };
        _linked = Tick(links, "linked", "edit.linked", on => { if (_edit() is { } e) e.EditLinked = on; });
        // Select Only My Objects, the official Build > Options item (SelectOwnedOnly), as a tick here too
        _ownedOnly = Tick(links, "owned_only", "edit.owned_only", on => { if (_edit() is { } e) e.SelectOwnedOnly = on; });
        general.AddChild(links);
        var linkRow = new HBoxContainer { Name = "link_row" };
        Link = new Button { Name = "link", Text = strings.Get("edit.link") };
        Link.AccessibilityName = Link.Text;
        Link.Pressed += () => DoLink();
        Unlink = new Button { Name = "unlink", Text = strings.Get("edit.unlink") };
        Unlink.AccessibilityName = Unlink.Text;
        Unlink.Pressed += () => _edit()?.Unlink();
        linkRow.AddChild(Link);
        linkRow.AddChild(Unlink);
        general.AddChild(linkRow);

        // ---------------- Behaviour (the official Features tab) and Contents (the Content tab) ----------------
        _featuresTab = new EditFeaturesTab(strings, edit, inventory ?? (() => null), Body(BuildSection.Behaviour), this);
        _contentTab = new EditContentTab(strings, edit, inventory ?? (() => null), Body(BuildSection.Contents), agent, names);

        foreach (var section in BuildLayout.Sections)
            ShowSection(section, _memory.IsOpen(section));
    }

    /// <summary>A tool button was pressed.</summary>
    public event Action<BuildTool>? ToolChosen;

    private Button Segment(string name, string key)
    {
        var b = new Button { Name = name, Text = _s.Get(key), TooltipText = _s.Get(key + ".tip"), ToggleMode = true, SizeFlagsHorizontal = SizeFlags.ExpandFill, ThemeTypeVariation = ReverieStyle.Segment };
        b.AccessibilityName = b.Text;
        return b;
    }

    private SectionParts MakeSection(VBoxContainer column, BuildSection id)
    {
        string name = BuildLayout.NameOf(id);
        string title = _s.Get(BuildLayout.TitleKey(id));
        var card = new PanelContainer { Name = "section_card_" + name, ThemeTypeVariation = ReverieStyle.Card };
        var box = new VBoxContainer { Name = "section_box_" + name };
        // the name and, while the section is closed, its one-line summary share one line (a header of fixed width keeps the summaries in a column)
        var line = new HBoxContainer { Name = "section_line_" + name };
        var header = new Button
        {
            Name = "section_" + name, Text = title, ToggleMode = true, Flat = true, Alignment = HorizontalAlignment.Left,
            TooltipText = _s.Get("edit.section.toggle.tip", title), CustomMinimumSize = new Vector2(112, 0),
        };
        header.AccessibilityName = title;
        header.Toggled += open =>
        {
            ShowSection(id, open);
            _memory.Set(id, open);
        };
        line.AddChild(header);
        var summary = new Label { Name = "summary_" + name, ThemeTypeVariation = ReverieStyle.Caption, ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            SizeFlagsHorizontal = SizeFlags.ExpandFill, VerticalAlignment = VerticalAlignment.Center };
        line.AddChild(summary);
        box.AddChild(line);
        var body = new VBoxContainer { Name = name + "_page", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        box.AddChild(body);
        card.AddChild(box);
        column.AddChild(card);
        return new SectionParts(id, header, summary, body, card);
    }

    private VBoxContainer Body(BuildSection s) => _sections[s].Body;

    private void ShowSection(BuildSection id, bool open)
    {
        var p = _sections[id];
        p.Header.SetPressedNoSignal(open);
        p.Header.Icon = ReverieIcons.Get(open ? LucideIcons.Down : LucideIcons.Submenu);
        p.Body.Visible = open;
        p.Summary.Visible = !open;
    }

    private Guid _contentsSeenFor;   // the object whose contents last decided whether the Contents section opens

    /// <summary>Whether a section is open.</summary>
    public bool IsOpen(BuildSection section) => _sections[section].Body.Visible;

    /// <summary>Opens or closes a section as its header does (the choice is kept).</summary>
    public void SetOpen(BuildSection section, bool open)
    {
        ShowSection(section, open);
        _memory.Set(section, open);
    }

    /// <summary>The settings search's text (for the automation and the render cases).</summary>
    public string SearchText
    {
        get => _search.Text;
        set
        {
            _search.Text = value;
            ApplySearch(value);
        }
    }

    /// <summary>Filters the sections to the rows the search finds, opens their sections (for this search only: the kept choice does not
    /// change), and lights the tools it found. An empty search shows everything again as it was.</summary>
    private void ApplySearch(string query)
    {
        foreach (var c in _hiddenBySearch)
            if (IsInstanceValid(c)) c.Visible = true;
        _hiddenBySearch.Clear();
        foreach (var b in _tools.Values.Concat(_manips.Values).Append(_faceMode).Append(_undo).Append(_redo))
            b.Modulate = b.Modulate with { A = 1f };
        var hits = BuildLayout.Search(query, _s);
        if (query.Trim().Length == 0)
        {
            _searchStatus.Visible = false;
            foreach (var section in BuildLayout.Sections)
                ShowSection(section, _memory.IsOpen(section));
            Refresh();
            return;
        }
        var found = hits.Select(h => h.Id).ToHashSet();
        var open = BuildLayout.SectionsOf(hits);
        foreach (var section in BuildLayout.Sections)
        {
            bool show = open.Contains(section);
            var p = _sections[section];
            HideIf(p.Card, !show);
            if (!show) continue;
            // shown open for this search only; the kept choice is not changed
            p.Header.SetPressedNoSignal(true);
            p.Header.Icon = ReverieIcons.Get(LucideIcons.Down);
            p.Body.Visible = true;
            p.Summary.Visible = false;
            foreach (var row in BuildLayout.Rows.Where(r => r.Section == section))
            {
                bool match = found.Contains(row.Id);
                foreach (var n in NodesOf(row.Id, p.Body))
                    HideIf(n, !match);
            }
        }
        // the tools a search found are lit, the others dimmed while it shows
        var toolHits = hits.Where(h => h.Section is null).ToList();
        foreach (var (id, b) in _tools.Select(kv => ("tool_" + kv.Key.ToString().ToLowerInvariant(), kv.Value))
                     .Concat(_manips.Select(kv => ("manip_" + kv.Key.ToString().ToLowerInvariant(), kv.Value)))
                     .Append(("manip_face", _faceMode)).Append(("undo", _undo)).Append(("redo", _redo)))
            b.Modulate = b.Modulate with { A = toolHits.Count > 0 && !found.Contains(id) ? 0.45f : 1f };
        _searchStatus.Text = BuildLayout.SearchLine(query, hits, _s);   // what the search found, or that it found nothing
        _searchStatus.Visible = _searchStatus.Text.Length > 0;
    }

    private void HideIf(Control c, bool hide)
    {
        if (!hide || !c.Visible) return;
        c.Visible = false;
        _hiddenBySearch.Add(c);
    }

    /// <summary>The controls that make up a row: the control itself (or its row holder) and its label.</summary>
    private static IEnumerable<Control> NodesOf(string id, Node body)
    {
        foreach (var name in new[] { id, id + "_label" })
            if (body.FindChild(name, true, false) is Control c)
            {
                yield return c;
                // a label and its control share a line (a holder named "<row>_line"): the line goes with them, so no empty gap stays
                if (c.GetParent() is Control { } holder && holder.Name.ToString().EndsWith("_line", StringComparison.Ordinal))
                    yield return holder;
            }
        if (id.EndsWith("_row", StringComparison.Ordinal) && body.FindChild(id[..^4] + "_label", true, false) is Control l && l.GetParent()?.Name != id)
            yield return l;
    }

    /// <summary>The section rows a search shows now, by control name (for the automation and the render cases).</summary>
    public IReadOnlyList<string> SearchShown() => BuildLayout.Search(_search.Text, _s).Select(r => r.Id).ToArray();

    /// <summary>A notice in the window's status line (a refusal from a menu entry, or a warning about a choice).</summary>
    public void Say(string key)
    {
        LastRefusal = key;
        _status.Text = _s.Get(key);
        _status.Visible = true;
    }

    private void SendSale(bool fromBox)
    {
        if (_edit() is not { } e || _filling)
            return;
        var type = (Viewer.Core.Building.SaleKind)_saleType.GetItemId(Math.Max(0, _saleType.Selected));
        e.SetSale(_forSale.ButtonPressed, type, (int)_price.Value);
        Refresh();
    }

    private void ClickChosen(Viewer.Core.Interaction.ClickChoice choice)
    {
        if (_edit() is not { } e || _filling)
            return;
        var (_, notice) = e.SetClickChoice(choice);
        if (notice is not null)
            Say(notice);
        Refresh();
    }

    private void DoLink()
    {
        LastRefusal = _edit()?.Link();
        if (LastRefusal is { } why)
            _status.Text = _s.Get("edit." + why);
    }

    /// <summary>A tick box named by the string table; <paramref name="showText"/> false leaves the box without text of its own when a label in the
    /// label column names it (the accessible name stays).</summary>
    private CheckBox Tick(Container parent, string name, string key, Action<bool> changed, bool showText = true)
    {
        var c = new CheckBox { Name = name, Text = showText ? _s.Get(key) : string.Empty };
        c.AccessibilityName = _s.Get(key);
        c.Toggled += on => { if (!_filling) changed(on); };
        parent.AddChild(c);
        return c;
    }

    private LineEdit Field(Container parent, string name, string key, Action<string> commit)
    {
        var e = new LineEdit { Name = name };
        e.AccessibilityName = _s.Get(key);
        e.TextSubmitted += t => commit(t);
        e.FocusExited += () => { if (!_filling) commit(e.Text); };
        parent.AddChild(Line(name, key, e));
        return e;
    }

    private Label Info(Container parent, string name, string key)
    {
        var row = new HBoxContainer { Name = name + "_row" };
        row.AddChild(LabelCell(name + "_label", _s.Get(key)));
        var v = new Label { Name = name, VerticalAlignment = VerticalAlignment.Center, ClipText = true, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        v.AccessibilityName = _s.Get(key);
        row.AddChild(v);
        parent.AddChild(row);
        return v;
    }

    private static SpinBox Spin(string name, string accessible, double min, double max, double step) => new()
    {
        Name = name, MinValue = min, MaxValue = max, Step = step, AllowGreater = false, AllowLesser = false, SizeFlagsHorizontal = SizeFlags.ExpandFill,
        AccessibilityName = accessible,
    };

    private void Triple(Container parent, string name, string key, SpinBox[] boxes, double min, double max, double step, Action<System.Numerics.Vector3> commit)
    {
        // a row of three values keeps its label on its own line with the three boxes on one line below it, equal in width and never wrapping
        // (the official viewer's grouping; UI-STANDARD U1, exception for X Y Z rows)
        var line = new VBoxContainer { Name = name + "_line" };
        line.AddChild(LabelCell(name + "_label", _s.Get(key)));
        var row = new HBoxContainer { Name = name, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        string[] axes = ["edit.x", "edit.y", "edit.z"];
        for (int i = 0; i < 3; i++)
        {
            var axis = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            axis.AddChild(new Label { Text = _s.Get(axes[i]), VerticalAlignment = VerticalAlignment.Center, CustomMinimumSize = new Vector2(16, 0) });   // one letter width, so the boxes are equal
            boxes[i] = Spin($"{name}_{"xyz"[i]}", _s.Get(key) + " " + _s.Get(axes[i]), min, max, step);
            boxes[i].ValueChanged += _ => { if (!_filling) commit(new System.Numerics.Vector3((float)boxes[0].Value, (float)boxes[1].Value, (float)boxes[2].Value)); };
            axis.AddChild(boxes[i]);
            row.AddChild(axis);
        }
        line.AddChild(row);
        parent.AddChild(line);
    }

    private void ShapeRow(Container parent, string name, string key, double min, double max, double step, bool pair)
    {
        var holder = new HBoxContainer { Name = name };
        holder.AddChild(LabelCell(name + "_label", _s.Get(key)));
        var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var a = Spin(name + "_a", _s.Get(key), min, max, step);
        a.ValueChanged += _ => SendShape();
        row.AddChild(a);
        SpinBox? b = null;
        if (pair)
        {
            b = Spin(name + "_b", _s.Get(key) + " 2", min, max, step);
            b.ValueChanged += _ => SendShape();
            row.AddChild(b);
        }
        holder.AddChild(row);
        parent.AddChild(holder);
        _shape[name] = (holder, a, b);
    }

    private void SendFlags()
    {
        if (!_filling)
            _edit()?.SetFlags(_physical.ButtonPressed, _temporary.ButtonPressed, _phantom.ButtonPressed);
    }

    /// <summary>The shape the Shape section's fields describe, on top of the selected prim's own.</summary>
    public PrimShape? ShapeFromFields()
    {
        if (_edit()?.Object() is not { Shape: { } current })
            return null;
        var kind = ShapeRules.KindOf(current);
        bool round = kind is PrimKind.Sphere or PrimKind.Torus or PrimKind.Tube or PrimKind.Ring;
        double V(string n) => _shape[n].A.Value;
        double W(string n) => _shape[n].B!.Value;
        double range = ShapeRules.TwistDegreesRange(current);
        var s = current with
        {
            ProfileHollow = V("hollow") / 100.0,
            ProfileHole = _hole.GetSelectedId() is var h and >= 0 ? h : current.ProfileHole,
            PathTwistBegin = V("twist") / range,
            PathTwist = W("twist") / range,
            PathShearX = V("top_shear"),
            PathShearY = W("top_shear"),
        };
        if (round)
        {
            s = s with { PathBegin = V("path_cut"), PathEnd = W("path_cut") };
            s = kind == PrimKind.Sphere
                ? s with { ProfileBegin = V("dimple"), ProfileEnd = W("dimple") }
                : s with
                {
                    ProfileBegin = V("profile_cut"), ProfileEnd = W("profile_cut"), PathScaleX = V("hole_size"), PathScaleY = W("hole_size"),
                    PathTaperX = V("taper"), PathTaperY = W("taper"), PathRadiusOffset = V("radius"), PathRevolutions = V("revolutions"), PathSkew = V("skew"),
                };
        }
        else
        {
            // a straight path: the cut is the profile's, the slice the path's, and taper is the top's size (scale = 1 - taper)
            s = s with
            {
                ProfileBegin = V("path_cut"), ProfileEnd = W("path_cut"), PathBegin = V("slice"), PathEnd = W("slice"),
                PathScaleX = 1 - V("taper"), PathScaleY = 1 - W("taper"),
            };
        }
        return s;
    }

    private void SendShape()
    {
        if (!_filling && ShapeFromFields() is { } s)
            _edit()?.SetShape(s);
    }

    public override void _Process(double delta)
    {
        // the hint follows a held Ctrl or Ctrl+Shift at once, as the tool's mode does; two key reads, only while the window shows
        if (!IsVisibleInTree())
            return;
        bool ctrl = Input.IsKeyPressed(Key.Ctrl), shift = Input.IsKeyPressed(Key.Shift);
        if (ctrl != _ctrlHeld || shift != _shiftHeld)
        {
            (_ctrlHeld, _shiftHeld) = (ctrl, shift);
            ShowHint();
        }
    }

    private void ShowHint()
    {
        var e = _edit();
        _hint.Text = e is not null
            ? BuildLayout.Hint(e.Tool, e.Manip, e.SelectFaces, _ctrlHeld, _shiftHeld, _s)
            : BuildLayout.Hint(BuildTool.Edit, ManipMode.Move, false, _ctrlHeld, _shiftHeld, _s);
    }

    // the object's flags as the grid sends them (the official FLAGS_PHANTOM and FLAGS_TEMPORARY_ON_REZ)
    private const uint Phantom = 1u << 10, TemporaryOnRez = 1u << 29;

    /// <summary>What the closed sections summarise, read from the controller.</summary>
    private BuildFacts? Facts()
    {
        var e = _edit();
        if (e?.Object() is not { } o)
            return null;
        var props = e.PropertiesOf(o.Id.LocalId);
        var f = e.Features(o.Id.LocalId);
        uint flags = o.Interaction?.Flags ?? 0;
        var shown = e.FaceShown();
        return new BuildFacts(
            Position: o.Position, Size: o.Scale, RotationDegrees: EditNumbers.ToEulerDegrees(o.Rotation),
            ShapeName: o.Shape is { } sh ? _s.Get("edit.type." + ShapeRules.KindOf(sh).ToString().ToLowerInvariant()) : null,
            Hollow: (o.Shape?.ProfileHollow ?? 0) * 100, TwistDegrees: o.Shape is { } tw ? (tw.PathTwist - tw.PathTwistBegin) * ShapeRules.TwistDegreesRange(tw) : 0,
            FacesPicked: e.SelectedFaces.Count, SelectFaces: e.SelectFaces,
            // a texture is named only by its inventory name; one known only by its id is left out of the line
            Texture: shown is not null && !Guid.TryParse(_textureTab.TextureName, out _) ? _textureTab.TextureName : null,
            Name: props?.Name, Locked: e.Access(o.Id.LocalId).Locked, Physical: (flags & Viewer.Core.Interaction.ObjectFlagBits.Physics) != 0,
            Temporary: (flags & TemporaryOnRez) != 0, Phantom: (flags & Phantom) != 0,
            Flexible: f?.Flexible is not null, Light: f?.Light is not null,
            PhysicsShape: f?.Physics is { ShapeType: var st } && st != FeatureRules.ShapePrim
                ? _s.Get(st == FeatureRules.ShapeNone ? "edit.feat.shape_type.none" : "edit.feat.shape_type.convex") : null,
            ContentCount: e.ContentsKnown ? e.Contents.Count : null, Pivot: e.PivotChoice);
    }

    /// <summary>Shows the controller's state; a field being typed in keeps its text.</summary>
    public void Refresh()
    {
        var e = _edit();
        // the price's unit is the grid's currency symbol, which is only known once the person is on a grid (empty on a grid with no money)
        _priceLabel.Text = _s.Get("edit.sale_price");
        _price.AccessibilityName = _priceLabel.Text.Length > 0 ? _priceLabel.Text : _s.Get("edit.sale_price.name");
        _filling = true;
        try
        {
            foreach (var (tool, b) in _tools)
                b.SetPressedNoSignal(e is { BuildMode: true } && e.Tool == tool);
            foreach (var (tool, b) in _tools)
                b.ThemeTypeVariation = b.ButtonPressed ? ReverieStyle.SegmentSelected : ReverieStyle.Segment;
            foreach (var (mode, b) in _manips)
            {
                b.SetPressedNoSignal(!(e?.SelectFaces ?? false) && (e?.Manip ?? ManipMode.Move) == mode);
                b.ThemeTypeVariation = b.ButtonPressed ? ReverieStyle.SegmentSelected : ReverieStyle.Segment;
            }
            _faceMode.SetPressedNoSignal(e?.SelectFaces ?? false);
            _faceMode.ThemeTypeVariation = _faceMode.ButtonPressed ? ReverieStyle.SegmentSelected : ReverieStyle.Segment;
            _undo.Disabled = !(e?.CanUndo ?? false);
            _redo.Disabled = !(e?.CanRedo ?? false);
            foreach (var (what, b) in _pasteButtons)
                b.Disabled = !(e?.CanPaste(what) ?? false);
            _manipTrack.Visible = e is null || e is { Tool: BuildTool.Edit or BuildTool.Move };
            _createRow.Visible = e is { BuildMode: true, Tool: BuildTool.Create };
            _card.Visible = !_createRow.Visible;   // Create has no selection to describe
            _keepTool.SetPressedNoSignal(e?.KeepToolSelected ?? false);
            // with Copy selection on no shape button shows chosen, as in the official window
            foreach (var (shape, b) in _shapeButtons)
            {
                b.SetPressedNoSignal(e is { CopySelection: false } && e.ShapeChoice == shape);
                b.ThemeTypeVariation = b.ButtonPressed ? ReverieStyle.SegmentSelected : ReverieStyle.Segment;
            }
            _copySelection.SetPressedNoSignal(e?.CopySelection ?? false);
            _copyCenters.SetPressedNoSignal(e?.CopyCenters ?? true);
            _copyRotates.SetPressedNoSignal(e?.CopyRotates ?? false);
            _copyCenters.Disabled = _copyRotates.Disabled = !(e?.CopySelection ?? false);
            _linked.SetPressedNoSignal(e?.EditLinked ?? false);
            _snap.SetPressedNoSignal(e?.SnapEnabled ?? true);
            var pivotChoice = e?.PivotChoice ?? PivotMode.Centre;
            _pivotMode.Selected = _pivotMode.GetItemIndex((int)pivotChoice);
            _pivotUnit.Selected = _pivotUnit.GetItemIndex((int)(e?.PivotUnitChoice ?? PivotUnit.Percent));
            SetVec(_pivotOffset, e?.PivotOffset ?? System.Numerics.Vector3.Zero, pivotChoice != PivotMode.Custom);
            _pivotUnit.Disabled = pivotChoice != PivotMode.Custom;
            _both.SetPressedNoSignal(e?.StretchBothSides ?? false);
            _stretchTex.SetPressedNoSignal(e?.StretchTextures ?? true);
            _ownedOnly.SetPressedNoSignal(e?.SelectOwnedOnly ?? false);
            var o = e?.Object();
            var access = o is null || e is null ? new EditAccess(false, false, false, false) : e.Access(o.Id.LocalId);
            int count = e?.Selection.Count ?? 0;
            DisabledTip.Set(Link, count < 2, _s.Get("disabled.edit.link"));
            DisabledTip.Set(Unlink, count == 0, _s.Get("disabled.edit.unlink"));
            if (LastRefusal is null)
                _status.Text = o is null ? _s.Get("edit.none") : access.ReadOnly ? _s.Get("edit.read_only") : count > 1 ? string.Format(CultureInfo.InvariantCulture, _s.Get("edit.selected"), count) : "";
            _status.Visible = _status.Text.Length > 0;
            var props = o is null || e is null ? null : e.PropertiesOf(o.Id.LocalId);
            SetText(_name, props?.Name ?? "");
            SetText(_desc, props?.Description ?? "");
            _name.Editable = _desc.Editable = access.CanReshape;
            // people by name through the session's name cache, never by id (llpanelpermissions.cpp shows names)
            _creator.Text = props is null ? "" : BuildNames.Person(_names, props.CreatorId, _s);
            _owner.Text = props is null ? "" : BuildNames.Owner(_names, props.OwnerId, props.GroupId, false, _s);
            var sale = e?.SaleState();
            _forSale.SetPressedNoSignal(sale?.ForSale ?? false);
            _forSale.Disabled = !(sale?.BoxEnabled ?? false);
            _price.Editable = sale?.PriceEnabled ?? false;
            if (!_price.GetLineEdit().HasFocus() && sale is not null)
                _price.SetValueNoSignal(sale.Price);
            _saleType.Disabled = !(sale?.TypeEnabled ?? false);
            if (sale is not null)
                _saleType.Select(_saleType.GetItemIndex((int)sale.Type));
            var shownClick = e?.ClickChoiceShown;
            _clickAction.Disabled = !(e?.ClickChoiceEnabled ?? false);
            if (shownClick is { } sc)
                _clickAction.Select(_clickAction.GetItemIndex((int)sc));
            _impact.Text = e?.LandImpactShown() is var (roots, impact)
                ? _s.Get("edit.card.land_impact", new Counted("count.objects", roots), impact.ToString(CultureInfo.InvariantCulture))
                : "";
            _impact.Visible = _impact.Text.Length > 0;
            const uint copy = 1u << 15, modify = 1u << 14, transfer = 1u << 13, move = 1u << 19;
            bool owned = access.Owned;
            foreach (var c in new[] { _shareGroup, _anyMove, _anyCopy, _nextModify, _nextCopy, _nextTransfer, _locked })
                c.Disabled = !owned;
            _anyMove.SetPressedNoSignal(props is not null && (props.EveryoneMask & move) != 0);
            _anyCopy.SetPressedNoSignal(props is not null && (props.EveryoneMask & copy) != 0);
            _nextModify.SetPressedNoSignal(props is not null && (props.NextOwnerMask & modify) != 0);
            _nextCopy.SetPressedNoSignal(props is not null && (props.NextOwnerMask & copy) != 0);
            _nextTransfer.SetPressedNoSignal(props is not null && (props.NextOwnerMask & transfer) != 0);
            _locked.SetPressedNoSignal(access.Locked);
            uint flags = o?.Interaction?.Flags ?? 0;
            _physical.SetPressedNoSignal((flags & Viewer.Core.Interaction.ObjectFlagBits.Physics) != 0);
            _physical.Disabled = _temporary.Disabled = _phantom.Disabled = !access.CanReshape;
            if (o is not null)
            {
                SetVec(_pos, o.Position, !access.CanPlace);
                SetVec(_size, o.Scale, !access.CanReshape);
                SetVec(_rot, EditNumbers.ToEulerDegrees(o.Rotation), !access.CanPlace);
                if (o.Shape is { } shape)
                    FillShape(shape, !access.CanReshape);
            }

            // the selection card: the object's name, what is picked, the owner
            _cardName.Text = o is null ? _s.Get("edit.card.none") : props?.Name is { Length: > 0 } n ? n : _s.Get("edit.summary.unnamed");
            int faces = e?.SelectedFaces.Count ?? 0;
            _cardPicked.Text = o is null ? "" : e!.SelectFaces && faces > 0
                ? (faces == 1 ? _s.Get("edit.card.face_one") : _s.Get("edit.card.faces", faces))
                : (count == 1 ? _s.Get("edit.card.prim_one") : _s.Get("edit.card.prims", count));
            _cardOwner.Text = props is null ? "" : _s.Get("edit.card.owner", BuildNames.Owner(_names, props.OwnerId, props.GroupId, false, _s));
            _cardPicked.Visible = _cardPicked.Text.Length > 0;
            _cardOwner.Visible = _cardOwner.Text.Length > 0;
        }
        finally { _filling = false; }
        _textureTab.Refresh();
        _featuresTab.Refresh(IsOpen(BuildSection.Behaviour) && IsVisibleInTree());
        _contentTab.Refresh(IsVisibleInTree());   // read once for each selected object, whether the section is open or not, so it can open for an object that holds something
        // the Contents section opens by itself, once for each selected object that holds something; the person can close it again
        var seen = e?.Object()?.FullId ?? Guid.Empty;
        if (seen != _contentsSeenFor && (seen == Guid.Empty || e!.ContentsKnown))
        {
            _contentsSeenFor = seen;
            ShowSection(BuildSection.Contents, seen != Guid.Empty && BuildLayout.ContentsOpenFor(e!.Contents.Count) || _memory.IsOpen(BuildSection.Contents));
        }
        var facts = Facts();
        foreach (var (id, p) in _sections)
            p.Summary.Text = BuildLayout.Summary(id, facts, _s);
        ShowHint();
    }

    /// <summary>The middle of a control of the window on screen, by its name, for the live check's pointer; null when it is not shown.
    /// "section:look" is a section's header; "tab:texture" (the official tab names the live checks were written with) is the header of the
    /// section that tab's controls went to.</summary>
    public float[]? ControlPoint(string name)
    {
        if ((name.StartsWith("tab:", StringComparison.Ordinal) || name.StartsWith("section:", StringComparison.Ordinal))
            && BuildLayout.SectionOfTab(name[(name.IndexOf(':') + 1)..]) is { } section)
        {
            var header = _sections[section].Header;
            if (!header.IsVisibleInTree())
                return null;
            var c = header.GetGlobalRect().GetCenter();
            return [c.X, c.Y];
        }
        // "popup:<dropdown>:<item>": an item of a dropdown's open list
        if (name.StartsWith("popup:", StringComparison.Ordinal) && name.Split(':') is [_, var owner, var item] && int.TryParse(item, out int index)
            && FindChild(owner, true, false) is OptionButton ob && ob.GetPopup() is { Visible: true } popup && index >= 0 && index < popup.ItemCount)
        {
            float h = popup.Size.Y / (float)popup.ItemCount;
            return [popup.Position.X + popup.Size.X / 2f, popup.Position.Y + h * (index + 0.5f)];
        }
        if (FindChild(name, true, false) is Control ctl && ctl.IsVisibleInTree())
        {
            if (ctl is SpinBox sb)
                ctl = sb.GetLineEdit();
            var g = ctl.GetGlobalRect().GetCenter();
            // a control in a popup window (the inventory picker) is placed in that window: add where the window stands
            var win = ctl.GetWindow();
            if (win is not null && win != GetWindow())
                g += new Vector2(win.Position.X, win.Position.Y);
            return [g.X, g.Y];
        }
        return null;
    }

    /// <summary>Brings a control into view as a person would: "tab:texture" or "section:look" scrolls to that section's header and, when it
    /// is closed, leaves it for the click to open; any other control opens the section that holds it and scrolls it into view.</summary>
    public void ScrollTo(string name)
    {
        Control? ctl;
        if ((name.StartsWith("tab:", StringComparison.Ordinal) || name.StartsWith("section:", StringComparison.Ordinal))
            && BuildLayout.SectionOfTab(name[(name.IndexOf(':') + 1)..]) is { } section)
        {
            // the live checks click the header to show a section: an open one is closed first, so their click opens it
            if (IsOpen(section))
                SetOpen(section, false);
            ctl = _sections[section].Header;
        }
        else
        {
            ctl = FindChild(name, true, false) as Control;
            if (ctl is null)
                return;
            foreach (var (id, p) in _sections)
                if (p.Body.IsAncestorOf(ctl) && !IsOpen(id))
                    SetOpen(id, true);
        }
        for (Node? n = ctl.GetParent(); n is not null; n = n.GetParent())
            if (n is ScrollContainer sc)
            {
                sc.EnsureControlVisible(ctl);
                return;
            }
    }

    private static void SetText(LineEdit e, string t)
    {
        if (!e.HasFocus() && e.Text != t)
            e.Text = t;
    }

    private System.Numerics.Vector3 PivotNumbers() => new((float)_pivotOffset[0].Value, (float)_pivotOffset[1].Value, (float)_pivotOffset[2].Value);
    private PivotUnit PivotUnitNow() => (PivotUnit)_pivotUnit.GetItemId(_pivotUnit.Selected);

    private static void SetVec(SpinBox[] boxes, System.Numerics.Vector3 v, bool readOnly)
    {
        float[] parts = [v.X, v.Y, v.Z];
        for (int i = 0; i < 3; i++)
        {
            boxes[i].Editable = !readOnly;
            if (!boxes[i].GetLineEdit().HasFocus())
                boxes[i].SetValueNoSignal(parts[i]);
        }
    }

    private void FillShape(PrimShape s, bool readOnly)
    {
        var kind = ShapeRules.KindOf(s);
        _type.Select(_type.GetItemIndex((int)kind));
        _type.Disabled = readOnly || kind == PrimKind.Sculpt;
        _hole.Select(Math.Max(0, _hole.GetItemIndex(s.ProfileHole & 0xF0)));
        bool round = kind is PrimKind.Sphere or PrimKind.Torus or PrimKind.Tube or PrimKind.Ring;
        bool ringish = kind is PrimKind.Torus or PrimKind.Tube or PrimKind.Ring;
        double range = ShapeRules.TwistDegreesRange(s);
        void Set(string n, double a, double? b = null, bool shown = true)
        {
            var (row, sa, sb) = _shape[n];
            // a row the search hid stays hidden until the search is cleared
            if (!_hiddenBySearch.Contains(row))
                row.Visible = shown && kind != PrimKind.Sculpt;
            sa.Editable = !readOnly;
            sa.SetValueNoSignal(a);
            if (sb is not null && b is { } bv)
            {
                sb.Editable = !readOnly;
                sb.SetValueNoSignal(bv);
            }
        }
        Set("path_cut", round ? s.PathBegin : s.ProfileBegin, round ? s.PathEnd : s.ProfileEnd);
        Set("hollow", s.ProfileHollow * 100);
        Set("twist", s.PathTwistBegin * range, s.PathTwist * range);
        Set("taper", ringish ? s.PathTaperX : 1 - s.PathScaleX, ringish ? s.PathTaperY : 1 - s.PathScaleY, kind != PrimKind.Sphere);
        Set("top_shear", s.PathShearX, s.PathShearY, kind != PrimKind.Sphere);
        Set("slice", s.PathBegin, s.PathEnd, !round);
        Set("dimple", s.ProfileBegin, s.ProfileEnd, kind == PrimKind.Sphere);
        Set("profile_cut", s.ProfileBegin, s.ProfileEnd, ringish);
        Set("hole_size", s.PathScaleX, s.PathScaleY, ringish);
        Set("radius", s.PathRadiusOffset, null, ringish);
        Set("revolutions", s.PathRevolutions, null, ringish);
        Set("skew", s.PathSkew, null, ringish);
    }

    /// <summary>The window's texts for the automation: the status line, the sections (open or closed, with their summaries), the hint,
    /// the search's hits, and which shape rows show. "tabs" keeps the official tab names the sections hold, for older checks.</summary>
    public object Texts() => new
    {
        status = _status.Text,
        hint = _hint.Text,
        card = new[] { _cardName.Text, _cardPicked.Text, _cardOwner.Text },
        sections = BuildLayout.Sections.Select(s => new { name = BuildLayout.NameOf(s), open = IsOpen(s), summary = _sections[s].Summary.Text }).ToArray(),
        search = _search.Text,
        search_hits = SearchShown(),
        tabs = BuildLayout.Sections.Select(s => _s.Get(BuildLayout.TitleKey(s))).ToArray(),
        shape_rows = _shape.Where(kv => kv.Value.Row.Visible).Select(kv => kv.Key).ToArray(),
        type = _type.Selected >= 0 ? _type.GetItemText(_type.Selected) : null,
        texture = _textureTab.Texts(),
        features = _featuresTab.Texts(),
        contents = _contentTab.Names,
    };
}
