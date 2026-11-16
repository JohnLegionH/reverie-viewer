// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using LibreMetaverse;
using Viewer.Core.Actions;
using Viewer.Core.Building;
using Viewer.Core.Interaction;
using Viewer.Core.Protocol;
using Viewer.Core.Session;
using Viewer.Core.World;
using Viewer.Shell;
using Viewer.Shell.Build;
using Xunit;
using Quaternion = System.Numerics.Quaternion;
using Vector3 = System.Numerics.Vector3;

namespace Viewer.Core.Tests;

/// <summary>
/// Preferences, Building: the settings for what Create makes, their defaults (the box Create always made, and Firestorm's), their ranges,
/// the size and material in the create message, and the edits put on the new box when it arrives, only for what differs from the region's
/// own new prim. In-memory settings and world; each test builds its own, so the class runs in parallel.
/// </summary>
public class NewPrimDefaultsTests
{
    private static Guid G(uint n) => new((int)n, 0, 0, new byte[8]);
    private const uint Full = ObjectFlagBits.YouOwner | ObjectFlagBits.Modify | ObjectFlagBits.Move | ObjectFlagBits.Copy | ObjectFlagBits.Transfer;
    private static readonly SimRef Sim = new(256000UL << 32 | 256000UL, "Here");

    private sealed class MemoryStore(string? text = null) : ISettingsStore
    {
        public string? Text = text;
        public string? Read() => Text;
        public bool Write(string text) { Text = text; return true; }
    }

    private static NewPrimDefaults Changed() => NewPrimDefaults.Default with
    {
        Size = new Vector3(1, 2, 3), Material = 1, Color = new Vector3(1, 0, 0), Transparency = 50f, Glow = 0.2f, FullBright = true, Texture = G(77),
        Physical = true, Phantom = true, NextOwnerCopy = true, NextOwnerModify = true, NextOwnerTransfer = false,
    };

    [Fact]
    public void The_defaults_are_the_box_Create_always_made_and_send_nothing_more()
    {
        var d = NewPrimDefaults.Default;
        Assert.Equal(new Vector3(0.5f), d.Size);
        Assert.Equal(DefaultBox.MaterialWood, d.Material);
        Assert.Equal(FaceRules.DefaultTexture, d.Texture);
        Assert.Equal((false, false, false, false, false, true), (d.Physical, d.Temporary, d.Phantom, d.NextOwnerCopy, d.NextOwnerModify, d.NextOwnerTransfer));
        Assert.Empty(d.EditsFor(5, NewPrimTracker.BoxFaces));
        Assert.Equal(d, NewPrimSettings.Read(new SettingsService(new MemoryStore())));
    }

    [Fact]
    public void Changed_settings_put_the_texture_colour_transparency_glow_and_full_bright_on_every_face_and_send_the_flags_and_next_owner_permissions()
    {
        var edits = Changed().EditsFor(5, NewPrimTracker.BoxFaces);
        var faces = Assert.Single(edits.OfType<SetObjectFacesIntent>());
        Assert.Equal(5u, faces.LocalId);
        Assert.Equal(Enumerable.Range(0, 6), faces.Faces.Select(f => f.Face));
        Assert.All(faces.Faces, f =>
        {
            Assert.Equal(G(77), f.Settings.TextureId);
            Assert.Equal(new Rgba(1, 0, 0, 0.5f), f.Settings.Colour);
            Assert.Equal((1f, 1f, 0f, 0f, 0f), (f.Settings.RepeatU, f.Settings.RepeatV, f.Settings.OffsetU, f.Settings.OffsetV, f.Settings.Rotation));
            Assert.True(f.Settings.Fullbright);
            Assert.Equal(0.2f, f.Settings.Glow);
        });
        Assert.Equal(new SetObjectFlagsIntent(5, true, false, true), Assert.Single(edits.OfType<SetObjectFlagsIntent>()));
        var perms = edits.OfType<SetObjectPermissionsIntent>().ToList();
        Assert.All(perms, p => Assert.Equal((LmvEdit.FieldNextOwner, 5u), (p.Field, Assert.Single(p.LocalIds))));
        Assert.Equal([(LmvBuild.PermCopy, true), (LmvBuild.PermModify, true), (LmvBuild.PermTransfer, false)], perms.Select(p => (p.Mask, p.Set)));
    }

