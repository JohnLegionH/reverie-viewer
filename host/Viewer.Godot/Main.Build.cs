// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Linq;
using Godot;
using Viewer.Core.Session;
using Viewer.Shell;

namespace Viewer.Godot;

/// <summary>The Build menu and its tools in the world: Create, Upload Image and Bulk Upload, rezzing an inventory object dropped on the
/// world, and the object sounds of the interaction controller.</summary>
public partial class Main
{
    private BuildFeature.Panels? _build;
    private ModelFeature.Panels? _models;

    /// <summary>Connects a new session's controller to the build tools; the menu, windows and choosers are made once and outlive a session.</summary>
    private void AttachBuild(ViewerSession s, InteractionController ic)
    {
        ic.PlaySound = slot => _uiSounds?.Play(slot);
        AttachEdit(s, ic);
        if (_build is not null || _shell is null)
            return;
        var shell = _shell.Shell;
        bool InWorld() => _interactFor is { IsEnded: false } && _interact is not null;
        _build = BuildFeature.Install(_shell, () => _interactFor?.BuildService,
            () => _interactFor?.Inventory.FolderOfKind(Viewer.Core.Inventory.FolderKind.Texture) ?? Guid.Empty);
        _build.Drop.Accepts = id => _interactFor?.Inventory.Item(id) is { Kind: Viewer.Core.Inventory.ItemKind.Object };
        _build.Drop.Dropped += (id, at) =>
        {
            if (_interact is { } c && _camera is not null)
                c.RezDrop(id, PickAt(at), Region(_camera.GlobalPosition));
        };
        // after an upload a notice says what was uploaded and where, with Show in inventory (which opens the inventory on the item)
        _build.Image.Finished += () => PostUploadNotice(_build.Image.Uploaded, Viewer.Shell.Notifications.UploadFolder.Textures);
        _build.Bulk.Finished += () => PostUploadNotice(_build.Bulk.Uploaded, Viewer.Shell.Notifications.UploadFolder.Textures);
        shell.Commands.Register(BuildMenu.ImageCommand, () => _build.ImageDialog.PopupCentered());
        shell.Commands.Register(BuildMenu.BulkCommand, () => _build.BulkDialog.PopupCentered());
        _models = ModelFeature.Install(_shell, () => _interactFor?.ModelService,
            () => _interactFor?.Inventory.FolderOfKind(Viewer.Core.Inventory.FolderKind.Object) ?? Guid.Empty,
            () => _interactFor?.Inventory.FolderOfKind(Viewer.Core.Inventory.FolderKind.Texture) ?? Guid.Empty);
        _models.Model.Finished += () => PostUploadNotice(_models.Model.Uploaded, Viewer.Shell.Notifications.UploadFolder.Objects);
        _models.Bulk.Finished += () => PostUploadNotice(_models.Bulk.Uploaded, Viewer.Shell.Notifications.UploadFolder.Objects);
        shell.Commands.Register(BuildMenu.ModelCommand, () => _models.ModelDialog.PopupCentered());
        shell.Commands.Register(BuildMenu.BulkModelsCommand, () => _models.BulkDialog.PopupCentered());
        BuildMenu.Install(shell.Menus, InWorld);
    }

    private Viewer.Shell.Notifications.Notification? _lastUploadNotice;

    /// <summary>Posts the notice after an upload (nothing when no item was made).</summary>
    private void PostUploadNotice(System.Collections.Generic.IReadOnlyList<(string Name, Guid ItemId)> uploaded, Viewer.Shell.Notifications.UploadFolder folder)
    {
        if (Viewer.Shell.Notifications.UploadNotice.For(uploaded, folder, ShowInInventory) is not { } notice)
            return;
        _lastUploadNotice = notice;
        _notes?.Model.Post(notice);
    }

    /// <summary>Opens the inventory with an item selected and scrolled into view.</summary>
    private void ShowInInventory(Guid itemId)
    {
        if (_shell is null || _inventoryPanel is null)
            return;
        _shell.Shell.Floaters.Open(InventoryFeature.FloaterId);
        _inventoryPanel.Reveal(itemId);
    }

