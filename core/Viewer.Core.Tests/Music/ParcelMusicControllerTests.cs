// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Music;
using Xunit;

namespace Viewer.Core.Tests.Music;

public sealed class ParcelMusicControllerTests
{
    private sealed class Playback : IMusicPlayback
    {
        public List<string> Log { get; } = [];
        public void Play(Uri url) => Log.Add("play " + url);
        public void Stop() => Log.Add("stop");
    }

    private sealed class Prefs : IMusicPrefs
    {
        public bool On { get; set; } = true;
        public Dictionary<string, SiteAnswer> Answers { get; } = [];
        public SiteAnswer AnswerFor(string site) => Answers.GetValueOrDefault(site, SiteAnswer.Ask);
        public void SetAnswer(string site, SiteAnswer answer) => Answers[site] = answer;
    }

    private sealed class Asker : IMusicAsker
    {
        public List<(string Site, Uri Url, Action<bool> Answer)> Asked { get; } = [];
        public void Ask(string site, Uri url, Action<bool> answered) => Asked.Add((site, url, answered));
    }

    private static (ParcelMusicController C, Playback P, Prefs Pr, Asker A) Make(SiteAnswer? answer = SiteAnswer.Play, string site = "radio.example.org")
    {
        var p = new Playback();
        var pr = new Prefs();
        if (answer is { } a)
            pr.Answers[site] = a;
        var asker = new Asker();
        return (new ParcelMusicController(p, pr, asker), p, pr, asker);
    }

    [Fact]
    public void AParcelWithMusicPlaysIt()
    {
        var (c, p, _, a) = Make();
        c.ParcelAddress("http://radio.example.org:8040");
        Assert.Equal(["play http://radio.example.org:8040/"], p.Log);
        Assert.True(c.HasMusic);
        Assert.True(c.IsPlaying);
        Assert.Empty(a.Asked);
    }

    [Fact]
    public void ANewAddressOnTheSameParcelSwitchesAtOnce()
    {
        var (c, p, _, _) = Make();
        c.ParcelAddress("http://radio.example.org/one");
        c.ParcelAddress("http://radio.example.org/two");
        c.ParcelAddress("http://radio.example.org/two");   // the same address again changes nothing
        Assert.Equal(["play http://radio.example.org/one", "play http://radio.example.org/two"], p.Log);
    }

    [Fact]
    public void AnEmptyAddressStopsTheMusic()
    {
        var (c, p, _, _) = Make();
        c.ParcelAddress("http://radio.example.org/one");
        c.ParcelAddress("");
        Assert.Equal(["play http://radio.example.org/one", "stop"], p.Log);
        Assert.False(c.HasMusic);
        Assert.False(c.IsPlaying);
    }

    [Fact]
    public void LeavingTheParcelStopsTheMusic()
    {
        var (c, p, _, _) = Make();
        c.ParcelAddress("http://radio.example.org/one");
        c.LeftParcel();
        Assert.Equal(["play http://radio.example.org/one", "stop"], p.Log);
        Assert.False(c.HasMusic);
    }

    [Theory]
    [InlineData("ftp://radio.example.org/x")]
    [InlineData("file:///music/x.mp3")]
    [InlineData("not an address")]
    public void AnAddressThatIsNotHttpIsNeverPlayed(string address)
    {
        var (c, p, _, a) = Make();
        c.ParcelAddress(address);
        Assert.Empty(p.Log);
        Assert.Empty(a.Asked);
        Assert.False(c.HasMusic);
    }

    [Fact]
    public void OffStaysOffAcrossParcelsAndAfterARelog()
    {
        var (c, p, pr, a) = Make();
        c.ParcelAddress("http://radio.example.org/one");
        c.Toggle();   // the person turns it off
        Assert.False(pr.On);
        Assert.Equal(["play http://radio.example.org/one", "stop"], p.Log);
        Assert.True(c.HasMusic);   // the control stays, so it can be turned on again
        p.Log.Clear();
        c.ParcelAddress("");
        c.ParcelAddress("http://radio.example.org/two");   // another parcel
        c.ParcelAddress("http://other.example.org/three");   // a site not asked about yet
        Assert.Empty(p.Log);
        Assert.Empty(a.Asked);
        var relogged = new ParcelMusicController(p, pr, a);   // a new session with the same saved settings
        relogged.ParcelAddress("http://radio.example.org/one");
        Assert.Empty(p.Log);
        Assert.False(relogged.IsPlaying);
        relogged.Toggle();   // only the person turns it on
        Assert.Equal(["play http://radio.example.org/one"], p.Log);
        Assert.True(pr.On);
    }

    [Fact]
    public void ASiteNotAnsweredYetIsAskedOnceAndNothingPlaysUntilTheAnswer()
    {
        var (c, p, pr, a) = Make(answer: null);
        c.ParcelAddress("http://radio.example.org/one");
        c.ParcelAddress("http://radio.example.org/two");   // the same site: not asked a second time
        Assert.Single(a.Asked);
        Assert.Equal("radio.example.org", a.Asked[0].Site);
        Assert.Empty(p.Log);
        a.Asked[0].Answer(true);   // Play
        Assert.Equal(SiteAnswer.Play, pr.AnswerFor("radio.example.org"));
        Assert.Equal(["play http://radio.example.org/two"], p.Log);   // the address the parcel has now
        c.LeftParcel();
        c.ParcelAddress("http://radio.example.org/one");   // remembered: no question, plays
        Assert.Single(a.Asked);
        Assert.Equal("play http://radio.example.org/one", p.Log[^1]);
    }

    [Fact]
    public void DontPlayIsRememberedAndNothingPlays()
    {
        var (c, p, pr, a) = Make(answer: null);
        c.ParcelAddress("http://radio.example.org/one");
        a.Asked[0].Answer(false);   // Don't play
        Assert.Equal(SiteAnswer.DontPlay, pr.AnswerFor("radio.example.org"));
        Assert.Empty(p.Log);
        c.LeftParcel();
        c.ParcelAddress("http://radio.example.org/one");   // remembered: no question, no music
        Assert.Single(a.Asked);
        Assert.Empty(p.Log);
        Assert.False(c.IsPlaying);
        Assert.True(c.HasMusic);   // the control shows, so the person can change their mind
    }

    [Fact]
    public void TurningMusicOnForASiteRefusedBeforeAsksAgain()
    {
        var (c, p, pr, a) = Make(answer: SiteAnswer.DontPlay);
        c.ParcelAddress("http://radio.example.org/one");
        Assert.Empty(a.Asked);
        c.Toggle();
        Assert.Single(a.Asked);
        a.Asked[0].Answer(true);
        Assert.Equal(SiteAnswer.Play, pr.AnswerFor("radio.example.org"));
        Assert.Equal(["play http://radio.example.org/one"], p.Log);
    }

    [Fact]
    public void AnAnswerForASiteTheParcelHasLeftDoesNotStartMusic()
    {
        var (c, p, pr, a) = Make(answer: null);
        c.ParcelAddress("http://radio.example.org/one");
        c.LeftParcel();
        a.Asked[0].Answer(true);   // answered after the person walked away
        Assert.Equal(SiteAnswer.Play, pr.AnswerFor("radio.example.org"));
        Assert.Empty(p.Log);
    }
}
