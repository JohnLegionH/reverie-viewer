// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

// Which checks an order's changes need (tools\select-checks.ps1). Compiled by that script with Add-Type (Windows PowerShell 5.1,
// so it sticks to C# 5) and linked into the unit tests, so the choice the script makes is the choice the tests check.

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace NvTools
{
    /// <summary>One area of tools\check-areas.tsv.</summary>
    public sealed class CheckArea
    {
        public string Name;
        public string Cases;       // a regular expression on a render case's tag, or null for none
        public string[] Backends;  // where those cases run
        public bool Smoke;         // whether the stock smoke runs
    }

    /// <summary>What a set of changed files selects.</summary>
    public sealed class CheckSelection
    {
        public bool Full;
        public bool Smoke;
        public List<string> Areas = new List<string>();
        public string Cases;       // every selected area's cases together (null when none)
        public List<string> Backends = new List<string>();
        public Dictionary<string, string> CasesByBackend = new Dictionary<string, string>();   // backend -> the cases to run on it
        public List<string> Unplaced = new List<string>();
        public Dictionary<string, List<string>> FileAreas = new Dictionary<string, List<string>>();
    }

    public static class CheckSelector
    {
        private sealed class Rule { public Regex Pattern; public string[] Areas; }

        private static IEnumerable<string[]> Rows(string text)
        {
            foreach (string raw in (text ?? "").Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.Length == 0 || line.StartsWith("#")) continue;
                yield return line.Split('\t');
            }
        }

        private static Dictionary<string, CheckArea> ReadAreas(string areasText)
        {
            var areas = new Dictionary<string, CheckArea>();
            foreach (string[] c in Rows(areasText))
            {
                if (c.Length < 4) throw new ArgumentException("an area line needs four columns: " + string.Join("\t", c));
                areas[c[0]] = new CheckArea
                {
                    Name = c[0],
                    Cases = c[1] == "-" ? null : TagPattern(c[1]),
                    Backends = c[2] == "-" ? new string[0] : c[2].Split(','),
                    Smoke = c[3] == "yes",
                };
            }
            if (!areas.ContainsKey("full")) throw new ArgumentException("the areas file has no 'full' area");
            return areas;
        }

        /// <summary>
        /// An area's pattern as render.ps1's case filter needs it: that filter reads the picture's tag, which writes the case's name with
        /// hyphens (shared-env-vulkan for shared_env_vulkan) and puts "proposal-" before it while no golden exists. So an underscore in the
        /// pattern stands for either, and a pattern that starts with "^(" also allows the prefix.
        /// </summary>
        public static string TagPattern(string pattern)
        {
            string p = pattern.Replace("_", "[-_]");
            if (p.StartsWith("^(")) p = "^(?:proposal[-_])?(" + p.Substring(2);
            return p;
        }

        private static List<Rule> ReadMap(string mapText)
        {
            var rules = new List<Rule>();
            foreach (string[] c in Rows(mapText))
            {
                if (c.Length != 2) throw new ArgumentException("a map line needs two columns: " + string.Join("\t", c));
                rules.Add(new Rule { Pattern = new Regex(c[0], RegexOptions.CultureInvariant), Areas = c[1].Split(',') });
            }
            return rules;
        }

        /// <summary>
        /// The checks <paramref name="changedFiles"/> (paths in the repository, either slash) need. Each file takes the areas of every
        /// map rule it matches; a file that matches none selects the full set and is listed as unplaced. The selection is the union of
        /// the files' areas: their cases (per backend), and the stock smoke if any of them runs it.
        /// </summary>
        public static CheckSelection Select(IEnumerable<string> changedFiles, string mapText, string areasText)
        {
            var areas = ReadAreas(areasText);
            var rules = ReadMap(mapText);
            var s = new CheckSelection();
            var perBackend = new Dictionary<string, List<string>>();
            var all = new List<string>();
            foreach (string f0 in changedFiles)
            {
                if (string.IsNullOrEmpty(f0)) continue;
                string f = f0.Replace('\\', '/');
                var mine = new List<string>();
                foreach (Rule r in rules)
                    if (r.Pattern.IsMatch(f))
                        foreach (string a in r.Areas) if (!mine.Contains(a)) mine.Add(a);
                if (mine.Count == 0) { mine.Add("full"); s.Unplaced.Add(f); }
                s.FileAreas[f] = mine;
                foreach (string a in mine)
                {
                    if (a == "none") continue;
                    CheckArea area;
                    if (!areas.TryGetValue(a, out area)) throw new ArgumentException("the map names an area the areas file does not have: " + a);
                    if (!s.Areas.Contains(a)) s.Areas.Add(a);
                    if (a == "full") s.Full = true;
                    if (area.Smoke) s.Smoke = true;
                    if (area.Cases == null) continue;
                    if (!all.Contains(area.Cases)) all.Add(area.Cases);
                    foreach (string b in area.Backends)
                    {
                        List<string> list;
                        if (!perBackend.TryGetValue(b, out list)) { list = new List<string>(); perBackend[b] = list; }
                        if (!list.Contains(area.Cases)) list.Add(area.Cases);
                        if (!s.Backends.Contains(b)) s.Backends.Add(b);
                    }
                }
            }
            if (s.Full)
            {
                CheckArea full = areas["full"];
                s.Smoke = s.Smoke || full.Smoke;
                s.Backends = new List<string>(full.Backends);
                s.Cases = full.Cases;
                foreach (string b in full.Backends) s.CasesByBackend[b] = full.Cases;
                return s;
            }
            if (all.Count > 0) s.Cases = Join(all);
            foreach (var kv in perBackend) s.CasesByBackend[kv.Key] = Join(kv.Value);
            return s;
        }

        private static string Join(List<string> patterns)
        {
            if (patterns.Count == 1) return patterns[0];
            var parts = new List<string>();
            foreach (string p in patterns) parts.Add("(?:" + p + ")");
            return string.Join("|", parts.ToArray());
        }

        /// <summary>
        /// Whether <paramref name="selection"/> runs the render case <paramref name="caseTag"/>, by its name or its picture's tag (either
        /// ends in its backend, _vulkan or -d3d12, sometimes with one more word after it; one without, such as refuse_opengl3, runs with the
        /// full set only).
        /// </summary>
        public static List<string> RenderLines(CheckSelection selection) { return new List<string>(); }

        public static bool Picks(CheckSelection selection, string caseTag)
        {
            if (selection == null || string.IsNullOrEmpty(caseTag)) return false;
            if (selection.Full) return true;
            Match m = Regex.Match(caseTag, "[-_](vulkan|d3d12)([-_][a-z]+)?$");
            if (!m.Success) return false;
            string cases;
            if (!selection.CasesByBackend.TryGetValue(m.Groups[1].Value, out cases) || cases == null) return false;
            return Regex.IsMatch(caseTag, cases);
        }
    }
}
