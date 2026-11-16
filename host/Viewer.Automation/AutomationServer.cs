// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Viewer.Automation;

/// <summary>A parsed request, after the guard has let it through.</summary>
public sealed record AutomationCall(string Command, JsonElement Body);

/// <summary>A command's answer: an HTTP status and a JSON-serialisable body.</summary>
public sealed record AutomationReply(int Status, object Body)
{
    public static AutomationReply Ok(object body) => new(200, body);
    public static AutomationReply Error(int status, string error) => new(status, new { error });
}

/// <summary>
/// Automation API v1 (the test strategy 2.1: a new security-sensitive surface starts safe). DEVELOPMENT BUILDS
/// ONLY: this project's sources are compiled into the Godot host only when the configuration is not ExportRelease
/// (Viewer.Godot.csproj), so a shipping export contains no type and no enable path; the S-1 stage scans for it.
/// Safety, by construction:
///   - off unless the process was started with the explicit <c>--automation</c> user argument (<see cref="AutomationArgs"/>);
///   - binds 127.0.0.1 only, on a port the OS picks at random; the address is not configurable;
///   - a per-launch random bearer token (256 bits), delivered ONLY through an owner-only file (<see cref="TokenFile"/>);
///     it is never printed or logged, and no response carries it;
///   - <see cref="AutomationGuard"/>: POST only; Host must name the loopback listener (DNS rebinding); any Origin or
///     Sec-Fetch-* header is refused (a browser made the request); wrong or missing token refused; JSON bodies only;
///   - one request per connection, bounded header and body sizes, a read timeout.
/// Commands (v1): session, observe, screenshot, quit. The host answers them on its main thread.
/// </summary>
public sealed class AutomationServer : IDisposable
{
    public const int MaxHeaderBytes = 8 * 1024, MaxBodyBytes = 64 * 1024;
    public static readonly string[] Commands = ["session", "observe", "screenshot", "camera", "orbit", "layers", "neighbours", "terrain", "neighbour_debug", "texture_probe", "look_probe", "plain_ground", "mesh_probe", "moving_probe", "presented_audit", "avatar_probe", "frame_burst", "keys", "mouselook", "motion", "chat", "friends", "groups", "teleport", "map", "inventory", "nametags", "prefs", "fault", "mute", "reconnect", "login_dialog", "login_screen", "menus", "prefs_layout", "upload_notice", "texture_preview", "windows", "inventory_view", "interact", "build", "edit", "aim", "body_mask", "dialog", "open_window", "sky", "personal", "overrider", "notecard", "script", "addons", "expression", "sample", "engine", "input", "typing", "ui_sounds", "world_sounds", "sound_tuner", "topbar", "dock", "quit"];   // camera (yaw only), orbit (the follow camera), layers; neighbours (the regions and what is drawn for each); texture_probe; keys (the own avatar's movement keys); mouselook (mouse-look, as the mouse and keys do it); motion (the motion trace); inventory; nametags, fault (development builds: a circuit closer), mute, reconnect; interact (click, menu and sit actions on objects); dialog (script dialogs, permission requests and notifications); sky (the time-of-day choice); sample (memory and engine figures for a long run); chat, friends, groups, teleport, map

    private readonly TcpListener _listener;
    private readonly byte[] _token;
    private readonly Func<AutomationCall, Task<AutomationReply>> _handler;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _thread;

    /// <param name="handler">Answers an accepted call (the host marshals it to its main thread).</param>
    /// <param name="log">Event names only (never a header, a body or the token).</param>
    public AutomationServer(Func<AutomationCall, Task<AutomationReply>> handler, Action<string>? log = null)
    {
        _handler = handler;
        _log = log ?? (_ => { });
        _token = RandomNumberGenerator.GetBytes(32);
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Server.ExclusiveAddressUse = true;
        _listener.Start(8);
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _thread = new Thread(AcceptLoop) { IsBackground = true, Name = "automation" };
        _thread.Start();
        _log("automation.listening");
    }

    public int Port { get; }

    private volatile bool _off;

    /// <summary>Whether the API has been turned off for this run (<see cref="TurnOffFor"/>). Once off it stays off: nothing turns it on again.</summary>
    public bool IsOff => _off;

