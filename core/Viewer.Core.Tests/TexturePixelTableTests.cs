// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Security.Cryptography;
using System.Text;
using Viewer.Core.Protocol;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// A hash of the decoded pixels of every committed texture fixture (fixtures/**/*.j2c outside the fuzz folder), at full
/// resolution and at the coarsest level, kept in fixtures/textures/pixel-table.tsv. A change of the JPEG 2000 decoder
/// (the CoreJ2K version the protocol library pulls in) that moves a single pixel of any fixture fails here by file name,
/// so a golden image can only change from decoding when this table says so. Set NV_WRITE_PIXEL_TABLE to a file path to
/// write the table the current code produces (the table is then reviewed and committed like a golden).
/// </summary>
public class TexturePixelTableTests
{
    private const string WriteVariable = "NV_WRITE_PIXEL_TABLE";
    private static string TablePath => Path.Combine(TestPaths.RepoRoot, "fixtures", "textures", "pixel-table.tsv");

    private static IEnumerable<string> Fixtures()
    {
        var root = Path.Combine(TestPaths.RepoRoot, "fixtures");
        string fuzz = Path.Combine(root, "textures", "fuzz") + Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(root, "*.j2c", SearchOption.AllDirectories)
            .Where(f => !f.StartsWith(fuzz, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal);
    }

    private static string Row(string file, string levelName, int level)
    {
        var bytes = File.ReadAllBytes(file);
        var r = level == 0 ? LmvTexture.Decode(bytes) : LmvTexture.DecodeLevel(bytes, level);
        string rel = Path.GetRelativePath(TestPaths.RepoRoot, file).Replace('\\', '/');
        if (!r.Ok) return $"{rel}\t{levelName}\trefused\t-";
        var t = r.Texture!;
        string hash = Convert.ToHexString(SHA256.HashData(t.Rgba.AsSpan(0, t.Width * t.Height * 4)));
        return $"{rel}\t{levelName}\t{t.Width}x{t.Height}\t{hash}";
    }

    private static List<string> Current()
    {
        var rows = new List<string>();
        foreach (var f in Fixtures())
        {
            rows.Add(Row(f, "L0", 0));
            rows.Add(Row(f, "Lmax", int.MaxValue));
        }
        return rows;
    }

    private static string Key(string row) => string.Join('\t', row.Split('\t').Take(2));

    [Fact]
    public void Every_committed_texture_fixture_decodes_to_the_pixels_in_the_table()
    {
        var current = Current();
        Assert.True(current.Count >= 40, $"only {current.Count} rows: the fixtures were not found");
        if (Environment.GetEnvironmentVariable(WriteVariable) is { Length: > 0 } path)
        {
            File.WriteAllText(path, string.Join("\n", current) + "\n", new UTF8Encoding(false));
            return;
        }
        Assert.True(File.Exists(TablePath), $"the pixel table {TablePath} is missing");
        var expected = File.ReadAllLines(TablePath).Where(l => l.Length > 0).ToDictionary(Key);
        var differ = new List<string>();
        foreach (var row in current)
        {
            if (!expected.TryGetValue(Key(row), out var want)) differ.Add($"{Key(row)}: not in the table");
            else if (want != row) differ.Add($"{Key(row)}: table {Describe(want)}, now {Describe(row)}");
        }
        var keys = current.Select(Key).ToHashSet();
        foreach (var key in expected.Keys)
            if (!keys.Contains(key)) differ.Add($"{key}: in the table, fixture gone");
        Assert.True(differ.Count == 0, $"{differ.Count} rows differ:\n" + string.Join("\n", differ));
    }

    private static string Describe(string row)
    {
        var f = row.Split('\t');
        return f[2] + " " + (f[3].Length > 12 ? f[3][..12] : f[3]);
    }
}
