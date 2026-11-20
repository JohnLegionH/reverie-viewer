// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell.Inventory;

/// <summary>
/// A newly made inventory item waits to be named: the official viewer selects it once its row is listed and starts renaming it (the folder
/// view's needsAutoRename flag, set when an item is created from the New menu and cleared at the first rebuild that shows the selection;
/// llinventorypanel.cpp, llviewerinventory.cpp). The grid's answer and the row can arrive some time apart, so the request is kept until the
/// row is shown, and dropped after <see cref="GiveUpSeconds"/> so a row that never comes cannot start a rename much later.
/// </summary>
public sealed class AutoRename
{
    public const double GiveUpSeconds = 10;

    private Guid _pending;
    private DateTimeOffset _since;

    /// <summary>Asks for the item to be renamed once it is listed; a newer request replaces an older one.</summary>
    public void Want(Guid id, DateTimeOffset now)
    {
        _pending = id;
        _since = now;
    }

    /// <summary>The item to start renaming now (it is among <paramref name="shown"/>), once; empty while it is not listed or after the wait ran out.</summary>
    public Guid Take(IEnumerable<Guid> shown, DateTimeOffset now)
    {
        if (_pending == Guid.Empty)
            return Guid.Empty;
        if ((now - _since).TotalSeconds > GiveUpSeconds)
        {
            _pending = Guid.Empty;
            return Guid.Empty;
        }
        if (!shown.Contains(_pending))
            return Guid.Empty;
        var id = _pending;
        _pending = Guid.Empty;
        return id;
    }
}
