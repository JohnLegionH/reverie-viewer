// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Viewer.Core.World;
using Viewer.Godot.Presentation;
using Viewer.Shell;

namespace Viewer.Godot;

/// <summary>
/// --tr-viewreuse: one terrain view serves three presenters one after another, as the lifecycle and kept-collections cases use it. The view
/// makes its own ground material, so it is the view that releases it, when it is cleared (a presenter's teardown clears it): a presenter that
/// disposed the view's material left the next presenter drawing with a disposed object (ObjectDisposedException). Checks: every presenter
/// draws its chunks on the shared view; the view makes no more than one ground material while a scene is on it and none is left alive once
/// the last presenter is gone (the leak the soak found: one StandardMaterial3D per scene run); the engine reports no errors.
/// </summary>
public partial class TestRender
{
    private async Task<int> ViewReuseRun()
    {
        long engineErrors0 = Main.EngineLog?.Errors ?? 0;
        var rig = MakeRig(new Vector2I(256, 256));
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        int materials0 = WrapperCounts()?.GetValueOrDefault("StandardMaterial3D") ?? -1;   // before the view exists: its own ground material is counted against the scene
        var view = (ServerTerrainView)MakeView("server", rig);
        if (materials0 < 0) { _failures.Add("the engine binding's object list could not be read, so the material count is unknown"); return 1; }
        var passes = new System.Collections.Generic.List<object>();
        for (int pass = 1; pass <= 3; pass++)
        {
            int chunks = -1, during = -1;
            string? error = null;
            WorldPresenter? presenter = null;
            Loaded? loaded = null;
            try
            {
                loaded = FromTerrain(SyntheticTerrain.Make("view-reuse", 256, 256), "view reuse", "generated");
                presenter = new WorldPresenter(loaded.World, view);
                presenter.Flush();
                chunks = view.ChunkCount;
                during = (WrapperCounts()?.GetValueOrDefault("StandardMaterial3D") ?? -1) - materials0;
                if (chunks <= 0) _failures.Add($"pass {pass}: the shared view holds {chunks} chunks after the scene was presented");
                if (during > 1) _failures.Add($"pass {pass}: {during} StandardMaterial3D objects alive while the scene is on the view (one ground material at most)");
            }
            catch (Exception e)
            {
                error = e.GetType().Name + ": " + e.Message;
                _failures.Add($"pass {pass}: presenting on the shared view threw {error}");
            }
            finally
            {
                try { presenter?.Dispose(); }
                catch (Exception e) { _failures.Add($"pass {pass}: disposing the presenter threw {e.GetType().Name}: {e.Message}"); }
                loaded?.Dispose();
            }
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            int left = (WrapperCounts()?.GetValueOrDefault("StandardMaterial3D") ?? -1) - materials0;
            if (left != 0) _failures.Add($"pass {pass}: {left} StandardMaterial3D objects alive after the presenter was disposed (the view's ground material must go with its teardown)");
            if (view.ChunkCount != 0) _failures.Add($"pass {pass}: the view still holds {view.ChunkCount} chunks after the presenter was disposed");
            passes.Add(new { pass, chunks, materials_during = during, materials_left = left, error });
        }
        long engineErrors = (Main.EngineLog?.Errors ?? 0) - engineErrors0;
        if (engineErrors != 0) _failures.Add($"{engineErrors} engine errors while three presenters used one view");
        _result["viewreuse"] = new { passes, engine_errors = engineErrors, materials_before = materials0 };
        return _failures.Count == 0 ? 0 : 1;
    }
}
