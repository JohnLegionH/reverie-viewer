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
/// The Build window: the tools (Focus, Move, Edit, Create), Edit linked, the grid and stretch ticks, Link and Unlink, and the General and
/// Object tabs of the selected object, laid out after the official build floater (floater_tools.xml, read as behaviour). It only shows what
/// <see cref="EditController"/> holds and hands every change to it. Standard controls styled through the Theme alone. Main thread only.
/// </summary>
public partial class EditPanel : VBoxContainer
{
    public const string FloaterId = "build";

    private readonly StringTable _s;
    private readonly Func<EditController?> _edit;
    private readonly Dictionary<BuildTool, Button> _tools = new();
    private readonly Dictionary<ManipMode, CheckBox> _manips = new();
    private readonly HBoxContainer _manipRow;
    private readonly CheckBox _linked, _snap, _both, _stretchTex, _ownedOnly;
    private readonly Label _status;
    public Button Link { get; }
    public Button Unlink { get; }
    public TabContainer Tabs { get; }

    // General
    private readonly LineEdit _name, _desc;
    private readonly Label _creator, _owner, _sale;
    private readonly CheckBox _shareGroup, _anyMove, _anyCopy, _nextModify, _nextCopy, _nextTransfer;
    // Object
    private readonly SpinBox[] _pos = new SpinBox[3], _size = new SpinBox[3], _rot = new SpinBox[3];
    private readonly CheckBox _locked, _physical, _temporary, _phantom;
    private readonly OptionButton _type, _hole;
    private readonly Dictionary<string, (Control Row, SpinBox A, SpinBox? B)> _shape = new();

    private bool _filling;
    public string? LastRefusal { get; private set; }

    private readonly EditTextureTab _textureTab;
    private CheckBox _faceMode = null!;
    private HBoxContainer _createRow = null!;
    private CheckBox _keepTool = null!;
    /// <summary>Raised when Keep Tool selected is ticked or cleared (the viewer remembers it).</summary>
    public event Action<bool>? KeepToolChanged;
    private readonly Dictionary<ObjectClip, Button> _pasteButtons = new();

    public EditTextureTab TextureTab => _textureTab;
    private readonly EditFeaturesTab _featuresTab;
    private readonly EditContentTab _contentTab;
    public EditFeaturesTab FeaturesTab => _featuresTab;
    public EditContentTab ContentTab => _contentTab;

