// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using Godot;
using Viewer.Core.Session;
using Viewer.Godot.Presentation;
using Viewer.Shell.Travel;

namespace Viewer.Godot;

public partial class TestRender
{
    /// <summary>The ban-line scene (--tr-banlines=ban|list|pass): a parcel of 64 m by 64 m on the terrain fixture that refuses the agent, with the agent
    /// standing 4 m west of its edge, drawn as the wall the viewer shows (a ban is the tall wall; a refusal by list a 50 m one; a refusal by list where passes
    /// are sold, the green one).</summary>
    private string? _banLines;

    /// <summary>Where the agent stands (--tr-banagent=x,y, region metres); the wall fades with the distance from it.</summary>
    private System.Numerics.Vector2 _banAgent = new(92f, 96f);

    private void ParseBanAgent(string[] args)
    {
        if (Arg(args, "--tr-banagent=") is { } a && a.Split(',') is { Length: 2 } xy)
            _banAgent = new System.Numerics.Vector2(float.Parse(xy[0], System.Globalization.CultureInfo.InvariantCulture), float.Parse(xy[1], System.Globalization.CultureInfo.InvariantCulture));
    }

    private void AddBanWall(Rig rig, Viewer.Core.World.WorldState world)
    {
        if (_banLines is null)
            return;
        var bitmap = new byte[BanWall.CellsPerEdge * BanWall.CellsPerEdge / 8];
        for (int y = 16; y < 32; y++)   // cells 4 m wide: x 96..160 m, y 64..128 m
            for (int x = 24; x < 40; x++)
            {
                int bit = x + y * BanWall.CellsPerEdge;
                bitmap[bit / 8] |= (byte)(1 << (bit % 8));
            }
        bool hidden = _banLines.EndsWith("-hidden", StringComparison.Ordinal);   // the negative control: the setting is off
        string kind = hidden ? _banLines[..^"-hidden".Length] : _banLines;
        var denial = kind == "ban" ? ParcelDenial.Banned : ParcelDenial.NotOnList;
        var state = new BanLineState { Mode = hidden ? BanLineMode.Hide : BanLineMode.OnProximity };
        state.Collision(new ParcelCollision(denial, bitmap, UsePassList: kind == "pass"), 0);
        var wall = new BanWallLayer();
        rig.Root.AddChild(wall);
        wall.Update(state, _banAgent, (x, y) => TerrainHeight(world, x, y), 0.5);
        _result["banlines"] = new { kind = _banLines, pieces = wall.Pieces, shown = wall.Shown, height = BanWall.HeightFor(denial) };
        if (!hidden && (!wall.Shown || wall.Pieces == 0))
            _failures.Add("the ban wall was not drawn");
    }
}
