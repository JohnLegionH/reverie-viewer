// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text;
using Viewer.Core.Session;

namespace Viewer.Core.Social;

/// <summary>
/// The block list's file on disk, one per account. Behaviour follows the official viewer's cached mute list (indra/newview/llmutelist.cpp,
/// requestFromServer, cache, loadFromFile and saveToFile): the file is named for the account's id with the extension
/// <c>.cached_mute</c>, and each line is <c>type id name|flags</c> (the protocol's numbers). Written from a behaviour spec in our own
/// words; the name is read up to the LAST bar, so a name that holds a bar is not cut. The file holds names of people and objects: it
/// lives in the user's own cache folder, and nothing here logs its content.
/// </summary>
public static class MuteCache
{
    public const string Extension = ".cached_mute";

    /// <summary>The most bytes of a cache file that are read; a larger file is not a block list.</summary>
    public const long MaxBytes = 4 * 1024 * 1024;

    /// <summary>The file for <paramref name="agent"/> in <paramref name="directory"/>.</summary>
    public static string FileFor(string directory, Guid agent) => Path.Combine(directory, agent.ToString() + Extension);

    /// <summary>
    /// The entries in the file. Null when there is no file or it cannot be read (too large, locked, a bad folder). A line that is not an
    /// entry is skipped and counted in <paramref name="skippedLines"/>; the good lines are kept.
    /// </summary>
    public static IReadOnlyList<MuteEntry>? Read(string path, out int skippedLines)
    {
        skippedLines = 0;
        string[] lines;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxBytes)
                return null;
            lines = File.ReadAllLines(path, Encoding.UTF8);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
        var entries = new List<MuteEntry>(lines.Length);
        foreach (var raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0)
                continue;
            if (TryParse(line, out var entry))
                entries.Add(entry);
            else
                skippedLines++;
        }
        return entries;
    }

    /// <summary>Writes the whole list to <paramref name="path"/> by writing a temporary file beside it and renaming it over, so a crash leaves
    /// the old file. False when it could not be written (the list in memory is unaffected).</summary>
    public static bool Write(string path, IEnumerable<MuteEntry> entries)
    {
        string temp = path + ".tmp";
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var text = new StringBuilder();
            foreach (var e in entries)
                text.Append((int)e.Kind).Append(' ').Append(e.Id.ToString()).Append(' ').Append(e.Name).Append('|').Append(e.Flags).Append('\n');
            File.WriteAllText(temp, text.ToString(), new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryParse(string line, out MuteEntry entry)
    {
        entry = null!;
        int a = line.IndexOf(' ');
        if (a <= 0)
            return false;
        int b = line.IndexOf(' ', a + 1);
        if (b < 0)
            return false;
        int bar = line.LastIndexOf('|');
        if (bar < b)
            return false;
        if (!int.TryParse(line.AsSpan(0, a), out int kind) || kind is < 0 or > 4)
            return false;
        if (!Guid.TryParse(line.AsSpan(a + 1, b - a - 1), out var id))
            return false;
        if (!uint.TryParse(line.AsSpan(bar + 1), out uint flags))
            return false;
        string name = line.Substring(b + 1, bar - b - 1).Trim();
        entry = new MuteEntry((MuteKind)kind, id, name, flags);
        return true;
    }
}
