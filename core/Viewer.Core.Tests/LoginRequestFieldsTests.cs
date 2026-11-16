// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Diagnostics;
using Viewer.Core.Identity;
using Viewer.Core.Net;
using Viewer.Core.Protocol;
using Viewer.Core.Session;
using Xunit;
using Xunit.Abstractions;

namespace Viewer.Core.Tests;

/// <summary>
/// What the login request carries and what is read back from the login service's reply, against a loopback stub. The first
/// request says the terms and the critical message are not yet agreed, sends an empty token and multifactor hash, and asks for
/// extended errors; a retry carries what the person's answers set; the reply's reason, message id and arguments, error code and
/// multifactor hash reach the caller; and no secret reaches the log.
/// </summary>
public class LoginRequestFieldsTests(ITestOutputHelper output)
{
    private static TransportLoginRequest Request(string url, LoginExtras? extras = null) =>
        new(url, "Test", "Resident", new Secret("pw", SensitiveKind.PasswordDigest), "last", ViewerIdentity.Current, Extras: extras);

    private static object Get(IReadOnlyDictionary<string, object> sent, string name)
    {
        Assert.True(sent.ContainsKey(name), $"the request carries no '{name}' field");
        return sent[name];
    }

    private async Task<(TransportLoginResult Result, LoginStubServer Server, ListSink Log)> LoginOnce(Func<int, string> reply, LoginExtras? extras = null, Action<LoginStubServer>? prepare = null)
    {
        LoginStubServer? made = null;
        var server = new LoginStubServer(StubCertificate.None, (i, _) => reply(i));
        made = server;
        prepare?.Invoke(server);
        var sink = new ListSink();
        using var transport = ProtocolAdapters.CreateLibreMetaverse(new OutboundPolicy(new OutboundOptions()), new CoreLog(sink, "test"));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var result = await transport.LoginAsync(Request(server.Url, extras), cts.Token);
        output.WriteLine($"login result: {result.FailureCode}: {result.FailureMessage}");
        return (result, server, sink);
    }

    [Fact]
    public async Task The_first_request_says_nothing_is_agreed_sends_an_empty_token_and_hash_and_asks_for_extended_errors()
    {
        var (_, server, _) = await LoginOnce(_ => LoginStubServer.Failure("key", "No."));
        using var _ = server;

        var sent = Assert.Single(server.Requests);
        Assert.Equal("false", Get(sent, "agree_to_tos"));
        Assert.Equal("false", Get(sent, "read_critical"));
        Assert.Equal("", Get(sent, "token"));
        Assert.Equal("", Get(sent, "mfa_hash"));
        Assert.Equal("true", Get(sent, "extended_errors"));
    }

    [Fact]
    public async Task The_request_fields_are_the_official_set_less_the_ones_the_library_does_not_send()
    {
        var (_, server, _) = await LoginOnce(_ => LoginStubServer.Failure("key", "No."));
        using var _ = server;
        var sent = Assert.Single(server.Requests);

        // the fields the official viewer's login request carries (lllogininstance.cpp, constructAuthParams, and the credentials it adds)
        string[] official =
        [
            "start", "agree_to_tos", "read_critical", "last_exec_event", "last_exec_duration", "last_exec_session_id", "mac", "version", "channel",
            "platform", "address_size", "platform_version", "platform_string", "id0", "host_id", "extended_errors", "token", "first", "last", "passwd", "mfa_hash",
        ];
        foreach (var name in official)
            output.WriteLine($"{name,-22} {(sent.ContainsKey(name) ? "sent" : "NOT sent")}");
        output.WriteLine("options: " + string.Join(", ", (List<string>)sent["options"]));

        // every field of the official request is sent: Second Life's login service reads them all
        Assert.Empty(official.Where(n => !sent.ContainsKey(n)).Order().ToArray());
        Assert.Equal("64", sent["address_size"]);
        Assert.Equal("", sent["host_id"]);
        Assert.Equal("0", sent["last_exec_event"]);
        Assert.Equal("0", sent["last_exec_duration"]);
        Assert.Equal(Guid.Empty.ToString(), sent["last_exec_session_id"]);
        Assert.False(string.IsNullOrWhiteSpace((string)sent["platform_string"]));
        Assert.Equal("last", sent["start"]);
        Assert.Contains("inventory-root", (List<string>)sent["options"]);
    }

