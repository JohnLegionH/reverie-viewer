// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Music;
using Viewer.Shell.Sound;
using Xunit;

namespace Viewer.Core.Tests.Music;

/// <summary>The answers to "play music from this site?" as they are kept in the settings, and the music settings' defaults.</summary>
public sealed class MusicSiteAnswersTests
{
    [Fact]
    public void ASiteNotAnsweredIsAsked()
    {
        Assert.Equal(SiteAnswer.Ask, MusicSiteAnswers.Get(null, "radio.example.org"));
        Assert.Equal(SiteAnswer.Ask, MusicSiteAnswers.Get("", "radio.example.org"));
    }

    [Fact]
    public void AnAnswerIsKeptPerSiteIgnoringCase()
    {
        string text = MusicSiteAnswers.Set(null, "Radio.Example.org", SiteAnswer.Play);
        text = MusicSiteAnswers.Set(text, "other.example.org", SiteAnswer.DontPlay);
        Assert.Equal(SiteAnswer.Play, MusicSiteAnswers.Get(text, "radio.example.org"));
        Assert.Equal(SiteAnswer.DontPlay, MusicSiteAnswers.Get(text, "OTHER.example.org"));
        Assert.Equal(SiteAnswer.Ask, MusicSiteAnswers.Get(text, "third.example.org"));
    }

    [Fact]
    public void ANewAnswerReplacesTheOldOneAndAskForgetsTheSite()
    {
        string text = MusicSiteAnswers.Set(null, "radio.example.org", SiteAnswer.DontPlay);
        text = MusicSiteAnswers.Set(text, "radio.example.org", SiteAnswer.Play);
        Assert.Equal("radio.example.org=play", text);
        Assert.Equal("", MusicSiteAnswers.Set(text, "radio.example.org", SiteAnswer.Ask));
    }

    [Fact]
    public void LinesThatAreNotAnswersAreLeftOut()
    {
        Assert.Equal(SiteAnswer.Play, MusicSiteAnswers.Get("junk\n=play\nradio.example.org=maybe\nradio.example.org=play\n", "radio.example.org"));
    }

    [Fact]
    public void OnlyTheNewestFiveHundredSitesAreKept()
    {
        string text = "";
        for (int i = 0; i < MusicSiteAnswers.MaxSites + 20; i++)
            text = MusicSiteAnswers.Set(text, $"site{i}.example.org", SiteAnswer.Play);
        Assert.Equal(SiteAnswer.Ask, MusicSiteAnswers.Get(text, "site0.example.org"));
        Assert.Equal(SiteAnswer.Play, MusicSiteAnswers.Get(text, $"site{MusicSiteAnswers.MaxSites + 19}.example.org"));
    }

    [Fact]
    public void MusicIsOnByDefaultAtTheOfficialLevelAndNotMuted()
    {
        Assert.True(Viewer.Shell.Sound.AudioLevels.MusicOn.Default);
        Assert.Equal(0.5, Viewer.Shell.Sound.AudioLevels.Music.Default);
        Assert.False(Viewer.Shell.Sound.AudioLevels.MuteMusic.Default);
    }
}
