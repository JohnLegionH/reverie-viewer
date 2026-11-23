// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using Viewer.Core.Building;
using Viewer.Core.Inventory;
using Viewer.Core.World;
using Viewer.Shell;
using Viewer.Shell.Build;

namespace Viewer.Godot;

/// <summary>
/// The Build window's Texture tab, laid out after the official panel_tools_texture.xml (read as behaviour): the texture (chosen from
/// inventory or the Library), Color, Transparency %, Glow, Full Bright, Mapping, the scales, Repeats per meter, Rotation degrees, the
/// offsets, Align planar faces, Copy and Paste, and the PBR material with its own scale, offset and rotation. Every change goes to the
/// edit controller, which sends it for the selected faces. Standard controls styled through the Theme alone. Main thread only.
/// </summary>
public sealed class EditTextureTab
{
    private readonly StringTable _s;
    private readonly Func<EditController?> _edit;
    private readonly Func<InventoryModel?> _inventory;
    private readonly Control _owner;
    private readonly Label _texture, _material;
    private readonly ColourButton _colour;
    private readonly SpinBox _transparency, _glow, _scaleU, _scaleV, _perMetre, _rotation, _offsetU, _offsetV;
    private readonly SpinBox _pbrScaleU, _pbrScaleV, _pbrOffsetU, _pbrOffsetV, _pbrRotation;
    private readonly CheckBox _fullbright;
    private readonly OptionButton _mapping;
    private readonly Button _paste;
    private FacePickerWindows? _pickers;
    private bool _filling;

