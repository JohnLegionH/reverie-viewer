// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Globalization;
using System.Numerics;
using System.Xml;
using System.Xml.Linq;

namespace Viewer.Core.Building;

/// <summary>
/// Reads COLLADA 1.4 models (.dae) for upload, following the COLLADA 1.4.1 specification (Khronos): the visual scene's nodes with their
/// transforms (matrix, translate, rotate, scale), each instanced geometry's triangles, polylists and polygons (one face per material
/// symbol), and the effects' diffuse colour or texture image. The asset's unit scales positions; its up axis turns Y_UP and X_UP models to
/// the viewer's z up, as the official viewer's importer does. A controller (skin) is a rigged mesh and is refused. External entities and
/// DTDs are not read.
/// </summary>
public static class DaeReader
{
    public static ImportedModel Read(byte[] file, Func<string, byte[]?> sibling)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = ModelImport.MaxFileBytes };
        XDocument doc;
        using (var stream = new MemoryStream(file))
        using (var xr = XmlReader.Create(stream, settings))
            doc = XDocument.Load(xr);
        var root = doc.Root!;
        XNamespace ns = root.Name.Namespace;
        XName N(string local) => ns + local;

        if (root.Descendants(N("instance_controller")).Any() || root.Descendants(N("skin")).Any())
            return ImportedModel.Refused(ModelRefusals.Rigged);

        // the asset's unit and up axis
        var asset = root.Element(N("asset"));
        float unit = float.TryParse((string?)asset?.Element(N("unit"))?.Attribute("meter"), NumberStyles.Float, CultureInfo.InvariantCulture, out var u) && u > 0 ? u : 1f;
        string up = ((string?)asset?.Element(N("up_axis")) ?? "Y_UP").Trim();
        var axis = up switch
        {
            "Z_UP" => Matrix4x4.Identity,
            "X_UP" => Matrix4x4.CreateRotationY(MathF.PI / 2),
            _ => Matrix4x4.CreateRotationX(MathF.PI / 2),   // y up to z up: (x, y, z) -> (x, -z, y)
        };
        var rootTransform = Matrix4x4.CreateScale(unit) * axis;

        var byId = root.Descendants().Where(e => e.Attribute("id") is not null).GroupBy(e => (string)e.Attribute("id")!).ToDictionary(g => g.Key, g => g.First());
        XElement? Ref(string? url) => url is { Length: > 1 } && url[0] == '#' && byId.TryGetValue(url[1..], out var e) ? e : null;

        // images and materials
        var images = new List<ModelImage>();
        var imageIndex = new Dictionary<string, int>();
        int ImageOf(XElement image)
        {
            string id = (string?)image.Attribute("id") ?? "";
            if (imageIndex.TryGetValue(id, out var i)) return i;
            string path = ((string?)image.Descendants(N("init_from")).FirstOrDefault() ?? "").Trim();
            if (path.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) path = path[7..];
            path = Uri.UnescapeDataString(path).Replace('\\', '/');
            string name = path[(path.LastIndexOf('/') + 1)..];
            byte[]? bytes = name.Length == 0 || name.Contains("..") ? null : sibling(name);
            i = images.Count;
            images.Add(new ModelImage(Path.GetFileNameWithoutExtension(name), bytes ?? []));
            imageIndex[id] = i;
            return i;
        }
        var materials = new List<ModelMaterial>();
        var materialIndex = new Dictionary<string, int>();
        int MaterialOf(string? url)
        {
            if (url is null) return -1;
            if (materialIndex.TryGetValue(url, out var i)) return i;
            var mat = Ref(url);
            var effect = Ref((string?)mat?.Element(N("instance_effect"))?.Attribute("url"));
            var color = Vector4.One; int image = -1;
            var diffuse = effect?.Descendants(N("diffuse")).FirstOrDefault();
            if (diffuse?.Element(N("color")) is { } c && Floats(c.Value) is { Length: >= 3 } cv)
                color = new Vector4(cv[0], cv[1], cv[2], cv.Length > 3 ? cv[3] : 1f);
            if (diffuse?.Element(N("texture")) is { } tex)
            {
                // texture -> sampler2D newparam -> surface newparam -> image (COLLADA 1.4.1 profile_COMMON), or the image's id directly
                string sid = (string?)tex.Attribute("texture") ?? "";
                var newparams = effect!.Descendants(N("newparam")).ToDictionary(p => (string?)p.Attribute("sid") ?? "", p => p);
                string? surface = newparams.TryGetValue(sid, out var sp) ? ((string?)sp.Descendants(N("source")).FirstOrDefault())?.Trim() : null;
                string? imageId = surface is not null && newparams.TryGetValue(surface, out var sf) ? ((string?)sf.Descendants(N("init_from")).FirstOrDefault())?.Trim() : sid;
                if (imageId is not null && byId.TryGetValue(imageId, out var img) && img.Name == N("image"))
                {
                    image = ImageOf(img);
                    color = Vector4.One;   // a textured diffuse is drawn with the texture alone
                }
            }
            i = materials.Count;
            materials.Add(new ModelMaterial((string?)mat?.Attribute("name") ?? (string?)mat?.Attribute("id") ?? $"material{i}", color, image));
            materialIndex[url] = i;
            return i;
        }

        var meshes = new List<ModelMesh>();
        var scene = Ref((string?)root.Element(N("scene"))?.Element(N("instance_visual_scene"))?.Attribute("url"))
            ?? root.Element(N("library_visual_scenes"))?.Element(N("visual_scene"));
        if (scene is null)
            return ImportedModel.Refused(ModelRefusals.NoGeometry);

        void Visit(XElement node, Matrix4x4 parent, int depth)
        {
            if (depth > 64) return;
            var world = Local(node, ns) * parent;
            foreach (var ig in node.Elements(N("instance_geometry")))
            {
                var geometry = Ref((string?)ig.Attribute("url"));
                if (geometry?.Element(N("mesh")) is not { } mesh) continue;
                var bind = ig.Descendants(N("instance_material")).ToDictionary(m => (string?)m.Attribute("symbol") ?? "", m => (string?)m.Attribute("target"));
                var faces = ReadMesh(mesh, world, sym => MaterialOf(sym is not null && bind.TryGetValue(sym, out var t) ? t : null), ns, Ref);
                if (faces.Count > 0)
                    meshes.Add(new ModelMesh((string?)node.Attribute("name") ?? (string?)geometry.Attribute("name") ?? $"mesh{meshes.Count}", faces));
            }
            foreach (var child in node.Elements(N("node"))) Visit(child, world, depth + 1);
            foreach (var inst in node.Elements(N("instance_node")))
                if (Ref((string?)inst.Attribute("url")) is { } target) Visit(target, world, depth + 1);
        }
        foreach (var node in scene.Elements(N("node"))) Visit(node, rootTransform, 0);
        return new ImportedModel(meshes, materials, images);
    }

    private static float[] Floats(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(s => float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();

    private static int[] Ints(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(s => int.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture)).ToArray();

    /// <summary>A node's own transform: its elements applied in document order (COLLADA's column-vector matrices, read here as row vectors).</summary>
    private static Matrix4x4 Local(XElement node, XNamespace ns)
    {
        var m = Matrix4x4.Identity;
        foreach (var e in node.Elements())
        {
            Matrix4x4? step = e.Name.LocalName switch
            {
                "matrix" when Floats(e.Value) is { Length: 16 } v => Matrix4x4.Transpose(new Matrix4x4(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9], v[10], v[11], v[12], v[13], v[14], v[15])),
                "translate" when Floats(e.Value) is { Length: 3 } v => Matrix4x4.CreateTranslation(v[0], v[1], v[2]),
                "scale" when Floats(e.Value) is { Length: 3 } v => Matrix4x4.CreateScale(v[0], v[1], v[2]),
                "rotate" when Floats(e.Value) is { Length: 4 } v && new Vector3(v[0], v[1], v[2]).LengthSquared() > 0 =>
                    Matrix4x4.CreateFromAxisAngle(Vector3.Normalize(new Vector3(v[0], v[1], v[2])), v[3] * MathF.PI / 180f),
                _ => null,
            };
            // the later element applies first to the vertex (it is innermost), so with row vectors it goes on the left
            if (step is { } s) m = s * m;
        }
        return m;
    }

    private sealed class Source(float[] data, int stride)
    {
        public float At(int index, int component) => index * stride + component < data.Length ? data[index * stride + component] : 0f;
        public int Count => stride == 0 ? 0 : data.Length / stride;
    }

    private static List<ModelFace> ReadMesh(XElement mesh, Matrix4x4 world, Func<string?, int> material, XNamespace ns, Func<string?, XElement?> reference)
    {
        XName N(string local) => ns + local;
        Source? SourceOf(string? url)
        {
            var s = reference(url);
            if (s is null) return null;
            if (s.Name == N("vertices"))
                s = reference((string?)s.Elements(N("input")).FirstOrDefault(i => (string?)i.Attribute("semantic") == "POSITION")?.Attribute("source"));
            var arr = s?.Element(N("float_array"));
            if (arr is null) return null;
            int stride = (int?)s!.Element(N("technique_common"))?.Element(N("accessor"))?.Attribute("stride") ?? 3;
            return new Source(Floats(arr.Value), Math.Max(1, stride));
        }
        Matrix4x4.Invert(world, out var inv);
        var normalMatrix = Matrix4x4.Transpose(inv);
        bool flip = world.GetDeterminant() < 0;
        var groups = new Dictionary<int, (List<float> P, List<float> Nm, List<float> U, List<int> I, Dictionary<(int, int, int), int> Seen)>();
        var order = new List<int>();
        foreach (var prim in mesh.Elements().Where(e => e.Name.LocalName is "triangles" or "polylist" or "polygons"))
        {
            var inputs = prim.Elements(N("input")).Select(i => (Semantic: (string?)i.Attribute("semantic") ?? "", Source: (string?)i.Attribute("source"),
                Offset: (int?)i.Attribute("offset") ?? 0, Set: (int?)i.Attribute("set") ?? 0)).ToArray();
            if (inputs.Length == 0) continue;
            int width = inputs.Max(i => i.Offset) + 1;
            var vertex = inputs.FirstOrDefault(i => i.Semantic == "VERTEX");
            if (vertex.Source is null) continue;
            var positions = SourceOf(vertex.Source);
            var normalInput = inputs.FirstOrDefault(i => i.Semantic == "NORMAL");
            var normals = normalInput.Source is null ? null : SourceOf(normalInput.Source);
            var uvInput = inputs.Where(i => i.Semantic == "TEXCOORD").OrderBy(i => i.Set).FirstOrDefault();
            var uvs = uvInput.Source is null ? null : SourceOf(uvInput.Source);
            if (positions is null) continue;
            // the polygons as lists of index tuples
            var polygons = new List<int[]>();
            if (prim.Name.LocalName == "polygons")
                polygons.AddRange(prim.Elements(N("p")).Select(p => Ints(p.Value)));
            else
            {
                var p = Ints((string?)prim.Element(N("p")) ?? "");
                int[] counts = prim.Name.LocalName == "polylist" ? Ints((string?)prim.Element(N("vcount")) ?? "") : Enumerable.Repeat(3, p.Length / (3 * width)).ToArray();
                int at = 0;
                foreach (int c in counts)
                {
                    if (at + c * width > p.Length) break;
                    polygons.Add(p[at..(at + c * width)]);
                    at += c * width;
                }
            }
            int mat = material((string?)prim.Attribute("material"));
            if (!groups.TryGetValue(mat, out var g))
            {
                g = ([], [], [], [], []);
                groups[mat] = g;
                order.Add(mat);
            }
            int Vertex(int[] poly, int corner)
            {
                int pi = poly[corner * width + vertex.Offset];
                int ni = normalInput.Source is null ? -1 : poly[corner * width + normalInput.Offset];
                int ti = uvInput.Source is null ? -1 : poly[corner * width + uvInput.Offset];
                if (g.Seen.TryGetValue((pi, ni, ti), out var existing)) return existing;
                var pos = (Vector3.Transform(new Vector3(positions.At(pi, 0), positions.At(pi, 1), positions.At(pi, 2)), world));
                g.P.Add(pos.X); g.P.Add(pos.Y); g.P.Add(pos.Z);
                var n = normals is not null && ni >= 0 ? Vector3.TransformNormal(new Vector3(normals.At(ni, 0), normals.At(ni, 1), normals.At(ni, 2)), normalMatrix) : Vector3.UnitZ;
                n = n.LengthSquared() > 0 ? Vector3.Normalize(n) : Vector3.UnitZ;
                g.Nm.Add(n.X); g.Nm.Add(n.Y); g.Nm.Add(n.Z);
                g.U.Add(uvs is not null && ti >= 0 ? uvs.At(ti, 0) : 0f); g.U.Add(uvs is not null && ti >= 0 ? uvs.At(ti, 1) : 0f);
                int index = g.P.Count / 3 - 1;
                g.Seen[(pi, ni, ti)] = index;
                return index;
            }
            foreach (var poly in polygons)
            {
                int corners = poly.Length / width;
                if (poly.Any(i => i < 0)) continue;
                // a polygon is fanned from its first corner
                for (int k = 1; k + 1 < corners; k++)
                {
                    int a = Vertex(poly, 0), b = Vertex(poly, k), c = Vertex(poly, k + 1);
                    g.I.Add(a); g.I.Add(flip ? c : b); g.I.Add(flip ? b : c);
                }
            }
        }
        return order.Select(m => { var g = groups[m]; return new ModelFace(g.P.ToArray(), g.Nm.ToArray(), g.U.ToArray(), g.I.ToArray(), m); })
            .Where(f => f.Triangles > 0).ToList();
    }
}
