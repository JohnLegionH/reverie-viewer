// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using LibreMetaverse;
using LibreMetaverse.Assets;
using LibreMetaverse.Imaging;
using Viewer.Core.Imaging;

namespace Viewer.Core.Protocol;

public sealed record TextureDecodeResult(DecodedTexture? Texture, string? Refusal)
{
    public bool Ok => Texture is not null;
}

/// <summary>
/// Texture decode with LibreMetaverse's own path (AssetTexture.Decode, which uses CoreJ2K, the managed
/// JJ2000 port; the version the viewer references directly, see Directory.Packages.props), the only place those types are touched (ADR-005). Every codestream passes <see cref="J2kGuard"/>
/// first; the decoded image is checked against the header (size, finite channel data). Output RGBA8 + alpha class.
/// Memory is bounded by the header limits (2048 x 2048 x 5 components); time is bounded by those sizes and measured by
/// the fuzz tests (a running decode cannot be aborted in .NET, so time is bounded by refusing large work up front).
/// Any exception is a refusal. Discard levels: CoreJ2K's decoder configuration supports a resolution level
/// (WithResolutionLevel) and a byte budget (WithDecodingBytes), which the partial fetch can use; not used here.
/// </summary>
public static class LmvTexture
{
    // One thread per decode, as CoreJ2K 2.3 decoded: the viewer's decode threads already run several decodes at once, and
    // CoreJ2K 2.4's default (a worker for every core inside each decode) raised the collector pauses. The library-wide
    // default covers the decodes LibreMetaverse's texture class makes; DecoderConfiguration sets it per call as well.
    static LmvTexture() => CoreJ2K.J2kImage.DefaultMaxDegreeOfParallelism = 1;

    /// <param name="pool">: where the RGBA buffer comes from (null: a new array, as before).</param>
    public static TextureDecodeResult Decode(byte[] codestream, J2kLimits? limits = null, PixelPool? pool = null)
    {
        var why = J2kGuard.Validate(codestream, limits, out var header);
        if (why is not null)
            return new(null, why);
        // more than 4 components (an avatar bake: R, G, B, A and the morph mask). LibreMetaverse
        // maps the fifth into alpha (measured), so these decode through CoreJ2K directly and keep components 0-3, as the
        // official viewer does for the image. 4 components or fewer keep LibreMetaverse's decode, unchanged.
        if (header!.Components > 4)
            return DecodeComponents(codestream, header, 0, pool);
        try
        {
            var t = new AssetTexture(UUID.Zero, codestream);
            if (!t.Decode() || t.Image is null)
                return new(null, "decoder refused the codestream");
            var img = t.Image;
            if (img.Width != header!.Width || img.Height != header.Height)
                return new(null, $"decoded {img.Width}x{img.Height}, header said {header.Width}x{header.Height}");
            int n = img.Width * img.Height;
            var rgba = pool?.Rent(n * 4) ?? new byte[n * 4];   // every byte is written below
            bool color = (img.Channels & ManagedImage.ImageChannels.Color) != 0;
            bool gray = (img.Channels & ManagedImage.ImageChannels.Gray) != 0;
            // Alpha only when the codestream carries it: LibreMetaverse reports an Alpha channel (filled with 254) for
            // 3-component images too (measured), so the channel flag cannot be trusted.
            bool alpha = (img.Channels & ManagedImage.ImageChannels.Alpha) != 0 && header.Components >= 4 && img.Alpha is not null;
            for (int i = 0; i < n; i++)
            {
                byte r = color ? img.Red[i] : gray ? img.Red[i] : (byte)0;
                rgba[i * 4] = r;
                rgba[i * 4 + 1] = color ? img.Green[i] : r;
                rgba[i * 4 + 2] = color ? img.Blue[i] : r;
                rgba[i * 4 + 3] = alpha ? img.Alpha![i] : (byte)255;
            }
            var alphaClass = DecodedTexture.Classify(rgba);
            return new(new DecodedTexture(img.Width, img.Height, rgba, alphaClass, header.Components) { Pool = pool, IsAlphaMask = DecodedTexture.AnalyzeMask(alphaClass, rgba, img.Width, img.Height) }, null);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return new(null, "decoder failed: " + e.GetType().Name);
        }
    }