    /// <summary>Turns the API off for the rest of the run when a login heads for a grid where the per-grid switch does not allow it
    /// (<see cref="Viewer.Core.Session.ViewerFeature.Automation"/>); from then on every request is refused before it reaches the handler.
    /// Returns <see cref="IsOff"/>.</summary>
    public bool TurnOffFor(Viewer.Core.Session.GridKind grid)
    {
        if (!Viewer.Core.Session.GridPolicy.Allows(Viewer.Core.Session.ViewerFeature.Automation, grid) && !_off)
        {
            _off = true;
            _log("automation.off_for_grid");
        }
        return _off;
    }

    public IPAddress Address => ((IPEndPoint)_listener.LocalEndpoint).Address;

    /// <summary>The token as the token file carries it. Only <see cref="TokenFile"/> calls this.</summary>
    internal string TokenText => Convert.ToHexString(_token).ToLowerInvariant();

    /// <summary>Writes the owner-only token file into <paramref name="directory"/> and returns its path.</summary>
    public string WriteTokenFile(string directory) => TokenFile.Write(directory, Port, TokenText, Environment.ProcessId);

    private void AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = _listener.AcceptTcpClient(); }
            catch { break; }
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            client.ReceiveTimeout = client.SendTimeout = 5000;
            var stream = client.GetStream();
            AutomationReply reply;
            try
            {
                using var read = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                read.CancelAfter(TimeSpan.FromSeconds(5));
                var req = await RawRequest.ReadAsync(stream, read.Token).ConfigureAwait(false);
                var verdict = _off ? new GuardVerdict(403, "off-on-this-grid") : AutomationGuard.Check(req, Port, _token);
                if (verdict is not null)
                {
                    _log("automation.refused." + verdict.Reason);
                    reply = AutomationReply.Error(verdict.Status, verdict.Reason);
                }
                else
                {
                    string command = req.Path["/v1/".Length..];
                    using var doc = JsonDocument.Parse(req.Body.Length == 0 ? "{}"u8.ToArray() : req.Body);
                    _log("automation.call." + command);
                    var call = _handler(new AutomationCall(command, doc.RootElement.Clone()));
                    reply = await call.WaitAsync(TimeSpan.FromSeconds(60), _stop.Token).ConfigureAwait(false);
                }
            }
            catch (RequestTooLargeException) { reply = AutomationReply.Error(413, "too-large"); }
            catch (JsonException) { reply = AutomationReply.Error(400, "bad-json"); }
            catch (FormatException) { reply = AutomationReply.Error(400, "bad-request"); }
            catch (TimeoutException) { reply = AutomationReply.Error(504, "timeout"); }
            catch (OperationCanceledException) { reply = AutomationReply.Error(503, "stopping"); }
            catch (Exception) { reply = AutomationReply.Error(500, "internal"); }
            try { await WriteAsync(stream, reply).ConfigureAwait(false); } catch { }
        }
    }

    private static async Task WriteAsync(NetworkStream stream, AutomationReply reply)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(reply.Body);
        string reason = reply.Status switch { 200 => "OK", 400 => "Bad Request", 401 => "Unauthorized", 403 => "Forbidden", 404 => "Not Found", 405 => "Method Not Allowed", 413 => "Payload Too Large", 415 => "Unsupported Media Type", _ => "Error" };
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {reply.Status} {reason}\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head).ConfigureAwait(false);
        await stream.WriteAsync(body).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_stop.IsCancellationRequested)
            return;
        _stop.Cancel();
        try { _listener.Stop(); } catch { }
        _thread.Join(2000);
        CryptographicOperations.ZeroMemory(_token);
        _log("automation.stopped");
    }
}

/// <summary>The per-grid switch for the logins the automation starts itself (the session command's login and the login screen's
/// submit): it never starts one to a grid where automation is not allowed (Second Life, <see cref="Viewer.Core.Session.ViewerFeature.Automation"/>).</summary>
public static class AutomationLogin
{
    public static bool Refuses(string? loginUri) => false;   // RED STUB
}

/// <summary>The explicit enable flag. Nothing else turns the API on: no setting, no environment variable.</summary>
public static class AutomationArgs
{
    public const string Flag = "--automation";

