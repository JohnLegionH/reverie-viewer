// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Avatars;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The special texture ids that tell a face to show one of the wearer's baked textures. The values are the protocol's constants
/// (indra_constants.cpp IMG_USE_BAKED_*, read for the values). Pure, parallel.</summary>
public class BakeChannelsTests
{
    [Theory]
    [InlineData("5a9f4a74-30f2-821c-b88d-70499d3e7183")]   // head
    [InlineData("ae2de45c-d252-50b8-5c6e-19f39ce79317")]   // upper body
    [InlineData("24daea5f-0539-cfcf-047f-fbc40b2786ba")]   // lower body
    [InlineData("52cc6bb6-2ee5-e632-d3ad-50197b1dcb8a")]   // eyes
    [InlineData("43529ce8-7faa-ad92-165a-bc4078371687")]   // skirt
    [InlineData("09aac1fb-6bce-0bee-7d44-caac6dbb6c63")]   // hair
    [InlineData("ff62763f-d60a-9855-890b-0c96f8f8cd98")]   // left arm
    [InlineData("8e915e25-31d1-cc95-ae08-d58a47488251")]   // left leg
    [InlineData("9742065b-19b5-297c-858a-29711d539043")]   // aux 1
    [InlineData("03642e83-2bd1-4eb9-34b4-4c47ed586d2d")]   // aux 2
    [InlineData("edd51b77-fc10-ce7a-4b3d-011dfc349e4f")]   // aux 3
    public void Each_of_the_special_ids_is_a_bake_channel(string id) => Assert.True(BakeChannels.IsBakeId(Guid.Parse(id)));

    [Fact]
    public void An_ordinary_texture_and_the_empty_id_are_not()
    {
        Assert.False(BakeChannels.IsBakeId(Guid.Parse("89556747-24cb-43ed-920b-47caed15465f")));   // the default plywood texture
        Assert.False(BakeChannels.IsBakeId(Guid.Empty));
    }
}
