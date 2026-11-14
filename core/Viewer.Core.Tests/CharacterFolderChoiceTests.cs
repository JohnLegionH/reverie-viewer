// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using Viewer.Core.Avatars;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Which avatar data folder the viewer reads: the --character argument when given, else NV_CHARACTER_DIR, else the
/// folder shipped beside the executable when it is there, else none (bodies are not drawn).
/// </summary>
public class CharacterFolderChoiceTests
{
    private static readonly string Exe = Path.Combine("install", "viewer");
    private static readonly string Arg = Path.Combine("from", "argument");
    private static readonly string Env = Path.Combine("from", "environment");
    private static readonly string Shipped = Path.Combine(Exe, CharacterFolderChoice.ShippedFolderName);
    private static bool ShippedExists(string p) => p == Shipped;
    private static bool Nothing(string p) => false;

    [Fact]
    public void The_argument_wins_over_the_environment_and_the_shipped_folder()
    {
        var c = CharacterFolderChoice.Resolve(Arg, Env, Exe, ShippedExists);
        Assert.Equal(Arg, c.Path);
        Assert.Equal(CharacterFolderSource.Argument, c.Source);
    }

    [Fact]
    public void The_environment_wins_over_the_shipped_folder()
    {
        var c = CharacterFolderChoice.Resolve(null, Env, Exe, ShippedExists);
        Assert.Equal(Env, c.Path);
        Assert.Equal(CharacterFolderSource.Environment, c.Source);
    }

    [Fact]
    public void Without_either_the_shipped_folder_is_used()
    {
        var c = CharacterFolderChoice.Resolve(null, null, Exe, ShippedExists);
        Assert.Equal(Shipped, c.Path);
        Assert.Equal(CharacterFolderSource.Shipped, c.Source);
    }

    [Fact]
    public void Blank_values_count_as_not_given()
    {
        var c = CharacterFolderChoice.Resolve("  ", "", Exe, ShippedExists);
        Assert.Equal(CharacterFolderSource.Shipped, c.Source);
    }

    [Fact]
    public void Quotes_and_spaces_around_a_value_are_taken_off()
    {
        var c = CharacterFolderChoice.Resolve(null, " \"" + Env + "\" ", Exe, ShippedExists);
        Assert.Equal(Env, c.Path);
    }

    [Fact]
    public void No_folder_when_nothing_is_given_and_none_is_shipped()
    {
        var c = CharacterFolderChoice.Resolve(null, null, Exe, Nothing);
        Assert.Null(c.Path);
        Assert.Equal(CharacterFolderSource.None, c.Source);
        var noExe = CharacterFolderChoice.Resolve(null, null, null, ShippedExists);
        Assert.Equal(CharacterFolderSource.None, noExe.Source);
    }

    [Fact]
    public void The_shipped_folder_is_named_character()
    {
        Assert.Equal("character", CharacterFolderChoice.ShippedFolderName);
    }
}
