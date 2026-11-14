// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell.Notifications;

/// <summary>The permissions a script can ask for, by bit, each with the string-table key that names it in words and whether it is a caution.</summary>
public static class ScriptPermissions
{
    public sealed record Entry(int Bit, string Key, bool Caution);

    /// <summary>The known permissions in the official order.</summary>
    public static IReadOnlyList<Entry> Table { get; } =
    [
        new(1 << 1, "script.perm.take_money", true),
        new(1 << 2, "script.perm.control_inputs", false),
        new(1 << 3, "script.perm.remap_controls", false),
        new(1 << 4, "script.perm.animate", false),
        new(1 << 5, "script.perm.attach", false),
        new(1 << 6, "script.perm.release_ownership", false),
        new(1 << 7, "script.perm.link", false),
        new(1 << 8, "script.perm.joints", false),
        new(1 << 9, "script.perm.change_permissions", false),
        new(1 << 10, "script.perm.track_camera", false),
        new(1 << 11, "script.perm.control_camera", false),
        new(1 << 12, "script.perm.teleport", false),
        new(1 << 13, "script.perm.experience", false),
        new(1 << 14, "script.perm.estate_access", false),
        new(1 << 15, "script.perm.override_animations", false),
        new(1 << 16, "script.perm.return_objects", false),
        new(1 << 17, "script.perm.force_sit", false),
        new(1 << 18, "script.perm.environment", false),
    ];

    /// <summary>The mask with unknown bits removed.</summary>
    public static int Known(int questions) => Table.Where(e => (questions & e.Bit) != 0).Aggregate(0, (mask, e) => mask | e.Bit);

    /// <summary>The entries a mask asks for, in table order.</summary>
    public static IReadOnlyList<Entry> Asked(int questions) => Table.Where(e => (questions & e.Bit) != 0).ToArray();

    public static bool HasCaution(int questions) => Asked(questions).Any(e => e.Caution);
}
