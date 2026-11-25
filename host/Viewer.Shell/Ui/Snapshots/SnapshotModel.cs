// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Globalization;
using System.Text.RegularExpressions;

namespace Viewer.Shell.Snapshots;

public enum SnapshotFormat { Png, Jpeg }

/// <summary>A size the Snapshot window offers. Width and height 0: the window's own size.</summary>
public sealed record SnapshotSize(int Width, int Height)
{
    public bool IsWindow => Width == 0;
}

/// <summary>The Snapshot window's rules: the sizes it offers, and where and under what names a picture is saved.</summary>
public static class SnapshotModel
{
    public const string FileStem = "Snapshot";

    public static IReadOnlyList<SnapshotSize> Sizes { get; } =
        [new(0, 0), new(1920, 1080), new(2560, 1440), new(3840, 2160), new(5120, 2880), new(7680, 4320)];

    public static (int Width, int Height) Resolve(SnapshotSize size, int windowWidth, int windowHeight) =>
        size.IsWindow ? (windowWidth, windowHeight) : (size.Width, size.Height);

    /// <summary>Whether the graphics card can draw a picture of this size (<paramref name="maxSide"/>: its largest 2D texture side).</summary>
    public static bool Allowed(SnapshotSize size, int maxSide) => size.IsWindow || (size.Width <= maxSide && size.Height <= maxSide);

    /// <summary>The content scale for a picture of another height, so the interface covers the same share of it as of the window.</summary>
    public static float ScaleFactor(float factor, int windowHeight, int targetHeight) => factor * targetHeight / Math.Max(1, windowHeight);

    public static string DefaultFolder(string pictures) => Path.Combine(pictures, "Reverie");

    public static string Extension(SnapshotFormat format) => format == SnapshotFormat.Jpeg ? ".jpg" : ".png";

    /// <summary>Writes the picture as the next numbered file (one count for both formats) and returns its path. The file is always made
    /// new: a name that is already taken is skipped, never written over.</summary>
    public static string Save(string folder, SnapshotFormat format, byte[] bytes) => Save(folder, format, file => file.Write(bytes));

    /// <summary>The same, with the picture written by <paramref name="write"/>.</summary>
    public static string Save(string folder, SnapshotFormat format, Action<Stream> write)
    {
        Directory.CreateDirectory(folder);
        var pattern = new Regex("^" + FileStem + @"_(\d{1,9})\.(png|jpg)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        int next = 1 + Directory.EnumerateFiles(folder, FileStem + "_*")
            .Select(f => pattern.Match(Path.GetFileName(f)))
            .Where(m => m.Success)
            .Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
            .DefaultIfEmpty(0).Max();
        while (true)
        {
            string path = Path.Combine(folder, string.Create(CultureInfo.InvariantCulture, $"{FileStem}_{next:D3}{Extension(format)}"));
            FileStream file;
            try
            {
                file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            }
            catch (IOException) when (File.Exists(path) || Directory.Exists(path))
            {
                next++;
                continue;
            }
            bool written = false;
            try
            {
                using (file)
                    write(file);
                written = true;
                return path;
            }
            finally
            {
                if (!written)
                    File.Delete(path);   // the half-written file this call has just made, and no other
            }
        }
    }
}
