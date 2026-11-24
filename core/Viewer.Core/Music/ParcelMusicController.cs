// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Music;

/// <summary>What a person answered about a site's stream.</summary>
public enum SiteAnswer { Ask, Play, DontPlay }

/// <summary>Starts and stops the stream that plays the parcel's music.</summary>
public interface IMusicPlayback
{
    /// <summary>Plays <paramref name="url"/>, in place of any stream that plays.</summary>
    void Play(Uri url);
    void Stop();
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
}

/// <summary>
/// Decides what the parcel's music does. It is told the parcel's music address whenever the parcel says it (the same address again changes nothing;
/// a new one switches at once, whatever the update's sequence number was; an empty one, or leaving the parcel, stops the music), and it follows
/// the person's switch (<see cref="Toggle"/>) and the answer remembered for the site. Music plays only when the switch is on and the site's answer
/// is Play; a site not answered yet is asked once. It works on one thread (the one that drives the interface).
/// </summary>
public sealed class ParcelMusicController
{
    private readonly IMusicPlayback _playback;
    private readonly IMusicPrefs _prefs;
    private readonly IMusicAsker _asker;
    private Uri? _url, _playingUrl;
    private bool _playing;
    private string? _asking;

    public ParcelMusicController(IMusicPlayback playback, IMusicPrefs prefs, IMusicAsker asker)
    {
        _playback = playback;
        _prefs = prefs;
        _asker = asker;
    }

    /// <summary>The parcel has a music address (the top bar shows its control).</summary>
    public bool HasMusic => _url is not null;

    /// <summary>The music plays now.</summary>
    public bool IsPlaying => _playing;

    /// <summary>The address the parcel has now; null when it has none, or one that is not http or https.</summary>
    public Uri? Address => _url;

    /// <summary>The parcel's music address as it stands now (empty or null: none).</summary>
    public void ParcelAddress(string? address)
    {
        Uri? next = RadioUrl.TryParse(address, out var uri) ? uri : null;
        if (next == _url)
            return;
        _url = next;
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
        string site = RadioUrl.Site(_url);
        if (_prefs.AnswerFor(site) == SiteAnswer.DontPlay)
            _prefs.SetAnswer(site, SiteAnswer.Ask);
        Evaluate();
    }

    /// <summary>The person turned music on or off somewhere else (the Sound page): the stream follows.</summary>
    public void SwitchChanged() => Evaluate();

    private void Evaluate()
    {
        if (_url is null || !_prefs.On)
        {
            Stop();
            return;
        }
        string site = RadioUrl.Site(_url);
        switch (_prefs.AnswerFor(site))
        {
            case SiteAnswer.Play:
                if (!_playing || _playingUrl != _url)
                {
                    _playing = true;
                    _playingUrl = _url;
                    _playback.Play(_url);
                }
                break;
            case SiteAnswer.DontPlay:
                Stop();
                break;
            default:
                Stop();
                if (_asking != site)
                {
                    _asking = site;
                    _asker.Ask(site, _url, ok => Answered(site, ok));
                }
                break;
        }
    }

    private void Answered(string site, bool play)
    {
        if (_asking == site)
            _asking = null;
        _prefs.SetAnswer(site, play ? SiteAnswer.Play : SiteAnswer.DontPlay);
        Evaluate();
    }

    private void Stop()
    {
        if (!_playing)
            return;
        _playing = false;
        _playback.Stop();
    }
}
