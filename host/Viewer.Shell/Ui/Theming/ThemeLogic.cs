// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Reflection;
using System.Text.Json.Nodes;

namespace Viewer.Shell;

/// <summary>
/// The fill for buttons and the active toolbar button drawn in an accent, and the text on it. If white text on the accent
/// reaches 4.5:1 the accent is the fill, with white text. Else, if near-black text (#111111) reaches 7:1 on it, the accent
/// is the fill, with that dark text. Else the accent is darkened in 10 % steps until white text reaches 4.5:1.
/// </summary>
public static class AccentFill
{
    private static readonly UiColour White = new(1f, 1f, 1f);
    private static readonly UiColour Dark = new(17 / 255f, 17 / 255f, 17 / 255f);

    /// <summary>The fill for buttons drawn in <paramref name="accent"/>, and the colour of the text on it.</summary>
    public static (UiColour Fill, UiColour Text) Derive(UiColour accent)
    {
        accent = accent.Opaque();
        if (UiColour.Contrast(White, accent) >= Readability.MinimumRatio)
            return (accent, White);
        if (UiColour.Contrast(Dark, accent) >= 7.0)
            return (accent, Dark);
        var fill = accent;
        for (int i = 0; i < 24 && UiColour.Contrast(White, fill) < Readability.MinimumRatio; i++)
            fill = fill.Scaled(0.9);
        return (fill, White);
    }
}

/// <summary>A theme file: the named values as JSON. Broken files are refused with a message, never half applied.</summary>
public static class ThemePack
{
    public const int FormatVersion = 1;

    /// <summary>What a file gave: the tokens, or the reason it was refused.</summary>
    public sealed record Result(ThemeTokens? Tokens, string? Id, string? Name, string? Error);

    private static readonly string[] Required = ["panel", "bar", "input", "border", "text", "textSecondary", "accent"];

    public static Result Parse(string json) => Parse(json, ThemePresets.Get(ThemePresets.Classic));

