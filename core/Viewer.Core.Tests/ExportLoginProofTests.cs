// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Security.Cryptography;
using Mono.Cecil;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The shipping export can log in because it carries the same login code as the development export, which is the one the
/// login tests drive. Compares the two exports tools\s1.ps1 has just made (NV_EXPORT_SCAN=1, as ExportScanTests):
/// every library the login path uses is byte-identical; the viewer's own core and shell assemblies hold the same types,
/// members and method bodies (instruction by instruction), so nothing in the login path exists in one export only. Only the
/// host assembly differs (the development export compiles the automation API in), and the scan next to this one proves
/// the shipping export has none of it.
/// </summary>
public class ExportLoginProofTests
{
    private static string Shipping => Path.Combine(TestPaths.GodotBuildDir, "data_Viewer.Godot_windows_x86_64");
    private static string Development => Path.Combine(TestPaths.GodotBuildDir, "dev", "data_Viewer.Godot_windows_x86_64");

    /// <summary>The assemblies the login path runs: the protocol library, its type and data libraries, and the CoreJ2K image codec.</summary>
    private static IEnumerable<string> LibraryNames() =>
        Directory.EnumerateFiles(Development, "*.dll").Select(Path.GetFileName).Cast<string>()
            .Where(n => !n.StartsWith("Viewer.", StringComparison.Ordinal) && !n.StartsWith("GodotSharp", StringComparison.Ordinal) && !n.StartsWith("System.", StringComparison.Ordinal) && !n.StartsWith("Microsoft.", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal);

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>Every method of every type, as "type::method(signature)" to its instruction text, so two builds compare by what they do.</summary>
    internal static SortedDictionary<string, string> Content(string dll)
    {
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        using var asm = AssemblyDefinition.ReadAssembly(dll);
        foreach (var t in asm.MainModule.GetTypes())
        {
            map["type " + t.FullName] = string.Join(",", t.Interfaces.Select(i => i.InterfaceType.FullName)) + "|" + t.BaseType?.FullName;
            foreach (var f in t.Fields) map["field " + f.FullName] = f.Attributes.ToString();
            foreach (var m in t.Methods)
                map["method " + m.FullName] = m.HasBody ? string.Join(";", m.Body.Instructions.Select(i => i.OpCode.Code + " " + i.Operand)) : "(no body)";
        }
        return map;
    }

    [ExportScanFact]
    public void The_login_libraries_are_byte_identical_in_both_exports()
    {
        var names = LibraryNames().ToList();
        Assert.Contains("LibreMetaverse.dll", names);
        foreach (var name in names)
        {
            string ship = Path.Combine(Shipping, name);
            Assert.True(File.Exists(ship), name + " is missing from the shipping export");
            Assert.Equal(Sha(Path.Combine(Development, name)), Sha(ship));
        }
        // and the shipping export carries nothing the development one lacks
        var shippingOnly = Directory.EnumerateFiles(Shipping, "*.dll").Select(Path.GetFileName).Except(Directory.EnumerateFiles(Development, "*.dll").Select(Path.GetFileName));
        Assert.Empty(shippingOnly);
    }

    [ExportScanTheory]
    [InlineData("Viewer.Core.dll")]
    [InlineData("Viewer.Shell.dll")]
    public void The_viewers_core_and_login_assemblies_hold_the_same_content_in_both_exports(string name)
    {
        var ship = Content(Path.Combine(Shipping, name));
        var dev = Content(Path.Combine(Development, name));
        Assert.True(ship.Count > 100, name + " read as empty");
        Assert.Equal(dev.Keys, ship.Keys);
        var different = dev.Where(kv => ship[kv.Key] != kv.Value).Select(kv => kv.Key).Take(10).ToList();
        Assert.Empty(different);
    }

    /// <summary>Negative control: the same comparison does see a difference where there is one (the host assembly, which only the
    /// development export compiles the automation API into).</summary>
    [ExportScanFact]
    public void Negative_control_the_comparison_sees_the_host_assemblys_difference()
    {
        var ship = Content(Path.Combine(Shipping, "Viewer.Godot.dll"));
        var dev = Content(Path.Combine(Development, "Viewer.Godot.dll"));
        Assert.NotEqual(dev.Keys, ship.Keys);
    }
}

public sealed class ExportScanTheoryAttribute : TheoryAttribute
{
    public ExportScanTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("NV_EXPORT_SCAN") != "1")
            Skip = "export scan runs from tools\\s1.ps1 right after it exports (NV_EXPORT_SCAN=1), never against a stale export";
    }
}
