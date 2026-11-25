// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Diagnostics;
using System.Numerics;
using System.Text;
using Viewer.Addons;
using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using Viewer.Shell;
using Viewer.Shell.Addons;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Add-on sounds (permission sound.play, built-in add-ons only): only sound files from the add-on's own folder, a voice cap, a delay on
/// the test's clock, the Ambient level and distance, the grid declaration, and every way the sounds stop. A recording output and a
/// test clock; each member makes its own folder, so the class runs in parallel.
/// </summary>
public class AddonSoundTests
{
    private sealed class Output : IAddonSoundOutput
    {
        public readonly Dictionary<int, (byte[] Bytes, bool Loop, Vector3? Position, float Gain, float? LowPass)> Playing = new();
        public int Started;
        public bool Decodes = true;
        public bool Start(int voice, byte[] bytes, bool loop, Vector3? position, float gain, float? lowPassHz)
        {
            if (!Decodes) return false;
            Started++;
            Playing[voice] = (bytes, loop, position, gain, lowPassHz);
            return true;
        }
        public void Place(int voice, Vector3? position, float gain, float? lowPassHz)
        {
            if (Playing.TryGetValue(voice, out var v)) Playing[voice] = (v.Bytes, v.Loop, position, gain, lowPassHz);
        }
        public void Stop(int voice) => Playing.Remove(voice);
        public bool IsPlaying(int voice) => Playing.ContainsKey(voice);
        /// <summary>The engine reached the end of a sound.</summary>
        public void Finish(int voice) => Playing.Remove(voice);
    }

    private sealed class Services : IAddonServices
    {
        public readonly Output Out = new();
        public double Clock;
        public AddonSoundScene Scene = new(0.5f, Vector3.Zero, false);
        public double Now => Clock;
        public void SendAnimations(IReadOnlyList<(Guid Animation, bool Start)> requests) { }
        public bool Say(string text) => false;
        public IAddonInventory? Inventory => null;
        public IAddonSoundOutput? SoundOutput => Out;
        public AddonSoundScene SoundScene() => Scene;
    }

    private sealed class Probe : IAddon
    {
        public IAddonContext? Context;
        public void Start(IAddonContext context) => Context = context;
        public void Stop() { }
    }

    private sealed record Rig(AddonHost Host, Services Services, Probe Probe, AddonEntry Entry, string Folder)
    {
        public IAddonSounds Sounds => Probe.Context!.Sounds!;
        public Output Out => Services.Out;
        public void At(double now) { Services.Clock = now; Host.Frame(); }
    }

    private static byte[] Wav(int bits = 16, int samples = 100)
    {
        var data = new byte[samples * (bits / 8)];
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write("RIFF"u8); w.Write(36 + data.Length); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(8000); w.Write(8000 * bits / 8); w.Write((short)(bits / 8)); w.Write((short)bits); w.Write("data"u8); w.Write(data.Length); w.Write(data);
        w.Flush();
        return stream.ToArray();
    }

    private static byte[] Ogg(string codec)
    {
        var bytes = new byte[64];
        "OggS"u8.CopyTo(bytes);
        Encoding.ASCII.GetBytes(codec).CopyTo(bytes, 28);
        return bytes;
    }

    private static string NewFolder()
    {
        string root = TestTemp.NewFolder("addon-sound");
        string folder = Path.Combine(root, "probe");
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        File.WriteAllBytes(Path.Combine(root, "outside.wav"), Wav());
        File.WriteAllBytes(Path.Combine(folder, "loop.wav"), Wav());
        File.WriteAllBytes(Path.Combine(folder, "sub", "inner.wav"), Wav());
        return folder;
    }

    private static Rig Start(bool builtIn = true, string[]? permissions = null, GridKind? login = GridKind.OpenSim, GridKind[]? grids = null)
    {
        var services = new Services();
        var host = new AddonHost(services, new CoreLog(new ListSink(), "test"), null);
        var probe = new Probe();
        string folder = NewFolder();
        var manifest = new AddonManifest("probe", "Probe", "1.0", "Someone", "MIT", "0.6", null, permissions ?? [AddonPermissions.SoundPlay], "probe.dll")
            { Grids = grids is { Length: > 0 } ? grids : null };
        var entry = host.Register(manifest, () => probe, builtIn, folder);
        host.StartEnabled();
        if (!builtIn)
            host.SetEnabled("probe", true, grantPermissions: true);
        if (login is { } grid)
            host.LoggedIn(grid);
        return new Rig(host, services, probe, entry, folder);
    }

