// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using Godot;
using Viewer.Core.Diagnostics;

namespace Viewer.Godot;

public partial class Main
{
    // NV_FRAME_BURST=1 (development builds only): on a trigger (a region crossing, or the automation command "frame_burst") every frame drawn for
    // the next FrameBurstSeconds is read back, at half size, and saved as a PNG under NV_LOG_DIR, named with the engine frame number it shows. The
    // frame numbers are written to the log (frame_burst.start, frame_burst.frame, frame_burst.end, frame_burst.saved), so a picture is matched to the
    // worn.* trace lines. The pictures are held as plain bytes while the burst runs and written by a worker afterwards (no engine object leaves the
    // main thread), so the burst does not slow the frames after it.
    private static readonly bool FrameBurstOn = System.Environment.GetEnvironmentVariable("NV_FRAME_BURST") == "1" && OS.IsDebugBuild();
    private const double FrameBurstSeconds = 1.5;
    private const int FrameBurstMaxFrames = 120;
    private double _burstUntil;
    private int _burstSerial;
    private string _burstTag = "";
    private string _burstDir = "";
    private bool _burstHooked;
    private bool _burstOffSaid;
    private Task _burstWriting = Task.CompletedTask;
    private Texture2D? _burstTexture;
    private readonly List<(long Frame, int Width, int Height, int Channels, byte[] Pixels)> _burstFrames = new();

    /// <summary>Starts a burst unless one is running. Returns the folder the pictures go to, or null when the aid is off.</summary>
    internal string? FrameBurstStart(string why)
    {
        if (!FrameBurstOn) return null;
        double now = Time.GetTicksMsec() / 1000.0;
        if (now < _burstUntil || !_burstWriting.IsCompleted) return null;
        // pictures go only to the folder the log-folder variable names, never to the viewer's own per-user folders
        string? logDir = FrameBurstTarget.Resolve(System.Environment.GetEnvironmentVariable("NV_LOG_DIR"),
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), out string offReason);
        if (logDir is not null) logDir = FrameBurstTarget.Resolve(logDir, OS.GetUserDataDir(), out offReason);
        if (logDir is null)
        {
            if (!_burstOffSaid) { _burstOffSaid = true; Log.Info("frame_burst.off", CoreLog.F("reason", offReason)); }
            return null;
        }
        _burstTag = $"{++_burstSerial:000}-{why}";
        _burstDir = Path.Combine(logDir, "frame-burst", _burstTag);
        Directory.CreateDirectory(_burstDir);
        _burstUntil = now + FrameBurstSeconds;
        _burstFrames.Clear();
        if (!_burstHooked)
        {
            RenderingServer.Singleton.FramePostDraw += FrameBurstCapture;
            _burstHooked = true;
        }
        Log.Info("frame_burst.start", CoreLog.F("tag", _burstTag), CoreLog.F("frame", (long)Engine.GetProcessFrames()), CoreLog.F("dir", _burstDir));
        return _burstDir;
    }

    private void FrameBurstCapture()
    {
        double now = Time.GetTicksMsec() / 1000.0;
        if (now >= _burstUntil || _burstFrames.Count >= FrameBurstMaxFrames) return;
        _burstTexture ??= GetViewport().GetTexture();
        using var picture = _burstTexture.GetImage();
        picture.Resize(Math.Max(1, picture.GetWidth() / 2), Math.Max(1, picture.GetHeight() / 2), Image.Interpolation.Bilinear);
        int channels = picture.GetFormat() switch { Image.Format.Rgba8 => 4, Image.Format.Rgb8 => 3, _ => 0 };
        if (channels == 0) { picture.Convert(Image.Format.Rgba8); channels = 4; }
        long frame = (long)Engine.GetProcessFrames();
        _burstFrames.Add((frame, picture.GetWidth(), picture.GetHeight(), channels, picture.GetData()));
        Log.Info("frame_burst.frame", CoreLog.F("tag", _burstTag), CoreLog.F("frame", frame), CoreLog.F("index", _burstFrames.Count));
    }

    /// <summary>Each frame: when a burst has ended, its pictures are handed to a worker that writes them.</summary>
    private void FrameBurstTick()
    {
        if (!FrameBurstOn || _burstFrames.Count == 0) return;
        double now = Time.GetTicksMsec() / 1000.0;
        if (now < _burstUntil && _burstFrames.Count < FrameBurstMaxFrames) return;
        var frames = _burstFrames.ToArray();
        _burstFrames.Clear();
        if (_burstHooked)
        {
            RenderingServer.Singleton.FramePostDraw -= FrameBurstCapture;   // nothing runs each frame while no burst is running
            _burstHooked = false;
        }
        string dir = _burstDir, tag = _burstTag;
        Log.Info("frame_burst.end", CoreLog.F("tag", tag), CoreLog.F("frames", frames.Length), CoreLog.F("first", frames[0].Frame), CoreLog.F("last", frames[^1].Frame));
        _burstWriting = Task.Run(() =>
        {
            foreach (var f in frames) PngFile.Write(Path.Combine(dir, $"frame-{f.Frame:000000}.png"), f.Width, f.Height, f.Channels, f.Pixels);
            Log.Info("frame_burst.saved", CoreLog.F("tag", tag), CoreLog.F("dir", dir));
        });
    }
}

/// <summary>A minimal PNG writer for the frame burst: 8-bit RGB or RGBA, no filtering, deflate at its fastest level. Plain .NET, so it can run on a worker.</summary>
internal static class PngFile
{
    public static void Write(string path, int width, int height, int channels, byte[] pixels)
    {
        using var file = File.Create(path);
        file.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var header = new byte[13];
        WriteBigEndian(header, 0, width);
        WriteBigEndian(header, 4, height);
        header[8] = 8;
        header[9] = (byte)(channels == 4 ? 6 : 2);
        Chunk(file, "IHDR", header);
        using var packed = new MemoryStream();
        using (var z = new ZLibStream(packed, CompressionLevel.Fastest, leaveOpen: true))
        {
            int row = width * channels;
            var zero = new byte[1];
            for (int y = 0; y < height; y++)
            {
                z.Write(zero, 0, 1);
                z.Write(pixels, y * row, row);
            }
        }
        Chunk(file, "IDAT", packed.ToArray());
        Chunk(file, "IEND", Array.Empty<byte>());
    }

    private static void WriteBigEndian(byte[] into, int at, int value)
    {
        into[at] = (byte)(value >> 24); into[at + 1] = (byte)(value >> 16); into[at + 2] = (byte)(value >> 8); into[at + 3] = (byte)value;
    }

    private static void Chunk(Stream to, string type, byte[] data)
    {
        var length = new byte[4];
        WriteBigEndian(length, 0, data.Length);
        to.Write(length);
        var name = System.Text.Encoding.ASCII.GetBytes(type);
        to.Write(name);
        to.Write(data);
        uint crc = Crc(Crc(0xFFFFFFFF, name), data) ^ 0xFFFFFFFF;
        var tail = new byte[4];
        WriteBigEndian(tail, 0, (int)crc);
        to.Write(tail);
    }

    private static readonly uint[] Table = MakeTable();

    private static uint[] MakeTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    private static uint Crc(uint crc, byte[] bytes)
    {
        foreach (byte b in bytes) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
