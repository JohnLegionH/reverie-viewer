// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System;
using System.Threading.Tasks;
using Godot;

namespace Viewer.Godot.Presentation;

/// <summary>
/// A picture of one avatar's outline: its body drawn flat white on black, with nothing else in the frame (no world, no worn objects, no
/// sky, no lighting, no post pass). The camera keeps its place and field of view, so the outline is the shape the body has on screen from
/// there, free of shading. Used by the running viewer (development builds) and by the render harness, so both pictures are made the same way.
/// Main thread.
/// </summary>
public static class BodyMask
{
    private static StandardMaterial3D? s_white;
    private static global::Godot.Environment? s_black;
    private static Compositor? s_plain;

    /// <summary>The flat material the body is drawn with: unshaded white, both sides.</summary>
    public static Material White => s_white ??= new StandardMaterial3D
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = Colors.White, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };

    /// <summary>
    /// Draws the avatar as a mask through <paramref name="setMask"/> (true when the avatar is drawn; it is called with null afterwards on every
    /// way out), lets <paramref name="place"/> move the camera once the mask is set (it gets the head joint as drawn), waits three drawn frames
    /// and reads the picture with <paramref name="read"/>. The camera's layers, environment and post pass are put back afterwards; the camera's
    /// place is left to the caller. Null when the avatar is not drawn.
    /// </summary>
    public static async Task<Image?> Capture(Node waiter, Camera3D camera, MaskSetter setMask, Action<System.Numerics.Matrix4x4>? place, Func<Image> read)
    {
        if (!setMask(White, out var head)) return null;
        uint cull = camera.CullMask;
        var env = camera.Environment;
        var compositor = camera.Compositor;
        try
        {
            place?.Invoke(head);
            camera.CullMask = AvatarLayer.MaskLayer;
            camera.Environment = s_black ??= new global::Godot.Environment
            {
                BackgroundMode = global::Godot.Environment.BGMode.Color, BackgroundColor = Colors.Black, TonemapMode = global::Godot.Environment.ToneMapper.Linear,
            };
            camera.Compositor = s_plain ??= new Compositor();
            for (int i = 0; i < 3; i++)
                await waiter.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            var img = read();
            img.Convert(Image.Format.Rgba8);
            return img;
        }
        finally
        {
            camera.CullMask = cull;
            camera.Environment = env;
            camera.Compositor = compositor;
            setMask(null, out _);
        }
    }

    /// <summary>Sets or clears an avatar's mask drawing and gives its head joint's place (SL axes) as drawn.</summary>
    public delegate bool MaskSetter(Material? mask, out System.Numerics.Matrix4x4 head);

    /// <summary>A point in SL axes (x east, y north, z up) as the engine places it.</summary>
    public static Vector3 ToEngine(System.Numerics.Vector3 p) => new(p.X, p.Z, -p.Y);

    /// <summary>A point as the engine places it, in SL axes.</summary>
    public static System.Numerics.Vector3 FromEngine(Vector3 p) => new(p.X, -p.Z, p.Y);
}
