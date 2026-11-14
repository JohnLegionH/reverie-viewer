// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Text.Json;
using Viewer.Core.Camera;

namespace Viewer.Automation;

/// <summary>
/// The body of the <c>aim</c> command (development builds): aim the camera at an avatar or an object and hold it there, report the
/// camera pose and where the target lands on the screen, or let go of the camera. Parsed and checked here so it is tested without the engine.
/// <para><c>{op: aim|state|release, target: avatar|object, id?, name?, local?, point: head|chest|body|centre, distance, around_deg, above_deg,
/// fov_deg, min_fill?, max_offset?}</c>. <c>around_deg</c> is measured from the way the target faces (0 in front of it, 90 on its left,
/// 180 behind it), <c>above_deg</c> from the horizontal; the field of view is the vertical one.</para>
/// </summary>
public sealed record AimRequest(
    string Op, string Target, Guid Id, string? Name, uint Local, AimPoint Point, float Distance, float AroundDeg, float AboveDeg, float FovDeg,
    float MinFill, float MaxOffset)
{
    public const float DefaultFovDeg = 40f;

    /// <summary>Reads and checks a body; false with the reason (never an exception) when it is not acceptable.</summary>
    public static bool TryParse(JsonElement body, out AimRequest request, out string error)
    {
        request = new AimRequest("", "", Guid.Empty, null, 0, AimPoint.Body, 0f, 0f, 0f, 0f, 0f, 0f);
        error = "";
        if (body.ValueKind != JsonValueKind.Object)
        {
            error = "the body must be a JSON object";
            return false;
        }
        string op = Text(body, "op") ?? "aim";
        if (op is not ("aim" or "state" or "release"))
        {
            error = "op must be aim, state or release";
            return false;
        }
        if (op != "aim")
        {
            request = request with { Op = op };
            return true;
        }
        string target = Text(body, "target") ?? "avatar";
        if (target is not ("avatar" or "object"))
        {
            error = "target must be avatar or object";
            return false;
        }
        Guid id = Guid.TryParse(Text(body, "id"), out var g) ? g : Guid.Empty;
        string? name = Text(body, "name");
        uint local = body.TryGetProperty("local", out var lv) && lv.ValueKind == JsonValueKind.Number && lv.TryGetUInt32(out var l) ? l : 0;
        if (id == Guid.Empty && string.IsNullOrEmpty(name) && local == 0)
        {
            error = "target needs an id, a name or a local id";
            return false;
        }
        AimPoint point;
        switch (Text(body, "point") ?? "body")
        {
            case "head": point = AimPoint.Head; break;
            case "chest": point = AimPoint.Chest; break;
            case "body": point = AimPoint.Body; break;
            case "centre": point = AimPoint.Centre; break;
            default: error = "point must be head, chest, body or centre"; return false;
        }
        if (!Number(body, "distance", float.NaN, out float distance) || float.IsNaN(distance) || distance <= 0f || distance > 200f)
        {
            error = "distance must be a number above 0 and at most 200 metres";
            return false;
        }
        if (!Number(body, "around_deg", 0f, out float around) || around is < -360f or > 360f)
        {
            error = "around_deg must be a number from -360 to 360";
            return false;
        }
        if (!Number(body, "above_deg", 0f, out float above) || above is <= -90f or >= 90f)
        {
            error = "above_deg must be a number above -90 and below 90";
            return false;
        }
        if (!Number(body, "fov_deg", DefaultFovDeg, out float fov) || fov is < 10f or > 120f)
        {
            error = "fov_deg must be a number from 10 to 120";
            return false;
        }
        if (!Number(body, "min_fill", point == AimPoint.Head ? 0.25f : 0.5f, out float minFill) || minFill is < 0f or > 1f)
        {
            error = "min_fill must be a number from 0 to 1";
            return false;
        }
        if (!Number(body, "max_offset", 0.1f, out float maxOffset) || maxOffset is < 0f or > 1f)
        {
            error = "max_offset must be a number from 0 to 1";
            return false;
        }
        request = new AimRequest("aim", target, id, name, local, point, distance, around, above, fov, minFill, maxOffset);
        return true;
    }

    private static string? Text(JsonElement body, string name) =>
        body.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>A number member, or the fallback when it is absent; false when it is present and not a number.</summary>
    private static bool Number(JsonElement body, string name, float fallback, out float value)
    {
        value = fallback;
        if (!body.TryGetProperty(name, out var v))
            return true;
        if (v.ValueKind != JsonValueKind.Number)
            return false;
        value = v.GetSingle();
        return true;
    }
}
