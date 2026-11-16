// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using Godot;
using Viewer.Core.Building;
using Viewer.Core.Inventory;
using Viewer.Shell;
using Viewer.Shell.Build;

namespace Viewer.Godot;

/// <summary>
/// The Build window's Features tab, laid out after the official floater_tools.xml Features panel (read as behaviour): Flexible Path with
/// Softness, Gravity, Drag, Wind, Tension and Force X, Y and Z; Light with its colour, its projector image, Intensity, FOV, Radius, Focus,
/// Falloff and Ambiance; the Physics Shape Type; the Material; and Gravity, Friction, Density and Restitution. Every change goes to the
/// edit controller. Standard controls styled through the Theme alone. Main thread only.
/// </summary>
public sealed class EditFeaturesTab
{
    private readonly StringTable _s;
    private readonly Func<EditController?> _edit;
    private readonly Func<InventoryModel?> _inventory;
    private readonly Control _owner;
    private readonly CheckBox _flexible, _light;
    private readonly SpinBox _softness, _flexGravity, _drag, _wind, _tension, _forceX, _forceY, _forceZ;
    private readonly SpinBox _intensity, _fov, _radius, _focus, _falloff, _ambiance;
    private readonly SpinBox _gravity, _friction, _density, _restitution;
    private readonly ColorPickerButton _colour;
    private readonly Label _projector;
    private readonly OptionButton _shape, _material;
    private readonly List<Control> _flexControls = new(), _lightControls = new(), _physicsControls = new();
    private Guid _projectorTexture;
    private InventoryPickPopup? _picker;
    private bool _filling;

    public EditFeaturesTab(StringTable strings, Func<EditController?> edit, Func<InventoryModel?> inventory, VBoxContainer page, Control owner)
    {
        _s = strings;
        _edit = edit;
        _inventory = inventory;
        _owner = owner;

        _flexible = Check(page, "flexible", "edit.feat.flexible", on => _edit()?.SetFlexible(on, on ? Flex() : null));
        _softness = Spin(page, _flexControls, "softness", "edit.feat.softness", 0, 3, 1, SendFlex);
        _flexGravity = Spin(page, _flexControls, "flex_gravity", "edit.feat.gravity", -10, 10, 0.5, SendFlex);
        _drag = Spin(page, _flexControls, "drag", "edit.feat.drag", 0, 10, 0.5, SendFlex);
        _wind = Spin(page, _flexControls, "wind", "edit.feat.wind", 0, 10, 0.5, SendFlex);
        _tension = Spin(page, _flexControls, "tension", "edit.feat.tension", 0, 10, 0.5, SendFlex);
        _forceX = Spin(page, _flexControls, "force_x", "edit.feat.force_x", -10, 10, 0.01, SendFlex);
        _forceY = Spin(page, _flexControls, "force_y", "edit.feat.force_y", -10, 10, 0.01, SendFlex);
        _forceZ = Spin(page, _flexControls, "force_z", "edit.feat.force_z", -10, 10, 0.01, SendFlex);

        page.AddChild(new HSeparator());
        _light = Check(page, "light", "edit.feat.light", on => _edit()?.SetLight(on, on ? Light() : null, on ? Projector() : null));
        var colourRow = new HBoxContainer { Name = "light_colour_row" };
        _colour = new ColorPickerButton { Name = "light_colour", EditAlpha = false, CustomMinimumSize = new Vector2(64, 24) };
        _colour.AccessibilityName = strings.Get("edit.feat.light_colour");
        _colour.ColorChanged += _ => SendLight();
        colourRow.AddChild(_colour);
        _lightControls.Add(_colour);
        _projector = new Label { Name = "light_texture", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipText = true };
        _projector.AccessibilityName = strings.Get("edit.feat.light_texture");
        colourRow.AddChild(_projector);
        var choose = Btn("light_texture_choose", "edit.feat.light_texture.choose", () => Pick(id => { _projectorTexture = id; SendLight(); }));
        var clear = Btn("light_texture_none", "edit.feat.light_texture.none", () => { _projectorTexture = Guid.Empty; SendLight(); });
        colourRow.AddChild(choose);
        colourRow.AddChild(clear);
        _lightControls.Add(choose);
        _lightControls.Add(clear);
        page.AddChild(colourRow);
        _intensity = Spin(page, _lightControls, "intensity", "edit.feat.intensity", 0, 1, 0.05, SendLight);
        _fov = Spin(page, _lightControls, "fov", "edit.feat.fov", 0, 3, 0.1, SendLight);
        _radius = Spin(page, _lightControls, "radius", "edit.feat.radius", 0, 20, 0.5, SendLight);
        _focus = Spin(page, _lightControls, "focus", "edit.feat.focus", -20, 20, 0.5, SendLight);
        _falloff = Spin(page, _lightControls, "falloff", "edit.feat.falloff", 0, 2, 0.25, SendLight);
        _ambiance = Spin(page, _lightControls, "ambiance", "edit.feat.ambiance", 0, 1, 0.05, SendLight);

        page.AddChild(new HSeparator());
        page.AddChild(new Label { Name = "shape_type_label", Text = strings.Get("edit.feat.shape_type") });
        _shape = new OptionButton { Name = "shape_type" };
        _shape.AccessibilityName = strings.Get("edit.feat.shape_type");
        _shape.AddItem(strings.Get("edit.feat.shape_type.prim"), FeatureRules.ShapePrim);
        _shape.AddItem(strings.Get("edit.feat.shape_type.none"), FeatureRules.ShapeNone);
        _shape.AddItem(strings.Get("edit.feat.shape_type.convex"), FeatureRules.ShapeConvex);
        _shape.ItemSelected += _ => SendPhysics();
        page.AddChild(_shape);
        _physicsControls.Add(_shape);
        page.AddChild(new Label { Name = "material_label", Text = strings.Get("edit.feat.material") });
        _material = new OptionButton { Name = "material" };
        _material.AccessibilityName = strings.Get("edit.feat.material");
        for (int i = 0; i < FeatureRules.Materials.Count; i++)
            _material.AddItem(strings.Get("edit.feat.material." + FeatureRules.Materials[i]), i);
        _material.ItemSelected += i => { if (!_filling) _edit()?.SetMaterial(_material.GetItemId((int)i)); };
        page.AddChild(_material);
        _physicsControls.Add(_material);
        _gravity = Spin(page, _physicsControls, "physics_gravity", "edit.feat.physics_gravity", -1, 28, 1, SendPhysics);
        _friction = Spin(page, _physicsControls, "friction", "edit.feat.friction", 0, 255, 0.1, SendPhysics);
        _density = Spin(page, _physicsControls, "density", "edit.feat.density", 1, 22587, 0.1, SendPhysics);
        _restitution = Spin(page, _physicsControls, "restitution", "edit.feat.restitution", 0, 1, 0.01, SendPhysics);
    }

