// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text.RegularExpressions;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// On the host's shipping paths every Godot collection is a kept one. GodotSharp 4.7.2 reads and
/// destroys a <c>Godot.Collections.Array</c> or <c>Dictionary</c> through an unpinned pointer into its managed wrapper
/// (every mutating call, <c>Dispose</c>, the finalizer), so the host makes its collections once
/// (<c>Presentation/KeptCollections.cs</c>), never disposes them, and pins them around every use. A source scan of the
/// host project (the test-render harness, <c>TestRender.cs</c>, and its node terrain view,
/// <c>Presentation/NodeTerrainView.cs</c>, which only the harness makes (SurfaceCallScanTests), are not shipping paths): (1) no Godot collection is made
/// anywhere but in KeptCollections.cs; (2) none is disposed (no <c>using</c> of one); (3) every engine surface call is
/// inside a pinned scope. Negative controls: the scan flags the forms the host had.
/// </summary>
public class KeptGodotCollectionsScanTests
{
    private static readonly Regex MadeCollection = new(@"\bnew\s+(?:global::)?Godot\.Collections\.(?:Array|Dictionary)\b|ChunkArrays\.Of\s*\(", RegexOptions.Compiled);
    private static readonly Regex UsingCollection = new(
        @"\busing\s*(?:\(\s*)?var\s+\w+\s*=\s*(?:new\s+(?:global::)?Godot\.Collections\.|ChunkArrays\.Of\s*\()", RegexOptions.Compiled);
    private static readonly Regex SurfaceCall = new(@"\b(?:MeshAddSurfaceFromArrays|AddSurfaceFromArrays|MeshAddSurface)\s*\(", RegexOptions.Compiled);
    private const string KeptFile = "Presentation/KeptCollections.cs";
    /// <summary>The harness files (not shipping paths): the render harness and the node terrain view.</summary>
    private static readonly string[] Harness = ["TestRender.cs", "Presentation/NodeTerrainView.cs"];
    private const int PinLookBack = 20;   // lines before a surface call in which its pins must be taken