    [Fact]
    public void Only_the_kinds_of_setting_that_changed_are_sent()
    {
        var onlyFlags = NewPrimDefaults.Default with { Temporary = true };
        Assert.Equal([new SetObjectFlagsIntent(5, false, true, false)], onlyFlags.EditsFor(5, 6));
        var onlyPerms = NewPrimDefaults.Default with { NextOwnerModify = true };
        Assert.Equal([LmvBuild.PermModify], onlyPerms.EditsFor(5, 6).OfType<SetObjectPermissionsIntent>().Select(p => p.Mask));
        Assert.Single(onlyPerms.EditsFor(5, 6));
        var onlySize = NewPrimDefaults.Default with { Size = new Vector3(2, 2, 2), Material = 0 };
        Assert.Empty(onlySize.EditsFor(5, 6));   // size and material go in the create message itself
    }

    [Fact]
    public void Settings_are_held_to_what_a_prim_may_have()
    {
        var c = (NewPrimDefaults.Default with { Size = new Vector3(100, 0, 1), Material = 9, Color = new Vector3(2, -1, 0.5f), Transparency = 95f, Glow = 2f, Texture = Guid.Empty })
            .Clamped(EditLimits.Official);
        Assert.Equal(new Vector3(64, 0.01f, 1), c.Size);
        Assert.Equal(DefaultBox.MaterialWood, c.Material);
        Assert.Equal(new Vector3(1, 0, 0.5f), c.Color);
        Assert.Equal((90f, 1f), (c.Transparency, c.Glow));
        Assert.Equal(FaceRules.DefaultTexture, c.Texture);
    }

    [Fact]
    public void Preferences_has_a_Building_page_with_every_setting_and_what_is_saved_there_is_what_Create_reads()
    {
        string[] keys = [NewPrimSettings.SizeX.Key, NewPrimSettings.SizeY.Key, NewPrimSettings.SizeZ.Key, NewPrimSettings.MaterialKey, NewPrimSettings.Red.Key,
            NewPrimSettings.Green.Key, NewPrimSettings.Blue.Key, NewPrimSettings.Transparency.Key, NewPrimSettings.Glow.Key, NewPrimSettings.FullBright.Key,
            NewPrimSettings.TextureKey, NewPrimSettings.Physical.Key, NewPrimSettings.Temporary.Key, NewPrimSettings.Phantom.Key,
            NewPrimSettings.NextOwnerCopy.Key, NewPrimSettings.NextOwnerModify.Key, NewPrimSettings.NextOwnerTransfer.Key];
        Assert.Equal(keys, PreferenceCatalog.OfPage(PrefPage.Building).Select(d => d.Key));
        var settings = new SettingsService(new MemoryStore());
        var model = new PreferencesModel(settings, "acct");
        Assert.True(model.Edit(PreferenceCatalog.Find(NewPrimSettings.SizeX.Key)!, 2.0));
        Assert.True(model.Edit(PreferenceCatalog.Find(NewPrimSettings.MaterialKey)!, "metal"));
        Assert.True(model.Edit(PreferenceCatalog.Find(NewPrimSettings.TextureKey)!, G(77).ToString()));
        Assert.True(model.Edit(PreferenceCatalog.Find(NewPrimSettings.Phantom.Key)!, true));
        Assert.False(model.Edit(PreferenceCatalog.Find(NewPrimSettings.TextureKey)!, "not a texture"));
        Assert.True(model.SaveAll());
        var read = NewPrimSettings.Read(settings);
        Assert.Equal((new Vector3(2f, 0.5f, 0.5f), 1, G(77), true), (read.Size, read.Material, read.Texture, read.Phantom));
    }