    private Button Btn(string name, string key, Action pressed)
    {
        var b = new Button { Name = name, Text = _s.Get(key) };
        b.AccessibilityName = b.Text;
        b.Pressed += pressed;
        return b;
    }

    private CheckBox Check(Container page, string name, string key, Action<bool> changed)
    {
        var c = new CheckBox { Name = name, Text = _s.Get(key) };
        c.AccessibilityName = c.Text;
        c.Toggled += on => { if (!_filling) changed(on); };
        page.AddChild(c);
        return c;
    }

    private SpinBox Spin(Container page, List<Control> group, string name, string key, double min, double max, double step, Action changed)
    {
        var row = new HBoxContainer { Name = name + "_row" };
        row.AddChild(new Label { Name = name + "_label", Text = _s.Get(key), CustomMinimumSize = new Vector2(150, 0) });
        var s = new SpinBox
        {
            Name = name, MinValue = min, MaxValue = max, Step = step, AllowGreater = false, AllowLesser = false,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, AccessibilityName = _s.Get(key),
        };
        s.ValueChanged += _ => { if (!_filling) changed(); };
        row.AddChild(s);
        page.AddChild(row);
        group.Add(s);
        return s;
    }

    private FlexibleParams Flex() => new((int)_softness.Value, (float)_flexGravity.Value, (float)_drag.Value, (float)_wind.Value, (float)_tension.Value,
        new System.Numerics.Vector3((float)_forceX.Value, (float)_forceY.Value, (float)_forceZ.Value));

    private LightParams Light() => new(new System.Numerics.Vector3(_colour.Color.R, _colour.Color.G, _colour.Color.B), (float)_intensity.Value, (float)_radius.Value,
        (float)_falloff.Value);

    private ProjectorParams? Projector() => _projectorTexture == Guid.Empty ? null : new ProjectorParams(_projectorTexture, (float)_fov.Value, (float)_focus.Value, (float)_ambiance.Value);

