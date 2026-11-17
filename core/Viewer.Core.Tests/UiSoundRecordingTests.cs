// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell.Sound;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The interface sounds shipped as recordings: which slots they are (the rest keep the generator's sound), their format and level
/// against the official viewer's sound for the same event, that the tuner and the generator leave them alone, and that the notices
/// credit each one. Reads only committed files and the shell's resources, so the class runs in parallel.
/// </summary>
public class UiSoundRecordingTests
{
    // the slots that keep the generator's sound for now
    private static readonly UiSoundSlot[] Generated =
    [
        UiSoundSlot.ChatPing, UiSoundSlot.HealthReductionF, UiSoundSlot.HealthReductionM, UiSoundSlot.Restart, UiSoundSlot.Snapshot,
        UiSoundSlot.Typing,
    ];

    private static string RepoFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Viewer.sln")))
                return Path.Combine(dir.FullName, relative);
        throw new InvalidOperationException("no repository root above the test");
    }

    private static (short[] Samples, WavData Wav) Shipped(string file)
    {
        var bytes = UiSoundFiles.Read(file);
        Assert.True(bytes is not null, file + " is not shipped");
        var wav = WavFile.Parse(bytes!);
        Assert.True(wav is not null, file + " is not a WAV");
        var s = new short[wav!.Pcm.Length / 2];
        Buffer.BlockCopy(wav.Pcm, 0, s, 0, s.Length * 2);
        return (s, wav);
    }

    [Fact]
    public void Seventeen_slots_play_a_recording_and_the_other_six_keep_the_generators_sound()
    {
        Assert.Equal(17, UiSoundRecordings.All.Count);
        Assert.Equal(UiSoundCatalog.All.Select(i => i.Slot).Except(Generated).Order(), UiSoundRecordings.All.Select(r => r.Slot).Order());
        foreach (var r in UiSoundRecordings.All)
        {
            Assert.True(UiSoundRecordings.IsRecorded(r.File));
            Assert.DoesNotContain(UiSoundSynth.Recipes, x => x.File == r.File);   // the generator does not write it
            Assert.DoesNotContain(r.File, SoundTunerList.Files);                  // the tuner has no recipe to show for it
            Assert.Null(UiSoundTuning.Committed(r.File));
        }
        foreach (var slot in Generated)
        {
            string file = UiSoundCatalog.Of(slot).File;
            Assert.False(UiSoundRecordings.IsRecorded(file));
            Assert.Contains(file, SoundTunerList.Files);
            Assert.Equal(UiSoundSynth.Wav(UiSoundSynth.Render(UiSoundTuning.Committed(file)!)), UiSoundFiles.Read(file));
        }
    }

    [Fact]
    public void Each_recording_is_16_bit_mono_at_44100_peaking_at_most_minus_1_dbfs_with_soft_ends()
    {
        Assert.Equal(17, UiSoundRecordings.All.Count);
        foreach (var r in UiSoundRecordings.All)
        {
            var (s, wav) = Shipped(r.File);
            Assert.Equal((44100, 1, 16), (wav.Rate, wav.Channels, wav.Bits));
            Assert.True(s.Length > 0, r.File);
            double peak = 20 * Math.Log10(s.Max(v => Math.Abs((int)v)) / 32767.0);
            Assert.True(peak <= -0.99, r.File + " peaks at " + peak + " dBFS");
            Assert.True(s.Take(10).All(v => Math.Abs((int)v) < 2000), r.File + " starts with a jump");
            Assert.True(s.TakeLast(10).All(v => Math.Abs((int)v) < 2000), r.File + " stops with a jump");
        }
    }

    [Fact]
    public void Each_recording_has_the_loudness_of_the_official_sound_for_its_event_unless_the_minus_1_dbfs_peak_holds_it_lower()
    {
        Assert.Equal(17, UiSoundRecordings.All.Count);
        var targets = UiSoundFit.ParseTargets(File.ReadAllText(RepoFile("tools/ui-sounds/sl-targets.json")));
        foreach (var r in UiSoundRecordings.All)
        {
            // the official viewer plays nothing when an object is taken; ours sits at the level of an object rezzing
            string setting = UiSoundCatalog.OfficialName(r.Slot == UiSoundSlot.ObjectRezOut ? UiSoundSlot.ObjectRezIn : r.Slot);
            double target = targets.Single(t => t.Setting == setting).Lufs;
            var (s, _) = Shipped(r.File);
            double peak = 20 * Math.Log10(s.Max(v => Math.Abs((int)v)) / 32767.0);
            double lufs = Loudness.Integrated(s.Select(v => v / 32767.0).ToArray(), 44100);
            Assert.True(Math.Abs(lufs - target) < 0.1 || (peak > -1.05 && lufs < target),
                $"{r.File}: {lufs:0.00} LUFS against {target:0.00}, peak {peak:0.00} dBFS");
        }
    }

    [Fact]
    public void The_notices_credit_each_recordings_pack_by_name_and_link_or_say_it_was_made_with_chatgpt()
    {
        Assert.Equal(17, UiSoundRecordings.All.Count);
        string notices = File.ReadAllText(RepoFile("THIRD-PARTY-NOTICES.md")).Replace("\r\n", "\n");
        int start = notices.IndexOf("\n## Interface sounds", StringComparison.Ordinal);
        Assert.True(start >= 0, "no interface sounds section");
        int end = notices.IndexOf("\n## ", start + 1, StringComparison.Ordinal);
        string section = notices[start..(end < 0 ? notices.Length : end)];
        foreach (var r in UiSoundRecordings.All)
        {
            string line = section.Split('\n').FirstOrDefault(l => l.Contains("`" + r.File + "`", StringComparison.Ordinal)) ?? "";
            Assert.True(line.Length > 0, r.File + " is not named in the notices");
            if (r.MadeWithChatGpt)
                Assert.Contains(UiSoundRecordings.ChatGptCredit, line);
            else
            {
                Assert.StartsWith("https://opengameart.org/content/", r.Link);
                Assert.Contains(r.Pack!, line);
                Assert.Contains(r.Author!, line);
                Assert.Contains(r.Link!, line);
                Assert.Contains("CC0 1.0", line);
            }
        }
        Assert.Contains("CC0 1.0 Universal", File.ReadAllText(RepoFile("ThirdPartyLicenses/LICENSE-CC0-1.0.txt")));
    }
}