    // ---------------- the permission ----------------

    [Fact]
    public void Playing_a_sound_needs_sound_play_and_the_manifest_knows_it()
    {
        Assert.Contains("sound.play", AddonPermissions.All);
        var r = Start(permissions: [AddonPermissions.Window]);
        var refused = Assert.Throws<AddonPermissionException>(() => r.Probe.Context!.Sounds);
        Assert.Equal("sound.play", refused.Permission);
    }

    [Fact]
    public void An_installed_add_on_is_refused_sound_play_even_when_the_user_granted_it()
    {
        var r = Start(builtIn: false);
        Assert.Contains(AddonPermissions.SoundPlay, r.Entry.Granted);
        Assert.Throws<AddonPermissionException>(() => r.Probe.Context!.Sounds);
    }

    // ---------------- which files ----------------

    [Fact]
    public void A_wav_in_the_add_ons_folder_plays_under_the_ambient_level()
    {
        var r = Start();
        var sound = r.Sounds.Play("loop.wav", new AddonSoundOptions(Loop: true, Gain: 0.8f, LowPassHz: 900));
        Assert.NotNull(sound);
        Assert.True(sound.IsActive);
        var voice = Assert.Single(r.Out.Playing).Value;
        Assert.Equal(File.ReadAllBytes(Path.Combine(r.Folder, "loop.wav")), voice.Bytes);
        Assert.True(voice.Loop);
        Assert.Null(voice.Position);
        Assert.Equal(0.4f, voice.Gain, 5);
        Assert.Equal(900f, voice.LowPass);
        var view = Assert.Single(r.Host.Sounds);
        Assert.Equal(("probe", "loop.wav", false), (view.AddonId, view.File, view.Waiting));
    }

    [Fact]
    public void A_name_that_leads_outside_the_folder_is_refused()
    {
        var r = Start();
        Assert.NotNull(r.Sounds.Play("sub/inner.wav"));
        string outside = Path.GetFullPath(Path.Combine(r.Folder, "..", "outside.wav"));
        Assert.True(File.Exists(outside));
        foreach (string name in new[] { "../outside.wav", "..\\outside.wav", "sub/../../outside.wav", "sub/../loop.wav", outside, "/outside.wav",
                     "\\outside.wav", "C:outside.wav", "\\\\server\\share\\x.wav", "loop.wav:stream.wav", "", " " })
            Assert.True(r.Sounds.Play(name) is null, name);
        Assert.Equal(1, r.Out.Started);
        Assert.True(r.Entry.Running);
    }

