// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell.Sound;

/// <summary>
/// OpenSim's region restart warning as plain text. OpenSim's classic restart module sends the warning as an alert without the official
/// notification id, in one of two shapes: the estate restart's "Region will restart in 2 minutes" and osRegionRestart's "Region: Name is
/// about to restart. ... Time remaining: 30 seconds." (the time being "N minute(s)", "N second(s)" or both joined by " and "). Newer
/// OpenSim sends the official RegionRestartSeconds id, which needs no text match.
/// </summary>
public static class OpenSimRestartNotice
{
    /// <summary>Whether an alert's text is OpenSim's restart warning.</summary>
    public static bool Matches(string? text) => !string.IsNullOrEmpty(text) && (Estate.IsMatch(text) || Scripted.IsMatch(text));

    // the time as OpenSim spells it: "1 minute", "2 minutes", "1 second", "30 seconds", or minutes " and " seconds
    private const string Time = @"\d+ minutes?(?: and \d+ seconds?)?|\d+ seconds?";

    // the estate restart (EstateManagementModule): "Region will restart in {0}"
    private static readonly System.Text.RegularExpressions.Regex Estate =
        new(@"^\s*Region will restart in (?:" + Time + @")\.?\s*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    // osRegionRestart (its own text, or the script's message) ends with "Time remaining: {0}."
    private static readonly System.Text.RegularExpressions.Regex Scripted =
        new(@"Time remaining: (?:" + Time + @")\.\s*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
}