    /// <summary>Reads a theme file. Values it leaves out come from <paramref name="fallback"/>; with none, only the sizes default.</summary>
    internal static Result Parse(string json, ThemeTokens? fallback)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(json); }
        catch (System.Text.Json.JsonException e) { return Refuse($"The theme file is not valid json: {e.Message}"); }
        if (node is not JsonObject root)
            return Refuse("The theme file must hold a json object.");
        if (root["format"] is not JsonValue fv || !fv.TryGetValue(out int format) || format != FormatVersion)
            return Refuse($"The theme file's format is not one this viewer reads (expected format {FormatVersion}).");
        if (root["colors"] is not JsonObject colors)
            return Refuse("The theme file has no colors section.");

        var read = new Dictionary<string, UiColour>();
        foreach (var key in Required.Concat(KnownOptional))
        {
            if (colors[key] is null)
            {
                if (Array.IndexOf(Required, key) >= 0 || fallback is null)
                    return Refuse($"The theme file has no value for {key}.");
                continue;
            }
            var c = colors[key] is JsonValue v && v.TryGetValue(out string? text) && text is not null ? ParseColour(text) : null;
            if (c is null)
                return Refuse($"The value for {key} is not a colour (use #rrggbb or #rrggbbaa).");
            read[key] = c.Value;
        }

        var classic = fallback ?? Bootstrap;
        var metrics = root["metrics"] as JsonObject;
        if (!Metric(metrics, "radius", classic.Radius, 0, 16, out int radius, out var err)
            || !Metric(metrics, "borderWidth", classic.BorderWidth, 0, 4, out int border, out err)
            || !Metric(metrics, "fontBody", classic.FontBody, 8, 32, out int body, out err)
            || !Metric(metrics, "fontCaption", classic.FontCaption, 8, 32, out int caption, out err)
            || !Metric(metrics, "fontTitle", classic.FontTitle, 8, 32, out int title, out err))
            return Refuse(err!);

        UiColour Get(string key, UiColour otherwise) => read.TryGetValue(key, out var c) ? c : otherwise;
        var accent = read["accent"];
        UiColour fill, fillText;
        if (read.TryGetValue("accentFill", out var givenFill))
        {
            fill = givenFill;
            fillText = read.TryGetValue("accentFillText", out var givenText) ? givenText
                : UiColour.Contrast(new UiColour(1f, 1f, 1f), fill) >= Readability.MinimumRatio ? new UiColour(1f, 1f, 1f) : new UiColour(17 / 255f, 17 / 255f, 17 / 255f);
        }
        else
        {
            (fill, fillText) = AccentFill.Derive(accent);
            if (read.TryGetValue("accentFillText", out var givenText))
                fillText = givenText;
        }
        var tokens = new ThemeTokens(
            read["panel"], read["bar"], read["input"], read["border"], read["text"], read["textSecondary"], accent, fill, fillText,
            Get("dangerFill", classic.DangerFill), Get("warning", classic.Warning), Get("warningText", classic.WarningText),
            Get("success", classic.Success), Get("chatOwn", classic.ChatOwn), Get("chatAgent", read["text"]),
            Get("chatObject", classic.ChatObject), Get("chatSystem", classic.ChatSystem), Get("chatTyping", read["textSecondary"]),
            Get("error", classic.Error), Get("outline", classic.Outline), Get("scrim", classic.Scrim), radius, border, body, caption, title);
        string id = root["id"] is JsonValue iv && iv.TryGetValue(out string? s) && !string.IsNullOrWhiteSpace(s) ? s : "imported";
        string name = root["name"] is JsonValue nv && nv.TryGetValue(out string? n) && !string.IsNullOrWhiteSpace(n) ? n : id;
        return new Result(tokens, id, name, null);
    }

    /// <summary>Stands in for the fallback while the built-in packs load: every colour is required then, so only its sizes are read.</summary>
    private static readonly ThemeTokens Bootstrap = new(default, default, default, default, default, default, default, default, default,
        default, default, default, default, default, default, default, default, default, default, default, default, 4, 1, 13, 11, 13);

    private static readonly string[] KnownOptional =
        ["accentFill", "accentFillText", "dangerFill", "warning", "warningText", "success", "chatOwn", "chatAgent", "chatObject",
         "chatSystem", "chatTyping", "error", "outline", "scrim"];

    private static Result Refuse(string message) => new(null, null, null, message);

    private static bool Metric(JsonObject? metrics, string key, int fallback, int min, int max, out int value, out string? error)
    {
        value = fallback;
        error = null;
        if (metrics?[key] is null)
            return true;
        if (metrics[key] is not JsonValue v || !v.TryGetValue(out int got) || got < min || got > max)
        {
            error = $"The value for {key} must be a whole number from {min} to {max}.";
            return false;
        }
        value = got;
        return true;
    }

    /// <summary>The file text for a theme: every value named, so a file stands alone.</summary>
    public static string Write(string id, string name, ThemeTokens tokens)
    {
        var colors = new JsonObject();
        foreach (var (key, colour) in tokens.Colours)
            colors[key] = FormatColour(colour);
        var root = new JsonObject
        {
            ["format"] = FormatVersion,
            ["id"] = id,
            ["name"] = name,
            ["colors"] = colors,
            ["metrics"] = new JsonObject
            {
                ["radius"] = tokens.Radius, ["borderWidth"] = tokens.BorderWidth, ["fontBody"] = tokens.FontBody,
                ["fontCaption"] = tokens.FontCaption, ["fontTitle"] = tokens.FontTitle,
            },
        };
        return root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>"#rrggbb" or "#rrggbbaa", or null when the text is neither.</summary>
    public static UiColour? ParseColour(string text)
    {
        if (text is not { Length: 7 or 9 } || text[0] != '#')
            return null;
        int Pair(int at) => int.TryParse(text.AsSpan(at, 2), System.Globalization.NumberStyles.AllowHexSpecifier, null, out int v) && text[at] != '+' && text[at] != '-' ? v : -1;
        int r = Pair(1), g = Pair(3), b = Pair(5), a = text.Length == 9 ? Pair(7) : 255;
        if (r < 0 || g < 0 || b < 0 || a < 0)
            return null;
        return new UiColour(r / 255f, g / 255f, b / 255f, a / 255f);
    }

    /// <summary>"#rrggbb", or "#rrggbbaa" when the colour is not fully opaque.</summary>
    public static string FormatColour(UiColour c)
    {
        string rgb = c.Hex;
        int a = (int)Math.Round(Math.Clamp(c.A, 0f, 1f) * 255f);
        return a == 255 ? rgb : rgb + a.ToString("x2");
    }
}