    public EditPanel(StringTable strings, Func<EditController?> edit, Func<Viewer.Core.Inventory.InventoryModel?>? inventory = null)
    {
        _s = strings;
        _edit = edit;
        Name = "edit_panel";
        SizeFlagsVertical = SizeFlags.ExpandFill;

        var tools = new HBoxContainer { Name = "tools" };
        var group = new ButtonGroup();
        foreach (var (tool, key) in new[] { (BuildTool.Focus, "edit.tool.focus"), (BuildTool.Move, "edit.tool.move"), (BuildTool.Edit, "edit.tool.edit"), (BuildTool.Create, "edit.tool.create") })
        {
            var b = new Button { Name = "tool_" + tool.ToString().ToLowerInvariant(), Text = strings.Get(key), ToggleMode = true, ButtonGroup = group };
            b.AccessibilityName = b.Text;
            b.Pressed += () => ToolChosen?.Invoke(tool);
            tools.AddChild(b);
            _tools[tool] = b;
        }
        AddChild(tools);

        // Move, Rotate or Stretch: which handles the Edit and Move tools show (floater_tools.xml's radio group; Ctrl and Ctrl+Shift switch while held)
        _manipRow = new HBoxContainer { Name = "manip" };
        var manipGroup = new ButtonGroup();
        foreach (var (mode, key) in new[] { (ManipMode.Move, "edit.manip.move"), (ManipMode.Rotate, "edit.manip.rotate"), (ManipMode.Scale, "edit.manip.scale") })
        {
            var b = new CheckBox { Name = "manip_" + mode.ToString().ToLowerInvariant(), Text = strings.Get(key), ButtonGroup = manipGroup };
            b.AccessibilityName = b.Text;
            b.Pressed += () => { if (_edit() is { } e) e.Manip = mode; };
            _manipRow.AddChild(b);
            _manips[mode] = b;
            b.Pressed += () => { if (_edit() is { } e) e.SelectFaces = false; };
        }
        // Select Face: the official radio choice beside Position, Rotate and Stretch (floater_tools.xml radio_select_face)
        _faceMode = new CheckBox { Name = "manip_face", Text = strings.Get("edit.manip.face"), ButtonGroup = manipGroup };
        _faceMode.AccessibilityName = _faceMode.Text;
        _faceMode.Pressed += () => { if (_edit() is { } e) e.SelectFaces = true; };
        _manipRow.AddChild(_faceMode);
        AddChild(_manipRow);

        // the Create tool's option: after a placement the tool stays Create instead of handing back to Edit (floater_tools.xml "checkbox sticky",
        // CreateToolKeepSelected); shown only while Create is the tool, as the official window shows it
        _createRow = new HBoxContainer { Name = "create_options" };
        _keepTool = Tick(_createRow, "keep_tool", "edit.keep_tool", on => { if (_edit() is { } e) e.KeepToolSelected = on; KeepToolChanged?.Invoke(on); });
        AddChild(_createRow);

        var ticks = new HFlowContainer { Name = "ticks" };
        _linked = Tick(ticks, "linked", "edit.linked", on => { if (_edit() is { } e) e.EditLinked = on; });
        _snap = Tick(ticks, "snap", "edit.snap", on => { if (_edit() is { } e) e.SnapEnabled = on; });
        _both = Tick(ticks, "stretch_both", "edit.stretch_both", on => { if (_edit() is { } e) e.StretchBothSides = on; });
        _stretchTex = Tick(ticks, "stretch_textures", "edit.stretch_textures", on => { if (_edit() is { } e) e.StretchTextures = on; });
        // Select Only My Objects, the official Build > Options item (SelectOwnedOnly), as a tick here too
        _ownedOnly = Tick(ticks, "owned_only", "edit.owned_only", on => { if (_edit() is { } e) e.SelectOwnedOnly = on; });
        AddChild(ticks);

        var linkRow = new HBoxContainer { Name = "link_row" };
        Link = new Button { Name = "link", Text = strings.Get("edit.link") };
        Link.AccessibilityName = Link.Text;
        Link.Pressed += () => DoLink();
        Unlink = new Button { Name = "unlink", Text = strings.Get("edit.unlink") };
        Unlink.AccessibilityName = Unlink.Text;
        Unlink.Pressed += () => _edit()?.Unlink();
        linkRow.AddChild(Link);
        linkRow.AddChild(Unlink);
        AddChild(linkRow);

        _status = new Label { Name = "status", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _status.AccessibilityName = strings.Get("edit.title");
        AddChild(_status);

        Tabs = new TabContainer { Name = "edit_tabs", SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        AddChild(Tabs);

        // ---------------- General ----------------
        var general = Page("general", "edit.tab.general");
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
        var next = new Label { Name = "next_owner", Text = strings.Get("edit.next_owner") };
        general.AddChild(next);
        var nextRow = new HBoxContainer { Name = "next_row" };
        _nextModify = Tick(nextRow, "next_modify", "edit.perm.modify", on => _edit()?.SetPermission(0x10, modify, on));
        _nextCopy = Tick(nextRow, "next_copy", "edit.perm.copy", on => _edit()?.SetPermission(0x10, copy, on));
        _nextTransfer = Tick(nextRow, "next_transfer", "edit.perm.transfer", on => _edit()?.SetPermission(0x10, transfer, on));
        general.AddChild(nextRow);
        _sale = new Label { Name = "for_sale" };
        _sale.AccessibilityName = strings.Get("edit.for_sale");
        general.AddChild(_sale);

        // ---------------- Object ----------------
        var obj = Page("object", "edit.tab.object");
        Triple(obj, "position", "edit.position", _pos, -256, 8192, 0.001, v => _edit()?.SetPosition(v));
        Triple(obj, "size", "edit.size", _size, 0.001, 256, 0.001, v => _edit()?.SetSize(v));
        Triple(obj, "rotation", "edit.rotation", _rot, -9999, 9999, 0.05, v => _edit()?.SetRotation(v));
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
        obj.AddChild(clip);
        var flags = new HFlowContainer { Name = "flags" };
        _locked = Tick(flags, "locked", "edit.locked", on => _edit()?.SetLocked(on));
        _physical = Tick(flags, "physical", "edit.physical", _ => SendFlags());
        _temporary = Tick(flags, "temporary", "edit.temporary", _ => SendFlags());
        _phantom = Tick(flags, "phantom", "edit.phantom", _ => SendFlags());
        obj.AddChild(flags);
        obj.AddChild(new Label { Name = "type_label", Text = strings.Get("edit.prim_type") });
        _type = new OptionButton { Name = "prim_type" };
        _type.AccessibilityName = strings.Get("edit.prim_type");
        foreach (var k in Enum.GetValues<PrimKind>())
            _type.AddItem(strings.Get("edit.type." + k.ToString().ToLowerInvariant()), (int)k);
        _type.ItemSelected += i => { if (!_filling && (PrimKind)_type.GetItemId((int)i) is var k && k != PrimKind.Sculpt) _edit()?.SetShape(ShapeRules.OfKind(k)); };
        obj.AddChild(_type);
        ShapeRow(obj, "path_cut", "edit.path_cut", 0, 1, 0.025, true);
        ShapeRow(obj, "hollow", "edit.hollow", 0, 95, 5, false);
        obj.AddChild(new Label { Name = "hole_label", Text = strings.Get("edit.hollow_shape") });
        _hole = new OptionButton { Name = "hollow_shape" };
        _hole.AccessibilityName = strings.Get("edit.hollow_shape");
        foreach (var (key, id) in new[] { ("same", 0x00), ("circle", 0x10), ("square", 0x20), ("triangle", 0x30) })
            _hole.AddItem(strings.Get("edit.hole." + key), id);
        _hole.ItemSelected += _ => SendShape();
        obj.AddChild(_hole);
        ShapeRow(obj, "twist", "edit.twist", -360, 360, 1, true);
        ShapeRow(obj, "taper", "edit.taper", -1, 1, 0.01, true);
        ShapeRow(obj, "top_shear", "edit.top_shear", -0.5, 0.5, 0.01, true);
        ShapeRow(obj, "slice", "edit.slice", 0, 1, 0.02, true);
        ShapeRow(obj, "dimple", "edit.dimple", 0, 1, 0.02, true);
        ShapeRow(obj, "profile_cut", "edit.profile_cut", 0, 1, 0.02, true);
        ShapeRow(obj, "hole_size", "edit.hole_size", 0.05, 1, 0.01, true);
        ShapeRow(obj, "radius", "edit.radius", -1, 1, 0.01, false);
        ShapeRow(obj, "revolutions", "edit.revolutions", 1, 4, 0.1, false);
        ShapeRow(obj, "skew", "edit.skew", -0.95, 0.95, 0.05, false);

        // ---------------- Features, Texture and Content, in the official order ----------------
        var features = Page("features", "edit.tab.features");
        _featuresTab = new EditFeaturesTab(strings, edit, inventory ?? (() => null), features, this);
        var texture = Page("texture", "edit.tab.texture");
        _textureTab = new EditTextureTab(strings, edit, inventory ?? (() => null), texture, this);
        var content = Page("content", "edit.tab.content");
        _contentTab = new EditContentTab(strings, edit, inventory ?? (() => null), content);
    }

    /// <summary>A tool button was pressed.</summary>
    public event Action<BuildTool>? ToolChosen;

    private void DoLink()
    {
        LastRefusal = _edit()?.Link();
        if (LastRefusal is { } why)
            _status.Text = _s.Get("edit." + why);
    }

    private VBoxContainer Page(string name, string titleKey)
    {
        var scroll = new ScrollContainer { Name = name, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        var page = new VBoxContainer { Name = name + "_page", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        scroll.AddChild(page);
        Tabs.AddChild(scroll);
        Tabs.SetTabTitle(Tabs.GetTabCount() - 1, _s.Get(titleKey));
        return page;
    }

    private CheckBox Tick(Container parent, string name, string key, Action<bool> changed)
    {
        var c = new CheckBox { Name = name, Text = _s.Get(key) };
        c.AccessibilityName = c.Text;
        c.Toggled += on => { if (!_filling) changed(on); };
        parent.AddChild(c);
        return c;
    }

    private LineEdit Field(Container parent, string name, string key, Action<string> commit)
    {
        parent.AddChild(new Label { Name = name + "_label", Text = _s.Get(key) });
        var e = new LineEdit { Name = name };
        e.AccessibilityName = _s.Get(key);
        e.TextSubmitted += t => commit(t);
        e.FocusExited += () => { if (!_filling) commit(e.Text); };
        parent.AddChild(e);
        return e;
    }

    private Label Info(Container parent, string name, string key)
    {
        var row = new HBoxContainer { Name = name + "_row" };
        row.AddChild(new Label { Text = _s.Get(key) });
        var v = new Label { Name = name };
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
        parent.AddChild(new Label { Name = name + "_label", Text = _s.Get(key) });
        var row = new HBoxContainer { Name = name };
        string[] axes = ["edit.x", "edit.y", "edit.z"];
        for (int i = 0; i < 3; i++)
        {
            row.AddChild(new Label { Text = _s.Get(axes[i]) });
            boxes[i] = Spin($"{name}_{"xyz"[i]}", _s.Get(key) + " " + _s.Get(axes[i]), min, max, step);
            boxes[i].ValueChanged += _ => { if (!_filling) commit(new System.Numerics.Vector3((float)boxes[0].Value, (float)boxes[1].Value, (float)boxes[2].Value)); };
            row.AddChild(boxes[i]);
        }
        parent.AddChild(row);
    }

    private void ShapeRow(Container parent, string name, string key, double min, double max, double step, bool pair)
    {
        var holder = new VBoxContainer { Name = name };
        holder.AddChild(new Label { Text = _s.Get(key) });
        var row = new HBoxContainer();
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

    /// <summary>The shape the Object tab's fields describe, on top of the selected prim's own.</summary>
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

    /// <summary>Shows the controller's state; a field being typed in keeps its text.</summary>
    public void Refresh()
    {
        var e = _edit();
        _filling = true;
        try
        {
            foreach (var (tool, b) in _tools)
                b.SetPressedNoSignal(e is { BuildMode: true } && e.Tool == tool);
            foreach (var (mode, b) in _manips)
                b.SetPressedNoSignal(!(e?.SelectFaces ?? false) && (e?.Manip ?? ManipMode.Move) == mode);
            _faceMode.SetPressedNoSignal(e?.SelectFaces ?? false);
            foreach (var (what, b) in _pasteButtons)
                b.Disabled = !(e?.CanPaste(what) ?? false);
            _manipRow.Visible = e is { Tool: BuildTool.Edit or BuildTool.Move };
            _createRow.Visible = e is { BuildMode: true, Tool: BuildTool.Create };
            _keepTool.SetPressedNoSignal(e?.KeepToolSelected ?? false);
            _linked.SetPressedNoSignal(e?.EditLinked ?? false);
            _snap.SetPressedNoSignal(e?.SnapEnabled ?? true);
            _both.SetPressedNoSignal(e?.StretchBothSides ?? false);
            _stretchTex.SetPressedNoSignal(e?.StretchTextures ?? true);
            _ownedOnly.SetPressedNoSignal(e?.SelectOwnedOnly ?? false);
            var o = e?.Object();
            var access = o is null || e is null ? new EditAccess(false, false, false, false) : e.Access(o.Id.LocalId);
            int count = e?.Selection.Count ?? 0;
            Link.Disabled = count < 2;
            Unlink.Disabled = count == 0;
            if (LastRefusal is null)
                _status.Text = o is null ? _s.Get("edit.none") : access.ReadOnly ? _s.Get("edit.read_only") : count > 1 ? string.Format(CultureInfo.InvariantCulture, _s.Get("edit.selected"), count) : "";
            var props = o is null || e is null ? null : e.PropertiesOf(o.Id.LocalId);
            SetText(_name, props?.Name ?? "");
            SetText(_desc, props?.Description ?? "");
            _name.Editable = _desc.Editable = access.CanReshape;
            _creator.Text = props is null ? "" : props.CreatorId.ToString();
            _owner.Text = props is null ? "" : props.OwnerId.ToString();
            _sale.Text = props is { ForSale: true } ? string.Format(CultureInfo.InvariantCulture, _s.Get("edit.for_sale.price"), props.SalePrice) : _s.Get("edit.not_for_sale");
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
        }
        finally { _filling = false; }
        _textureTab.Refresh();
        _featuresTab.Refresh(Tabs.GetCurrentTabControl()?.Name == "features" && IsVisibleInTree());
        _contentTab.Refresh(Tabs.GetCurrentTabControl()?.Name == "content" && IsVisibleInTree());
    }

    /// <summary>The middle of a control of the window on screen, by its name ("tab:texture" for a tab's title), for the live check's
    /// pointer; null when it is not shown.</summary>
    public float[]? ControlPoint(string name)
    {
        if (name.StartsWith("tab:", StringComparison.Ordinal))
        {
            for (int i = 0; i < Tabs.GetTabCount(); i++)
                if (Tabs.GetTabControl(i).Name == name[4..])
                {
                    var bar = Tabs.GetTabBar();
                    var r = bar.GetTabRect(i);
                    var c = bar.GetGlobalTransformWithCanvas() * (r.Position + r.Size / 2);
                    return [c.X, c.Y];
                }
            return null;
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

    /// <summary>Scrolls the shown page so a control is in view (a person scrolls the tab the same way).</summary>
    public void ScrollTo(string name)
    {
        if (FindChild(name, true, false) is not Control ctl)
            return;
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

    /// <summary>The window's texts for the automation: the status line, the tab names, and which shape rows show.</summary>
    public object Texts() => new
    {
        status = _status.Text,
        tabs = Enumerable.Range(0, Tabs.GetTabCount()).Select(i => Tabs.GetTabTitle(i)).ToArray(),
        shape_rows = _shape.Where(kv => kv.Value.Row.Visible).Select(kv => kv.Key).ToArray(),
        type = _type.Selected >= 0 ? _type.GetItemText(_type.Selected) : null,
        texture = _textureTab.Texts(),
        features = _featuresTab.Texts(),
        contents = _contentTab.Names,
    };
}
