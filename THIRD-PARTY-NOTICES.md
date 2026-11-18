# Third-party notices

The viewer is licensed under the GNU Lesser General Public License, version 2.1 (`LICENSE`): the project's own code under
version 2.1 or (at your option) any later version, and code ported from elsewhere under the licence its source gives, with
that source's notice kept at the top of the file. Each source file names its licence on an `SPDX-License-Identifier` line;
`ThirdPartyLicenses/PORTED-FILES.tsv` lists the ported files and the upstream notices they carry.

This file lists the source code in this repository that is derived from third-party work, and every component the shipped
build bundles or links, with its licence and copyright line as the component itself states them. (Package versions are
pinned in `Directory.Packages.props` and the `packages.lock.json` files.)

## PrimMesher (BSD 3-clause)

- **Files:** `core/Viewer.Core/Meshing/PrimMesher.cs`. (`PrimGeometry.cs` is listed separately below: it is adapted from a
  file with a different notice.)
- **Derived from:** the web viewer's TypeScript port (`client/src/mesh/primmesher/`), which ports PrimMesher from
  LibreMetaverse (https://github.com/cinderblocks/libremetaverse, `PrimMesher/PrimMesher.cs`); PrimMesher itself derives from the OpenSimulator PrimMesher
  by Dahlia Trimble.
- **Copyright:** "Copyright (c) Contributors. See CONTRIBUTORS.TXT for a full list of copyright holders." (the notice at
  the top of `PrimMesher/PrimMesher.cs`, kept word for word at the top of the file).
- **Changes:** three regressions of the LibreMetaverse 3.1.4 copy are not reproduced (hollow/cut flags, twist degrees
  used as radians, missing bottom cap); see the port notes at the top of `PrimMesher.cs`.

## SculptMap / SculptMesh (BSD 3-clause, same licence text as PrimMesher below)

- **Files:** `core/Viewer.Core/Meshing/SculptMesher.cs` (also carries the Second Life viewer notice, below).
- **Derived from:** the web viewer's TypeScript port (`client/src/mesh/sculpt.ts`), which ports
  `PrimMesher/SculptMap.cs` and `PrimMesher/SculptMesh.cs` from LibreMetaverse; they derive from the OpenSimulator
  PrimMesher by Dahlia Trimble. Same copyright holders as PrimMesher.
- **Changes:** degenerate maps (sampling to fewer than 2 x 2 points, or a side halved to 0) return nothing instead of
  NaN or an out-of-range read. The port's sampler is kept only as a test reference (`BuildReference`); drawing uses
  new code (the official sampler, the LOD rule, placeholders and surface-area check) written from a behaviour spec of
  the official viewer (`Docs/SCULPT-SAMPLER-SPEC.md`). `BuildOfficial`, `OfficialGrid` and `Mesh` follow
  `indra/llmath/llvolume.cpp` of the Second Life viewer; see the Second Life viewer section below.

```
Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:
    * Redistributions of source code must retain the above copyright
      notice, this list of conditions and the following disclaimer.
    * Redistributions in binary form must reproduce the above copyright
      notice, this list of conditions and the following disclaimer in the
      documentation and/or other materials provided with the distribution.
    * Neither the name of the OpenSimulator Project nor the
      names of its contributors may be used to endorse or promote products
      derived from this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

## LibreMetaverse MeshFoundry (BSD 3-clause)

- **Files:** `core/Viewer.Core/Meshing/PrimGeometry.cs` (the prim shape block and its mapping to PrimMesher parameters).
- **Derived from:** `GeneratePrimMesh` in `LibreMetaverse.Rendering.MeshFoundry/MeshFoundry.cs`
  (https://github.com/cinderblocks/libremetaverse).
- **Copyright:** "Copyright (c) 2008 Robert Adams" and "Copyright (c) 2021-2026, Sjofn LLC. All rights reserved."; the
  full notice, whose clause 3 and disclaimer differ from the PrimMesher text above, is at the top of the file.

## Avatar bake compositor (MPL-2.0)

- **Files:** `core/Viewer.Baking/*.cs` and `core/Viewer.Baking/Docs/*.md`.
- **Brought in from:** the OpenSimNGC.Appearance.Baking library, Copyright (c) 2026 Legion Builds, MPL-2.0 (the
  header at the top of each file is unchanged). It composites the avatar's baked textures from the worn wearables, for
  regions that do not bake on the server.
- **Changes here:** the SkiaSharp use and the four-component encoder that needed it are removed; the embedded
  `avatar_lad.xml` and character images are removed, so this viewer reads them from the avatar data folder (shipped
  beside the executable, or named at run time; see the avatar data folder section below); the default
  constructors that used the embedded data are removed; `SkiaBakeBackend` is renamed `BakeBackend`; the `OpenMetaverse`
  namespace is `LibreMetaverse`; CoreJ2K is the version this viewer pins.
- **Followed, not copied:** the pixel rules follow the behaviour of the Second Life viewer's texture layer compositor
  (`LLTexLayerSet`, as described in `Docs/BAKE-COMPOSITOR-SPEC.md`); no viewer code is used here.

## Second Life viewer (LGPL 2.1 only)

- **Copyright:** "Second Life Viewer Source Code, Copyright (C) 2010, Linden Research, Inc." under the GNU Lesser General
  Public License, version 2.1 only. The licence text is `ThirdPartyLicenses/LGPL-2.1-secondlife-viewer.txt` (a copy of
  `doc/LGPL-license.txt` of the viewer source).
- **Files and sources:**
  - `core/Viewer.Motion/` (the whole assembly is licensed LGPL-2.1 only; see its `NOTICE.md`): `indra/newview/llviewerinput.cpp`, `llagent.cpp`,
    `llmoveview.cpp`, `llviewermessage.cpp` (`send_agent_update`), `lltoolgun.cpp`, `llviewerwindow.cpp`, `llviewerobject.cpp`
    (`idleUpdate`, `interpolateLinearMotion`, `processUpdateMessage`), `lldrawable.cpp` (`updateXform`),
    `indra/llcommon/indra_constants.h` and `llcriticaldamp.cpp`; the camera (`AgentCamera.cs`, `CameraControls.cs`): `indra/newview/llagentcamera.cpp`,
    `lltoolfocus.cpp`, `llviewerinput.cpp` and `app_settings/key_bindings.xml`; the default key table (`KeyBindingTable.cs`): `llviewerinput.cpp` and
    `app_settings/key_bindings.xml`; left-drag steering (`MouseSteering.cs`): `indra/newview/lltoolpie.cpp`.
  - `host/Viewer.Godot/Presentation/FaceShader.cs` (the legacy face shader, `LegacyBody`): `indra/newview/llface.cpp`
    (planar texture-coordinate generation) and `indra/newview/pipeline.cpp` (specular normalisation).
  - `core/Viewer.Core/Meshing/MeshGeometry.cs` (`MeshLodRule.Detail`): `indra/newview/llvovolume.cpp` (`calcLOD`,
    `computeLODDetail`) and `indra/llmath/llvolumemgr.cpp` (detail thresholds).
  - `core/Viewer.Core/Meshing/SculptMesher.cs` (`BuildOfficial`, `OfficialGrid`, `Mesh`): `indra/llmath/llvolume.cpp`.
  - `core/Viewer.Core/World/Lights.cs` (`LightRules.SpotLightPriority`): `indra/newview/llvovolume.cpp`
    (`updateSpotLightPriority`) and `indra/newview/pipeline.cpp` (`calcPixelArea`).
  - `core/Viewer.Core/World/ProjectorPicture.cs` (`Render`): `indra/newview/app_settings/shaders/class1/deferred/deferredUtil.glsl`
    (`getTexture2DLodDiffuse`, `getProjectedLightAmbiance`).
  - `host/Viewer.Godot/Presentation/GlowPass.cs` (`BlurCode`) and `core/Viewer.Core/World/Glow.cs` (`GlowBlur`):
    `indra/newview/app_settings/shaders/class1/effects/glowF.glsl` (copyright 2007 for the shader files).
  - `core/Viewer.Core/Avatars/AvatarShape.cs` (driver parameters, skeletal parameters, body size, quantised weights):
    `indra/llappearance/lldriverparam.cpp`, `indra/llappearance/llpolyskeletaldistortion.cpp`,
    `indra/llappearance/llavatarappearance.cpp`, `indra/llmath/llquantize.h` and `indra/llmath/xform.cpp`.
  - `core/Viewer.Core/Avatars/BodyMorph.cs` (morph targets on the default body, volume morphs, the sex rule): `indra/llappearance/llpolymorph.cpp`.
  - `core/Viewer.Core/Avatars/DefaultMotions.cs` (the body sway, breathing, pelvis fix, flying roll, eye, head and hand motions): `indra/newview/llvoavatar.cpp`,
    `indra/llcharacter/llheadrotmotion.cpp`, `indra/llcharacter/llhandmotion.cpp` and `indra/llcharacter/llkeyframewalkmotion.cpp`.
  - `core/Viewer.Core/Avatars/OfficialMath.cs` (the quaternion helpers, the smoothing interpolant, the gradient noise, `clamp_rescale`): `indra/llmath/llquaternion.cpp`,
    `indra/llcommon/llcriticaldamp.cpp`, `indra/llmath/llperlin.cpp` and `indra/llmath/llmath.h`.
  - `core/Viewer.Core/Avatars/AvatarDefaultMotions.cs` (the motions every avatar runs, the flying roll's conditions): `indra/newview/llvoavatar.cpp`.
  - `core/Viewer.Core/Avatars/AnimationMix.cs` (priority and weight blending, additive motions): `indra/llcharacter/llpose.cpp`.
  - `core/Viewer.Core/Avatars/MotionController.cs` (starting, stopping and easing motions): `indra/llcharacter/llmotioncontroller.cpp` and `indra/newview/llvoavatar.cpp`.
  - `core/Viewer.Core/Imaging/AlphaMask.cs` (whether a texture's alpha can be drawn as a cut-out): `indra/llrender/llimagegl.cpp` (`analyzeAlphaData`).
  - `core/Viewer.Core/World/AutoMask.cs` (when a face without a material is drawn as a cut-out, and its cutoff): `indra/newview/llface.cpp` (`canRenderAsMask`),
    `indra/newview/pipeline.cpp` (`sAutoMaskAlphaDeferred`, `sAutoMaskAlphaNonDeferred`) and `indra/newview/llvovolume.cpp` (the mask cutoff of a draw info).
  - `core/Viewer.Core/Avatars/MorphTarget.cs` (the morph section of a body mesh file and the loader's copies for the physics-driven
    parameters): `indra/llappearance/llpolymorph.cpp`, `indra/llappearance/llpolymesh.cpp` and `indra/llappearance/llpolyskeletaldistortion.cpp`.
  - `core/Viewer.Core/World/SkyLighting.cs` (`SkyLighting.Haze`): `indra/newview/app_settings/shaders/class1/deferred/skyV.glsl`
    (file header `WLSkyV.glsl`) and `indra/newview/app_settings/shaders/class1/deferred/skyF.glsl` (copyright 2005 for both).
  - `core/Viewer.Core/World/SkyLighting.cs` (`SkyLighting.Classic`, `ClassicDiffuse`, `ClassicSpecular`) and `host/Viewer.Godot/Presentation/FaceShader.cs` (the avatar shaders' classic lighting):
    `indra/newview/app_settings/shaders/class1/windlight/atmosphericsFuncs.glsl` (copyright 2022) and `indra/newview/app_settings/shaders/class3/deferred/softenLightF.glsl` (copyright 2007).
  - `core/Viewer.Core/World/SkyLighting.cs` (`SkyLighting.WhiteFace`): `indra/newview/app_settings/shaders/class1/windlight/atmosphericsFuncs.glsl`
    (copyright 2022) and `indra/newview/app_settings/shaders/class3/deferred/softenLightF.glsl` (copyright 2007).
  - `core/Viewer.Core/World/SkyLighting.cs` (`SkyLighting.DomeVertexColour`, `DomeShown`, `DomeColour`): `indra/newview/llvowlsky.cpp` and `indra/newview/lldrawpoolwlsky.cpp` (copyright 2010), `indra/newview/llenvironment.cpp` (file header `llenvmanager.cpp`, copyright 2011), with `skyV.glsl`, `skyF.glsl` and `softenLightF.glsl` as above.
  - `core/Viewer.Core/World/WaterShading.cs`: `indra/newview/app_settings/shaders/class3/environment/waterF.glsl` (copyright 2022) and
    `indra/newview/app_settings/shaders/class1/environment/waterFogF.glsl` (copyright 2007).
  - `host/Viewer.Godot/Presentation/EnvironmentLayer.cs` (the water shader): `indra/newview/app_settings/shaders/class3/environment/waterF.glsl` (copyright 2022)
    and `indra/newview/app_settings/shaders/class1/environment/waterFogF.glsl` (copyright 2007).
  - `core/Viewer.Core/World/WaterWaves.cs`: `indra/newview/app_settings/shaders/class1/environment/waterV.glsl` (copyright 2007).
  - `core/Viewer.Core/World/TreeMesh.cs` (`TreeMesh`, `TreeLod`): `indra/newview/llvotree.cpp` (copyright 2010) and the species table of `indra/newview/app_settings/trees.xml`.
  - `core/Viewer.Core/World/TreeNoise.cs`: `indra/newview/noise.h` (copyright 2010).
  - `core/Viewer.Core/World/WaterShading.cs` (`Glint`) and `host/Viewer.Godot/Presentation/EnvironmentLayer.cs` (the water shader's glint and wave coordinates):
    `indra/newview/app_settings/shaders/class1/deferred/deferredUtil.glsl` and `indra/newview/app_settings/shaders/class1/environment/waterV.glsl` (copyright 2007 for both).
  - `core/Viewer.Core/World/TerrainComposition.cs` (`LayerWeights`, `Jitter`), `core/Viewer.Core/World/TerrainRamp.cs` and `host/Viewer.Godot/Presentation/TerrainView.cs` (the terrain shader):
    `indra/newview/app_settings/shaders/class1/deferred/terrainF.glsl` (copyright 2007) and `indra/newview/llsurfacepatch.cpp` (copyright 2001: the second texture coordinate of a land vertex).
  - `core/Viewer.Core/World/Data/terrain-ramp.raw`: the picture of `indra/newview/skins/default/textures/alpha_gradient_2d.j2c`, the viewer's terrain blend ramp, decoded to 256 x 256 eight-bit values
    (bottom row first). It is a texture of the viewer repository, which is under the licence above; no separate licence for it was found in that repository.
  - `core/Viewer.Core/World/ToneMap.cs` (`ToneMap`, `ColourPath`): `indra/newview/app_settings/shaders/class1/deferred/tonemapUtilF.glsl` (its `toneMap`: exposure, mix and clamp; the two tone curves are not taken from it, see Baking Lab below) and `postDeferredTonemap.glsl` (copyright 2024, both with the file header `postDeferredTonemap.glsl`), `indra/llinventory/llsettingssky.cpp` and `indra/newview/llsettingsvo.cpp` (copyright 2017) and `indra/newview/pipeline.cpp` (copyright 2010); its `AutoExposure` follows `indra/newview/app_settings/shaders/class1/deferred/exposureF.glsl` and `luminanceF.glsl` (copyright 2023) and the exposure range `LLPipeline::generateExposure` sets in `pipeline.cpp`.
  - `core/Viewer.Core/World/SkyProbe.cs` (the sky reflection probe's irradiance map): `indra/newview/llreflectionmapmanager.cpp` (copyright 2022), `indra/newview/app_settings/shaders/class2/interface/irradianceGenF.glsl` and `class3/deferred/reflectionProbeF.glsl` (copyright 2022) and `indra/newview/llsettingsvo.cpp` (copyright 2017); `host/Viewer.Godot/Presentation/FaceShader.cs` (the probe lookup in the face shaders): `reflectionProbeF.glsl`.
  - `host/Viewer.Godot/Presentation/HdrTonemap.cs` (the compute shader of the tone mapping post pass): `indra/newview/app_settings/shaders/class1/deferred/tonemapUtilF.glsl` (its `toneMap`; the two tone curves are not taken from it; copyright 2024, file header `postDeferredTonemap.glsl`), and its luminance sample and exposure step follow `exposureF.glsl` and `luminanceF.glsl` (copyright 2023).
  - `core/Viewer.Core/World/SunDisc.cs` (the square the sun's picture is drawn on and the default sun picture): `indra/newview/llvosky.cpp` and `llvosky.h` (`LLVOSky::updateHeavenlyBodyGeometry`, the body factor and disk radius; copyright 2010), `indra/newview/app_settings/shaders/class1/deferred/sunDiscF.glsl` (copyright 2005) and `indra/llcommon/indra_constants.cpp` (`IMG_SUN`; copyright 2010).
  - `core/Viewer.Core/Avatars/ConstraintSolver.cs` (the animation constraints: hands, feet and lower legs held to the ground or to another part of the body): `indra/llcharacter/llkeyframemotion.cpp` (`LLKeyframeMotion::initializeConstraint`, `activateConstraint`, `deactivateConstraint`, `applyConstraint`; copyright 2010), `indra/llcommon/llcriticaldamp.cpp` (`LLSmoothInterpolation::calcInterpolant`; copyright 2010) and `indra/llmath/llquaternion.cpp` (`LLQuaternion::shortestArc` and `nlerp`; copyright 2010). The file carries the header of `llkeyframemotion.cpp`.
  - `core/Viewer.Core/Avatars/JointMask.cs` (which joints another motion already drives, so a constraint is skipped): `indra/llcharacter/llmotioncontroller.cpp` (`LLMotionController::updateMotionsByType`, the joint signatures), `indra/llcharacter/llkeyframemotion.cpp` (`applyConstraint`, the joint mask check) and `indra/llcharacter/llmotion.cpp` (`addJointState`, the signature byte; copyright 2010 for all). The file carries the header of `llmotioncontroller.cpp`.
  - `core/Viewer.Core/World/CloudLighting.cs` (`CloudLighting`, `CloudScroll`): `indra/newview/app_settings/shaders/class1/deferred/cloudsV.glsl` (file header `WLCloudsV.glsl`) and
    `cloudsF.glsl` (copyright 2005 for both), `indra/newview/llvowlsky.cpp`, `lldrawpoolwlsky.cpp` and `llvosky.cpp` (copyright 2010), `indra/newview/llenvironment.cpp`
    (file header `llenvmanager.cpp`, copyright 2011) and `indra/newview/llsettingsvo.cpp` (copyright 2017).
  - `core/Viewer.Core/World/Data/cloud-noise.tga`: a byte-identical copy of `indra/newview/app_settings/windlight/clouds2.tga`, the viewer's default cloud noise picture. It is a
    texture of the viewer repository, which is under the licence above; no separate licence for it was found in that repository.
  - `host/Viewer.Godot/Presentation/EnvironmentLayer.cs` (the cloud layer of the sky shader): `indra/newview/app_settings/shaders/class1/deferred/cloudsV.glsl`
    (file header `WLCloudsV.glsl`) and `cloudsF.glsl` (copyright 2005 for both).
  - `core/Viewer.Core/Appearance/WearableAsset.cs` (the wearable file's layout and limits, `TerseWeight`): `indra/llappearance/llwearable.cpp` (`importStream`, `exportStream`, `terse_F32_to_string`).
  - `core/Viewer.Core/Appearance/WearableKinds.cs` (the wearable kinds, texture slots and colour parameters): `indra/llappearance/llwearabletype.cpp`, `indra/llappearance/llavatarappearancedefines.cpp`,
    `indra/llappearance/llavatarappearance.cpp` (`teToColorParams`) and `indra/newview/llpaneleditwearable.cpp` (the picker lists).
  - `core/Viewer.Core/Appearance/EditParamCatalog.cs` (the tabs of each wearable panel and the sliders a tab lists): `indra/newview/llpaneleditwearable.cpp` and `indra/llcharacter/llvisualparam.h`.
  - Followed for behaviour only, in our own words and with no notice of their own: `core/Viewer.Core/Appearance/WearableEditor.cs`, `WearableSave.cs`, `host/Viewer.Shell/Appearance/AppearanceEditing.cs` and `host/Viewer.Godot/AppearancePanels.cs`
    (the order in which the official editor saves a wearable and asks about unsaved changes: `indra/newview/llpaneleditwearable.cpp`, `llagentwearables.cpp` and `llviewerwearable.cpp`).
- Each file carries the notice of its source files word for word at the top of the file.

## Firestorm viewer (LGPL 2.1 only)

- **Copyright:** "Second Life Viewer Source Code, Copyright (C) 2011, Zi Ree @ Second Life" under the GNU Lesser General
  Public License, version 2.1 only. The licence text is `ThirdPartyLicenses/LGPL-2.1-secondlife-viewer.txt` (the same
  text as `doc/LGPL-license.txt` of the Firestorm source).
- **Files and sources:** `core/Viewer.Overrider/` (the whole assembly is licensed LGPL-2.1 only; see its `NOTICE.md`):
  `AoEngine.cs` from `indra/newview/aoengine.cpp` (with the calls that feed it following `indra/newview/llvoavatar.cpp`),
  `AoSet.cs` from `indra/newview/aoset.h` and `aoset.cpp`, at Firestorm commit 895f65ab43.
- **Changes:** rewritten in C#; the inventory is only read; sets are not edited, imported or saved; see the assembly's
  `NOTICE.md`.

## Script editor keyword files (Second Life viewer and Firestorm, LGPL 2.1 only)

- **Files:** `vendor/lsl-keywords/keywords_lsl_default.xml`, the Second Life viewer's LSL keyword file
  (`indra/newview/app_settings/keywords_lsl_default.xml`, release 26.4), and `vendor/lsl-keywords/scriptlibrary_ossl.xml`,
  Firestorm's OSSL keyword list (`indra/newview/app_settings/scriptlibrary_ossl.xml`, commit 895f65ab43). Both are kept
  unchanged and embedded in the Viewer.Shell assembly; the script editor reads them for its word colours and tooltips.
  `vendor/lsl-keywords/UPSTREAM.md` gives the sources.
- **Licence:** Second Life Viewer Source Code (Copyright (C) Linden Research, Inc.) and the Firestorm viewer's source (The
  Phoenix Firestorm Project, Inc.), each under the GNU Lesser General Public License, version 2.1 only; the licence text is
  `ThirdPartyLicenses/LGPL-2.1-secondlife-viewer.txt`. Neither file carries a notice of its own.
- **Changes:** none.

## Packages and binaries

Package versions are pinned in `Directory.Packages.props` and the `packages.lock.json` files.

- **LibreMetaverse 3.3.0** and its packages **LibreMetaverse.Assets**, **.Protocol**, **.Transport**, **.Types**, **.StructuredData**
  and **.Imaging.Abstractions** 3.3.0 (BSD 3-clause): "Copyright (c) 2006-2016, openmetaverse.co" and "Copyright (c)
  2017-2025, Sjofn LLC". Licence text: `ThirdPartyLicenses/LICENSE-LibreMetaverse-source.txt` (identical to the
  `LICENSE.txt` in each of the seven 3.3.0 packages). The packages are the unchanged ones from nuget.org.
- **CoreJ2K 2.4.1.109-alloc.1** (BSD 3-clause; the build the viewer loads: a build of a fork,
  `https://github.com/JohnLegionH/CoreJ2K`, branch `alloc-on-2.4.1`, commit `15c219da2e63c6f6fa87a5e2206b73349384c474`, of
  upstream CoreJ2K 2.4.1, `https://github.com/cinderblocks/CoreJ2K`, commit `f5895ac`). The viewer references it directly, so it
  replaces the CoreJ2K that LibreMetaverse names. The copyright holders, the licence text and the JJ2000 notice are those of
  the entry below, unchanged: `ThirdPartyLicenses/LICENSE-CoreJ2K.txt` is identical to the `LICENSE` in the package, and
  `ThirdPartyLicenses/COPYRIGHT-JJ2000-5.1.txt` to its `COPYRIGHT-JJ2000-5.1`. **Change in the fork** (each changed file says
  so under its untouched JJ2000 header): the wavelet stage sizes its reconstruction buffer for the resolution level asked for,
  so a reduced-resolution decode allocates for the picture it returns (`InvWTAdapter.cs`, `InvWTFull.cs`). The package is in
  `nuget-feed/`; `tools/rebuild-corej2k.ps1` rebuilds it from that commit.
- **CoreJ2K 2.4.1** (BSD 3-clause; the upstream library the fork above is built from): "Copyright (c) 2007-2016, CSJ2K contributors" and

  "Copyright (c) 2024-2026, Sjofn LLC"; the package metadata also names JJ2000 Partners (1999-2000), Jason S. Clary
  (2007-2012) and Anders Gustafsson, Cureos AB (2013-2018). Licence text: `ThirdPartyLicenses/LICENSE-CoreJ2K.txt`; the
  package also ships a `COPYRIGHT-JJ2000-5.1` notice, which must travel with any redistribution of CoreJ2K.
- **Godot 4.7.2 and GodotSharp 4.7.2** (MIT): "Copyright (c) Godot Engine contributors" (package metadata). I can't
  confirm the Godot licence text from the NuGet packages, which do not ship it. The Godot 4.7.2 .NET editor binary the
  viewer is exported with carries it: "Copyright (c) 2014-present Godot Engine contributors (see AUTHORS.md)" and "Copyright (c) 2007-2014
  Juan Linietsky, Ariel Manzur"; the text is in `ThirdPartyLicenses/LICENSE-Godot-4.7.2.txt`. The engine bundles further
  third-party components; their notices are compiled into the engine (`Engine.GetCopyrightInfo` and
  `Engine.GetLicenseInfo`), and the viewer's About window shows them under Licences, read from the running engine.
- **JoltPhysicsSharp 2.19.1** (MIT): "Copyright (c) Amer Koleci and Contributors" (package metadata). The package does not
  ship a licence file; the same holder's MIT text is in `native/joltc/win-x64/LICENSE-joltc.txt`.
- **JoltPhysics.Native 1.0.4** (MIT): the package ships `LICENSE.MIT.txt` (Jolt Physics, "Copyright 2021 Jorrit Rouwe"),
  copied to `ThirdPartyLicenses/LICENSE-JoltPhysics.Native-1.0.4.txt`; package copyright "Copyright (c) Amer Koleci and
  Contributors".
- **joltc.dll** (`native/joltc/win-x64/joltc.dll`, MIT): built from joltc (https://github.com/amerkoleci/joltc, "Copyright
  (c) Amer Koleci and Contributors") with `per-system-tempallocator.patch`, and it contains Jolt Physics 5.4.0
  (https://github.com/jrouwe/JoltPhysics, "Copyright 2021 Jorrit Rouwe", MIT). Both licence texts are beside the dll
  (`LICENSE-joltc.txt`, `LICENSE-JoltPhysics.txt`) and at the top of the patch.
- **libjoltc.so** (`native/joltc/linux-x64/libjoltc.so` and `native/joltc/linux-arm64/libjoltc.so`, MIT): the Linux builds
  of joltc exactly as JoltPhysics.Native 1.0.4 ships them (`runtimes/linux-x64/native/` and `runtimes/linux-arm64/native/`),
  unpatched; they contain Jolt Physics ("Copyright 2021 Jorrit Rouwe", MIT). The same two licence texts are beside each
  file. Provenance and hashes: `native/joltc/PROVENANCE.md`.
- **.NET runtime 10.0.11** (Microsoft.NETCore.App, bundled with the shipped build; MIT): "Copyright (c) .NET Foundation and
  Contributors" (the runtime pack's `LICENSE.TXT`, copied to `ThirdPartyLicenses/LICENSE-dotnet-runtime-10.0.11.txt`). The
  runtime pack's `THIRD-PARTY-NOTICES.TXT` (46 notices) is copied unchanged to
  `ThirdPartyLicenses/THIRD-PARTY-NOTICES-dotnet-runtime-10.0.11.txt`.
- **Microsoft.Bcl.AsyncInterfaces, Microsoft.Extensions.Configuration, .Configuration.Abstractions, .Configuration.Binder,
  .DependencyInjection, .DependencyInjection.Abstractions, .Logging, .Logging.Abstractions, .Logging.Configuration,
  .Logging.Console, .ObjectPool, .Options, .Options.ConfigurationExtensions, .Primitives and System.Threading.RateLimiting
  10.0.13; System.Security.Cryptography.ProtectedData 10.0.12; System.IO.Hashing 8.0.0; Microsoft.NET.StringTools 17.11.4**
  (MIT, package metadata; "© Microsoft Corporation. All rights reserved."). Each package carries its own
  `THIRD-PARTY-NOTICES.TXT` (StringTools: `notices/THIRDPARTYNOTICES.txt`); the MIT text is the runtime's above.
- **MessagePack 3.1.12 and MessagePack.Annotations 3.1.12** (MIT, package metadata): "© Yoshifumi Kawai and contributors.
  All rights reserved." (https://github.com/MessagePack-CSharp/MessagePack-CSharp). The packages carry no licence file.
- **ZLogger 2.5.10** and **Utf8StringInterpolation 1.3.1** (MIT, package metadata): "© Cysharp, Inc."
  (https://github.com/Cysharp/ZLogger, https://github.com/Cysharp/Utf8StringInterpolation). No licence file in the packages.
- **OggVorbisEncoder 1.2.2** (MIT, package metadata; author Steve Lillis,
  https://github.com/SteveLillis/.NET-Ogg-Vorbis-Encoder). The package names no copyright line and carries no licence file.
- **Pfim 0.11.4** (author and copyright "Nick Babcock", package metadata; https://nickbabcock.github.io/Pfim/). The package
  gives no licence expression and no licence file, only a licence address,
  https://raw.githubusercontent.com/nickbabcock/Pfim/master/LICENSE.txt; its licence is not confirmed from the package.
- **zlib.net-mutliplatform 1.1.0** (BSD-3-Clause, package metadata): "Copyright © 2006-2007, ComponentAce, 2022-2025 Sjofn
  LLC" (https://github.com/cinderblocks/zlib.net). No licence file in the package.

The six packages above ship no licence file. Their projects publish one each; what those files say (the texts are under
"Licence texts of the packages" below):

- **MessagePack 3.1.12 and MessagePack.Annotations 3.1.12** (MIT): "Copyright (c) 2017 Yoshifumi Kawai and contributors"
  (https://github.com/MessagePack-CSharp/MessagePack-CSharp). The packages carry no licence file; the project's licence file,
  which also carries the notices of lz4net (BSD 2-clause, "Copyright (c) 2013-2017, Milosz Krajewski") and of BufferWriter.cs
  (Apache License 2.0, "Copyright 2019 .NET Foundation"), is under "Licence texts of the packages" below. The full Apache
  License 2.0 text is in `ThirdPartyLicenses/LICENSE-Apache-2.0.txt`.
- **ZLogger 2.5.10** (MIT): "Copyright (c) 2020 Cysharp, Inc." (https://github.com/Cysharp/ZLogger). No licence file in the
  package; the project's licence text is below.
- **Utf8StringInterpolation 1.3.1** (MIT): "Copyright (c) 2023 Cysharp, Inc." (https://github.com/Cysharp/Utf8StringInterpolation).
  No licence file in the package; the project's licence text is below.
- **OggVorbisEncoder 1.2.2** (MIT; author Steve Lillis, https://github.com/SteveLillis/.NET-Ogg-Vorbis-Encoder). The package
  names no copyright holder and carries no licence file. The project's own licence file names "Copyright (c) 2016 Steve
  Lillis"; its text is below.
- **Pfim 0.11.4** (MIT; author Nick Babcock, https://github.com/nickbabcock/Pfim). The package gives no licence expression and
  no licence file, only the address of the project's licence file, which is the MIT licence (the same text at the project's
  0.11.4 tag). That file names no copyright holder; the package's metadata gives the copyright as "Nick Babcock". The text
  is below.
- **zlib.net-mutliplatform 1.1.0** (BSD 3-clause): "Copyright (c) 2006-2007, ComponentAce" (the project's licence file); the
  package's metadata adds "2022-2025 Sjofn LLC" (https://github.com/cinderblocks/zlib.net). No licence file in the package;
  the project's licence text is below.

### Licence texts of the packages

These packages ship no licence file of their own. Each text below is the project's licence file, unchanged.

#### MessagePack and MessagePack.Annotations

From https://github.com/MessagePack-CSharp/MessagePack-CSharp, file LICENSE:

```
MessagePack for C#

MIT License

Copyright (c) 2017 Yoshifumi Kawai and contributors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

---

lz4net

BSD 2-Clause "Simplified" License

Copyright (c) 2013-2017, Milosz Krajewski

All rights reserved.

Redistribution and use in source and binary forms, with or without modification, are permitted provided that the following conditions are met:

Redistributions of source code must retain the above copyright notice, this list of conditions and the following disclaimer.

Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following disclaimer in the documentation and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

---

BufferWriter.cs

Copyright 2019 .NET Foundation

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
```

#### ZLogger

From https://github.com/Cysharp/ZLogger, file LICENSE:

```
MIT License

Copyright (c) 2020 Cysharp, Inc.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

#### Utf8StringInterpolation

From https://github.com/Cysharp/Utf8StringInterpolation, file LICENSE:

```
MIT License

Copyright (c) 2023 Cysharp, Inc.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

#### OggVorbisEncoder

From https://github.com/SteveLillis/.NET-Ogg-Vorbis-Encoder, file LICENSE:

```
MIT License

Copyright (c) 2016 Steve Lillis

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

#### Pfim

From https://github.com/nickbabcock/Pfim, file LICENSE.txt (no copyright line in the file; copyright "Nick Babcock" from the package):

```
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the "Software"), to deal in
the Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies
of the Software, and to permit persons to whom the Software is furnished to do
so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

#### zlib.net-mutliplatform

From https://github.com/cinderblocks/zlib.net, file LICENSE:

```
BSD 3-Clause License

Copyright (c) 2006-2007, ComponentAce
http://www.componentace.com
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

* Redistributions of source code must retain the above copyright notice, this
  list of conditions and the following disclaimer.

* Redistributions in binary form must reproduce the above copyright notice,
  this list of conditions and the following disclaimer in the documentation
  and/or other materials provided with the distribution.

* Neither the name of ComponentAce nor the names of its
  contributors may be used to endorse or promote products derived from
  this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

## Tabler Icons (MIT)

- **Files:** `host/Viewer.Shell/Assets/Icons/*.svg`, the outline icons the interface uses, unchanged (their own comment
  headers are kept). The viewer draws them at a 1.5 px stroke by changing the stroke width and colour in the text when it
  turns them into pictures.
- **Source:** https://github.com/tabler/tabler-icons, release v3.24.0, `icons/outline/`.
- **Copyright:** "Copyright (c) 2020-2024 Paweł Kuna" (the notice in the project's `LICENSE`, kept as it is in
  `ThirdPartyLicenses/LICENSE-TablerIcons.txt`).
- **Licence:** MIT, full text in `ThirdPartyLicenses/LICENSE-TablerIcons.txt`.

## Inter (SIL Open Font License 1.1)

- **Files:** `host/Viewer.Shell/Assets/Fonts/Inter-Regular.ttf` and `Inter-SemiBold.ttf`, unchanged, from the Inter 4.0
  release (https://github.com/rsms/inter, `extras/ttf/`). They are the interface font.
- **Copyright:** "Copyright (c) 2016 The Inter Project Authors (https://github.com/rsms/inter)".
- **Licence:** SIL Open Font License 1.1, full text in `ThirdPartyLicenses/LICENSE-Inter.txt`. The font files are not
  renamed or modified.

## Lucide (ISC)

- **Files:** `host/Viewer.Shell/Assets/Icons/Lucide/*.svg`, the icons of the Reverie interface (the top bar, menus, windows,
  controls and the dock). Each is Lucide's own drawing, written out as the plain SVG Lucide publishes (24-unit grid, stroke 2,
  round caps and joins) from the icon data of lucide-preact 1.39.0; the paths are unchanged. The viewer sets the stroke colour
  in the text when it turns an icon into a picture. `eye.svg`, `eye-off.svg` and `circle-question-mark.svg` (the password
  field's show and hide button and the login screen's Help button) are written out the same way from lucide-static 1.39.0;
  `circle-question-mark` is the icon Feather named `help-circle`, listed among the Feather-derived icons in the licence file.
- **Source:** https://github.com/lucide-icons/lucide (lucide-preact 1.39.0 on npm).
- **Copyright:** "Copyright (c) 2026 Lucide Icons and Contributors"; the icons Lucide took from Feather also carry "Copyright
  (c) 2013-present Cole Bemis" under the MIT licence (both notices kept as they are in `ThirdPartyLicenses/LICENSE-Lucide.txt`).
- **Licence:** ISC, and MIT for the Feather-derived icons listed in that file; full texts in `ThirdPartyLicenses/LICENSE-Lucide.txt`.

## Noto Sans (SIL Open Font License 1.1)

- **Files:** `host/Viewer.Shell/Assets/Fonts/NotoSans-Regular.ttf` and `NotoSans-Bold.ttf`, unchanged, version 2.015. The
  interface falls back to them for characters Inter does not have; for CJK, Arabic, emoji and other scripts it falls back to
  the fonts installed on the system, so none of those are carried.
- **Source:** https://github.com/notofonts/latin-greek-cyrillic.
- **Copyright:** "Copyright 2022 The Noto Project Authors (https://github.com/notofonts/latin-greek-cyrillic)".
- **Licence:** SIL Open Font License 1.1, full text in `ThirdPartyLicenses/LICENSE-NotoSans.txt`. The font files are not
  renamed or modified.

## Login background picture (generated)

- **File:** `host/Viewer.Shell/Assets/Splash/login-splash.jpg`, the picture the login screen shows behind its card.
- **Origin:** generated with ChatGPT for this project by its author, from the author's own prompt, then saved as a JPEG
  with its metadata removed; the picture itself is not edited. It is the author's own work for this project, supplied
  under the repository's own terms; no third-party licence text applies to it.

## Interface sounds (recordings and generated)

- **Files:** `host/Viewer.Shell/Assets/Sounds/ui-*.wav`, the sounds the interface plays. Seventeen are recordings, listed below;
  the other six are generated. Every recording was resampled to 16-bit mono 44.1 kHz and changed in level to match the official
  viewer's loudness for the same event, its peak no higher than -1 dBFS.
- **Public domain (CC0 1.0 Universal).** Each of these is one sound from a pack published on OpenGameArt.org under CC0 1.0, as the
  pack's page states; it was trimmed, played at the speed given and changed in level. CC0 asks for no credit; each is credited
  here by pack, author and page. Licence text: `ThirdPartyLicenses/LICENSE-CC0-1.0.txt`.
  - `ui-alert.wav`: bell_02.ogg from "100 CC0 SFX" by rubberduck, https://opengameart.org/content/100-cc0-sfx (CC0 1.0), speed x1.10.
  - `ui-bad-keystroke.wav`: chips-stack-5.ogg from "54 Casino sound effects (cards, dice, chips)" by Kenney, https://opengameart.org/content/54-casino-sound-effects-cards-dice-chips (CC0 1.0), speed x1.10.
  - `ui-chat-mention.wav`: bell_01.ogg from "100 CC0 SFX" by rubberduck, https://opengameart.org/content/100-cc0-sfx (CC0 1.0), speed x0.90.
  - `ui-click.wav`: bfh1_misc_05.ogg from "75 CC0 breaking / falling / hit sfx" by rubberduck, https://opengameart.org/content/75-cc0-breaking-falling-hit-sfx (CC0 1.0), speed x0.90.
  - `ui-click-release.wav`: bfh1_misc_05.ogg from "75 CC0 breaking / falling / hit sfx" by rubberduck, https://opengameart.org/content/75-cc0-breaking-falling-hit-sfx (CC0 1.0), speed x0.90.
  - `ui-invalid-op.wav`: machine_03.ogg from "100 CC0 SFX" by rubberduck, https://opengameart.org/content/100-cc0-sfx (CC0 1.0), speed x0.95.
  - `ui-money-change-down.wav`: coinsounds011015.wav (one of its coins) from "coin sounds" by syncopika, https://opengameart.org/content/coin-sounds (CC0 1.0), speed x0.90.
  - `ui-money-change-up.wav`: coin.8.ogg from "12 Coin Sound Effects" by StarNinjas, https://opengameart.org/content/12-coin-sound-effects (CC0 1.0), speed x0.90.
  - `ui-object-rez-in.wav`: sfx100v2_air_01.ogg from "100 CC0 SFX #2" by rubberduck, https://opengameart.org/content/100-cc0-sfx-2 (CC0 1.0), speed x0.90.
  - `ui-start-im.wav`: phaserDown2.mp3 from "63 Digital sound effects (lasers, phasers, space etc.)" by Kenney, https://opengameart.org/content/63-digital-sound-effects-lasers-phasers-space-etc (CC0 1.0), speed x0.90.
  - `ui-window-close.wav`: shot_02.ogg from "50 CC0 retro / synth SFX" by rubberduck, https://opengameart.org/content/50-cc0-retro-synth-sfx (CC0 1.0), speed x0.90.
- Generated with ChatGPT (OpenAI) for this project, then trimmed and levelled. OpenAI's terms assign any rights in the output to the user; whatever rights exist are offered under this repository's terms.
  - `ui-new-incoming-im-session.wav`: made with ChatGPT for Reverie by Legion Builds.
  - `ui-object-create.wav`: made with ChatGPT for Reverie by Legion Builds.
  - `ui-object-delete.wav`: made with ChatGPT for Reverie by Legion Builds.
  - `ui-object-rez-out.wav`: made with ChatGPT for Reverie by Legion Builds.
  - `ui-teleport-out.wav`: made with ChatGPT for Reverie by Legion Builds.
  - `ui-window-open.wav`: made with ChatGPT for Reverie by Legion Builds.
- **Generated:** `ui-chat-ping.wav`, `ui-health-reduction-f.wav`, `ui-health-reduction-m.wav`, `ui-restart.wav`,
  `ui-snapshot.wav` and `ui-typing.wav`, and `world-footstep.wav`, are made from code by the generator in `tools/ui-sounds`
  (sine and FM tones, filtered noise, envelopes and a low-pass; recipe table in `UiSoundSynth.cs`), Copyright (c) Legion Builds,
  LGPL-2.1-or-later. Nothing in them is sampled, traced or copied from another sound. They are this project's own generated work;
  no third-party licence applies to them.
- The settings can play a grid's own sound assets instead; those are fetched from the grid at run time and are not part of
  this repository.

## Assets

The manifests of `fixtures/materials`, `fixtures/meshes`, `fixtures/prims`, `fixtures/terrain` and `fixtures/textures` say
"generated in this repo; no third-party content", so those meshes, textures and golden images are generated in this
repository and no outside licence applies to them. `fixtures/primmesher` and `fixtures/sculpts` hold geometry generated
from the BSD-licensed code above over synthetic input (their PROVENANCE.md files say so). `fixtures/colour`,
`fixtures/environment` and `fixtures/orientation` have no manifest that states where they come from. The avatar data folder is kept unchanged in this repository as
`vendor/second-life-character/` (with its `NOTICE.txt` and `UPSTREAM.md`), and the build copies it into the shipped build
(see the next section).

## Avatar data folder (Second Life viewer 26.4 character folder)

- **Files:** the shipped build's `character/` folder: the 96 files of `indra/newview/character/` of the Second Life viewer
  release 26.4 (tag Second_Life_Release#e960123-26.4), unchanged: the avatar definition and skeleton (`avatar_lad.xml`,
  `avatar_skeleton.xml`, `attentions.xml`, `attentionsN.xml`, `genepool.xml`), the body meshes (`*.llm`) and the body
  pictures (`*.tga`). It is copied in when the build is made, from the folder the tests use, and `character/NOTICE.txt`
  goes with it. The viewer reads it to draw avatar bodies; `--character=<folder>` or `NV_CHARACTER_DIR` names another
  folder instead.
- **Licence, as the viewer source's own documents give it:** `doc/LICENSE-source.txt` says that, unless otherwise
  specified, the source code in that distribution is provided under the GNU Lesser General Public License, version 2.1;
  `doc/LICENSE-logos.txt` says: "Linden Research, Inc. ("Linden Lab") licenses the Second Life viewer artwork and other
  works in the files distributed with this Notice under the Creative Commons Attribution-Share Alike 3.0 License", and asks
  that any copy include that notice. The folder's files carry no notice of their own. The definitions and meshes are
  treated as part of the viewer source (LGPL 2.1, `ThirdPartyLicenses/LGPL-2.1-secondlife-viewer.txt`), and the
  pictures as viewer artwork (Creative Commons Attribution-Share Alike 3.0; the notice is copied unchanged to
  `ThirdPartyLicenses/LICENSE-secondlife-viewer-artwork-CC-BY-SA-3.0.txt` and into `character/NOTICE.txt`). The files
  are not changed. Second Life and Linden Lab are trademarks of Linden Research, Inc.; nothing in the folder is a logo.
- **Copyright:** "Second Life(TM) Viewer Artwork. Copyright (C) 2008 Linden Research, Inc." (the artwork notice) and
  "Second Life Viewer Source Code, Copyright (C) 2010, Linden Research, Inc." (the source notice).

## Baking Lab: the ACES fit by Stephen Hill (MIT)

- **Files:** `core/Viewer.Core/World/ToneMap.cs` (`AcesHill`) and the compute shader in
  `host/Viewer.Godot/Presentation/HdrTonemap.cs` use the fitted curve and its two matrices from Baking Lab's `ACES.hlsl`,
  written again in C# and GLSL. Both files carry the source file's credit and the MIT notice in their headers.
- **Source:** https://github.com/TheRealMJP/BakingLab, `BakingLab/ACES.hlsl` ("The code in this file was originally written by
  Stephen Hill (@self_shadow), who deserves all credit for coming up with this fit and implementing it.").
- **Copyright:** "Copyright (c) 2016 MJP" (the project's `LICENSE`, kept as it is in `ThirdPartyLicenses/LICENSE-BakingLab.txt`).
- **Licence:** MIT, full text in `ThirdPartyLicenses/LICENSE-BakingLab.txt`.

The PBR Neutral tone mapper in the same two files and the sky probe's irradiance filtering in `core/Viewer.Core/World/SkyProbe.cs`
are this project's own code, written from published mathematics (the PBR Neutral specification's equations; Hammersley points,
cosine-weighted hemisphere sampling and GPU Gems 3, chapter 20.4), and carry no third-party notice.
