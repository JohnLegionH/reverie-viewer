// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text.RegularExpressions;

namespace Viewer.Core.Avatars;

/// <summary>
/// The hover height a person sets for their own avatar. The range and the step are the official viewer's
/// (floater_edit_hover_height.xml, -2 to 2 metres in steps of a millimetre). The control works only where the region offers
/// the AgentPreferences capability and its simulator features say AvatarHoverHeightEnabled.
/// </summary>
public static class HoverHeightRules
{
    public const float Minimum = -2f, Maximum = 2f, Step = 0.001f;
    public const string Capability = "AgentPreferences";
    public const string Feature = "AvatarHoverHeightEnabled";

    public static float Clamp(float metres) => float.IsFinite(metres) ? Math.Clamp(metres, Minimum, Maximum) : 0f;

    public static bool Supported(bool hasCapability, bool featureEnabled) => hasCapability && featureEnabled;

    /// <summary>Whether what was typed in the value box is a number: digits with a point or a comma, or a sum of such numbers, which the
    /// engine's value box works out. Anything else is not taken, and the person is told so.</summary>
    public static bool IsTypedNumber(string text) => TypedNumber.IsMatch(text.Replace(',', '.'));

    private static readonly Regex TypedNumber = new(@"^\s*[+-]?(\d+(\.\d*)?|\.\d+)(\s*[-+*/]\s*[+-]?(\d+(\.\d*)?|\.\d+))*\s*$", RegexOptions.CultureInvariant);
}
