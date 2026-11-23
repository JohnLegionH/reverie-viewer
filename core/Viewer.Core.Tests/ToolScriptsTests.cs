// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace Viewer.Core.Tests;

/// <summary>
/// Checks that need no grid, for the scripts under tools\ that drive a check in a running viewer: the engine check places each box it makes at an exact
/// position (it runs the script's own NewBox function against stand-in commands and reads what it asked for), and a script that
/// takes a -Token search token never also keeps a variable of that name (PowerShell variable names ignore case, so an
/// automation token kept as $script:token would silently replace the parameter). Each test starts its own child process.
/// </summary>
public sealed class ToolScriptsTests
{
    private readonly ITestOutputHelper _out;
    public ToolScriptsTests(ITestOutputHelper output) { _out = output; }

    private static string Tools => Path.Combine(TestPaths.RepoRoot, "tools");

    private (int Exit, string Output) RunPowerShell(string scriptText, params string[] scriptArgs)
    {
        string folder = TestTemp.NewFolder("tool-scripts");
        string file = Path.Combine(folder, "probe.ps1");
        File.WriteAllText(file, scriptText);
        var args = new List<string> { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", file };
        args.AddRange(scriptArgs);
        var r = ChildProcess.Run("powershell.exe", args, TimeSpan.FromMinutes(2));
        _out.WriteLine($"exit {r.ExitCode}\n{r.Output}");
        return r;
    }

    // Loads the NewBox function out of engine-live.ps1 (without running the script), runs it against stand-ins for the commands it
    // calls and prints one line per call that changes the world: "create x y z", "select <id>", "position x y z".
    private const string NewBoxProbe = """
        param([string]$Script, [double]$Dx)
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($Script, [ref]$null, [ref]$null)
        $fn = $ast.Find({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'NewBox' }, $true)
        if (-not $fn) { Write-Output "NO NewBox"; exit 3 }
        $script:created = $false
        $script:calls = New-Object System.Collections.Generic.List[string]
        function Obs { @{ self = @{ region_position = @(100.0, 50.0, 25.0) } } }
        function NoFocus { }
        function Press { param($k, $d) }
        function ClickCtl { param($n) }
        function Add-Rezzed { param($l) }
        function Start-Sleep { param([int]$Milliseconds, [int]$Seconds) }
        function Wait-Until { param([scriptblock]$s, $t) & $s }
        function Build($a) {
            if ($a.action -eq 'create') { $script:created = $true; $script:calls.Add(("create {0} {1} {2}" -f $a.x, $a.y, $a.z)); return @{} }
            if ($script:created) { return @{ own_objects = @(@{ id = 'box-1'; local = 77 }) } }
            return @{ own_objects = @() }
        }
        function Edit($a) {
            if ($a.action -eq 'state') { return @{ build_mode = $true; contents = @() } }
            if ($a.action -eq 'select') { $script:calls.Add("select " + $a.text) }
            elseif ($a.action -eq 'position') { $script:calls.Add(("position {0} {1} {2}" -f $a.x, $a.y, $a.z)) }
            return @{}
        }
        Invoke-Expression $fn.Extent.Text
        $b = NewBox $Dx
        $script:calls | ForEach-Object { Write-Output $_ }
        if ($b -and $b.local -eq 77) { Write-Output "returned box 77" }
        """;

    [Theory]
    [InlineData(3.0, "position 103 52 25")]
    [InlineData(-4.5, "position 95.5 52 25")]
    [InlineData(12.0, "position 112 52 25")]
    public void The_engine_check_places_each_box_it_makes_at_an_exact_position_after_creating_it(double dx, string expected)
    {
        var r = RunPowerShell(NewBoxProbe, "-Script", Path.Combine(Tools, "engine-live.ps1"), "-Dx", dx.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(0, r.Exit);
        var lines = r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        int create = lines.FindIndex(l => l.StartsWith("create "));
        int select = lines.FindIndex(l => l.StartsWith("select "));
        int position = lines.FindIndex(l => l == expected);
        Assert.True(create >= 0, "the box was never created: " + r.Output);
        Assert.True(position >= 0, $"no exact placement '{expected}' after Create (a Create click ray can land the box anywhere): " + r.Output);
        Assert.True(position > create && position > select, "the box is placed before it exists or is selected: " + r.Output);
        Assert.Contains("returned box 77", r.Output);
    }

    // Loads PickTarget out of engine-live.ps1 and asks it for each compile target of a drop-down like the one of a region that lists SLua
    // among its engines AND runs Lua: two entries are both labelled "SLua (LSL)" (the engine SLua, and the LSL-on-Lua target), so only the target
    // the editor reports after choosing an entry tells them apart. The stand-in drop-down reports the target of the entry last picked.
    // Prints "<target> => <index>" and the entries picked, in order.
    private const string PickTargetProbe = """
        param([string]$Script)
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($Script, [ref]$null, [ref]$null)
        foreach ($name in 'ComboIndex', 'EngineIndex', 'PickTarget') {
            $fn = $ast.Find({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name }, $true)
            if ($fn) { Invoke-Expression $fn.Extent.Text }
        }
        if (-not (Get-Command PickTarget -ErrorAction SilentlyContinue)) { Write-Output "NO PickTarget"; exit 3 }
        $script:targets = @('engine:YEngine', 'engine:Example.Phlox', 'engine:SLua', 'lsl-luau', 'luau', 'engine:Greyed')
        $script:labels = @('YEngine (LSL)', 'Example.Phlox (LSL)', 'SLua (LSL)', 'SLua (LSL)', 'SLua (Lua)', 'Greyed (LSL)')
        $script:picked = New-Object System.Collections.Generic.List[int]
        $script:last = -1
        function Start-Sleep { param([int]$Milliseconds, [int]$Seconds) }
        function SState {
            $items = for ($i = 0; $i -lt $script:labels.Count; $i++) { @{ text = $script:labels[$i]; disabled = ($i -eq 5) } }
            @{ combo_items = @($items); target = $(if ($script:last -ge 0) { $script:targets[$script:last] } else { 'mono' }) }
        }
        function Scr($b) { if ($b.action -eq 'pick') { $script:last = [int]$b.text; $script:picked.Add($script:last) }; SState }
        foreach ($t in 'engine:YEngine', 'engine:Example.Phlox', 'engine:SLua', 'lsl-luau', 'luau', 'engine:Greyed', 'engine:Missing') {
            $script:last = -1; $script:picked.Clear()
            Write-Output ("{0} => {1} (last pick {2})" -f $t, (PickTarget $t), $script:last)
        }
        """;

    [Fact]
    public void The_engine_check_picks_each_compile_target_by_the_target_the_editor_reports_not_by_its_label()
    {
        var r = RunPowerShell(PickTargetProbe, "-Script", Path.Combine(Tools, "engine-live.ps1"));
        Assert.Equal(0, r.Exit);
        Assert.Contains("engine:YEngine => 0 (last pick 0)", r.Output);
        Assert.Contains("engine:Example.Phlox => 1 (last pick 1)", r.Output);
        Assert.Contains("engine:SLua => 2 (last pick 2)", r.Output);
        Assert.Contains("lsl-luau => 3 (last pick 3)", r.Output);     // the second "SLua (LSL)" entry; a label match answered 2
        Assert.Contains("luau => 4 (last pick 4)", r.Output);
        Assert.Contains("engine:Greyed => -1", r.Output);             // greyed entries are never picked
        Assert.Contains("engine:Missing => -1", r.Output);            // not offered: no other engine's entry is used instead
    }
    // For every script under tools\ with a -Token parameter, prints "TOKEN-PARAM <file>"; and for each assignment in it to a
    // variable named token (any case, any scope prefix) outside a function's own parameters, "CLASH <file>:<line>".
    private const string TokenProbe = """
        param([string]$ToolsDir)
        foreach ($f in Get-ChildItem -LiteralPath $ToolsDir -Filter *.ps1) {
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($f.FullName, [ref]$null, [ref]$null)
            if (-not $ast.ParamBlock) { continue }
            $named = @($ast.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -ieq 'token' })
            if ($named.Count -eq 0) { continue }
            Write-Output ("TOKEN-PARAM " + $f.Name)
            $assigns = $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.AssignmentStatementAst] }, $true)
            foreach ($a in $assigns) {
                foreach ($v in $a.Left.FindAll({ param($n) $n -is [System.Management.Automation.Language.VariableExpressionAst] }, $true)) {
                    $name = $v.VariablePath.UserPath -replace '^(script|global|local|private):', ''
                    if ($name -ieq 'token') { Write-Output ("CLASH {0}:{1}" -f $f.Name, $a.Extent.StartLineNumber) }
                }
            }
        }
        """;

    [Fact]
    public void A_script_that_takes_a_Token_parameter_never_assigns_a_variable_of_that_name_and_the_engine_check_takes_one()
    {
        var r = RunPowerShell(TokenProbe, "-ToolsDir", Tools);
        Assert.Equal(0, r.Exit);
        Assert.DoesNotContain("CLASH", r.Output);
        // the engine check's second account searches for the first one's token, so it takes the token as a parameter
        Assert.Contains("TOKEN-PARAM engine-live.ps1", r.Output);
    }
}
