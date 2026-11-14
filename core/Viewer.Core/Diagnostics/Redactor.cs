// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text.RegularExpressions;

namespace Viewer.Core.Diagnostics;

/// <summary>
/// Turns a log field's value into the text that is written (ADR-019). Two layers:
///   1. by construction: a <see cref="Sensitive"/> value renders as "[redacted:Kind]";
///   2. a scrub of every other value's text, for what arrives as free text (exception messages, library strings):
///      any URL keeps only its scheme and host (a capability's secret is its path), and a "$1$" MD5 password digest
///      is removed.
/// </summary>
public sealed class Redactor
{
    private static readonly Regex Url = new(@"\b(?<origin>[a-zA-Z][a-zA-Z0-9+.-]*://[^/\s""'<>?#]+)(?<rest>[^\s""'<>]*)", RegexOptions.Compiled);
    private static readonly Regex Digest = new(@"\$1\$[0-9a-fA-F]{32}", RegexOptions.Compiled);
    // IPv4 addresses (with an optional :port) and bracketed or bare IPv6 addresses, in engine and library text.
    private static readonly Regex Address = new(@"\b(?:\d{1,3}\.){3}\d{1,3}(?::\d{1,5})?\b|\[[0-9a-fA-F:]{2,39}\](?::\d{1,5})?|\b(?:[0-9a-fA-F]{1,4}:){4,7}[0-9a-fA-F]{1,4}\b", RegexOptions.Compiled);
    private static readonly Regex IdShaped = new(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b", RegexOptions.Compiled);

    private readonly HashSet<SensitiveKind> _disabled;

    /// <summary>The only redactor product code uses: everything on.</summary>
    public static Redactor Default { get; } = new(Array.Empty<SensitiveKind>());

    private Redactor(IEnumerable<SensitiveKind> disabled) { _disabled = new HashSet<SensitiveKind>(disabled); }

    /// <summary>
    /// NEGATIVE CONTROL ONLY (tests): a redactor with one kind switched off, to prove each redaction test fails
    /// without the redaction it guards. Internal, so no product code can reach it.
    /// </summary>
    internal static Redactor WithoutForTests(SensitiveKind kind) => new(new[] { kind });

    private bool On(SensitiveKind kind) => !_disabled.Contains(kind);

    public string Render(object? value) => value switch
    {
        null => "null",
        Sensitive s => On(s.Kind) ? s.ToString() : s.Reveal(),
        EngineText e => ScrubEngine(e.Text),
        Exception ex => Scrub(ex.GetType().Name + ": " + ex.Message),
        IFormattable f => Scrub(f.ToString(null, System.Globalization.CultureInfo.InvariantCulture)),
        _ => Scrub(value.ToString() ?? ""),
    };

    /// <summary>
    /// Engine and library text (adds LibreMetaverse's own log): <see cref="Scrub"/>, plus every
    /// session-id-shaped value (a UUID) and every IP address removed. Stricter than the
    /// general scrub, because the core's own fields legitimately carry ids (an agent id) while the engine's never need one.
    /// </summary>
    public string ScrubEngine(string text)
    {
        text = Scrub(text);
        if (!string.IsNullOrEmpty(text) && On(SensitiveKind.SessionId))
            text = IdShaped.Replace(text, "[redacted:SessionId]");
        if (!string.IsNullOrEmpty(text) && On(SensitiveKind.NetworkAddress))
            text = Address.Replace(text, "[redacted:NetworkAddress]");
        return text;
    }

    /// <summary>Scrubs free text: URLs down to scheme://host, "$1$" digests removed.</summary>
    public string Scrub(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;
        if (On(SensitiveKind.CapabilityUrl))
            text = Url.Replace(text, m => m.Groups["rest"].Length == 0 ? m.Value : m.Groups["origin"].Value + "/[redacted:path]");
        if (On(SensitiveKind.PasswordDigest))
            text = Digest.Replace(text, "[redacted:PasswordDigest]");
        return text;
    }
}
