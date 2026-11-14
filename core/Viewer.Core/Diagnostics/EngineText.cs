// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Diagnostics;

/// <summary>
/// Free text from the engine (a Godot error, warning or printed message), as a log field value (ADR-019). It is never
/// rendered raw: <see cref="ToString"/> hides it, and only <see cref="Redactor.Render"/> reads it, applying the engine
/// scrub (URL paths, "$1$" digests AND session-id-shaped values: engine text has no legitimate reason to carry one).
/// So no engine text can reach a sink without passing redaction.
/// </summary>
public sealed class EngineText(string text)
{
    internal string Text { get; } = text ?? "";

    public override string ToString() => "[engine-text]";
}
