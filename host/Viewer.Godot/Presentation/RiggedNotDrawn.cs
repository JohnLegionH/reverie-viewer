// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Meshing;

namespace Viewer.Godot.Presentation;

/// <summary>A worn rigged mesh that is drawn (reported once for each object, the first time).</summary>
/// <param name="WearerLocalId">The wearer's region-local id.</param>
/// <param name="WearerAgentId">The wearer's agent id.</param>
/// <param name="WearerName">The wearer's name as the region gave it (for the local log only); empty when unknown.</param>
/// <param name="Point">The attachment point id the object is worn at.</param>
/// <param name="Asset">The mesh asset.</param>
/// <param name="Lod">The level of detail it is drawn at (0 lowest .. 3 high).</param>
/// <param name="Joints">How many of the mesh's joints are used (at most the official per-mesh limit).</param>
/// <param name="PlainBecause">Set when the mesh is drawn as a plain mesh at its attachment point instead of through its skin, and why.</param>
public sealed record RiggedDrawn(uint WearerLocalId, System.Guid WearerAgentId, string WearerName, int Point, System.Guid Asset, int Lod, int Joints, RiggedWhy? PlainBecause = null);

/// <summary>A worn rigged mesh that is not drawn, and why (reported once for each object and reason).</summary>
/// <param name="WearerLocalId">The wearer's region-local id.</param>
/// <param name="WearerAgentId">The wearer's agent id.</param>
/// <param name="WearerName">The wearer's name as the region gave it (for the local log only); empty when unknown.</param>
/// <param name="Point">The attachment point id the object is worn at.</param>
/// <param name="Asset">The mesh asset.</param>
/// <param name="Why">The reason.</param>
/// <param name="Temporary">True when the piece is presented again by itself.</param>
public sealed record RiggedNotDrawn(uint WearerLocalId, System.Guid WearerAgentId, string WearerName, int Point, System.Guid Asset, RiggedWhy Why, bool Temporary);
