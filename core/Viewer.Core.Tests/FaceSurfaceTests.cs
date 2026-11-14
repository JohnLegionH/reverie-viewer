// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Avatars;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>An expression change re-encodes only the head and eyelash surfaces, each exactly as the whole body's morph encodes it, and the weights
/// given by name merge the hand pose and the expressions.</summary>
public sealed class FaceSurfaceTests
{
    [Fact]
    public void HandAndExpressionWeightsMerge()
    {
        Assert.Null(AvatarFaceSurfaces.Named(null, null));
        Assert.Null(AvatarFaceSurfaces.Named(null, new Dictionary<string, float>()));
        var hands = new Dictionary<string, float> { ["Hands_Fist"] = 1f };
        var h = AvatarFaceSurfaces.Named(hands, null);
        Assert.NotNull(h);
        Assert.Equal(1f, h!["Hands_Fist"]);
        var m = AvatarFaceSurfaces.Named(hands, new Dictionary<string, float> { ["Express_Smile"] = 0.5f, [Emotes.ClosedMouth] = 0.5f, ["Hands_Fist"] = 0f });
        Assert.NotNull(m);
        Assert.Equal(3, m!.Count);
        Assert.Equal(0.5f, m["Express_Smile"]);
        Assert.Equal(0.5f, m[Emotes.ClosedMouth]);
        Assert.Equal(1f, m["Hands_Fist"]);
    }

    [CharacterFact]
    public void AFaceChangeEncodesOnlyTheHeadAndEyelashesAsTheWholeBodyWould()
    {
        var body = AvatarBody.Load(CharacterFactAttribute.Folder!);
        var def = body.Shape!;
        var types = BodyMorph.MeshTypesOf(def, AvatarFaceSurfaces.ExpressionParams);
        var shape = AvatarShape.Neutral(new AvatarRig(body.Skeleton));
        var named = new Dictionary<string, float> { ["Express_Toothsmile"] = 0.7f, [Emotes.ClosedMouth] = 0.3f };
        var face = AvatarFaceSurfaces.Prepare(body, def, shape, named, shapeless: false, types);
        Assert.NotNull(face);
        var whole = BodyMorph.Compute(body, def, shape, named, onlyNamed: false)!;
        var expected = Enumerable.Range(0, body.Parts.Count).Where(i => types.Contains(body.Parts[i].Type)).ToArray();
        Assert.Equal(expected, face!.Parts);
        Assert.True(face.Parts.Length < body.Parts.Count);
        for (int k = 0; k < face.Parts.Length; k++)
        {
            int i = face.Parts[k];
            var full = AvatarSurfaceData.Encode(body.Parts[i], whole[i], body.Palette.Length);
            Assert.Equal(full.Surface.VertexData, face.Surfaces[k].Surface.VertexData);
            Assert.Equal(full.Surface.AttributeData, face.Surfaces[k].Surface.AttributeData);
        }
    }
}
