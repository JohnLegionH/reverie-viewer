// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text.RegularExpressions;
using Mono.Cecil;
using Xunit;
using Xunit.Abstractions;

namespace Viewer.Core.Tests;

/// <summary>
/// L0 architecture rules (ADR-005, ADR-015):
///   1. the core references no Godot assembly and exposes no Godot type;
///   2. LibreMetaverse types appear only in *.Protocol namespaces;
///   3. nothing calls the transport's command side (ICommandTransport) except the action pipeline.
/// The SAME rules run against Viewer.Core.ArchFixture, which breaks all three on purpose: the permanent negative control.
/// </summary>
public class ArchitectureTests
{
    private readonly ITestOutputHelper _out;
    public ArchitectureTests(ITestOutputHelper output) { _out = output; }

    [Fact]
    public void Viewer_Core_obeys_the_architecture_rules()
    {
        var report = ArchRules.Check(TestPaths.CoreDll);
        foreach (var v in report.Violations)
            _out.WriteLine(v.ToString());
        _out.WriteLine($"Viewer.Core: {report.Violations.Count} violation(s); LibreMetaverse uses inside *.Protocol: {report.ProtocolLmvUses}");

        Assert.Empty(report.Violations);
        // Not vacuous: the scanner does see the core's legitimate LibreMetaverse use inside Viewer.Core.Protocol.
        Assert.True(report.ProtocolLmvUses > 0, "the scanner saw no LibreMetaverse use at all in Viewer.Core.Protocol");
        // Not vacuous either: the scanner does see the pipeline's own (one permitted) call of the command side.
        Assert.True(report.PipelineCommandCalls > 0, "the scanner saw no call of ICommandTransport in the action pipeline");
    }

    [Fact]
    public void The_host_assembly_that_holds_the_interface_logic_has_no_protocol_library_and_no_command_calls()
    {
        var report = ArchRules.Check(typeof(Viewer.Shell.StringTable).Assembly.Location);
        foreach (var v in report.Violations)
            _out.WriteLine(v.ToString());
        Assert.Empty(report.Violations);
        Assert.Equal(0, report.ProtocolLmvUses);
        Assert.Equal(0, report.PipelineCommandCalls);
    }

    [Fact]
    public void Negative_control_fixture_is_flagged_for_both_violations()
    {
        Assert.True(File.Exists(TestPaths.ArchFixtureDll), $"fixture not built: {TestPaths.ArchFixtureDll}");
        var report = ArchRules.Check(TestPaths.ArchFixtureDll);
        foreach (var v in report.Violations)
            _out.WriteLine(v.ToString());

        Assert.Contains(report.Violations, v => v.Rule == ArchRule.GodotAssemblyReferenced);
        Assert.Contains(report.Violations, v => v.Rule == ArchRule.GodotTypeExposed);
        Assert.Contains(report.Violations, v => v.Rule == ArchRule.LibreMetaverseOutsideProtocol);
        Assert.Contains(report.Violations, v => v.Rule == ArchRule.CommandBypassesPipeline);
    }
}

public enum ArchRule
{
    GodotAssemblyReferenced,
    GodotTypeExposed,
    LibreMetaverseOutsideProtocol,
    CommandBypassesPipeline,
}

public sealed record ArchViolation(ArchRule Rule, string Where, string What)
{
    public override string ToString() => $"VIOLATION {Rule}: {Where} -> {What}";
}

/// <param name="PipelineCommandCalls">How many calls of the command side the action pipeline makes (the permitted ones).</param>
public sealed record ArchReport(IReadOnlyList<ArchViolation> Violations, int ProtocolLmvUses, int PipelineCommandCalls = 0);

/// <summary>Reads an assembly's metadata and IL with Mono.Cecil (nothing is loaded or executed).</summary>
public static class ArchRules
{
    private static readonly Regex ProtocolNamespace = new(@"(^|\.)Protocol(\.|$)", RegexOptions.Compiled);

    private static bool IsGodotAssembly(string name) => name.StartsWith("Godot", StringComparison.OrdinalIgnoreCase);

    private static bool IsLmvAssembly(string name) => name.StartsWith("LibreMetaverse", StringComparison.OrdinalIgnoreCase);

