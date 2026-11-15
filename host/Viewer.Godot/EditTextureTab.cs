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
    private readonly ColorPickerButton _colour;
    private readonly SpinBox _transparency, _glow, _scaleU, _scaleV, _perMetre, _rotation, _offsetU, _offsetV;
    private readonly SpinBox _pbrScaleU, _pbrScaleV, _pbrOffsetU, _pbrOffsetV, _pbrRotation;
    private readonly CheckBox _fullbright;
    private readonly OptionButton _mapping;
    private readonly Button _paste;
    private InventoryPickPopup? _picker;
    private bool _filling;

    public EditTextureTab(StringTable strings, Func<EditController?> edit, Func<InventoryModel?> inventory, VBoxContainer page, Control owner)
    {
        _s = strings;
        _edit = edit;
        _inventory = inventory;
        _owner = owner;

        page.AddChild(new Label { Name = "texture_label", Text = strings.Get("edit.tex.texture") });
        var texRow = new HBoxContainer { Name = "texture_row" };
        _texture = new Label { Name = "texture_name", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipText = true };
        _texture.AccessibilityName = strings.Get("edit.tex.texture");
        texRow.AddChild(_texture);
        var choose = Btn("texture_choose", "edit.tex.choose", () => Pick(ItemKind.Texture, id => _edit()?.EditFaces(new FaceChange(TextureId: id))));
        texRow.AddChild(choose);
        page.AddChild(texRow);

        page.AddChild(new Label { Name = "colour_label", Text = strings.Get("edit.tex.colour") });
        _colour = new ColorPickerButton { Name = "colour", EditAlpha = false, CustomMinimumSize = new Vector2(64, 24) };
        _colour.AccessibilityName = strings.Get("edit.tex.colour");
        _colour.ColorChanged += c => { if (!_filling) _edit()?.EditFaces(new FaceChange(Colour: new System.Numerics.Vector3(c.R, c.G, c.B))); };
        page.AddChild(_colour);
        _transparency = Spin(page, "transparency", "edit.tex.transparency", 0, 100, 1, v => new FaceChange(Transparency: (float)v));
        _glow = Spin(page, "glow", "edit.tex.glow", 0, 1, 0.01, v => new FaceChange(Glow: (float)v));
        _fullbright = new CheckBox { Name = "fullbright", Text = strings.Get("edit.tex.fullbright") };
        _fullbright.AccessibilityName = _fullbright.Text;
        _fullbright.Toggled += on => { if (!_filling) _edit()?.EditFaces(new FaceChange(Fullbright: on)); };
        page.AddChild(_fullbright);

        page.AddChild(new Label { Name = "mapping_label", Text = strings.Get("edit.tex.mapping") });
        _mapping = new OptionButton { Name = "mapping" };
        _mapping.AccessibilityName = strings.Get("edit.tex.mapping");
        _mapping.AddItem(strings.Get("edit.tex.mapping.default"), (int)TexGen.Default);
        _mapping.AddItem(strings.Get("edit.tex.mapping.planar"), (int)TexGen.Planar);
        _mapping.ItemSelected += i => { if (!_filling) _edit()?.EditFaces(new FaceChange(TexGen: (TexGen)_mapping.GetItemId((int)i))); };
        page.AddChild(_mapping);
        _scaleU = Spin(page, "scale_u", "edit.tex.scale_u", -100, 100, 0.01, v => new FaceChange(RepeatU: (float)v));
        _scaleV = Spin(page, "scale_v", "edit.tex.scale_v", -100, 100, 0.01, v => new FaceChange(RepeatV: (float)v));
        _perMetre = Spin(page, "per_metre", "edit.tex.per_metre", 0, 100, 0.01, null);
        _perMetre.ValueChanged += v => { if (!_filling) _edit()?.SetRepeatsPerMetre((float)v); };
        _rotation = Spin(page, "rotation", "edit.tex.rotation", -360, 360, 0.1, v => new FaceChange(RotationDegrees: (float)v));
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
        page.AddChild(new Label { Name = "material_label", Text = strings.Get("edit.tex.material") });
        var matRow = new HBoxContainer { Name = "material_row" };
        _material = new Label { Name = "material_name", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipText = true };
        _material.AccessibilityName = strings.Get("edit.tex.material");
        matRow.AddChild(_material);
        matRow.AddChild(Btn("material_choose", "edit.tex.material.choose", () => Pick(ItemKind.Material, id => _edit()?.ApplyMaterial(id))));
        matRow.AddChild(Btn("material_remove", "edit.tex.material.remove", () => _edit()?.RemoveMaterial()));
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
        page.AddChild(new Label { Name = name + "_label", Text = _s.Get(key) });
        var s = new SpinBox
        {
            Name = name, MinValue = min, MaxValue = max, Step = step, AllowGreater = false, AllowLesser = false,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, AccessibilityName = _s.Get(key),
        };
        if (change is not null)
            s.ValueChanged += v => { if (!_filling) _edit()?.EditFaces(change(v)); };
        page.AddChild(s);
        return s;
    }

    private void SendPbrTransform()
    {
        if (_filling)
            return;
        _edit()?.SetMaterialTransform(new TextureTransform((float)_pbrOffsetU.Value, (float)_pbrOffsetV.Value, (float)_pbrScaleU.Value, (float)_pbrScaleV.Value,
            (float)(_pbrRotation.Value * Math.PI / 180.0)));
    }

    /// <summary>Opens the inventory picker for textures or materials (the account's and the Library's); the chosen item's asset is used.</summary>
    private void Pick(ItemKind kind, Action<Guid> use)
    {
        if (_inventory() is not { } model)
            return;
        _picker?.QueueFree();
        _picker = new InventoryPickPopup(_s, model, kind, use);
        _owner.AddChild(_picker);
        _picker.PopupCentered(new Vector2I(360, 420));
    }

    /// <summary>The picker, for the automation and the live check: open for textures or materials.</summary>
    public InventoryPickPopup? Picker => _picker is { } p && GodotObject.IsInstanceValid(p) ? p : null;

    public void OpenPicker(ItemKind kind) => Pick(kind, kind == ItemKind.Material ? id => _edit()?.ApplyMaterial(id) : id => _edit()?.EditFaces(new FaceChange(TextureId: id)));

    /// <summary>Shows the face the controller shows; a field being typed in keeps its text.</summary>
    public void Refresh()
    {
        var e = _edit();
        _filling = true;
        try
        {
            var f = e?.FaceShown();
            bool editable = f is not null && e?.Primary is { } p && e.Access(p).CanReshape;
            _texture.Text = f is { } face ? NameOf(face.TextureId) : "";
            if (f is { } shown)
            {
                _colour.Color = new Color(shown.Colour.R, shown.Colour.G, shown.Colour.B);
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
            _material.Text = material == Guid.Empty ? _s.Get("edit.tex.material.none") : NameOf(material);
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
    private string NameOf(Guid asset)
    {
        if (_inventory()?.AllItems().FirstOrDefault(i => i.AssetId == asset) is { } item)
            return item.Name;
        return asset.ToString();
    }

    /// <summary>The tab's texts for the automation: the shown face's values as the fields show them.</summary>
    public object Texts() => new
    {
        texture = _texture.Text,
        material = _material.Text,
        transparency = _transparency.Value, glow = _glow.Value, fullbright = _fullbright.ButtonPressed,
        mapping = _mapping.Selected >= 0 ? _mapping.GetItemText(_mapping.Selected) : null,
        scale_u = _scaleU.Value, scale_v = _scaleV.Value, per_metre = _perMetre.Value, rotation = _rotation.Value,
        offset_u = _offsetU.Value, offset_v = _offsetV.Value,
    };
}

/// <summary>An inventory picker for one kind of item (textures or materials), the account's and the Library's, by name with a search.</summary>
public partial class InventoryPickPopup : PopupPanel
{
    private readonly InventoryModel _model;
    private readonly ItemKind _kind;
    private readonly Action<Guid> _use;
    private readonly ItemList _list;
    private readonly LineEdit _search;
    private List<InventoryItemData> _rows = new();
    private string _shown = "\0";

    public ItemList List => _list;

    public InventoryPickPopup(StringTable strings, InventoryModel model, ItemKind kind, Action<Guid> use)
    {
        _model = model;
        _kind = kind;
        _use = use;
        Name = "inventory_pick";
        var box = new VBoxContainer();
        _search = new LineEdit { Name = "pick_search", PlaceholderText = strings.Get("appearance.texture.search") };
        _search.AccessibilityName = strings.Get("appearance.texture.search");
        box.AddChild(_search);
        _list = new ItemList { Name = "pick_list", CustomMinimumSize = new Vector2(320, 300) };
        _list.AccessibilityName = strings.Get("appearance.texture.list");
        _list.ItemActivated += i => Use((int)i);
        box.AddChild(_list);
        var use_ = new Button { Name = "pick_use", Text = strings.Get("appearance.texture.pick") };
        use_.AccessibilityName = use_.Text;
        use_.Pressed += () => { var s = _list.GetSelectedItems(); if (s.Length > 0) Use(s[0]); };
        box.AddChild(use_);
        AddChild(box);
    }

    public override void _Process(double delta)
    {
        if (!Visible)
            return;
        bool ready = true;
        foreach (var f in _model.AllFolders())
        {
            bool holds = _kind == ItemKind.Texture ? f.Kind == FolderKind.Texture : f.Kind == FolderKind.Material;
            if (!holds || _model.LoadState(f.Id) == FolderLoad.Loaded)
                continue;
            ready = false;
            if (!_model.EnsureLoaded(f.Id))
                _model.Retry(f.Id);
        }
        if (_shown != _search.Text || !ready || _rows.Count == 0)
            Fill();
    }

    private void Fill()
    {
        string needle = _search.Text.Trim();
        var trash = _model.FolderOfKind(FolderKind.Trash);
        _rows = _model.AllItems()
            .Where(i => i.Kind == _kind && i.AssetId is { } a && a != Guid.Empty && (needle.Length == 0 || i.Name.Contains(needle, StringComparison.OrdinalIgnoreCase))
                && !(trash != Guid.Empty && _model.IsDescendantOf(i.Id, trash)))
            .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).Take(500).ToList();
        _shown = _search.Text;
        _list.Clear();
        foreach (var r in _rows)
            _list.AddItem(r.Name);
    }

    /// <summary>Uses the row at <paramref name="index"/>.</summary>
    public bool Use(int index)
    {
        if (index < 0 || index >= _rows.Count || _rows[index].AssetId is not { } asset)
            return false;
        _use(asset);
        Hide();
        return true;
    }

    /// <summary>The rows' names (for the automation).</summary>
    public IReadOnlyList<string> Names => _rows.Select(r => r.Name).ToList();
}