    /// <summary>
    /// Decodes discard level <paramref name="level"/> (0 = full resolution; clamped to the codestream's coarsest,
    /// so <see cref="int.MaxValue"/> asks for the coarsest) through the same guard. Level 0 is <see cref="Decode"/>
    /// itself; a lower level uses CoreJ2K's reduced-resolution decode of the whole codestream (it skips the finer wavelet
    /// levels, so it costs far less), with the same channel rules. The result carries its level, the level count and
    /// the full size, and with <paramref name="mipmaps"/> its mipmap chain, built here, off the caller's main thread.
    /// Components deeper than 8 bits have no lower levels here (level 0 only).
    /// Every layer count and tiling takes the reduced decode: the decoder's work and memory follow the level asked for
    /// (the picture returned, not the whole picture), and the pixels are the same on every decode.
    /// The level's RGBA and its mip chain come from <paramref name="pool"/> (default <see cref="PixelPool.Shared"/>), and
    /// the caller hands them back with
    /// <see cref="DecodedTexture.ReleasePixels"/> once nothing reads them (the viewer: once its engine image exists).
    /// With <paramref name="prefix"/> the bytes are the start of a longer codestream (a partial fetch):
    /// the guard accepts it when only the tail is missing (<see cref="J2kGuard"/>), and CoreJ2K decodes the reduced level
    /// from what is there. Full resolution (level 0) is never decoded from a prefix: it needs the whole file.
    /// </summary>
    public static TextureDecodeResult DecodeLevel(byte[] codestream, int level, bool mipmaps = false, J2kLimits? limits = null, bool prefix = false, PixelPool? pool = null)
    {
        if (prefix) QuietTruncationWarnings.Install();
        pool ??= PixelPool.Shared;
        var why = J2kGuard.Validate(codestream, limits, out var header, allowTruncated: prefix);
        if (why is not null)
            return new(null, why);
        int count = header!.MaxBitDepth > 8 ? 1 : TextureLevels.Count(header.Width, header.Height, header.DecompositionLevels);
        level = Math.Clamp(level, 0, count - 1);
        if (prefix && level == 0)
            return new(null, "a partial codestream cannot give full resolution");
        DecodedTexture? t;
        if (level == 0)
        {
            var full = Decode(codestream, limits, pool);
            if (!full.Ok) return full;
            t = full.Texture!;
        }
        else
        {
            var r = DecodeComponents(codestream, header, level, pool);
            if (!r.Ok) return r;
            t = r.Texture!;
        }
        return new(t with
        {
            Level = level, LevelCount = count, FullWidth = header.Width, FullHeight = header.Height,
            Mips = mipmaps ? MipChain.Build(t.Width, t.Height, t.Rgba, pool) : null, Pool = pool,
        }, null);
    }

