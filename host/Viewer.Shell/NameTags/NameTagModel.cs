// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;

namespace Viewer.Shell.NameTags;

// Behaviour follows the official viewer's avatar name tags, written from a behaviour spec in our own words (no code, text or layout
// copied): NAME-1's spec, section 1. The model works out WHAT tags to show and where; the engine host draws them.

/// <summary>When tags show: never, always, or for a while after a tag is made and then fade away.</summary>
public enum NameTagMode { Off = 0, Always = 1, Fade = 2 }

/// <summary>The two text sizes a tag uses.</summary>
public enum NameTagSize { Normal, Small }

/// <summary>One line of a tag.</summary>
public sealed record NameTagLine(string Text, NameTagSize Size, UiColour Colour);

/// <summary>An account's names. <paramref name="DisplayName"/> is null when the grid offers none.</summary>
public sealed record AvatarNames(string FullName, string? DisplayName = null);

/// <summary>What the model needs to know about one avatar in view. <paramref name="Position"/> is the middle of its body box and
/// <paramref name="Size"/> the box (region metres, the region's axes: X east, Y north, Z up). <paramref name="Head"/> is where the drawn
/// skeleton's head joint is, when the host knows it (a seated or crouching avatar's head is nowhere near 90 % of its box).</summary>
public sealed record NameTagInput(
    Guid AgentId, AvatarNames Names, string Title, Vector3 Position, Quaternion Rotation, Vector3 Size, bool IsSelf, bool IsFriend, bool IsBlocked,
    IReadOnlyCollection<Guid> Animations, Vector3? Head = null);

/// <summary>The camera, in the region's axes. <paramref name="Forward"/> and <paramref name="Up"/> are unit vectors.</summary>
public sealed record NameTagCamera(Vector3 Eye, Vector3 Forward, Vector3 Up, float Near = 0.1f);

/// <summary>A tag to draw: its lines, its opacity (0 to 1), and the world point its bottom edge hangs from (the host adds the
/// 25-pixel lift on screen).</summary>
public sealed record NameTagPlan(Guid AgentId, IReadOnlyList<NameTagLine> Lines, float Alpha, Vector3 Anchor, float Distance);

/// <summary>The names of the animations that mark an avatar's state (the protocol's fixed ids).</summary>
public static class NameTagAnimations
{
    public static readonly Guid Away = new("fd037134-85d4-f241-72c6-4f42164fedee");
    public static readonly Guid Busy = new("efcf670c-2d18-8128-973a-034ebc806b67");
    public static readonly Guid EditingAppearance = new("038fcec9-5ebd-8a8e-0e2e-6e71a0a1ac53");
}

/// <summary>The tag rules as pure functions, and the model that applies them to the avatars in view.</summary>
public sealed class NameTagModel
{
    /// <summary>Fade mode: how long a tag shows, and how much of the end of that it fades over (seconds).</summary>
    public const float ShowSeconds = 10f, FadeSeconds = 1f;

    /// <summary>Full opacity up to this distance (metres), then none by the end of the range after it.</summary>
    public const float FadeStartMetres = 20f, FadeRangeMetres = 5f;

    /// <summary>The apparent angle (degrees) above which a tag is fully opaque, and at or below which there is none.</summary>
    public const float FullAngle = 5f, NoAngle = 2f;

    public const int MaxWidthPixels = 298;
    public const int LiftPixels = 25;

    /// <summary>The angle in degrees an avatar of bounding radius <paramref name="radius"/> covers at <paramref name="range"/>; a range under a millimetre counts as 180.</summary>
    public static float ApparentAngle(float range, float radius) => range < 0.001f ? 180f : MathF.Atan2(radius, range) * 180f / MathF.PI;

    /// <summary>The opacity the apparent angle allows: 1 above 5 degrees, (angle - 2) / 3 between 2 and 5, 0 at or below 2.</summary>
    public static float AngleAlpha(float angle) => angle > FullAngle ? 1f : angle > NoAngle ? (angle - NoAngle) / (FullAngle - NoAngle) : 0f;

    /// <summary>The opacity fade mode allows after a tag has shown for <paramref name="seconds"/>: 1 until the last second of the ten, then falling to 0.</summary>
    public static float FadeAlpha(float seconds)
    {
        float start = ShowSeconds - FadeSeconds;
        return seconds > start ? Math.Clamp(1f - (seconds - start) / FadeSeconds, 0f, 1f) : 1f;
    }

