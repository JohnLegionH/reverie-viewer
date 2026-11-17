// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text.RegularExpressions;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The one colour picker every caller uses, after the official viewer's LLFloaterColorPicker (read as behaviour only): its conversions
/// (R, G, B 0-255; H 0-360; S and L 0-100; hex), "Apply as I choose" on by default with each change shown on the target, OK keeping and
/// Cancel putting back the old colour, the 16 standard colours, My colours and the last five colours kept across a restart, and in the
/// sources that no window chooses a colour with anything else. No engine; each test makes its own settings, so the class runs in parallel.
/// </summary>
public class ColourPickerTests
{
    private sealed class MemoryStore : ISettingsStore
    {
        public string? Text;
        public string? Read() => Text;
        public bool Write(string text) { Text = text; return true; }
    }

    private static readonly UiColour Red = new(1, 0, 0), Teal = new(0, 0.5f, 0.5f), Gold = new(1, 0.84f, 0);

    private sealed class Target
    {
        public UiColour Colour;
        public readonly List<UiColour> Applied = new();
        public ColourRequest Request(bool alpha = false) => new("edit.picker.colour.title", Colour, alpha, c => { Colour = c; Applied.Add(c); });
    }

    private static ColourPalette Palette(MemoryStore? store = null) => new(new SettingsService(store ?? new MemoryStore()));

    // ---------------- conversions ----------------

    [Fact]
    public void Hsv_hsl_and_hex_round_trip_and_read_as_the_official_fields_do()
    {
        var (h, s, v) = ColourMath.ToHsv(Teal);
        Assert.Equal(0.5f, h, 3);
        Assert.Equal(1f, s, 3);
        Assert.Equal(0.5f, v, 3);
        Assert.True(ColourMath.Same(Teal, ColourMath.FromHsv(h, s, v)));
        var (hh, ss, ll) = ColourMath.ToHsl(Gold);
        Assert.Equal(50.4f, hh * 360, 1);   // 0-360 in the H field
        Assert.Equal(100f, ss * 100, 1);    // 0-100 in the S field
        Assert.Equal(50f, ll * 100, 1);     // 0-100 in the L field
        Assert.True(ColourMath.Same(Gold, ColourMath.FromHsl(hh, ss, ll)));
        Assert.Equal("#008080", ColourMath.ToHex(Teal));
        Assert.Equal("#FF000080", ColourMath.ToHex(Red with { A = 0.5f }, alpha: true));
        Assert.True(ColourMath.TryParseHex("f00", out var short3) && ColourMath.Same(short3, Red));
        Assert.True(ColourMath.TryParseHex("#008080", out var six) && ColourMath.Same(six, Teal));
        Assert.False(ColourMath.TryParseHex("#12345", out _));
        Assert.False(ColourMath.TryParseHex("zzzzzz", out _));
    }

    // ---------------- the session ----------------

    [Fact]
    public void Apply_as_I_choose_is_on_by_default_and_shows_each_change_on_the_target()
    {
        Assert.True(new SettingsService(new MemoryStore()).Get(ColourPalette.ApplyAsIChoose));
        var t = new Target { Colour = Red };
        var session = new ColourSession(t.Request(), Palette(), applyAsIChoose: true);
        session.Choose(Teal);
        session.Choose(Gold);
        Assert.Equal([Teal, Gold], t.Applied);
        Assert.Equal(Red, session.Was);
        Assert.Equal(Gold, session.New);
    }

    [Fact]
    public void Cancel_puts_the_old_colour_back()
    {
        var t = new Target { Colour = Red };
        var session = new ColourSession(t.Request(), Palette(), applyAsIChoose: true);
        session.Choose(Teal);
        session.Cancel();
        Assert.True(ColourMath.Same(Red, t.Colour));
        session.Choose(Gold);   // ended: nothing more reaches the target
        Assert.True(ColourMath.Same(Red, t.Colour));
    }

    [Fact]
    public void Closing_the_window_without_OK_puts_the_old_colour_back()
    {
        var t = new Target { Colour = Red };
        var session = new ColourSession(t.Request(), Palette(), applyAsIChoose: true);
        session.Choose(Teal);
        session.Closed();
        Assert.True(ColourMath.Same(Red, t.Colour));
    }

    [Fact]
    public void OK_keeps_the_new_colour_and_a_later_close_changes_nothing()
    {
        var t = new Target { Colour = Red };
        var session = new ColourSession(t.Request(), Palette(), applyAsIChoose: true);
        session.Choose(Teal);
        session.Ok();
        session.Closed();
        Assert.True(ColourMath.Same(Teal, t.Colour));
        Assert.True(session.Kept);
    }