    /// <summary>
    /// CoreJ2K's decode at <paramref name="level"/> (0: full resolution; above 0 its reduced-resolution decode), the image
    /// from components 0-3 (grey, RGB, RGBA; a fifth component, a bake's morph mask, is not part of the image), 8-bit
    /// components only. Alpha only when the codestream carries it, as <see cref="Decode"/>.
    /// </summary>
    private static TextureDecodeResult DecodeComponents(byte[] codestream, J2kHeader header, int level, PixelPool? pool)
    {
        try
        {
            var cfg = DecoderConfiguration(header.DecompositionLevels, level);
            using var img = CoreJ2K.J2kImage.FromBytes(codestream, cfg);
            int w = TextureLevels.Size(header.Width, level), h = TextureLevels.Size(header.Height, level);
            if (img.Width != w || img.Height != h)
                return new(null, $"level {level} decoded {img.Width}x{img.Height}, expected {w}x{h}");
            int nc = img.NumberOfComponents;
            if (nc != header.Components || Enumerable.Range(0, nc).Any(c => img.GetBitDepth(c) != 8))
                return new(null, level > 0 ? "reduced decode: unexpected components" : "decode: unexpected components");
            int n = w * h;
            var planes = Enumerable.Range(0, Math.Min(nc, 4)).Select(c => img.GetComponentBytes(c)).ToArray();
            var rgba = pool?.Rent(n * 4) ?? new byte[n * 4];   // every byte is written below
            bool color = nc >= 3, alpha = nc >= 4;
            for (int i = 0; i < n; i++)
            {
                byte r = planes[0][i];
                rgba[i * 4] = r;
                rgba[i * 4 + 1] = color ? planes[1][i] : r;
                rgba[i * 4 + 2] = color ? planes[2][i] : r;
                rgba[i * 4 + 3] = alpha ? planes[3][i] : (byte)255;
            }
            var alphaClass = DecodedTexture.Classify(rgba);
            return new(new DecodedTexture(w, h, rgba, alphaClass, header.Components) { Pool = pool, IsAlphaMask = DecodedTexture.AnalyzeMask(alphaClass, rgba, w, h) }, null);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return new(null, "decoder failed: " + e.GetType().Name);
        }
    }

    /// <summary>The decoder settings for one decode: the resolution level asked for (0 is the full picture), and the number of
    /// threads one decode may use.</summary>
    internal static CoreJ2K.Configuration.J2KDecoderConfiguration DecoderConfiguration(int decompositionLevels, int level)
    {
        var cfg = new CoreJ2K.Configuration.J2KDecoderConfiguration().WithMaxDegreeOfParallelism(1);
        if (level > 0) cfg = cfg.WithResolutionLevel(decompositionLevels - level);
        return cfg;
    }

    /// <summary>
    /// CoreJ2K prints a "Codestream truncated" warning to the console for every decode of a prefix, which is
    /// expected here (a partial fetch). Its process-wide message logger is wrapped once: that warning is dropped, every
    /// other message goes to the logger that was there before, unchanged.
    /// </summary>
    private sealed class QuietTruncationWarnings(CoreJ2K.j2k.util.IMsgLogger inner) : CoreJ2K.j2k.util.IMsgLogger
    {
        private static int s_installed;

        public static void Install()
        {
            if (Interlocked.Exchange(ref s_installed, 1) == 0)
                CoreJ2K.j2k.util.FacilityManager.DefaultMsgLogger = new QuietTruncationWarnings(CoreJ2K.j2k.util.FacilityManager.GetMsgLogger());
        }

        public void printmsg(int sev, string msg)
        {
            if (msg is not null && msg.StartsWith("Codestream truncated", StringComparison.Ordinal)) return;
            inner.printmsg(sev, msg!);
        }

        public void println(string str, int flind, int ind) => inner.println(str, flind, ind);

        public void flush() => inner.flush();
    }

    /// <summary>
    /// A LOSSLESS codestream of an RGB image (alpha ignored), with CoreJ2K's reversible configuration (5/3
    /// wavelet, no quantisation), for test fixtures whose exact pixels matter (sculpt maps, the colour chart). The
    /// pixels go in as an in-memory binary PPM, CoreJ2K's own raw input.
    /// </summary>
    public static byte[] EncodeLossless(int width, int height, byte[] rgba)
    {
        var header = System.Text.Encoding.ASCII.GetBytes($"P6\n{width} {height}\n255\n");
        var ppm = new byte[header.Length + width * height * 3];
        header.CopyTo(ppm, 0);
        for (int i = 0; i < width * height; i++)
        {
            ppm[header.Length + i * 3] = rgba[i * 4];
            ppm[header.Length + i * 3 + 1] = rgba[i * 4 + 1];
            ppm[header.Length + i * 3 + 2] = rgba[i * 4 + 2];
        }
        using var input = new MemoryStream(ppm);
        var source = CoreJ2K.J2kImage.CreateEncodableSource(input) ?? throw new InvalidDataException("CoreJ2K: no encodable source");
        var parameters = new CoreJ2K.Configuration.J2KEncoderConfiguration().WithLossless().WithFileFormat(false).ToParameterList();
        return CoreJ2K.J2kImage.ToBytes(source, parameters);
    }

