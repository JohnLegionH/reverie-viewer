// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The automation API and the fault-injection hook are absent from the SHIPPING export (no type, no enable path) and present in the development
/// export (the negative control: the scan can see it). Scans the exports tools\s1.ps1 has just made; it sets
/// NV_EXPORT_SCAN=1 and runs this class after exporting, so a stale export is never scanned. Otherwise a real SKIP.
/// </summary>
public class ExportScanTests
{
    private static string Shipping => Path.Combine(TestPaths.GodotBuildDir, "data_Viewer.Godot_windows_x86_64");
    private static string Development => Path.Combine(TestPaths.GodotBuildDir, "dev", "data_Viewer.Godot_windows_x86_64");

    internal sealed record Found(List<string> Types, List<string> Strings, List<string> Files, List<string> Calls, List<string> Switches);

    /// <summary>Development-only switches (the automation command that holds a neighbour back from an advance connect): their names must be
    /// in no string of a shipping export.</summary>
    private static readonly string[] DevOnlySwitches = ["neighbour_debug"];

    internal static Found Scan(string dataDir, string pck)
    {
        var types = new List<string>();
        var strings = new List<string>();
        var calls = new List<string>();
        var switches = new List<string>();
        var files = Directory.EnumerateFiles(dataDir, "*Automation*", SearchOption.AllDirectories).Select(Path.GetFileName).ToList()!;
        foreach (var dll in Directory.EnumerateFiles(dataDir, "Viewer.*.dll"))
        {
            using var asm = AssemblyDefinition.ReadAssembly(dll);
            foreach (var t in asm.MainModule.GetTypes())
            {
                if (t.FullName.Contains("Automation", StringComparison.OrdinalIgnoreCase))
                    types.Add(Path.GetFileName(dll) + ":" + t.FullName);
                foreach (var m in t.Methods.Where(m => m.HasBody))
                {
                    foreach (var i in m.Body.Instructions.Where(i => i.OpCode == OpCodes.Ldstr))
                    {
                        if (((string)i.Operand).Contains("--automation", StringComparison.Ordinal) || ((string)i.Operand).Contains("drop_inbound", StringComparison.Ordinal))
                            strings.Add(Path.GetFileName(dll) + ":" + t.FullName + "." + m.Name);
                        foreach (var name in DevOnlySwitches)
                            if (((string)i.Operand).Contains(name, StringComparison.Ordinal))
                                switches.Add(Path.GetFileName(dll) + ":" + t.FullName + "." + m.Name + ":" + name);
                    }
                    // the development-only command that opens any window by name
                    foreach (var i in m.Body.Instructions.Where(i => i.OpCode == OpCodes.Ldstr))
                        if (string.Equals((string)i.Operand, "open_window", StringComparison.Ordinal))
                            strings.Add("open_window@" + Path.GetFileName(dll) + ":" + t.FullName + "." + m.Name);
                    // the circuit gate (a fault command's only door): any call to the session's method for it is a caller
                    foreach (var i in m.Body.Instructions.Where(i => i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt))
                        if (i.Operand?.ToString()?.Contains("CloseInboundCircuit", StringComparison.Ordinal) == true)
                            calls.Add(Path.GetFileName(dll) + ":" + t.FullName + "." + m.Name);
                }
            }
        }
        // The pack: no automation script path either (the shipping preset excludes Automation/*).
        var pckBytes = File.ReadAllBytes(pck);
        if (Contains(pckBytes, Encoding.UTF8.GetBytes("AutomationHost")))
            files.Add(Path.GetFileName(pck) + ":AutomationHost");
        return new(types, strings, files!, calls, switches);
    }

    private static bool Contains(byte[] hay, byte[] needle)
    {
        for (int i = 0; i <= hay.Length - needle.Length; i++)
            if (hay.AsSpan(i, needle.Length).SequenceEqual(needle))
                return true;
        return false;
    }

    [ExportScanFact]
    public void The_shipping_export_contains_no_automation_type_string_or_file()
    {
        var f = Scan(Shipping, Path.Combine(TestPaths.GodotBuildDir, "Reverie.pck"));
        Assert.Empty(f.Types);
        Assert.Empty(f.Strings);
        Assert.Empty(f.Files);
        Assert.Empty(f.Calls);   // nothing in a shipping build closes a circuit
        Assert.Empty(f.Switches);
    }

    [ExportScanFact]
    public void Negative_control_the_development_export_does_contain_it()
    {
        var f = Scan(Development, Path.Combine(TestPaths.GodotBuildDir, "dev", "Reverie.pck"));
        Assert.Contains(f.Types, t => t.EndsWith("Viewer.Automation.AutomationServer", StringComparison.Ordinal));
        Assert.Contains(f.Types, t => t.EndsWith("Viewer.Godot.AutomationHost", StringComparison.Ordinal));
        Assert.Contains(f.Strings, s => s.Contains("AutomationArgs", StringComparison.Ordinal));
        // the development-only fault command: it names its action and calls the session's circuit gate (the shipping scan above finds neither)
        Assert.Contains(f.Strings, s => s.Contains("Main.FaultCall", StringComparison.Ordinal));
        Assert.Contains(f.Calls, c => c.Contains("Main.FaultCall", StringComparison.Ordinal));
        Assert.Contains(f.Files, s => s.EndsWith(":AutomationHost", StringComparison.Ordinal));
        // the open-window command is there too, so the shipping scan's silence about it means something
        Assert.Contains(f.Strings, s => s.StartsWith("open_window@", StringComparison.Ordinal));
        Assert.Contains(f.Switches, s => s.EndsWith(":neighbour_debug", StringComparison.Ordinal));   // so the shipping scan above can see it
    }
}

public sealed class ExportScanFactAttribute : FactAttribute
{
    public ExportScanFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("NV_EXPORT_SCAN") != "1")
            Skip = "export scan runs from tools\\s1.ps1 right after it exports (NV_EXPORT_SCAN=1), never against a stale export";
    }
}
