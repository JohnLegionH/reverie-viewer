// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using Godot;
using Viewer.Core.Session;
using Viewer.Shell.Travel;

namespace Viewer.Godot.Presentation;

/// <summary>
/// The wall drawn at the edge of a parcel that refuses the agent (<see cref="BanWall"/>): one translucent, double-sided,
/// unlit strip of caution-tape bands per 4 m of the parcel's outline, faded by the agent's distance, standing on the ground and
/// reaching up 50 m (5000 m for a ban). Rebuilt when the wall changes or the agent has moved; nothing is drawn while
/// <see cref="BanLineState.Drawing"/> says no. The texture and the immediate mesh are the engine's own; no Godot collection is made
/// here. Region coordinates in, the engine's axes out (x east, y up, z south). Main thread only.
/// </summary>
public sealed partial class BanWallLayer : Node3D
{
    private const float MoveToRebuild = 0.25f;

    private readonly ImmediateMesh _mesh = new();
    private readonly MeshInstance3D _instance;
    private readonly StandardMaterial3D _yellow;
    private readonly StandardMaterial3D _green;
    private byte[]? _drawnSegments;
    private bool _drawnPass;
    private System.Numerics.Vector2 _drawnAgent;

    public BanWallLayer()
    {
        _yellow = MakeMaterial(pass: false);
        _green = MakeMaterial(pass: true);
        _instance = new MeshInstance3D { Mesh = _mesh, Visible = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(_instance);
    }

    /// <summary>How many pieces of wall the last rebuild drew (for a check).</summary>
    public int Pieces { get; private set; }

    /// <summary>Where the region the wall belongs to stands from the anchor region (metres east and north); the wall is built in that region's own coordinates.</summary>
    public System.Numerics.Vector2 RegionOffset
    {
        get => _regionOffset;
        set
        {
            _regionOffset = value;
            Position = new Vector3(value.X, 0, -value.Y);
        }
    }
    private System.Numerics.Vector2 _regionOffset;

    public bool Shown => _instance.Visible;

    private static StandardMaterial3D MakeMaterial(bool pass)
    {
        var image = Image.CreateFromData(BanWall.TileSize, BanWall.TileSize, false, Image.Format.Rgba8, BanWall.Tile(pass));
        image.GenerateMipmaps();
        return new StandardMaterial3D
        {
            AlbedoTexture = ImageTexture.CreateFromImage(image),
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            VertexColorUseAsAlbedo = true,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmaps,
            TextureRepeat = true,
            DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
        };
    }

    /// <summary>Draws, redraws or hides the wall for this frame.</summary>
    /// <param name="agent">The agent's place in the region, metres.</param>
    /// <param name="ground">The ground height at a region place.</param>
    public void Update(BanLineState state, System.Numerics.Vector2 agent, Func<float, float, float> ground, double now, int cells = BanWall.CellsPerEdge)
    {
        if (!state.Drawing(now) || state.Segments is not { } segments || state.Denial is not { } denial)
        {
            if (_instance.Visible)
            {
                _mesh.ClearSurfaces();
                _instance.Visible = false;
                _drawnSegments = null;
                Pieces = 0;
            }
            return;
        }
        if (_instance.Visible && ReferenceEquals(_drawnSegments, segments) && _drawnPass == state.UsePass
            && System.Numerics.Vector2.Distance(agent, _drawnAgent) < MoveToRebuild)
            return;
        Build(segments, denial, state.UsePass, agent, ground, cells);
        _drawnSegments = segments;
        _drawnPass = state.UsePass;
        _drawnAgent = agent;
    }

    private void Build(byte[] segments, ParcelDenial denial, bool pass, System.Numerics.Vector2 agent, Func<float, float, float> ground, int cells)
    {
        _mesh.ClearSurfaces();
        var quads = BanWall.Quads(segments, cells, agent, ground, BanWall.HeightFor(denial));
        int drawn = 0;
        _mesh.SurfaceBegin(Mesh.PrimitiveType.Triangles, pass && denial == ParcelDenial.NotOnList ? _green : _yellow);
        foreach (var q in quads)
        {
            if (q.Alpha <= 0f) continue;
            // the texture runs along the wall the same way from both sides (the official viewer's coordinates, 2 m to a repeat)
            (float a1, float a2) = q.Facing switch
            {
                WallFacing.South => (q.X1, q.X2),
                WallFacing.North => (q.X2, q.X1),
                WallFacing.West => (q.Y1, q.Y2),
                _ => (q.Y2, q.Y1),
            };
            var colour = new Color(1f, 1f, 1f, q.Alpha);
            var bottom1 = new Vector3(q.X1, q.Z1, -q.Y1);
            var bottom2 = new Vector3(q.X2, q.Z2, -q.Y2);
            var top1 = new Vector3(q.X1, q.Top, -q.Y1);
            var top2 = new Vector3(q.X2, q.Top, -q.Y2);
            Vert(bottom1, a1, q.Z1, colour);
            Vert(bottom2, a2, q.Z2, colour);
            Vert(top2, a2, q.Top, colour);
            Vert(bottom1, a1, q.Z1, colour);
            Vert(top2, a2, q.Top, colour);
            Vert(top1, a1, q.Top, colour);
            drawn++;
        }
        _mesh.SurfaceEnd();
        Pieces = drawn;
        _instance.Visible = drawn > 0;
    }

    private void Vert(Vector3 position, float along, float height, Color colour)
    {
        _mesh.SurfaceSetColor(colour);
        _mesh.SurfaceSetUV(new Vector2(along * 0.5f + 0.5f, height * 0.5f));
        _mesh.SurfaceAddVertex(position);
    }

    /// <summary>The ground height from the region's patches (16 by 16 samples a patch), nearest sample; 0 where a patch has not arrived.</summary>
    public static float GroundFrom(Viewer.Core.World.WorldState world, float x, float y)
    {
        int sx = world.Region?.SizeX ?? 256, sy = world.Region?.SizeY ?? 256;
        int ix = Math.Clamp((int)x, 0, sx - 1), iy = Math.Clamp((int)y, 0, sy - 1);
        return world.TryGetPatch(ix / 16, iy / 16, out var h) ? h[(iy % 16) * 16 + ix % 16] : 0f;
    }
}