    [Fact]
    public void A_link_inside_the_folder_that_leads_outside_it_is_refused()
    {
        var r = Start();
        string elsewhere = Path.Combine(Path.GetDirectoryName(r.Folder)!, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllBytes(Path.Combine(elsewhere, "far.wav"), Wav());
        string link = Path.Combine(r.Folder, "link");
        if (OperatingSystem.IsWindows())
        {
            using var made = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{elsewhere}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
            made.StandardOutput.ReadToEnd();
            made.WaitForExit();
        }
        else
            Directory.CreateSymbolicLink(link, elsewhere);
        Assert.True(File.Exists(Path.Combine(link, "far.wav")));
        Assert.NotNull(r.Sounds.Play("loop.wav"));
        Assert.Null(r.Sounds.Play("link/far.wav"));
        Assert.Equal(1, r.Out.Started);
    }

    [Fact]
    public void A_file_that_is_not_a_sound_is_refused()
    {
        var r = Start();
        void Put(string name, byte[] bytes) => File.WriteAllBytes(Path.Combine(r.Folder, name), bytes);
        Put("music.ogg", Ogg("\u0001vorbis"));
        Put("text.wav", "not a sound at all"u8.ToArray());
        Put("sound.txt", Wav());
        Put("sound.dll", Wav());
        Put("eight.wav", Wav(bits: 8));
        Put("opus.ogg", Ogg("OpusHead"));
        Put("wav-named.ogg", Wav());
        Put("ogg-named.wav", Ogg("\u0001vorbis"));
        Put("empty.wav", Wav(samples: 0));
        Put("huge.wav", Wav(samples: 4 * 1024 * 1024 + 100));
        Assert.NotNull(r.Sounds.Play("music.ogg"));
        foreach (string name in new[] { "text.wav", "sound.txt", "sound.dll", "eight.wav", "opus.ogg", "wav-named.ogg", "ogg-named.wav", "empty.wav", "huge.wav", "missing.wav", "sub" })
            Assert.True(r.Sounds.Play(name) is null, name);
        Assert.Equal(1, r.Out.Started);
    }

    [Fact]
    public void A_sound_the_engine_cannot_decode_is_not_started()
    {
        var r = Start();
        r.Out.Decodes = false;
        Assert.Null(r.Sounds.Play("loop.wav"));
        Assert.Empty(r.Host.Sounds);
        r.Out.Decodes = true;
        Assert.NotNull(r.Sounds.Play("loop.wav"));
    }

    [Fact]
    public void The_sound_sample_asks_to_play_sounds_and_its_own_file_is_accepted()
    {
        string root = typeof(AddonSoundTests).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>().Single(a => a.Key == "RepoRoot").Value!;
        string folder = Path.Combine(root, "samples", "SoundSample");
        var manifest = AddonManifest.Parse(File.ReadAllText(Path.Combine(folder, AddonManifest.FileName)), out var problems);
        Assert.Empty(problems);
        Assert.Contains(AddonPermissions.SoundPlay, manifest!.Permissions);
        var services = new Services();
        var host = new AddonHost(services, new CoreLog(new ListSink(), "test"), null);
        var probe = new Probe();
        host.Register(manifest, () => probe, builtIn: true, folder);
        host.StartEnabled();
        host.SetEnabled(manifest.Id, true);
        host.LoggedIn(GridKind.OpenSim);
        Assert.NotNull(probe.Context!.Sounds!.Play("tone.wav"));
    }

    // ---------------- cap, delay, end ----------------

    [Fact]
    public void An_add_on_has_at_most_eight_sounds_waiting_or_playing()
    {
        var r = Start();
        var sounds = Enumerable.Range(0, 8).Select(i => r.Sounds.Play("loop.wav", new AddonSoundOptions(Loop: true, DelaySeconds: i % 2 == 0 ? 0 : 30))).ToArray();
        Assert.All(sounds, s => Assert.NotNull(s));
        Assert.Null(r.Sounds.Play("loop.wav"));
        Assert.Equal(8, r.Host.Sounds.Count);
        sounds[0]!.Stop();
        Assert.False(sounds[0]!.IsActive);
        Assert.NotNull(r.Sounds.Play("loop.wav"));
    }

    [Fact]
    public void A_delayed_sound_starts_when_the_clock_reaches_its_time()
    {
        var r = Start();
        r.Services.Clock = 100;
        var sound = r.Sounds.Play("loop.wav", new AddonSoundOptions(DelaySeconds: 3));
        Assert.NotNull(sound);
        Assert.True(sound.IsActive);
        Assert.True(Assert.Single(r.Host.Sounds).Waiting);
        r.At(100);
        r.At(102.99);
        Assert.Equal(0, r.Out.Started);
        r.At(103);
        Assert.Equal(1, r.Out.Started);
        Assert.False(Assert.Single(r.Host.Sounds).Waiting);
        r.At(103.5);
        Assert.Equal(1, r.Out.Started);
    }

    [Fact]
    public void A_sound_played_once_ends_when_the_engine_finishes_it_and_disposing_stops_one()
    {
        var r = Start();
        var once = r.Sounds.Play("loop.wav")!;
        Assert.NotNull(once);
        var loop = r.Sounds.Play("loop.wav", new AddonSoundOptions(Loop: true))!;
        r.At(1);
        Assert.Equal(2, r.Host.Sounds.Count);
        r.Out.Finish(r.Out.Playing.First(v => !v.Value.Loop).Key);
        r.At(2);
        Assert.False(once.IsActive);
        Assert.True(loop.IsActive);
        Assert.Single(r.Host.Sounds);
        loop.Dispose();
        Assert.Empty(r.Out.Playing);
        Assert.Empty(r.Host.Sounds);
    }

    // ---------------- level, distance, options ----------------

    [Fact]
    public void The_ambient_level_and_mute_apply_to_a_sound_already_playing()
    {
        var r = Start();
        Assert.NotNull(r.Sounds.Play("loop.wav", new AddonSoundOptions(Loop: true)));
        r.Services.Scene = new(0f, Vector3.Zero, false);   // Ambient muted
        r.At(1);
        Assert.Equal(0f, Assert.Single(r.Out.Playing).Value.Gain);
        Assert.Equal(0f, Assert.Single(r.Host.Sounds).Gain);
        r.Services.Scene = new(0.25f, Vector3.Zero, false);
        r.At(2);
        Assert.Equal(0.25f, Assert.Single(r.Out.Playing).Value.Gain, 5);
    }

    [Fact]
    public void A_placed_sound_falls_off_with_distance_as_world_sounds_do_and_can_be_moved()
    {
        var r = Start();
        var sound = r.Sounds.Play("loop.wav", new AddonSoundOptions(Loop: true, Position: new Vector3(0, 5, 0)))!;
        Assert.NotNull(sound);
        Assert.Equal(new Vector3(0, 5, 0), Assert.Single(r.Out.Playing).Value.Position);
        Assert.Equal(0.1f, Assert.Single(r.Out.Playing).Value.Gain, 5);
        sound.Position = new Vector3(0, 10, 0);
        r.At(1);
        Assert.Equal(0.05f, Assert.Single(r.Out.Playing).Value.Gain, 5);
        r.Services.Scene = new(0.5f, new Vector3(0, 9, 0), false);
        r.At(2);
        Assert.Equal(0.5f, Assert.Single(r.Out.Playing).Value.Gain, 5);
        r.Services.Scene = new(0.5f, Vector3.Zero, true);   // under water the fall-off is five times as steep
        r.At(3);
        Assert.Equal(0.5f / 46f, Assert.Single(r.Out.Playing).Value.Gain, 5);
    }

    [Fact]
    public void Options_are_held_in_range_and_values_that_are_not_numbers_are_refused_or_ignored()
    {
        var r = Start();
        r.Services.Scene = new(1f, Vector3.Zero, false);
        var ambient = r.Sounds.Play("loop.wav", new AddonSoundOptions(Loop: true, Gain: 3, DelaySeconds: -5, LowPassHz: 5))!;
        Assert.NotNull(ambient);
        Assert.Equal((1f, 20f), (ambient.Gain, ambient.LowPassHz!.Value));
        Assert.Equal(1, r.Out.Started);
        ambient.Gain = float.NaN;
        ambient.LowPassHz = float.PositiveInfinity;
        ambient.Position = new Vector3(1, 1, 1);
        Assert.Equal((1f, 20f), (ambient.Gain, ambient.LowPassHz!.Value));
        Assert.Null(ambient.Position);
        ambient.Gain = -1;
        ambient.LowPassHz = 1e9f;
        r.At(1);
        Assert.Equal((0f, 20000f), (ambient.Gain, ambient.LowPassHz!.Value));
        ambient.LowPassHz = null;
        r.At(2);
        Assert.Null(Assert.Single(r.Out.Playing).Value.LowPass);
        ambient.Stop();
        foreach (var bad in new[] { new AddonSoundOptions(Gain: float.NaN), new AddonSoundOptions(DelaySeconds: double.NaN),
                     new AddonSoundOptions(LowPassHz: float.NaN), new AddonSoundOptions(Position: new Vector3(float.NaN, 0, 0)) })
            Assert.Null(r.Sounds.Play("loop.wav", bad));
        r.Services.Clock = 10;
        Assert.NotNull(r.Sounds.Play("loop.wav", new AddonSoundOptions(DelaySeconds: 500)));
        r.At(69.9);
        Assert.Equal(1, r.Out.Started);
        r.At(70);
        Assert.Equal(2, r.Out.Started);
    }

    // ---------------- every way they stop ----------------

    private static IAddonSound[] Three(Rig r)
    {
        var sounds = new[]
        {
            r.Sounds.Play("loop.wav", new AddonSoundOptions(Loop: true)),
            r.Sounds.Play("loop.wav", new AddonSoundOptions(Position: new Vector3(1, 1, 1))),
            r.Sounds.Play("loop.wav", new AddonSoundOptions(DelaySeconds: 5)),
        };
        Assert.All(sounds, s => Assert.NotNull(s));
        Assert.Equal(2, r.Out.Playing.Count);
        return sounds!;
    }

    private static void AllStopped(Rig r, IAddonSound[] sounds)
    {
        Assert.Empty(r.Out.Playing);
        Assert.Empty(r.Host.Sounds);
        r.At(100);
        Assert.Equal(2, r.Out.Started);   // the one still waiting never starts
        Assert.All(sounds, s => Assert.False(s.IsActive));
    }

    [Fact]
    public void Switching_the_add_on_off_stops_its_sounds()
    {
        var r = Start();
        var held = r.Sounds;
        var sounds = Three(r);
        r.Host.SetEnabled("probe", false);
        AllStopped(r, sounds);
        Assert.Null(held.Play("loop.wav"));
    }

    [Fact]
    public void An_add_on_that_fails_has_its_sounds_stopped()
    {
        var r = Start();
        var sounds = Three(r);
        r.Probe.Context!.Tick += () => throw new InvalidOperationException("broken");
        r.Host.Frame();
        Assert.False(r.Entry.Running);
        AllStopped(r, sounds);
    }

    [Fact]
    public void Logging_out_stops_the_sounds_and_none_starts_while_logged_out()
    {
        var r = Start();
        var sounds = Three(r);
        r.Host.LoggedOut();
        AllStopped(r, sounds);
        Assert.Null(r.Sounds.Play("loop.wav"));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Another_region_or_the_end_of_a_teleport_stops_the_sounds_before_the_world_events(bool regionChanges, bool teleported)
    {
        var r = Start(permissions: [AddonPermissions.SoundPlay, AddonPermissions.RegionRead]);
        r.Host.InRegion("Home", 1);
        var sounds = Three(r);
        var playing = new List<int>();
        var started = new List<IAddonSound?>();
        var world = r.Probe.Context!.World!;
        void Heard()
        {
            playing.Add(r.Host.Sounds.Count);
            started.Add(r.Sounds.Play("loop.wav", new AddonSoundOptions(Loop: true)));
        }
        world.RegionChanged += _ => Heard();
        world.Teleported += _ => Heard();
        r.Host.InRegion("Home", 1);
        Assert.Equal(3, r.Host.Sounds.Count);   // nothing happened: they go on
        if (teleported)
            r.Host.TeleportArrived();
        r.Host.InRegion(regionChanges ? "Far" : "Home", regionChanges ? 2UL : 1UL);
        Assert.All(sounds, s => Assert.False(s.IsActive));
        Assert.Equal(regionChanges && teleported ? 2 : 1, playing.Count);
        Assert.Equal(0, playing[0]);
        Assert.All(started, s => Assert.True(s!.IsActive));   // started in a handler: it goes on
    }

    // ---------------- the grid ----------------

    [Theory]
    [InlineData(GridKind.OpenSim, new GridKind[0], true)]
    [InlineData(GridKind.SecondLife, new GridKind[0], false)]
    [InlineData(GridKind.SecondLife, new[] { GridKind.SecondLife }, true)]
    [InlineData(GridKind.OpenSim, new[] { GridKind.SecondLife }, false)]
    public void Sounds_play_only_on_a_grid_the_add_on_may_use_them_on(GridKind grid, GridKind[] declared, bool plays)
    {
        var r = Start(login: grid, grids: declared);
        Assert.Equal(plays, r.Sounds.Play("loop.wav") is not null);
        Assert.Equal(plays ? 1 : 0, r.Out.Started);
        Assert.True(r.Entry.Running);
    }
}