/// <summary>The built-in presets, as data packs embedded in the assembly.</summary>
public static class ThemePresets
{
    public const string Classic = "classic";
    public const string ClassicLight = "classic-light";
    public const string PowerUser = "power-user";
    public const string HighContrast = "high-contrast";

    private static readonly string[] AllIds = [Classic, ClassicLight, PowerUser, HighContrast];
    private static readonly Lazy<Dictionary<string, ThemePack.Result>> Packs = new(Load);

    public static IReadOnlyList<string> Ids => AllIds;

    /// <summary>The preset's tokens; the Classic ones for an id that is not a preset.</summary>
    public static ThemeTokens Get(string id) =>
        ReverieTheme.VariantOf(id) is { } variant ? ReverieTheme.ToThemeTokens(ReverieTheme.Tokens(variant))
        : (Packs.Value.TryGetValue(id, out var pack) ? pack : Packs.Value[Classic]).Tokens!;

    /// <summary>Whether the id names a theme this viewer has built in: a preset pack or a Reverie variant.</summary>
    public static bool Known(string id) => Array.IndexOf(AllIds, id) >= 0 || ReverieTheme.VariantOf(id) is not null;

    public static string NameOf(string id) => Packs.Value.TryGetValue(id, out var pack) ? pack.Name! : id;

    private static Dictionary<string, ThemePack.Result> Load()
    {
        var assembly = typeof(ThemePresets).Assembly;
        var all = new Dictionary<string, ThemePack.Result>();
        foreach (var id in AllIds)
        {
            using var stream = assembly.GetManifestResourceStream($"Viewer.Shell.Themes.{id}.json")
                ?? throw new InvalidOperationException($"The theme pack {id} is not in the assembly.");
            using var reader = new StreamReader(stream);
            var result = ThemePack.Parse(reader.ReadToEnd(), null);
            all[id] = result.Error is null ? result : throw new InvalidOperationException($"The theme pack {id} is broken: {result.Error}");
        }
        return all;
    }
}

