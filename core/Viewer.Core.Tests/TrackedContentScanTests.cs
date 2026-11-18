// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// as a permanent test (pre-push scan found a real LAN address in a test): tracked files
/// carry no private (RFC 1918) or link-local address, no host outside the documentation-reserved and reviewed public
/// set (so no grid hostname), and nothing secret-shaped. Examples use 127.0.0.1, 192.0.2.0/24, 198.51.100.0/24,
/// 203.0.113.0/24, *.invalid, *.example and example.com/.net/.org. Negative control: the scanner flags each class.
/// </summary>
public class TrackedContentScanTests
{
    private static readonly Regex PrivateV4 = new(@"(?<![\d.])(10\.\d{1,3}\.\d{1,3}\.\d{1,3}|192\.168\.\d{1,3}\.\d{1,3}|172\.(1[6-9]|2\d|3[01])\.\d{1,3}\.\d{1,3}|169\.254\.\d{1,3}\.\d{1,3})(?![\d.]*\d)", RegexOptions.Compiled);
    // Network schemes only (Godot's res:// and uid:// are not hosts).
    private static readonly Regex UrlHost = new(@"\b(?:https?|wss?|ftp)://(?:[^/\s""'<>@]*@)?(?<host>[a-zA-Z0-9.-]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SecretShape = new(@"BEGIN (RSA |EC |OPENSSH |DSA |)PRIVATE KEY|ghp_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|gho_[A-Za-z0-9]{20,}|xox[abprs]-[A-Za-z0-9-]{10,}|AKIA[0-9A-Z]{16}|discord(app)?\.com/api/webhooks/\d+|\$1\$[0-9a-fA-F]{32}", RegexOptions.Compiled);

    /// <summary>The synthetic digest the redaction tests use (0-9a-f in order): documented, not a secret.</summary>
    private const string SyntheticDigest = "$1$0123456789abcdef0123456789abcdef";
    /// <summary>RedactionTests' own synthetic digest (reviewed rewrite: not the digest of any account's password).</summary>
    private const string RedactionTestsDigest = "$1$5f4dcc3b5aa765d61d8327deb882cf99";

    /// <summary>Reviewed exception for addresses ONLY: the outbound policy defines and tests the private ranges it refuses.</summary>
    private static readonly string[] PrivateRangeFiles = ["core/Viewer.Core/Net/OutboundPolicy.cs", "core/Viewer.Core.Tests/OutboundPolicyTests.cs"];

    /// <summary>Hosts allowed in URLs: documentation-reserved, loopback, and the public sites the project cites.</summary>
    private static bool AllowedHost(string host, string path)
    {
        host = host.ToLowerInvariant().TrimEnd('.');
        if (!host.Contains('.'))
            return true;   // single-label synthetic test hosts ("x", "grid"): not resolvable on the internet
        if (PrivateRangeFiles.Contains(path) && PrivateV4.IsMatch(host))
            return true;   // the outbound policy's own private-destination cases
        if (host is "localhost" or "127.0.0.1" || host.StartsWith("192.0.2.") || host.StartsWith("198.51.100.") || host.StartsWith("203.0.113."))
            return true;
        if (host is "example.com" or "example.net" or "example.org" || host.EndsWith(".example.com") || host.EndsWith(".example.org") || host.EndsWith(".example.net") || host.EndsWith(".example") || host.EndsWith(".invalid") || host.EndsWith(".test"))
            return true;
        string[] publicSites =
        [
            "github.com", "schemas.microsoft.com", "raw.githubusercontent.com", "api.github.com", "nuget.org", "api.nuget.org", "www.nuget.org", "godotengine.org", "docs.godotengine.org",
            "opensimulator.org", "mozilla.org", "learn.microsoft.com", "aka.ms", "www.w3.org", "json-schema.org", "creativecommons.org", "www.gnu.org", "opensource.org", "dotnet.microsoft.com",
            "claude.com", "claude.ai", "www.apache.org", "secondlife.com", "wiki.secondlife.com", "jpeg.org", "khronos.org", "www.khronos.org", "mynameismjp.wordpress.com",
            // Second Life's own login hosts, which the script editor tells apart from other grids (BuildContentTabTests)
            "login.agni.lindenlab.com", "login.aditi.lindenlab.com",
            // a lookalike that ends in Linden Lab's domain name without being under it, which the grid switch must not take for Second Life (GridPolicyTests)
            "notlindenlab.com",
            // cited in Docs (public references, reviewed rewrite): a new host fails this test until reviewed here
            "modemworld.me", "jrouwe.github.io", "www.firestormviewer.org", "wiki.firestormviewer.org", "docs.wasmtime.dev", "www.stride3d.net",
            "www.minecraft.net", "www.gamedeveloper.com", "web.dev", "threejs.org", "theupdateframework.github.io", "luau.org", "glama.ai",
            "fosdem.org", "docs.rs",
            // the public-domain sound packs the interface sounds come from, credited in THIRD-PARTY-NOTICES.md and UiSoundRecordings.cs
            "opengameart.org",
            // graphics makers' published API support, cited in Docs/SPECS.md
            "developer.nvidia.com", "www.amd.com", "www.intel.com",
            // named in the unmodified licence text of the vendored interface font (ThirdPartyLicenses/LICENSE-Inter.txt)
            "scripts.sil.org",
            // named in the unmodified third-party notices of the bundled .NET runtime (ThirdPartyLicenses/THIRD-PARTY-NOTICES-dotnet-runtime-*.txt)
            "sourceforge.net", "www.unicode.org", "www.mono-project.com", "www.xamarin.com", "www.novell.com", "llvm.org", "www.ryanjuckett.com", "www.xxhash.com", "arxiv.org",
            // a bundled package's own address, as its package metadata gives it (THIRD-PARTY-NOTICES.md)
            "nickbabcock.github.io",
            // named in the unmodified licence text of a bundled package (zlib.net's BSD licence, THIRD-PARTY-NOTICES.md)
            "www.componentace.com",
            // the COLLADA 1.4.1 schema namespace that every .dae file names (the model upload's generated test models)
            "www.collada.org",
        ];
        return publicSites.Contains(host) || publicSites.Any(p => host.EndsWith("." + p));
    }

    internal static List<string> Scan(string path, string text)
    {
        var found = new List<string>();
        int Line(int index) => text.AsSpan(0, index).Count('\n') + 1;
        if (!PrivateRangeFiles.Contains(path))
            foreach (Match m in PrivateV4.Matches(text))
                found.Add($"{path}:{Line(m.Index)} private or link-local address");
        foreach (Match m in UrlHost.Matches(text))
            if (!AllowedHost(m.Groups["host"].Value, path))
                found.Add($"{path}:{Line(m.Index)} host not documentation-reserved or reviewed ({m.Groups["host"].Value.Length} chars)");
        foreach (Match m in SecretShape.Matches(text))
            if (m.Value != SyntheticDigest && m.Value != RedactionTestsDigest)
                found.Add($"{path}:{Line(m.Index)} secret-shaped value");
        return found;
    }

    /// <summary>A tracked file that is the viewer's saved-login file, whatever it holds: it never belongs in the repository.</summary>
    internal static List<string> ScanName(string path) =>
        string.Equals(Path.GetFileName(path), Viewer.Shell.DpapiLoginVault.FileName, StringComparison.OrdinalIgnoreCase) ? [$"{path} the viewer's saved-login file"] : [];

    private static readonly string[] BinaryExtensions = [".png", ".gz", ".llmesh", ".bin", ".j2c", ".dll", ".pck", ".exe"];

    [Fact]
    public void Tracked_files_carry_no_private_address_grid_host_or_secret()
    {
        var (exit, listing) = ChildProcess.Run("git", ["-C", TestPaths.RepoRoot, "ls-files"], TimeSpan.FromSeconds(30));
        Assert.Equal(0, exit);
        var files = listing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(f => !BinaryExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .Where(f => f != "core/Viewer.Core.Tests/TrackedContentScanTests.cs")   // this file: only the negative control's synthetic samples
            .ToList();
        Assert.True(files.Count > 100, $"only {files.Count} tracked text files listed");
        var findings = new List<string>();
        foreach (var name in listing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            findings.AddRange(ScanName(name.Replace('\\', '/')));   // every tracked file, binary ones too
        foreach (var f in files)
        {
            var full = Path.Combine(TestPaths.RepoRoot, f);
            if (!File.Exists(full)) continue;
            var bytes = File.ReadAllBytes(full);
            if (Array.IndexOf(bytes, (byte)0) >= 0) continue;   // binary by content
            findings.AddRange(Scan(f.Replace('\\', '/'), Encoding.UTF8.GetString(bytes)));
        }
        Assert.True(findings.Count == 0, string.Join("\n", findings));
    }

    [Fact]
    public void Negative_control_each_class_is_flagged_and_documentation_examples_are_not()
    {
        Assert.NotEmpty(Scan("x.cs", "sim at 192.168.4.20:9000"));
        Assert.NotEmpty(Scan("x.cs", "gateway 10.1.2.3"));
        Assert.NotEmpty(Scan("x.cs", "docker 172.22.0.5"));
        Assert.NotEmpty(Scan("x.cs", "link 169.254.10.1"));
        Assert.NotEmpty(Scan("x.cs", "login http://sim.mygrid.net:8002/"));
        Assert.NotEmpty(Scan("x.cs", "caps https://10.0.0.8:9000/CAPS/abc/"));
        Assert.NotEmpty(Scan("x.cs", "token ghp_" + new string('a', 36)));
        Assert.NotEmpty(Scan("x.cs", "-----BEGIN RSA PRIVATE KEY-----"));
        Assert.NotEmpty(Scan("x.cs", "$1$" + "fedcba9876543210fedcba9876543210"));
        Assert.Empty(Scan("x.cs", "use 127.0.0.1, 192.0.2.7, 198.51.100.40, 203.0.113.9 and https://grid.invalid:9000/CAPS/x/ or http://login.example/"));
        Assert.Empty(Scan("x.cs", SyntheticDigest + " https://github.com/cinderblocks/libremetaverse version 10.0.12"));
        Assert.Empty(Scan(PrivateRangeFiles[1], "10.0.0.1 is private"));   // the reviewed exception, addresses only
    }

    [Fact]
    public void The_saved_login_file_is_flagged_by_its_name_wherever_it_sits_and_other_names_are_not()
    {
        Assert.NotEmpty(ScanName(Viewer.Shell.DpapiLoginVault.FileName));
        Assert.NotEmpty(ScanName("fixtures/appdata/ReverieViewer/" + Viewer.Shell.DpapiLoginVault.FileName));
        Assert.NotEmpty(ScanName("host/Viewer.Godot/build/" + Viewer.Shell.DpapiLoginVault.FileName.ToUpperInvariant()));
        Assert.Empty(ScanName("host/Viewer.Shell/RememberedLogins.cs"));
        Assert.Empty(ScanName("docs/remembered-logins.md"));
    }
}