    /// <summary>The opacity the distance allows: 1 to 20 m, then falling linearly to 0 at 25 m.</summary>
    public static float DistanceAlpha(float distance) => distance <= FadeStartMetres ? 1f : Math.Clamp(1f - (distance - FadeStartMetres) / FadeRangeMetres, 0f, 1f);

    /// <summary>"First Last" with a last name "Resident" left off.</summary>
    public static string FullName(string first, string last) =>
        string.IsNullOrWhiteSpace(last) || string.Equals(last.Trim(), "Resident", StringComparison.OrdinalIgnoreCase) ? first : first + " " + last;

    /// <summary>The account name: "first.last" in lower case, or "first" for a last name "Resident".</summary>
    public static string UserName(string fullName)
    {
        var parts = (fullName ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => "",
            1 => parts[0].ToLowerInvariant(),
            _ => string.Equals(parts[1], "Resident", StringComparison.OrdinalIgnoreCase) ? parts[0].ToLowerInvariant() : (parts[0] + "." + parts[1]).ToLowerInvariant(),
        };
    }

    private static readonly UiColour White = TagColours.Text;
    private static readonly UiColour FriendColour = TagColours.Friend;

    /// <summary>Control characters in a title or name become question marks.</summary>
    private static string Clean(string text) => string.Create(text.Length, text, (span, t) =>
    {
        for (int i = 0; i < t.Length; i++)
            span[i] = t[i] < ' ' || t[i] == (char)127 ? '?' : t[i];
    });

    /// <summary>The lines of a tag, top to bottom: states, group title, names.</summary>
    public static IReadOnlyList<NameTagLine> Lines(NameTagInput avatar, NameTagSettings settings, Func<string, string> words)
    {
        string full = Clean(avatar.Names.FullName ?? "").Trim();
        if (full.Length == 0)
            return [];
        var colour = settings.FriendColour && avatar.IsFriend ? FriendColour : White;
        var lines = new List<NameTagLine>();
        var states = new List<string>();
        if (avatar.Animations.Contains(NameTagAnimations.Away)) states.Add(words("nametag.away"));
        if (avatar.Animations.Contains(NameTagAnimations.Busy)) states.Add(words("nametag.busy"));
        if (avatar.IsBlocked && !avatar.IsSelf) states.Add(words("nametag.blocked"));
        if (avatar.Animations.Contains(NameTagAnimations.EditingAppearance)) states.Add(words("nametag.editing"));
        if (states.Count > 0)
            lines.Add(new(string.Join(", ", states), NameTagSize.Small, colour));
        if (settings.GroupTitles && !string.IsNullOrEmpty(avatar.Title))
            lines.Add(new(Clean(avatar.Title), NameTagSize.Small, colour));
        if (avatar.Names.DisplayName is { } display)
        {
            string shown = Clean(display).Trim();
            if (settings.DisplayNames)
                lines.Add(new(shown, NameTagSize.Normal, colour));
            bool same = string.Equals(shown, full, StringComparison.OrdinalIgnoreCase) || string.Equals(shown, UserName(full), StringComparison.OrdinalIgnoreCase);
            if (settings.UserNames && !same)
                lines.Add(new(UserName(full), NameTagSize.Small, colour.Dimmed(0.83f)));
        }
        else
            lines.Add(new(full, NameTagSize.Normal, colour));
        return lines;
    }

