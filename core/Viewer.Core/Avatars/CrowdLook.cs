// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later


using System.Security.Cryptography;
using System.Text;
using Viewer.Core.World;

namespace Viewer.Core.Avatars;

/// <summary>One server bake of a crowd avatar: its slot, the side of its square picture, the id it arrives under, which of the crowd's picture
/// variants it is (see <see cref="CrowdBakeImages"/>), and whether it is an alpha bake (every pixel transparent, hiding the system body's region).</summary>
public sealed record CrowdBake(string Slot, int Size, Guid Id, int Variant, bool Hides);

/// <summary>
/// One avatar of a realistic crowd (--tr-avatars=crowd:N,real): a shape (the male/female choice and a set of the shape editor's sliders),
/// its server bakes at the sizes server baking gives each slot, and whether it wears a mesh body. A mesh body names the bake placeholders
/// of <see cref="MeshBodySlots"/> on its faces (bake on mesh, so the body hides those system parts), and its alpha bakes
/// (<see cref="CrowdBake.Hides"/>) hide the system body's other regions. Everything comes from the seed and the avatar's number alone.
/// </summary>
public sealed record CrowdLook(int Index, bool Male, VisualParamSet Params, bool MeshBody, IReadOnlyList<CrowdBake> Bakes, IReadOnlySet<string> MeshBodySlots)
{
    /// <summary>The slots whose bake is an alpha bake.</summary>
    public IReadOnlySet<string> AlphaSlots => Bakes.Where(b => b.Hides).Select(b => b.Slot).ToHashSet();

    /// <summary>The body bake slots whose parts the system body does not draw for this avatar: the alpha bakes' and the mesh body's.</summary>
    public IReadOnlySet<string> HiddenSlots => AlphaSlots.Union(MeshBodySlots).ToHashSet();
}

/// <summary>Makes <see cref="CrowdLook"/>s. Pure: the same seed and number give the same look on any machine.</summary>
public static class CrowdLooks
{
    /// <summary>The shape editor sliders a crowd avatar varies (the group-0 parameters of the official avatar definition, by id), with their
    /// range as the file gives it. The wire byte maps linearly onto the range.</summary>
    public static readonly IReadOnlyList<(int Id, string Name, float Min, float Max, float Default)> Sliders =
    [
        (33, "Height", -2.3f, 2f, 0f), (36, "Shoulders", -1.8f, 1.4f, -0.5f), (38, "Torso Length", -1f, 1f, 0f), (105, "Breast Size", 0f, 1f, 0.5f),
        (155, "Lip Width", -0.9f, 1.3f, 0f), (157, "Belly Size", 0f, 1f, 0f), (196, "Eye Spacing", -2f, 1f, 0f), (515, "Foot Size", -1f, 3f, 0f),
        (637, "Body Fat", 0f, 1f, 0f), (649, "Torso Muscles", 0f, 1f, 0.5f), (652, "Leg Muscles", 0f, 1f, 0.5f), (675, "Hand Size", -0.3f, 0.3f, 0f),
        (676, "Love Handles", -1f, 2f, 0f), (682, "Head Size", 0f, 1f, 0.5f), (683, "Neck Thickness", -0.4f, 0.2f, -0.15f), (690, "Eye Size", 0f, 1f, 0.5f),
        (692, "Leg Length", -1f, 1f, 0f), (693, "Arm Length", -1f, 1f, 0.6f), (756, "Neck Length", -1f, 1f, 0f), (769, "Eye Depth", 0f, 1f, 0.5f),
        (795, "Butt Size", 0f, 1f, 0.25f),
    ];

    /// <summary>The side of each slot's server bake: the body regions and the skirt 1024, the hair 512, the eyes 256 (server baking gives no
    /// slot more than 1024).</summary>
    public static int BakeSize(string slot) => slot switch { "eyes" => 256, "hair" => 512, _ => 1024 };