    public static bool Enabled(IEnumerable<string> userArgs) => userArgs.Any(a => a == Flag);
}

public sealed record GuardVerdict(int Status, string Reason);

/// <summary>The checks every request passes before a command runs. Pure; the order is fixed and tested.</summary>
public static class AutomationGuard
{
    public static GuardVerdict? Check(RawRequest req, int port, byte[] token)
    {
        if (req.Method != "POST")
            return new(405, "method");
        if (req.Headers.TryGetValue("host", out var hosts) is false || hosts.Count != 1 ||
            !(hosts[0] == $"127.0.0.1:{port}" || hosts[0].Equals($"localhost:{port}", StringComparison.OrdinalIgnoreCase)))
            return new(403, "host");
        if (req.Headers.ContainsKey("origin") || req.Headers.Keys.Any(k => k.StartsWith("sec-fetch-", StringComparison.Ordinal)))
            return new(403, "origin");
        if (!req.Headers.TryGetValue("authorization", out var auth) || auth.Count != 1 || !TokenMatches(auth[0], token))
            return new(401, "token");
        if (!req.Headers.TryGetValue("content-type", out var ct) || ct.Count != 1 || !ct[0].StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            return new(415, "content-type");
        if (!req.Path.StartsWith("/v1/", StringComparison.Ordinal) || !AutomationServer.Commands.Contains(req.Path["/v1/".Length..]))
            return new(404, "command");
        return null;
    }

    private static bool TokenMatches(string header, byte[] token)
    {
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        var given = Encoding.ASCII.GetBytes(header[prefix.Length..].Trim());
        var expected = Encoding.ASCII.GetBytes(Convert.ToHexString(token).ToLowerInvariant());
        return CryptographicOperations.FixedTimeEquals(given, expected);
    }
}

public sealed class RequestTooLargeException : Exception;

/// <summary>A minimal HTTP/1.1 request: request line, headers (lower-cased names), a Content-Length body.</summary>
public sealed record RawRequest(string Method, string Path, IReadOnlyDictionary<string, List<string>> Headers, byte[] Body)
{
    public static async Task<RawRequest> ReadAsync(Stream stream, CancellationToken ct)
    {
        var buf = new byte[MaxHeader];
        int len = 0, end;
        while ((end = HeaderEnd(buf, len)) < 0)
        {
            if (len == buf.Length)
                throw new RequestTooLargeException();
            int n = await stream.ReadAsync(buf.AsMemory(len), ct).ConfigureAwait(false);
            if (n == 0)
                throw new FormatException("connection closed before the headers ended");
            len += n;
        }
        var lines = Encoding.ASCII.GetString(buf, 0, end).Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length != 3 || !first[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
            throw new FormatException("bad request line");
        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0)
                throw new FormatException("bad header");
            string name = line[..colon].Trim().ToLowerInvariant();
            if (!headers.TryGetValue(name, out var values))
                headers[name] = values = new List<string>();
            values.Add(line[(colon + 1)..].Trim());
        }
        int bodyLen = 0;
        if (headers.TryGetValue("content-length", out var cl))
        {
            if (cl.Count != 1 || !int.TryParse(cl[0], out bodyLen) || bodyLen < 0)
                throw new FormatException("bad content-length");
            if (bodyLen > AutomationServer.MaxBodyBytes)
                throw new RequestTooLargeException();
        }
        var body = new byte[bodyLen];
        int have = Math.Min(len - end - 4, bodyLen);
        Array.Copy(buf, end + 4, body, 0, have);
        while (have < bodyLen)
        {
            int n = await stream.ReadAsync(body.AsMemory(have), ct).ConfigureAwait(false);
            if (n == 0)
                throw new FormatException("connection closed inside the body");
            have += n;
        }
        return new(first[0], first[1], headers, body);
    }

    private const int MaxHeader = AutomationServer.MaxHeaderBytes;

    private static int HeaderEnd(byte[] b, int len)
    {
        for (int i = 3; i < len; i++)
            if (b[i - 3] == '\r' && b[i - 2] == '\n' && b[i - 1] == '\r' && b[i] == '\n')
                return i - 3;
        return -1;
    }
}
