// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Media;
using Viewer.Core.Music;
using Viewer.Shell.Sound;
using Xunit;

namespace Viewer.Core.Tests.Media;

/// <summary>Consent for media: answers are kept per site and kind, a blocked site wins over any answer, a site is asked once.</summary>
public sealed class MediaConsentTests
{
    private sealed class Asker : IMediaAsker
    {
        public List<(MediaKind Kind, string Site, Action<bool> Answer)> Asked { get; } = [];
        public List<MediaKind> Cancelled { get; } = [];
        public void Ask(MediaKind kind, string site, Uri url, Action<bool> answered) => Asked.Add((kind, site, answered));
        public void Cancel(MediaKind kind) => Cancelled.Add(kind);
    }

    private static (MediaConsent Consent, MediaSiteSettings Store, Asker Asker, Dictionary<string, string> Settings) Make()
    {
        var settings = new Dictionary<string, string>();
        var store = new MediaSiteSettings(k => settings.GetValueOrDefault(k), (k, v) => settings[k] = v);
        var asker = new Asker();
        return (new MediaConsent(store, asker), store, asker, settings);
    }

    private static readonly Uri Page = new("http://media.example.org/page");

    [Theory]
    [InlineData(SiteAnswer.Ask, false, SiteAnswer.Ask)]
    [InlineData(SiteAnswer.Play, false, SiteAnswer.Play)]
    [InlineData(SiteAnswer.DontPlay, false, SiteAnswer.DontPlay)]
    [InlineData(SiteAnswer.Ask, true, SiteAnswer.DontPlay)]
    [InlineData(SiteAnswer.Play, true, SiteAnswer.DontPlay)]
    [InlineData(SiteAnswer.DontPlay, true, SiteAnswer.DontPlay)]
    public void The_blocked_list_wins_over_any_answer(SiteAnswer kept, bool blocked, SiteAnswer expected)
    {
        var (consent, store, _, _) = Make();
        store.Set(MediaKind.Video, "media.example.org", kept);
        store.SetBlocked("Media.Example.org", blocked);
        Assert.Equal(expected, consent.AnswerFor(MediaKind.Video, "media.example.org"));
        Assert.Equal(expected == SiteAnswer.Play, consent.MayLoad(MediaKind.Video, Page.ToString()));
    }

    [Theory]
    [InlineData(MediaKind.Sound)]
    [InlineData(MediaKind.Video)]
    [InlineData(MediaKind.Web)]
    public void An_answer_holds_for_its_kind_only(MediaKind allowed)
    {
        var (consent, store, _, _) = Make();
        store.Set(allowed, "media.example.org", SiteAnswer.Play);
        foreach (var kind in Enum.GetValues<MediaKind>())
            Assert.Equal(kind == allowed, consent.MayLoad(kind, Page.ToString()));
    }

    [Fact]
    public void Music_answers_kept_before_stay_sound_answers_and_allow_nothing_else()
    {
        var (consent, _, _, settings) = Make();
        settings[AudioLevels.MusicSitesKey] = "media.example.org=play";
        Assert.True(consent.MayLoad(MediaKind.Sound, Page.ToString()));
        Assert.False(consent.MayLoad(MediaKind.Video, Page.ToString()));
        Assert.False(consent.MayLoad(MediaKind.Web, Page.ToString()));
    }

    [Theory]
    [InlineData("file:///clip.mp4")]
    [InlineData("secondlife:///app/agent/x/about")]
    [InlineData("")]
    [InlineData(null)]
    public void Only_http_and_https_can_ever_load(string? address)
    {
        var (consent, _, _, _) = Make();
        Assert.False(consent.MayLoad(MediaKind.Web, address));
    }

    [Theory]
    [InlineData(SiteAnswer.Play, true, true)]
    [InlineData(SiteAnswer.Play, false, false)]
    [InlineData(SiteAnswer.Ask, true, false)]
    [InlineData(SiteAnswer.DontPlay, true, false)]
    public void Auto_play_needs_an_allowed_site_and_auto_play_on(SiteAnswer answer, bool autoPlayOn, bool expected)
    {
        var (consent, store, asker, _) = Make();
        store.Set(MediaKind.Video, "media.example.org", answer);
        Assert.Equal(expected, consent.MayAutoPlay(MediaKind.Video, Page.ToString(), autoPlayOn));
        Assert.Empty(asker.Asked);
    }

    [Fact]
    public void A_site_is_asked_once_and_the_answer_is_kept()
    {
        var (consent, _, asker, _) = Make();
        int changed = 0;
        Assert.Equal(MediaDecision.Waiting, consent.Want(MediaKind.Web, Page, [], () => changed++));
        Assert.Equal(MediaDecision.Waiting, consent.Want(MediaKind.Web, Page, [], () => changed++));
        var asked = Assert.Single(asker.Asked);
        Assert.Equal((MediaKind.Web, "media.example.org"), (asked.Kind, asked.Site));
        asked.Answer(true);
        Assert.Equal(1, changed);
        Assert.Equal(MediaDecision.Allowed, consent.Want(MediaKind.Web, Page, [], () => { }));
        Assert.Single(asker.Asked);
    }

    [Fact]
    public void A_move_to_another_site_asks_again_for_that_site()
    {
        var (consent, store, asker, _) = Make();
        store.Set(MediaKind.Web, "media.example.org", SiteAnswer.Play);
        Assert.Equal(MediaDecision.Waiting, consent.Want(MediaKind.Web, Page, ["other.example.org"], () => { }));
        Assert.Equal("other.example.org", Assert.Single(asker.Asked).Site);
        asker.Asked[0].Answer(false);
        Assert.Equal(MediaDecision.Refused, consent.Want(MediaKind.Web, Page, ["other.example.org"], () => { }));
    }

    [Fact]
    public void Leaving_takes_the_question_away_without_an_answer()
    {
        var (consent, store, asker, _) = Make();
        consent.Want(MediaKind.Video, Page, [], () => { });
        consent.Release(MediaKind.Video);
        Assert.Equal([MediaKind.Video], asker.Cancelled);
        Assert.Equal(SiteAnswer.Ask, store.Get(MediaKind.Video, "media.example.org"));
    }

    [Fact]
    public void Asking_again_forgets_a_refusal_but_not_a_block()
    {
        var (consent, store, _, _) = Make();
        store.Set(MediaKind.Sound, "a.example.org", SiteAnswer.DontPlay);
        store.Set(MediaKind.Sound, "b.example.org", SiteAnswer.DontPlay);
        store.SetBlocked("b.example.org", true);
        consent.AskAgain(MediaKind.Sound, ["a.example.org", "b.example.org"]);
        Assert.Equal(SiteAnswer.Ask, consent.AnswerFor(MediaKind.Sound, "a.example.org"));
        Assert.Equal(SiteAnswer.DontPlay, consent.AnswerFor(MediaKind.Sound, "b.example.org"));
    }

    [Fact]
    public void The_sound_snapshot_holds_allowed_unblocked_sites_only()
    {
        var (_, store, _, _) = Make();
        store.Set(MediaKind.Sound, "a.example.org", SiteAnswer.Play);
        store.Set(MediaKind.Sound, "b.example.org", SiteAnswer.Play);
        store.SetBlocked("b.example.org", true);
        var may = store.SoundSnapshot();
        Assert.True(may("a.example.org"));
        Assert.False(may("b.example.org"));
        Assert.False(may("c.example.org"));
    }
}