    public EditTextureTab(StringTable strings, Func<EditController?> edit, Func<InventoryModel?> inventory, VBoxContainer page, Control owner)
    {
        _s = strings;
        _edit = edit;
        _inventory = inventory;
        _owner = owner;

        // the name beside its label; the button on the line below, so the name keeps its room in the smallest window
        var texRow = new VBoxContainer { Name = "texture_row" };
        var texLine = new HBoxContainer();
        texLine.AddChild(LabelCell("texture_label", "edit.tex.texture"));
        _texture = new Label { Name = "texture_name", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
        _texture.AccessibilityName = strings.Get("edit.tex.texture");
        texLine.AddChild(_texture);
        texRow.AddChild(texLine);
        var choose = Btn("texture_choose", "edit.tex.choose", () => OpenPicker(ItemKind.Texture));
        texRow.AddChild(choose);
        page.AddChild(texRow);

        // the face's colour as a swatch; a press opens the shared colour picker window (which stays open while the person clicks
        // elsewhere). Its changes go to the selected faces, and Cancel puts back each face's own colour (the official selectionRevertColors)
        _colour = new ColourButton { Name = "colour", TitleKey = "edit.picker.colour.title", TooltipText = strings.Get("colour.swatch.tip") };
        _colour.AccessibilityName = strings.Get("edit.tex.colour");
        _colour.Request = FaceColourRequest;
        page.AddChild(Line("colour", "edit.tex.colour", _colour));
        _transparency = Spin(page, "transparency", "edit.tex.transparency", 0, 100, 1, v => new FaceChange(Transparency: (float)v));
        _glow = Spin(page, "glow", "edit.tex.glow", 0, 1, 0.01, v => new FaceChange(Glow: (float)v));
        // the label stands in the label column like its neighbours, with the tick box (no text of its own) beside it; a click on the label ticks it
        _fullbright = new CheckBox { Name = "fullbright" };
        _fullbright.AccessibilityName = strings.Get("edit.tex.fullbright");
        _fullbright.Toggled += on => { if (!_filling) _edit()?.EditFaces(new FaceChange(Fullbright: on)); };
        var fullbrightLine = Line("fullbright", "edit.tex.fullbright", _fullbright);
        _fullbright.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
        if (fullbrightLine.GetChild(0) is Label fullbrightLabel)
        {
            fullbrightLabel.MouseFilter = Control.MouseFilterEnum.Stop;
            fullbrightLabel.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } && !_fullbright.Disabled) _fullbright.ButtonPressed = !_fullbright.ButtonPressed; };
        }
        page.AddChild(fullbrightLine);

        _mapping = new OptionButton { Name = "mapping" };
        _mapping.AccessibilityName = strings.Get("edit.tex.mapping");
        _mapping.AddItem(strings.Get("edit.tex.mapping.default"), (int)TexGen.Default);
        _mapping.AddItem(strings.Get("edit.tex.mapping.planar"), (int)TexGen.Planar);
        _mapping.ItemSelected += i => { if (!_filling) _edit()?.EditFaces(new FaceChange(TexGen: (TexGen)_mapping.GetItemId((int)i))); };
        page.AddChild(Line("mapping", "edit.tex.mapping", _mapping));
        _scaleU = Spin(page, "scale_u", "edit.tex.scale_u", -100, 100, 0.01, v => new FaceChange(RepeatU: (float)v));
        _scaleV = Spin(page, "scale_v", "edit.tex.scale_v", -100, 100, 0.01, v => new FaceChange(RepeatV: (float)v));
        _perMetre = Spin(page, "per_metre", "edit.tex.per_metre", 0, 100, 0.01, null);
        _perMetre.ValueChanged += v => { if (!_filling) _edit()?.SetRepeatsPerMetre((float)v); };
        _rotation = Spin(page, "texture_rotation", "edit.tex.rotation", -360, 360, 0.1, v => new FaceChange(RotationDegrees: (float)v));
        _offsetU = Spin(page, "offset_u", "edit.tex.offset_u", -1, 1, 0.001, v => new FaceChange(OffsetU: (float)v));
        _offsetV = Spin(page, "offset_v", "edit.tex.offset_v", -1, 1, 0.001, v => new FaceChange(OffsetV: (float)v));

        var alignRow = new HBoxContainer { Name = "align_row" };
        alignRow.AddChild(new Label { Text = strings.Get("edit.tex.align_label") });
        alignRow.AddChild(Btn("align", "edit.tex.align", () => _edit()?.AlignPlanarFaces()));
        page.AddChild(alignRow);

        var clip = new HBoxContainer { Name = "face_clipboard" };
        clip.AddChild(Btn("face_copy", "edit.copy", () => _edit()?.CopyFaces()));
        _paste = Btn("face_paste", "edit.paste", () => _edit()?.PasteFaces());
        clip.AddChild(_paste);
        page.AddChild(clip);

        page.AddChild(new HSeparator());
        // the material's name beside its label; its two buttons wrap on the line below, so no button is squeezed
        var matRow = new VBoxContainer { Name = "material_row" };
        var matName = new HBoxContainer();
        matName.AddChild(LabelCell("material_label", "edit.tex.material"));
        _material = new Label { Name = "material_name", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, AutowrapMode = TextServer.AutowrapMode.WordSmart };   // a long name wraps, it is not cut
        _material.AccessibilityName = strings.Get("edit.tex.material");
        matName.AddChild(_material);
        matRow.AddChild(matName);
        var matButtons = new HFlowContainer();
        matButtons.AddChild(Btn("material_choose", "edit.tex.material.choose", () => OpenPicker(ItemKind.Material)));
        matButtons.AddChild(Btn("material_remove", "edit.tex.material.remove", () => _edit()?.RemoveMaterial()));
        matRow.AddChild(matButtons);
        page.AddChild(matRow);
        _pbrScaleU = Spin(page, "pbr_scale_u", "edit.tex.pbr_scale_u", -100, 100, 0.01, null);
        _pbrScaleV = Spin(page, "pbr_scale_v", "edit.tex.pbr_scale_v", -100, 100, 0.01, null);
        _pbrOffsetU = Spin(page, "pbr_offset_u", "edit.tex.pbr_offset_u", -1, 1, 0.001, null);
        _pbrOffsetV = Spin(page, "pbr_offset_v", "edit.tex.pbr_offset_v", -1, 1, 0.001, null);
        _pbrRotation = Spin(page, "pbr_rotation", "edit.tex.pbr_rotation", -360, 360, 0.1, null);
        foreach (var b in new[] { _pbrScaleU, _pbrScaleV, _pbrOffsetU, _pbrOffsetV, _pbrRotation })
            b.ValueChanged += _ => SendPbrTransform();
    }

    private Button Btn(string name, string key, Action pressed)
    {
        var b = new Button { Name = name, Text = _s.Get(key) };
        b.AccessibilityName = b.Text;
        b.Pressed += pressed;
        return b;
    }

    private SpinBox Spin(Container page, string name, string key, double min, double max, double step, Func<double, FaceChange>? change)
    {
        var s = new SpinBox
        {
            Name = name, MinValue = min, MaxValue = max, Step = step, AllowGreater = false, AllowLesser = false,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, AccessibilityName = _s.Get(key),
        };
        if (change is not null)
            s.ValueChanged += v => { if (!_filling) _edit()?.EditFaces(change(v)); };
        page.AddChild(Line(name, key, s));
        return s;
    }

    /// <summary>The label column of the Look section: wide enough for its longest name ("Repeats per meter"), so every control starts at the same x.</summary>
    private const float LabelColumn = 150;

    private Label LabelCell(string name, string key) => new() { Name = name, Text = _s.Get(key), CustomMinimumSize = new Vector2(LabelColumn, 0), VerticalAlignment = VerticalAlignment.Center };

    /// <summary>A label in a fixed column with its control beside it (UI-STANDARD U1); the line is named "&lt;row&gt;_line" so a search hides it with the row.</summary>
    private HBoxContainer Line(string name, string key, Control control)
    {
        var line = new HBoxContainer { Name = name + "_line" };
        line.AddChild(LabelCell(name + "_label", key));
        control.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        line.AddChild(control);
        return line;
    }

    private void SendPbrTransform()
    {
        if (_filling)
            return;
        _edit()?.SetMaterialTransform(new TextureTransform((float)_pbrOffsetU.Value, (float)_pbrOffsetV.Value, (float)_pbrScaleU.Value, (float)_pbrScaleV.Value,
            (float)(_pbrRotation.Value * Math.PI / 180.0)));
    }

    /// <summary>The colour picker's request for the selected faces: each change goes to them all, and Cancel puts back each face's own
    /// colour as it was when the picker opened.</summary>
    private ColourRequest? FaceColourRequest()
    {
        if (_edit() is not { } e)
            return null;
        var saved = e.SaveFacePicks();
        return new ColourRequest("edit.picker.colour.title", ColourPickerWindow.ToUi(_colour.Color), false,
            c => _edit()?.EditFaces(new FaceChange(Colour: new System.Numerics.Vector3(c.R, c.G, c.B))),
            _ => _edit()?.RevertColours(saved));
    }

    /// <summary>The shown face's texture, by name where the inventory has it.</summary>
    public string TextureName => _textureFull;
    private string _textureFull = "";
    /// <summary>Whether the shown texture is named (an inventory name or a standard texture) and not just an id.</summary>
    public bool TextureNamed => !Guid.TryParse(_textureFull, out _);

    /// <summary>The face colour's swatch (it opens the shared colour picker).</summary>
    public ColourButton Colour => _colour;

    /// <summary>Gives the tab its picker windows (a shell to put them in); without them the choose buttons and the swatch do nothing.</summary>
    public void AttachPickers(ShellRoot shell) => _pickers ??= new FacePickerWindows(_s, shell, _edit);

    /// <summary>The picker windows, for the automation and the live check.</summary>
    public FacePickerWindows? Pickers => _pickers;

    /// <summary>The texture or material picker's list while its window is open, for the automation and the live check.</summary>
    public InventoryPickPanel? Picker => _pickers is { ItemsOpen: true } p ? p.Items : null;

    /// <summary>Opens the inventory picker window for textures or materials (the account's and the Library's).</summary>
    public void OpenPicker(ItemKind kind)
    {
        if (_inventory() is { } model)
            _pickers?.OpenItems(model, kind);
    }

    /// <summary>Shows the face the controller shows; a field being typed in keeps its text.</summary>
    public void Refresh()
    {
        var e = _edit();
        _filling = true;
        try
        {
            var f = e?.FaceShown();
            bool editable = f is not null && e?.Primary is { } p && e.Access(p).CanReshape;
            _textureFull = f is { } face ? Show(_texture, face.TextureId) : Clear(_texture);
            if (f is { } shown)
            {
                _colour.Color = ShellTheme.DataColour(shown.Colour.R, shown.Colour.G, shown.Colour.B);   // the face's own colour, not a theme colour
                Set(_transparency, Math.Round((1 - shown.Colour.A) * 100));
                Set(_glow, shown.Glow);
                _fullbright.SetPressedNoSignal(shown.Fullbright);
                _mapping.Select(_mapping.GetItemIndex((int)shown.TexGen));
                Set(_scaleU, shown.RepeatU);
                Set(_scaleV, shown.RepeatV);
                Set(_rotation, shown.Rotation * 180.0 / Math.PI);
                Set(_offsetU, shown.OffsetU);
                Set(_offsetV, shown.OffsetV);
                if (e?.ShownFaceSize is { } size)
                    Set(_perMetre, FaceRules.PerMetre(shown.RepeatU, size.Face, size.Scale));
            }
            foreach (var c in new[] { _transparency, _glow, _scaleU, _scaleV, _perMetre, _rotation, _offsetU, _offsetV })
                c.Editable = editable;
            _fullbright.Disabled = !editable;
            _mapping.Disabled = !editable;
            _colour.Disabled = !editable;
            _paste.Disabled = !(e?.CanPasteFaces ?? false) || !editable;
            Guid material = e?.ShownMaterial ?? Guid.Empty;
            if (material == Guid.Empty) { Clear(_material); _material.Text = _s.Get("edit.tex.material.none"); }
            else Show(_material, material);
            var t = e?.ShownMaterialTransform ?? TextureTransform.Identity;
            Set(_pbrScaleU, t.ScaleU);
            Set(_pbrScaleV, t.ScaleV);
            Set(_pbrOffsetU, t.OffsetU);
            Set(_pbrOffsetV, t.OffsetV);
            Set(_pbrRotation, t.Rotation * 180.0 / Math.PI);
            foreach (var c in new[] { _pbrScaleU, _pbrScaleV, _pbrOffsetU, _pbrOffsetV, _pbrRotation })
                c.Editable = editable && material != Guid.Empty;
        }
        finally { _filling = false; }
    }

    private static void Set(SpinBox s, double v)
    {
        if (!s.GetLineEdit().HasFocus())
            s.SetValueNoSignal(v);
    }

    /// <summary>An asset's name from the inventory when an item there holds it, else its id.</summary>
    private string NameOf(Guid asset) =>
        _inventory()?.AllItems().FirstOrDefault(i => i.AssetId == asset) is { } item ? item.Name : asset.ToString();

    private static string Clear(Label label)
    {
        label.Text = label.TooltipText = string.Empty;
        return string.Empty;
    }

    /// <summary>Shows an asset on a label by its name (a standard texture by its own name, else the first part of its id), with the whole
    /// id as the tooltip when the name is not an inventory name; the automation's text keeps what <see cref="NameOf"/> gives.</summary>
    private string Show(Label label, Guid asset)
    {
        string full = NameOf(asset);
        bool named = full != asset.ToString();
        label.Text = BuildNames.Texture(asset, named ? full : null, _s);
        label.TooltipText = named ? string.Empty : asset.ToString();
        return full;
    }

    /// <summary>The tab's texts for the automation: the shown face's values as the fields show them.</summary>
    public object Texts() => new
    {
        texture = _textureFull,
        material = _material.Text,
        transparency = _transparency.Value, glow = _glow.Value, fullbright = _fullbright.ButtonPressed,
        mapping = _mapping.Selected >= 0 ? _mapping.GetItemText(_mapping.Selected) : null,
        scale_u = _scaleU.Value, scale_v = _scaleV.Value, per_metre = _perMetre.Value, rotation = _rotation.Value,
        offset_u = _offsetU.Value, offset_v = _offsetV.Value,
    };
}

