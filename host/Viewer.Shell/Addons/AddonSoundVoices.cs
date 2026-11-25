// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Addons;
using Viewer.Core.Diagnostics;

namespace Viewer.Shell.Addons;

/// <summary>What plays add-ons' sounds: the engine side. Positions are metres in the current region; the master level is not in the
/// gain. Main thread only.</summary>
public interface IAddonSoundOutput
{
    /// <summary>Starts a voice on a WAV or Ogg file's bytes, already at its place, gain and low-pass. False when they do not decode.</summary>
    bool Start(int voice, byte[] bytes, bool loop, Vector3? position, float gain, float? lowPassHz);
    void Place(int voice, Vector3? position, float gain, float? lowPassHz);
    void Stop(int voice);
    bool IsPlaying(int voice);
}

/// <summary>The Ambient level (0 when muted) and where the person hears from, in the current region's metres.</summary>
public readonly record struct AddonSoundScene(float AmbientLevel, Vector3 Listener, bool Underwater);

/// <summary>One add-on sound as the tests and the automation see it. Gain is what is heard before the master level.</summary>
public sealed record AddonSoundView(int Voice, string AddonId, string File, bool Waiting, bool Loop, Vector3? Position, float Gain, float? LowPassHz);

/// <summary>
/// The add-ons' sounds (interface 0.6): which file may be played, the cap per add-on, delays on the viewer's clock, and each frame the
/// gain heard: the add-on's own, times the Ambient level, times for a placed sound the fall-off world sounds have (see
/// <see cref="Sound.WorldSoundMath"/>). Nothing is done in a frame with no sound. Main thread only.
/// </summary>
internal sealed class AddonSoundVoices(IAddonServices services, CoreLog log)
{
    public const int MaxPerAddon = 8;
    public const long MaxFileBytes = 8 * 1024 * 1024;
    public const double MaxDelaySeconds = 60;
    public const float MinLowPassHz = 20f, MaxLowPassHz = 20000f;

    private sealed class Voice : IAddonSound
    {
        private float _gain;
        private float? _lowPass;
        private Vector3? _position;

        public required AddonSoundVoices Owner;
        public required int Number;
        public required string AddonId, File;
        public required bool Loop;
        public byte[]? Bytes;          // held only while it waits
        public double StartsAt;

        public bool IsActive => Owner._voices.Contains(this);
        public bool Waiting => Bytes is not null;

        public float Gain
        {
            get => _gain;
            set
            {
                if (!float.IsFinite(value))
                    return;
                _gain = Math.Clamp(value, 0f, 1f);
                Owner.Place(this);
            }
        }

        public float? LowPassHz
        {
            get => _lowPass;
            set
            {
                if (value is { } hz && !float.IsFinite(hz))
                    return;
                _lowPass = LowPass(value);
                Owner.Place(this);
            }
        }

        public Vector3? Position
        {
            get => _position;
            set
            {
                if (_position is null || value is not { } p || !IsFinite(p))
                    return;   // an ambient sound stays ambient, a placed one stays placed
                _position = p;
                Owner.Place(this);
            }
        }

        public void Init(float gain, float? lowPass, Vector3? position)
        {
            _gain = Math.Clamp(gain, 0f, 1f);
            _lowPass = LowPass(lowPass);
            _position = position;
        }

        public void Stop() => Owner.Stop(this);
        public void Dispose() => Stop();

        private static float? LowPass(float? hz) => hz is { } v ? Math.Clamp(v, MinLowPassHz, MaxLowPassHz) : null;
    }

    private readonly List<Voice> _voices = [];
    private int _next;

    public IReadOnlyList<AddonSoundView> Views
    {
        get
        {
            if (_voices.Count == 0)
                return [];
            var scene = services.SoundScene();
            return _voices.Select(v => new AddonSoundView(v.Number, v.AddonId, v.File, v.Waiting, v.Loop, v.Position, Heard(v, scene), v.LowPassHz)).ToArray();
        }
    }

    public IAddonSound? Play(string addonId, string? folder, string file, AddonSoundOptions options)
    {
        if (services.SoundOutput is null || folder is null)
            return Refused(addonId, "no_sound");
        if (!float.IsFinite(options.Gain) || !double.IsFinite(options.DelaySeconds) || (options.LowPassHz is { } hz && !float.IsFinite(hz))
            || (options.Position is { } at && !IsFinite(at)))
            return Refused(addonId, "bad_option");
        if (_voices.Count(v => v.AddonId == addonId) >= MaxPerAddon)
            return Refused(addonId, "too_many");
        if (Read(folder, file, out string why) is not { } bytes)
            return Refused(addonId, why);
        var voice = new Voice { Owner = this, Number = ++_next, AddonId = addonId, File = file, Loop = options.Loop };
        voice.Init(options.Gain, options.LowPassHz, options.Position);
        double delay = Math.Clamp(options.DelaySeconds, 0, MaxDelaySeconds);
        voice.Bytes = bytes;
        voice.StartsAt = services.Now + delay;
        if (delay <= 0 && !Begin(voice, services.SoundScene()))
            return Refused(addonId, "does_not_decode");
        _voices.Add(voice);
        log.Info("addon.sound", CoreLog.F("id", addonId), CoreLog.F("voice", voice.Number), CoreLog.F("loop", voice.Loop),
            CoreLog.F("placed", voice.Position is not null), CoreLog.F("delay", delay));
        return voice;
    }

