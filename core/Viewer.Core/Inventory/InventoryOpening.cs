// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Inventory;

/// <summary>Which inventory items a double-click or Enter opens in a window of their own.</summary>
public static class InventoryOpening
{
    /// <summary>Notecards, textures, scripts and gestures open in their editors; the official viewer's open action for a gesture shows its
    /// gesture editor (llinventorybridge.cpp LLGestureBridgeAction::doIt, chosen for AT_GESTURE in LLInvFVBridgeAction::createAction), and a
    /// landmark opens its place information, from which it can be teleported to (LLLandmarkBridgeAction::doIt).</summary>
    public static bool Opens(ItemKind kind) => kind is ItemKind.Notecard or ItemKind.Texture or ItemKind.Script or ItemKind.Gesture or ItemKind.Landmark;
}
