// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections.Concurrent;

namespace Viewer.Core.Tests;

/// <summary>
/// The one place test code creates and deletes temp paths. Every path is a new, unique name
/// under %TEMP% starting with <see cref="Prefix"/>, recorded when it is handed out. <see cref="Delete"/> removes only a
/// path recorded in this run (the exact file, or the unique folder and what the test put in it) and refuses anything
/// else; no wildcard or glob is ever used. Whatever a test has not deleted itself is deleted when the test process
/// exits. Leftovers from earlier runs are never touched. TempHygieneTests proves no other test code deletes.
/// </summary>
internal static class TestTemp
{
    public const string Prefix = "nvtest-";

    private static readonly ConcurrentDictionary<string, bool> Recorded = new(StringComparer.OrdinalIgnoreCase);   // path -> is a folder

    static TestTemp() => AppDomain.CurrentDomain.ProcessExit += (_, _) => DeleteAllRecorded();

    private static string Root => Path.GetFullPath(Path.GetTempPath());

    /// <summary>A unique folder path (not yet created; the test or the code under test creates it).</summary>
    public static string NewFolderPath(string tag) => Record(tag, "", folder: true);

    /// <summary>A unique folder, created now.</summary>
    public static string NewFolder(string tag)
    {
        string p = NewFolderPath(tag);
        Directory.CreateDirectory(p);
        return p;
    }

    /// <summary>A unique file path (not yet created).</summary>
    public static string NewFilePath(string tag, string extension) => Record(tag, extension, folder: false);

    public static bool IsRecorded(string path) => Recorded.ContainsKey(Path.GetFullPath(path));

    /// <summary>Deletes a path this helper handed out in this run; throws for any other path.</summary>
    public static void Delete(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("TestTemp refuses an empty path", nameof(path));
        string full = Path.GetFullPath(path);
        if (!Recorded.TryGetValue(full, out bool folder))
            throw new InvalidOperationException($"TestTemp refuses to delete a path it did not create in this run: {path}");
        if (!string.Equals(Path.GetDirectoryName(full), Root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(full).StartsWith(Prefix, StringComparison.Ordinal))
            throw new InvalidOperationException($"TestTemp refuses a path outside %TEMP% or without its prefix: {path}");   // cannot happen for a recorded path
        if (folder)
        {
            if (Directory.Exists(full))
                Directory.Delete(full, recursive: true);
        }
        else if (File.Exists(full))
            File.Delete(full);
        Recorded.TryRemove(full, out _);
    }

    /// <summary>Deletes, best effort, every path still recorded (the end of the run).</summary>
    public static void DeleteAllRecorded()
    {
        foreach (var p in Recorded.Keys.ToArray())
        {
            try { Delete(p); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static string Record(string tag, string extension, bool folder)
    {
        if (tag.Length == 0 || tag.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || tag.Contains('*') || tag.Contains('?'))
            throw new ArgumentException("TestTemp: the tag must be a plain name", nameof(tag));
        string p = Path.Combine(Root, Prefix + tag + "-" + Guid.NewGuid().ToString("N") + extension);
        if (File.Exists(p) || Directory.Exists(p) || !Recorded.TryAdd(p, folder))
            throw new InvalidOperationException($"TestTemp: {p} already exists");   // a fresh GUID: never expected
        return p;
    }
}