    private void SendFlex()
    {
        if (!_filling && _flexible.ButtonPressed)
            _edit()?.SetFlexible(true, Flex());
    }

    private void SendLight()
    {
        if (!_filling && _light.ButtonPressed)
            _edit()?.SetLight(true, Light(), Projector());
    }

    private void SendPhysics()
    {
        if (_filling)
            return;
        _edit()?.SetPhysics(new PhysicsParams((byte)_shape.GetItemId(Math.Max(0, _shape.Selected)), (float)_gravity.Value, (float)_friction.Value, (float)_density.Value,
            (float)_restitution.Value));
    }

    /// <summary>Opens the inventory picker for the projector's texture (the account's and the Library's).</summary>
    private void Pick(Action<Guid> use)
    {
        if (_inventory() is not { } model)
            return;
        _picker?.QueueFree();
        _picker = new InventoryPickPopup(_s, model, ItemKind.Texture, use);
        _owner.AddChild(_picker);
        _picker.PopupCentered(new Vector2I(360, 420));
    }

    /// <summary>Shows the primary prim's features; the controls are off for a prim that may not be modified.</summary>
    public void Refresh()
    {
        var e = _edit();
        var o = e?.Object();
        var f = o is null ? null : e!.Features(o.Id.LocalId);
        bool modify = o is not null && e!.Access(o.Id.LocalId).CanModify;
        _filling = true;
        try
        {
            _flexible.SetPressedNoSignal(f?.Flexible is not null);
            _flexible.Disabled = !modify || f is not { CanFlex: true };
            var flex = f?.Flexible ?? FlexibleParams.Default;
            Set(_softness, flex.Softness);
            Set(_flexGravity, flex.Gravity);
            Set(_drag, flex.Drag);
            Set(_wind, flex.Wind);
            Set(_tension, flex.Tension);
            Set(_forceX, flex.Force.X);
            Set(_forceY, flex.Force.Y);
            Set(_forceZ, flex.Force.Z);
            foreach (var c in _flexControls)
                Enable(c, modify && f?.Flexible is not null);

            _light.SetPressedNoSignal(f?.Light is not null);
            _light.Disabled = !modify;
            var light = f?.Light ?? LightParams.Default;
            _colour.Color = ShellTheme.DataColour(light.Color.X, light.Color.Y, light.Color.Z);   // the light's own colour, not a theme colour
            Set(_intensity, light.Intensity);
            Set(_radius, light.Radius);
            Set(_falloff, light.Falloff);
            var projector = f?.Projector ?? ProjectorParams.Default;
            _projectorTexture = f?.Projector?.Texture ?? Guid.Empty;
            _projector.Text = _projectorTexture == Guid.Empty ? _s.Get("edit.feat.light_texture.no_image") : _projectorTexture.ToString();
            Set(_fov, projector.Fov);
            Set(_focus, projector.Focus);
            Set(_ambiance, projector.Ambiance);
            foreach (var c in _lightControls)
                Enable(c, modify && f?.Light is not null);

            var physics = f?.Physics ?? PhysicsParams.Default;
            int at = _shape.GetItemIndex(physics.ShapeType);
            _shape.Select(at >= 0 ? at : 0);
            int mat = _material.GetItemIndex(f?.Material ?? DefaultBox.MaterialWood);
            _material.Select(mat >= 0 ? mat : 0);
            Set(_gravity, physics.Gravity);
            Set(_friction, physics.Friction);
            Set(_density, physics.Density);
            Set(_restitution, physics.Restitution);
            foreach (var c in _physicsControls)
                Enable(c, modify);
        }
        finally { _filling = false; }
    }

    private static void Set(SpinBox s, double v)
    {
        if (!s.GetLineEdit().HasFocus())
            s.SetValueNoSignal(v);
    }

    private static void Enable(Control c, bool on)
    {
        switch (c)
        {
            case SpinBox s: s.Editable = on; break;
            case BaseButton b: b.Disabled = !on; break;
        }
    }

    /// <summary>The tab's texts for the automation.</summary>
    public object Texts() => new
    {
        flexible = _flexible.ButtonPressed, light = _light.ButtonPressed,
        shape_type = _shape.Selected >= 0 ? _shape.GetItemText(_shape.Selected) : null,
        material = _material.Selected >= 0 ? _material.GetItemText(_material.Selected) : null,
        softness = _softness.Value, radius = _radius.Value, density = _density.Value, friction = _friction.Value,
    };
}
