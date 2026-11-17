// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Globalization;

namespace Viewer.Shell;

/// <summary>Colour conversions for the colour picker: hue, saturation and value for its square and strip; hue, saturation and lightness
/// and red, green and blue for its fields (the official picker shows R, G, B from 0 to 255, H from 0 to 360, S and L from 0 to 100:
/// floater_color_picker.xml); and the six- or eight-digit hex form.</summary>
public static class ColourMath
{
    /// <summary>Hue (0 to 1), saturation and value (0 to 1) of a colour.</summary>
    public static (float H, float S, float V) ToHsv(UiColour c)
    {
        float max = MathF.Max(c.R, MathF.Max(c.G, c.B)), min = MathF.Min(c.R, MathF.Min(c.G, c.B)), d = max - min;
        return (Hue(c, max, d), max <= 0 ? 0 : d / max, max);
    }

    /// <summary>The colour of a hue, saturation and value (0 to 1 each).</summary>
    public static UiColour FromHsv(float h, float s, float v, float a = 1f)
    {
        h = (h % 1f + 1f) % 1f * 6f;
        float c = v * s, x = c * (1 - MathF.Abs(h % 2f - 1)), m = v - c;
        var (r, g, b) = Sector(h, c, x);
        return new(r + m, g + m, b + m, a);
    }

    /// <summary>Hue (0 to 1), saturation and lightness (0 to 1) of a colour.</summary>
    public static (float H, float S, float L) ToHsl(UiColour c)
    {
        float max = MathF.Max(c.R, MathF.Max(c.G, c.B)), min = MathF.Min(c.R, MathF.Min(c.G, c.B)), d = max - min, l = (max + min) / 2;
        float s = d <= 0 ? 0 : d / (1 - MathF.Abs(2 * l - 1));
        return (Hue(c, max, d), Math.Clamp(s, 0, 1), l);
    }

    /// <summary>The colour of a hue, saturation and lightness (0 to 1 each).</summary>
    public static UiColour FromHsl(float h, float s, float l, float a = 1f)
    {
        h = (h % 1f + 1f) % 1f * 6f;
        float c = (1 - MathF.Abs(2 * l - 1)) * s, x = c * (1 - MathF.Abs(h % 2f - 1)), m = l - c / 2;
        var (r, g, b) = Sector(h, c, x);
        return new(r + m, g + m, b + m, a);
    }

    private static float Hue(UiColour c, float max, float d)
    {
        if (d <= 0) return 0;
        float h = max == c.R ? (c.G - c.B) / d % 6 : max == c.G ? (c.B - c.R) / d + 2 : (c.R - c.G) / d + 4;
        return (h / 6f % 1f + 1f) % 1f;
    }

    private static (float, float, float) Sector(float h, float c, float x) => (int)h switch
    {
        0 => (c, x, 0), 1 => (x, c, 0), 2 => (0, c, x), 3 => (0, x, c), 4 => (x, 0, c), _ => (c, 0, x),
    };

    private static int Byte(float v) => (int)MathF.Round(Math.Clamp(v, 0, 1) * 255);

    /// <summary>"#RRGGBB", or "#RRGGBBAA" with <paramref name="alpha"/>.</summary>
    public static string ToHex(UiColour c, bool alpha = false) =>
        "#" + Byte(c.R).ToString("X2", CultureInfo.InvariantCulture) + Byte(c.G).ToString("X2", CultureInfo.InvariantCulture)
        + Byte(c.B).ToString("X2", CultureInfo.InvariantCulture) + (alpha ? Byte(c.A).ToString("X2", CultureInfo.InvariantCulture) : "");

    /// <summary>Reads "#RGB", "#RRGGBB" or "#RRGGBBAA" (the # may be left out); false for anything else.</summary>
    public static bool TryParseHex(string? text, out UiColour colour)
    {
        colour = default;
        var t = (text ?? "").Trim().TrimStart('#');
        if (t.Length == 3)
            t = string.Concat(t.Select(ch => new string(ch, 2)));
        if (t.Length is not (6 or 8) || !uint.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v))
            return false;
        if (t.Length == 6)
            v = v << 8 | 0xFF;
        colour = new((v >> 24 & 0xFF) / 255f, (v >> 16 & 0xFF) / 255f, (v >> 8 & 0xFF) / 255f, (v & 0xFF) / 255f);
        return true;
    }

    /// <summary>Whether two colours are the same to the byte (what the fields show).</summary>
    public static bool Same(UiColour a, UiColour b) => Byte(a.R) == Byte(b.R) && Byte(a.G) == Byte(b.G) && Byte(a.B) == Byte(b.B) && Byte(a.A) == Byte(b.A);
}

/// <summary>What a caller hands the shared colour picker: the window's title, the colour now, whether transparency may be chosen, what
/// to do with a colour (shown on the target as it is chosen, or on OK), and what to do when the old colour is put back.</summary>
public sealed record ColourRequest(string TitleKey, UiColour Was, bool AllowAlpha, Action<UiColour> Apply, Action<UiColour>? Revert = null);

/// <summary>
/// One opening of the shared colour picker. With "Apply as I choose" on (the official Apply now, ApplyColorImmediately, on by default:
/// llfloatercolorpicker.cpp onImmediateCheck) each change is shown on the target at once; off, the target changes only on OK. OK keeps
/// the colour and adds it to the recent colours (llfloatercolorpicker.cpp onClickSelect, 407-420); Cancel, or the window closing any
/// other way, puts the old colour back (onClickCancel, 391-403). Once ended, nothing more is applied or put back. Main thread only.
/// </summary>
public sealed class ColourSession
{
    private readonly ColourRequest _request;
    private readonly ColourPalette _palette;
    private bool _applied;

