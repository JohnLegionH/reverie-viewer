// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Avatars;

/// <summary>Where the avatar data folder came from.</summary>
public enum CharacterFolderSource { None, Argument, Environment, Shipped }

/// <summary>The avatar data folder the viewer reads, and where that choice came from.</summary>
public sealed record CharacterFolderChoice(string? Path, CharacterFolderSource Source)
{
    /// <summary>The name of the folder the shipped build carries beside its executable.</summary>
    public const string ShippedFolderName = "character";

    /// <summary>The --character argument when given, else the environment value, else the shipped folder beside the
    /// executable when it exists, else none.</summary>
    public static CharacterFolderChoice Resolve(string? argument, string? environment, string? executableFolder, Func<string, bool> exists)
    {
        if (Clean(argument) is { } a)
            return new(a, CharacterFolderSource.Argument);
        if (Clean(environment) is { } e)
            return new(e, CharacterFolderSource.Environment);
        if (!string.IsNullOrWhiteSpace(executableFolder))
        {
            var shipped = System.IO.Path.Combine(executableFolder, ShippedFolderName);
            if (exists(shipped))
                return new(shipped, CharacterFolderSource.Shipped);
        }
        return new(null, CharacterFolderSource.None);
    }

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var v = value.Trim().Trim('"').Trim();
        return v.Length == 0 ? null : v;
    }
}
