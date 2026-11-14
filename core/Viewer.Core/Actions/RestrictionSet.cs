// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text;

namespace Viewer.Core.Actions;

/// <summary>
/// The restrictions currently in force, as a veto point: kinds of intent that are refused while a restriction holds them.
/// Adding one while a held intent runs ends that hold at once (<see cref="IChangingVeto"/>). Safe to use from any thread.
/// </summary>
public sealed class RestrictionSet : IChangingVeto
{
    private readonly object _lock = new();
    private readonly HashSet<ActionKind> _kinds = new();
    private bool _all;

    public event Action? Changed;

    public void Restrict(ActionKind kind)
    {
        lock (_lock)
        {
            if (!_kinds.Add(kind))
                return;
        }
        Changed?.Invoke();
    }

    public void Lift(ActionKind kind)
    {
        lock (_lock)
        {
            if (!_kinds.Remove(kind))
                return;
        }
        Changed?.Invoke();
    }

    public void RestrictAll()
    {
        lock (_lock)
        {
            if (_all)
                return;
            _all = true;
        }
        Changed?.Invoke();
    }

    public void LiftAll()
    {
        lock (_lock)
        {
            if (!_all && _kinds.Count == 0)
                return;
            _all = false;
            _kinds.Clear();
        }
        Changed?.Invoke();
    }

    public bool IsRestricted(ActionKind kind)
    {
        lock (_lock) return _all || _kinds.Contains(kind);
    }

    public VetoDecision Check(ActionIntent intent, ActionPhase phase) =>
        IsRestricted(intent.Kind) ? VetoDecision.Refuse("restricted." + SnakeCase(intent.Kind.ToString())) : VetoDecision.Allow;

    private static string SnakeCase(string name)
    {
        var sb = new StringBuilder();
        foreach (char c in name)
        {
            if (char.IsUpper(c) && sb.Length > 0)
                sb.Append('_');
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }
}
