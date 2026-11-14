// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Viewer.Core.Meshing;
using Viewer.Core.World;

namespace Viewer.Shell;

/// <summary>
/// L4 "detail at a distance": from one camera (at <see cref="Camera"/>, looking east, 60 degrees), a 3 m textured wall
/// and a 3 m mesh sphere at each of 8, 24, 48 and 96 m, each distance in its own direction so nothing hides anything.
/// The walls carry a generated 512 x 512 texture, each wall under its own texture id, so each shows the level its own
/// distance wants; the sphere is the mesh zoo's (four levels of detail, 4 to 32 segments). The fixture
/// (fixtures/textures/detail-distance.texzoo.json) is this serialisation plus the image and mesh maps; the image is
/// encoded with LibreMetaverse's encoder (fixtures/textures/tex-detail-512.j2c).
/// </summary>
public static class DetailZoo
{
    public const int Size = 512;
    public const string ImageFile = "tex-detail-512.j2c";
    /// <summary>The camera's position (SL axes); it looks along +x.</summary>
    public static readonly Vector3 Camera = new(40, 128, 30);
    public static readonly float[] Distances = [8f, 24f, 48f, 96f];
    /// <summary>The bearing of each distance's pair: the wall at +bearing (north of the view's centre), the sphere at -bearing.</summary>
    public static readonly float[] Bearings = [32f, 16f, 8f, 2.5f];
    public static readonly Vector3 WallScale = new(0.2f, 3f, 3f), SphereScale = new(3f, 3f, 3f);
    public static Guid Texture(int i) => new($"4e560008-0000-4000-8000-00000000000{i + 1}");
    private static readonly Guid Sphere = new("4e560006-0000-4000-8000-000000000002");

    /// <summary>The source pixels (RGBA8, top row first): 8 px checks with a 1 px line every 16 px and four coloured
    /// quadrants, so each halving of the resolution shows.</summary>
    public static byte[] Pixels()
    {
        var b = new byte[Size * Size * 4];
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                int i = (y * Size + x) * 4;
                bool check = (x / 8 + y / 8) % 2 == 0;
                int q = (x < Size / 2 ? 0 : 1) + (y < Size / 2 ? 0 : 2);
                (byte r, byte g, byte bl) dark = q switch { 0 => (40, 60, 150), 1 => (150, 40, 40), 2 => (40, 120, 50), _ => (120, 90, 30) };
                var (r, g, bl) = check ? ((byte)235, (byte)235, (byte)235) : dark;
                if (x % 16 == 0 || y % 16 == 0) (r, g, bl) = ((byte)10, (byte)10, (byte)10);
                b[i] = r; b[i + 1] = g; b[i + 2] = bl; b[i + 3] = 255;
            }
        return b;
    }

    /// <summary>Where the pair at distance index <paramref name="i"/> stands: <paramref name="side"/> +1 the wall, -1 the sphere.</summary>
    public static Vector3 Position(int i, int side)
    {
        float a = side * Bearings[i] * MathF.PI / 180f;
        return Camera + new Vector3(MathF.Cos(a), MathF.Sin(a), 0) * Distances[i];
    }

    public static IReadOnlyList<ZooPrim> Make()
    {
        var prims = new List<ZooPrim>();
        var white = new FaceColors(Rgba.White);
        for (int i = 0; i < Distances.Length; i++)
        {
            prims.Add(new ZooPrim((uint)(7001 + i * 2), 0, $"wall-{Distances[i]}m", Position(i, 1), Quaternion.Identity, WallScale, PrimShape.Box, white,
                new FaceTextures(FaceTexture.Plain(Texture(i)))));
            prims.Add(new ZooPrim((uint)(7002 + i * 2), 0, $"sphere-{Distances[i]}m", Position(i, -1), Quaternion.Identity, SphereScale,
                PrimShape.Box with { SculptType = PrimShape.SculptTypeMesh, SculptId = Sphere }, new FaceColors(new Rgba(0.85f, 0.85f, 0.30f, 1))));
        }
        return prims;
    }

    public static string Serialise(IReadOnlyList<ZooPrim> prims)
    {
        var root = JsonNode.Parse(PrimZoo.Serialise(prims))!.AsObject();
        root["format"] = TextureZoo.Format;
        var images = new JsonObject();
        for (int i = 0; i < Distances.Length; i++) images[Texture(i).ToString()] = ImageFile;
        root["images"] = images;
        root["meshes"] = new JsonObject { [Sphere.ToString()] = "../meshes/" + MeshZoo.Assets[Sphere] };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n") + "\n";
    }
}