    /// <summary>The automation's upload-notice command: state (the last notice's text and buttons, the inventory's selection), or show (presses
    /// its Show in inventory button as a click would).</summary>
    public object UploadNoticeCall(string action)
    {
        if (action == "show" && _lastUploadNotice?.Responder is { } answer)
            answer(new Viewer.Shell.Notifications.NotificationAnswer(Viewer.Shell.Notifications.UploadNotice.ShowButton, ""));
        var n = _lastUploadNotice;
        return new
        {
            posted = n is not null,
            body = n is null ? null : n.Body.Key,
            buttons = n?.Buttons.Count ?? 0,
            inventory_open = _shell?.Shell.Floaters.IsOpen(InventoryFeature.FloaterId) ?? false,
            selected = _inventoryPanel?.SelectedItem is { } sel && sel != Guid.Empty ? sel.ToString() : null,
        };
    }

    /// <summary>The automation's build command. state: the tools' state, the own objects in the region (ids and places) and the inventory items
    /// of the Textures and Objects folders (ids; a name only as a count matching <paramref name="expect"/>); create: Build, Create, then a click at
    /// the point; rez: the inventory object <paramref name="id"/> dropped on the world at the point; upload_image and upload_bulk: the files in
    /// <paramref name="text"/> (separated by '|') opened in the window; press_upload: its Upload button (text: image or bulk).</summary>
    public object? BuildCall(string action, Guid id, float x, float y, float z, string? text, string? expect)
    {
        if (_interact is not { } ic || _interactFor is not { } s || _camera is null || _build is not { } build || _shell is null)
            return null;
        Vector2? Screen(System.Numerics.Vector3 p)
        {
            var engine = new Vector3(p.X, p.Z, -p.Y);
            return _camera.IsPositionBehind(engine) ? null : _camera.UnprojectPosition(engine);
        }
        var point = new System.Numerics.Vector3(x, y, z);
        switch (action)
        {
            case "create":
            {
                bool chosen = _shell.Shell.Menus.Activate(BuildMenu.CreateId, _shell.Shell.Commands);
                if (Screen(point) is not { } px)
                {
                    ic.CancelCreate();
                    return new { chosen, placed = false, error = "not_in_view" };
                }
                var pick = PickAt(px);
                // the shape button to press (named in expect) and, with Copy selection on, the copy of the selection instead of a shape
                if (Enum.TryParse<Viewer.Core.Building.CreateShape>(expect, true, out var button))
                    _edit?.ChooseShape(button);
                bool placed;
                if (_edit is { CopySelection: true } copying)
                {
                    placed = copying.PlaceCopy(pick, Region(_camera.GlobalPosition), Region(-_camera.GlobalBasis.Z), Region(_camera.ProjectRayNormal(px)), _camera.Near);
                    if (placed) ic.CancelCreate();
                }
                else
                    placed = ic.Place(pick, Region(_camera.GlobalPosition), Region(-_camera.GlobalBasis.Z), Region(_camera.ProjectRayNormal(px)), _camera.Near,
                        _edit?.ShapeChoice ?? Viewer.Core.Building.CreateShape.Cube);
                if (!placed) ic.CancelCreate();
                else HandBackAfterCreate(ic);
                return new { chosen, placed, pick = pick.Kind.ToString(), tool = _edit?.Tool.ToString(), sounds = RecentObjectSounds() };
            }
            case "rez":
            {
                if (Screen(point) is not { } px)
                    return new { rezzed = false, error = "not_in_view" };
                bool accepted = build.Drop.Accepts?.Invoke(id) ?? false;
                bool rezzed = accepted && ic.RezDrop(id, PickAt(px), Region(_camera.GlobalPosition));
                return new { accepted, rezzed, sounds = RecentObjectSounds() };
            }
            case "upload_image" or "upload_bulk":
            {
                var paths = (text ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries);
                var panel = action == "upload_image" ? build.Image : build.Bulk;
                panel.Open(paths);
                _shell.Shell.Floaters.Open(action == "upload_image" ? BuildFeature.ImageFloaterId : BuildFeature.BulkFloaterId);
                return new { texts = panel.Texts() };
            }
            case "upload_model" or "upload_bulk_models" when _models is { } models:
            {
                // expect: the settings, comma-separated: textures, lowest (physics), noauto (no generated LODs), scale=<n>
                var paths = (text ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries);
                var opts = (expect ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                float scale = opts.FirstOrDefault(o => o.StartsWith("scale=", StringComparison.Ordinal)) is { } sc
                    && float.TryParse(sc[6..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 1f;
                var panel = action == "upload_model" ? models.Model : models.Bulk;
                panel.Open(paths);
                panel.Choose(opts.Contains("textures"), opts.Contains("lowest"), !opts.Contains("noauto"), scale);
                _shell.Shell.Floaters.Open(action == "upload_model" ? ModelFeature.ModelFloaterId : ModelFeature.BulkModelsFloaterId);
                return new { texts = panel.Texts() };
            }
            case "press_model_upload" when _models is { } models:
            {
                var panel = text == "bulk" ? models.Bulk : models.Model;
                bool enabled = !panel.Upload.Disabled;
                if (enabled) panel.Upload.EmitSignal(BaseButton.SignalName.Pressed);
                return new { pressed = enabled, texts = panel.Texts() };
            }
            case "take_back":
            {
                // text: the local ids to take back, comma-separated; each own root is taken once, with no pick
                var ids = (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(t => uint.TryParse(t, out var v) ? v : 0u).Where(v => v != 0).ToArray();
                return new { taken = ic.TakeBack(ids).ToArray() };
            }
            case "press_upload":
            {
                var panel = text == "bulk" ? build.Bulk : build.Image;
                bool enabled = !panel.Upload.Disabled;
                if (enabled) panel.Upload.EmitSignal(BaseButton.SignalName.Pressed);
                return new { pressed = enabled, texts = panel.Texts() };
            }
        }
        var inv = s.Inventory;
        Guid textures = inv.FolderOfKind(Viewer.Core.Inventory.FolderKind.Texture), objects = inv.FolderOfKind(Viewer.Core.Inventory.FolderKind.Object);
        var own = s.World.Snapshot().Objects.Values
            .Where(o => o.ParentLocalId == 0 && o.AttachmentPoint is null && ((o.Interaction?.Flags ?? 0) & Viewer.Core.Interaction.ObjectFlagBits.YouOwner) != 0)
            .Select(o => new
            {
                id = o.FullId, local = o.Id.LocalId, x = o.Position.X, y = o.Position.Y, z = o.Position.Z, sx = o.Scale.X, sy = o.Scale.Y, sz = o.Scale.Z,
                click = o.Interaction?.ClickAction ?? 0, plant = o.Shape is null,
                profile = o.Shape?.ProfileCurve, path = o.Shape?.PathCurve, path_scale = o.Shape is { } sh ? new[] { sh.PathScaleX, sh.PathScaleY } : null,
                path_end = o.Shape?.PathEnd, profile_cut = o.Shape is { } pc ? new[] { pc.ProfileBegin, pc.ProfileEnd } : null,
                shear_x = o.Shape?.PathShearX, rotation = new[] { o.Rotation.X, o.Rotation.Y, o.Rotation.Z, o.Rotation.W },
            })
            .ToArray();
        // every own prim on the ground, children included, with each face's texture (the first eight faces; empty where none is sent)
        var snapshot = s.World.Snapshot().Objects.Values;
        var ownRoots = snapshot.Where(o => o.ParentLocalId == 0 && o.AttachmentPoint is null && ((o.Interaction?.Flags ?? 0) & Viewer.Core.Interaction.ObjectFlagBits.YouOwner) != 0)
            .Select(o => o.Id.LocalId).ToHashSet();
        var ownPrims = snapshot.Where(o => ownRoots.Contains(o.Id.LocalId) || ownRoots.Contains(o.ParentLocalId))
            .Select(o => new
            {
                id = o.FullId, local = o.Id.LocalId, parent = o.ParentLocalId, mesh = o.Shape?.SculptType.ToString(),
                faces = Enumerable.Range(0, 8).Select(f => o.Textures is { } t ? t[f].TextureId : Guid.Empty).ToArray(),
                // each face's colour as the region last stated it (white where none is sent), for checks that a picker put each back
                colours = Enumerable.Range(0, 8).Select(f => Viewer.Core.Building.FaceRules.Settings(o, f).Colour is var c ? new[] { c.R, c.G, c.B } : null).ToArray(),
            }).ToArray();
        var textureItems = inv.ChildItems(textures);
        var objectItems = inv.ChildItems(objects);
        return new
        {
            placing = ic.Placing,
            own_objects = own,
            world_plants = snapshot.Count(o => o.AttachmentPoint is null),
            own_prims = ownPrims,
            model = _models?.Model.Texts(), model_states = _models?.Model.States.Select(st => st.Error is null ? st.State.ToString() : $"{st.State}:{st.Error}").ToArray(),
            bulk_models = _models?.Bulk.Texts(), bulk_model_states = _models?.Bulk.States.Select(st => st.Error is null ? st.State.ToString() : $"{st.State}:{st.Error}").ToArray(),
            texture_items = textureItems.Select(i => i.Id).ToArray(),
            texture_named = expect is null ? 0 : textureItems.Count(i => i.Name == expect),
            object_items = objectItems.Where(i => i.Kind == Viewer.Core.Inventory.ItemKind.Object).Select(i => i.Id).ToArray(),
            object_named = expect is null ? 0 : objectItems.Count(i => i.Name == expect),
            image = build.Image.Texts(), image_states = build.Image.States.Select(st => st.Error is null ? st.State.ToString() : $"{st.State}:{st.Error}").ToArray(),
            bulk = build.Bulk.Texts(), bulk_states = build.Bulk.States.Select(st => st.Error is null ? st.State.ToString() : $"{st.State}:{st.Error}").ToArray(),
            sounds = RecentObjectSounds(),
        };
    }

    /// <summary>The object sounds the interface played lately, oldest first (from the sound player's recent list).</summary>
    private string[] RecentObjectSounds() =>
        _uiSounds?.Recent.Where(e => e.Slot is Viewer.Shell.Sound.UiSoundSlot.ObjectCreate or Viewer.Shell.Sound.UiSoundSlot.ObjectRezIn or Viewer.Shell.Sound.UiSoundSlot.ObjectRezOut or Viewer.Shell.Sound.UiSoundSlot.ObjectDelete)
            .Select(e => $"{e.Slot}:{e.Outcome}").ToArray() ?? [];

    /// <summary>An engine position in region coordinates (x east, y north, z up).</summary>
    private static System.Numerics.Vector3 Region(Vector3 v) => new(v.X, -v.Z, v.Y);

    /// <summary>The create tool's click: the box goes where the click points, the ray starting just in front of the camera.</summary>
    private void PlaceBox(InteractionController ic, Vector2 screen)
    {
        if (_camera is null)
            return;
        var at = -_camera.GlobalBasis.Z;
        if (_edit is { CopySelection: true } copy)
        {
            // Copy selection: the selection is copied to the click instead of a new shape being made
            if (copy.PlaceCopy(PickAt(screen), Region(_camera.GlobalPosition), Region(at), Region(_camera.ProjectRayNormal(screen)), _camera.Near))
            {
                ic.CancelCreate();
                HandBackAfterCreate(ic);
            }
            else
                _uiSounds?.Play(Viewer.Shell.Sound.UiSoundSlot.InvalidOp);
            return;
        }
        if (ic.Place(PickAt(screen), Region(_camera.GlobalPosition), Region(at), Region(_camera.ProjectRayNormal(screen)), _camera.Near, _edit?.ShapeChoice ?? Viewer.Core.Building.CreateShape.Cube))
            HandBackAfterCreate(ic);
    }

    /// <summary>A click on an object set to Zoom: the camera moves to frame its box and the focus goes to its centre (lltoolpie.cpp).</summary>
    private void ZoomOnObject(Viewer.Core.World.ObjectState o)
    {
        if (_camera is null || _presented is not { } s || !Viewer.Core.Interaction.ObjectPicker.WorldPose(s.World, o, out var centre, out _))
            return;
        var cameraAt = Region(_camera.GlobalPosition);
        var toCamera = cameraAt - centre;
        toCamera = toCamera.LengthSquared() > 1e-6f ? System.Numerics.Vector3.Normalize(toCamera) : System.Numerics.Vector3.UnitX;
        float aspect = GetViewport().GetVisibleRect().Size.X / System.Math.Max(1f, GetViewport().GetVisibleRect().Size.Y);
        float distance = Viewer.Core.Interaction.ClickChoices.ZoomDistance(o.Scale, Mathf.DegToRad(_camera.Fov), aspect);
        var world = s.World;
        _follow.SetCameraPosAndFocus(centre + toCamera * distance, centre, () => Viewer.Core.Interaction.ObjectPicker.WorldPose(world, o, out var p, out _) ? p : centre, o.Scale);
    }

    /// <summary>After a placement: the tool hands back to Edit's Move, or with Keep Tool selected the create tool waits for the next click
    /// (lltoolplacer.cpp, CreateToolKeepSelected).</summary>
    private void HandBackAfterCreate(InteractionController ic)
    {
        if (_edit is not { BuildMode: true } e)
            return;
        if (e.Placed())
            ic.BeginCreate();
        _editPanel?.Refresh();
    }
}