/// <summary>
/// The list of an inventory picker for one kind of item (textures or materials), the account's and the Library's, by name with a search.
/// A row chosen (clicked, or moved to with the keys) raises <see cref="Selected"/>; a row activated (double-clicked, or Enter) raises
/// <see cref="Activated"/>. Shown in the texture picker window and in the Features tab's popup.
/// </summary>
public partial class InventoryPickPanel : VBoxContainer
{
    private readonly StringTable _strings;
    private readonly ItemList _list;
    private readonly LineEdit _search;
    private readonly Label _empty;
    private Viewer.Shell.Build.InventoryPickList? _pick;
    private IReadOnlyList<InventoryItemData> _rows = [];
    private string _shown = "\0";

    public ItemList List => _list;

    /// <summary>A row was chosen: its asset.</summary>
    public event Action<Guid>? Selected;

    /// <summary>A row was activated: its asset.</summary>
    public event Action<Guid>? Activated;

    public InventoryPickPanel(StringTable strings)
    {
        _strings = strings;
        Name = "inventory_pick";
        SizeFlagsVertical = SizeFlags.ExpandFill;
        _search = new LineEdit { Name = "pick_search", PlaceholderText = strings.Get("appearance.texture.search") };
        _search.AccessibilityName = strings.Get("appearance.texture.search");
        AddChild(_search);
        _list = new ItemList { Name = "pick_list", CustomMinimumSize = new Vector2(320, 300), SizeFlagsVertical = SizeFlags.ExpandFill };
        _list.AccessibilityName = strings.Get("appearance.texture.list");
        _list.ItemSelected += i => { if (AssetAt((int)i) is { } a) Selected?.Invoke(a); };
        _list.ItemActivated += i => { if (AssetAt((int)i) is { } a) Activated?.Invoke(a); };
        AddChild(_list);
        // with none of the kind anywhere in the inventory, the list says so (materials)
        _empty = new Label { Name = "pick_empty", Visible = false };
        AddChild(_empty);
    }