/// <summary>
/// What one person chose: a preset (or an imported theme file) plus changes on top of it. Colours that are null are the
/// preset's own. Kept as text in the settings file, one per account.
/// </summary>
public sealed record ThemeChoice(
    string Preset, UiColour? Accent, UiColour? Panel, UiColour? Bar, UiColour? Text, UiColour? TextSecondary, int ScalePercent,
    bool UseGridAccent, string? ImportedPack)
{
    /// <summary>The preset name that stands for a theme file the person imported.</summary>
    public const string ImportedPreset = "imported";

    public static ThemeChoice Default => new(ReverieTheme.DarkId, null, null, null, null, null, 100, false, null);

    public string ToJson()
    {
        var o = new JsonObject { ["preset"] = Preset };
        void Put(string key, UiColour? c) { if (c is { } v) o[key] = ThemePack.FormatColour(v); }
        Put("accent", Accent);
        Put("panel", Panel);
        Put("bar", Bar);
        Put("text", Text);
        Put("textSecondary", TextSecondary);
        o["scale"] = ScalePercent;
        o["useGridAccent"] = UseGridAccent;
        if (ImportedPack is not null)
            o["pack"] = ImportedPack;
        return o.ToJsonString();
    }

    /// <summary>A stored choice, or null when the text is not one (not json, an unknown preset, a colour that is not a colour).</summary>
    public static ThemeChoice? FromJson(string json)
    {
        JsonObject? o;
        try { o = JsonNode.Parse(json) as JsonObject; }
        catch (System.Text.Json.JsonException) { return null; }
        if (o?["preset"] is not JsonValue pv || !pv.TryGetValue(out string? preset) || preset is null)
            return null;
        string? pack = o["pack"] is JsonValue kv && kv.TryGetValue(out string? p) ? p : null;
        if (preset == ImportedPreset ? pack is null || ThemePack.Parse(pack).Error is not null : !ThemePresets.Known(preset))
            return null;
        bool ok = true;
        UiColour? Read(string key)
        {
            if (o[key] is null)
                return null;
            var c = o[key] is JsonValue v && v.TryGetValue(out string? text) && text is not null ? ThemePack.ParseColour(text) : null;
            ok &= c is not null;
            return c;
        }
        var accent = Read("accent");
        var panel = Read("panel");
        var bar = Read("bar");
        var textColour = Read("text");
        var secondary = Read("textSecondary");
        if (!ok)
            return null;
        int scale = o["scale"] is JsonValue sv && sv.TryGetValue(out int s) ? Math.Clamp(s, InterfacePreferences.MinScalePercent, InterfacePreferences.MaxScalePercent) : 100;
        bool grid = o["useGridAccent"] is JsonValue gv && gv.TryGetValue(out bool g) && g;
        return new ThemeChoice(preset, accent, panel, bar, textColour, secondary, scale, grid, preset == ImportedPreset ? pack : null);
    }

    /// <summary>A theme file as a choice, or null with the reason it was refused.</summary>
    public static ThemeChoice? Import(string fileText, out string? error)
    {
        var parsed = ThemePack.Parse(fileText);
        error = parsed.Error;
        return parsed.Error is null ? new ThemeChoice(ImportedPreset, null, null, null, null, null, 100, false, fileText) : null;
    }
}

public static class ThemeResolver
{
    /// <summary>The tokens a choice gives. <paramref name="gridAccent"/> is used only when the choice asks for it.</summary>
    public static ThemeTokens Resolve(ThemeChoice choice, UiColour? gridAccent)
    {
        ThemeTokens t = choice.Preset == ThemeChoice.ImportedPreset && choice.ImportedPack is { } pack && ThemePack.Parse(pack).Tokens is { } imported
            ? imported
            : ThemePresets.Get(choice.Preset);
        var accent = (choice.UseGridAccent ? gridAccent : null) ?? choice.Accent;
        if (ReverieTheme.VariantOf(choice.Preset) is { } variant)
            t = ReverieTheme.ToThemeTokens(ReverieTheme.Tokens(variant, accent));   // the Reverie rule for text on the accent
        else if (accent is { } a && a.Quantized() != t.Accent)
        {
            var (fill, fillText) = AccentFill.Derive(a.Quantized());
            t = t with { Accent = a.Quantized(), AccentFill = fill, AccentFillText = fillText };
        }
        if (choice.Panel is { } panel)
            t = t with { Panel = panel.Quantized() with { A = t.Panel.A } };
        if (choice.Bar is { } bar)
            t = t with { Bar = bar.Quantized() };
        if (choice.Text is { } text)
            t = t with { Text = text.Quantized() };
        if (choice.TextSecondary is { } secondary)
            t = t with { TextSecondary = secondary.Quantized() };
        return t;
    }
}

/// <summary>One line of the Preferences readability list: the pair's key, its contrast ratio, and whether it reaches 4.5:1.</summary>
public sealed record ReadabilityRow(string Key, double Ratio, bool Readable);

public static class Readability
{
    public const double MinimumRatio = 4.5;