    /// <summary>The slots every crowd avatar has a bake for.</summary>
    public static readonly IReadOnlyList<string> BakeSlots = ["head", "upper", "lower", "eyes", "hair"];

    /// <summary>The body slots a mesh body names on its faces (bake on mesh): its torso and legs; the head stays the system head unless an alpha bake hides it.</summary>
    public static readonly IReadOnlySet<string> MeshBodySlots = new HashSet<string> { "upper", "lower" };

    /// <summary>How many different pictures the crowd shares per slot (each avatar still has its own id, fetch, decode and upload).</summary>
    public const int PictureVariants = 4;

    public static CrowdLook Make(int seed, int index)
    {
        var rng = new Random(HashCode(seed, index, "look"));
        bool male = index % 2 == 1 ? rng.Next(100) < 70 : rng.Next(100) < 30;   // a mix, either sex in either parity
        bool mesh = rng.Next(100) < 50;
        var ids = new List<int> { VisualParamSet.MaleParamId };
        var values = new List<byte> { male ? (byte)255 : (byte)0 };
        foreach (var s in Sliders.OrderBy(s => s.Id))
        {
            if (s.Id == 105 && male) continue;   // no breast slider on a male shape
            ids.Add(s.Id);
            values.Add(SliderByte(rng, s, male));
        }
        // the wire carries the parameters in ascending id order
        var order = ids.Select((id, i) => (id, v: values[i])).OrderBy(x => x.id).ToArray();
        var parms = new VisualParamSet(order.Select(x => x.v), [.. order.Select(x => x.id)]);

        // a mesh body hides the head's region too for about a third of its wearers (an alpha bake on the head, no mesh head)
        bool alphaHead = mesh && rng.Next(3) == 0;
        var bakes = new List<CrowdBake>();
        foreach (var slot in BakeSlots)
        {
            bool hides = mesh && (slot is "upper" or "lower" || (slot == "head" && alphaHead));
            bakes.Add(new CrowdBake(slot, BakeSize(slot), BakeId(seed, index, slot), rng.Next(PictureVariants), hides));
        }
        return new CrowdLook(index, male, parms, mesh, bakes, mesh ? MeshBodySlots : new HashSet<string>());
    }

    /// <summary>The whole crowd's looks.</summary>
    public static CrowdLook[] MakeAll(int seed, int count) => Enumerable.Range(0, count).Select(i => Make(seed, i)).ToArray();

    private static byte SliderByte(Random rng, (int Id, string Name, float Min, float Max, float Default) s, bool male)
    {
        float span = s.Max - s.Min;
        float def = (s.Default - s.Min) / span;   // the default as a fraction of the range
        // most people sit near the default, some far from it: the sum of two draws, pulled toward the default
        float spread = s.Id is 33 or 637 or 649 or 652 or 157 ? 0.45f : 0.28f;
        float f = Math.Clamp(def + (float)((rng.NextDouble() + rng.NextDouble() - 1.0) * spread), 0f, 1f);
        if (s.Id == 637 && male) f = Math.Min(1f, f * 1.1f);
        return (byte)Math.Round(f * 255f);
    }

    private static int HashCode(int seed, int index, string what)
    {
        var h = SHA256.HashData(Encoding.UTF8.GetBytes($"crowd-look/{seed}/{index}/{what}"));
        return BitConverter.ToInt32(h, 0);
    }

    /// <summary>The id a bake arrives under: a version-4 id made from the seed, the avatar's number and the slot.</summary>
    public static Guid BakeId(int seed, int index, string slot)
    {
        var h = SHA256.HashData(Encoding.UTF8.GetBytes($"crowd-bake/{seed}/{index}/{slot}"));
        var b = h.AsSpan(0, 16).ToArray();
        b[7] = (byte)((b[7] & 0x0f) | 0x40);
        b[8] = (byte)((b[8] & 0x3f) | 0x80);
        return new Guid(b);
    }

