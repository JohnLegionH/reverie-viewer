// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using LibreMetaverse;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Parity oracle: the gateway's AppearanceCharacterisationTests (web-viewer bb91ba5), the subset the extraction map lists
/// (slot map, shared numbering, the sim's stored parameters flowing from the receiver); the bake, cache-id and echo tests
/// belong to a bake pipeline the passive core does not have. ToBakeType is adapted: the core's numbering is checked
/// against LibreMetaverse 3.1.6's own BakeType and its bake-to-face table, instead of the baking library's channel enum.
/// </summary>
public class AppearanceCharacterisationTests
{
    [Theory]
    [InlineData(BakeType.Head, "head")]
    [InlineData(BakeType.UpperBody, "upper")]
    [InlineData(BakeType.LowerBody, "lower")]
    [InlineData(BakeType.Eyes, "eyes")]
    [InlineData(BakeType.Hair, "hair")]
    [InlineData(BakeType.Skirt, "skirt")]
    [InlineData(BakeType.BakedLeftArm, "leftarm")]
    [InlineData(BakeType.BakedLeftLeg, "leftleg")]
    [InlineData(BakeType.BakedAux1, "aux1")]
    [InlineData(BakeType.BakedAux2, "aux2")]
    [InlineData(BakeType.BakedAux3, "aux3")]
    public void SlotOfMapsEveryBakeTheClientDraws(BakeType bt, string expected) => Assert.Equal(expected, AppearanceSlots.SlotOf((int)bt));

    [Fact]
    public void SlotOfIsNullForABakeTheClientDoesNotDraw()
    {
        Assert.Null(AppearanceSlots.SlotOf(99));
        Assert.Null(AppearanceSlots.SlotOf((int)BakeType.Unknown));
    }

    /// <summary>The same eleven tokens the sim's appearance service serves under, in the viewer's order.</summary>
    [Fact]
    public void TheSlotNamesAreTheViewersEleven()
    {
        var slots = Enumerable.Range(0, AppearanceSlots.Count).Select(AppearanceSlots.SlotOf).ToArray();
        Assert.Equal(new string?[] { "head", "upper", "lower", "eyes", "skirt", "hair", "leftarm", "leftleg", "aux1", "aux2", "aux3" }, slots);
    }

    [Fact]
    public void ToBakeTypeIsTheViewersSharedNumbering()
    {
        // Every bake type LibreMetaverse 3.1.6 knows: same number, and the bake arrives on the face the library says.
        var types = Enum.GetValues<BakeType>().Where(b => b != BakeType.Unknown).ToArray();
        Assert.Equal(AppearanceSlots.Count, types.Length);
        foreach (var bt in types)
        {
            Assert.NotNull(AppearanceSlots.SlotOf((int)bt));
            Assert.Equal((int)AppearanceManager.BakeTypeToAgentTextureIndex(bt), AppearanceSlots.FaceOf((int)bt));
        }
    }

    [Fact]
    public void SimParamsFlowFromTheAppearanceReceiverToTheBake()
    {
        // "To the bake" in the gateway; in the passive core the receiver records the sim's stored blob for a later consumer.
        var state = new SelfAppearanceState();
        Assert.Null(state.SimParams);

        state.RecordSimParams(new byte[] { 1, 2, 3 });
        Assert.Equal(new byte[] { 1, 2, 3 }, state.SimParams);

        state.RecordSimParams(new byte[] { 9, 9 });   // a later appearance replaces it
        Assert.Equal(new byte[] { 9, 9 }, state.SimParams);

        state.RecordSimParams(Array.Empty<byte>());   // an empty blob is not a blob
        Assert.Equal(new byte[] { 9, 9 }, state.SimParams);

        // And the receiver is what writes it: a self appearance through the session lands in World.Self.
        var (s, t, _) = ServerAppearanceTests.LoggedIn();
        ServerAppearanceTests.Arrive(t, new Session.SimRef(1, "R"), serverBaked: false);
        var look = ServerAppearanceTests.Look(ServerAppearanceTests.Me, 0) with
        {
            VisualParams = new VisualParamSet(new byte[] { 7, 8 }, [1, 2]),
        };
        t.Raise(new Session.AvatarAppearanceReceived(t.CurrentSim, look));
        Assert.Equal(new byte[] { 7, 8 }, s.World.Self.SimParams);
    }

    [Fact]
    public void SimParamsAreCopiedNotAliased()
    {
        var state = new SelfAppearanceState();
        var source = new byte[] { 4, 5, 6 };
        state.RecordSimParams(source);
        source[0] = 42;
        Assert.Equal(new byte[] { 4, 5, 6 }, state.SimParams);
        state.SimParams![1] = 99;   // nor is what it hands out
        Assert.Equal(new byte[] { 4, 5, 6 }, state.SimParams);
    }
}
