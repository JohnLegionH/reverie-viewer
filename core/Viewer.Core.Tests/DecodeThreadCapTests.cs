// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Protocol;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// CoreJ2K 2.4 decodes one picture on as many threads as the machine has (its default), where 2.3 used one. The viewer
/// already decodes several textures at once on its own decode threads, so a second layer of threads inside each decode
/// only adds allocation and collector pauses. Each decode is held to one thread.
/// </summary>
public class DecodeThreadCapTests
{
    [Theory]
    [InlineData(5, 0)]
    [InlineData(5, 1)]
    [InlineData(5, 3)]
    [InlineData(6, 5)]
    public void One_decode_uses_one_thread_at_every_level(int decompositionLevels, int level)
    {
        var cfg = LmvTexture.DecoderConfiguration(decompositionLevels, level);
        Assert.Equal(1, cfg.MaxDegreeOfParallelism);
    }

    [Theory]
    [InlineData(5, 0, -1)]
    [InlineData(5, 1, 4)]
    [InlineData(5, 3, 2)]
    public void The_resolution_level_asked_for_is_still_set(int decompositionLevels, int level, int expectedResolutionLevel)
    {
        var cfg = LmvTexture.DecoderConfiguration(decompositionLevels, level);
        Assert.Equal(expectedResolutionLevel, cfg.ResolutionLevel);
    }

    /// <summary>
    /// Pictures of up to four components are decoded by LibreMetaverse's own texture class, which asks CoreJ2K for its default
    /// settings; the cap there is CoreJ2K's process-wide default, set when the viewer's texture decoder is first used.
    /// </summary>
    [Fact]
    public void The_library_wide_default_is_one_thread_once_the_texture_decoder_is_in_use()
    {
        _ = LmvTexture.DecoderConfiguration(5, 0);
        Assert.Equal(1, CoreJ2K.J2kImage.DefaultMaxDegreeOfParallelism);
    }
}
