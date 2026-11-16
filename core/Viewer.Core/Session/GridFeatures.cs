// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Globalization;

namespace Viewer.Core.Session;

/// <summary>The values the login reply gave that the grid's own region settings can replace (null when the login reply had none).</summary>
public sealed record GridLoginValues(string? MapServerUrl = null, string? SearchUrl = null, string? DestinationGuideUrl = null,
    string? AvatarPickerUrl = null, string? Currency = null, string? CurrencyHelper = null);

/// <summary>
/// The grid's own settings from the region's simulator features ("OpenSimExtras"), with the defaults that apply where a grid sends
/// none of them. The keys and their fallbacks follow Firestorm's reading of them (lfsimfeaturehandler.cpp, setSupportedFeatures):
/// the region's value first, then the login reply's, then Second Life's (say 20 m, shout 100 m, whisper 10 m, the official viewer's
/// CHAT_NORMAL_RADIUS, CHAT_SHOUT_RADIUS and CHAT_WHISPER_RADIUS). The prim size and height limits are read separately, by the edit
/// limits (Building.EditLimits).
/// </summary>
public sealed record GridFeatures(int SayRange, int ShoutRange, int WhisperRange, string? MapServerUrl, string? SearchUrl,
    string? DestinationGuideUrl, string? AvatarPickerUrl, string? CurrencySymbol, string? CurrencyBaseUri, bool ExportSupported,
    string? GridUrl, string? GridStatusUrl, string? GridStatusRss, bool FromGrid)
{
    public const int DefaultSayRange = 20, DefaultShoutRange = 100, DefaultWhisperRange = 10;

    /// <summary>The symbol Firestorm uses on an OpenSim grid that has a currency but names no symbol (llstartup.cpp).</summary>
    public const string UnnamedCurrency = "OS$";

    /// <summary>The settings with no OpenSim extras and nothing from the login reply.</summary>
    public static GridFeatures Defaults { get; } = From(null, null);

    /// <summary>The settings from the region's OpenSim extras (null when it sent none), falling back to the login reply's values.</summary>
    public static GridFeatures From(IReadOnlyDictionary<string, object?>? extras, GridLoginValues? login)
    {
        login ??= new GridLoginValues();
        string? baseUri = Text(extras, "currency-base-uri");
        string? symbol = Text(extras, "currency") ?? NonEmpty(login.Currency) ?? (baseUri is not null ? UnnamedCurrency : null);
        string? gridUrl = Text(extras, "GridURL");
        if (gridUrl is not null && gridUrl.IndexOf("://", StringComparison.Ordinal) is var at and >= 0)
            gridUrl = gridUrl[(at + 3)..];
        return new GridFeatures(
            Whole(extras, "say-range") ?? DefaultSayRange,
            Whole(extras, "shout-range") ?? DefaultShoutRange,
            Whole(extras, "whisper-range") ?? DefaultWhisperRange,
            Text(extras, "map-server-url") ?? NonEmpty(login.MapServerUrl),
            Text(extras, "search-server-url") ?? NonEmpty(login.SearchUrl),
            Text(extras, "destination-guide-url") ?? NonEmpty(login.DestinationGuideUrl),
            Text(extras, "avatar-picker-url") ?? NonEmpty(login.AvatarPickerUrl),
            symbol,
            baseUri,
            extras is not null && extras.TryGetValue("ExportSupported", out var e) && e is bool b && b,
            gridUrl,
            Text(extras, "GridStatus"),
            Text(extras, "GridStatusRSS"),
            extras is not null);
    }

    /// <summary>Whether the grid has a currency, so a balance is shown: the region or the login reply named one, or the region gave a
    /// currency helper address.</summary>
    public bool HasCurrency => CurrencySymbol is not null;

    /// <summary>The balance as the status bar shows it, or null on a grid with no currency.</summary>
    public string? BalanceText(int balance) =>
        HasCurrency ? CurrencySymbol + balance.ToString("N0", CultureInfo.InvariantCulture) : null;

    /// <summary>Whether a line spoken at <paramref name="distance"/> metres is beyond the grid's say range. Firestorm dims nearby chat
    /// from beyond the grid's say range (llviewerchat.cpp, formatChatMsg).</summary>
    public bool BeyondSayRange(float distance) => distance > SayRange;

    private static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static string? Text(IReadOnlyDictionary<string, object?>? extras, string key) =>
        extras is not null && extras.TryGetValue(key, out var v) && v is string s ? NonEmpty(s) : null;

    private static int? Whole(IReadOnlyDictionary<string, object?>? extras, string key)
    {
        if (extras is null || !extras.TryGetValue(key, out var v))
            return null;
        return v switch
        {
            long l when l is >= 0 and <= int.MaxValue => (int)l,
            int i when i >= 0 => i,
            double d when d >= 0 && d <= int.MaxValue => (int)d,
            _ => null,
        };
    }
}
