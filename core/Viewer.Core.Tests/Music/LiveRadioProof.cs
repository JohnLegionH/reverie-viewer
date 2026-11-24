// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Music;
using Xunit;

namespace Viewer.Core.Tests.Music;

/// <summary>
/// A check against real internet radio, switched off unless <c>NV_LIVE_RADIO_URL</c> names a station (and <c>NV_LIVE_RADIO_URL2</c> another one for
/// the switch). Every other test in this folder runs against a local test server. Each run plays the station, waits for decoded audio and
/// checks that it is sound (loud enough, not one constant value), then the player moves to the second station without being stopped.
/// </summary>
public sealed class LiveRadioProof
{
    private static async Task<(int Frames, double Rms, int Distinct)> Hear(RadioPlayer player, int seconds)
    {
        var stop = DateTime.UtcNow.AddSeconds(40);
        while (player.Output.SampleRate == 0 && DateTime.UtcNow < stop)
            await Task.Delay(50);
        Assert.True(player.Output.SampleRate > 0, $"no audio decoded; state {player.State}, error {player.Error}");
        int want = player.Output.SampleRate * seconds, got = 0;
        var buffer = new float[2048 * 2];
        double sum = 0;
        var seen = new HashSet<int>();
        while (got < want && DateTime.UtcNow < stop)
        {
            int n = player.Output.Read(buffer, Math.Min(2048, want - got));
            if (n == 0) { await Task.Delay(10); continue; }
            for (int i = 0; i < n; i++)
            {
                sum += buffer[i * 2] * buffer[i * 2];
                seen.Add((int)(buffer[i * 2] * 4096));
            }
            got += n;
        }
        return (got, Math.Sqrt(sum / Math.Max(1, got)), seen.Count);
    }

    [Fact]
    public async Task TheStationPlaysTenTimesAndAChangeOfAddressSwitchesWithoutStopping()
    {
        string? first = Environment.GetEnvironmentVariable("NV_LIVE_RADIO_URL"), second = Environment.GetEnvironmentVariable("NV_LIVE_RADIO_URL2");
        if (string.IsNullOrEmpty(first))
            return;   // not asked for: the local tests cover the player
        var log = new List<string>();
        for (int run = 1; run <= 10; run++)
        {
            using var player = new RadioPlayer();
            player.Play(new Uri(first));
            var heard = await Hear(player, 2);
            log.Add($"run {run}: {heard.Frames} frames, rms {heard.Rms:0.000}, {heard.Distinct} distinct values, {player.Format}, rate {player.Output.SampleRate}, from {player.Resolved}");
            Assert.True(heard.Frames >= player.Output.SampleRate * 2, "not two seconds of audio: " + string.Join(" | ", log));
            Assert.InRange(heard.Rms, 0.005, 1.0);
            Assert.True(heard.Distinct > 50, "the decoded audio is flat: " + string.Join(" | ", log));
            if (!string.IsNullOrEmpty(second))
            {
                player.Play(new Uri(second));   // the parcel reported a new address: no Stop in between
                var next = await Hear(player, 2);
                log.Add($"run {run} switch: {next.Frames} frames, rms {next.Rms:0.000}, {next.Distinct} distinct values, {player.Format}, from {player.Resolved}");
                Assert.True(next.Frames >= player.Output.SampleRate * 2, "the switched stream gave no audio: " + string.Join(" | ", log));
                Assert.InRange(next.Rms, 0.005, 1.0);
            }
        }
        File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "live-radio-proof.txt"), log);
    }
}
