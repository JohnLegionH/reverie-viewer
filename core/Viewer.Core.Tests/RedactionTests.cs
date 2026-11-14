// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// ADR-019 hazard 1: message bodies, capability URLs, password digests, session ids and tokens never reach a log.
/// Every case runs twice: with the product redactor (the secret must be absent) and with that one kind switched off
/// (the SAME probe must find it), which proves each test fails if its redaction is removed.
/// </summary>
public class RedactionTests
{
    private const string ChatText = "meet me at the old pier at nine";
    private const string ImText = "my new phone number is 555-0199";
    private const string CapUrl = "http://sim.example:9000/CAPS/3f2a9c1e-5b7d-4e8f-9a0b-1c2d3e4f5a6b/";
    private const string CapPath = "3f2a9c1e-5b7d-4e8f-9a0b-1c2d3e4f5a6b";
    private const string DigestText = "$1$5f4dcc3b5aa765d61d8327deb882cf99";
    private const string SessionIdText = "9e107d9d-372b-4b6d-b0c9-2a1c4f8e7d6c";
    private const string TokenText = "tok_Zm9vYmFyYmF6cXV4";

    private static string Logged(Redactor redactor, Action<CoreLog> write)
    {
        var sink = new ListSink();
        write(new CoreLog(sink, "test", redactor));
        return sink.Text;
    }

    public static TheoryData<string, SensitiveKind, string> Cases => new()
    {
        { "chat body", SensitiveKind.MessageBody, ChatText },
        { "IM body", SensitiveKind.MessageBody, ImText },
        { "capability URL", SensitiveKind.CapabilityUrl, CapPath },
        { "capability URL inside an exception message", SensitiveKind.CapabilityUrl, CapPath },
        { "password digest", SensitiveKind.PasswordDigest, "5f4dcc3b5aa765d61d8327deb882cf99" },
        { "password digest in free text", SensitiveKind.PasswordDigest, "5f4dcc3b5aa765d61d8327deb882cf99" },
        { "session id", SensitiveKind.SessionId, SessionIdText },
        { "token", SensitiveKind.Token, TokenText },
    };

    private static void Write(string name, CoreLog log)
    {
        switch (name)
        {
            case "chat body": log.Info("chat.in", CoreLog.F("body", new MessageBody(ChatText, MessageChannel.LocalChat))); break;
            case "IM body": log.Info("im.in", CoreLog.F("body", new MessageBody(ImText, MessageChannel.InstantMessage))); break;
            case "capability URL": log.Info("caps.seed", CoreLog.F("url", new CapabilityUrl(CapUrl))); break;
            case "capability URL inside an exception message": log.Error("caps.failed", new HttpRequestException($"GET {CapUrl} returned 502")); break;
            case "password digest": log.Info("login.params", CoreLog.F("digest", new Secret(DigestText, SensitiveKind.PasswordDigest))); break;
            case "password digest in free text": log.Warn("login.echo", CoreLog.F("detail", $"server echoed passwd={DigestText}")); break;
            case "session id": log.Info("login.ok", CoreLog.F("session", new Secret(SessionIdText, SensitiveKind.SessionId))); break;
            case "token": log.Info("asset.token", CoreLog.F("token", new Secret(TokenText, SensitiveKind.Token))); break;
            default: throw new ArgumentException(name);
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Is_never_written(string name, SensitiveKind kind, string secret)
    {
        _ = kind;
        var text = Logged(Redactor.Default, log => Write(name, log));
        Assert.DoesNotContain(secret, text);
        Assert.Contains("[redacted:", text);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Would_be_written_without_its_redaction(string name, SensitiveKind kind, string secret)
    {
        var text = Logged(Redactor.WithoutForTests(kind), log => Write(name, log));
        Assert.Contains(secret, text);
    }

    [Fact]
    public void A_sensitive_value_never_shows_through_ToString_or_interpolation()
    {
        var body = new MessageBody(ChatText, MessageChannel.LocalChat);
        Assert.Equal("[redacted:MessageBody]", body.ToString());
        Assert.DoesNotContain(ChatText, $"said {body}");
        Assert.Equal(ChatText, body.Reveal());
    }

    [Fact]
    public void Free_text_cannot_ride_in_on_the_event_or_field_name()
    {
        var text = Logged(Redactor.Default, log => log.Info(ChatText, new LogField(ImText, 1)));
        Assert.DoesNotContain(ChatText, text);
        Assert.DoesNotContain(ImText, text);
        Assert.Contains("invalid-event", text);
        Assert.Contains("invalid-field", text);
    }

    [Fact]
    public void A_URL_keeps_its_host_for_diagnosis_and_loses_its_path()
    {
        var text = Logged(Redactor.Default, log => log.Warn("net.error", CoreLog.F("detail", $"connect to {CapUrl} failed")));
        Assert.Contains("http://sim.example:9000/[redacted:path]", text);
    }

    /// <summary>End to end: a whole session login + teardown against the fake layer writes none of its secrets.</summary>
    [Fact]
    public async Task A_session_login_and_logout_log_no_password_session_id_or_capability_path()
    {
        var sink = new ListSink();
        var t = new FakeTransport
        {
            OnLogin = (_, _) => Task.FromResult(FakeTransport.Ok(Guid.NewGuid(), sessionId: SessionIdText)),
        };
        using (var s = new ViewerSession(new SessionOptions { LoginUri = "http://login.example:8002/secret-login-path?token=" + TokenText }, t, new CoreLog(sink, "test")))
        {
            await s.LoginAsync(new("Test", "Resident", new Secret("hunter2-password", SensitiveKind.PasswordDigest)), CancellationToken.None);
            t.DriveToInWorld(new SimRef(1, "Home"));
            await s.LogoutAsync(CancellationToken.None);
        }
        var text = sink.Text;
        Assert.NotEmpty(sink.Entries);
        foreach (var secret in new[] { "hunter2-password", SessionIdText, TokenText, "secret-login-path" })
            Assert.DoesNotContain(secret, text);
        Assert.Contains("http://(host)/[redacted:path]", text);   // the login host and port are left out by default
        Assert.DoesNotContain("login.example", text);
    }
}
