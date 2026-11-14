// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;

namespace Viewer.Core.Session;

/// <summary>What a grid alert is about.</summary>
public enum AlertKind
{
    Tip, Important, Error,
    HomeSet, HomeNotAllowed, HomeFailed,
    ParcelBanned, ParcelBannedWarning, ParcelNoAccess, ParcelNotOnList, ParcelNotInGroup, ParcelOther,
}

/// <summary>
/// Sorts the grid's alert messages. An alert may carry a notification key (the official viewer looks it up in its table of
/// notifications first: llviewermessage.cpp process_agent_alert_message / attempt_standard_notification, and the three
/// "Cannot enter parcel: ..." entries of its notifications table); without a known key the words decide. A leading slash marks an
/// important system message and the modal flag an error (llviewermessage.cpp process_alert_core); anything else is a tip.
/// The words are the ones an OpenSim region sends for the same events: a parcel that bans or restricts the agent
/// (LandManagementModule.EnforceBans) and the answer to a set-home request (LandManagementModule.ClientOnSetHome).
/// </summary>
public static class ServerAlerts
{
    /// <summary>The alert's words without the one slash the region server puts in front of every plain alert except the home confirmation
    /// (so that a viewer shows it as an important system message).</summary>
    public static string Words(string text) => text is { Length: > 0 } && text[0] == '/' ? text[1..] : text ?? "";

    public static AlertKind Classify(string text, string key, bool modal)
    {
        text ??= "";
        key ??= "";
        switch (key)
        {
            case "Cannot enter parcel: banned": return AlertKind.ParcelBanned;
            case "Cannot enter parcel: not on access list": return AlertKind.ParcelNotOnList;
            case "Cannot enter parcel: not a group member": return AlertKind.ParcelNotInGroup;
            case "HomePositionSet": return AlertKind.HomeSet;
        }
        bool slash = text.StartsWith('/');
        string words = Words(text);
        if (words == "Home position set.")
            return AlertKind.HomeSet;
        if (words == "You are not allowed to set your home location in this parcel.")
            return AlertKind.HomeNotAllowed;
        if (words.StartsWith("Set Home request failed", StringComparison.Ordinal))
            return AlertKind.HomeFailed;
        if (words == "You are banned from parcel, please leave by your own will")
            return AlertKind.ParcelBannedWarning;
        if (words == "You are banned from parcel")
            return AlertKind.ParcelBanned;
        if (words == "You do not have access to the parcel")
            return AlertKind.ParcelNoAccess;
        if (words.StartsWith("Cannot enter parcel", StringComparison.Ordinal))
            return AlertKind.ParcelOther;
        if (slash)
            return AlertKind.Important;
        return modal ? AlertKind.Error : AlertKind.Tip;
    }
}

/// <summary>
/// The place a set-home request sends: the official viewer keeps it half a metre inside the region on each side and never below the
/// ground or above the region's top (llagent.cpp setStartPosition). It also lowers the height from the avatar's middle to its feet;
/// this viewer does not know the body's size at the transport, and the region lifts any home to the ground plus half the body when the
/// agent arrives, so the height is only kept inside the same limits.
/// </summary>
public static class HomePosition
{
    public const float Inset = 0.5f;

    /// <summary>The place to send for an agent standing at <paramref name="here"/>: <see cref="Clamp"/> with the ground taken from
    /// <paramref name="ground"/>. A ground lookup that fails or answers nothing sensible counts as ground at 0 (the region lifts any home
    /// below its ground when the agent arrives); the lookup is the library's, which can index past its terrain while the region's patches
    /// are still arriving.</summary>
    public static Vector3 For(Vector3 here, float regionWidth, Func<int, int, float> ground, float maxHeight)
    {
        int x = (int)Math.Clamp(MathF.Floor(here.X), 0, regionWidth - 1);
        int y = (int)Math.Clamp(MathF.Floor(here.Y), 0, regionWidth - 1);
        float g;
        try { g = ground(x, y); }
        catch (Exception ex) when (ex is IndexOutOfRangeException or NullReferenceException or ArgumentException) { g = 0f; }
        if (!float.IsFinite(g) || g < 0f)
            g = 0f;
        return Clamp(here, regionWidth, g, maxHeight);
    }

    public static Vector3 Clamp(Vector3 position, float regionWidth, float ground, float maxHeight) => new(
        Math.Clamp(position.X, Inset, regionWidth - Inset),
        Math.Clamp(position.Y, Inset, regionWidth - Inset),
        Math.Clamp(position.Z, ground, Math.Max(ground, maxHeight)));
}
