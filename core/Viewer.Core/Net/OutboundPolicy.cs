// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Net;
using System.Net.Sockets;

namespace Viewer.Core.Net;

/// <summary>Where <see cref="OutboundPolicy"/> is applied, for the log line when something is denied.</summary>
public enum OutboundKind
{
    Login,
    Http,
    Simulator,
}

/// <summary>Thrown when a destination fails the policy. The message names the host and the offending address.</summary>
public sealed class OutboundDeniedException(string message) : Exception(message);

public enum OutboundMode
{
    /// <summary>The gateway connects wherever the grid tells it to. The default, and how it always behaved.</summary>
    Off,

    /// <summary>Private, loopback, link-local, CGNAT and multicast destinations are refused unless exempted.</summary>
    Strict,
}

/// <summary>Where a session may connect: the core's subset of the gateway's options (defaults reproduce the gateway: off).</summary>
public sealed record OutboundOptions
{
    /// <summary>"off" (default) or "strict". Anything else fails <see cref="Validate"/>; it is never read as "off".</summary>
    public string Policy { get; init; } = "off";

    /// <summary>Addresses, CIDRs or host names exempt from strict mode (a grid on the same LAN).</summary>
    public IReadOnlyList<string> AllowedInternalEndpoints { get; init; } = Array.Empty<string>();

    /// <summary>The names of invalid settings; empty when valid.</summary>
    public IEnumerable<string> Validate()
    {
        if (Policy is null || !(Policy.Equals("off", StringComparison.OrdinalIgnoreCase) || Policy.Equals("strict", StringComparison.OrdinalIgnoreCase)))
            yield return nameof(Policy);
    }
}

/// <summary>
/// Copied from the web gateway (web-viewer bb91ba5, gateway/src/Gateway/OutboundPolicy.cs; ADR-006 copy-and-refactor),
/// with GatewayOptions replaced by <see cref="OutboundOptions"/>. One instance per session.
///
/// WV-3d-1: one gate in front of every outbound connection the gateway makes on its own account.
///
/// The gateway goes where the grid tells it to go: the login URI, the simulator address in the login response, the
/// capability URLs a region hands out, map and appearance services, and whatever a hypergrid teleport names. On a
/// home-hosted gateway that is a server-side request forgery path straight into the LAN -- a hostile or compromised
/// region can point the gateway at a router admin page, a NAS, or another service on the host, and the gateway will
/// dutifully connect as itself.
///
/// Strict mode refuses those destinations. It is off by default, because turning it on without
/// <see cref="OutboundOptions.AllowedInternalEndpoints"/> breaks any grid that lives on the same LAN -- which is
/// exactly the case for a grid hosted on the same LAN as the viewer.
///
/// The check is always made on the RESOLVED addresses, never on the name, and the caller then connects to an address
/// that was checked rather than re-resolving: a name that answers with a public address and then a private one has
/// nothing to win.
/// </summary>
public sealed class OutboundPolicy
{
    private readonly (IPAddress Address, int Prefix)[] _exemptNets;
    private readonly string[] _exemptHosts;

    public OutboundMode Mode { get; }
    public bool Strict => Mode == OutboundMode.Strict;

    /// <summary>The exemptions as configured, for the startup log.</summary>
    public IReadOnlyList<string> Exemptions { get; }

    public OutboundPolicy(OutboundOptions opts)
    {
        Mode = string.Equals(opts.Policy, "strict", StringComparison.OrdinalIgnoreCase)
            ? OutboundMode.Strict
            : OutboundMode.Off;

        var nets = new List<(IPAddress, int)>();
        var hosts = new List<string>();
        foreach (var raw in opts.AllowedInternalEndpoints)
        {
            var entry = raw?.Trim();
            if (string.IsNullOrEmpty(entry)) continue;   // compose maps unused slots as empty strings
            if (TryParseNetwork(entry, out var net)) nets.Add(net);
            else hosts.Add(entry);
        }
        _exemptNets = nets.ToArray();
        _exemptHosts = hosts.ToArray();
        Exemptions = opts.AllowedInternalEndpoints.Where(e => !string.IsNullOrWhiteSpace(e)).ToArray();
    }

    /// <summary>An IP, or a CIDR. Anything else is taken to be a host name.</summary>
    public static bool TryParseNetwork(string entry, out (IPAddress Address, int Prefix) network)
    {
        network = default;
        var slash = entry.IndexOf('/');
        if (slash < 0)
        {
            if (!IPAddress.TryParse(entry, out var single)) return false;
            network = (single, single.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32);
            return true;
        }
        if (!IPAddress.TryParse(entry[..slash], out var baseAddr)) return false;
        if (!int.TryParse(entry[(slash + 1)..], out var prefix)) return false;
        var max = baseAddr.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
        if (prefix < 0 || prefix > max) return false;
        network = (baseAddr, prefix);
        return true;
    }

