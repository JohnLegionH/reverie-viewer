// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Globalization;
using System.Text.RegularExpressions;
using Viewer.Core.Net;
using Viewer.Core.Session;

namespace Viewer.Shell;

public enum LoginDialogKind { Terms, Critical, Update, Token, TrustCertificate, CertificateError, Unknown, Disclosure }

/// <summary>One labelled line of a dialog (a certificate's subject, a fingerprint).</summary>
public sealed record DialogField(string Label, string Value);

/// <summary>
/// A dialog the login flow asks the person to answer, as plain data: what it says, which lines it lists, whether it has a
/// text input, an agreement box that has to be ticked before the main button works, a link to a page that opens in the system
/// browser, and its buttons. The view draws any of them the same way; tests read them without a window.
/// </summary>
/// <param name="Body">The text of the dialog (the grid's own words where the grid supplied them).</param>
/// <param name="ReadOnlyText">Longer text shown in a scrolling read-only box (terms text, a critical message).</param>
/// <param name="Link">An address shown as a link that opens in the system browser (a terms page).</param>
/// <param name="InputLabel">When set the dialog has a one-line input with this label (the multifactor token).</param>
/// <param name="AgreeLabel">When set the dialog has a box with this label that must be ticked before <paramref name="ConfirmLabel"/> works.</param>
/// <param name="ConfirmLabel">The main button's text.</param>
/// <param name="CancelLabel">The second button's text; null when the dialog has only the main button.</param>
public sealed record LoginDialog(
    LoginDialogKind Kind, string Title, string Body, IReadOnlyList<DialogField> Fields, string ReadOnlyText, string? Link,
    string? InputLabel, string? AgreeLabel, string ConfirmLabel, string? CancelLabel)
{
    /// <summary>A short name for the dialog, for the accessible tree and the automation.</summary>
    public string Id => Kind.ToString();
}

/// <summary>The answer to a <see cref="LoginDialog"/>: whether the main button was chosen, and the text typed into its input.</summary>
public sealed record LoginDialogResult(bool Confirmed, string? Text = null, bool Agreed = false)
{
    public static LoginDialogResult Cancelled { get; } = new(false);
}

/// <summary>Shows login dialogs and returns the person's answer. The shipping view puts them on the login screen.</summary>
public interface ILoginPrompts
{
    Task<LoginDialogResult> AskAsync(LoginDialog dialog, CancellationToken ct);
}

