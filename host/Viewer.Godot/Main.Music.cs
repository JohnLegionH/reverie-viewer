// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using Godot;
using Viewer.Core.Diagnostics;
using Viewer.Core.Media;
using Viewer.Core.Music;
using Viewer.Core.Session;
using Viewer.Shell;
using Viewer.Shell.Sound;

namespace Viewer.Godot;

/// <summary>
/// Parcel music in the running viewer. Every frame the music address of the parcel the agent stands in goes to a <see cref="ParcelMusicController"/>
/// (which asks once per site, follows the person's switch and the parcel's address, and starts or stops a <see cref="RadioPlayer"/>); the player
/// reads and decodes the stream on its own thread, and the frames it decoded are handed to an audio stream generator here, at the music level of the
/// Sound page (the master level is the master bus's). The music control in the top bar shows while the parcel has music.
/// </summary>
public partial class Main
{
    private RadioPlayer? _radio;
    private ParcelMusicController? _parcelMusic;
    private AudioStreamPlayer? _musicPlayer;
    private AudioStreamGeneratorPlayback? _musicPlayback;
    private int _musicRate;
    private ConfirmationDialog? _musicAsk;
    private Action<bool>? _musicAnswer;
    private bool _musicWired, _musicWasActive;
    private (string Site, Action<bool> Answered)? _musicWaitingAsk;
    private (bool Has, bool Playing, bool Muted, double Level) _musicShown = (false, false, false, -1);
    private readonly float[] _musicFrames = new float[4096 * 2];

    /// <summary>The parcel music's controller (null until the first frame of a session), for the automation's checks.</summary>
    public ParcelMusicController? ParcelMusic => _parcelMusic;

    /// <summary>The stream player (null until the first frame of a session), for the automation's checks.</summary>
    public RadioPlayer? Radio => _radio;

    private sealed class MusicPlayback(RadioPlayer radio, MediaSiteSettings sites) : IMusicPlayback
    {
        // the player works on its own thread: it gets the answers as they stand now, and asks for any other site through SiteNeeded
        public void Play(Uri url) => radio.Play(url, sites.SoundSnapshot());
        public void Stop() => radio.Stop();
        public string? SiteNeeded => radio.SiteNeeded;
    }

    private sealed class MusicPrefs(SettingsService settings, MediaSiteSettings sites) : IMusicPrefs
    {
        public bool On { get => settings.Get(AudioLevels.MusicOn); set => settings.Set(AudioLevels.MusicOn, value); }
        public SiteAnswer AnswerFor(string site) => sites.IsBlocked(site) ? SiteAnswer.DontPlay : sites.Get(MediaKind.Sound, site);
        public void SetAnswer(string site, SiteAnswer answer) => sites.Set(MediaKind.Sound, site, answer);
    }

    private sealed class MusicAsker(Main main) : IMusicAsker
    {
        public void Ask(string site, Uri url, Action<bool> answered) => main.AskMusic(site, answered);
        public void Cancel() => main.CancelMusicAsk();
    }

    /// <summary>Every frame: the parcel's music address to the controller, the decoded music to the speakers, the control in the top bar.</summary>
    private void UpdateParcelMusic(ViewerSession? session)
    {
        var settings = UserSettings(null);
        if (_parcelMusic is null)
        {
            _radio = new RadioPlayer();
            var sites = new MediaSiteSettings(settings.GetText, (key, text) => settings.SetText(key, text));
            _parcelMusic = new ParcelMusicController(new MusicPlayback(_radio, sites), new MusicPrefs(settings, sites), new MusicAsker(this));
        }
        if (_musicWaitingAsk is { } waiting)
            AskMusic(waiting.Site, waiting.Answered);
        _parcelMusic.ParcelAddress(session?.ParcelMusicUrl);
        PumpMusic(settings);
        SyncMusicControl(settings, session is not null);
    }

    private void PumpMusic(SettingsService settings)
    {
        var radio = _radio!;
        int rate = radio.Output.SampleRate;
        if (rate > 0 && (_musicPlayer is null || rate != _musicRate))
        {
            if (_musicPlayer is null)
            {
                _musicPlayer = new AudioStreamPlayer { Name = "parcel_music" };
                AddChild(_musicPlayer);
            }
            _musicPlayer.Stream = new AudioStreamGenerator { MixRate = rate, BufferLength = 0.4f };
            _musicPlayer.Play();
            _musicPlayback = _musicPlayer.GetStreamPlayback() as AudioStreamGeneratorPlayback;
            _musicRate = rate;
        }
        if (_musicPlayback is not { } playback)
            return;
        if (radio.State == RadioState.Idle)
        {
            if (_musicWasActive)
                playback.ClearBuffer();   // the person turned it off or left the parcel: what was queued does not play out
            _musicWasActive = false;
            return;
        }
        _musicWasActive = true;
        float gain = settings.Get(AudioLevels.MuteMusic) ? 0f : (float)settings.Get(AudioLevels.Music);
        bool mono = settings.Get(AudioLevels.Mono);
        // fill the generator as far as the decoded music reaches (a slow frame must not leave it short)
        for (int free = playback.GetFramesAvailable(); free > 0;)
        {
            int got = radio.Output.Read(_musicFrames, Math.Min(free, _musicFrames.Length / 2));
            if (got == 0)
                break;
            for (int i = 0; i < got; i++)
            {
                float l = _musicFrames[i * 2] * gain, r = _musicFrames[i * 2 + 1] * gain;
                if (mono)
                    l = r = (l + r) / 2;
                playback.PushFrame(new Vector2(l, r));
            }
            free -= got;
        }
    }

