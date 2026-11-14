// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using LibreMetaverse.StructuredData;

namespace Viewer.Core.Protocol;

/// <summary>How a set-home request is sent.</summary>
public enum HomeRoute { Legacy, Capability }

/// <summary>The region's answer to a set-home post: success, and the place it stored when it says so.</summary>
public readonly record struct HomeReply(bool Success, Vector3? Place);

/// <summary>
/// The set-home request as the official viewer sends it: one LLSD map posted to the region's "HomeLocation" capability
/// (llagent.cpp setStartPosition and setStartPositionSuccess). The older start-location message is used only when the region
/// offers no such capability.
/// </summary>
public static class HomeLocationCapability
{
    public const string Name = "HomeLocation";

    public static HomeRoute Choose(Uri? capability) => capability is null ? HomeRoute.Legacy : HomeRoute.Capability;

    public static string Body(Vector3 place, Vector3 lookAt)
    {
        var home = new OSDMap
        {
            ["LocationId"] = OSD.FromInteger(1),
            ["LocationPos"] = Vec(place),
            ["LocationLookAt"] = Vec(lookAt),
        };
        return OSDParser.SerializeLLSDXmlString(new OSDMap { ["HomeLocation"] = home });
    }

    /// <summary>Reads the reply. Anything but a "true" success with a whole stored place (X, Y and Z) is not a success, as in the
    /// official viewer; a reply that is not LLSD is not a success either.</summary>
    public static HomeReply ReadReply(string llsdXml)
    {
        try
        {
            if (OSDParser.DeserializeLLSDXml(llsdXml) is not OSDMap root)
                return new HomeReply(false, null);
            if (!root.TryGetValue("success", out var ok) || !ok.AsString().StartsWith("true", StringComparison.Ordinal))
                return new HomeReply(false, null);
            if (root["HomeLocation"] is not OSDMap home || home["LocationPos"] is not OSDMap pos
                || !pos.ContainsKey("X") || !pos.ContainsKey("Y") || !pos.ContainsKey("Z"))
                return new HomeReply(false, null);
            return new HomeReply(true, new Vector3((float)pos["X"].AsReal(), (float)pos["Y"].AsReal(), (float)pos["Z"].AsReal()));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new HomeReply(false, null);
        }
    }

    private static OSDMap Vec(Vector3 v) => new() { ["X"] = OSD.FromReal(v.X), ["Y"] = OSD.FromReal(v.Y), ["Z"] = OSD.FromReal(v.Z) };
}
