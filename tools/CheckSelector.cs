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
        public static CheckSelection Select(IEnumerable<string> changedFiles, string mapText, string areasText)
        {
            return new CheckSelection();
        }

        public static bool Picks(CheckSelection selection, string caseTag)
        {
            return false;
        }
    }
}
