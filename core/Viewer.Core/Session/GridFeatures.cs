// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Globalization;

namespace Viewer.Core.Session;

/// <summary>The values the login reply (or, behind it, the grid's own grid-info document) gave that the grid's own region settings can
/// replace (null when neither had one). <paramref name="CurrencyHelper"/> is the grid's currency helper address.</summary>
public sealed record GridLoginValues(string? MapServerUrl = null, string? SearchUrl = null, string? DestinationGuideUrl = null,
    string? AvatarPickerUrl = null, string? Currency = null, string? CurrencyHelper = null, string? GridName = null,
    string? InfoCurrency = null, string? CurrencyName = null, IReadOnlyList<string>? HomeHosts = null);

/// <summary>
/// The grid's own settings from the region's simulator features ("OpenSimExtras"), with the defaults that apply where a grid sends
/// none of them. The keys and their fallbacks follow Firestorm's reading of them (lfsimfeaturehandler.cpp, setSupportedFeatures):
/// the region's value first, then the login reply's, then Second Life's (say 20 m, shout 100 m, whisper 10 m, the official viewer's
/// CHAT_NORMAL_RADIUS, CHAT_SHOUT_RADIUS and CHAT_WHISPER_RADIUS). The prim size and height limits are read separately, by the edit
/// limits (Building.EditLimits).
/// </summary>
public sealed record GridFeatures(int SayRange, int ShoutRange, int WhisperRange, string? MapServerUrl, string? SearchUrl,
    string? DestinationGuideUrl, string? AvatarPickerUrl, string? CurrencySymbol, string? CurrencyBaseUri, bool ExportSupported,
    string? GridUrl, string? GridStatusUrl, string? GridStatusRss, bool FromGrid, string? GridName = null, string? CurrencyName = null,
    bool NoCurrency = false, bool Visiting = false, string? HomeCurrencySymbol = null, string? VisitedHost = null)
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
        string? gridUrl = Text(extras, "GridURL");
        if (gridUrl is not null && gridUrl.IndexOf("://", StringComparison.Ordinal) is var at and >= 0)
            gridUrl = gridUrl[(at + 3)..];
        string? regionName = Plain(Text(extras, "GridName"), MaxGridName);
        bool visiting = IsVisiting(gridUrl, regionName, login);

        // The login reply and the grid-info document describe the grid the person logged in to. While the region belongs to another
        // grid (a Hypergrid visit) they do not describe it, so only the region's own values stand for the visited grid.
        string? baseUri = Text(extras, "currency-base-uri") ?? (visiting ? null : NonEmpty(login.CurrencyHelper));
        string? named = Plain(Text(extras, "currency"), MaxSymbol) ?? (visiting ? null : NonEmpty(login.Currency) ?? NonEmpty(login.InfoCurrency));
        bool none = IsNone(named);
        string? symbol = none ? null : named ?? (baseUri is not null ? UnnamedCurrency : null);

        // the balance is the home grid's money, in the home grid's symbol
        string? homeNamed = NonEmpty(login.Currency) ?? NonEmpty(login.InfoCurrency);
        string? home = visiting
            ? (IsNone(homeNamed) ? null : homeNamed ?? (NonEmpty(login.CurrencyHelper) is not null ? UnnamedCurrency : null))
            : symbol;
        string? currencyName = none ? null : NonEmpty(login.CurrencyName) is { } cn && !visiting ? cn : symbol;
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
            extras is not null,
            regionName ?? (visiting ? null : Plain(NonEmpty(login.GridName), MaxGridName)),
            currencyName,
            none,
            visiting,
            home,
            visiting ? HostPort(gridUrl) : null);
    }

    /// <summary>The longest grid name and currency symbol shown (the spec's limits); a longer one, or one with control characters, is ignored.</summary>
    public const int MaxGridName = 40, MaxSymbol = 6;

    private static string? Plain(string? text, int max) =>
        text is not null && text.Length <= max && !text.Any(char.IsControl) ? text : null;

    /// <summary>The word a grid sets as its currency to say it has no money (compared without regard to case).</summary>
    public const string NoCurrencyWord = "none";

    private static bool IsNone(string? text) => text is not null && text.Trim().Equals(NoCurrencyWord, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the region belongs to a grid other than the one logged in to. The region's grid address is compared with the
    /// home grid's hosts (without port or case); a region that sends no address is compared by its grid name; a region that sends
    /// neither is taken as home.</summary>
    private static bool IsVisiting(string? regionGridUrl, string? regionName, GridLoginValues login)
    {
        if (regionGridUrl is not null && login.HomeHosts is { Count: > 0 } hosts)
        {
            string host = HostOnly(regionGridUrl);
            if (host.Length == 0 || hosts.Any(h => HostOnly(h).Equals(host, StringComparison.OrdinalIgnoreCase)))
                return false;
            // another name for the same grid (a LAN address against a public name, an alias): a region that names the home grid is home
            return !(regionName is not null && NonEmpty(login.GridName) is { } known && known.Equals(regionName, StringComparison.OrdinalIgnoreCase));
        }
        return regionName is not null && NonEmpty(login.GridName) is { } home && !home.Equals(regionName, StringComparison.OrdinalIgnoreCase);
    }

    private static string HostPort(string? gridUrl) => gridUrl is null ? "" : gridUrl.TrimEnd('/').Split('/')[0];

    private static string HostOnly(string address)
    {
        string h = HostPort(address);
        if (h.StartsWith('['))
            return h;   // an IPv6 literal is kept whole
        int colon = h.LastIndexOf(':');
        return colon > 0 ? h[..colon] : h;
    }

    /// <summary>The settings as Second Life has them: a grid-supplied name, symbol or currency name is never applied there (the
    /// ranges, map and search addresses are kept, as they are the region's own settings).</summary>
    public GridFeatures OnSecondLife() => this with
    {
        GridName = null, CurrencyName = null, NoCurrency = false, Visiting = false, VisitedHost = null, CurrencySymbol = null, HomeCurrencySymbol = null,
    };

    /// <summary>The symbol the prices and fees on screen carry: Second Life's L$ on Second Life, nothing on a grid that says it has
    /// no money, otherwise the grid's own symbol or the unnamed-currency one.</summary>
    public string PriceSymbol(bool secondLife) => secondLife ? SecondLifeSymbol : NoCurrency ? "" : CurrencySymbol ?? UnnamedCurrency;

    /// <summary>Second Life's currency symbol (the official viewer's), used on Second Life whatever a grid document says.</summary>
    public const string SecondLifeSymbol = "L$";

    /// <summary>Whether the grid has a currency, so a balance is shown: the region or the login reply named one, or the region gave a
    /// currency helper address.</summary>
    public bool HasCurrency => CurrencySymbol is not null;

    /// <summary>The balance as the status bar shows it, or null on a grid with no currency.</summary>
    public string? BalanceText(int balance) =>
        HomeCurrencySymbol is { } symbol ? symbol + balance.ToString("N0", CultureInfo.InvariantCulture) : null;

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
