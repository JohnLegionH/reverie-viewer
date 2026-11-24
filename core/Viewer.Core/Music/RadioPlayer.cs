// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text;

namespace Viewer.Core.Music;

/// <summary>Where a <see cref="RadioPlayer"/> is.</summary>
public enum RadioState { Idle, Connecting, Playing, Failed }

/// <summary>
/// Plays one internet radio stream at a time into a <see cref="PcmQueue"/>. <see cref="Play"/> takes an http or https address: a station's own
/// address (even a bare server address), a redirect, or a .pls or .m3u playlist that names the stream. The stream is read and decoded on a
/// background thread of its own, never the caller's; <see cref="Play"/> with another address moves to it at once, and <see cref="Stop"/> closes the
/// connection and ends the thread. A stream that drops is reconnected a few times before the player gives up (<see cref="RadioState.Failed"/>).
/// Reading starts only when the address is passed to <see cref="Play"/>; nothing is opened by merely having the class.
/// </summary>
public sealed class RadioPlayer : IDisposable
{
    private const int MaxPlaylistDepth = 3;
    private const int MaxFailures = 4;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private int _generation;
    private RadioState _state;
    private Uri? _current, _resolved;
    private AudioFormat _format;
    private string? _error;

    /// <summary>The decoded sound: stereo frames at the stream's sample rate.</summary>
    public PcmQueue Output { get; } = new();

    public RadioState State { get { lock (_gate) return _state; } }

    /// <summary>The address last passed to <see cref="Play"/>, until <see cref="Stop"/>.</summary>
    public Uri? Current { get { lock (_gate) return _current; } }

    /// <summary>The stream's own address once playlists and redirects were followed.</summary>
    public Uri? Resolved { get { lock (_gate) return _resolved; } }

    public AudioFormat Format { get { lock (_gate) return _format; } }

    /// <summary>Why the player is in the failed state.</summary>
    public string? Error { get { lock (_gate) return _error; } }

    /// <summary>Starts the stream at <paramref name="url"/>, ending the one that plays.</summary>
    public void Play(Uri url)
    {
        if (!RadioUrl.TryParse(url.ToString(), out url))
            throw new ArgumentException("only http and https addresses are played", nameof(url));
        CancellationTokenSource cts;
        int generation;
        lock (_gate)
        {
            _cts?.Cancel();
            _cts = cts = new CancellationTokenSource();
            generation = ++_generation;
            _state = RadioState.Connecting;
            _current = url;
            _resolved = null;
            _format = AudioFormat.Unknown;
            _error = null;
        }
        Output.Clear();
        _ = Task.Factory.StartNew(() => Run(url, cts.Token, generation), cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    /// <summary>Ends the stream and closes its connection.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _cts?.Cancel();
            _cts = null;
            _generation++;
            _state = RadioState.Idle;
            _current = _resolved = null;
            _format = AudioFormat.Unknown;
            _error = null;
        }
        Output.Clear();
    }

    public void Dispose() => Stop();

    private void Set(int generation, Action change)
    {
        lock (_gate)
            if (generation == _generation)
                change();
    }