    public static ArchReport Check(string assemblyPath)
    {
        var violations = new List<ArchViolation>();
        int protocolLmvUses = 0, pipelineCommandCalls = 0;
        using var asm = AssemblyDefinition.ReadAssembly(assemblyPath);

        foreach (var module in asm.Modules)
        {
            foreach (var r in module.AssemblyReferences.Where(r => IsGodotAssembly(r.Name)))
                violations.Add(new(ArchRule.GodotAssemblyReferenced, asm.Name.Name, r.FullName));

            foreach (var type in AllTypes(module.Types))
            {
                string ns = TopLevel(type).Namespace;
                bool inProtocol = ProtocolNamespace.IsMatch(ns);

                // Rule 3 (ADR-015): a call, or a delegate made from a method group, of the command side belongs to the pipeline alone.
                foreach (var m in type.Methods.Where(m => m.HasBody))
                    foreach (var ins in m.Body.Instructions)
                        if (ins.Operand is MethodReference mr && mr.DeclaringType?.FullName == CommandInterface)
                        {
                            if (TopLevel(type).FullName == PipelineType)
                                pipelineCommandCalls++;
                            else
                                violations.Add(new(ArchRule.CommandBypassesPipeline, $"{type.FullName}::{m.Name}", mr.FullName));
                        }

                foreach (var (use, member, isPublicSurface) in ReferencedTypes(type))
                {
                    string owner = OwnerAssembly(use, module);
                    if (IsGodotAssembly(owner) && isPublicSurface)
                        violations.Add(new(ArchRule.GodotTypeExposed, $"{type.FullName}::{member}", use.FullName));
                    if (IsLmvAssembly(owner))
                    {
                        if (inProtocol)
                            protocolLmvUses++;
                        else
                            violations.Add(new(ArchRule.LibreMetaverseOutsideProtocol, $"{type.FullName}::{member}", use.FullName));
                    }
                }
            }
        }

        // One line per distinct violation.
        return new ArchReport(violations.Distinct().ToList(), protocolLmvUses, pipelineCommandCalls);
    }

    /// <summary>The transport's command side, and the one type allowed to call it.</summary>
    private const string CommandInterface = "Viewer.Core.Session.ICommandTransport", PipelineType = "Viewer.Core.Actions.ActionPipeline";

    private static TypeDefinition TopLevel(TypeDefinition t)
    {
        while (t.DeclaringType != null)
            t = t.DeclaringType;
        return t;
    }

    private static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types)
    {
        foreach (var t in types)
        {
            yield return t;
            foreach (var n in AllTypes(t.NestedTypes))
                yield return n;
        }
    }

    private static string OwnerAssembly(TypeReference t, ModuleDefinition self) => t.Scope switch
    {
        AssemblyNameReference a => a.Name,
        ModuleDefinition m => m.Assembly.Name.Name,
        _ => self.Assembly.Name.Name,
    };

    private static bool IsVisible(TypeDefinition t) =>
        t.IsPublic || (t.IsNestedPublic && t.DeclaringType != null && IsVisible(t.DeclaringType));

    /// <summary>Every type a type touches: signatures, attributes, locals and IL operands, generics unwrapped.</summary>
    private static IEnumerable<(TypeReference Use, string Member, bool PublicSurface)> ReferencedTypes(TypeDefinition type)
    {
        bool visible = IsVisible(type);
        var found = new List<(TypeReference, string, bool)>();
        void Add(TypeReference? t, string member, bool surface)
        {
            foreach (var u in Unwrap(t))
                found.Add((u, member, surface));
        }

        Add(type.BaseType, "(base)", visible);
        foreach (var i in type.Interfaces)
            Add(i.InterfaceType, "(interface)", visible);
        foreach (var a in type.CustomAttributes)
            Add(a.AttributeType, "(attribute)", visible);

        foreach (var f in type.Fields)
            Add(f.FieldType, f.Name, visible && (f.IsPublic || f.IsFamily || f.IsFamilyOrAssembly));
        foreach (var p in type.Properties)
            Add(p.PropertyType, p.Name, visible && new[] { p.GetMethod, p.SetMethod }.Any(m => m != null && (m.IsPublic || m.IsFamily || m.IsFamilyOrAssembly)));
        foreach (var e in type.Events)
            Add(e.EventType, e.Name, visible && e.AddMethod != null && (e.AddMethod.IsPublic || e.AddMethod.IsFamily));

        foreach (var m in type.Methods)
        {
            bool surface = visible && (m.IsPublic || m.IsFamily || m.IsFamilyOrAssembly);
            Add(m.ReturnType, m.Name, surface);
            foreach (var p in m.Parameters)
                Add(p.ParameterType, m.Name, surface);
            foreach (var a in m.CustomAttributes)
                Add(a.AttributeType, m.Name, surface);
            if (!m.HasBody)
                continue;
            foreach (var v in m.Body.Variables)
                Add(v.VariableType, m.Name, false);
            foreach (var ins in m.Body.Instructions)
            {
                switch (ins.Operand)
                {
                    case TypeReference t:
                        Add(t, m.Name, false);
                        break;
                    case MethodReference mr:
                        Add(mr.DeclaringType, m.Name, false);
                        Add(mr.ReturnType, m.Name, false);
                        foreach (var p in mr.Parameters)
                            Add(p.ParameterType, m.Name, false);
                        break;
                    case FieldReference fr:
                        Add(fr.DeclaringType, m.Name, false);
                        Add(fr.FieldType, m.Name, false);
                        break;
                }
            }
        }
        return found;
    }

    private static IEnumerable<TypeReference> Unwrap(TypeReference? t)
    {
        if (t == null || t is GenericParameter)
            yield break;
        if (t is GenericInstanceType g)
        {
            foreach (var u in Unwrap(g.ElementType))
                yield return u;
            foreach (var a in g.GenericArguments)
                foreach (var u in Unwrap(a))
                    yield return u;
            yield break;
        }
        if (t is TypeSpecification spec)
        {
            foreach (var u in Unwrap(spec.ElementType))
                yield return u;
            yield break;
        }
        yield return t;
    }
}
