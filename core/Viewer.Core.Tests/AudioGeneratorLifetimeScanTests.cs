// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text.RegularExpressions;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// An audio generator must outlive every mix of its playback. In Godot 4.7 an AudioStreamGeneratorPlayback keeps only a plain pointer
/// to the AudioStreamGenerator that made it (servers/audio/effects/audio_stream_generator.h), and its mix reads the generator's mix
/// rate through that pointer (AudioStreamPlaybackResampled::mix calls get_stream_sampling_rate). A stopped playback is mixed once
/// more, to fade out, before the audio server takes it off its list (servers/audio/audio_server.cpp, _mix_step), and the engine frees
/// the scene before it stops the audio thread (main/main.cpp, Main::cleanup: delete_main_loop before audio_server->finish). So a
/// generator held only by its player is freed at quit while the audio thread may still mix its playback: an access violation in the
/// engine's shutdown, seen after a garbage collection had finalized the generator's managed wrapper.
/// This source scan checks that every generator is kept in a field, and that each one is stopped and kept until a whole mix step
/// has run after the stop, both at quit and when the music's rate changes.
/// </summary>
public class AudioGeneratorLifetimeScanTests
{
    private static string HostDir => Path.Combine(TestPaths.RepoRoot, "host", "Viewer.Godot");

    private static string Host(string file) => File.ReadAllText(Path.Combine(HostDir, file));

    private static IEnumerable<(string File, string Text)> HostFiles()
    {
        foreach (string file in Directory.EnumerateFiles(HostDir, "*.cs", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(HostDir, file).Replace('\\', '/');
            if (rel.StartsWith("build/", StringComparison.Ordinal) || rel.StartsWith(".godot/", StringComparison.Ordinal) || rel.Contains("/obj/") || rel.StartsWith("obj/", StringComparison.Ordinal))
                continue;
            yield return (rel, File.ReadAllText(file));
        }
    }

    [Fact]
    public void Every_generator_is_kept_in_a_field_not_made_inline_for_a_player()
    {
        var all = HostFiles().ToList();
        var inline = all.Where(f => Regex.IsMatch(f.Text, @"(?<!\w)Stream\s*=\s*new\s+AudioStreamGenerator\b")).Select(f => f.File).ToList();
        Assert.Empty(inline);
        // every generator made is assigned to a field of the host
        var made = all.SelectMany(f => Regex.Matches(f.Text, @"(\S+)\s*=\s*new\s+AudioStreamGenerator\b").Select(m => (f.File, Target: m.Groups[1].Value))).ToList();
        Assert.NotEmpty(made);
        Assert.All(made, m => Assert.Matches(@"^_\w+Stream$", m.Target));
    }

    [Fact]
    public void Every_generator_is_stopped_and_kept_until_a_mix_step_has_passed_at_quit()
    {
        var all = HostFiles().ToList();
        var fields = all.SelectMany(f => Regex.Matches(f.Text, @"AudioStreamGenerator\?\s+(_\w+Stream)\b").Select(m => m.Groups[1].Value)).Distinct().ToList();
        var playbacks = all.SelectMany(f => Regex.Matches(f.Text, @"AudioStreamGeneratorPlayback\?\s+(_\w+Playback)\b").Select(m => m.Groups[1].Value)).Distinct().ToList();
        Assert.Equal(2, fields.Count);      // wind and parcel music
        Assert.Equal(2, playbacks.Count);
        string release = Host("Main.AudioGenerators.cs");
        int quit = release.IndexOf("private void ReleaseAudioGenerators()", StringComparison.Ordinal);
        Assert.True(quit >= 0, "Main.AudioGenerators.cs has ReleaseAudioGenerators");
        foreach (string field in fields)
            Assert.Matches(new Regex($@"ReleaseGenerator\(_\w+Player,\s*ref\s+_\w+Playback,\s*ref\s+{field}\)"), release.Substring(quit));
        foreach (string playback in playbacks)
            Assert.Matches(new Regex($@"ReleaseGenerator\(_\w+Player,\s*ref\s+{playback},\s*ref\s+_\w+Stream\)"), release.Substring(quit));
        // the stop happens under the audio server's lock, so no mix step is half done when the stop's time is taken
        int stop = release.IndexOf("private void ReleaseGenerator(", StringComparison.Ordinal);
        Assert.True(stop >= 0);
        int locked = release.IndexOf("AudioServer.Lock();", stop, StringComparison.Ordinal);
        int stopCall = release.IndexOf(".Stop();", stop, StringComparison.Ordinal);
        int unlocked = release.IndexOf("AudioServer.Unlock();", stop, StringComparison.Ordinal);
        Assert.True(locked > stop && stopCall > locked && unlocked > stopCall, "the player is stopped under AudioServer.Lock");
        // a generator's wrapper is disposed only after the witness has seen a whole mix step, and after its playback's wrapper
        int prune = release.IndexOf("private int PruneRetiredGenerators(", StringComparison.Ordinal);
        Assert.True(prune >= 0);
        int done = release.IndexOf("Witness.Done", prune, StringComparison.Ordinal);
        int playbackGone = release.IndexOf("Playback?.Dispose();", prune, StringComparison.Ordinal);
        int generatorGone = release.IndexOf("Generator.Dispose();", prune, StringComparison.Ordinal);
        Assert.True(done > prune && playbackGone > done && generatorGone > playbackGone, "disposed after the witness, playback before generator");
        // a generator the quit could not see mixed past is given an engine reference that is never returned, so it is never freed
        int timeout = release.IndexOf("if (waiting > 0)", quit, StringComparison.Ordinal);
        Assert.True(timeout > quit && release.IndexOf("r.Generator.Reference();", timeout, StringComparison.Ordinal) > timeout
            && release.IndexOf("r.Playback?.Reference();", timeout, StringComparison.Ordinal) > timeout, "the timeout keeps an engine reference");
        // _ExitTree releases them before the engine's shutdown, even when an earlier step threw
        string main = Host("Main.cs");
        int exit = main.IndexOf("public override void _ExitTree()", StringComparison.Ordinal);
        int fin = main.IndexOf("finally", exit, StringComparison.Ordinal);
        int stage = main.IndexOf("_quitWatchdog.Enter(\"audio\")", exit, StringComparison.Ordinal);
        int call = main.IndexOf("ReleaseAudioGenerators();", exit, StringComparison.Ordinal);
        int engine = main.IndexOf("_quitWatchdog.Enter(\"engine-shutdown\")", exit, StringComparison.Ordinal);
        Assert.True(exit >= 0 && fin > exit && stage > fin && call > stage && engine > call, "_ExitTree's finally releases the generators in stage audio before the engine's shutdown");
    }

    [Fact]
    public void A_music_rate_change_retires_the_old_generator_before_making_the_new_one()
    {
        string music = Host("Main.Music.cs");
        int release = music.IndexOf("ReleaseGenerator(_musicPlayer, ref _musicPlayback, ref _musicStream)", StringComparison.Ordinal);
        int create = music.IndexOf("new AudioStreamGenerator", StringComparison.Ordinal);
        Assert.True(release >= 0 && create > release, "the old playback and generator are retired before a new generator is made");
        // retired generators are let go on later frames, once their witness is done
        Assert.Contains("PruneRetiredGenerators();", Host("Main.WorldSounds.cs"));
    }
}
