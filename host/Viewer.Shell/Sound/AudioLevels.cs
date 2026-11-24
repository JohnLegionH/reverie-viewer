// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell.Sound;

/// <summary>
/// The Sound page's levels beside the interface level: Master (over everything), Sounds (objects in the world) and Ambient (footsteps,
/// wind) and Music (the stream of a parcel), each with its mute, at the official viewer's defaults (master 1.0, sounds 0.5, ambient 0.5, music 0.5). Media and voice
/// are not built yet and have no setting. Mute when minimized (the interface sounds' setting) silences these too. The page also
/// holds the official collision-sound and gesture-sound switches (EnableCollisionSounds, EnableGestureSounds; both on by default) and the
/// switch for our own footstep where a grid has no step sound (on by default).
/// </summary>
public static class AudioLevels
{
    private static double? Level(double v) => double.IsFinite(v) ? Math.Clamp(v, 0, 1) : null;

    public static readonly Setting<double> Master = new("audio_level_master", 1.0, Level);
    public static readonly Setting<bool> MuteMaster = new("audio_mute_master", false, v => v);
    public static readonly Setting<double> Sounds = new("audio_level_sounds", 0.5, Level);
    public static readonly Setting<bool> MuteSounds = new("audio_mute_sounds", false, v => v);
    public static readonly Setting<double> Ambient = new("audio_level_ambient", 0.5, Level);
    public static readonly Setting<bool> MuteAmbient = new("audio_mute_ambient", false, v => v);
    /// <summary>Parcel music: the stream a parcel names (an internet radio address). Its level and mute are the Sound page's Music row and
    /// the top bar's volume beside the music control (one setting, so the two stay in step); official default 0.5.</summary>
    public static readonly Setting<double> Music = new("audio_level_music", 0.5, Level);
    public static readonly Setting<bool> MuteMusic = new("audio_mute_music", false, v => v);
    /// <summary>The person's switch for parcel music (the music control in the top bar). Off stays off across parcels and relogs until the person turns it on.</summary>
    public static readonly Setting<bool> MusicOn = new("audio_music_on", true, v => v);
    /// <summary>The settings key of the answers to the question asked once per site (see <see cref="MusicSiteAnswers"/>).</summary>
    public const string MusicSitesKey = "audio_music_sites";
    public static readonly Setting<bool> CollisionSounds = new("audio_collision_sounds", true, v => v);
    public static readonly Setting<bool> GestureSounds = new("audio_gesture_sounds", true, v => v);
    /// <summary>Mono: both ears get the same mix (for one-sided hearing or one earphone). Off by default.</summary>
    public static readonly Setting<bool> Mono = new("audio_mono", false, v => v);

    public static IReadOnlyList<string> Keys { get; } = [Master.Key, MuteMaster.Key, Sounds.Key, MuteSounds.Key, Ambient.Key, MuteAmbient.Key,
        Music.Key, MuteMusic.Key, MusicOn.Key, CollisionSounds.Key, GestureSounds.Key, Mono.Key];

    /// <summary>The level of a kind of world sound (0 when it is muted), master not included.</summary>
    public static float KindLevel(SettingsService settings, WorldSoundKind kind) => kind switch
    {
        WorldSoundKind.Ambient => settings.Get(MuteAmbient) ? 0f : (float)settings.Get(Ambient),
        _ => settings.Get(MuteSounds) ? 0f : (float)settings.Get(Sounds),
    };

    /// <summary>The master level (0 when muted, or when minimized with mute when minimized on).</summary>
    public static float MasterLevel(SettingsService settings, bool minimized) =>
        settings.Get(MuteMaster) || (minimized && settings.Get(UiSoundSettings.MuteWhenMinimized)) ? 0f : (float)settings.Get(Master);
    /// <summary>The master bus's stereo spread for the mono setting: 0 folds both channels into one mix, 1 leaves stereo as it is.</summary>
    public static float StereoSpread(SettingsService settings) => settings.Get(Mono) ? 0f : 1f;

    /// <summary>How strongly sounds placed in the world are put to one side: none with Mono on (each sound reaches both ears at the same
    /// gain, wherever it is), full otherwise.</summary>
    public static float PanningStrength(SettingsService settings) => settings.Get(Mono) ? 0f : 1f;
}
