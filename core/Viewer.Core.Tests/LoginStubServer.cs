// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml.Linq;

namespace Viewer.Core.Tests;

/// <summary>Which certificate the stub presents. <see cref="None"/> serves plain HTTP.</summary>
public enum StubCertificate { None, SelfSigned, Expired, WrongHost }

/// <summary>
/// A login service for tests: a loopback server on an ephemeral port that answers each login request with the next scripted
/// LLSD reply, over plain HTTP or over TLS with a certificate the test makes in memory (self-signed, expired, or issued for
/// another host). It records every login request as its fields. Test code only; nothing is installed in any certificate store.
/// </summary>
internal sealed class LoginStubServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly X509Certificate2? _cert;
    private readonly Func<int, IReadOnlyDictionary<string, object>, string> _reply;
    private readonly List<Dictionary<string, object>> _requests = [];
    private int _handshakes;

    public string Host => "127.0.0.1";
    public int Port { get; }
    public bool Tls { get; }
    public string Url => $"{(Tls ? "https" : "http")}://{Host}:{Port}/login";
    public string Sha256 { get; } = "";
    public string Sha1 { get; } = "";
    public string Md5 { get; } = "";
    public int CompletedHandshakes => Volatile.Read(ref _handshakes);

    /// <summary>The grid-info document a GET of get_grid_info is answered with; null: such a GET is not found.</summary>
    public string? GridInfo { get; set; }

    /// <summary>The login requests received so far, as field name to value (a string, or a list of strings for "options").</summary>
    public IReadOnlyList<Dictionary<string, object>> Requests { get { lock (_requests) return _requests.ToList(); } }

    public LoginStubServer(StubCertificate certificate, Func<int, IReadOnlyDictionary<string, object>, string> reply)
    {
        _reply = reply;
        Tls = certificate != StubCertificate.None;
        if (Tls)
        {
            using var key = RSA.Create(2048);
            var req = new CertificateRequest("CN=test-grid.example.org", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder();
            if (certificate == StubCertificate.WrongHost) san.AddDnsName("other.example.org");
            else { san.AddIpAddress(IPAddress.Loopback); san.AddDnsName("test-grid.example.org"); }
            req.CertificateExtensions.Add(san.Build());
            var from = certificate == StubCertificate.Expired ? DateTimeOffset.UtcNow.AddDays(-60) : DateTimeOffset.UtcNow.AddDays(-1);
            var to = certificate == StubCertificate.Expired ? DateTimeOffset.UtcNow.AddDays(-30) : DateTimeOffset.UtcNow.AddDays(30);
            using var self = req.CreateSelfSigned(from, to);
            // SChannel cannot serve with an in-memory key, so the key goes through PKCS#12 into a temporary key that dies with the object
            _cert = X509CertificateLoader.LoadPkcs12(self.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.DefaultKeySet);
            Sha256 = Convert.ToHexString(SHA256.HashData(_cert.RawData));
            Sha1 = Convert.ToHexString(SHA1.HashData(_cert.RawData));
            Md5 = Convert.ToHexString(MD5.HashData(_cert.RawData));
        }
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(Accept);
    }

    private async Task Accept()
    {
        while (true)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(); } catch { return; }
            _ = Task.Run(() => Serve(client));
        }
    }

    private async Task Serve(TcpClient client)
    {
        using var _ = client;
        try
        {
            Stream stream = client.GetStream();
            if (_cert is not null)
            {
                var ssl = new SslStream(stream, false);
                await ssl.AuthenticateAsServerAsync(_cert);
                Interlocked.Increment(ref _handshakes);
                stream = ssl;
            }
            using var s = stream;
            var buf = new List<byte>();
            var one = new byte[4096];
            int headEnd;
            while ((headEnd = IndexOf(buf, "\r\n\r\n"u8)) < 0)
            {
                int n = await s.ReadAsync(one);
                if (n == 0) return;
                buf.AddRange(one.AsSpan(0, n).ToArray());
            }
            string head = Encoding.ASCII.GetString(buf.GetRange(0, headEnd).ToArray());
            int length = 0;
            foreach (var line in head.Split("\r\n"))
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line[15..].Trim());
            while (buf.Count < headEnd + 4 + length)
            {
                int n = await s.ReadAsync(one);
                if (n == 0) return;
                buf.AddRange(one.AsSpan(0, n).ToArray());
            }
            string response;
            if (head.StartsWith("GET", StringComparison.Ordinal) && GridInfo is { } info && head.Contains("/get_grid_info", StringComparison.Ordinal))
                response = $"HTTP/1.1 200 OK\r\nContent-Type: application/xml\r\nContent-Length: {Encoding.UTF8.GetByteCount(info)}\r\nConnection: close\r\n\r\n{info}";
            else if (!head.StartsWith("POST", StringComparison.Ordinal))
                response = "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
            else
            {
                var fields = ParseRequest(Encoding.UTF8.GetString(buf.GetRange(headEnd + 4, length).ToArray()));
                int index;
                lock (_requests) { index = _requests.Count; _requests.Add(fields); }
                string body = _reply(index, fields);
                response = $"HTTP/1.1 200 OK\r\nContent-Type: application/llsd+xml\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";
            }
            await s.WriteAsync(Encoding.UTF8.GetBytes(response));
            await s.FlushAsync();
        }
        catch (Exception e) when (e is IOException or System.Security.Authentication.AuthenticationException or ObjectDisposedException) { /* the client refused the certificate */ }
    }

    private static int IndexOf(List<byte> data, ReadOnlySpan<byte> pattern)
    {
        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(data);
        return span.IndexOf(pattern);
    }

    /// <summary>An LLSD login request as field name to value: strings as text, booleans as "true"/"false", arrays of strings as lists.</summary>
    public static Dictionary<string, object> ParseRequest(string xml)
    {
        var map = XDocument.Parse(xml).Root!.Element("map")!;
        var result = new Dictionary<string, object>();
        var items = map.Elements().ToList();
        for (int i = 0; i + 1 < items.Count; i += 2)
        {
            var value = items[i + 1];
            result[items[i].Value] = value.Name.LocalName switch
            {
                "array" => value.Elements().Select(e => e.Value).ToList(),
                "boolean" => value.Value is "1" or "true" ? "true" : "false",
                _ => value.Value,
            };
        }
        return result;
    }

    /// <summary>An LLSD login reply. Values: string, bool, int, a list of strings, or a map of strings.</summary>
    public static string Reply(bool login, params (string Key, object Value)[] fields)
    {
        var sb = new StringBuilder("<?xml version=\"1.0\"?><llsd><map><key>login</key><string>").Append(login ? "true" : "false").Append("</string>");
        foreach (var (key, value) in fields)
        {
            sb.Append("<key>").Append(System.Security.SecurityElement.Escape(key)).Append("</key>").Append(Value(value));
        }
        return sb.Append("</map></llsd>").ToString();

        static string Value(object v) => v switch
        {
            bool b => $"<boolean>{(b ? "true" : "false")}</boolean>",
            int n => $"<integer>{n}</integer>",
            IReadOnlyDictionary<string, string> m => "<map>" + string.Concat(m.Select(kv => $"<key>{System.Security.SecurityElement.Escape(kv.Key)}</key><string>{System.Security.SecurityElement.Escape(kv.Value)}</string>")) + "</map>",
            _ => $"<string>{System.Security.SecurityElement.Escape(v.ToString())}</string>",
        };
    }

    /// <summary>The login service's "try this address next" answer (login = indeterminate).</summary>
    public static string Redirect(string nextUrl, int seconds, string message) =>
        Reply(false, ("next_url", nextUrl), ("next_duration", seconds), ("message", message)).Replace("<key>login</key><string>false</string>", "<key>login</key><string>indeterminate</string>");

    /// <summary>A failed login with a reason key and a message.</summary>
    public static string Failure(string reason, string message, params (string Key, object Value)[] more) =>
        Reply(false, [("reason", reason), ("message", message), .. more]);

    public void Dispose()
    {
        _listener.Stop();
        _cert?.Dispose();
    }
}
