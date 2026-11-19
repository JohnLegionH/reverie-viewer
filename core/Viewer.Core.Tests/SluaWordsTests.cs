// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text;
using Viewer.Core.Assets;
using Viewer.Core.Inventory;
using Viewer.Shell.Inventory;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The words of an SLua (Lua) script: the Luau reserved words and the ll, ossl, iw and bot functions read from the list generated from the
/// SLua engine's function tables, their tooltips found by table ("ll.Say"), and when the editor treats a script as Lua. Pure data; no shared
/// state, so the class runs in parallel.
/// </summary>
public class SluaWordsTests
{
    private static readonly LslKeywords Slua = LslKeywords.ShippedSlua();

    [Fact]
    public void The_shipped_list_holds_the_luau_reserved_words_and_the_four_function_tables()
    {
        Assert.True(Slua.IsSlua);
        foreach (var word in new[] { "and", "break", "do", "else", "elseif", "end", "false", "for", "function", "if", "in", "local", "nil", "not", "or", "repeat", "return", "then", "true", "until", "while" })
            Assert.Equal(LslWordKind.Control, Slua.Find(word)?.Kind);
        foreach (var table in new[] { "ll.", "ossl.", "iw.", "bot." })
            Assert.True(Slua.Words.Keys.Count(k => k.StartsWith(table, StringComparison.Ordinal)) > 20, table);
        Assert.Equal("ll.Say(integer, string)", Slua.Find("ll.Say")?.Tooltip);
        Assert.Equal("ll.Abs(integer) -> integer", Slua.Find("ll.Abs")?.Tooltip);
    }

    [Fact]
    public void A_function_with_several_signatures_lists_each_on_its_own_line()
    {
        var tip = Slua.Find("ossl.GetPrimCount")?.Tooltip;
        Assert.Equal(["ossl.GetPrimCount() -> integer", "ossl.GetPrimCount(key) -> integer"], tip?.Split('\n'));
    }

    [Fact]
    public void The_tooltip_of_a_function_is_found_by_its_table_and_a_bare_name_is_not_a_function()
    {
        Assert.Equal("ll.Say(integer, string)", Slua.FindAt("    ll.Say(0, \"hi\")", 8)?.Tooltip);   // on "Say"
        Assert.Null(Slua.FindAt("    ll.Say(0, \"hi\")", 5));                                         // on "ll": a table, not a function
        Assert.Null(Slua.FindAt("local Say = 1", 7));                                                  // a variable that shares the name
        Assert.Null(Slua.FindAt("iw.Say(0)", 4));                                                      // another table has no such function
        Assert.Equal("iw.MakeNotecard(string, list)", Slua.FindAt("iw.MakeNotecard(a, b)", 5)?.Tooltip);
        Assert.Equal(LslWordKind.Control, Slua.FindAt("local x = 1", 2)?.Kind);
        Assert.Null(Slua.FindAt("", 0));
    }

    [Fact]
    public void The_words_to_colour_are_the_keywords_the_four_table_names_and_each_function_name()
    {
        var coloured = Slua.Coloured().ToDictionary(c => c.Word, c => c.Kind);
        Assert.Equal(LslWordKind.Control, coloured["local"]);
        Assert.Equal(LslWordKind.Function, coloured["ll"]);
        Assert.Equal(LslWordKind.Function, coloured["bot"]);
        Assert.Equal(LslWordKind.Function, coloured["Say"]);
        Assert.False(coloured.ContainsKey("ll.Say"));
    }

    [Fact]
    public void The_list_file_is_read_by_kind_word_and_tooltip_and_skips_comments()
    {
        var k = LslKeywords.FromSlua(new MemoryStream(Encoding.UTF8.GetBytes("# header\nkeyword\tlocal\t\nfunction\tll.Say\tll.Say(integer, string)\\nsecond\nnoise\tx\ty\n\n")));
        Assert.Equal(2, k.Words.Count);
        Assert.Equal("ll.Say(integer, string)\nsecond", k.Find("ll.Say")?.Tooltip);
    }

    [Fact]
    public void The_lsl_words_are_still_found_by_their_own_name()
    {
        var lsl = LslKeywords.Shipped(openSim: false);
        Assert.False(lsl.IsSlua);
        Assert.Equal(lsl.Find("llSay"), lsl.FindAt("llSay(0, \"x\");", 3));
    }

    private static ScriptEditing Opened(string text)
    {
        var agent = Guid.NewGuid();
        var access = new ScriptAccess(Guid.NewGuid(), Guid.NewGuid(), agent,
            new NotecardPermissions(agent, agent, 0x7fffffff, NotecardPermissions.Copy | NotecardPermissions.Modify, 0, 0, 0), false);
        var e = new ScriptEditing(access, "x");
        e.TextFetched(AssetFetchResult.Ok(Encoding.UTF8.GetBytes(text)));
        return e;
    }

    [Fact]
    public void A_script_is_lua_when_it_is_compiled_as_lua_or_its_first_line_says_so()
    {
        Assert.False(Opened("default{}").IsLua);
        var lua = Opened("default{}");
        lua.RunningKnown(new ScriptRuntime(true, false, Luau: true, LuauLanguage: true));
        Assert.True(lua.IsLua);
        Assert.True(Opened("--!slua\nprint(1)").IsLua);
        var lslOnLua = Opened("default{}");
        lslOnLua.RunningKnown(new ScriptRuntime(true, false, Luau: true, LuauLanguage: false));
        Assert.False(lslOnLua.IsLua);   // LSL syntax, run by the Lua VM
    }
}
