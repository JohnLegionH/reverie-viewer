// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>Where the content cache lives and how large it may grow.</summary>
public readonly record struct ContentCacheChoice(string? Root, long CapBytes);

/// <summary>
/// The content cache as Preferences sets it. A folder that is not an absolute path is ignored (the per-user default is used, Root null);
/// the size is the megabytes setting in bytes.
/// </summary>
public static class ContentCacheSetting
{
    public static ContentCacheChoice Resolve(SettingsService settings)
    {
        string? folder = settings.GetText(Settings.CacheFolderKey)?.Trim();
        string? root = !string.IsNullOrEmpty(folder) && Path.IsPathFullyQualified(folder) ? folder : null;
        return new ContentCacheChoice(root, settings.Get(Settings.CacheSizeMb) * 1024L * 1024L);
    }
}