    private void SyncMusicControl(SettingsService settings, bool inSession)
    {
        if (_shell?.TopBar is not { } top || _parcelMusic is not { } music)
            return;
        if (!_musicWired)
        {
            _musicWired = true;
            top.Music.ToggleRequested += () => _parcelMusic?.Toggle();
            top.Music.MuteRequested += () => settings.Set(AudioLevels.MuteMusic, !settings.Get(AudioLevels.MuteMusic));
            top.Music.VolumeRequested += v => settings.Set(AudioLevels.Music, v);
        }
        var now = (inSession && music.HasMusic, music.IsPlaying, settings.Get(AudioLevels.MuteMusic), settings.Get(AudioLevels.Music));
        if (now == _musicShown)
            return;
        _musicShown = now;
        top.Music.Show(now.IsPlaying, now.Item3, now.Item4);
        top.ShowMusic(now.Item1);
    }

    /// <summary>The question asked once per site: Play or Don't play.</summary>
    private void AskMusic(string site, Action<bool> answered)
    {
        if (_strings is not { } strings)
        {
            _musicWaitingAsk = (site, answered);   // the words are not ready yet: asked on a later frame, never answered for the person
            return;
        }
        if (PackBoxUp)
        {
            _musicWaitingAsk = (site, answered);   // one box at a time: asked when the pack's box is gone
            return;
        }
        _musicWaitingAsk = null;
        if (_musicAsk is null)
        {
            _musicAsk = BuildMusicAsk(strings);
            _musicAsk.Confirmed += () => Answer(true);
            _musicAsk.Canceled += () => Answer(false);
            AddChild(_musicAsk);
        }
        _musicAnswer = answered;
        ShowMusicAsk(_musicAsk, strings, site);
        Log.Info("music.ask", CoreLog.F("site", site));
    }

    /// <summary>The width the question's words wrap to, in interface units.</summary>
    private const int MusicAskWidth = 460;

    internal static ConfirmationDialog BuildMusicAsk(Viewer.Shell.StringTable strings) => new()
    {
        Name = "music_ask", Title = strings.Get("music.ask.title"), OkButtonText = strings.Get("music.ask.play"), CancelButtonText = strings.Get("music.ask.dontplay"),
        DialogAutowrap = true,
    };

    internal static void ShowMusicAsk(ConfirmationDialog dialog, Viewer.Shell.StringTable strings, string site)
    {
        dialog.DialogText = strings.Get("music.ask.text", site);
        PopupQuestion(dialog, MusicAskWidth);
    }

    /// <summary>Shows a question box of <paramref name="width"/> as tall as what it holds. Wrapped words know their height only once they have
    /// their width, so the words are given it first and the box takes its height again when they have been laid out.</summary>
    internal static void PopupQuestion(AcceptDialog dialog, int width)
    {
        dialog.GetLabel().CustomMinimumSize = new Vector2(width - QuestionSideMargins, 0);
        dialog.ResetSize();
        dialog.PopupCentered(new Vector2I(width, 0));
        Callable.From(() =>
        {
            if (!IsInstanceValid(dialog) || !dialog.Visible)
                return;
            dialog.Size = new Vector2I(width, (int)MathF.Ceiling(dialog.GetContentsMinimumSize().Y));
            dialog.MoveToCenter();
        }).CallDeferred();
    }

    /// <summary>What the theme keeps free beside a dialog's words, both sides together.</summary>
    private const int QuestionSideMargins = 24;

    private void CancelMusicAsk()
    {
        _musicWaitingAsk = null;
        _musicAnswer = null;
        if (_musicAsk is { Visible: true })
            _musicAsk.Hide();
    }

    private void Answer(bool play)
    {
        var answer = _musicAnswer;
        _musicAnswer = null;
        if (answer is not null)
            Log.Info("music.answer", CoreLog.F("play", play));
        answer?.Invoke(play);
    }
}