/// <summary>
/// The dialogs of the login flow, made from the login service's reply or the refused certificate and the string table. The texts
/// follow the official viewer's notifications for the same situations (terms of service, critical message, required update,
/// unrecognised failure, multifactor token, certificate trust and certificate error), with the product and site names of the
/// official viewer left out: this viewer has no download page or support site of its own to name.
/// </summary>
public static class LoginDialogs
{
    private static readonly Regex Url = new(@"https?://\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The terms of service. A grid's message that is (or holds) a web address becomes a link that opens in the system
    /// browser, as OpenSim grids supply a terms page; any other message is shown as the terms text; no message says to accept the terms
    /// on the grid's website. A text shown for reading gets the agreement box; a page elsewhere is accepted there.</summary>
    public static LoginDialog Terms(StringTable s, string message)
    {
        string text = (message ?? "").Trim();
        if (text.Length == 0)
            return new(LoginDialogKind.Terms, s.Get("login.terms.title"), s.Get("login.terms.external"), [], "", null, null, null, s.Get("login.button.continue"), s.Get("login.button.cancel"));
        var match = Url.Match(text);
        string? link = match.Success ? match.Value : null;
        string readOnly = link is not null && text == link ? "" : text;
        return new(LoginDialogKind.Terms, s.Get("login.terms.title"), s.Get("login.terms.heading"), [], readOnly, link, null, s.Get("login.terms.agree"),
            s.Get("login.button.continue"), s.Get("login.button.cancel"));
    }

    /// <summary>The grid's critical message as read-only text with one button; the error code and certificate the grid sent with it are listed when present.</summary>
    public static LoginDialog Critical(StringTable s, LoginReply reply)
    {
        var fields = new List<DialogField>();
        if (reply.ErrorCode.Length > 0) fields.Add(new(s.Get("login.critical.code"), reply.ErrorCode));
        if (reply.Certificate.Length > 0) fields.Add(new(s.Get("login.critical.certificate"), reply.Certificate));
        return new(LoginDialogKind.Critical, s.Get("login.critical.title"), s.Get("login.critical.heading"), fields, reply.Message, null, null, null, s.Get("login.button.continue"), null);
    }

    /// <summary>The required-update notice: the version the grid names (when it did), then the grid's own message. The official text sends
    /// people to its download page; this viewer has none to name yet.</summary>
    public static LoginDialog Update(StringTable s, LoginReply reply)
    {
        string head = reply.MessageArgs.TryGetValue("VERSION", out var version) && version.Length > 0 ? s.Get("login.update.required", version) : s.Get("login.update.required_unknown");
        string body = reply.Message.Trim().Length > 0 ? head + "\n\n" + reply.Message.Trim() : head;
        return new(LoginDialogKind.Update, s.Get("login.update.title"), body, [], "", null, null, null, s.Get("login.button.ok"), null);
    }

    public static LoginDialog Unknown(StringTable s) =>
        new(LoginDialogKind.Unknown, s.Get("login.unknown.title"), s.Get("login.unknown.body"), [], "", null, null, null, s.Get("login.button.ok"), null);

    /// <summary>The multifactor token prompt. <paramref name="body"/> comes from <see cref="TokenBody"/>.</summary>
    public static LoginDialog Token(StringTable s, string body) =>
        new(LoginDialogKind.Token, s.Get("login.token.title"), body, [], "", null, s.Get("login.token.label"), null, s.Get("login.button.continue"), s.Get("login.button.cancel"));

    /// <summary>The text above the token box: the viewer's own text for the message id the grid sent (the one id the official viewer has text
    /// for), else the grid's own message, else the same required-token text.</summary>
    public static string TokenBody(StringTable s, LoginReply? reply)
    {
        if (reply is null || reply.MessageId == "LoginFailedAuthenticationMFARequired" || reply.Message.Trim().Length == 0)
            return s.Get("login.token.required");
        return reply.Message.Trim();
    }

    /// <summary>The token as typed, with every whitespace character removed (a code is often pasted with spaces).</summary>
    public static string StripWhitespace(string? token) => new((token ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray());

    private static string Pairs(string hex) => hex.Length == 0 ? "-" : string.Join(":", Enumerable.Range(0, hex.Length / 2).Select(i => hex.Substring(i * 2, 2)));
    private static string When(DateTimeOffset? t) => t is { } v ? v.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) : "-";
    private static string Or(string v) => v.Length == 0 ? "-" : v;

    /// <summary>The certificate's facts, each fingerprint under its own label (the official dialog's text swaps the MD5 and SHA-1 labels).</summary>
    public static IReadOnlyList<DialogField> CertificateFields(StringTable s, CertificateRefusal c) =>
    [
        new(s.Get("login.cert.field.subject"), Or(c.Subject)),
        new(s.Get("login.cert.field.issuer"), Or(c.Issuer)),
        new(s.Get("login.cert.field.valid_from"), When(c.NotBefore)),
        new(s.Get("login.cert.field.valid_to"), When(c.NotAfter)),
        new(s.Get("login.cert.field.md5"), Pairs(c.Md5)),
        new(s.Get("login.cert.field.sha1"), Pairs(c.Sha1)),
        new(s.Get("login.cert.field.sha256"), Pairs(c.Sha256)),
        new(s.Get("login.cert.field.key_usage"), Or(c.KeyUsage)),
        new(s.Get("login.cert.field.extended_key_usage"), Or(c.ExtendedKeyUsage)),
        new(s.Get("login.cert.field.subject_key_identifier"), Or(c.SubjectKeyIdentifier)),
    ];

    /// <summary>A certificate from an unknown authority, with Trust and Cancel: Trust pins this certificate for this server.</summary>
    public static LoginDialog TrustCertificate(StringTable s, CertificateRefusal c) =>
        new(LoginDialogKind.TrustCertificate, s.Get("login.cert.title.trust"),
            s.Get("login.cert.unknown_authority") + "\n\n" + s.Get("login.cert.pinned") + "\n" + s.Get("login.cert.trust_question"),
            CertificateFields(s, c), "", null, null, null, s.Get("login.button.trust"), s.Get("login.button.cancel"));

    /// <summary>Any other certificate problem: the reason and, when the certificate could be read, its facts; one OK button and no way to trust it.</summary>
    public static LoginDialog CertificateError(StringTable s, CertificateRefusal c)
    {
        string reason = c.Kind switch
        {
            CertificateProblem.Expired => s.Get("login.cert.expired"),
            CertificateProblem.WrongHost => s.Get("login.cert.invalid_hostname"),
            CertificateProblem.Unreadable => s.Get("login.cert.invalid"),
            CertificateProblem.UnknownAuthority => s.Get("login.cert.unknown_authority"),
            _ => s.Get("login.cert.invalid_signature"),
        };
        IReadOnlyList<DialogField> fields = c.Kind == CertificateProblem.Unreadable || c.Sha256.Length == 0 ? [] : CertificateFields(s, c);
        return new(LoginDialogKind.CertificateError, s.Get("login.cert.title.error"), s.Get("login.cert.cannot_connect") + "\n" + reason, fields, "", null, null, null, s.Get("login.button.ok"), null);
    }
}