    /// <summary>Shows the items of <paramref name="kind"/> from <paramref name="model"/>, from the top, with an empty search.</summary>
    public void Begin(InventoryModel model, ItemKind kind)
    {
        _pick = new Viewer.Shell.Build.InventoryPickList(model, kind);
        _empty.Text = _pick.EmptyKey is { } k ? _strings.Get(k) : "";
        _search.Clear();
        _shown = "\0";
        _rows = [];
        _list.Clear();
    }

    public override void _Process(double delta)
    {
        if (_pick is null || !IsVisibleInTree())
            return;
        // every folder not yet loaded is fetched: an item can sit in any folder, not only its kind's own
        bool ready = _pick.AllLoaded;
        if (!ready)
            _pick.Fetch();
        if (_shown != _search.Text || !ready || _rows.Count == 0)
            Fill();
        _empty.Visible = ready && _rows.Count == 0 && _search.Text.Trim().Length == 0 && _pick.EmptyKey is not null;
    }

    private void Fill()
    {
        if (_pick is null)
            return;
        // the chosen row stays chosen when the list is filled again (more folders arrived)
        var chosen = _list.GetSelectedItems() is { Length: > 0 } sel && sel[0] < _rows.Count ? _rows[sel[0]].Id : (Guid?)null;
        _rows = _pick.Rows(_search.Text);
        _shown = _search.Text;
        _list.Clear();
        for (int i = 0; i < _rows.Count; i++)
        {
            _list.AddItem(_rows[i].Name);
            if (_rows[i].Id == chosen)
                _list.Select(i);
        }
    }