    private static bool InNetwork(IPAddress address, (IPAddress Address, int Prefix) net)
    {
        var a = Unwrap(address);
        var b = Unwrap(net.Address);
        if (a.AddressFamily != b.AddressFamily) return false;
        var ab = a.GetAddressBytes();
        var bb = b.GetAddressBytes();
        var bits = net.Prefix;
        // An IPv4 prefix written against an address we unwrapped from IPv4-mapped IPv6 keeps its own length.
        if (bits > ab.Length * 8) return false;
        for (var i = 0; i < ab.Length && bits > 0; i++, bits -= 8)
        {
            var mask = bits >= 8 ? (byte)0xFF : (byte)(0xFF << (8 - bits));
            if ((ab[i] & mask) != (bb[i] & mask)) return false;
        }
        return true;
    }

    /// <summary>IPv4-mapped IPv6 (:ffff:192.168.0.1) is the same destination as the IPv4 address; compare it as one.</summary>
    private static IPAddress Unwrap(IPAddress ip) =>
        ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;

    /// <summary>
    /// Addresses a gateway has no business dialling on a grid's say-so: loopback, RFC1918 private, link-local,
    /// CGNAT (100.64/10), multicast, unspecified/broadcast, and the IPv6 equivalents including unique-local fc00::/7.
    /// </summary>
    public static bool IsInternal(IPAddress address)
    {
        var ip = Unwrap(address);

        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b[0] == 0) return true;                                     // 0.0.0.0/8 "this network"
            if (b[0] == 10) return true;                                    // 10/8
            if (b[0] == 127) return true;                                   // 127/8 (also IsLoopback)
            if (b[0] == 169 && b[1] == 254) return true;                    // 169.254/16 link-local
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;       // 172.16/12
            if (b[0] == 192 && b[1] == 168) return true;                    // 192.168/16
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true;      // 100.64/10 CGNAT
            if (b[0] == 192 && b[1] == 0 && b[2] == 0) return true;         // 192.0.0/24 IETF protocol assignments
            if (b[0] >= 224) return true;                                   // 224/4 multicast + 240/4 reserved + broadcast
            return false;
        }

        if (ip.IsIPv6LinkLocal || ip.IsIPv6Multicast || ip.IsIPv6SiteLocal) return true;
        var v6 = ip.GetAddressBytes();
        if ((v6[0] & 0xFE) == 0xFC) return true;                            // fc00::/7 unique local
        return false;
    }

    /// <summary>True when this destination may be dialled. Off mode allows everything, as before.</summary>
    public bool IsAllowed(IPAddress address, string? host = null)
    {
        if (!Strict) return true;
        if (host is not null && IsExemptHost(host)) return true;
        foreach (var net in _exemptNets)
            if (InNetwork(address, net)) return true;
        return !IsInternal(address);
    }

    /// <summary>
    /// May the agent open a circuit to this simulator? The address comes from the grid (the login response, or a
    /// teleport), never from us. A null endpoint is allowed: there is nothing to judge, and refusing would break
    /// connections for a reason unrelated to the policy.
    /// </summary>
    public bool AllowsSimulator(IPEndPoint? endpoint) =>
        endpoint?.Address is not { } address || IsAllowed(address);

    public bool IsExemptHost(string host) =>
        _exemptHosts.Any(h => string.Equals(h, host, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Resolve <paramref name="host"/> and return addresses that all passed the policy. Throws
    /// <see cref="OutboundDeniedException"/> if ANY answer is denied: a name that mixes a public and a private answer
    /// is not a name we are willing to race against.
    /// </summary>
    public async Task<IPAddress[]> ResolveAllowedAsync(string host, OutboundKind kind, CancellationToken ct)
    {
        IPAddress[] addresses = IPAddress.TryParse(host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);

        if (addresses.Length == 0) throw new OutboundDeniedException($"{kind}: {host} resolved to nothing");
        if (!Strict) return addresses;
        if (IsExemptHost(host)) return addresses;

        foreach (var a in addresses)
            if (!IsAllowed(a, host))
                throw new OutboundDeniedException(
                    $"{kind}: {host} resolves to {a}, which is an internal address; add it to AllowedInternalEndpoints if that is intended");
        return addresses;
    }

    /// <summary>http and https only. A capability URL naming file:// or gopher:// is not something we follow.</summary>
    public void EnsureSchemeAllowed(Uri uri, OutboundKind kind)
    {
        if (!Strict) return;
        if (uri.Scheme is not ("http" or "https"))
            throw new OutboundDeniedException($"{kind}: scheme '{uri.Scheme}' is not allowed ({uri.GetLeftPart(UriPartial.Authority)})");
    }

    /// <summary>Check a whole URI: scheme, then the resolved host. Used for the login URI and any URL from the grid.</summary>
    public async Task EnsureUriAllowedAsync(Uri uri, OutboundKind kind, CancellationToken ct)
    {
        EnsureSchemeAllowed(uri, kind);
        if (!Strict) return;
        await ResolveAllowedAsync(uri.Host, kind, ct).ConfigureAwait(false);
    }
}
