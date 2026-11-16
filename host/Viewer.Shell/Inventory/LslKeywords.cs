// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Xml;
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

    public LslWord? Find(string word) => _words.GetValueOrDefault(word);

    private static readonly XmlReaderSettings Safe = new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };

    /// <summary>Reads the official keyword file (LLSD XML): each section's map of words, a word marked "deprecated" taking that colour.</summary>
    public static LslKeywords FromLsl(Stream llsd)
    {
        var k = new LslKeywords();
        using var reader = XmlReader.Create(llsd, Safe);
        var top = XDocument.Load(reader).Root?.Element("map");
        if (top is null)
            return k;
        foreach (var (section, body) in Pairs(top))
        {
            LslWordKind? kind = section switch
            {
                "controls" => LslWordKind.Control, "types" => LslWordKind.Type, "constants" => LslWordKind.Constant,
                "events" => LslWordKind.Event, "functions" => LslWordKind.Function, _ => null,
            };
            if (kind is null || body.Name != "map")
                continue;
            foreach (var (word, entry) in Pairs(body))
            {
                string tooltip = "";
                bool deprecated = false;
                foreach (var (key, value) in Pairs(entry))
                {
                    if (key == "tooltip")
                        tooltip = value.Value.Replace("\\n", "\n", StringComparison.Ordinal);
                    else if (key == "deprecated")
                        deprecated = value.Name == "boolean" ? value.Value is "1" or "true" : true;
                }
                k._words[word] = new LslWord(word, deprecated ? LslWordKind.Deprecated : kind.Value, tooltip);
            }
        }
        return k;
    }

    /// <summary>The key and value pairs of an LLSD map element.</summary>
    private static IEnumerable<(string Key, XElement Value)> Pairs(XElement map)
    {
        XElement? key = null;
        foreach (var e in map.Elements())
        {
            if (e.Name == "key")
                key = e;
            else if (key is not null)
            {
                yield return (key.Value, e);
                key = null;
            }
        }
    }

    /// <summary>Adds Firestorm's OSSL library (its legacy script_library format). Words LSL already has keep their LSL meaning.</summary>
    public void AddOssl(Stream library)
    {
        using var reader = XmlReader.Create(library, Safe);
        var root = XDocument.Load(reader).Root;
        if (root is null)
            return;
        foreach (var e in root.Descendants())
        {
            LslWordKind? kind = e.Name.LocalName switch
            {
                "function" => LslWordKind.Function,
                "integer_constant" or "float_constant" or "string_constant" or "key_constant" or "vector_constant" or "rotation_constant" => LslWordKind.Constant,
                _ => null,
            };
            if (kind is null || e.Attribute("name")?.Value is not { Length: > 0 } name || _words.ContainsKey(name))
                continue;
            _words[name] = new LslWord(name, kind.Value, (e.Attribute("desc")?.Value ?? "").Replace(";", "\n", StringComparison.Ordinal), Ossl: true);
        }
    }

    private static readonly Lazy<LslKeywords> ShippedLsl = new(() => Load(false)), ShippedOpenSim = new(() => Load(true));

    /// <summary>The words the viewer ships with, read once: LSL, and OSSL when <paramref name="openSim"/>.</summary>
    public static LslKeywords Shipped(bool openSim) => openSim ? ShippedOpenSim.Value : ShippedLsl.Value;

    private static LslKeywords Load(bool openSim)
    {
        var asm = typeof(LslKeywords).Assembly;
        using var lsl = asm.GetManifestResourceStream("Viewer.Shell.Keywords.keywords_lsl_default.xml");
        var k = lsl is null ? new LslKeywords() : FromLsl(lsl);
        if (openSim)
        {
            using var ossl = asm.GetManifestResourceStream("Viewer.Shell.Keywords.scriptlibrary_ossl.xml");
            if (ossl is not null)
                k.AddOssl(ossl);
        }
        return k;
    }
}
