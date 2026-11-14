// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Viewer.Core.Avatars;
using Viewer.Core.Meshing;
using Viewer.Core.Session;
using Viewer.Core.World;
using Viewer.Godot.Presentation;
using Viewer.Shell;
using Quaternion = System.Numerics.Quaternion;
using Vector3 = System.Numerics.Vector3;

namespace Viewer.Godot;

public partial class TestRender
{
    /// <summary>
    /// An outfit of rigged and unrigged pieces across crossings (--tr-avatars=riggedcrossing). One avatar wears a rigged shirt, a rigged face piece, an unrigged mesh
    /// and a plain box. It is made in a region the way a crossing sends it: first the pieces (under local ids the region has not told the avatar for), then the
    /// avatar; then the avatar first and the pieces after; then, as the viewer builds a neighbouring region, in a neighbour stack. Every piece must be drawn after
    /// each leg, and none reported lost for a lasting reason.
    /// </summary>
    private async Task<int> AvatarRiggedCrossing(AvatarBody body, PreparedSkinnedSurface[] surfaces, string? assets)
    {
        var avatarRig = new AvatarRig(body.Skeleton);
        var rigged = RiggedPieces(avatarRig).Where(p => p.Id == ShirtId || p.Id == FaceId).ToList();
        var plainMeshId = Guid.Parse("a77a2000-0000-4000-8000-0000000000f1");
        var shirt = rigged.First(p => p.Id == ShirtId);
        var plainMesh = shirt with { Id = plainMeshId, Geometry = shirt.Geometry with { Skin = null }, Point = 9 };
        using var animations = AnimationSourceFor(assets);
        using var meshes = RiggedMeshes(rigged.Append(plainMesh));
        var rig = MakeRig(new Vector2I(640, 480));
        var scenario = rig.Scenario;
        var floor = Floor(scenario, new Vector3(128, 128, 20), 8f);
        double now = 0;
        var size = new Vector3(0.45f, 0.6f, 1.9f);
        var cam = rig.Camera;
        cam.Fov = 40f; cam.Near = 0.1f; cam.Far = 100f;
        cam.Position = new global::Godot.Vector3(134f, 21.3f, -128f);
        cam.LookAt(new global::Godot.Vector3(128f, 20.95f, -128f), global::Godot.Vector3.Up);

        int expected = rigged.Count + 2;   // the rigged pieces, the unrigged mesh and the box
        int leg = 0;
        foreach (var (attachmentsFirst, inStack) in new[] { (true, false), (false, false), (true, true) })
        {
            leg++;
            object sim = inStack ? new SimRef(256256UL << 32 | 256000UL, "East") : FixtureSim;
            var world = new WorldState(new Viewer.Core.Diagnostics.CoreLog(new DiscardSink(), "test-render"));
            var info = new RegionInfo("crossing" + leg, 1000, 1000, 256, 256, true);
            world.BeginRegion(sim, info);
            NeighbourStack? stack = null;
            WorldPresenter? presenter = null;
            ITerrainView? terrainView = null;
            AvatarPresenter avatars;
            PrimLayer prims;
            if (inStack)
            {
                // the neighbouring region as the viewer builds it, with the viewer's own mesh factor
                stack = new NeighbourStack(scenario, rig.Root, new RegionWorld((SimRef)sim, info, world, false, Vector3.Zero, Vector3.Zero), Guid.Empty,
                    Task.FromResult<AvatarPresenter.LoadedBody?>(new(body, surfaces)), animations, null, meshes, null, null, MeshLodRule.DefaultLodFactor, (1000, 1000));
                avatars = stack.Avatars;
                prims = stack.Prims;
            }
            else
            {
                var v = MakeView("server", rig);
                terrainView = v;
                avatars = new AvatarPresenter(scenario, world, Guid.Empty, Task.FromResult<AvatarPresenter.LoadedBody?>(new(body, surfaces)), animations, null, null, () => now);
                prims = new PrimLayer(scenario, world, meshes, null, _budget);
                presenter = new WorldPresenter(world, v, prims: prims, avatars: avatars);
            }
            prims.Camera = cam.GlobalPosition;
            var lost = new List<RiggedNotDrawn>();
            prims.RiggedAttachmentMet = lost.Add;
            void Frame()
            {
                if (stack is not null) stack.Frame(cam, null, true);
                else presenter!.Frame();
            }
            uint wearer = (uint)(4100 + leg * 100);
            var avatar = new AvatarData(wearer, RiggedAgents[0], "Crossing Test", new(128, 128, 20.95f), Quaternion.Identity, size);
            void WearAll()
            {
                for (int k = 0; k < rigged.Count; k++)
                    world.ApplyObject(sim, WornRigged(wearer * 10 + (uint)k, wearer, rigged[k]), eventIsAttachment: true);
                world.ApplyObject(sim, WornRigged(wearer * 10 + 8, wearer, plainMesh), eventIsAttachment: true);
                world.ApplyObject(sim, new(wearer * 10 + 9, PrimId(wearer * 10 + 9), wearer, 2, Vector3.Zero, Quaternion.Identity, new Vector3(0.2f, 0.2f, 0.2f), Shape: PrimShape.Box), eventIsAttachment: true);
            }
            if (attachmentsFirst) { WearAll(); await Frames(3); Frame(); world.ApplyAvatar(sim, avatar); }
            else { world.ApplyAvatar(sim, avatar); await Frames(3); Frame(); WearAll(); }
            var sw = Stopwatch.StartNew();
            while (!(avatars.Drawn == 1 && prims.Instances == expected && prims.Dirty == 0) && sw.Elapsed < TimeSpan.FromSeconds(30))
            {
                now += 0.05;
                prims.UpdateLods();
                Frame();
                await Frames(1);
            }
            string order = (inStack ? "in a neighbour stack, " : "") + (attachmentsFirst ? "the pieces before the avatar" : "the avatar before the pieces");
            if (avatars.Drawn != 1) _failures.Add($"{order}: the avatar is not drawn");
            if (prims.Instances != expected) _failures.Add($"{order}: {prims.Instances} of {expected} worn pieces are drawn (rigged {Figure(prims.Describe(), "rigged_shown")?.ToString() ?? "?"} of {rigged.Count})");
            if (lost.Any(r => !r.Temporary)) _failures.Add($"{order}: rigged pieces lost for a lasting reason: {string.Join(", ", lost.Where(r => !r.Temporary).Select(r => r.Why))}");
            _result["crossing_leg_" + leg] = new { order, pieces_drawn = prims.Instances, expected, avatars_drawn = avatars.Drawn, reports = lost.Select(r => new { r.Why, r.Temporary }).ToArray() };
            if (stack is not null) { stack.Dispose(); }
            else
            {
                presenter!.Dispose();
                prims.Clear();
                terrainView!.Clear();
                if (terrainView is Node n) { n.GetParent()?.RemoveChild(n); n.Free(); }
            }
            world.Dispose();
        }
        // Legs 4 to 6: the agent crosses from one region of a session into the next. The outfit is drawn in the region left; the region entered then sends the
        // avatar and the pieces (the same objects, by full id). From that moment on, in every frame, the avatar and every piece must be drawn somewhere (in
        // the region left or in the region entered), and in the end only the region entered draws them. The official viewer keeps one drawable per object, so
        // nothing is missing for even a frame, and nothing is let go of on a timer: leg 5 has the region entered send its pieces ten seconds late (a mesh that
        // takes that long to arrive), then moves the avatar far away; leg 6 hands the outfit back to the region it left, under the ids it had there.
        async Task Handover(string label, int lateFrames, bool far, bool back, bool walk = false, int avatarLateFrames = 0, bool staleDirty = false, float stepMetres = 0.075f, bool withChild = false)
        {
            int expectedAll = expected + (withChild ? 1 : 0);
            leg++;
            var hub = new IdentityHub { SelfAgent = RiggedAgents[0] };
            var simFrom = FixtureSim;
            var simTo = new SimRef(256256UL << 32 | 256000UL, "East");
            var worldFrom = new WorldState(new Viewer.Core.Diagnostics.CoreLog(new DiscardSink(), "test-render"));
            var worldTo = new WorldState(new Viewer.Core.Diagnostics.CoreLog(new DiscardSink(), "test-render"));
            worldFrom.AttachIdentity(hub);
            worldTo.AttachIdentity(hub);
            var infoFrom = new RegionInfo("handoff-from", 1000, 1000, 256, 256, true);
            var infoTo = new RegionInfo("handoff-to", 1000, 1000, 256, 256, true);
            worldFrom.BeginRegion(simFrom, infoFrom);
            worldTo.BeginRegion(simTo, infoTo);
            hub.Current = worldFrom;
            var viewFrom = MakeView("server", rig);
            var ledger = new HandoffLedger(() => now);
            ConfigureLedger(ledger, hub);
            // the walking leg draws every region's avatars from one body and skeleton, as the viewer does
            var bodyHost = walk ? new AvatarBodyHost(scenario, Task.FromResult<AvatarPresenter.LoadedBody?>(new(body, surfaces)), () => now) : null;
            var avatarsFrom = new AvatarPresenter(scenario, worldFrom, walk ? RiggedAgents[0] : Guid.Empty, Task.FromResult<AvatarPresenter.LoadedBody?>(new(body, surfaces)), animations, null, null, () => now) { Handoffs = ledger, Host = bodyHost };
            var primsFrom = new PrimLayer(scenario, worldFrom, meshes, null, _budget) { Handoffs = ledger };
            var presenterFrom = new WorldPresenter(worldFrom, viewFrom, prims: primsFrom, avatars: avatarsFrom);
            primsFrom.Camera = cam.GlobalPosition;
            var stackTo = new NeighbourStack(scenario, rig.Root, new RegionWorld(simTo, infoTo, worldTo, false, Vector3.Zero, Vector3.Zero), walk ? RiggedAgents[0] : Guid.Empty,
                Task.FromResult<AvatarPresenter.LoadedBody?>(new(body, surfaces)), animations, null, meshes, null, null, MeshLodRule.DefaultLodFactor, (1000, 1000), null, ledger, bodyHost);
            stackTo.Prims.Camera = cam.GlobalPosition;
            // a neighbour region with nothing in it joins the shared body in the middle of a frame, as the viewer adds a neighbour while a frame runs, and is framed after the others
            NeighbourStack? idleStack = null;
            WorldState? idleWorld = null;
            void FrameBoth()
            {
                now += 0.05;
                primsFrom.UpdateLods();
                presenterFrom.Frame();
                if (walk && idleStack is null)
                {
                    var idleSim = new SimRef(256512UL << 32 | 256000UL, "Far");
                    var idleInfo = new RegionInfo("idle", 1002, 1000, 256, 256, true);
                    idleWorld = new WorldState(new Viewer.Core.Diagnostics.CoreLog(new DiscardSink(), "test-render"));
                    idleWorld.BeginRegion(idleSim, idleInfo);
                    idleStack = new NeighbourStack(scenario, rig.Root, new RegionWorld(idleSim, idleInfo, idleWorld, false, Vector3.Zero, Vector3.Zero), RiggedAgents[0],
                        Task.FromResult<AvatarPresenter.LoadedBody?>(new(body, surfaces)), animations, null, meshes, null, null, MeshLodRule.DefaultLodFactor, (1000, 1000), null, ledger, bodyHost);
                }
                idleStack?.Frame(cam, null, true);
                stackTo.Frame(cam, null, true);
            }
            uint wearerFrom = (uint)(4100 + leg * 100), wearerTo = wearerFrom + 50;
            var agentId = RiggedAgents[0];
            var agentAvatar = new AvatarData(wearerFrom, agentId, "Crossing Test", new(128, 128, 20.95f), Quaternion.Identity, size, walk ? new Vector3(1.5f, 0, 0) : default);
            PrimData[] Outfit(uint wearer, uint localBase)
            {
                var all = new List<PrimData>();
                for (int k = 0; k < rigged.Count; k++)
                    all.Add(WornRigged(wearerFrom * 10 + (uint)k, wearerFrom, rigged[k]) with { LocalId = localBase + (uint)k, ParentLocalId = wearer });
                all.Add(WornRigged(wearerFrom * 10 + 8, wearerFrom, plainMesh) with { LocalId = localBase + 8, ParentLocalId = wearer });
                all.Add(new PrimData(wearerFrom * 10 + 9, PrimId(wearerFrom * 10 + 9), wearer, 2, Vector3.Zero, Quaternion.Identity, new Vector3(0.2f, 0.2f, 0.2f), Shape: PrimShape.Box) with { LocalId = localBase + 9 });
                // a linked part of a worn piece: a worn item is usually many linked parts, and each is drawn on the avatar through its root
                if (withChild) all.Add(new PrimData(wearerFrom * 10 + 7, PrimId(wearerFrom * 10 + 7), localBase + 8, 0, new Vector3(0.05f, 0, 0), Quaternion.Identity, new Vector3(0.1f, 0.1f, 0.1f), Shape: PrimShape.Box) with { LocalId = localBase + 7 });
                return all.ToArray();
            }
            worldFrom.ApplyAvatar(simFrom, agentAvatar);
            if (walk) worldFrom.ApplyAnimations(simFrom, agentId, [new(WalkId, 2)]);
            foreach (var piece in Outfit(wearerFrom, wearerFrom * 10))
                worldFrom.ApplyObject(simFrom, piece, eventIsAttachment: piece.AttachmentPoint != 0);
            var settle = Stopwatch.StartNew();
            while (!(avatarsFrom.Drawn == 1 && primsFrom.Instances == expectedAll && primsFrom.Dirty == 0) && settle.Elapsed < TimeSpan.FromSeconds(120))
            {
                FrameBoth();
                await Frames(1);
            }
            if (avatarsFrom.Drawn != 1 || primsFrom.Instances != expectedAll)
                _failures.Add($"{label}: the outfit is not drawn in the region left before the move ({primsFrom.Instances} of {expectedAll} pieces, {avatarsFrom.Drawn} avatars)");

            // the crossing: the region entered is where the agent is, and it sends the avatar (and the pieces, at once or late) under its own ids
            hub.Current = worldTo;
            // the avatar of the region entered: at once, or (avatarLateFrames) after its pieces, as a region may send them in either order
            bool avatarSent = avatarLateFrames == 0;
            if (avatarSent)
            {
                worldTo.ApplyAvatar(simTo, agentAvatar with { LocalId = wearerTo });
                if (walk) worldTo.ApplyAnimations(simTo, agentId, [new(WalkId, 2)]);
            }
            bool sent = lateFrames == 0;
            if (staleDirty)
            {
                // a worn piece waiting to be presented again (a piece moved far enough to be) when the region left lets go of it, and the frame's deferred work
                // runs before the queued removal is applied: the layer must not free the piece because its world no longer has it
                primsFrom.MarkAll();
            }
            if (sent) foreach (var piece in Outfit(wearerTo, wearerTo * 10)) worldTo.ApplyObject(simTo, piece, eventIsAttachment: piece.AttachmentPoint != 0);
            int fewestPieces = int.MaxValue, fewestAvatars = int.MaxValue, framesRun = 0, firstGap = -1, settledFrames = 0;
            int fewestShown = int.MaxValue, firstHidden = -1, framesWithAHiddenPiece = 0, framesWithAMissingObject = 0, fewestObjects = int.MaxValue, firstMissingObject = -1;
            // a piece counts as drawn in a frame when some region's layer has an instance of it that is shown: an instance that is hidden is not on the screen
            void CountShownPieces(int frameNumber)
            {
                var frameStates = new List<WornFrameState>();
                primsFrom.WornFrameStates(agentId, frameStates);
                stackTo.Prims.WornFrameStates(agentId, frameStates);
                int shownPieces = frameStates.Where(st => st.HasInstance && st.Shown).Select(st => st.Full).Distinct().Count();
                var objects = new HashSet<Guid>();
                primsFrom.WornObjectsShown(objects);
                stackTo.Prims.WornObjectsShown(objects);
                if (withChild && objects.Count < expectedAll) { framesWithAMissingObject++; fewestObjects = System.Math.Min(fewestObjects, objects.Count); if (firstMissingObject < 0) firstMissingObject = frameNumber; }
                if (shownPieces >= expectedAll - (withChild ? 1 : 0)) return;   // the roots a wearer's pieces hang from; the linked part is counted above
                framesWithAHiddenPiece++;
                fewestShown = System.Math.Min(fewestShown, shownPieces);
                if (firstHidden >= 0) return;
                firstHidden = frameNumber;
                var rl2 = new List<string>(); primsFrom.WornReport(agentId, rl2); var rr2 = new List<string>(); stackTo.Prims.WornReport(agentId, rr2);
                _result["first_hidden_" + leg] = new { left = rl2, entered = rr2 };
            }
            // what the walking legs measure in every frame of the move: bodies shown for the agent, pieces bound to another skeleton, and the largest gap between a piece's joint and the body's
            int framesNotOne = 0, mostInstances = 0, boundElsewhere = 0, firstNotOne = -1, framesOffPlace = 0, firstOffPlace = -1;
            double largestOffPlace = 0;
            double largestMismatch = 0;
            if (staleDirty)
            {
                for (int k = 0; k < 64 && primsFrom.Step(); k++) { }
                CountShownPieces(0);
            }
            var move = Stopwatch.StartNew();
            while (framesRun < 900 && move.Elapsed < TimeSpan.FromSeconds(60))
            {
                if (!avatarSent && framesRun >= avatarLateFrames)
                {
                    avatarSent = true;
                    worldTo.ApplyAvatar(simTo, agentAvatar with { LocalId = wearerTo });
                }
                if (walk && avatarSent) worldTo.ApplyAvatar(simTo, agentAvatar with { LocalId = wearerTo, Position = new(128 + stepMetres * framesRun, 128, 20.95f) });
                if (!sent && framesRun >= lateFrames)
                {
                    sent = true;
                    foreach (var piece in Outfit(wearerTo, wearerTo * 10)) worldTo.ApplyObject(simTo, piece, eventIsAttachment: piece.AttachmentPoint != 0);
                }
                FrameBoth();
                framesRun++;
                int pieces = primsFrom.Instances + stackTo.Prims.Instances, avatarsDrawn = avatarsFrom.Drawn + stackTo.Avatars.Drawn;
                fewestPieces = System.Math.Min(fewestPieces, pieces);
                fewestAvatars = System.Math.Min(fewestAvatars, avatarsDrawn);
                CountShownPieces(framesRun);
                if ((pieces < expectedAll || avatarsDrawn < 1) && firstGap < 0)
                {
                    firstGap = framesRun;
                    var rl = new List<string>(); primsFrom.WornReport(agentId, rl); var rr = new List<string>(); stackTo.Prims.WornReport(agentId, rr);
                    _result["first_gap_" + leg] = new { left = rl, entered = rr, left_counts = primsFrom.HandoffCounts, entered_counts = stackTo.Prims.HandoffCounts, scenes = new[] { avatarsFrom.Scene.Count, stackTo.Avatars.Scene.Count } };
                }
                bool entered = (walk ? stackTo.Avatars.AgentDrawn(agentId) : stackTo.Avatars.Drawn == 1) && stackTo.Prims.Instances == expectedAll && stackTo.Prims.Dirty == 0;
                bool left = (walk ? avatarsFrom.Scene.Count : avatarsFrom.Drawn) == 0 && primsFrom.Instances == 0;
                if (walk && framesRun > 2)
                {
                    var shown = avatarsFrom.DrawnInstances(agentId).Concat(stackTo.Avatars.DrawnInstances(agentId)).Distinct().ToList();
                    mostInstances = System.Math.Max(mostInstances, shown.Count);
                    // the body is drawn where the agent is: within half a metre of the scene's own place for it, in every frame
                    foreach (var presenterOf in new[] { avatarsFrom, stackTo.Avatars })
                        if (presenterOf.TryDrawnSoles(agentId, out var drawnAt, out var wantedAt))
                        {
                            double off = System.Math.Sqrt((drawnAt.X - wantedAt.X) * (drawnAt.X - wantedAt.X) + (drawnAt.Y - wantedAt.Y) * (drawnAt.Y - wantedAt.Y));
                            largestOffPlace = System.Math.Max(largestOffPlace, off);
                            if (off > 0.5) { framesOffPlace++; if (firstOffPlace < 0) firstOffPlace = framesRun; }
                            break;
                        }
                    if (shown.Count != 1) { framesNotOne++; if (firstNotOne < 0) firstNotOne = framesRun; }
                    var bound = new List<WornBinding>();
                    primsFrom.WornBindings(agentId, bound);
                    stackTo.Prims.WornBindings(agentId, bound);
                    foreach (var b in bound)
                    {
                        if (shown.Count == 1 && !(ReferenceEquals(b.Layer, shown[0].Layer) && b.SkeletonRow == shown[0].Row)) boundElsewhere++;
                        System.Numerics.Matrix4x4 bodyJoint = default;
                        bool have = b.Rigged ? avatarsFrom.TryDrawnRigged(agentId, out bodyJoint) || stackTo.Avatars.TryDrawnRigged(agentId, out bodyJoint)
                            : avatarsFrom.TryDrawnPoint(agentId, b.Point, out bodyJoint) || stackTo.Avatars.TryDrawnPoint(agentId, b.Point, out bodyJoint);
                        if (have) largestMismatch = System.Math.Max(largestMismatch, MaxAbsDifference(b.Joint, bodyJoint)); else boundElsewhere++;
                    }
                }
                if (sent && avatarSent && entered && left && (!walk || ++settledFrames > 40)) break;
                await Frames(1);
            }
            if (fewestPieces < expectedAll) _failures.Add($"{label}: in the frame {firstGap} after the move only {fewestPieces} of {expectedAll} worn pieces were drawn (in either region)");
            if (fewestAvatars < 1) _failures.Add($"{label}: in the frame {firstGap} after the move the avatar was drawn in neither region");
            if (framesWithAMissingObject > 0) _failures.Add($"{label}: in {framesWithAMissingObject} frames after the move a worn object (a linked part of a piece) was shown by neither region (first in frame {firstMissingObject}, fewest shown {fewestObjects} of {expectedAll})");
            if (framesWithAHiddenPiece > 0) _failures.Add($"{label}: in {framesWithAHiddenPiece} frames after the move a worn piece had no shown instance in either region (first in frame {firstHidden}, fewest shown {fewestShown} of {expectedAll})");
            if (walk)
            {
                // one avatar: its body shown exactly once in every frame, every piece bound to that skeleton, no joint of a piece off the body's
                if (framesNotOne > 0) _failures.Add($"{label}: the avatar was drawn other than once in {framesNotOne} frames (first at frame {firstNotOne}, most at once {mostInstances})");
                if (boundElsewhere > 0) _failures.Add($"{label}: {boundElsewhere} piece-frames were bound to a skeleton other than the one drawn");
                if (largestMismatch > 1e-4) _failures.Add($"{label}: a worn piece's joint was {largestMismatch:0.####} off the drawn body's");
                if (framesOffPlace > 0) _failures.Add($"{label}: the drawn body was more than half a metre from the agent in {framesOffPlace} frames (first at frame {firstOffPlace}, furthest {largestOffPlace:0.##} m)");
            }
            else if (stackTo.Avatars.Drawn != 1 || stackTo.Prims.Instances != expectedAll) _failures.Add($"{label}: the region entered draws {stackTo.Prims.Instances} of {expectedAll} pieces and {stackTo.Avatars.Drawn} avatars at the end");
            if (walk ? avatarsFrom.Scene.Count != 0 || primsFrom.Instances != 0 : avatarsFrom.Drawn != 0 || primsFrom.Instances != 0) _failures.Add($"{label}: the region left still draws {primsFrom.Instances} pieces and {avatarsFrom.Scene.Count} avatars at the end");

            int farPieces = -1, farAvatars = -1;
            if (far)
            {
                // the avatar is a long way from where the move happened and from the camera: nothing about a worn piece is judged by where it was handed over
                worldTo.ApplyAvatar(simTo, agentAvatar with { LocalId = wearerTo, Position = new(100000, 100000, 20.95f) });
                farPieces = int.MaxValue; farAvatars = int.MaxValue;
                for (int f = 0; f < 80; f++)
                {
                    FrameBoth();
                    farPieces = System.Math.Min(farPieces, primsFrom.Instances + stackTo.Prims.Instances);
                    farAvatars = System.Math.Min(farAvatars, avatarsFrom.Drawn + stackTo.Avatars.Drawn);
                    await Frames(1);
                }
                if (farPieces < expectedAll) _failures.Add($"{label}: far from the handover and the camera only {farPieces} of {expectedAll} worn pieces stayed drawn");
                if (farAvatars < 1) _failures.Add($"{label}: far from the handover and the camera the avatar was not drawn");
            }
            int backPieces = -1, backAvatars = -1;
            if (back)
            {
                // the agent crosses back: the region left sends the avatar and the pieces again, under the ids it used before
                hub.Current = worldFrom;
                worldFrom.ApplyAvatar(simFrom, agentAvatar);
                foreach (var piece in Outfit(wearerFrom, wearerFrom * 10))
                    worldFrom.ApplyObject(simFrom, piece, eventIsAttachment: piece.AttachmentPoint != 0);
                backPieces = int.MaxValue; backAvatars = int.MaxValue;
                for (int f = 0; f < 400; f++)   // twenty seconds of the clock: past any timer
                {
                    FrameBoth();
                    backPieces = System.Math.Min(backPieces, primsFrom.Instances + stackTo.Prims.Instances);
                    backAvatars = System.Math.Min(backAvatars, avatarsFrom.Drawn + stackTo.Avatars.Drawn);
                    await Frames(1);
                }
                if (backPieces < expectedAll) _failures.Add($"{label}: coming back only {backPieces} of {expectedAll} worn pieces were drawn in a frame");
                if (backAvatars < 1) _failures.Add($"{label}: coming back the avatar was drawn in neither region in a frame");
                if (avatarsFrom.Drawn != 1 || primsFrom.Instances != expectedAll) _failures.Add($"{label}: after coming back the region draws {primsFrom.Instances} of {expectedAll} pieces and {avatarsFrom.Drawn} avatars");
            }
            _result["crossing_leg_" + leg] = new { order = label, pieces_drawn = stackTo.Prims.Instances, expectedAll, avatars_drawn = stackTo.Avatars.Drawn, fewest_pieces_in_a_frame = fewestPieces, fewest_avatars_in_a_frame = fewestAvatars, fewest_pieces_shown_in_a_frame = fewestShown == int.MaxValue ? expectedAll : fewestShown, frames_with_a_hidden_piece = framesWithAHiddenPiece, frames = framesRun, handed_over = hub.Handoffs, far_fewest_pieces = farPieces, far_fewest_avatars = farAvatars, back_fewest_pieces = backPieces, back_fewest_avatars = backAvatars };
            if (walk) _result["crossing_leg_" + leg + "_one_avatar"] = new { frames_not_once = framesNotOne, most_bodies_at_once = mostInstances, pieces_on_another_skeleton = boundElsewhere, largest_joint_mismatch = largestMismatch, frames_off_place = framesOffPlace, largest_off_place_m = System.Math.Round(largestOffPlace, 3) };
            idleStack?.Dispose();
            idleWorld?.Dispose();
            stackTo.Dispose();
            presenterFrom.Dispose();
            primsFrom.Clear();
            viewFrom.Clear();
            if (viewFrom is Node vn) { vn.GetParent()?.RemoveChild(vn); vn.Free(); }
            worldFrom.Dispose();
            worldTo.Dispose();
            bodyHost?.Dispose();
        }
        static double MaxAbsDifference(System.Numerics.Matrix4x4 a, System.Numerics.Matrix4x4 b) =>
            new[] { a.M11 - b.M11, a.M12 - b.M12, a.M13 - b.M13, a.M14 - b.M14, a.M21 - b.M21, a.M22 - b.M22, a.M23 - b.M23, a.M24 - b.M24,
                    a.M31 - b.M31, a.M32 - b.M32, a.M33 - b.M33, a.M34 - b.M34, a.M41 - b.M41, a.M42 - b.M42, a.M43 - b.M43, a.M44 - b.M44 }.Max(System.Math.Abs);
        await Handover("handing over", 0, false, false);
        await Handover("handing over, the pieces ten seconds late, then far away", 200, true, false);
        await Handover("handing over and coming back", 0, false, true);
        // one drawn avatar: the walk plays as the agent crosses, and in every frame the body is shown once and the pieces ride its skeleton
        // a region may send the avatar after the pieces it wears: in no frame may a piece be left without a shown instance in either region
        await Handover("handing over, the avatar after its pieces", 0, false, false, avatarLateFrames: 6);
        await Handover("handing over, the avatar well after its pieces", 0, false, false, avatarLateFrames: 40);
        await Handover("handing over, the region left presenting a worn piece again as it lets go", 0, false, false, staleDirty: true);
        await Handover("handing over, the avatar already handed over and its pieces still in the region left, which presents them again", 120, false, false, staleDirty: true);
        // a worn piece made of linked parts: every part stays drawn by some region in every frame, as the root does
        await Handover("handing over, a piece with a linked part", 0, false, false, withChild: true);
        await Handover("handing over, a piece with a linked part, the region left presenting its parts again as it lets go", 0, false, false, staleDirty: true, withChild: true);
        await Handover("flying across, a piece with a linked part", 40, false, false, walk: true, stepMetres: 0.4f, withChild: true);
        await Handover("walking across", 40, false, false, walk: true);
        await Handover("walking across, the pieces late", 120, false, false, walk: true);
        // a flying avatar covers about 0.4 m a frame, five times a walker's step: nothing may be missing, doubled or off the body in any frame of the move
        await Handover("flying across", 40, false, false, walk: true, stepMetres: 0.4f);
        await Handover("flying across, the avatar after its pieces", 0, false, false, walk: true, avatarLateFrames: 6, stepMetres: 0.4f);
        // flying fast across, the region entered stating the avatar late: in every frame the drawn body moves on by the speed and the pieces stay on it
        await FastCrossing("flying fast across, the region entered stating the avatar where it crossed a fifth of a second late", enteredFramesFirst: false);
        await FastCrossing("flying fast across into the region drawn first, stated a fifth of a second late", enteredFramesFirst: true);
        // The own avatar flies at 18 m/s. The region left states it every 5 m, as a region does for an avatar moving as predicted, and stops at the border. The
        // region entered states it only a fifth of a second after the crossing, and at the place where it crossed (a region resumes the avatar there once the
        // viewer has told it the agent arrived), then every 5 m again. The official viewer has one object for the agent's avatar, carried along its velocity
        // across the border (llviewerobject.cpp, interpolateLinearMotion) and moved by the entered region's updates as by any (processUpdateMessage), taken
        // where they say: the late statement puts it back, and the official viewer's body steps back too. In every frame from the run-up on, the body is drawn
        // where the official viewer draws it on the same statements taken in the same frames (Viewer.Motion.OfficialOwnAvatarReplay, within 0.1 m), at the
        // scene's place for the agent (within 0.1 m), and every piece rides the body's skeleton. With enteredFramesFirst the region entered is framed before the
        // region left in each frame, as the region the viewer started in is. The largest frame step beyond the speed's is reported.
        async Task FastCrossing(string label, bool enteredFramesFirst)
        {
            leg++;
            const float speed = 18f, late = 0.2f, dt = 0.05f, crossX = 128f, startX = 110f, flyZ = 40f;
            var hub = new IdentityHub { SelfAgent = RiggedAgents[0] };
            var agentId = RiggedAgents[0];
            var simFrom = FixtureSim;
            var simTo = new SimRef(256256UL << 32 | 256000UL, "East");
            var worldFrom = new WorldState(new Viewer.Core.Diagnostics.CoreLog(new DiscardSink(), "test-render"));
            var worldTo = new WorldState(new Viewer.Core.Diagnostics.CoreLog(new DiscardSink(), "test-render"));
            worldFrom.AttachIdentity(hub);
            worldTo.AttachIdentity(hub);
            var infoFrom = new RegionInfo("handoff-from", 1000, 1000, 256, 256, true);
            var infoTo = new RegionInfo("handoff-to", 1000, 1000, 256, 256, true);
            worldFrom.BeginRegion(simFrom, infoFrom);
            worldTo.BeginRegion(simTo, infoTo);
            hub.Current = worldFrom;
            var viewFrom = MakeView("server", rig);
            var ledger = new HandoffLedger(() => now);
            ConfigureLedger(ledger, hub);
            var bodyHost = new AvatarBodyHost(scenario, Task.FromResult<AvatarPresenter.LoadedBody?>(new(body, surfaces)), () => now);
            var avatarsFrom = new AvatarPresenter(scenario, worldFrom, agentId, Task.FromResult<AvatarPresenter.LoadedBody?>(new(body, surfaces)), animations, null, null, () => now) { Handoffs = ledger, Host = bodyHost };
            var primsFrom = new PrimLayer(scenario, worldFrom, meshes, null, _budget) { Handoffs = ledger };
            var presenterFrom = new WorldPresenter(worldFrom, viewFrom, prims: primsFrom, avatars: avatarsFrom);
            primsFrom.Camera = cam.GlobalPosition;
            // the neighbour stack's presenter has its own clock, as in the viewer
            var stackTo = new NeighbourStack(scenario, rig.Root, new RegionWorld(simTo, infoTo, worldTo, false, Vector3.Zero, Vector3.Zero), agentId,
                Task.FromResult<AvatarPresenter.LoadedBody?>(new(body, surfaces)), animations, null, meshes, null, null, MeshLodRule.DefaultLodFactor, (1000, 1000), null, ledger, bodyHost);
            stackTo.Prims.Camera = cam.GlobalPosition;
            void FrameBoth()
            {
                now += dt;
                primsFrom.UpdateLods();
                if (enteredFramesFirst) { stackTo.Frame(cam, null, true); presenterFrom.Frame(); }
                else { presenterFrom.Frame(); stackTo.Frame(cam, null, true); }
            }
            uint wearerFrom = (uint)(4100 + leg * 100), wearerTo = wearerFrom + 50;
            var avatarAt = new AvatarData(wearerFrom, agentId, "Crossing Test", new(startX, 128, flyZ), Quaternion.Identity, size);
            PrimData[] Outfit(uint wearer, uint localBase)
            {
                var all = new List<PrimData>();
                for (int k = 0; k < rigged.Count; k++)
                    all.Add(WornRigged(wearerFrom * 10 + (uint)k, wearerFrom, rigged[k]) with { LocalId = localBase + (uint)k, ParentLocalId = wearer });
                all.Add(WornRigged(wearerFrom * 10 + 8, wearerFrom, plainMesh) with { LocalId = localBase + 8, ParentLocalId = wearer });
                all.Add(new PrimData(wearerFrom * 10 + 9, PrimId(wearerFrom * 10 + 9), wearer, 2, Vector3.Zero, Quaternion.Identity, new Vector3(0.2f, 0.2f, 0.2f), Shape: PrimShape.Box) with { LocalId = localBase + 9 });
                return all.ToArray();
            }
            worldFrom.ApplyAvatar(simFrom, avatarAt);
            foreach (var piece in Outfit(wearerFrom, wearerFrom * 10))
                worldFrom.ApplyObject(simFrom, piece, eventIsAttachment: piece.AttachmentPoint != 0);
            var settle = Stopwatch.StartNew();
            while (!(avatarsFrom.Drawn == 1 && primsFrom.Instances == expected && primsFrom.Dirty == 0) && settle.Elapsed < TimeSpan.FromSeconds(120))
            {
                FrameBoth();
                await Frames(1);
            }
            if (avatarsFrom.Drawn != 1 || primsFrom.Instances != expected)
                _failures.Add($"{label}: the outfit is not drawn in the region left before the flight ({primsFrom.Instances} of {expected} pieces, {avatarsFrom.Drawn} avatars)");
            var velocity = new Vector3(speed, 0, 0);
            var official = new Viewer.Motion.OfficialOwnAvatarReplay();
            var statedNow = new List<System.Numerics.Vector3>();
            double worstOfficial = 0;
            string worstOfficialAt = "";
            double t0 = now, crossedAt = t0 + (crossX - startX) / speed, statedAt = crossedAt + late;
            float lastStated = float.NegativeInfinity;
            bool crossed = false, entered = false;
            float? before = null;
            double worstStep = 0, worstOff = 0, worstMismatch = 0;
            int firstStepJump = -1, framesBound = 0, framesDrawn = 0, frame = 0;
            string worstAt = "";
            for (; frame < 70; frame++)
            {
                double t = now - t0;
                float x = startX + speed * (float)t;   // where the agent truly is
                if (!crossed && x < crossX && x - lastStated >= 5f)
                {
                    lastStated = x;
                    worldFrom.ApplyAvatar(simFrom, avatarAt with { Position = new(x, 128, flyZ), Velocity = velocity });
                    statedNow.Add(new(x, 128, flyZ));
                }
                if (!crossed && now >= crossedAt) { crossed = true; hub.Current = worldTo; }
                if (crossed && !entered && now >= statedAt)
                {
                    entered = true;
                    lastStated = crossX;
                    worldTo.ApplyAvatar(simTo, avatarAt with { LocalId = wearerTo, Position = new(crossX, 128, flyZ), Velocity = velocity });   // where it crossed
                    statedNow.Add(new(crossX, 128, flyZ));
                    foreach (var piece in Outfit(wearerTo, wearerTo * 10)) worldTo.ApplyObject(simTo, piece, eventIsAttachment: piece.AttachmentPoint != 0);
                }
                else if (entered && x - lastStated >= 5f)
                {
                    lastStated = x;
                    worldTo.ApplyAvatar(simTo, avatarAt with { LocalId = wearerTo, Position = new(x, 128, flyZ), Velocity = velocity });
                    statedNow.Add(new(x, 128, flyZ));
                }
                FrameBoth();
                // the official viewer takes this frame's statements in its network step, then moves the object and its drawable
                foreach (var p in statedNow) official.Statement(p, new System.Numerics.Vector3(speed, 0, 0), System.Numerics.Vector3.Zero, 256, now, dt, 1f);
                statedNow.Clear();
                var drawnOfficial = official.Frame(now, dt, 1f, _ => true);
                await Frames(1);
                var shown = avatarsFrom.DrawnInstances(agentId).Concat(stackTo.Avatars.DrawnInstances(agentId)).Distinct().ToList();
                if (shown.Count != 1 || shown[0].Layer is not AvatarLayer layer || !layer.TryDrawnSoles(shown[0].Row, out var soles)) { before = null; continue; }
                framesDrawn++;
                if (frame >= 6 && before is { } b)   // from the third statement of the run-up on (the motion's start has settled)
                {
                    double err = System.Math.Abs(soles.X - b - speed * dt);
                    if (err > worstStep) { worstStep = err; worstAt = $"frame {frame} ({(now - crossedAt):0.00} s from the crossing): drawn at x {soles.X:0.00} after {b:0.00}"; }
                    if (err > 0.1 && firstStepJump < 0) firstStepJump = frame;
                    double fromOfficial = System.Math.Abs(soles.X - drawnOfficial.X);
                    if (fromOfficial > worstOfficial) { worstOfficial = fromOfficial; worstOfficialAt = $"frame {frame} ({(now - crossedAt):0.00} s from the crossing): drawn at x {soles.X:0.00}, official {drawnOfficial.X:0.00}"; }
                    foreach (var presenterOf in new[] { avatarsFrom, stackTo.Avatars })
                        if (presenterOf.TryDrawnSoles(agentId, out var drawnAt, out var wantedAt))
                        {
                            worstOff = System.Math.Max(worstOff, System.Math.Sqrt((drawnAt.X - wantedAt.X) * (drawnAt.X - wantedAt.X) + (drawnAt.Y - wantedAt.Y) * (drawnAt.Y - wantedAt.Y)));
                            break;
                        }
                    var bound = new List<WornBinding>();
                    primsFrom.WornBindings(agentId, bound);
                    stackTo.Prims.WornBindings(agentId, bound);
                    if (bound.Count > 0) framesBound++;
                    foreach (var wb in bound)
                    {
                        System.Numerics.Matrix4x4 bodyJoint = default;
                        bool have = wb.Rigged ? avatarsFrom.TryDrawnRigged(agentId, out bodyJoint) || stackTo.Avatars.TryDrawnRigged(agentId, out bodyJoint)
                            : avatarsFrom.TryDrawnPoint(agentId, wb.Point, out bodyJoint) || stackTo.Avatars.TryDrawnPoint(agentId, wb.Point, out bodyJoint);
                        if (have) worstMismatch = System.Math.Max(worstMismatch, MaxAbsDifference(wb.Joint, bodyJoint));
                    }
                }
                before = soles.X;
            }
            if (!entered) _failures.Add($"{label}: the flight did not reach the region entered's statement ({frame} frames)");
            if (framesDrawn < 60) _failures.Add($"{label}: the body was drawn once in only {framesDrawn} of {frame} frames");
            if (worstOfficial > 0.1) _failures.Add($"{label}: the drawn body was up to {worstOfficial:0.000} m from where the official viewer draws it ({worstOfficialAt})");
            if (worstOff > 0.1) _failures.Add($"{label}: the drawn body was up to {worstOff:0.00} m from the scene's place for the agent");
            if (framesBound == 0) _failures.Add($"{label}: no worn piece was bound to the body during the flight (the test did not see the pieces)");
            if (worstMismatch > 1e-4) _failures.Add($"{label}: a worn piece's joint was {worstMismatch:0.####} off the drawn body's");
            _result["crossing_leg_" + leg] = new { order = label, frames = frame, frames_drawn = framesDrawn, largest_step_error_m = System.Math.Round(worstStep, 3), worst = worstAt, largest_from_official_m = System.Math.Round(worstOfficial, 3), worst_from_official = worstOfficialAt, largest_off_place_m = System.Math.Round(worstOff, 3), largest_joint_mismatch = worstMismatch, frames_with_bound_pieces = framesBound };
            stackTo.Dispose();
            presenterFrom.Dispose();
            primsFrom.Clear();
            viewFrom.Clear();
            if (viewFrom is Node vn) { vn.GetParent()?.RemoveChild(vn); vn.Free(); }
            worldFrom.Dispose();
            worldTo.Dispose();
            bodyHost.Dispose();
        }
        // a rigged piece worn is presented again after its decoded geometry was let go (the content keeper lets a mesh go once nothing asks for it, and a
        // worn piece is presented again each time its wearer has moved a little): the piece stays drawn until the decode returns, as the official viewer keeps
        // drawing an object's current volume until the new one is ready (llvovolume.cpp, lodOrSculptChanged). In no frame may a piece be missing.
        {
            leg++;
            var letGoWorld = new WorldState(new Viewer.Core.Diagnostics.CoreLog(new DiscardSink(), "test-render"));
            letGoWorld.BeginRegion(FixtureSim, new RegionInfo("letgo", 1000, 1000, 256, 256, true));
            var letGoView = MakeView("server", rig);
            var letGoAvatars = new AvatarPresenter(scenario, letGoWorld, Guid.Empty, Task.FromResult<AvatarPresenter.LoadedBody?>(new(body, surfaces)), animations, null, null, () => now);
            var letGoPrims = new PrimLayer(scenario, letGoWorld, meshes, null, _budget);
            var letGoPresenter = new WorldPresenter(letGoWorld, letGoView, prims: letGoPrims, avatars: letGoAvatars);
            letGoPrims.Camera = cam.GlobalPosition;
            uint letGoWearer = 9100;
            letGoWorld.ApplyAvatar(FixtureSim, new AvatarData(letGoWearer, RiggedAgents[0], "Crossing Test", new(128, 128, 20.95f), Quaternion.Identity, size));
            for (int k = 0; k < rigged.Count; k++)
                letGoWorld.ApplyObject(FixtureSim, WornRigged(letGoWearer * 10 + (uint)k, letGoWearer, rigged[k]), eventIsAttachment: true);
            var letGoClock = Stopwatch.StartNew();
            while (!(letGoAvatars.Drawn == 1 && letGoPrims.Instances == rigged.Count && letGoPrims.Dirty == 0) && letGoClock.Elapsed < TimeSpan.FromSeconds(30))
            {
                now += 0.05;
                letGoPrims.UpdateLods();
                letGoPresenter.Frame();
                await Frames(1);
            }
            if (letGoPrims.Instances != rigged.Count) _failures.Add($"geometry let go while worn: only {letGoPrims.Instances} of {rigged.Count} rigged pieces were drawn before the test began");
            int released = rigged.Count(p => meshes.Release(p.Id, TimeSpan.FromSeconds(now)));
            letGoPrims.MarkAll();   // every piece is presented again, as the wearer's movement does
            int fewest = int.MaxValue, framesRun = 0;
            while (framesRun < 240 && (framesRun < 20 || letGoPrims.Dirty > 0))
            {
                now += 0.05;
                letGoPrims.UpdateLods();
                letGoPresenter.Frame();
                fewest = System.Math.Min(fewest, letGoPrims.Instances);
                framesRun++;
                await Frames(1);
            }
            if (released != rigged.Count) _failures.Add($"geometry let go while worn: {released} of {rigged.Count} meshes could be let go (the test did not set the case up)");
            if (fewest < rigged.Count) _failures.Add($"geometry let go while worn: in a frame only {fewest} of {rigged.Count} rigged pieces were drawn while their geometry was decoded again");
            if (letGoPrims.Instances != rigged.Count) _failures.Add($"geometry let go while worn: {letGoPrims.Instances} of {rigged.Count} rigged pieces are drawn after the decode");
            _result["crossing_leg_" + leg] = new { order = "rigged pieces presented again after their geometry was let go", released, fewest_pieces_in_a_frame = fewest, frames = framesRun, pieces_drawn = letGoPrims.Instances, expected = rigged.Count };
            letGoPresenter.Dispose();
            letGoPrims.Clear();
            letGoView.Clear();
            if (letGoView is Node letGoNode) { letGoNode.GetParent()?.RemoveChild(letGoNode); letGoNode.Free(); }
            letGoWorld.Dispose();
        }
        _result["mode"] = "avatars-rigged-crossing";
        Rids.Free(floor.Instance);
        Rids.Free(floor.Mesh);
        floor.Material.Dispose();
        return _failures.Count == 0 ? 0 : 1;
    }

    private static void ConfigureLedger(HandoffLedger ledger, IdentityHub hub)
    {
        ledger.Exists = hub.Holds;
        ledger.Locate = hub.CurrentAvatar;
    }
}
