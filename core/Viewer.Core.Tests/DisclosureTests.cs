// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The disclosures Linden Lab's Third-Party Viewer Policy asks for (sections 1.c and 4.b): what they say, that they are shown once at
/// the first run and once before the first Second Life login, that refusing the second sends nothing to Second Life, and the
/// privacy policy they link to.
/// </summary>
public class DisclosureTests
{
    private const string SecondLife = "https://login.agni.lindenlab.com/cgi-bin/login.cgi";

    private sealed class MemoryStore : ISettingsStore
    {
        public string? Text;
        public string? Read() => Text;
        public bool Write(string text) { Text = text; return true; }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_disclosure_says_what_the_policy_asks(bool beforeSecondLife)
    {
        var s = StringTable.English();
        var d = beforeSecondLife ? Disclosures.BeforeSecondLife(s) : Disclosures.FirstRun(s);
        string all = d.Title + "\n" + d.Body + "\n" + d.ReadOnlyText;
        Assert.Equal(LoginDialogKind.Disclosure, d.Kind);
        Assert.Contains("not provided or supported by Linden Lab", all);                   // 1.c: the name, and not Linden Lab's
        Assert.Contains("Reverie Viewer", all);
        Assert.Contains("https://github.com/JohnLegionH/reverie-viewer/issues", all);      // 1.c: support
        Assert.Contains("General", all);                                                   // 1.c: content not rated General
        Assert.Contains("official viewer", all);                                           // 1.c: functionality beyond and limits
        Assert.Equal("https://github.com/JohnLegionH/reverie-viewer/blob/main/PRIVACY.md", d.Link);   // 1.c and 4.b: the privacy policy
        Assert.NotNull(d.CancelLabel);                                                     // it can be refused
    }

    [Fact]
    public void The_first_run_disclosure_is_due_once_and_kept_when_accepted()
    {
        var store = new MemoryStore();
        var settings = new SettingsService(store);
        Assert.True(Disclosures.FirstRunDue(settings));
        Disclosures.FirstRunAccepted(settings);
        Assert.False(Disclosures.FirstRunDue(settings));
        Assert.False(Disclosures.FirstRunDue(new SettingsService(store)));   // kept between runs
    }

    private static (LoginFlow Flow, SettingsService Settings) SecondLifeFlow(SettingsService? settings = null)
    {
        var flow = new LoginFlow(null, null, LoginFlow.Fail("key", "Sorry! We couldn't log you in."));
        flow.Model.LoginUri = SecondLife;
        settings ??= new SettingsService(new MemoryStore());
        flow.Model.UserSettings = settings;
        return (flow, settings);
    }

    [Fact]
    public async Task Refusing_the_disclosure_before_the_first_Second_Life_login_sends_nothing()
    {
        var (flow, _) = SecondLifeFlow();
        flow.Answer(LoginDialogResult.Cancelled);
        Assert.False(await flow.Model.LoginAsync());
        Assert.Equal(LoginDialogKind.Disclosure, Assert.Single(flow.Shown).Kind);
        Assert.Empty(flow.Requests);
        Assert.Equal("cancelled", flow.Model.LastFailureCode);
    }

    [Fact]
    public async Task Accepting_it_logs_in_and_it_is_not_asked_again()
    {
        var (flow, settings) = SecondLifeFlow();
        flow.Answer(new LoginDialogResult(true));
        await flow.Model.LoginAsync();
        Assert.Equal(LoginDialogKind.Disclosure, Assert.Single(flow.Shown).Kind);
        Assert.Single(flow.Requests);

        var (again, _) = SecondLifeFlow(settings);
        await again.Model.LoginAsync();
        Assert.Empty(again.Shown);
        Assert.Single(again.Requests);
    }

    [Fact]
    public async Task Another_grid_does_not_ask_it()
    {
        var flow = new LoginFlow(null, null, LoginFlow.Fail("key", "no"));
        flow.Model.UserSettings = new SettingsService(new MemoryStore());
        await flow.Model.LoginAsync();
        Assert.DoesNotContain(flow.Shown, d => d.Kind == LoginDialogKind.Disclosure);
        Assert.Single(flow.Requests);
    }

    [Fact]
    public void The_privacy_policy_is_published_with_what_it_must_say()
    {
        string path = Path.Combine(TestPaths.RepoRoot, "PRIVACY.md");
        Assert.True(File.Exists(path), "PRIVACY.md at the repository root");
        string text = File.ReadAllText(path);
        Assert.Contains("https://github.com/JohnLegionH/reverie-viewer/issues", text);
        Assert.Contains("Linden Lab", text);
        Assert.Contains("ViewerStats", text);
        Assert.Contains("id0", text);
        Assert.Contains("api.github.com", text);   // the update check, the one request that goes to neither grid
        Assert.DoesNotMatch(@"(?<![A-Za-z])[A-Za-z]:[\\/]", text);   // no local paths (a drive letter, not a web address's scheme)
    }

    [Theory]
    [InlineData("disclosure_first_run")]
    [InlineData("disclosure_second_life")]
    public void The_samples_show_both_disclosures(string kind)
    {
        Assert.Contains(kind, LoginDialogSamples.Kinds);
        Assert.Equal(LoginDialogKind.Disclosure, LoginDialogSamples.For(StringTable.English(), kind)!.Kind);
    }
}
