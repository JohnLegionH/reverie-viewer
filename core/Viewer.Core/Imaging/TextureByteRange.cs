// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Imaging;

/// <summary>
/// How many bytes of a texture's codestream to ask for.
/// SL textures are LRCP codestreams (quality layers first) with no packet-length markers (measured: every cached
/// codestream), so the bytes a level needs cannot be read from the file; any prefix decodes, at lower quality. The
/// estimate is therefore the official viewer's behaviour, which the grid's encoders are made for, implemented here from
/// our own description of it (section 8.1): a first request of <see cref="FirstChunk"/> bytes, then for discard level L
/// a size that depends only on the image's longest side and L. Measured over 261 level decodes (the texture zoo and
/// the locally cached real textures): never too few bytes (every level within the decode tolerance), about half the bytes
/// of whole files; see section 8.3.
/// </summary>
public static class TextureByteRange
{
    /// <summary>The first request, before the image's size is known: the official viewer's first packet size (600 bytes),
    /// which holds the main header of every codestream measured and often the coarsest level.</summary>
    public const int FirstChunk = 600;

    /// <summary>The most quality layers a codestream may declare for the estimate to be used. The estimate follows the
    /// official encoder's layer layout (a handful of layers, each about one discard level); every real texture measured
    /// declares 1-7 layers and decodes within tolerance at the estimate. LibreMetaverse's own encoder writes 27 thin
    /// layers, and its prefixes at the estimate come out far from the whole file (measured: mean difference 54-66), so a
    /// codestream with more layers than this is fetched whole (the behaviour before partial fetch).</summary>
    public const int MaxLayers = 8;

    /// <summary>The image size the first request assumes before the header is known: 1024x1024, the size of most
    /// of the grid's textures measured and the largest an ordinary upload makes.</summary>
    public const int AssumedSide = 1024;

    /// <summary>The bytes of a texture's first request for discard
    /// level <paramref name="level"/>, before its header is known; null for the whole file (level 0). The level's own
    /// estimate for a <see cref="AssumedSide"/>-square texture, so the first answer usually holds the level and the first
    /// image needs one request instead of two (600 bytes, then the level: two of the grid's ~105 ms waits). A
    /// smaller file comes back whole (the server stops at its end); a larger image, or a codestream the layer rule fetches
    /// whole, asks for the rest from where this stopped, as before. Measured (region A v5, 105 ms per answer): 60 of 60
    /// textures in one request against 9 of 60, the same bytes.</summary>
    public static long? FirstRequest(int level) => BytesFor(AssumedSide, AssumedSide, level);

    /// <summary>As <see cref="BytesFor(int, int, int)"/> for a codestream whose header is known: null (the whole file) when
    /// it declares more than <see cref="MaxLayers"/> quality layers.</summary>
    public static long? BytesFor(J2kHeader header, int level) =>
        header.Layers > MaxLayers ? null : BytesFor(header.Width, header.Height, level);

    /// <summary>The bytes to ask for to show level <paramref name="level"/>, header included; null for the whole file
    /// (level 0: full resolution always takes the whole file).</summary>
    public static long? BytesFor(int width, int height, int level)
    {
        if (level <= 0) return null;
        // Behaviour spec (section 8.1): the image is modelled as a base layer of 2,000 bytes per component (4 components
        // at 8 bits assumed) plus one layer per power of two between a 64-pixel code block and the longest side (at
        // least 4 layers), each layer's area four times the last, the first being the block area times the layer count,
        // at a rate of 1/8 of the raw bits. Level L takes the layers up to (5 - L); then 600 bytes of header are added.
        int longest = Math.Max(width > 0 ? width : 2048, height > 0 ? height : 2048);
        int layers = Math.Max((int)Math.Round(Math.Log2(longest) - 6, MidpointRounding.AwayFromZero), 4);
        long area = 64L * 64 * layers;
        long bits = 2000L * 4 * 8;
        for (int layer = 0; layer <= layers; layer++, area *= 4)
            if (layer <= 5 - level)
                bits += area * 4 * 8 / 8;   // area x 4 components x 8 bits x rate 1/8
        return bits / 8 + FirstChunk;
    }
}
