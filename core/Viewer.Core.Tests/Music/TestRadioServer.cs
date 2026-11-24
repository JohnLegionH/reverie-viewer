// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Viewer.Core.Tests.Music;

/// <summary>
/// A small radio server on the loopback address for the stream tests, so no test needs the internet: audio at /mp3, /aac and /ogg,
/// the bare address (/) answering the way an old Shoutcast server does ("ICY 200 OK"), a redirect at /go, and a .pls and a .m3u playlist
/// that name the streams. MP3 and AAC loop for as long as the client reads; Ogg is sent once and then the connection stays open, as a live
/// station that has nothing more to say.
/// </summary>
internal sealed class TestRadioServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private int _open;

    public ConcurrentQueue<string> Requests { get; } = new();
    public int Open => Volatile.Read(ref _open);
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public Uri Url(string path = "") => new($"http://127.0.0.1:{Port}{path}");

    public static byte[] Data(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Music", "Data", name));

    public TestRadioServer()
    {
        _listener.Start();
        _ = Task.Run(AcceptLoop);
    }

    private async Task AcceptLoop()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _ = Task.Run(() => Serve(client));
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task Serve(TcpClient client)
    {
        Interlocked.Increment(ref _open);
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
            string line = head.ToString().Split("\r\n")[0];
            string path = line.Split(' ')[1];
            Requests.Enqueue(path);
            string pls = $"[playlist]\r\nNumberOfEntries=1\r\nFile1=http://127.0.0.1:{Port}/mp3\r\nTitle1=Test\r\nLength1=-1\r\nVersion=2\r\n";
            string m3u = $"#EXTM3U\r\n#EXTINF:-1,Test\r\nhttp://127.0.0.1:{Port}/aac\r\n";
            switch (path)
            {
                case "/":
                    await Send(stream, "ICY 200 OK\r\nicy-name: Test\r\nContent-Type: audio/mpeg\r\n\r\n", Data("tone.mp3"), loop: true);
                    break;
                case "/mp3":
                    await Send(stream, "HTTP/1.0 200 OK\r\nContent-Type: audio/mpeg\r\n\r\n", Data("tone.mp3"), loop: true);
                    break;
                case "/mp3s":
                    await Send(stream, "HTTP/1.0 200 OK\r\nContent-Type: audio/mpeg\r\n\r\n", Data("tone-stereo.mp3"), loop: true);
                    break;
                case "/aac":
                    await Send(stream, "HTTP/1.0 200 OK\r\nContent-Type: audio/aacp\r\n\r\n", Data("tone.aac"), loop: true);
                    break;
                case "/ogg":
                    await Send(stream, "HTTP/1.0 200 OK\r\nContent-Type: application/ogg\r\n\r\n", Data("tone.ogg"), loop: false);
                    break;
                case "/go":
                    await Text(stream, "302 Found", "Location: /mp3\r\n", "");
                    break;
                case "/list.pls":
                    await Text(stream, "200 OK", "Content-Type: audio/x-scpls\r\n", pls);
                    break;
                case "/list.m3u":
                    await Text(stream, "200 OK", "Content-Type: audio/x-mpegurl\r\n", m3u);
                    break;
                default:
                    await Text(stream, "404 Not Found", "", "");
                    break;
            }
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        finally { Interlocked.Decrement(ref _open); }
    }

    private static async Task Text(NetworkStream stream, string status, string headers, string body)
    {
        var bytes = Encoding.ASCII.GetBytes($"HTTP/1.0 {status}\r\n{headers}Content-Length: {body.Length}\r\n\r\n{body}");
        await stream.WriteAsync(bytes);
    }

    private async Task Send(NetworkStream stream, string head, byte[] data, bool loop)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        do
        {
            for (int at = 0; at < data.Length; at += 1024)
                await stream.WriteAsync(data.AsMemory(at, Math.Min(1024, data.Length - at)), _stop.Token);
        }
        while (loop);
        // a live station with nothing more to say: the connection stays open until the client leaves
        var one = new byte[1];
        while (await stream.ReadAsync(one, _stop.Token) > 0) { }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }
}
