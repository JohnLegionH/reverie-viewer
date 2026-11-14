# Viewer.Motion notice

This assembly moves and draws the user's own avatar. It is ported from the Second Life Viewer source, version 7.2.3, and is licensed under the GNU Lesser General Public License, version 2.1 only, the licence of that source. Nothing else in this repository changes licence. The licence text is `ThirdPartyLicenses/LGPL-2.1-secondlife-viewer.txt`.

The rest of the repository is licensed under the same licence (`LICENSE`), the project's own code under version 2.1 or any later version; the sentence above means that this assembly's licence does not change the licence of any other file.

Every ported file keeps the source files' notices ("Copyright (C) 2010, Linden Research, Inc.") word for word at its top, followed by the line "Adapted 2026 by Legion Builds".
Since then it has been brought up to the Second Life Viewer release 26.4 (release tag Second_Life_Release#e960123-26.4, commit e960123230d521e8557a9582100bebcac36a7ca2): every official function the files below follow was compared with that release, and the files whose behaviour differs there were changed to match (the table below lists the source files).

| File here | Second Life Viewer 26.4 source files |
|---|---|
| `AgentControls.cs` | `indra/newview/llviewerinput.cpp`, `llagent.cpp` (`moveUp`, `onAnimStop`), `llmoveview.cpp`, `llviewermessage.cpp` (`send_agent_update`), `indra/llcommon/indra_constants.h` |
| `MouseLook.cs` | `indra/newview/lltoolgun.cpp`, `llviewerwindow.cpp`, `llagent.cpp` |
| `ScriptControls.cs` | `indra/newview/llagent.cpp` (`processScriptControlChange`), `indra/llcommon/indra_constants.h` |
| `ObjectMotion.cs` | `indra/newview/llviewerobject.cpp` (`idleUpdate`, `interpolateLinearMotion`, `processUpdateMessage`), `lldrawable.cpp` (`updateXform`), `indra/llcommon/llcriticaldamp.cpp` (`calcInterpolant`) |
| `InterpolatedObject.cs` | `indra/newview/llviewerobject.cpp` (`idleUpdate`, `interpolateLinearMotion`, `applyAngularVelocity`, `processUpdateMessage`), `llworld.cpp` (`clipToVisibleRegions`, `getMinAllowedZ`), `indra/llmath/xform.h` |
| `AgentCamera.cs` | `indra/newview/llagentcamera.cpp`, `lltoolfocus.cpp`, `llviewerinput.cpp` |
| `CameraControls.cs` | `indra/newview/lltoolfocus.cpp`, `llviewerinput.cpp`, `app_settings/key_bindings.xml` |
| `KeyBindingTable.cs` | `indra/newview/llviewerinput.cpp`, `app_settings/key_bindings.xml` |
| `MouseSteering.cs` | `indra/newview/lltoolpie.cpp` |
| `IAvatarMotion.cs` | none (new interface) |
| `OfficialOwnAvatarReplay.cs` | `indra/newview/llviewerobject.cpp` (`processUpdateMessage`, `idleUpdate`, `interpolateLinearMotion`), `llviewerobjectlist.cpp` (`processObjectUpdate`), `llappviewer.cpp` (`idle`), `lldrawable.cpp` (`updateXform`) |
