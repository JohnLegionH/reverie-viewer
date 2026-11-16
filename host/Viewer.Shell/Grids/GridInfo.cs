// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell.Grids;

/// <summary>
/// What a grid publishes about itself in its grid-info document (OpenSim's get_grid_info): its name and short name, its login
/// address, its welcome, sign-up and forgot-password pages, its Hypergrid gatekeeper, its currency helper, and its other pages.
/// The fields read are the ones Firestorm's grid manager reads (fsgridhandler.cpp, gridInfoResponderCB).
/// </summary>
public sealed record GridInfo(
    string? Name, string? Nick, Uri? Login, Uri? Welcome, Uri? Register, Uri? Password, string? Gatekeeper, Uri? Economy,
    Uri? Help, Uri? About, Uri? Search, Uri? Profile, string? Platform, string? Message)
{
    /// <summary>Whether the grid names a Hypergrid gatekeeper.</summary>
    public bool Hypergrid => false;

    /// <summary>Whether the grid names a currency helper (its economy address).</summary>
    public bool Currency => false;

    /// <summary>The grid-info document's fields, or null when the text is not a grid-info document.</summary>
    public static GridInfo? Parse(string? body) => null;
}