    [Fact]
    public async Task The_device_ids_are_hashes_of_the_real_device_never_blank_or_made_up()
    {
        // Third-Party Viewer Policy 2.c: no masking of the MAC address. The official viewer sends an MD5 of the network card's
        // address as "mac" and of the disk serial number as "id0" (lllogininstance.cpp, llHashedUniqueID and mSerialNumber).
        var (_, server, _) = await LoginOnce(_ => LoginStubServer.Failure("key", "No."));
        using var _ = server;
        var sent = Assert.Single(server.Requests);
        foreach (var name in new[] { "mac", "id0" })
        {
            string v = (string)sent[name];
            output.WriteLine($"{name}: {v.Length} characters, all hex: {v.All(Uri.IsHexDigit)}");
            Assert.Matches("^[0-9a-fA-F]{32}$", v);
            Assert.NotEqual(new string('0', 32), v);
        }
        // the hash is of one of this machine's own network cards (LibreMetaverse sends the same hash as id0)
        var real = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Select(n => n.GetPhysicalAddress().GetAddressBytes()).Where(a => a.Length > 0)
            .SelectMany(a => new[] { Convert.ToHexString(a), string.Join(":", a.Select(b => b.ToString("X2"))), string.Join("-", a.Select(b => b.ToString("X2"))) }
                .SelectMany(t => new[] { t, t.ToLowerInvariant() }))
            .SelectMany(t => new[] { Convert.ToHexString(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.ASCII.GetBytes(t))) })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        output.WriteLine($"network cards hashed: {real.Count}; the sent mac is one of them: {real.Contains((string)sent["mac"])}");
        Assert.Contains((string)sent["mac"], real);
    }

    [Fact]
    public async Task A_retry_carries_the_answers_the_dialogs_set()
    {
        var extras = new LoginExtras(AgreeToTos: true, ReadCritical: true, Token: new Secret("123456", SensitiveKind.Token), MfaHash: new Secret("hash-from-grid", SensitiveKind.Token));
        var (_, server, _) = await LoginOnce(_ => LoginStubServer.Failure("key", "No."), extras);
        using var _ = server;

        var sent = Assert.Single(server.Requests);
        Assert.Equal("true", Get(sent, "agree_to_tos"));
        Assert.Equal("true", Get(sent, "read_critical"));
        Assert.Equal("123456", Get(sent, "token"));
        Assert.Equal("hash-from-grid", Get(sent, "mfa_hash"));
        Assert.Equal("true", Get(sent, "extended_errors"));
    }

    [Fact]
    public async Task The_reply_fields_reach_the_caller()
    {
        var (result, server, _) = await LoginOnce(_ => LoginStubServer.Failure("mfa_challenge", "Enter your code.",
            ("message_id", "LoginFailedAuthenticationMFARequired"), ("error_code", "E42"),
            ("message_args", new Dictionary<string, string> { ["VERSION"] = "9.9.9" }), ("mfa_hash", "hash-from-grid")));
        using var _ = server;

        Assert.False(result.Ok);
        var reply = result.Reply;
        Assert.NotNull(reply);
        Assert.Equal("mfa_challenge", reply.Reason);
        Assert.Equal("Enter your code.", reply.Message);
        Assert.Equal("LoginFailedAuthenticationMFARequired", reply.MessageId);
        Assert.Equal("E42", reply.ErrorCode);
        Assert.Equal("9.9.9", reply.MessageArgs["VERSION"]);
        Assert.Equal("hash-from-grid", reply.MfaHash?.Reveal());
    }

    [Fact]
    public async Task No_secret_reaches_the_log()
    {
        var extras = new LoginExtras(Token: new Secret("987654", SensitiveKind.Token), MfaHash: new Secret("hash-secret-value", SensitiveKind.Token));
        var (_, server, sink) = await LoginOnce(_ => LoginStubServer.Failure("key", "No.", ("mfa_hash", "hash-from-reply")), extras);
        using var _ = server;

        string log = sink.Text;
        output.WriteLine(log);
        Assert.Contains("login.request", log);   // the fields are logged, as the official viewer does before it adds the credentials
        Assert.Contains("extended_errors", log);
        Assert.DoesNotContain("987654", log);
        Assert.DoesNotContain("hash-secret-value", log);
        Assert.DoesNotContain("hash-from-reply", log);
        Assert.DoesNotContain("Resident", log);
    }

    /// <summary>Out of this order's scope but checked: a login redirect (the service answers "indeterminate" with the next address) is followed, and the
    /// retry carries the same fields.</summary>
    [Fact]
    public async Task A_login_redirect_is_followed_with_the_same_fields()
    {
        string? self = null;
        var (result, server, _) = await LoginOnce(i => i == 0 ? LoginStubServer.Redirect(self!, 0, "Moving you.") : LoginStubServer.Failure("key", "No."), prepare: s => self = s.Url);
        using var _ = server;

        Assert.Equal("key", result.FailureCode);
        Assert.Equal(2, server.Requests.Count);
        Assert.Equal("true", Get(server.Requests[1], "extended_errors"));
        Assert.Equal("false", Get(server.Requests[1], "agree_to_tos"));
    }
}
