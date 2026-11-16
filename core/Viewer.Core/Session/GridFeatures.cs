// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Session;

/// <summary>The values the login reply gave that the grid's own region settings can replace (null when the login reply had none).</summary>
public sealed record GridLoginValues(string? MapServerUrl = null, string? SearchUrl = null, string? DestinationGuideUrl = null,
    string? AvatarPickerUrl = null, string? Currency = null);

/// <summary>
/// The grid's own settings from the region's simulator features ("OpenSimExtras"), with the defaults that apply where a grid sends
/// none of them.
/// </summary>
public sealed record GridFeatures(int SayRange, int ShoutRange, int WhisperRange, string? MapServerUrl, string? SearchUrl,
    string? DestinationGuideUrl, string? AvatarPickerUrl, string? CurrencySymbol, string? CurrencyBaseUri, bool ExportSupported,
    string? GridUrl, string? GridStatusUrl, string? GridStatusRss, bool FromGrid)
{
    public const int DefaultSayRange = 20, DefaultShoutRange = 100, DefaultWhisperRange = 10;

    /// <summary>The settings with no OpenSim extras and nothing from the login reply.</summary>
    public static GridFeatures Defaults { get; } = new(0, 0, 0, null, null, null, null, null, null, false, null, null, null, false);

    /// <summary>The settings from the region's OpenSim extras (null when it sent none), falling back to the login reply's values.</summary>
    public static GridFeatures From(IReadOnlyDictionary<string, object?>? extras, GridLoginValues? login) => Defaults;

    /// <summary>Whether the grid has a currency, so a balance is shown.</summary>
    public bool HasCurrency => false;

    /// <summary>The balance as the status bar shows it, or null on a grid with no currency.</summary>
    public string? BalanceText(int balance) => null;

    /// <summary>Whether a line spoken at <paramref name="distance"/> metres is beyond the grid's say range.</summary>
    public bool BeyondSayRange(float distance) => false;
}