    private async Task Run(Uri start, CancellationToken ct, int generation)
    {
        int failures = 0;
        string why = "";
        while (!ct.IsCancellationRequested)
        {
            long before = Output.TotalFrames;
            try
            {
                await ReadStream(start, ct, generation);
                why = "the stream ended";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                if (ct.IsCancellationRequested)
                    return;
                why = e is StreamRefused ? e.Message : e.Message.Length > 0 ? e.Message : e.GetType().Name;
                if (e is StreamRefused)
                {
                    Set(generation, () => { _state = RadioState.Failed; _error = why; });
                    return;
                }
            }
            if (ct.IsCancellationRequested)
                return;
            failures = Output.TotalFrames > before ? 1 : failures + 1;   // a stream that played for a while and dropped starts the count again
            if (failures > MaxFailures)
            {
                Set(generation, () => { _state = RadioState.Failed; _error = why; });
                return;
            }
            Set(generation, () => _state = RadioState.Connecting);
            try { await Task.Delay(RetryDelay, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>The server answered, but not with a stream this player can read: no point in trying again.</summary>
    private sealed class StreamRefused(string message) : Exception(message);

    private async Task ReadStream(Uri start, CancellationToken ct, int generation)
    {
        var target = start;
        bool triedSemicolon = false;
        for (int depth = 0; ; )
        {
            using var response = await RadioHttp.GetAsync(target, ct);
            using var closer = ct.Register(response.Dispose);   // a stop closes the connection, which ends a read that is waiting
            if (response.Status != 200)
                throw new StreamRefused($"the server answered {response.Status}");
            var head = await ReadHead(response.Body, ct);
            string type = response.Header("content-type").ToLowerInvariant();
            var format = FormatSniffer.Detect(head);
            if (format == AudioFormat.Unknown)
            {
                string text = Encoding.UTF8.GetString(head);
                bool playlist = text.TrimStart().StartsWith("[playlist]", StringComparison.OrdinalIgnoreCase) || text.TrimStart().StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase)
                    || type.Contains("mpegurl") || type.Contains("scpls") || type.Contains("x-pls")
                    || response.Uri.AbsolutePath.EndsWith(".pls", StringComparison.OrdinalIgnoreCase) || response.Uri.AbsolutePath.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase);
                if (playlist)
                {
                    if (text.Contains("#EXT-X-", StringComparison.Ordinal))
                        throw new StreamRefused("this is an HLS stream, which the player does not read");
                    if (++depth > MaxPlaylistDepth)
                        throw new StreamRefused("the playlists lead to more playlists");
                    var rest = await ReadRest(response.Body, head, ct);
                    var entries = PlaylistParser.Parse(Encoding.UTF8.GetString(rest), response.Uri);
                    if (entries.Count == 0)
                        throw new StreamRefused("the playlist names no http or https stream");
                    target = entries[0];
                    continue;
                }
                // a bare Shoutcast address answers a page to a browser; the stream is at /;
                if (type.Contains("text/html") && !triedSemicolon && (response.Uri.AbsolutePath is "" or "/") && response.Uri.Query.Length == 0)
                {
                    triedSemicolon = true;
                    target = new UriBuilder(response.Uri) { Path = "/;" }.Uri;
                    continue;
                }
                throw new StreamRefused("the address is not an audio stream");
            }
            Set(generation, () => { _resolved = response.Uri; _format = format; });
            using var input = new PrefixedStream(head, response.Body);
            using var reader = AudioReaders.Open(format, input);
            await Task.Run(() => Decode(reader, ct, generation), ct);
            return;
        }
    }

    private void Decode(IPcmReader reader, CancellationToken ct, int generation)
    {
        int channels = Math.Max(1, reader.Channels);
        var samples = new float[2048 * channels];
        var stereo = new float[2048 * 2];
        int rate = reader.SampleRate;
        while (!ct.IsCancellationRequested)
        {
            int n = reader.Read(samples, samples.Length);
            if (n <= 0)
                return;
            int frames = n / channels;
            for (int i = 0; i < frames; i++)
            {
                stereo[i * 2] = samples[i * channels];
                stereo[i * 2 + 1] = channels > 1 ? samples[i * channels + 1] : samples[i * channels];
            }
            if (!Output.Write(stereo, frames, rate, ct))
                return;
            Set(generation, () => _state = RadioState.Playing);
        }
    }

    /// <summary>The first bytes of the body: enough to tell the format (or to see that it is a page or a playlist).</summary>
    private static async Task<byte[]> ReadHead(Stream body, CancellationToken ct)
    {
        var buffer = new byte[4096];
        int have = 0;
        while (have < 2048)
        {
            int n = await body.ReadAsync(buffer.AsMemory(have, buffer.Length - have), ct);
            if (n == 0)
                break;
            have += n;
            // a playlist or page is short and the server closes after it: do not wait for more than has come
            if (have >= 64 && FormatSniffer.Detect(buffer.AsSpan(0, have)) != AudioFormat.Unknown)
                break;
        }
        if (have == 0)
            throw new IOException("the stream sent nothing");
        return buffer.AsSpan(0, have).ToArray();
    }

    private static async Task<byte[]> ReadRest(Stream body, byte[] head, CancellationToken ct)
    {
        const int Limit = 64 * 1024;
        using var all = new MemoryStream();
        all.Write(head);
        var buffer = new byte[4096];
        while (all.Length < Limit)
        {
            int n = await body.ReadAsync(buffer, ct);
            if (n == 0)
                break;
            all.Write(buffer, 0, n);
        }
        return all.ToArray();
    }
}
