// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The Preferences window ends as the official viewer's does: Cancel puts back every value changed since the window opened, on every
/// page, and OK keeps every page. The interface size the person chose is kept for the next run and for another account, and it
/// survives the grid publishing its accent colour while the window is up.
/// </summary>
public class PreferencesAllPagesTests
{
    private sealed class MemoryStore(string? text = null) : ISettingsStore
    {
        public string? Text = text;
        public string? Read() => Text;
        public bool Write(string text) { Text = text; return true; }
    }

    private static PrefDef Def(string key) => PreferenceCatalog.Find(key) ?? throw new Xunit.Sdk.XunitException("no setting " + key);

    private static (PreferencesModel Model, SettingsService Settings, MemoryStore Store) Make()
    {
        var store = new MemoryStore();
        var settings = new SettingsService(store);
        return (new PreferencesModel(settings, "acct"), settings, store);
    }

    [Fact]
    public void Cancel_puts_back_what_was_changed_on_every_page()
    {
        var (m, s, _) = Make();
        Assert.True(m.Edit(Def("draw_distance"), 300));
        Assert.True(m.Edit(Def("login_start_location"), "home"));
        Assert.True(m.AnyChanged);
        m.CancelAll();
        Assert.Equal(Settings.DrawDistance.Default, s.Get(Settings.DrawDistance));
        Assert.Equal("last", m.Value(Def("login_start_location")));
        Assert.False(m.AnyChanged);
    }

    [Fact]
    public void OK_keeps_what_was_changed_on_every_page_and_a_later_Cancel_does_not_undo_it()
    {
        var (m, _, store) = Make();
        m.Edit(Def("draw_distance"), 300);
        m.Edit(Def("login_start_location"), "home");
        Assert.True(m.SaveAll());
        Assert.False(m.AnyChanged);
        m.CancelAll();
        var reread = new SettingsService(new MemoryStore(store.Text));
        Assert.Equal(300, reread.Get(Settings.DrawDistance));
        Assert.Equal("home", reread.GetText("login_start_location"));
        Assert.Equal(300, m.Service.Get(Settings.DrawDistance));
    }

    [Fact]
    public void A_page_cancelled_alone_leaves_the_other_pages_edits_for_the_window_to_decide()
    {
        var (m, s, _) = Make();
        m.Edit(Def("draw_distance"), 300);
        m.Edit(Def("login_start_location"), "home");
        m.Cancel(PrefPage.General);
        Assert.Equal(300, s.Get(Settings.DrawDistance));
        Assert.True(m.AnyChanged);
    }

    [Fact]
    public void A_grid_accent_arriving_while_the_window_is_up_keeps_the_size_the_person_chose()
    {
        var page = new InterfacePreferences(ThemeChoice.Default with { ScalePercent = 150 }, null, scaleOwn: true, displayPercent: 100);
        var accent = new UiColour(10, 200, 160);
        var next = page.WithGridAccent(accent);
        Assert.Equal(accent, next.GridAccent);
        Assert.True(next.ScaleOwn);
        Assert.Equal(150, next.EffectiveScale);
    }

    [Fact]
    public void A_grid_accent_arriving_keeps_following_the_displays_scale_when_the_person_set_none()
    {
        var page = new InterfacePreferences(ThemeChoice.Default, null, scaleOwn: false, displayPercent: 150);
        var next = page.WithGridAccent(new UiColour(10, 200, 160));
        Assert.False(next.ScaleOwn);
        Assert.Equal(150, next.EffectiveScale);
    }

    [Fact]
    public void A_size_the_person_chose_and_saved_comes_back_next_run_even_on_a_larger_display_and_for_another_account()
    {
        var store = new MemoryStore();
        var settings = new SettingsService(store);
        var page = new InterfacePreferences(ThemeStore.Load(settings, "a.b"), null, ThemeStore.ScaleOwn(settings, "a.b", ThemeStore.Load(settings, "a.b")), 100);
        page.SetScale(130);
        Assert.Equal(SaveOutcome.Saved, page.Save(settings, "a.b", confirmed: true));

        var later = new SettingsService(new MemoryStore(store.Text));
        foreach (var account in new string?[] { "a.b", "c.d", null })
        {
            var choice = ThemeStore.Load(later, account);
            Assert.Equal(130, ThemeStore.EffectiveScale(later, account, choice, displayPercent: 175));
        }
    }

    [Fact]
    public void A_size_set_and_cancelled_is_not_kept()
    {
        var settings = new SettingsService(new MemoryStore());
        var page = new InterfacePreferences(ThemeChoice.Default, null, false, 100);
        page.SetScale(150);
        page.Cancel();
        Assert.Equal(100, page.EffectiveScale);
        Assert.False(page.ScaleOwn);
        Assert.Null(settings.GetText("ui_theme_last"));
    }
}
