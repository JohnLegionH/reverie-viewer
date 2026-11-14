// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Platform;

public enum CarryOverOutcome { Copied, NewFolderExists, NothingToCopy, Failed }

/// <summary>What a carry-over did: its outcome, how many files it copied, and the reason when it failed.</summary>
public sealed record CarryOverResult(CarryOverOutcome Outcome, int Files, string? Error = null);

/// <summary>
/// The settings folder follows the viewer's name. When the folder under the new name has no settings yet and the folder
/// under the previous name exists, everything in it (settings, window places, inventory sort, AO sets, add-on choices,
/// saved logins) is copied across once. The previous folder is only read, never changed. The engine's own caches
/// (shader and pipeline caches, its log folder) are not copied: the engine rebuilds them, and it may already have made
/// them under the new name before this runs, so a new folder holding only those still counts as having no settings.
/// The copy goes to a staging folder beside the new one first and is moved into place only when it is complete, so a
/// copy that fails part way leaves nothing half-done and is tried again at the next start.
/// </summary>
public static class SettingsCarryOver
{
    /// <summary>Folders the engine makes and fills itself in its user folder; never copied, and not counted as settings.</summary>
    public static readonly IReadOnlySet<string> EngineFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "shader_cache", "vulkan", "logs" };

    public static CarryOverResult Run(string previousFolder, string newFolder)
    {
        string previous = Path.GetFullPath(previousFolder);
        string current = Path.GetFullPath(newFolder);
        if (string.Equals(previous.TrimEnd('\\', '/'), current.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase) || !Directory.Exists(previous))
            return new(CarryOverOutcome.NothingToCopy, 0);
        if (HasSettings(current))
            return new(CarryOverOutcome.NewFolderExists, 0);

        string staging = current.TrimEnd('\\', '/') + ".carry-over-" + Environment.ProcessId;
        int files = 0;
        try
        {
            files = CopyTree(previous, staging, top: true);
            if (!Directory.Exists(current))
            {
                Directory.Move(staging, current);
            }
            else
            {
                // the engine made the folder already (its caches only): move each copied entry into it
                foreach (var dir in Directory.GetDirectories(staging))
                    Directory.Move(dir, Path.Combine(current, Path.GetFileName(dir)));
                foreach (var file in Directory.GetFiles(staging))
                    File.Move(file, Path.Combine(current, Path.GetFileName(file)));
                Directory.Delete(staging, recursive: false);   // empty now: this run made it
            }
            return new(CarryOverOutcome.Copied, files);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // the staging folder is this run's own; nothing was put in the new folder
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch (Exception) { /* left for the next start's own name */ }
            return new(CarryOverOutcome.Failed, 0, e.Message);
        }
    }

    /// <summary>Whether a folder holds anything besides the engine's own caches.</summary>
    public static bool HasSettings(string folder) =>
        Directory.Exists(folder)
        && (Directory.EnumerateFiles(folder).Any() || Directory.EnumerateDirectories(folder).Any(d => !EngineFolders.Contains(Path.GetFileName(d))));

    private static int CopyTree(string from, string to, bool top)
    {
        Directory.CreateDirectory(to);
        int count = 0;
        foreach (var file in Directory.GetFiles(from))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: false);
            count++;
        }
        foreach (var dir in Directory.GetDirectories(from))
        {
            if (top && EngineFolders.Contains(Path.GetFileName(dir)))
                continue;
            count += CopyTree(dir, Path.Combine(to, Path.GetFileName(dir)), top: false);
        }
        return count;
    }
}
