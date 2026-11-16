// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Xml.Linq;

namespace Viewer.Shell.Inventory;

/// <summary>The kinds of word the script editor colours, after the official viewer's colour groups (indra/llui/llkeywords.cpp
/// getColorGroup: functions, controls, events, types, constants, deprecated; comments and string literals by their own rules).</summary>
public enum LslWordKind { Function, Control, Event, Type, Constant, Deprecated }

/// <summary>One word the editor knows: its kind and its tooltip.</summary>
public sealed record LslWord(string Word, LslWordKind Kind, string Tooltip, bool Ossl = false);

/// <summary>
/// The words of LSL and, on OpenSim grids, of OSSL, read from the files this viewer ships unchanged (vendor/lsl-keywords): the official
/// viewer's keyword file keywords_lsl_default.xml (an LLSD map of controls, types, constants, events and functions, each with its tooltip;
/// indra/newview/llsyntaxid.cpp loads it as the default) and Firestorm's OSSL library scriptlibrary_ossl.xml (its integer and float constants
/// and its functions; Firestorm loads it only on OpenSim grids, indra/newview/llpreviewscript.cpp).
/// </summary>
public sealed class LslKeywords
{
    private readonly Dictionary<string, LslWord> _words = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, LslWord> Words => _words;

    public LslWord? Find(string word) => null;

    /// <summary>Reads the official keyword file (LLSD XML).</summary>
    public static LslKeywords FromLsl(Stream llsd) => new();

    /// <summary>Adds Firestorm's OSSL library (its legacy script_library format). Words LSL already has keep their LSL meaning.</summary>
    public void AddOssl(Stream library) { }

    /// <summary>The words the viewer ships with, read once: LSL, and OSSL when <paramref name="openSim"/>.</summary>
    public static LslKeywords Shipped(bool openSim) => new();
}