    /// <summary>Text on a panel, secondary text on a panel, the accent on a bar, and button text on its fill. A panel is judged
    /// at full opacity, the worst case for a light theme over a dark world and the best for a dark one over a light world.</summary>
    public static IReadOnlyList<ReadabilityRow> Check(ThemeTokens t)
    {
        var panel = t.Panel.Opaque();
        ReadabilityRow Row(string key, UiColour a, UiColour b)
        {
            double ratio = UiColour.Contrast(a, b);
            return new ReadabilityRow(key, ratio, ratio >= MinimumRatio);
        }
        return
        [
            Row("text-on-panel", t.Text, panel),
            Row("secondary-on-panel", t.TextSecondary, panel),
            Row("accent-on-bar", t.Accent, t.Bar),
            Row("button-text", t.AccentFillText, t.AccentFill),
        ];
    }
}

/// <summary>Keeps a person's theme choice in the settings file: one per account, and the last one saved for the login screen
/// and for an account that has not saved one.</summary>
public static class ThemeStore
{
    private const string LastKey = "ui_theme_last";

    private static string KeyFor(string accountKey)
    {
        var sb = new System.Text.StringBuilder("ui_theme_");
        foreach (char c in accountKey.Trim().ToLowerInvariant())
            sb.Append(char.IsLetterOrDigit(c) || c is '.' or '-' ? c : '_');
        return sb.ToString();
    }

    /// <summary>Whether the scale in a stored look is the person's own choice (else the display's scale applies).</summary>
    public static bool ScaleOwn(SettingsService settings, string? accountKey, ThemeChoice choice)
    {
        var flag = (accountKey is null ? null : settings.GetText(KeyFor(accountKey) + OwnSuffix)) ?? settings.GetText(LastKey + OwnSuffix);
        // a look saved before the display's scale was followed has no flag: a scale other than 100 was the person's choice
        return flag is null ? choice.ScalePercent != 100 : flag == "1";
    }

    private const string OwnSuffix = "_scale_own";

    /// <summary>The scale to draw the interface at: the person's own, else the display's scale.</summary>
    public static int EffectiveScale(SettingsService settings, string? accountKey, ThemeChoice choice, int displayPercent) =>
        ScaleOwn(settings, accountKey, choice) ? choice.ScalePercent : Math.Clamp(displayPercent, InterfaceSize.MinPercent, InterfaceSize.MaxPercent);

    /// <summary>False when the settings file could not be written. <paramref name="scaleOwn"/>, when given, records whether the
    /// scale in the choice is the person's own.</summary>
    public static bool Save(SettingsService settings, string accountKey, ThemeChoice choice, bool? scaleOwn = null)
    {
        string json = choice.ToJson();
        bool own = settings.SetText(KeyFor(accountKey), json);
        bool last = settings.SetText(LastKey, json);
        bool flag = true;
        if (scaleOwn is { } v)
            flag = settings.SetText(KeyFor(accountKey) + OwnSuffix, v ? "1" : "0") & settings.SetText(LastKey + OwnSuffix, v ? "1" : "0");
        return own && last && flag;
    }

    /// <summary>The account's own choice; else the last one saved; else the default. A stored text that cannot be read is skipped.</summary>
    public static ThemeChoice Load(SettingsService settings, string? accountKey)
    {
        if (accountKey is not null && settings.GetText(KeyFor(accountKey)) is { } own && ThemeChoice.FromJson(own) is { } mine)
            return mine;
        if (settings.GetText(LastKey) is { } last && ThemeChoice.FromJson(last) is { } lastChoice)
            return lastChoice;
        return ThemeChoice.Default;
    }
}

/// <summary>The accent colours the Interface page offers as swatches, by name (the custom colour is the person's own).</summary>
public static class ThemeSwatches
{
    public static IReadOnlyList<(string Key, UiColour Colour)> Accents { get; } =
    [
        ("blue", Hex("#4c9aff")), ("teal", Hex("#2dd4bf")), ("green", Hex("#4ade80")),
        ("amber", Hex("#f59e0b")), ("rose", Hex("#f472b6")), ("violet", Hex("#c084fc")),
    ];

    private static UiColour Hex(string text) => ThemePack.ParseColour(text)!.Value;
}