    /// <summary>
    /// A LOSSY codestream of an RGB image (alpha ignored) with a few quality layers at the given bit rates
    /// (bits per pixel, e.g. "0.0625 0.25 0.5 1.0 2.0"), layer-progressive (LRCP) like the grid's textures, for test
    /// fixtures of partial fetch (our own content only). CoreJ2K's default of about 30 thin layers is not what the grid's
    /// textures look like (1-7 layers measured).
    /// </summary>
    public static byte[] EncodeLayered(int width, int height, byte[] rgba, string layerRates, double rate)
    {
        var header = System.Text.Encoding.ASCII.GetBytes($"P6\n{width} {height}\n255\n");
        var ppm = new byte[header.Length + width * height * 3];
        header.CopyTo(ppm, 0);
        for (int i = 0; i < width * height; i++)
        {
            ppm[header.Length + i * 3] = rgba[i * 4];
            ppm[header.Length + i * 3 + 1] = rgba[i * 4 + 1];
            ppm[header.Length + i * 3 + 2] = rgba[i * 4 + 2];
        }
        using var input = new MemoryStream(ppm);
        var source = CoreJ2K.J2kImage.CreateEncodableSource(input) ?? throw new InvalidDataException("CoreJ2K: no encodable source");
        var parameters = new CoreJ2K.Configuration.J2KEncoderConfiguration().WithFileFormat(false).ToParameterList();
        parameters["Alayers"] = layerRates;
        parameters["rate"] = rate.ToString(System.Globalization.CultureInfo.InvariantCulture);
        parameters["Aptype"] = "layer";
        return CoreJ2K.J2kImage.ToBytes(source, parameters);
    }

    /// <summary>
    /// A LOSSY codestream with one 8-bit component per plane (test fixtures only, our own content),
    /// shaped like the grid's server-side bakes (Tranquillity's SSB, read for behaviour only): one tile, the 9/7 wavelet,
    /// resolution-major (RPCL) order, CoreJ2K's default quality layers at a quality target. Five planes are R, G, B, A and
    /// the morph mask. Each plane goes in as a greyscale PGM, CoreJ2K's raw input for a multi-component source.
    /// </summary>
    public static byte[] EncodePlanes(int width, int height, IReadOnlyList<byte[]> planes, double quality = 0.85, string? layerRates = null, double rate = 2.0)
    {
        var header = System.Text.Encoding.ASCII.GetBytes($"P5\n{width} {height}\n255\n");
        var streams = planes.Select(p =>
        {
            if (p.Length != width * height) throw new ArgumentException("plane size does not match the image");
            var ms = new MemoryStream(header.Length + p.Length);
            ms.Write(header); ms.Write(p); ms.Position = 0;
            return (Stream)ms;
        }).ToList();
        try
        {
            var source = CoreJ2K.J2kImage.CreateEncodableSource(streams) ?? throw new InvalidDataException("CoreJ2K: no encodable source");
            var cfg = new CoreJ2K.Configuration.J2KEncoderConfiguration().WithQuality(quality)
                .WithTiles(t => t.SetSize(width, height))
                .WithWavelet(w => { w.UseIrreversible97(); w.WithDecompositionLevels(Math.Clamp((int)Math.Log2(Math.Min(width, height)) - 2, 1, 7)); })
                .WithProgression(p => p.WithOrder(CoreJ2K.Configuration.ProgressionOrder.RPCL))
                .WithFileFormat(false);
            if (layerRates is null) return CoreJ2K.J2kImage.ToBytes(source, cfg);
            // a few layers at the given bit rates, as the grid's textures have (1-7 measured); CoreJ2K's default is about 30 thin ones
            var parameters = cfg.ToParameterList();
            parameters["Alayers"] = layerRates;
            parameters["rate"] = rate.ToString(System.Globalization.CultureInfo.InvariantCulture);
            parameters["Aptype"] = "layer";
            return CoreJ2K.J2kImage.ToBytes(source, parameters);
        }
        finally { foreach (var st in streams) st.Dispose(); }
    }