    [Fact]
    public void The_create_message_carries_the_settings_size_and_material()
    {
        var p = new Placement(new Vector3(1, 1, 30), new Vector3(1, 1, 20), Guid.Empty, true);
        var cmd = Assert.IsType<CreateObjectCommand>(new CreateBoxIntent(p, new Vector3(1, 2, 3), 1).ToCommand());
        Assert.Equal((new Vector3(1, 2, 3), (byte)1), (cmd.Scale, cmd.Material));
        var plain = Assert.IsType<CreateObjectCommand>(new CreateBoxIntent(p).ToCommand());
        Assert.Equal((DefaultBox.Scale, DefaultBox.MaterialWood), (plain.Scale, plain.Material));
        var m = LmvBuild.ObjectAdd(UUID.Zero, UUID.Zero, UUID.Zero, p, new Vector3(1, 2, 3), 1).ObjectData;
        Assert.Equal((new LibreMetaverse.Vector3(1, 2, 3), (byte)1), (m.Scale, m.Material));
    }

    [Fact]
    public void Create_places_a_box_of_the_settings_size_and_material()
    {
        var world = new WorldState(new Viewer.Core.Diagnostics.CoreLog(new ListSink(), "test"));
        world.BeginRegion(Sim, new RegionInfo("Here", 1000, 1000, 256, 256, false));
        var sent = new List<ActionIntent>();
        var ic = new InteractionController(world, i => { sent.Add(i); return new ActionResult(ActionStatus.Sent, sent.Count); }, Guid.NewGuid(),
            new SocialHooks(_ => { }, _ => { }, _ => { }, _ => { }, () => { }), _ => false)
        {
            NewPrim = () => Changed(),
        };
        ic.BeginCreate();
        var pick = new PickResult(PickKind.Land, null, null, new Vector3(10, 10, 20), 10f);
        Assert.True(ic.Place(pick, new Vector3(10, 0, 25), Vector3.UnitY, Vector3.UnitY, 0.1f));
        var create = Assert.IsType<CreateBoxIntent>(Assert.Single(sent));
        Assert.Equal((new Vector3(1, 2, 3), (byte?)1), (create.Scale, create.Material));
    }

    [Fact]
    public void A_box_this_viewer_made_gets_the_settings_once_it_arrives_and_other_objects_do_not()
    {
        var sent = new List<ActionIntent>();
        var t = new NewPrimTracker(i => { sent.Add(i); return new ActionResult(ActionStatus.Sent, sent.Count); }, Changed);
        ObjectState O(uint local, uint flags, uint parent = 0) => new(new ObjectId(1, local), G(local), parent, null, null, Vector3.Zero, Quaternion.Identity,
            Vector3.One, Vector3.Zero, Vector3.Zero, Vector3.Zero, Interaction: new PrimInteraction(0, flags));
        Assert.False(t.Arrived(O(5, Full | ObjectFlagBits.CreateSelected)));   // nothing expected
        t.Expect();
        Assert.Equal(1, t.Waiting);
        Assert.False(t.Arrived(O(6, Full)));   // not made selected: someone's script, a rez
        Assert.False(t.Arrived(O(7, ObjectFlagBits.YouOwner | ObjectFlagBits.Modify | ObjectFlagBits.CreateSelected)));   // not every permission
        Assert.False(t.Arrived(O(8, Full | ObjectFlagBits.CreateSelected, parent: 5)));   // a part
        Assert.Empty(sent);
        Assert.True(t.Arrived(O(9, Full | ObjectFlagBits.CreateSelected)));
        Assert.Equal(0, t.Waiting);
        Assert.Equivalent(Changed().EditsFor(9, NewPrimTracker.BoxFaces), sent, strict: true);   // records holding lists compare by their contents
        Assert.False(t.Arrived(O(10, Full | ObjectFlagBits.CreateSelected)));
    }

    [Fact]
    public void The_made_selected_flag_reaches_the_world()
    {
        var p = new Primitive { PrimData = new Primitive.ConstructionData(), Flags = PrimFlags.CreateSelected | PrimFlags.ObjectYouOwner };
        Assert.NotEqual(0u, (LmvTransport.ToInteraction(p)?.Flags ?? 0) & ObjectFlagBits.CreateSelected);
    }
}