    public ColourSession(ColourRequest request, ColourPalette palette, bool applyAsIChoose)
    {
        _request = request;
        _palette = palette;
        Was = request.AllowAlpha ? request.Was : request.Was with { A = 1f };
        New = Was;
        ApplyAsIChoose = applyAsIChoose;
    }

    /// <summary>The colour the target had when the picker opened (the "Was" swatch).</summary>
    public UiColour Was { get; }

    /// <summary>The colour being chosen (the "New" swatch).</summary>
    public UiColour New { get; private set; }

    public bool AllowAlpha => _request.AllowAlpha;
    public string TitleKey => _request.TitleKey;
    public bool Ended { get; private set; }
    public bool Kept { get; private set; }

    private bool _live;

    /// <summary>"Apply as I choose": turned on with a changed colour, the target takes it at once (as the official check box does).</summary>
    public bool ApplyAsIChoose
    {
        get => _live;
        set
        {
            _live = value;
            if (value && !Ended && !ColourMath.Same(New, Was))
                Push(New);
        }
    }

    /// <summary>A colour chosen in the window (square, strip, field, swatch or Pick).</summary>
    public void Choose(UiColour colour)
    {
        if (Ended)
            return;
        New = AllowAlpha ? colour : colour with { A = 1f };
        if (ApplyAsIChoose)
            Push(New);
    }

    private void Push(UiColour c)
    {
        _applied = true;
        _request.Apply(c);
    }

    /// <summary>OK: the target keeps the new colour, which joins the recent colours.</summary>
    public void Ok()
    {
        if (Ended)
            return;
        if (!ApplyAsIChoose || !_applied)
            Push(New);
        Ended = true;
        Kept = true;
        _palette.AddRecent(New);
    }

    /// <summary>Cancel: the old colour is put back on the target (only when something was applied).</summary>
    public void Cancel()
    {
        if (Ended)
            return;
        Ended = true;
        if (_applied)
            (_request.Revert ?? _request.Apply)(Was);
    }

    /// <summary>The window closed without OK or Cancel: the same as Cancel.</summary>
    public void Closed() => Cancel();
}

/// <summary>
/// The picker's colour lists: 16 standard colours (the official picker's first palette row, colors.xml ColorPaletteEntry01 to 16), My
/// colours (saved with the user's settings; the official picker keeps its second row the same way when a colour is dragged onto it,
/// llfloatercolorpicker.cpp 975-995), and the last five colours chosen with OK, newest first. Main thread only.
/// </summary>
public sealed class ColourPalette(SettingsService settings)
{
    public const string SavedKey = "colour_picker_saved", RecentKey = "colour_picker_recent";
    public const int RecentCount = 5, SavedLimit = 32;

    /// <summary>"Apply as I choose", under the official setting's name (on by default, as there).</summary>
    public static readonly Setting<bool> ApplyAsIChoose = new("ApplyColorImmediately", true, v => v);

    private static UiColour C(float r, float g, float b) => new(r, g, b);

    /// <summary>The 16 standard colours.</summary>
    public static IReadOnlyList<UiColour> Standard { get; } =
    [
        C(0, 0, 0), C(0.5f, 0.5f, 0.5f), C(0.5f, 0, 0), C(0.5f, 0.5f, 0), C(0, 0.5f, 0), C(0, 0.5f, 0.5f), C(0, 0, 0.5f), C(0.5f, 0, 0.5f),
        C(0.5f, 0.5f, 0), C(0, 0.25f, 0.25f), C(0, 0.5f, 1), C(0, 0.25f, 0.5f), C(0.5f, 0, 1), C(0.5f, 0.25f, 0), C(1, 1, 1), C(1, 1, 0.79f),
    ];

    private IReadOnlyList<UiColour> Read(string key) =>
        (settings.GetText(key) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => ColourMath.TryParseHex(t, out var c) ? c : (UiColour?)null).Where(c => c is not null).Select(c => c!.Value).ToArray();

    private void Write(string key, IEnumerable<UiColour> colours) => settings.SetText(key, string.Join(",", colours.Select(c => ColourMath.ToHex(c, alpha: true))));

    /// <summary>My colours, in the order they were saved.</summary>
    public IReadOnlyList<UiColour> Saved => Read(SavedKey);

    /// <summary>The last colours chosen, newest first (at most five).</summary>
    public IReadOnlyList<UiColour> Recent => Read(RecentKey);

    /// <summary>"+": saves a colour to My colours (once; the oldest goes when there are more than <see cref="SavedLimit"/>).</summary>
    public void Save(UiColour c)
    {
        var list = Saved.Where(x => !ColourMath.Same(x, c)).Append(c).ToList();
        Write(SavedKey, list.Skip(Math.Max(0, list.Count - SavedLimit)));
    }

    /// <summary>Takes a colour off My colours.</summary>
    public void Forget(UiColour c) => Write(SavedKey, Saved.Where(x => !ColourMath.Same(x, c)));

    /// <summary>A colour chosen with OK goes to the front of the recent colours.</summary>
    public void AddRecent(UiColour c) => Write(RecentKey, Recent.Where(x => !ColourMath.Same(x, c)).Prepend(c).Take(RecentCount));
}
