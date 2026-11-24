// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Music;
using Xunit;

namespace Viewer.Core.Tests.Music;

/// <summary>The stream player against a local test radio server: each format, a bare server address, a redirect, both playlist kinds, a
/// switch of address, and a clean stop. Each test waits for decoded audio, never for a fixed time.</summary>
public sealed class RadioPlayerTests
{
    private static async Task Until(Func<bool> done, int seconds = 30)
    {
        var stop = DateTime.UtcNow.AddSeconds(seconds);
        while (!done() && DateTime.UtcNow < stop)
            await Task.Delay(20);
        Assert.True(done(), "the condition was not reached in time");
    }

    /// <summary>Reads a second of what the player decoded and returns the pitch and loudness of 0.4 s of it, from the first audible sample on
    /// (an encoder starts a stream with a short silence). The pitch comes from the zero crossings of the left side.</summary>
    private static async Task<(double Hz, double Rms)> Listen(RadioPlayer player)
    {
        int rate = 0;
        await Until(() => (rate = player.Output.SampleRate) > 0 && player.Output.TotalFrames > 0);
        int want = rate, got = 0;
        var all = new float[want * 2];
        var one = new float[2048 * 2];
        var stop = DateTime.UtcNow.AddSeconds(30);
        while (got < want && DateTime.UtcNow < stop)
        {
            int n = player.Output.Read(one, Math.Min(2048, want - got));
            if (n == 0) { await Task.Delay(10); continue; }
            Array.Copy(one, 0, all, got * 2, n * 2);
            got += n;
        }
        Assert.Equal(want, got);
        int start = 0;
        while (start < want && Math.Abs(all[start * 2]) < 0.05f)
            start++;
        int length = rate * 4 / 10;
        Assert.True(start + length <= want, "the tone did not start in time");
        int crossings = 0;
        double sum = 0;
        for (int i = start + 1; i < start + length; i++)
        {
            if (all[(i - 1) * 2] < 0 != all[i * 2] < 0) crossings++;
            sum += all[i * 2] * all[i * 2];
        }
        return (crossings / 2.0 / (length / (double)rate), Math.Sqrt(sum / length));
    }

    private static void AssertTone((double Hz, double Rms) heard)
    {
        Assert.InRange(heard.Hz, 400, 480);   // a 440 Hz tone, decoded: not silence and not noise
        Assert.InRange(heard.Rms, 0.05, 1.0);
    }

    [Theory]
    [InlineData("/mp3", AudioFormat.Mp3)]
    [InlineData("/mp3s", AudioFormat.Mp3)]
    [InlineData("/aac", AudioFormat.Aac)]
    [InlineData("/ogg", AudioFormat.OggVorbis)]
    public async Task EachFormatIsDecodedToItsTone(string path, AudioFormat format)
    {
        using var server = new TestRadioServer();
        using var player = new RadioPlayer();
        player.Play(server.Url(path));
        AssertTone(await Listen(player));
        Assert.Equal(format, player.Format);
        Assert.Equal(RadioState.Playing, player.State);
    }

    [Fact]
    public async Task ABareServerAddressPlays()
    {
        using var server = new TestRadioServer();
        using var player = new RadioPlayer();
        player.Play(server.Url());   // http://host:port with no path, answered "ICY 200 OK"
        AssertTone(await Listen(player));
        Assert.Equal(AudioFormat.Mp3, player.Format);
    }

    [Fact]
    public async Task ARedirectIsFollowed()
    {
        using var server = new TestRadioServer();
        using var player = new RadioPlayer();
        player.Play(server.Url("/go"));
        AssertTone(await Listen(player));
        Assert.EndsWith("/mp3", player.Resolved!.AbsolutePath);
    }

    [Fact]
    public async Task APlsPlaylistIsResolvedToItsStream()
    {
        using var server = new TestRadioServer();
        using var player = new RadioPlayer();
        player.Play(server.Url("/list.pls"));
        AssertTone(await Listen(player));
        Assert.EndsWith("/mp3", player.Resolved!.AbsolutePath);
    }