    /// <summary>The appearance message for a look: the bakes on their texture-entry faces, the shape's wire bytes.</summary>
    public static AvatarAppearanceData Appearance(Guid agent, CrowdLook look, int version = 1)
    {
        var faces = new Guid[45];
        for (int bt = 0; bt < AppearanceSlots.Count; bt++)
            if (AppearanceSlots.SlotOf(bt) is { } slot && look.Bakes.FirstOrDefault(b => b.Slot == slot) is { } bake)
                faces[AppearanceSlots.FaceOf(bt)] = bake.Id;
        return new AvatarAppearanceData(agent, [.. faces], look.Params, version, version);
    }
}

/// <summary>
/// The pictures of a crowd's bakes, made at run time from the seed (no stored image, no one's capture): a smooth ramp with seeded noise and
/// blotches in the colour channels so the codestream carries real packet data at every level, an alpha channel (opaque, or all transparent for an
/// alpha bake), and a fifth plane as server bakes carry (the bump layer). Encoded as JPEG 2000 with the same encoder the other synthetic textures use.
/// </summary>
public static class CrowdBakeImages
{
    /// <summary>The quality layers of the pictures: five, at these bit rates, as the grid's textures have (one to seven were measured). The encoder's own default is about
    /// thirty thin layers, which the partial fetch does not estimate (<see cref="Viewer.Core.Imaging.TextureByteRange.MaxLayers"/>), so such a picture is fetched whole.</summary>
    public const string GridLayerRates = "0.0625 0.25 0.5 1.0 2.0";

