// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Buffers.Binary;

namespace Viewer.Shell.Sound;

/// <summary>A PCM WAV file's format and its sample bytes (little-endian, as the file holds them).</summary>
public sealed record WavData(int Rate, int Channels, int Bits, byte[] Pcm)
{
    /// <summary>How long it plays.</summary>
    public double Seconds => Rate <= 0 || Channels <= 0 || Bits <= 0 ? 0 : Pcm.Length / (double)(Rate * Channels * (Bits / 8));
}

/// <summary>Reads a PCM WAV file: the RIFF chunks "fmt " and "data". Anything else (another format, a broken header) gives null.</summary>
public static class WavFile
{
    public static WavData? Parse(byte[] bytes)
    {
        if (bytes.Length < 12 || !bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            return null;
        int rate = 0, channels = 0, bits = 0;
        bool format = false;
        for (int at = 12; at + 8 <= bytes.Length;)
        {
            var id = bytes.AsSpan(at, 4);
            int size = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at + 4, 4));
            int body = at + 8;
            if (size < 0 || body + size > bytes.Length)
                return null;
            if (id.SequenceEqual("fmt "u8) && size >= 16)
            {
                if (BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(body, 2)) != 1)
                    return null;   // PCM only
                channels = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(body + 2, 2));
                rate = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(body + 4, 4));
                bits = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(body + 14, 2));
                format = true;
            }
            else if (id.SequenceEqual("data"u8))
                return format && channels is 1 or 2 && bits is 8 or 16 && rate > 0 ? new WavData(rate, channels, bits, bytes.AsSpan(body, size).ToArray()) : null;
            at = body + size + (size & 1);
        }
        return null;
    }
}