    internal static List<string> Offenders(string hostDir)
    {
        var found = new List<string>();
        foreach (var file in Directory.EnumerateFiles(hostDir, "*.cs", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(hostDir, file).Replace('\\', '/');
            if (rel.Contains("/obj/") || rel.Contains("/bin/") || rel.StartsWith("obj/") || rel.StartsWith("bin/") || Harness.Contains(rel))
                continue;
            found.AddRange(Offenders(rel, File.ReadAllLines(file)));
        }
        return found;
    }

    internal static IEnumerable<string> Offenders(string rel, string[] lines)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string code = line.Split("//")[0];
            if (UsingCollection.IsMatch(code)) yield return $"{rel}:{i + 1}: disposed: {line.Trim()}";
            else if (rel != KeptFile && MadeCollection.IsMatch(code)) yield return $"{rel}:{i + 1}: made outside KeptCollections: {line.Trim()}";
            if (SurfaceCall.IsMatch(code) && !code.TrimStart().StartsWith("///"))
            {
                bool pinned = false;
                for (int j = Math.Max(0, i - PinLookBack); j < i && !pinned; j++) pinned = lines[j].Contains("KeptCollections.Pin(");
                if (!pinned) yield return $"{rel}:{i + 1}: engine surface call outside a pinned scope: {line.Trim()}";
            }
        }
    }

    [Fact]
    public void Every_Godot_collection_on_the_shipping_paths_is_kept_and_pinned()
    {
        string host = Path.Combine(TestPaths.RepoRoot, "host", "Viewer.Godot");
        Assert.True(Directory.Exists(host), host);
        Assert.Empty(Offenders(host));
        Assert.True(File.Exists(Path.Combine(host, "Presentation", "KeptCollections.cs")), "KeptCollections.cs is missing");
    }

    [Fact]
    public void The_kept_collections_are_never_disposed()
    {
        string kept = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "host", "Viewer.Godot", "Presentation", "KeptCollections.cs"));
        Assert.DoesNotContain(".Dispose(", kept);
        Assert.DoesNotContain("IDisposable", kept);
    }

    /// <summary>The pinned Variant disposal reads GodotSharp 4.7.2's private <c>Variant._disposer</c>;
    /// a GodotSharp other than 4.7.2 must fail here (the harness check, --tr-keptcollections, fails if the member is not
    /// found at run time; the viewer then falls back to the holding pattern and logs it once).</summary>
    [Fact]
    public void The_pinned_Variant_disposal_is_for_GodotSharp_4_7_2()
    {
        string host = Path.Combine(TestPaths.RepoRoot, "host", "Viewer.Godot");
        Assert.Contains("<Project Sdk=\"Godot.NET.Sdk/4.7.2\">", File.ReadAllText(Path.Combine(host, "Viewer.Godot.csproj")));
        string held = File.ReadAllText(Path.Combine(host, "Presentation", "HeldValues.cs"));
        Assert.Contains("public const string ExpectedGodotSharp = \"4.7.2\";", held);
        Assert.Contains("GetField(\"_disposer\"", held);
    }

    [Theory]
    [InlineData("        using var arrays = new global::Godot.Collections.Array();   // held through the native call")]
    [InlineData("            using var d = new global::Godot.Collections.Dictionary();")]
    [InlineData("        using var arrays = ChunkArrays.Of(chunk, held);")]
    [InlineData("        using (var lods = new global::Godot.Collections.Dictionary())")]
    [InlineData("            var blendShapes = new global::Godot.Collections.Array<global::Godot.Collections.Array>();   // not disposable: kept alive instead")]
    [InlineData("        var arrays = new global::Godot.Collections.Array();")]
    public void The_scan_flags_the_forms_the_host_had_before(string line)
    {
        Assert.NotEmpty(Offenders("Presentation/Example.cs", [line]));
    }

    [Fact]
    public void The_scan_flags_a_surface_call_with_no_pin_before_it()
    {
        Assert.NotEmpty(Offenders("Presentation/Example.cs", ["        RenderingServer.MeshAddSurfaceFromArrays(mesh, RenderingServer.PrimitiveType.Triangles, arrays, blendShapes, lods);"]));
        Assert.Empty(Offenders("Presentation/Example.cs", [
            "        fixed (byte* pa = &KeptCollections.Pin(k.Arrays))",
            "            RenderingServer.MeshAddSurfaceFromArrays(mesh, RenderingServer.PrimitiveType.Triangles, k.Arrays, k.BlendShapes, k.Lods);"]));
    }

    /// <summary>A Godot collection the engine hands one of the host's callbacks (an override such as
    /// <c>Logger._LogError</c>'s script backtraces) is a new managed wrapper holding its own reference to the engine's
    /// collection; left alone, only a finalizer releases it (through the unpinned destroy). Every such parameter must be
    /// handed over for the main thread's pinned disposal (<c>LoggedErrorReleases.Hand</c>).</summary>
    private static readonly Regex CallbackWithCollection = new(
        @"\boverride\s+[\w.<>]+\s+(?<name>_\w+)\s*\((?<params>[^)]*)\)", RegexOptions.Compiled);
    private static readonly Regex CollectionParameter = new(
        @"(?:global::)?Godot\.Collections\.(?:Array|Dictionary)(?:<[^>]*>)?\s+(?<param>\w+)", RegexOptions.Compiled);

    internal static IEnumerable<string> UnreleasedCallbackCollections(string rel, string text)
    {
        foreach (Match m in CallbackWithCollection.Matches(text))
        {
            foreach (Match p in CollectionParameter.Matches(m.Groups["params"].Value))
            {
                // the callback's body: from its signature to the next member's override (or the end of the file)
                int start = m.Index + m.Length;
                int next = text.IndexOf("override ", start, StringComparison.Ordinal);
                string body = text[start..(next < 0 ? text.Length : next)];
                if (!body.Contains($"LoggedErrorReleases.Hand({p.Groups["param"].Value})"))
                    yield return $"{rel}: {m.Groups["name"].Value}'s {p.Groups["param"].Value} is never handed over for release";
            }
        }
    }

    [Fact]
    public void Every_Godot_collection_the_engine_hands_a_callback_is_released()
    {
        string host = Path.Combine(TestPaths.RepoRoot, "host", "Viewer.Godot");
        var found = new List<string>();
        foreach (var file in Directory.EnumerateFiles(host, "*.cs", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(host, file).Replace('\\', '/');
            if (rel.Contains("/obj/") || rel.StartsWith("obj/") || rel.StartsWith("bin/") || rel.StartsWith(".godot/")) continue;
            found.AddRange(UnreleasedCallbackCollections(rel, File.ReadAllText(file)));
        }
        Assert.Empty(found);
        // the logger is the one such callback today: the scan must see it
        string logger = File.ReadAllText(Path.Combine(host, "EngineLogger.cs"));
        Assert.Single(CallbackWithCollection.Matches(logger).Where(m => CollectionParameter.IsMatch(m.Groups["params"].Value)));
    }

    [Fact]
    public void The_callback_scan_flags_the_logger_as_it_was()
    {
        const string before = """
            public override void _LogError(string function, string file, int line, string code, string rationale, bool editorNotify, int errorType,
                global::Godot.Collections.Array<ScriptBacktrace> scriptBacktraces)
            {
                if (_inside || route is null) return;
                _inside = true;
                try { route.Error(errorType, function, file, line, code, rationale); }
                finally { _inside = false; }
            }

            public override void _LogMessage(string message, bool error)
            {
                LoggedErrorReleases.Hand(scriptBacktraces);
            }
            """;
        Assert.Single(UnreleasedCallbackCollections("EngineLogger.cs", before));
        Assert.Empty(UnreleasedCallbackCollections("EngineLogger.cs", before.Replace("    if (_inside", "    LoggedErrorReleases.Hand(scriptBacktraces);\n        if (_inside")));
    }

    [Theory]
    [InlineData("        using var held = new HeldValues();")]
    [InlineData("        var k = KeptCollections.Prims;")]
    [InlineData("        using var self = System.Diagnostics.Process.GetCurrentProcess();")]
    public void The_scan_passes_kept_collections_and_other_disposables(string line)
    {
        Assert.Empty(Offenders("Presentation/Example.cs", [line]));
    }
}
