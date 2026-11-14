// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Actions;
using Viewer.Core.Diagnostics;
using Viewer.Core.Interaction;
using Viewer.Core.Session;
using Viewer.Core.World;
using Viewer.Shell;

namespace Viewer.Core.Tests;

/// <summary>A small in-memory world with a controller, shared by the tests of the menus that meet across features: block, attach, the own avatar's menu.</summary>
internal sealed class WireRig
{
    public static readonly SimRef Sim = new(256000UL << 32 | 256000UL, "Here");
    public static readonly RegionInfo Region = new("Here", 1000, 1000, 256, 256, false);
    public static readonly Guid Me = Guid.Parse("0f3f2d4e-7a41-4c1d-9a55-5b0f2f6a1e11");
    public static readonly Guid Other = Guid.Parse("0f3f2d4e-7a41-4c1d-9a55-5b0f2f6a1e22");
    public static Guid G(uint n) => new((int)n, 0, 0, new byte[8]);

    public readonly WorldState World = new(new CoreLog(new ListSink(), "test"));
    public readonly List<ActionIntent> Sent = new();
    public readonly List<string> Calls = new();
    public readonly HashSet<(MuteKind, Guid)> Blocked = new();
    public readonly InteractionController C;

    public WireRig(Func<ActionIntent, ActionResult>? submit = null)
    {
        World.BeginRegion(Sim, Region);
        World.ApplyAvatar(Sim, new AvatarData(500, Me, "Me", new Vector3(100, 100, 21), Quaternion.Identity, new Vector3(0.45f, 0.6f, 1.9f)));
        World.ApplyAvatar(Sim, new AvatarData(501, Other, "Other", new Vector3(120, 100, 21), Quaternion.Identity, new Vector3(0.45f, 0.6f, 1.9f)));
        var hooks = new SocialHooks(a => Calls.Add("im"), a => Calls.Add("friend"), a => Calls.Add("offer"), a => Calls.Add("request"), () => Calls.Add("home"),
            Block: (k, id) => { Calls.Add($"block:{k}:{id}"); Blocked.Add((k, id)); },
            Unblock: (k, id) => { Calls.Add($"unblock:{k}:{id}"); Blocked.Remove((k, id)); },
            IsBlocked: (k, id) => Blocked.Contains((k, id)));
        C = new InteractionController(World, submit ?? (i => { Sent.Add(i); return new ActionResult(ActionStatus.Sent, Sent.Count); }), Me, hooks, _ => false);
    }

    public void Prim(uint local, Vector3 pos, PrimInteraction? i = null, uint parent = 0) =>
        World.ApplyObject(Sim, new PrimData(local, G(local), parent, 0, pos, Quaternion.Identity, new Vector3(2, 2, 2), Interaction: i));

    /// <summary>A root object worn by the own avatar (local id 500) on <paramref name="point"/>, in that point's frame, with the inventory item it came from.</summary>
    public void Worn(uint local, int point, Guid item, Vector3 inPoint = default, PrimInteraction? i = null, uint parent = 500) =>
        World.ApplyObject(Sim, new PrimData(local, G(local), parent, point, inPoint, Quaternion.Identity, new Vector3(2, 2, 2), Interaction: i, AttachItemId: item), eventIsAttachment: true);

    /// <summary>The frame of an attachment point for the tests: the point sits at (50, 50, 30).</summary>
    public static Matrix4x4? PointAt5050(ObjectState o) => Matrix4x4.CreateTranslation(50, 50, 30);

    /// <summary>What a ray down from above (50, 50) hits when the own worn objects can be found through their attachment point.</summary>
    public PickResult DownAtWorn(float x = 50, float y = 50) =>
        ObjectPicker.Pick(World, new PickRay(new Vector3(x, y, 60), new Vector3(0, 0, -1)), (_, _) => 20f, ignoreAgent: Me, attachmentFrame: PointAt5050);

    /// <summary>What a ray down from above a spot of the region hits; the own avatar counts unless <paramref name="ignoreSelf"/>.</summary>
    public PickResult Down(float x, float y, bool ignoreSelf = true) =>
        ObjectPicker.Pick(World, new PickRay(new Vector3(x, y, 60), new Vector3(0, 0, -1)), (_, _) => 20f, ignoreAgent: ignoreSelf ? Me : null);
}