    private IAddonSound? Refused(string addonId, string why)
    {
        log.Warn("addon.sound_refused", CoreLog.F("id", addonId), CoreLog.F("why", why));
        return null;
    }

    private bool Begin(Voice v, AddonSoundScene scene)
    {
        if (services.SoundOutput is not { } output || !output.Start(v.Number, v.Bytes!, v.Loop, v.Position, Heard(v, scene), v.LowPassHz))
            return false;
        v.Bytes = null;
        return true;
    }

    /// <summary>Once a frame: starts the sounds whose time has come, lets go of those that ended, and keeps the rest at their gain.</summary>
    public void Update()
    {
        if (_voices.Count == 0 || services.SoundOutput is not { } output)
            return;
        var scene = services.SoundScene();
        double now = services.Now;
        foreach (var v in _voices.ToArray())
        {
            if (v.Waiting)
            {
                if (now >= v.StartsAt && !Begin(v, scene))
                {
                    _voices.Remove(v);
                    Refused(v.AddonId, "does_not_decode");
                }
            }
            else if (!v.Loop && !output.IsPlaying(v.Number))
                Stop(v);
            else
                output.Place(v.Number, v.Position, Heard(v, scene), v.LowPassHz);
        }
    }

    private void Place(Voice v)
    {
        if (!v.Waiting && v.IsActive)
            services.SoundOutput?.Place(v.Number, v.Position, Heard(v, services.SoundScene()), v.LowPassHz);
    }

    private static float Heard(Voice v, AddonSoundScene scene)
    {
        float gain = Sound.WorldSoundMath.ChannelGain(v.Gain, scene.AmbientLevel, false);
        return v.Position is { } at ? gain * Sound.WorldSoundMath.DistanceGain(Vector3.Distance(at, scene.Listener), scene.Underwater) : gain;
    }

    private void Stop(Voice v)
    {
        if (_voices.Remove(v) && !v.Waiting)
            services.SoundOutput?.Stop(v.Number);
        v.Bytes = null;
    }

    public void StopAddon(string addonId)
    {
        foreach (var v in _voices.Where(v => v.AddonId == addonId).ToArray())
            Stop(v);
    }

    public void StopAll()
    {
        foreach (var v in _voices.ToArray())
            Stop(v);
    }

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    /// <summary>The bytes of a sound file inside the add-on's folder; null, with why, for any other name or file. A name is relative,
    /// with no ".." and no ":", and neither the file nor a folder on the way to it may be a link.</summary>
    private static byte[]? Read(string folder, string file, out string why)
    {
        why = "outside_folder";
        if (string.IsNullOrWhiteSpace(file) || file.Contains(':') || file[0] is '/' or '\\' || Path.IsPathRooted(file))
            return null;
        string[] parts = file.Split('/', '\\');
        // a part of dots and spaces only is "." or "..", which Windows also reads from names with trailing dots or spaces
        if (parts.Any(p => p.TrimEnd('.', ' ').Length == 0))
            return null;
        string kind = Path.GetExtension(file).ToLowerInvariant();
        why = "not_a_sound";
        if (kind is not (".wav" or ".ogg"))
            return null;
        try
        {
            string path = Path.GetFullPath(folder);
            for (int i = 0; i < parts.Length; i++)
            {
                path = Path.Combine(path, parts[i]);
                FileSystemInfo info = i == parts.Length - 1 ? new FileInfo(path) : new DirectoryInfo(path);
                if (!info.Exists)
                {
                    why = "missing";
                    return null;
                }
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    why = "outside_folder";
                    return null;
                }
            }
            if (new FileInfo(path).Length > MaxFileBytes)
            {
                why = "too_large";
                return null;
            }
            byte[] bytes = File.ReadAllBytes(path);
            bool sound = kind == ".wav" ? Sound.WavFile.Parse(bytes) is { Bits: 16, Pcm.Length: > 0 } : IsOggVorbis(bytes);
            return sound ? bytes : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            why = "unreadable";
            return null;
        }
    }

    // an Ogg stream whose first packet is a Vorbis identification header
    private static bool IsOggVorbis(byte[] bytes) =>
        bytes.AsSpan().StartsWith("OggS"u8) && bytes.AsSpan(0, Math.Min(bytes.Length, 128)).IndexOf("\u0001vorbis"u8) >= 0;
}
