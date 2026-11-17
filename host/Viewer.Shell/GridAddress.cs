// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Xml;
using Viewer.Core.Diagnostics;
using Viewer.Core.Net;

namespace Viewer.Shell;

/// <summary>The outcome of tidying a typed grid address: the address, or the string-table key of what is wrong with it.</summary>
public sealed record TidyResult(Uri? Uri, string? ProblemKey);

/// <summary>What fetching one address got: the HTTP status (null when nothing answered), the body, a short failure reason, and the
/// server certificate that was refused when that is why nothing answered.</summary>
public sealed record GridFetch(int? Status, string? Body, string? Failure, CertificateRefusal? Refused = null);

/// <summary>The one GET the resolver makes (injected so tests can use a loopback server and the shipping build uses the session's outbound rules).</summary>
public delegate Task<GridFetch> GridFetcher(Uri uri, CancellationToken ct);

/// <summary>The login address a typed address resolves to, or why it does not (<see cref="ProblemKey"/> with its HTTP status).</summary>
public sealed record ResolvedGrid(Uri? Login, string? ProblemKey, int? Status, bool FromGridInfo);

/// <summary>
/// What people paste into the grid field: stray quotes, backticks and spaces are removed from its ends, a missing
/// http:// or https:// is reported (never guessed), and a grid's base address or its grid-info address is resolved to the
/// login address the grid publishes. Grid info is fetched only from the address the user typed.
/// </summary>
public static class GridAddress
{
    private static readonly char[] Wrapping = ['\'', '"', '`', '\u2018', '\u2019', '\u201C', '\u201D', '<', '>', ' ', '\t', '\r', '\n'];
    private const string GridInfoPath = "get_grid_info";

    public static TidyResult Tidy(string? text)
    {
        string t = (text ?? "").Trim(Wrapping);
        if (t.Length == 0) return new(null, "login.status.bad_uri");
        if (!t.Contains("://", StringComparison.Ordinal)) return new(null, "login.status.no_scheme");
        if (t.IndexOfAny(['\'', '"', '`', ' ', '<', '>']) >= 0) return new(null, "login.status.bad_uri");
        if (!Uri.TryCreate(t, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || uri.Host.Length == 0)
            return new(null, "login.status.bad_uri");
        return new(uri, null);
    }

    /// <summary>The grid-info address for a typed address: itself when it already names get_grid_info, else get_grid_info under it.</summary>
    public static Uri GridInfoOf(Uri typed)
    {
        string path = typed.AbsolutePath;
        if (path.TrimEnd('/').EndsWith("/" + GridInfoPath, StringComparison.OrdinalIgnoreCase) || path.TrimEnd('/').Equals(GridInfoPath, StringComparison.OrdinalIgnoreCase))
            return typed;
        var b = new UriBuilder(typed) { Query = "", Fragment = "" };
        b.Path = path.TrimEnd('/') + "/" + GridInfoPath;
        return b.Uri;
    }

    /// <summary>The login value of a grid-info document, or null when it is not one.</summary>
    public static Uri? LoginOfGridInfo(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 256 * 1024 };
            using var r = XmlReader.Create(new StringReader(body), settings);
            bool inInfo = false;
            while (r.Read())
            {
                if (r.NodeType != XmlNodeType.Element) continue;
                if (r.Name.Equals("gridinfo", StringComparison.OrdinalIgnoreCase)) { inInfo = true; continue; }
                if (inInfo && r.Name.Equals("login", StringComparison.OrdinalIgnoreCase))
                {
                    var tidy = Tidy(r.ReadElementContentAsString());
                    return tidy.Uri;
                }
            }
        }
        catch (XmlException) { }
        return null;
    }

    /// <summary>Resolves a tidied address to the login address to use.</summary>
    public static async Task<ResolvedGrid> ResolveAsync(Uri typed, GridFetcher fetch, CancellationToken ct)
    {
        var infoUri = GridInfoOf(typed);
        bool namedInfo = infoUri == typed;
        var got = await fetch(infoUri, ct).ConfigureAwait(false);
        if (got.Status is >= 200 and < 300 && LoginOfGridInfo(got.Body) is { } login)
            return new(login, null, got.Status, true);
        // an address that names grid info but is not grid info is not a login service either way
        if (namedInfo) return new(null, "login.status.not_login_service", got.Status, false);
        // a base address whose host publishes no grid info: it may still be the login service itself
        return new(typed, null, got.Status, false);
    }

    /// <summary>The fetcher the viewer uses: a bounded GET through the same outbound rules as the session.</summary>
    /// <param name="trust">The certificates the person chose to trust; the GET accepts those and no others that the platform refuses.</param>
    public static GridFetcher HttpFetcher(OutboundPolicy policy, CoreLog log, TimeSpan? timeout = null, CertificateTrust? trust = null)
    {
        var refusals = new CertificateRefusals { Trust = trust };
        var client = OutboundHttp.CreateClient(policy, log, timeout ?? TimeSpan.FromSeconds(10), refusals: refusals);
        return async (uri, ct) =>
        {
            refusals.Clear(uri.Host);   // a refusal reported is this GET's own, not an earlier one's
            try
            {
                var r = await BoundedHttp.GetBoundedAsync(client, uri, 256 * 1024, ct).ConfigureAwait(false);
                return new(r.Status, r.Body is null ? null : System.Text.Encoding.UTF8.GetString(r.Body), r.Why, r.Status is null ? refusals.Latest(uri.Host) : null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or OutboundDeniedException or InvalidOperationException)
            {
                return new(null, null, ex.GetType().Name, refusals.Latest(uri.Host));
            }
        };
    }
}

/// <summary>The last successful login address, kept in the user's settings folder.</summary>
public static class LoginAddressStore
{
    public const string FileName = "last-login-address.txt";

    public static string? Load(string settingsDir)
    {
        try
        {
            string path = Path.Combine(settingsDir, FileName);
            if (!File.Exists(path)) return null;
            var tidy = GridAddress.Tidy(File.ReadAllText(path));
            return tidy.Uri?.ToString();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public static void Save(string settingsDir, string address)
    {
        try
        {
            Directory.CreateDirectory(settingsDir);
            File.WriteAllText(Path.Combine(settingsDir, FileName), address);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a settings folder that cannot be written is not a login failure */ }
    }
}
