// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Media;

namespace Viewer.Core.Music;

/// <summary>What a person answered about a site's stream.</summary>
public enum SiteAnswer { Ask, Play, DontPlay }

/// <summary>Starts and stops the stream that plays the parcel's music.</summary>
public interface IMusicPlayback
{
    /// <summary>Plays <paramref name="url"/>, in place of any stream that plays.</summary>
    void Play(Uri url);
    void Stop();

    /// <summary>A site the stream led to (a redirect or a playlist entry) that it did not open for want of an answer; null otherwise.</summary>
    string? SiteNeeded => null;
}

/// <summary>What is kept between visits: whether music is on, and the answer for each site.</summary>
public interface IMusicPrefs
{
    /// <summary>The person's switch for parcel music. Off stays off until the person turns it on.</summary>
    bool On { get; set; }
    SiteAnswer AnswerFor(string site);
    void SetAnswer(string site, SiteAnswer answer);
}

/// <summary>Asks the person once about a site (Play or Don't play).</summary>
public interface IMusicAsker
{
    /// <summary>Shows the question for <paramref name="site"/>; <paramref name="answered"/> is called with true for Play and false for Don't play.</summary>
    void Ask(string site, Uri url, Action<bool> answered);

    /// <summary>The question is not wanted any more (the person left the parcel or went to another site): take it away without an answer.</summary>
    void Cancel() { }
}

/// <summary>
/// Decides what the parcel's music does. It is told the parcel's music address whenever the parcel says it (the same address again changes nothing;
/// a new one switches at once, whatever the update's sequence number was; an empty one, or leaving the parcel, stops the music), and it follows
/// the person's switch (<see cref="Toggle"/>) and the answer remembered for the site. Music plays only when the switch is on and the site's answer
/// is Play; a site not answered yet is asked once. A further site the stream leads to (a redirect or a playlist entry) needs its own answer before
/// it is opened. It works on one thread (the one that drives the interface).
/// </summary>
public sealed class ParcelMusicController
{
    /// <summary>The most further sites one address may lead to before the music is left off.</summary>
    private const int MaxFurtherSites = 5;

    private readonly IMusicPlayback _playback;
    private readonly IMusicPrefs _prefs;
    private readonly MediaConsent _consent;
    private readonly List<string> _furtherSites = [];
    private Uri? _url, _playingUrl;
    private bool _playing;

    public ParcelMusicController(IMusicPlayback playback, IMusicPrefs prefs, IMusicAsker asker)
    {
        _playback = playback;
        _prefs = prefs;
        _consent = new MediaConsent(new SoundAnswers(prefs), new SoundQuestion(asker));
    }

    /// <summary>The parcel's music answers as sound answers (a blocked site reaches here as Don't play).</summary>
    private sealed class SoundAnswers(IMusicPrefs prefs) : IMediaSiteStore
    {
        public SiteAnswer Get(MediaKind kind, string site) => prefs.AnswerFor(site);
        public void Set(MediaKind kind, string site, SiteAnswer answer) => prefs.SetAnswer(site, answer);
        public bool IsBlocked(string site) => false;
    }

    private sealed class SoundQuestion(IMusicAsker asker) : IMediaAsker
    {
        public void Ask(MediaKind kind, string site, Uri url, Action<bool> answered) => asker.Ask(site, url, answered);
        public void Cancel(MediaKind kind) => asker.Cancel();
    }

    /// <summary>The parcel has a music address (the top bar shows its control).</summary>
    public bool HasMusic => _url is not null;

    /// <summary>The music plays now.</summary>
    public bool IsPlaying => _playing;

    /// <summary>The address the parcel has now; null when it has none, or one that is not http or https.</summary>
    public Uri? Address => _url;

    /// <summary>The parcel's music address as it stands now (empty or null: none). Called every frame.</summary>
    public void ParcelAddress(string? address)
    {
        Uri? next = RadioUrl.TryParse(address, out var uri) ? uri : null;
        if (next == _url)
        {
            if (_playing && _playback.SiteNeeded is { } site)
                NeedsSite(site.ToLowerInvariant());
            return;
        }
        _url = next;
        _furtherSites.Clear();
        Evaluate();
    }

    /// <summary>The agent left the parcel (or the region, or the session ended): the music stops.</summary>
    public void LeftParcel() => ParcelAddress(null);

    /// <summary>The person's click on the music control: off if it plays, otherwise on (and a site that was refused is asked again).</summary>
    public void Toggle()
    {
        if (_url is null)
            return;
        if (_playing)
        {
            _prefs.On = false;
            Stop();
            return;
        }
        _prefs.On = true;
        _consent.AskAgain(MediaKind.Sound, _furtherSites.Prepend(RadioUrl.Site(_url)));
        Evaluate();
    }

    /// <summary>The person turned music on or off somewhere else (the Sound page): the stream follows.</summary>
    public void SwitchChanged() => Evaluate();

    private void NeedsSite(string site)
    {
        Stop();
        if (!_furtherSites.Contains(site))
        {
            if (_furtherSites.Count >= MaxFurtherSites)
                return;
            _furtherSites.Add(site);
        }
        Evaluate();
    }

    private void Evaluate()
    {
        if (_url is null || !_prefs.On)
        {
            _consent.Release(MediaKind.Sound);
            Stop();
            return;
        }
        if (_consent.Want(MediaKind.Sound, _url, _furtherSites, Evaluate) != MediaDecision.Allowed)
        {
            Stop();
            return;
        }
        if (!_playing || _playingUrl != _url)
        {
            _playing = true;
            _playingUrl = _url;
            _playback.Play(_url);
        }
    }

    private void Stop()
    {
        if (!_playing)
            return;
        _playing = false;
        _playback.Stop();
    }
}
