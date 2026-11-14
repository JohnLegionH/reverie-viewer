// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;

namespace Viewer.Core.Session;

// Behaviour: NAME-1's spec, section 3. The drop notice follows the official viewer; the reconnect goes beyond it and is the user's click.

/// <summary>What a session knew when the grid ended it unasked: why, the grid's own words (a kick's reason), and the place the agent was.</summary>
/// <param name="HasPlace">Whether <paramref name="Region"/> and <paramref name="Position"/> (the soles, in region metres) are known.</param>
public sealed record DropInfo(DisconnectKind Kind, string Message, string Region, int RegionSizeX, int RegionSizeY, Vector3 Position, bool HasPlace);

/// <summary>How a reconnect goes: the start locations to try, in order, and which login refusals end it at once.</summary>
public static class ReconnectPlan
{
    /// <summary>The most login attempts one click on Reconnect makes.</summary>
    public const int MaxAttempts = 2;

    /// <summary>The wait between two attempts of one click.</summary>
    public static readonly TimeSpan Pause = TimeSpan.FromSeconds(3);

    /// <summary>The login refusal code that means the grid still holds the previous session of the account.</summary>
    public const string PresenceCode = "presence";

    /// <summary>How many times a refusal for a held session is retried at the same spot, the wait before each retry, and the longest
    /// one click goes on (retries and logins together).</summary>
    public const int PresenceRetries = 3;
    public static readonly TimeSpan PresenceWait = TimeSpan.FromSeconds(8);
    public static readonly TimeSpan PresenceBudget = TimeSpan.FromSeconds(60);

    /// <summary>The login start location for the place the agent was: <c>uri:Region&amp;x&amp;y&amp;z</c>, the spot kept inside the region. Empty when the region's name cannot be used there.</summary>
    public static string SameSpot(string region, Vector3 position, int sizeX, int sizeY)
    {
        if (string.IsNullOrWhiteSpace(region) || region.Any(c => c is '&' or ':' || char.IsControl(c)))
            return "";   // the location form is split at these
        int x = ClampWhole(position.X, 1, Math.Max(1, sizeX - 1));
        int y = ClampWhole(position.Y, 1, Math.Max(1, sizeY - 1));
        int z = ClampWhole(position.Z, 0, 4096);
        return $"uri:{region.Trim()}&{x}&{y}&{z}";
    }

    private static int ClampWhole(float v, int min, int max) => float.IsFinite(v) ? Math.Clamp((int)MathF.Round(v), min, max) : min;

    /// <summary>The start locations to try for this drop: the same spot when it is known, then the grid's last location. Never more than <see cref="MaxAttempts"/>.</summary>
    public static IReadOnlyList<string> StartLocations(DropInfo? drop)
    {
        string spot = drop is { HasPlace: true } ? SameSpot(drop.Region, drop.Position, drop.RegionSizeX, drop.RegionSizeY) : "";
        return spot.Length > 0 ? [spot, "last"] : ["last"];
    }

    /// <summary>True for a login refusal another attempt cannot cure: the credentials, an agreement to accept, a version that is too old, a second factor.</summary>
    public static bool StopsReconnect(string? failureCode) => failureCode is "key" or "mfa_challenge" or "tos" or "update";
}
