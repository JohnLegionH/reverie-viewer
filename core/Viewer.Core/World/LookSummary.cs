// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.World;

/// <summary>One avatar's look reduced to comparable figures: the versions, the bake ids by slot (first eight hex digits), a hash of the
/// visual parameters and the Height byte. Used by the automation reports so a test can tell whether two copies of the same avatar
/// (the agent's own and another region's) hold the same look. Ids and figures only, never a name.</summary>
public sealed record LookSummary(int Version, int CofVersion, string Bakes, string ParamsHash, byte? HeightByte)
{
    /// <summary>The visual parameter that sets the body height.</summary>
    public const int HeightParamId = 33;

    public static LookSummary Of(AvatarAppearance look) => new(
        look.AppearanceVersion,
        look.CofVersion,
        string.Join(",", look.Bakes.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Key + "=" + k.Value.ToString("N")[..8])),
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(look.VisualParams.Raw.ToArray()))[..12],
        look.VisualParams.TryGet(HeightParamId, out var height) ? height : null);
}
