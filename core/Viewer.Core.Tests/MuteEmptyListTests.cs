// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using LibreMetaverse;
using LibreMetaverse.Packets;
using Viewer.Core.Diagnostics;
using Viewer.Core.Protocol;
using Viewer.Core.Session;
using Viewer.Core.Social;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// When nothing is blocked the grid does not send a list file at all: it answers the block list request with a generic message named
/// "emptymutelist", and the official viewer treats that as the list having loaded (indra/newview/llmutelist.cpp, LLDispatchEmptyMuteList).
/// The protocol library does not handle that message, so without this the list stays "requested" until the wait runs out and a block list
/// kept on disk would never be replaced by the grid's empty one. Every member builds its own objects, so the class runs in parallel.
/// </summary>
public class MuteEmptyListTests
{
    private static readonly SimRef Here = new(1099511628032256UL, "Example Region");
    private static readonly Guid Self = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid Pest = Guid.Parse("0a1b2c3d-0000-4000-8000-00000000abcd");

    private static GenericMessagePacket Message(string method)
    {
        var p = new GenericMessagePacket();
        p.MethodData.Method = Utils.StringToBytes(method);
        p.MethodData.Invoice = UUID.Zero;
        p.ParamList = [];
        return p;
    }

    [Fact]
    public void The_empty_list_message_is_an_empty_list_from_the_grid()
    {
        var e = LmvTransport.ToEmptyMuteList(Message("emptymutelist"));
        Assert.NotNull(e);
        Assert.Empty(e!.Entries);
    }

    [Theory]
    [InlineData("")]
    [InlineData("emptymutelist2")]
    [InlineData("EMPTYMUTELIST")]
    [InlineData("estatechangeinfo")]
    public void Any_other_generic_message_is_not_the_empty_list(string method) =>
        Assert.Null(LmvTransport.ToEmptyMuteList(Message(method)));

    [Fact]
    public void An_empty_list_from_the_grid_loads_the_session_list_and_ends_the_wait()
    {
        var fake = new FakeTransport();
        var session = new ViewerSession(new SessionOptions { LoginUri = "http://grid.invalid/" }, fake, new CoreLog(new ListSink(), "test"));
        Assert.True(session.LoginAsync(new LoginCredentials("Test", "User", new Secret("pw-not-real", SensitiveKind.PasswordDigest)), CancellationToken.None).Result.Ok);
        fake.DriveToInWorld(Here);
        Assert.Equal(MuteListState.Requested, session.Mutes.State);
        fake.Raise(new MuteListReceived([]));
        Assert.Equal(MuteListState.Loaded, session.Mutes.State);
        Assert.Equal(0, session.Mutes.Count);
    }

    [Fact]
    public void The_transport_listens_for_generic_messages_and_lets_go_of_them()
    {
        string text = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "core", "Viewer.Core", "Protocol", "LmvTransport.cs"));
        Assert.Contains("RegisterCallback(PacketType.GenericMessage, OnGenericMessage)", text);
        Assert.Contains("UnregisterCallback(PacketType.GenericMessage, OnGenericMessage)", text);
    }
}
