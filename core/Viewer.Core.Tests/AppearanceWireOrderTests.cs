// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text.Json;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The order the viewer reads an AvatarAppearance message's visual-parameter bytes in must be the one the official viewer sends them in: ascending id over the
/// parameters that are transmitted (local input: the reference file, skipped without NV_SHAPE_REFERENCE).</summary>
public class AppearanceWireOrderTests
{
    [ShapeReferenceFact]
    public void The_wire_order_is_the_official_transmit_order()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(ShapeReferenceFactAttribute.File!));
        var official = doc.RootElement.GetProperty("meta").GetProperty("transmitOrder").EnumerateArray().Select(e => e.GetInt32()).ToList();
        var ours = LibreMetaverse.VisualParams.Group0ParamIds.ToList();
        Assert.Equal(official, ours);
    }
}