    /// <summary>The asset of the row at <paramref name="index"/>, or null.</summary>
    public Guid? AssetAt(int index) => index >= 0 && index < _rows.Count ? _rows[index].AssetId : null;

    /// <summary>The asset of the chosen row, or null.</summary>
    public Guid? SelectedAsset => _list.GetSelectedItems() is { Length: > 0 } s ? AssetAt(s[0]) : null;

    /// <summary>The rows' names (for the automation).</summary>
    public IReadOnlyList<string> Names => _rows.Select(r => r.Name).ToList();
}

/// <summary>An inventory picker as a popup (the Features tab's projector texture): the list, and a button that uses the chosen row.</summary>
public partial class InventoryPickPopup : PopupPanel
{
    private readonly InventoryPickPanel _panel;
    private readonly Action<Guid> _use;

    public ItemList List => _panel.List;

    public InventoryPickPopup(StringTable strings, InventoryModel model, ItemKind kind, Action<Guid> use)
    {
        _use = use;
        Name = "inventory_pick_popup";
        var box = new VBoxContainer();
        _panel = new InventoryPickPanel(strings);
        _panel.Begin(model, kind);
        _panel.Activated += Use;
        box.AddChild(_panel);
        var use_ = new Button { Name = "pick_use", Text = strings.Get("appearance.texture.pick") };
        use_.AccessibilityName = use_.Text;
        use_.Pressed += () => { if (_panel.SelectedAsset is { } a) Use(a); };
        box.AddChild(use_);
        AddChild(box);
        ReverieRestyle.Apply(this);   // a popup is not a floater: it takes the Reverie look itself
    }

    private void Use(Guid asset)
    {
        _use(asset);
        Hide();
    }

    /// <summary>The rows' names (for the automation).</summary>
    public IReadOnlyList<string> Names => _panel.Names;
}

/// <summary>
/// The Build window's texture (or material) picker, and its link to the shared colour picker (<see cref="ColourPickerWindow"/>), each a window of its own with a title bar and a close button, as the
/// official LLFloaterTexturePicker and LLFloaterColorPicker are (floater_texture_ctrl.xml 1-13, floater_color_picker.xml 1-9): they stay
/// open while the person clicks elsewhere. A choice is applied as it is made (the official Apply now, on by default: TextureLivePreview
/// and ApplyColorImmediately, settings.xml 228-248); OK keeps it and closes (lltexturectrl.cpp 1060-1068, llfloatercolorpicker.cpp
/// 407-420); Cancel, the close button, or the Build window closing puts back each face's texture, material or colour as it was when the
/// picker opened (lltexturectrl.cpp 1047-1057, llfloatercolorpicker.cpp 391-403, llpanelface.cpp 3280-3283 and 2907-2910). Main thread only.
/// </summary>
public sealed class FacePickerWindows
{
    public const string ItemPickerId = "edit_item_picker";
    public const string ColourPickerId = ColourPickerWindow.FloaterId;

