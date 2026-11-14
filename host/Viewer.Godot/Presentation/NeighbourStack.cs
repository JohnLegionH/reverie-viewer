// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Threading.Tasks;
using Godot;
using Viewer.Core.Residency;
using Viewer.Core.Session;
using Viewer.Core.World;
using Viewer.Shell;

namespace Viewer.Godot.Presentation;

/// <summary>
/// The presentation of one neighbouring region: its terrain, its objects and its avatars, drawn in the scene the agent's own region is
/// drawn in. The region's world keeps free-standing things in the anchor region's frame (<see cref="WorldState.OriginOffset"/>), so its
/// prim layer and avatar presenter are the ordinary ones; only the terrain, whose heights are the region's own, is placed at the
/// region's offset. It has no sky or water of its own (those are the anchor region's), no glow pass and no ban wall.
///
/// Textures: the stack's prim layer joins the texture hub of the anchor's layer (<see cref="TextureHub"/>), so one residency manager
/// with one budget, one set of GPU textures and one fetch cover every region: a texture two regions use is fetched, decoded and held once.
/// </summary>
public sealed class NeighbourStack : System.IDisposable
{
    /// <summary>A neighbour is drawn (and worked on) while its nearest edge is within the draw distance plus this margin (metres), so one on the edge of
    /// the range does not flicker as the camera moves.</summary>
    public const float ReachMargin = 32f;

    public SimRef Sim { get; }
    public WorldState World { get; }
    public ServerTerrainView View { get; }
    public PrimLayer Prims { get; }
    public AvatarPresenter Avatars { get; }
    public WorldPresenter Presenter { get; }

    /// <summary>Whether the stack was in reach at its last frame.</summary>
    public bool Drawn { get; private set; } = true;

    public NeighbourStack(Rid scenario, Node3D labels, RegionWorld region, System.Guid selfAgent, Task<AvatarPresenter.LoadedBody?>? body,
        AnimationSource? animations, TextureSource? textures, MeshSource? meshes, MaterialSource? materials, TextureHub? hub, float lodFactor,
        (int X, int Y) anchorCell, System.Func<int, int, float?>? outsideHeight = null, HandoffLedger? handoffs = null, AvatarBodyHost? bodyHost = null)
    {
        Sim = region.Sim;
        World = region.World;
        View = new ServerTerrainView(scenario, region.OriginOffset);
        // a mesh source has one clock, set by the layer made last: the anchor's stays
        var clock = meshes?.Clock;
        Prims = new PrimLayer(scenario, region.World, meshes, textures, null, hub) { MaterialAssets = materials, LodFactor = lodFactor, Handoffs = handoffs };
        if (meshes is not null) meshes.Clock = clock;
        Avatars = new AvatarPresenter(scenario, region.World, selfAgent, body, animations, textures, Prims.Residency) { Handoffs = handoffs, Host = bodyHost };
        Presenter = new WorldPresenter(region.World, View, prims: Prims, terrainTextures: textures, environment: null, avatars: Avatars)
        {
            DetailGridOverride = anchorCell,
            OutsideHeight = outsideHeight,
        };
    }

    /// <summary>Main thread, once per frame. Out of reach the stack's terrain is hidden and no work is done for it; the changes it is sent
    /// wait in its queue.</summary>
    public void Frame(Camera3D camera, ResidencyCamera? view, bool inReach)
    {
        Drawn = inReach;
        View.SetVisible(inReach);
        if (!inReach) return;
        Prims.SetCamera(camera);
        Prims.UpdateLods();
        Prims.ResidencyView = view;
        Avatars.BakeView = view;
        Presenter.Frame();
        Prims.UpdateBlendOrder();
    }

    private Viewer.Core.Scheduling.SlicedRelease<Rid>? _retiring;

    /// <summary>The region is dropped and its instances are being let go of a slice a frame (<see cref="RetireStep"/>).</summary>
    public bool Retiring => _retiring is { Done: false };

    /// <summary>
    /// Starts letting go of a dropped region: it stops taking changes and is no longer drawn as terrain, and its engine instances are handed
    /// to a list that <see cref="RetireStep"/> frees a slice a frame, so a region of many objects is not freed in one frame. Call
    /// <see cref="Dispose"/> when <see cref="RetireStep"/> reports it is done (or at once at shutdown; it frees what is left).
    /// </summary>
    public void BeginRetire()
    {
        if (_retiring is not null) return;
        Presenter.StopListening();
        Drawn = false;
        View.SetVisible(false);
        _retiring = new Viewer.Core.Scheduling.SlicedRelease<Rid>(Prims.TakeInstances(), Rids.Free);
    }

    /// <summary>Frees instances for up to <paramref name="budgetMs"/> of the clock <paramref name="nowMs"/>; true once none are left.</summary>
    public bool RetireStep(double budgetMs, System.Func<double> nowMs)
    {
        _retiring?.Step(budgetMs, nowMs);
        return _retiring is null || _retiring.Done;
    }

    public void Dispose()
    {
        _retiring?.ReleaseAll();   // what a slice has not freed yet goes before the meshes, materials and textures
        _retiring = null;
        Presenter.Dispose();
        Prims.Detach();   // after the presenter let its textures go: the hub keeps those other regions use
    }
}
