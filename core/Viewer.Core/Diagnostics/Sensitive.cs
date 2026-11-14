// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Diagnostics;

/// <summary>The kinds of value ADR-019 says are never written to a log (hazard 1).</summary>
public enum SensitiveKind
{
    /// <summary>Local chat, IM, lure and script-dialog text.</summary>
    MessageBody,
    /// <summary>A capability URL: its path is a bearer secret.</summary>
    CapabilityUrl,
    /// <summary>A password, or its "$1$" MD5 digest as the login sends it.</summary>
    PasswordDigest,
    /// <summary>A session or secure-session id.</summary>
    SessionId,
    /// <summary>Any other token (asset, resume, seed).</summary>
    Token,
    /// <summary>An IP address (a simulator's LAN address in third-party text). Scrubbed from engine and library text.</summary>
    NetworkAddress,
}

/// <summary>
/// Redaction by construction: a sensitive value travels in one of these wrappers. <see cref="ToString"/> never shows
/// it, so string interpolation, a debugger display or an accidental log field cannot leak it; the raw value comes out
/// only through an explicit, greppable <c>Reveal</c> at the one place that must send it.
/// </summary>
public abstract class Sensitive
{
    private readonly string _value;

    protected Sensitive(string value) { _value = value ?? ""; }

    public abstract SensitiveKind Kind { get; }

    /// <summary>The raw value, for the protocol call that needs it. Never pass the result to a log.</summary>
    public string Reveal() => _value;

    public sealed override string ToString() => "[redacted:" + Kind + "]";

    public override bool Equals(object? obj) => obj is Sensitive s && s.Kind == Kind && s._value == _value;

    public override int GetHashCode() => HashCode.Combine(Kind, _value);
}

/// <summary>Which conversation a <see cref="MessageBody"/> came from (for routing; the text is never logged).</summary>
public enum MessageChannel { LocalChat, InstantMessage, ScriptDialog, Lure }

/// <summary>Chat, IM, dialog or lure text.</summary>
public sealed class MessageBody(string text, MessageChannel channel) : Sensitive(text)
{
    public MessageChannel Channel { get; } = channel;
    public override SensitiveKind Kind => SensitiveKind.MessageBody;
}

/// <summary>A capability URL handed out by a region or the login service.</summary>
public sealed class CapabilityUrl(string url) : Sensitive(url)
{
    public override SensitiveKind Kind => SensitiveKind.CapabilityUrl;
}

/// <summary>A password, digest, session id or token.</summary>
public sealed class Secret : Sensitive
{
    public Secret(string value, SensitiveKind kind) : base(value)
    {
        if (kind is SensitiveKind.MessageBody or SensitiveKind.CapabilityUrl)
            throw new ArgumentException("use MessageBody or CapabilityUrl for that kind", nameof(kind));
        Kind = kind;
    }

    public override SensitiveKind Kind { get; }
}
