// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Viewer.Core.Music;
using Viewer.Shell.Sound;
using Xunit;

namespace Viewer.Core.Tests.Media;

/// <summary>
/// With the real stream player and a server that counts connections: nothing connects before the person allows the site, and a redirect or a
/// playlist entry that leads to another site connects nowhere until that site is allowed too. "localhost" and "127.0.0.1" are two sites.
/// </summary>
public sealed class NoRequestBeforeAllowTests
{
    /// <summary>Counts every connection; answers 302 to <see cref="RedirectTo"/>, a playlist naming <see cref="PlaylistEntry"/>, or 404.</summary>
    private sealed class CountingServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private int _connections;

        public CountingServer()
        {
            _listener.Start();
            _ = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                        Interlocked.Increment(ref _connections);
                        _ = Task.Run(() => Serve(client));
                    }
                }
                catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException) { }
            });
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public int Connections => Volatile.Read(ref _connections);
        public Uri? RedirectTo { get; set; }
        public Uri? PlaylistEntry { get; set; }

        private async Task Serve(TcpClient client)
        {
            try
            {
                using var _ = client;
                var stream = client.GetStream();
                var head = new StringBuilder();
                var one = new byte[1];
                while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    if (await stream.ReadAsync(one, _stop.Token) == 0)
                        return;
                    head.Append((char)one[0]);
                }
                string pls = $"[playlist]\r\nFile1={PlaylistEntry}\r\n";
                string answer = RedirectTo is { } to ? $"HTTP/1.0 302 Found\r\nLocation: {to}\r\nContent-Length: 0\r\n\r\n"
                    : PlaylistEntry is not null ? $"HTTP/1.0 200 OK\r\nContent-Type: audio/x-scpls\r\nContent-Length: {pls.Length}\r\n\r\n{pls}"
                    : "HTTP/1.0 404 Not Found\r\nContent-Length: 0\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(answer), _stop.Token);
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }

    private sealed class Playback(RadioPlayer radio, MediaSiteSettings sites) : IMusicPlayback
    {
        public void Play(Uri url) => radio.Play(url, sites.SoundSnapshot());
        public void Stop() => radio.Stop();
        public string? SiteNeeded => radio.SiteNeeded;
    }

    private sealed class Prefs(MediaSiteSettings sites) : IMusicPrefs
    {
        public bool On { get; set; } = true;
        public SiteAnswer AnswerFor(string site) => sites.IsBlocked(site) ? SiteAnswer.DontPlay : sites.Get(Viewer.Core.Media.MediaKind.Sound, site);
        public void SetAnswer(string site, SiteAnswer answer) => sites.Set(Viewer.Core.Media.MediaKind.Sound, site, answer);
    }

    private sealed class Asker : IMusicAsker
    {
        public ConcurrentQueue<(string Site, Action<bool> Answer)> Asked { get; } = new();
        public void Ask(string site, Uri url, Action<bool> answered) => Asked.Enqueue((site, answered));
    }

    private sealed class Rig : IDisposable
    {
        public readonly RadioPlayer Radio = new();
        public readonly Asker Asker = new();
        public readonly MediaSiteSettings Sites;
        public readonly ParcelMusicController Controller;

        public Rig()
        {
            var settings = new ConcurrentDictionary<string, string>();
            Sites = new MediaSiteSettings(k => settings.GetValueOrDefault(k), (k, v) => settings[k] = v);
            Controller = new ParcelMusicController(new Playback(Radio, Sites), new Prefs(Sites), Asker);
        }

        /// <summary>Drives the controller as the viewer does every frame, until the condition holds or the cap passes.</summary>
        public async Task<bool> Pump(string address, Func<bool> done, int seconds = 20)
        {
            var end = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < end)
            {
                Controller.ParcelAddress(address);
                if (done())
                    return true;
                await Task.Delay(20);
            }
            return false;
        }

        public void Dispose() => Radio.Dispose();
    }

    private sealed class AnswersAtOnce : IMusicAsker
    {
        public void Ask(string site, Uri url, Action<bool> answered) => answered(true);
    }

    private sealed class Recorder : IMusicPlayback
    {
        public List<string> Log { get; } = [];
        public void Play(Uri url) => Log.Add("play");
        public void Stop() => Log.Add("stop");
    }

    [Fact]
    public void An_answer_given_at_once_leaves_the_allowed_music_on()
    {
        var settings = new Dictionary<string, string>();
        var sites = new MediaSiteSettings(k => settings.GetValueOrDefault(k), (k, v) => settings[k] = v);
        var played = new Recorder();
        var controller = new ParcelMusicController(played, new Prefs(sites), new AnswersAtOnce());
        controller.ParcelAddress("http://radio.example.org/stream");
        Assert.True(controller.IsPlaying);
        Assert.Equal(["play"], played.Log);
    }

    [Fact]
    public async Task Nothing_connects_while_the_question_is_open_or_after_a_refusal()
    {
        using var server = new CountingServer();
        using var rig = new Rig();
        string address = $"http://127.0.0.1:{server.Port}/stream";
        rig.Controller.ParcelAddress(address);
        // proving that something does not happen: a real wait
        await rig.Pump(address, () => server.Connections > 0, seconds: 2);
        Assert.Equal(0, server.Connections);
        Assert.True(rig.Asker.Asked.TryDequeue(out var question));
        question.Answer(false);
        await rig.Pump(address, () => server.Connections > 0, seconds: 2);
        Assert.Equal(0, server.Connections);
        // the control: after Play the same server is reached
        rig.Controller.Toggle();
        Assert.True(rig.Asker.Asked.TryDequeue(out question));
        question.Answer(true);
        Assert.True(await rig.Pump(address, () => server.Connections > 0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Another_site_the_stream_leads_to_is_asked_about_before_it_is_connected_to(bool playlist)
    {
        using var first = new CountingServer();
        using var second = new CountingServer();
        var target = new Uri($"http://localhost:{second.Port}/stream");
        if (playlist) first.PlaylistEntry = target; else first.RedirectTo = target;
        using var rig = new Rig();
        rig.Sites.Set(Viewer.Core.Media.MediaKind.Sound, "127.0.0.1", SiteAnswer.Play);
        string address = $"http://127.0.0.1:{first.Port}/go";

        Assert.True(await rig.Pump(address, () => !rig.Asker.Asked.IsEmpty, seconds: 10), "the other site was not asked about");
        Assert.True(rig.Asker.Asked.TryDequeue(out var question));
        Assert.Equal("localhost", question.Site);
        await rig.Pump(address, () => second.Connections > 0, seconds: 2);
        Assert.Equal(0, second.Connections);
        Assert.False(rig.Controller.IsPlaying);

        question.Answer(true);
        Assert.True(await rig.Pump(address, () => second.Connections > 0), "the allowed site was not reached");
    }

    [Fact]
    public async Task A_refused_other_site_is_never_connected_to_and_the_stream_stops()
    {
        using var first = new CountingServer();
        using var second = new CountingServer();
        first.RedirectTo = new Uri($"http://localhost:{second.Port}/stream");
        using var rig = new Rig();
        rig.Sites.Set(Viewer.Core.Media.MediaKind.Sound, "127.0.0.1", SiteAnswer.Play);
        string address = $"http://127.0.0.1:{first.Port}/go";
        Assert.True(await rig.Pump(address, () => !rig.Asker.Asked.IsEmpty, seconds: 10), "the other site was not asked about");
        Assert.True(rig.Asker.Asked.TryDequeue(out var question));
        question.Answer(false);
        int reached = first.Connections;
        await rig.Pump(address, () => second.Connections > 0, seconds: 2);
        Assert.Equal(0, second.Connections);
        Assert.Equal(reached, first.Connections);   // no retry loop against the first site either
        Assert.False(rig.Controller.IsPlaying);
        Assert.Empty(rig.Asker.Asked);
    }
}