    [Fact]
    public void With_apply_as_I_choose_off_the_target_changes_only_on_OK_and_turning_it_on_applies_at_once()
    {
        var t = new Target { Colour = Red };
        var session = new ColourSession(t.Request(), Palette(), applyAsIChoose: false);
        session.Choose(Teal);
        Assert.Empty(t.Applied);
        session.ApplyAsIChoose = true;
        Assert.Equal([Teal], t.Applied);

        var t2 = new Target { Colour = Red };
        var off = new ColourSession(t2.Request(), Palette(), applyAsIChoose: false);
        off.Choose(Gold);
        off.Cancel();
        Assert.Empty(t2.Applied);   // nothing was shown, so nothing is put back
        var ok = new ColourSession(t2.Request(), Palette(), applyAsIChoose: false);
        ok.Choose(Gold);
        ok.Ok();
        Assert.Equal([Gold], t2.Applied);
    }

    [Fact]
    public void Transparency_is_kept_only_where_the_caller_allows_it()
    {
        var t = new Target { Colour = Red with { A = 0.4f } };
        var opaque = new ColourSession(t.Request(alpha: false), Palette(), true);
        opaque.Choose(Teal with { A = 0.25f });
        Assert.Equal(1f, t.Colour.A);
        var clear = new ColourSession(t.Request(alpha: true), Palette(), true);
        clear.Choose(Teal with { A = 0.25f });
        Assert.Equal(0.25f, t.Colour.A);
    }

    // ---------------- colour lists ----------------

    [Fact]
    public void There_are_16_standard_colours_from_black_to_light_yellow()
    {
        Assert.Equal(16, ColourPalette.Standard.Count);
        Assert.Equal(new UiColour(0, 0, 0), ColourPalette.Standard[0]);
        Assert.Equal(new UiColour(1, 1, 1), ColourPalette.Standard[14]);
    }

    [Fact]
    public void Recent_colours_are_the_last_five_chosen_with_OK_newest_first_and_survive_a_restart()
    {
        var store = new MemoryStore();
        var palette = Palette(store);
        var picks = new[] { Red, Teal, Gold, new UiColour(0, 0, 1), new UiColour(0, 1, 0), new UiColour(1, 0, 1), Teal };
        foreach (var c in picks)
        {
            var s = new ColourSession(new Target().Request(), palette, true);
            s.Choose(c);
            s.Ok();
        }
        var cancelled = new ColourSession(new Target().Request(), palette, true);
        cancelled.Choose(new UiColour(0.3f, 0.3f, 0.3f));
        cancelled.Cancel();   // not chosen: not recent
        var again = Palette(store).Recent;   // the viewer started again
        Assert.Equal(5, again.Count);
        Assert.True(ColourMath.Same(Teal, again[0]));
        Assert.True(ColourMath.Same(new UiColour(1, 0, 1), again[1]));
        Assert.True(ColourMath.Same(Gold, again[4]));
        Assert.Equal(1, again.Count(c => ColourMath.Same(c, Teal)));
    }

    [Fact]
    public void My_colours_are_saved_with_the_settings_once_each_and_survive_a_restart()
    {
        var store = new MemoryStore();
        var palette = Palette(store);
        palette.Save(Gold);
        palette.Save(Teal with { A = 0.5f });
        palette.Save(Gold);
        var again = Palette(store).Saved;
        Assert.Equal(2, again.Count);
        Assert.True(ColourMath.Same(Teal with { A = 0.5f }, again[0]));
        Assert.True(ColourMath.Same(Gold, again[1]));
        Palette(store).Forget(Gold);
        Assert.Single(Palette(store).Saved);
    }

    // ---------------- every caller uses the shared picker ----------------

    private static readonly string[] Callers =
    [
        "EditTextureTab.cs", "EditFeaturesTab.cs", "PersonalLightingPanel.cs", "PreferencesPanels.cs", "AppearancePanels.cs",
    ];

    [Fact]
    public void No_window_chooses_a_colour_with_the_engine_s_own_picker()
    {
        string dir = Path.Combine(RepoRoot(), "host", "Viewer.Godot");
        foreach (string file in Directory.GetFiles(dir, "*.cs"))
        {
            if (Path.GetFileName(file) == "ColourPickerWindow.cs")
                continue;
            string text = File.ReadAllText(file);
            Assert.False(Regex.IsMatch(text, @"new\s+ColorPicker(Button)?\b"), $"{Path.GetFileName(file)} makes its own colour picker");
            Assert.False(Regex.IsMatch(text, @"\bColorPicker(Button)?\b"), $"{Path.GetFileName(file)} uses the engine's colour picker");
        }
    }

    [Fact]
    public void Every_caller_opens_the_shared_picker()
    {
        foreach (string name in Callers)
        {
            string text = File.ReadAllText(Path.Combine(RepoRoot(), "host", "Viewer.Godot", name));
            Assert.True(text.Contains("ColourSwatch", StringComparison.Ordinal), $"{name} has no shared colour swatch");
        }
        string window = File.ReadAllText(Path.Combine(RepoRoot(), "host", "Viewer.Godot", "ColourPickerWindow.cs"));
        foreach (string part in new[] { "ColourSession", "ColourPalette", "\"pick\"", "\"hex\"", "\"transparency\"", "\"apply_as_i_choose\"", "\"save_colour\"" })
            Assert.Contains(part, window);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Viewer.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