    /// <summary>The world point the tag's bottom edge hangs from: the head (taken as 90 % of the body height above the soles), lifted by the body-sized
    /// ellipsoid along the camera's up, less the part of the ellipsoid along the line of sight.</summary>
    public static Vector3 Anchor(NameTagInput avatar, NameTagCamera camera)
    {
        var size = avatar.Size;
        // the drawn head joint when it is known (llvoavatar.cpp, idleCalcNameTagPosition hangs the tag from the head joint), else 90 % of the box
        var head = avatar.Head ?? new Vector3(avatar.Position.X, avatar.Position.Y, avatar.Position.Z - size.Z / 2f + 0.9f * size.Z);
        var toAvatar = avatar.Position - camera.Eye;
        toAvatar = toAvatar.LengthSquared() < 1e-8f ? camera.Forward : Vector3.Normalize(toAvatar);
        var left = Vector3.Normalize(Vector3.Cross(camera.Up, camera.Forward));
        var cameraUp = Vector3.Normalize(Vector3.Cross(toAvatar, left));
        var ellipsoid = new Vector3(0.4f * size.X, 0.4f * size.Y, 0.17f * size.Z);
        var inverse = Quaternion.Inverse(avatar.Rotation);
        var up = Vector3.Transform(Vector3.Transform(cameraUp, inverse) * ellipsoid, avatar.Rotation);
        var at = Vector3.Transform(Vector3.Transform(toAvatar, inverse) * ellipsoid, avatar.Rotation);
        return head + up - toAvatar * Vector3.Dot(at, toAvatar);
    }

    private readonly Func<NameTagSettings> _settings;
    private readonly Func<double> _now;
    private readonly Func<string, string> _words;
    private readonly Dictionary<Guid, (string Text, double Since)> _shown = new();

    public NameTagModel(Func<NameTagSettings> settings, Func<double> now, Func<string, string> words)
    {
        _settings = settings;
        _now = now;
        _words = words;
    }

    /// <summary>The tags to draw this frame for the avatars in view.</summary>
    public IReadOnlyList<NameTagPlan> Update(IReadOnlyList<NameTagInput> avatars, NameTagCamera camera, bool mouselook)
    {
        var settings = _settings();
        double now = _now();
        var plans = new List<NameTagPlan>();
        var present = new HashSet<Guid>();
        foreach (var a in avatars)
        {
            present.Add(a.AgentId);
            if (settings.Mode == NameTagMode.Off || (a.IsSelf && (!settings.ShowSelf || mouselook)))
                continue;
            var lines = Lines(a, settings, _words);
            if (lines.Count == 0)
                continue;
            string text = string.Join((char)10, lines.Select(l => l.Text));
            if (!_shown.TryGetValue(a.AgentId, out var state) || state.Text != text)
                _shown[a.AgentId] = state = (text, now);   // a new or changed tag starts its clock
            float seen = (float)(now - state.Since);
            if (settings.Mode == NameTagMode.Fade && seen >= ShowSeconds)
                continue;
            var anchor = Anchor(a, camera);
            var fromEye = anchor - camera.Eye;
            float along = Vector3.Dot(fromEye, camera.Forward);
            if (fromEye.LengthSquared() < 1e-8f || Vector3.Dot(Vector3.Normalize(fromEye), camera.Forward) <= 0f)
                continue;   // behind the camera
            float radius = a.Size.Length() / 2f;
            float angle = ApparentAngle(Vector3.Distance(a.Position, camera.Eye), radius);
            float alpha = AngleAlpha(angle);
            if (alpha <= 0f)
                continue;
            if (settings.Mode == NameTagMode.Fade && angle > FullAngle)
                alpha *= FadeAlpha(seen);
            // the tag is pushed toward the camera by the avatar's radius, but not past the near plane
            float distance = along <= camera.Near + 0.1f + radius
                ? (fromEye * ((camera.Near + 0.1f) / along)).Length()
                : fromEye.Length() - radius;
            alpha *= DistanceAlpha(distance);
            if (alpha < 0.01f)
                continue;
            plans.Add(new NameTagPlan(a.AgentId, lines, alpha, anchor, distance));
        }
        foreach (var gone in _shown.Keys.Where(k => !present.Contains(k)).ToList())
            _shown.Remove(gone);
        return plans;
    }
}

/// <summary>The settings that decide tags, read from the settings service each frame.</summary>
public sealed record NameTagSettings(
    NameTagMode Mode, bool ShowSelf, bool GroupTitles, bool DisplayNames, bool UserNames, bool FriendColour)
{
    /// <summary>The tag settings as the settings service holds them now.</summary>
    public static NameTagSettings From(SettingsService settings) => new(
        (NameTagMode)settings.Get(Settings.NameTagMode), settings.Get(Settings.NameTagShowSelf), settings.Get(Settings.NameTagGroupTitles),
        settings.Get(Settings.NameTagDisplayNames), settings.Get(Settings.NameTagUserNames), settings.Get(Settings.NameTagFriends));
}
