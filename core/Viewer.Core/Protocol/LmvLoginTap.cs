// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using LibreMetaverse;
using LibreMetaverse.StructuredData;
using Viewer.Core.Diagnostics;
using Viewer.Core.Session;

namespace Viewer.Core.Protocol;

/// <summary>
/// Stands between the protocol library and the network for the login request only. The library does not send the
/// <c>extended_errors</c> field the official viewer asks for and keeps nothing of the login service's reply but a few fields, so
/// this handler adds that one field to the request, writes the request's fields to the log the way the official viewer does
/// (before the credentials, and never the token or the multifactor hash), and keeps the reply's fields for the caller: reason,
/// message, message id and arguments, error code, the multifactor hash, and any certificate it described. Every other request
/// passes through untouched.
/// </summary>
internal sealed class LmvLoginTap(HttpMessageHandler inner, CoreLog log) : DelegatingHandler(inner)
{
    // fields that identify the person or the device, or are secrets: their values never reach the log
    private static bool Withheld(string name) => name is "first" or "last" or "passwd" or "token" or "mfa_hash" or "mac" or "id0";

    private volatile LoginReply? _reply;

    /// <summary>The hashed device ids the last login request sent ("mac" and "id0"); empty before one.</summary>
    public (string Mac, string Id0) DeviceIds { get; private set; } = ("", "");

    /// <summary>Forgets the reply of an earlier attempt.</summary>
    public void Begin() => _reply = null;

    /// <summary>The last login reply seen since <see cref="Begin"/> (after any redirects), or null when none arrived.</summary>
    public LoginReply? Reply => _reply;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        bool isLogin = false;
        if (request.Method == HttpMethod.Post && request.Content is { } body)
        {
            try
            {
                var bytes = await body.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                if (OSDParser.Deserialize(bytes) is OSDMap map && map.ContainsKey("passwd") && map.ContainsKey("first") && map.ContainsKey("options"))
                {
                    isLogin = true;
                    map["extended_errors"] = OSD.FromBoolean(true);   // asks for message ids and arguments (lllogininstance.cpp, constructAuthParams)
                    AddOfficialFields(map);
                    // the hashed device ids as sent, kept for the statistics report's system block (never logged)
                    DeviceIds = (map.TryGetValue("mac", out var mac) ? mac.AsString() : "", map.TryGetValue("id0", out var id0) ? id0.AsString() : "");
                    LogFields(map);
                    var rewritten = new ByteArrayContent(OSDParser.SerializeLLSDXmlBytes(map));
                    foreach (var h in body.Headers.Where(h => !h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))) rewritten.Headers.TryAddWithoutValidation(h.Key, h.Value);
                    request.Content = rewritten;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                // a body that is not LLSD is not a login request: it goes out as it was
            }
        }
        var response = await base.SendAsync(request, ct).ConfigureAwait(false);
        if (isLogin)
        {
            try
            {
                var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                var replacement = new ByteArrayContent(bytes);
                foreach (var h in response.Content.Headers.Where(h => !h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))) replacement.Headers.TryAddWithoutValidation(h.Key, h.Value);
                response.Content = replacement;
                if (OSDParser.Deserialize(bytes) is OSDMap reply)
                    _reply = ReplyOf(reply);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                // a reply that is not LLSD is left to the library to report
            }
        }
        return response;
    }

    /// <summary>The fields of the official viewer's request that the library does not send (lllogininstance.cpp, constructAuthParams):
    /// the address size, the operating system's own description, an empty host id, and how the last run ended. This viewer keeps no
    /// record of its last run's end, so it says what the official viewer says after a normal exit: event 0, no duration, no session.
    /// A field the library already sent is left as it is.</summary>
    internal static void AddOfficialFields(OSDMap map)
    {
        void Add(string name, OSD value)
        {
            if (!map.ContainsKey(name))
                map[name] = value;
        }
        Add("address_size", OSD.FromInteger(IntPtr.Size * 8));
        Add("platform_string", OSD.FromString(System.Runtime.InteropServices.RuntimeInformation.OSDescription));
        Add("host_id", OSD.FromString(""));
        Add("last_exec_event", OSD.FromInteger(0));
        Add("last_exec_duration", OSD.FromInteger(0));
        Add("last_exec_session_id", OSD.FromString(Guid.Empty.ToString()));
    }

    private void LogFields(OSDMap map)
    {
        var fields = new List<LogField>();
        foreach (var (name, value) in map.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            string text = Withheld(name) ? "(withheld)"
                : value is OSDArray array ? string.Join(",", array.Select(a => a.AsString()))
                : value.AsString();
            fields.Add(CoreLog.F(name, text));
        }
        log.Info("login.request", fields.ToArray());
    }

    internal static LoginReply ReplyOf(OSDMap map)
    {
        string Text(string key) => map.TryGetValue(key, out var v) ? v.AsString() : "";
        var args = new Dictionary<string, string>();
        if (map.TryGetValue("message_args", out var a) && a is OSDMap argMap)
            foreach (var k in argMap.Keys) args[k] = argMap[k].AsString();
        string error = Text("error_code") is { Length: > 0 } e ? e : Text("errorcode");
        string hash = Text("mfa_hash");
        // login-flags is a list whose first entry holds the flags; a non-empty ever_logged_in of "N" is a first login (llstartup.cpp)
        bool? firstLogin = null;
        if (map.TryGetValue("login-flags", out var flags) && flags is OSDArray { Count: > 0 } list && list[0] is OSDMap first
            && first.TryGetValue("ever_logged_in", out var ever) && ever.AsString() is { Length: > 0 } everText)
            firstLogin = everText == "N";
        return new LoginReply(Text("reason"), Text("message"), Text("message_id"), args, error,
            hash.Length > 0 ? new Secret(hash, SensitiveKind.Token) : null, map.TryGetValue("certificate", out var c) ? c.ToString() ?? "" : "")
        { FirstLogin = firstLogin };
    }
}
