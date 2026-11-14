// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Diagnostics;
using Viewer.Core.Net;
using Viewer.Core.Protocol;
using Viewer.Core.Session;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The LibreMetaverse side of the command interface, with no grid: the conversions, and the answer of an adapter that
/// has no circuit (nothing is sent, and it says why). Every member is independent, so the class runs in parallel.
/// </summary>
public class LmvCommandTests
{
    private static LmvTransport New() => new(new OutboundPolicy(new OutboundOptions()), new CoreLog(new ListSink(), "test"));

    [Theory]
    [InlineData(ChatKind.Whisper, LibreMetaverse.ChatType.Whisper)]
    [InlineData(ChatKind.Say, LibreMetaverse.ChatType.Normal)]
    [InlineData(ChatKind.Shout, LibreMetaverse.ChatType.Shout)]
    public void Chat_kinds_become_the_librarys_chat_types(ChatKind kind, LibreMetaverse.ChatType expected) =>
        Assert.Equal(expected, LmvTransport.ChatTypeOf(kind));

    public static TheoryData<TransportCommand> Commands => new()
    {
        new SayCommand("hi", 0, ChatKind.Say),
        new InstantMessageCommand(Guid.NewGuid(), "hi"),
        new TouchCommand(5),
        new SitCommand(Guid.NewGuid(), Vector3.Zero),
        new StandCommand(),
        new TeleportRegionCommand("Example Region", new Vector3(128, 128, 30)),
        new TeleportLandmarkCommand(Guid.NewGuid()),
        new TeleportHomeCommand(),
        new MoveInventoryItemCommand(Guid.NewGuid(), Guid.NewGuid(), null),
    };

    [Theory]
    [MemberData(nameof(Commands))]
    public void Without_a_circuit_every_command_is_refused_with_its_reason_and_nothing_throws(TransportCommand command)
    {
        using var t = New();
        var answer = ((ICommandTransport)t).Execute(command);
        Assert.False(answer.Sent);
        Assert.Equal("not_connected", answer.Reason);
    }

    [Fact]
    public void A_disposed_adapter_sends_nothing_and_says_so()
    {
        var t = New();
        t.Dispose();
        var answer = ((ICommandTransport)t).Execute(new TouchCommand(5));
        Assert.False(answer.Sent);
        Assert.Equal("disposed", answer.Reason);
    }

    private sealed record UnknownCommand : TransportCommand;

    [Fact]
    public void A_command_the_adapter_does_not_know_is_refused_not_dropped()
    {
        using var t = New();
        var answer = ((ICommandTransport)t).Execute(new UnknownCommand());
        Assert.False(answer.Sent);
        Assert.Equal("unknown_command", answer.Reason);
        Assert.Throws<ArgumentNullException>(() => ((ICommandTransport)t).Execute(null!));
    }
}