    /// <summary>The compression an upload uses: the official viewer's default rate of one eighth of the raw size, so one bit per pixel
    /// per 8-bit component (three for RGB, four with alpha).</summary>
    public const double UploadBitsPerComponent = 1.0;

    /// <summary>The smallest byte budget an upload's codestream is given (room for its headers).</summary>
    public const int UploadMinBytes = 1024;

    /// <summary>
    /// An uploaded texture's codestream, as the official viewer encodes one by default (llviewertexturelist.cpp convertToUploadFile:
    /// lossy, the 9/7 wavelet, at the default rate; behaviour only): CoreJ2K with one plane per component, the alpha plane only when the
    /// image has transparency.
    /// </summary>
    public static byte[] EncodeUpload(int width, int height, byte[] rgba, bool withAlpha)
    {
        int components = withAlpha ? 4 : 3;
        var planes = new byte[components][];
        for (int c = 0; c < components; c++)
        {
            var plane = new byte[width * height];
            for (int i = 0; i < plane.Length; i++)
                plane[i] = rgba[i * 4 + c];
            planes[c] = plane;
        }
        // a small image cannot be held at one eighth of its size: the codestream's own headers take more than that, and the encoder refuses
        // such a target. It gets at least UploadMinBytes; one whose full precision fits in that is stored losslessly.
        if ((long)width * height * components <= UploadMinBytes)
            return EncodePlanesLossless(width, height, planes);
        double rate = Math.Min(components * 8.0, Math.Max(components * UploadBitsPerComponent, UploadMinBytes * 8.0 / ((double)width * height)));
        // a few quality layers below the full rate, so a partial fetch already shows the picture
        string layers = string.Join(' ', new[] { rate / 16, rate / 4 }.Select(r => r.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return EncodePlanes(width, height, planes, layerRates: layers, rate: rate);
    }

    /// <summary>A lossless codestream with one 8-bit component per plane (CoreJ2K's reversible configuration).</summary>
    private static byte[] EncodePlanesLossless(int width, int height, IReadOnlyList<byte[]> planes)
    {
        var header = System.Text.Encoding.ASCII.GetBytes($"P5\n{width} {height}\n255\n");
        var streams = planes.Select(p =>
        {
            var ms = new MemoryStream(header.Length + p.Length);
            ms.Write(header); ms.Write(p); ms.Position = 0;
            return (Stream)ms;
        }).ToList();
        try
        {
            var source = CoreJ2K.J2kImage.CreateEncodableSource(streams) ?? throw new InvalidDataException("CoreJ2K: no encodable source");
            var cfg = new CoreJ2K.Configuration.J2KEncoderConfiguration().WithLossless()
                .WithWavelet(w => w.WithDecompositionLevels(Math.Clamp((int)Math.Log2(Math.Min(width, height)) - 1, 0, 5)))
                .WithFileFormat(false);
            return CoreJ2K.J2kImage.ToBytes(source, cfg);
        }
        finally { foreach (var st in streams) st.Dispose(); }
    }

    /// <summary>Encodes an RGBA8 image with LibreMetaverse's own encoder (tests and fixtures: our own content only).</summary>
    public static byte[] Encode(int width, int height, byte[] rgba, bool withAlpha)
    {
        var img = new ManagedImage(width, height, ManagedImage.ImageChannels.Color | (withAlpha ? ManagedImage.ImageChannels.Alpha : 0));
        for (int i = 0; i < width * height; i++)
        {
            img.Red[i] = rgba[i * 4];
            img.Green[i] = rgba[i * 4 + 1];
            img.Blue[i] = rgba[i * 4 + 2];
            if (withAlpha) img.Alpha[i] = rgba[i * 4 + 3];
        }
        var t = new AssetTexture(img);
        t.Encode();
        return t.AssetData;
    }
}
