// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text.Json.Nodes;
using LibreMetaverse.StructuredData;
using Viewer.Core.Protocol;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The LLSD notation reader the GLTF override messages are read with: every value kind, the shapes the library's own
/// parser gets wrong, and refusals of malformed or too deeply nested input.
/// </summary>
public class LlsdNotationTests
{
    [Fact]
    public void Every_value_kind_reads_as_json()
    {
        var o = LlsdNotation.Parse(
            "{'u':!, 'i':i-42, 'r':r1.5e2, 'ok':true, 'no':0, 't':T, 'id':u6f1d7c1e-3a52-4d1b-9a0e-2c55f0b7a101, " +
            "'q':\"a\\\"b\\x41\", 'sq':'it\\'s', 's':s(3)\"x,y\", 'l':l\"http://example.org/\", 'd':d\"2026-01-01T00:00:00Z\", " +
            "'b16':b16\"0aff\", 'b64':b64\"AQID\", 'nan':rnan, 'arr':[i1,r2,[]], 'map':{}}")!.AsObject();
        Assert.Null(o["u"]);
        Assert.Equal(-42, (long)o["i"]!);
        Assert.Equal(150.0, (double)o["r"]!);
        Assert.True((bool)o["ok"]!);
        Assert.False((bool)o["no"]!);
        Assert.True((bool)o["t"]!);
        Assert.Equal("6f1d7c1e-3a52-4d1b-9a0e-2c55f0b7a101", (string)o["id"]!);
        Assert.Equal("a\"bA", (string)o["q"]!);
        Assert.Equal("it's", (string)o["sq"]!);
        Assert.Equal("x,y", (string)o["s"]!);
        Assert.Equal("http://example.org/", (string)o["l"]!);
        Assert.Equal("2026-01-01T00:00:00Z", (string)o["d"]!);
        Assert.Equal(Convert.ToBase64String([0x0a, 0xff]), (string)o["b16"]!);
        Assert.Equal("AQID", (string)o["b64"]!);
        Assert.Null(o["nan"]);   // JSON has no NaN
        Assert.Equal(3, o["arr"]!.AsArray().Count);
        Assert.Empty(o["map"]!.AsObject());
    }

    /// <summary>
    /// The shapes an override message uses to clear overrides: an empty array followed by more entries, and an empty map
    /// followed by more entries. Read whole here.
    /// </summary>
    [Fact]
    public void Empty_containers_followed_by_more_entries_read_whole()
    {
        var clear = LlsdNotation.Parse("{'id':i77,'te':[],'od':[]}")!.AsObject();
        Assert.Equal(77, (long)clear["id"]!);
        Assert.Empty(clear["te"]!.AsArray());
        Assert.Empty(clear["od"]!.AsArray());
        var m = LlsdNotation.Parse("{'a':{},'b':i1}")!.AsObject();
        Assert.Equal(1, (long)m["b"]!);
    }

    /// <summary>
    /// Why the reader exists: LibreMetaverse 3.1.6's notation parser threw on the clearing message and silently dropped
    /// the entry after an empty map. LibreMetaverse 3.3.0 parses both shapes, so this test now holds that the library and
    /// the reader agree on them; the reader stays for its own limits and error type (see the tests around this one).
    /// </summary>
    [Fact]
    public void The_library_parser_now_reads_these_shapes_as_the_reader_does()
    {
        var cleared = OSDParser.DeserializeLLSDNotation("{'id':i77,'te':[],'od':[]}");
        Assert.IsType<OSDMap>(cleared);
        var kept = (OSDMap)OSDParser.DeserializeLLSDNotation("{'a':{},'b':i1}");
        Assert.True(kept.ContainsKey("b"));
    }

    [Theory]
    [InlineData("{'a':i1")]                  // unterminated map
    [InlineData("['x]")]                     // unterminated string
    [InlineData("{'a' i1}")]                 // no colon
    [InlineData("{i1:i2}")]                  // a key that is not a string
    [InlineData("[i1 i2]")]                  // no comma
    [InlineData("ux-not-a-uuid-at-all-not-a-uuid-at-a")]
    [InlineData("s(10)\"short\"")]           // a length longer than the string
    [InlineData("i1 i2")]                    // trailing characters
    [InlineData("?")]
    public void Malformed_input_is_refused(string text) => Assert.Throws<FormatException>(() => LlsdNotation.Parse(text));

    [Fact]
    public void Nesting_deeper_than_the_bound_is_refused()
    {
        string ok = new string('[', LlsdNotation.MaxDepth) + new string(']', LlsdNotation.MaxDepth);
        Assert.IsType<JsonArray>(LlsdNotation.Parse(ok));
        string deep = new string('[', LlsdNotation.MaxDepth + 2) + new string(']', LlsdNotation.MaxDepth + 2);
        Assert.Throws<FormatException>(() => LlsdNotation.Parse(deep));
    }
}
