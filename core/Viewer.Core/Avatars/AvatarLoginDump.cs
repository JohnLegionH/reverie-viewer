// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Viewer.Core.Avatars;

/// <summary>
/// The parts of the own-avatar dump the viewer writes at each login that are worked out from the core's own data: which joints the worn rigged meshes
/// move (and which they pin in scale), and a fingerprint of the character folder the body is built from. Pure; the host adds the window and camera readings.
/// </summary>
public static class AvatarLoginDump
{
    /// <summary>One wearable the current outfit holds: its type (lower case), its layer (0 is the bottom layer of its type), the item it links to and
    /// that item's name, whether it is an alpha layer and whether it is a body part.</summary>
    public sealed record WornWearable(string Type, int Layer, Guid Item, string Name, bool Alpha, bool BodyPart);

    /// <summary>The wearables in <paramref name="current"/> (the current outfit as the outfit planner lists it), body parts first, then by type and
    /// layer from the bottom up; worn objects and links to nothing wearable are left out.</summary>
    public static IReadOnlyList<WornWearable> Wearables(IReadOnlyList<Viewer.Core.Inventory.OutfitEntry> current) =>
        current.Where(e => e.Type != Viewer.Core.Inventory.WearableType.None && e.Section is 1 or 2)
            .OrderBy(e => e.Section).ThenBy(e => (int)e.Type).ThenBy(e => e.Layer)
            .Select(e => new WornWearable(e.Type.ToString().ToLowerInvariant(), e.Layer, e.TargetId, e.Name, e.Type == Viewer.Core.Inventory.WearableType.Alpha, e.Section == 1))
            .ToList();

    /// <summary>One joint a worn mesh has moved: its name, the position it was given (relative to its parent, metres) and the mesh that won it.</summary>
    public sealed record JointOverride(string Joint, float[] Position, Guid Winner);

    /// <summary>What the worn rigged meshes do to the skeleton: how many of them there are, the joints they move, the joints whose scale they pin, and the pelvis raise.</summary>
    public sealed record OverrideReport(int MeshCount, float PelvisFixupMetres, IReadOnlyList<JointOverride> Joints, IReadOnlyList<string> ScaleLocked);

    /// <summary>The files of a character folder: one hash over all of them (names, sizes and bytes, in name order), their count and size, and each one's own hash.</summary>
    public sealed record FolderFingerprint(string Hash, int Files, long Bytes, IReadOnlyDictionary<string, string> FileHashes);

    /// <summary>The joints <paramref name="overrides"/> moves and pins, by name, in joint order.</summary>
    public static OverrideReport Describe(JointOverrides overrides, AvatarRig rig)
    {
        var moved = overrides.OverriddenNodes.Where(n => n >= 0 && n < rig.NodeCount).OrderBy(n => n)
            .Select(n => new JointOverride(rig.NodeName(n), [overrides.PositionOf(n).X, overrides.PositionOf(n).Y, overrides.PositionOf(n).Z], overrides.WinnerOf(n) ?? Guid.Empty)).ToArray();
        var locked = overrides.LockedNodes.Where(n => n >= 0 && n < rig.NodeCount).OrderBy(n => n).Select(rig.NodeName).ToArray();
        return new(overrides.MeshCount, overrides.PelvisFixup, moved, locked);
    }

    /// <summary>A fingerprint of every file under <paramref name="folder"/>; null when there is no such folder. The files are read once, in name order
    /// (forward slashes, ordinal), so the same files give the same hash on any machine.</summary>
    public static FolderFingerprint? Fingerprint(string? folder)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return null;
        var names = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Select(f => (Full: f, Name: Path.GetRelativePath(folder, f).Replace(Path.DirectorySeparatorChar, '/')))
            .OrderBy(f => f.Name, StringComparer.Ordinal).ToArray();
        using var whole = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var hashes = new Dictionary<string, string>(names.Length, StringComparer.Ordinal);
        long bytes = 0;
        foreach (var (full, name) in names)
        {
            var data = File.ReadAllBytes(full);
            bytes += data.Length;
            var one = SHA256.HashData(data);
            hashes[name] = Convert.ToHexString(one).ToLowerInvariant();
            whole.AppendData(Encoding.UTF8.GetBytes(name + '\n'));
            whole.AppendData(BitConverter.GetBytes((long)data.Length));
            whole.AppendData(one);
        }
        return new(Convert.ToHexString(whole.GetHashAndReset()).ToLowerInvariant(), names.Length, bytes, hashes);
    }
}