    private readonly StringTable _s;
    private readonly ShellRoot _shell;
    private readonly Func<EditController?> _edit;
    private readonly InventoryPickPanel _items;
    private readonly ColourPickerWindow _colours;
    private PickSession<Guid>? _itemSession;
    private ItemKind _itemKind;

    public InventoryPickPanel Items => _items;
    public ColourPickerWindow Colours => _colours;

    public FacePickerWindows(StringTable strings, ShellRoot shell, Func<EditController?> edit)
    {
        _s = strings;
        _shell = shell;
        _edit = edit;

        var itemBox = new VBoxContainer { Name = "item_picker" };
        _items = new InventoryPickPanel(strings);
        _items.Selected += a => _itemSession?.Choose(a);
        _items.Activated += a => { _itemSession?.Choose(a); EndItems(keep: true); };
        itemBox.AddChild(_items);
        itemBox.AddChild(Buttons("pick", () => EndItems(keep: true), () => EndItems(keep: false)));
        shell.AddFloater(new FloaterSpec(ItemPickerId, "edit.picker.texture.title", new UiSize(380, 480), new UiSize(320, 360)), itemBox);

        _colours = ColourPickerWindow.Install(shell);

        shell.Shell.Floaters.Closed += id =>
        {
            if (id == ItemPickerId)
                _itemSession?.Closed();
            else if (id == EditPanel.FloaterId)
            {
                // the pickers belong to the Build window: they close with it, putting back what was not kept
                _shell.Shell.Floaters.Close(ItemPickerId);
                foreach (var panel in _shell.FindChildren("edit_panel", "", true, false))
                    _colours.CloseIfFrom(panel);
            }
        };
    }

    private HBoxContainer Buttons(string prefix, Action ok, Action cancel)
    {
        var row = new HBoxContainer { Name = prefix + "_buttons", Alignment = BoxContainer.AlignmentMode.End };
        var okButton = new Button { Name = prefix + "_ok", Text = _s.Get("edit.picker.ok") };
        okButton.AccessibilityName = okButton.Text;
        okButton.Pressed += ok;
        var cancelButton = new Button { Name = prefix + "_cancel", Text = _s.Get("edit.picker.cancel") };
        cancelButton.AccessibilityName = cancelButton.Text;
        cancelButton.Pressed += cancel;
        row.AddChild(okButton);
        row.AddChild(cancelButton);
        return row;
    }

    /// <summary>True while the texture (or material) picker is open.</summary>
    public bool ItemsOpen => _shell.Shell.Floaters.IsOpen(ItemPickerId);

    /// <summary>True while the shared colour picker is open.</summary>
    public bool ColoursOpen => _colours.IsOpen;

    /// <summary>Opens the picker for textures or materials. Open already for the same kind, it comes to the front; for the other kind,
    /// the open one ends as a close does (what it applied is put back) and the new one opens.</summary>
    public void OpenItems(InventoryModel model, ItemKind kind)
    {
        if (_edit() is not { } e)
            return;
        if (ItemsOpen && _itemKind == kind && _itemSession is { Ended: false })
        {
            _shell.Shell.Floaters.Focus(ItemPickerId);
            return;
        }
        _shell.Shell.Floaters.Close(ItemPickerId);
        var saved = e.SaveFacePicks();
        _itemKind = kind;
        _itemSession = kind == ItemKind.Material
            ? new PickSession<Guid>(id => _edit()?.ApplyMaterial(id), () => _edit()?.RevertMaterials(saved))
            : new PickSession<Guid>(id => _edit()?.EditFaces(new FaceChange(TextureId: id)), () => _edit()?.RevertTextures(saved));
        _items.Begin(model, kind);
        _shell.SetFloaterTitle(ItemPickerId, _s.Get(kind == ItemKind.Material ? "edit.picker.material.title" : "edit.picker.texture.title"));
        _shell.Shell.Floaters.Open(ItemPickerId);
    }

    private void EndItems(bool keep)
    {
        if (keep) _itemSession?.Ok(); else _itemSession?.Cancel();
        _shell.Shell.Floaters.Close(ItemPickerId);
    }

}