    [Fact]
    public async Task AnM3uPlaylistIsResolvedToItsStream()
    {
        using var server = new TestRadioServer();
        using var player = new RadioPlayer();
        player.Play(server.Url("/list.m3u"));
        AssertTone(await Listen(player));
        Assert.EndsWith("/aac", player.Resolved!.AbsolutePath);
        Assert.Equal(AudioFormat.Aac, player.Format);
    }

    [Fact]
    public async Task ASwitchOfAddressMovesToTheNewStreamAtOnce()
    {
        using var server = new TestRadioServer();
        using var player = new RadioPlayer();
        player.Play(server.Url("/mp3"));
        AssertTone(await Listen(player));
        player.Play(server.Url("/aac"));
        await Until(() => player.Format == AudioFormat.Aac && player.State == RadioState.Playing);
        AssertTone(await Listen(player));
        Assert.Contains("/aac", server.Requests);
        await Until(() => server.Open == 1);   // the first connection was let go
    }

    [Fact]
    public async Task StopEndsTheConnectionAndTheState()
    {
        using var server = new TestRadioServer();
        using var player = new RadioPlayer();
        player.Play(server.Url("/mp3"));
        await Listen(player);
        player.Stop();
        Assert.Equal(RadioState.Idle, player.State);
        await Until(() => server.Open == 0);
    }

    [Fact]
    public async Task AnAddressThatIsNotAStreamFails()
    {
        using var server = new TestRadioServer();
        using var player = new RadioPlayer();
        player.Play(server.Url("/nothing"));
        await Until(() => player.State == RadioState.Failed);
        Assert.False(string.IsNullOrEmpty(player.Error));
    }

    [Theory]
    [InlineData("http://example.org/stream", true)]
    [InlineData("https://example.org:8443/live.mp3", true)]
    [InlineData("  http://example.org:8040  ", true)]
    [InlineData("HTTP://EXAMPLE.ORG/x", true)]
    [InlineData("file:///c:/music.mp3", false)]
    [InlineData("ftp://example.org/x.mp3", false)]
    [InlineData("rtsp://example.org/x", false)]
    [InlineData("example.org/stream", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyHttpAndHttpsAddressesAreAccepted(string? text, bool accepted)
    {
        Assert.Equal(accepted, RadioUrl.TryParse(text, out _));
    }

    [Fact]
    public void ASiteIsTheHostNameInLowerCase() =>
        Assert.Equal("uk7.internet-radio.com", RadioUrl.Site(new Uri("http://UK7.internet-radio.com:8040/x")));

    [Fact]
    public void PlaylistsAreReadForTheirStreams()
    {
        var b = new Uri("http://example.org/dir/list.pls");
        Assert.Equal(["http://a.example.org/s", "http://b.example.org/t"], PlaylistParser.Parse("[playlist]\nNumberOfEntries=2\nFile1=http://a.example.org/s\nTitle1=A\nFile2=http://b.example.org/t\n", b).Select(u => u.ToString()));
        Assert.Equal(["http://a.example.org/s", "http://example.org/dir/rel.mp3"], PlaylistParser.Parse("#EXTM3U\n#EXTINF:-1,A\nhttp://a.example.org/s\n\nrel.mp3\n", b).Select(u => u.ToString()));
        Assert.Empty(PlaylistParser.Parse("[playlist]\nFile1=file:///c:/x.mp3\nFile2=ftp://example.org/x\n", b));
    }

    [Fact]
    public void FormatsAreToldApartByTheirFirstBytes()
    {
        Assert.Equal(AudioFormat.OggVorbis, FormatSniffer.Detect("OggS\0\0"u8));
        Assert.Equal(AudioFormat.Mp3, FormatSniffer.Detect([0xFF, 0xFB, 0x90, 0x00]));
        Assert.Equal(AudioFormat.Mp3, FormatSniffer.Detect("ID3\x03\0\0"u8));
        Assert.Equal(AudioFormat.Aac, FormatSniffer.Detect([0xFF, 0xF1, 0x5C, 0x40]));
        Assert.Equal(AudioFormat.Unknown, FormatSniffer.Detect("<html>"u8));
    }
}