    /// <summary>The bit rates of <paramref name="layers"/> quality layers, for a run that wants the layer count real bakes carry (a capture's server bakes declare 15 to 19):
    /// five is the grid's own list; any other count steps evenly (by ratio) from the first of those rates to the last.</summary>
    public static string RatesFor(int layers)
    {
        if (layers == 5) return GridLayerRates;
        if (layers < 1) throw new ArgumentOutOfRangeException(nameof(layers));
        if (layers == 1) return "2.0";
        double first = 0.0625, last = 2.0;
        var rates = new string[layers];
        for (int i = 0; i < layers; i++)
            rates[i] = (first * Math.Pow(last / first, i / (double)(layers - 1))).ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture);
        return string.Join(' ', rates);
    }

    /// <summary>The codestream for one picture: <paramref name="size"/> square, the variant's own noise, alpha all transparent when <paramref name="hides"/>.</summary>
    public static byte[] Make(int seed, string slot, int size, int variant, bool hides, bool thirtyLayers = false, int layers = 0)
    {
        var rng = new Random(unchecked(seed * 7919 + variant * 104729 + slot.Aggregate(17, (h, ch) => h * 31 + ch) * 31) ^ size);
        var planes = new byte[5][];
        for (int c = 0; c < 5; c++) planes[c] = new byte[size * size];
        int blots = 24;
        var blot = new (int X, int Y, int R, int C)[blots];
        for (int k = 0; k < blots; k++) blot[k] = (rng.Next(size), rng.Next(size), 8 + rng.Next(size / 8), rng.Next(3));
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int i = y * size + x;
                int[] v = [x * 200 / size + 30, y * 200 / size + 20, 120 + (x + y) * 60 / size];
                foreach (var b in blot)
                {
                    int dx = x - b.X, dy = y - b.Y;
                    if (dx * dx + dy * dy < b.R * b.R) v[b.C] += 50;
                }
                for (int c = 0; c < 3; c++) planes[c][i] = (byte)Math.Clamp(v[c] + rng.Next(-24, 25), 0, 255);
                planes[3][i] = hides ? (byte)0 : (byte)255;
                planes[4][i] = (byte)(128 + rng.Next(-16, 17));
            }
        return Protocol.LmvTexture.EncodePlanes(size, size, planes, layerRates: thirtyLayers ? null : layers > 0 ? RatesFor(layers) : GridLayerRates);
    }

    /// <summary>All of a crowd's pictures, made in parallel: for each slot, <see cref="CrowdLooks.PictureVariants"/> opaque variants, and the alpha variant when any look needs it.</summary>
    public static Dictionary<(string Slot, int Variant, bool Hides), byte[]> MakePool(int seed, IEnumerable<CrowdLook> looks, bool thirtyLayers = false, int layers = 0)
    {
        var wanted = looks.SelectMany(l => l.Bakes).Select(b => (b.Slot, b.Variant, b.Hides, b.Size)).Distinct().ToArray();
        var pool = new System.Collections.Concurrent.ConcurrentDictionary<(string, int, bool), byte[]>();
        Parallel.ForEach(wanted, w => pool[(w.Slot, w.Variant, w.Hides)] = Make(seed, w.Slot, w.Size, w.Variant, w.Hides, thirtyLayers, layers));
        return new Dictionary<(string, int, bool), byte[]>(pool);
    }

    /// <summary>The pictures of <see cref="MakePool(int, IEnumerable{CrowdLook})"/>, kept in <paramref name="cacheFolder"/> so a later run reads them instead of encoding them again.</summary>
    public static CrowdBakePool MakePool(int seed, IEnumerable<CrowdLook> looks, string? cacheFolder, bool thirtyLayers = false, int layers = 0)
    {
        if (cacheFolder is null)
        {
            var made = MakePool(seed, looks, thirtyLayers, layers);
            return new CrowdBakePool(made, made.Count, 0);
        }
        Directory.CreateDirectory(cacheFolder);
        var wanted = looks.SelectMany(l => l.Bakes).Select(b => (b.Slot, b.Variant, b.Hides, b.Size)).Distinct().ToArray();
        var pool = new System.Collections.Concurrent.ConcurrentDictionary<(string, int, bool), byte[]>();
        int encoded = 0, loaded = 0;
        Parallel.ForEach(wanted, w =>
        {
            string path = Path.Combine(cacheFolder, $"bake-v{PictureVersion}{(thirtyLayers ? "-L30" : layers > 0 ? $"-L{layers}" : "")}-{seed}-{w.Slot}-{w.Variant}-{(w.Hides ? "clear" : "opaque")}-{w.Size}.j2k");
            if (TryRead(path, out var bytes)) { Interlocked.Increment(ref loaded); }
            else
            {
                bytes = Make(seed, w.Slot, w.Size, w.Variant, w.Hides, thirtyLayers, layers);
                Interlocked.Increment(ref encoded);
                TryWrite(path, bytes);
            }
            pool[(w.Slot, w.Variant, w.Hides)] = bytes;
        });
        return new CrowdBakePool(new Dictionary<(string, int, bool), byte[]>(pool), encoded, loaded);
    }

    /// <summary>Part of every cached file's name: changes when <see cref="Make"/> makes a different picture for the same seed, so an older folder is not read.</summary>
    public const int PictureVersion = 1;

    // A cached file is its picture's length (8 bytes, little endian) and then the picture: a file cut short by an interrupted write is not taken.
    private static bool TryRead(string path, out byte[] bytes)
    {
        bytes = [];
        try
        {
            if (!File.Exists(path)) return false;
            var all = File.ReadAllBytes(path);
            if (all.Length < 8) return false;
            long length = BitConverter.ToInt64(all, 0);
            if (length <= 0 || length != all.Length - 8) return false;
            bytes = all[8..];
            return true;
        }
        catch (IOException) { return false; }
    }

    // Created, never replaced: a file already there (another run's) stays as it is.
    private static void TryWrite(string path, byte[] bytes)
    {
        try
        {
            using var f = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            f.Write(BitConverter.GetBytes((long)bytes.Length));
            f.Write(bytes);
        }
        catch (IOException) { }
    }
}

/// <summary>A crowd's bake pictures, and how many of them were encoded in this call and how many were read from the cache folder.</summary>
public sealed record CrowdBakePool(Dictionary<(string Slot, int Variant, bool Hides), byte[]> Pictures, int Encoded, int Loaded);
