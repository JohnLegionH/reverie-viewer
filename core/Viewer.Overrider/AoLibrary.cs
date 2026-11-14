// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Globalization;

namespace Viewer.Overrider;

/// <summary>Changes to the user's inventory the set editor asks for. Every call is answered when the grid has taken it.</summary>
public interface IAoInventoryWriter
{
    /// <summary>A new folder; empty when it could not be made.</summary>
    Task<Guid> CreateFolderAsync(Guid parent, string name);

    /// <summary>A link in <paramref name="folder"/> to the item <paramref name="target"/>, with the description given.</summary>
    Task<bool> LinkAsync(Guid folder, Guid target, string description);

    Task<bool> RenameFolderAsync(Guid folder, string name);

    /// <summary>Moves an item or a folder to the Trash. Never purges.</summary>
    Task<bool> MoveToTrashAsync(Guid id, bool isFolder);
}

/// <summary>A set's or a state's folder name: the name and the options after it, each after a colon ("Beach:SO:SM").</summary>
public sealed record AoFolderName(string Name, IReadOnlyList<string> Options)
{
    public static AoFolderName Parse(string folderName)
    {
        string[] parts = folderName.Split(':');
        return new AoFolderName(parts[0], parts.Skip(1).ToArray());
    }

    public string Format() => Options.Count == 0 ? Name : Name + ":" + string.Join(":", Options);
}

/// <summary>Where a reviewed animation goes: a state, Unsorted (no state), or nowhere.</summary>
public readonly record struct AoTarget(AoStateId? State, bool Skip)
{
    public static AoTarget Unsorted => new(null, false);
    public static AoTarget Skipped => new(null, true);
    public static AoTarget To(AoStateId state) => new(state, false);
}

/// <summary>One animation in the review list: what the viewer guessed, and what the user chose (the guess at first).</summary>
public sealed class AoReviewRow(Guid itemId, string name, AoStateId? guess)
{
    public Guid ItemId { get; } = itemId;
    public string Name { get; } = name;
    public AoStateId? Guess { get; } = guess;
    public AoTarget Choice { get; set; } = guess is { } g ? AoTarget.To(g) : AoTarget.Unsorted;
}

/// <summary>What an import made: the new set's folder, how many links, and the names the card gave that the folder lacks.</summary>
public sealed record AoImportResult(Guid SetFolder, int Linked, IReadOnlyList<string> Missing, IReadOnlyList<string> UnknownStates);

/// <summary>
/// Makes and changes animation sets in the inventory, in the layout the Firestorm viewer reads and writes (aoengine.cpp):
/// the sets are folders in #Firestorm/#AO; a set's options follow its name (:SO sit override, :SM smart, :DM no stand in
/// mouse-look, :** the default set); each state is a sub-folder named after the state, with its options after the name
/// (:CT seconds with two decimals, :CY cycle, :RN random); a state holds links to the animations, never copies, with the
/// play order as a number in each link's description. Options this editor does not know are kept as they are.
/// Removing moves to the Trash and never purges. Animations the user sorts as Unsorted go to an "Unsorted" folder in the
/// set, which Firestorm passes over. Every await resumes where it was started (in the viewer, its main thread), so the
/// inventory is read and the next change asked for from there.
/// </summary>
public sealed class AoLibrary(IAoInventory inventory, IAoInventoryWriter writer)
{
    public const string FirestormFolder = "#Firestorm", AoFolder = "#AO", UnsortedFolder = "Unsorted";

    // set options this editor sets; any other option of a set (":**" for the default set) is kept after them
    private static readonly string[] SetFlags = ["SO", "SM", "DM"];

    private static readonly AoSet StateNames = new(Guid.Empty);

    private readonly IAoInventory _inventory = inventory;
    private readonly IAoInventoryWriter _writer = writer;

    /// <summary>The #AO folder, or empty while there is none.</summary>
    public Guid FindAoFolder()
    {
        Guid top = Child(_inventory.RootId, FirestormFolder);
        return top == Guid.Empty ? Guid.Empty : Child(top, AoFolder);
    }

    /// <summary>The #AO folder, made (with #Firestorm) when missing.</summary>
    public async Task<Guid> EnsureAoFolderAsync()
    {
        Guid top = Child(_inventory.RootId, FirestormFolder);
        if (top == Guid.Empty)
            top = await _writer.CreateFolderAsync(_inventory.RootId, FirestormFolder);
        if (top == Guid.Empty)
            return Guid.Empty;
        Guid ao = Child(top, AoFolder);
        return ao != Guid.Empty ? ao : await _writer.CreateFolderAsync(top, AoFolder);
    }

    /// <summary>The set folders, with their parsed names.</summary>
    public IReadOnlyList<(Guid Id, AoFolderName Name)> SetFolders()
    {
        Guid ao = FindAoFolder();
        return ao == Guid.Empty ? [] : _inventory.Folders(ao).Select(f => (f.Id, AoFolderName.Parse(f.Name))).ToArray();
    }

    /// <summary>A new empty set; empty when the name is empty, has a colon, or is taken.</summary>
    public async Task<Guid> NewSetAsync(string name)
    {
        name = name.Trim();
        if (!NameUsable(name, except: Guid.Empty))
            return Guid.Empty;
        Guid ao = await EnsureAoFolderAsync();
        return ao == Guid.Empty ? Guid.Empty : await _writer.CreateFolderAsync(ao, name);
    }

    /// <summary>A copy of a set under a new name: the same states and options, with new links to the same animations.</summary>
    public async Task<Guid> DuplicateSetAsync(Guid set, string name)
    {
        name = name.Trim();
        if (!NameUsable(name, except: Guid.Empty) || SetName(set) is not { } sourceName)
            return Guid.Empty;
        // the copy is never the default set
        var copyName = new AoFolderName(name, sourceName.Options.Where(o => o != "**").ToArray());
        Guid copy = await _writer.CreateFolderAsync(FindAoFolder(), copyName.Format());
        if (copy != Guid.Empty)
            await CopyContentsAsync(set, copy);
        return copy;
    }

    private async Task CopyContentsAsync(Guid from, Guid to)
    {
        foreach (var item in _inventory.Items(from).Where(i => i.IsLink))
            await _writer.LinkAsync(to, item.LinkedId, item.Description);
        foreach (var folder in _inventory.Folders(from))
        {
            Guid made = await _writer.CreateFolderAsync(to, folder.Name);
            if (made != Guid.Empty)
                await CopyContentsAsync(folder.Id, made);
        }
    }

    /// <summary>Renames a set and keeps its options.</summary>
    public async Task<bool> RenameSetAsync(Guid set, string name)
    {
        name = name.Trim();
        if (!NameUsable(name, except: set) || SetName(set) is not { } found)
            return false;
        return await _writer.RenameFolderAsync(set, (found with { Name = name }).Format());
    }

    /// <summary>Moves a set's folder to the Trash; refused unless the user confirmed.</summary>
    public Task<bool> RemoveSetAsync(Guid set, bool confirmed) =>
        confirmed && SetFolders().Any(s => s.Id == set) ? _writer.MoveToTrashAsync(set, isFolder: true) : Task.FromResult(false);

    /// <summary>Links the items into the state's folder (made when missing), after the animations already there. A null
    /// state means Unsorted. Returns how many links were made.</summary>
    public async Task<int> AddToStateAsync(Guid set, AoStateId? state, IReadOnlyList<Guid> items)
    {
        if (items.Count == 0)
            return 0;
        Guid folder = await StateFolderAsync(set, state);
        if (folder == Guid.Empty)
            return 0;
        int order = NextSortOrder(folder);
        int made = 0;
        foreach (Guid item in items)
        {
            if (await _writer.LinkAsync(folder, item, (order + made).ToString(CultureInfo.InvariantCulture)))
                made++;
        }
        return made;
    }

    /// <summary>Moves one link of a state to the Trash.</summary>
    public Task<bool> RemoveFromStateAsync(Guid link) => _writer.MoveToTrashAsync(link, isFolder: false);

    public async Task<bool> SetSetOptionsAsync(Guid set, bool sitOverride, bool smart, bool mouselookStandDisable)
    {
        if (SetName(set) is not { } found)
            return false;
        var options = new List<string>();
        if (sitOverride) options.Add("SO");
        if (smart) options.Add("SM");
        if (mouselookStandDisable) options.Add("DM");
        options.AddRange(found.Options.Where(o => !SetFlags.Contains(o)));
        return await _writer.RenameFolderAsync(set, (found with { Options = options }).Format());
    }

    public async Task<bool> SetStateOptionsAsync(Guid set, AoStateId state, bool cycle, bool random, float cycleTime)
    {
        Guid folder = await StateFolderAsync(set, state);
        if (folder == Guid.Empty)
            return false;
        var name = AoFolderName.Parse(NameOf(set, folder));
        var options = new List<string>();
        if (cycleTime > 0)
            options.Add("CT" + cycleTime.ToString("F2", CultureInfo.InvariantCulture));
        if (cycle) options.Add("CY");
        if (random) options.Add("RN");
        options.AddRange(name.Options.Where(o => o is not ("CY" or "RN") && !o.StartsWith("CT", StringComparison.Ordinal)));
        return await _writer.RenameFolderAsync(folder, (name with { Options = options }).Format());
    }

    /// <summary>The review list for animations dropped on the overrider: each with the state its name suggests.</summary>
    public static IReadOnlyList<AoReviewRow> Review(IEnumerable<(Guid Id, string Name)> animations) =>
        animations.Select(a => new AoReviewRow(a.Id, a.Name, AoStateGuess.Guess(a.Name))).ToArray();

    /// <summary>Writes the reviewed list into the set: nothing is written before this. Returns how many links were made.</summary>
    public async Task<int> ApplyReviewAsync(Guid set, IReadOnlyList<AoReviewRow> rows)
    {
        int made = 0;
        foreach (var group in rows.Where(r => !r.Choice.Skip).GroupBy(r => r.Choice.State))
            made += await AddToStateAsync(set, group.Key, group.Select(r => r.ItemId).ToArray());
        return made;
    }

    /// <summary>A new set from a folder holding a ZHAO-II notecard and its animations, named after the folder.</summary>
    public async Task<AoImportResult> ImportAsync(Guid folder, string setName, string notecardText)
    {
        var card = ZhaoNotecard.Parse(notecardText);
        var byName = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var item in _inventory.Items(folder).Where(i => i.IsAnimation))
            byName.TryAdd(item.Name, item.IsLink ? item.LinkedId : item.Id);

        var missing = new List<string>();
        foreach (string name in card.Lines.SelectMany(l => l.Steps).SelectMany(s => s))
        {
            if (!byName.ContainsKey(name) && !missing.Contains(name))
                missing.Add(name);
        }
        if (!card.Lines.Any(l => l.Steps.Any(s => s.Any(byName.ContainsKey))))
            return new AoImportResult(Guid.Empty, 0, missing, card.UnknownStates);

        Guid set = await NewSetAsync(UniqueSetName(setName));
        if (set == Guid.Empty)
            return new AoImportResult(Guid.Empty, 0, missing, card.UnknownStates);

        int linked = 0;
        foreach (var line in card.Lines)
        {
            var steps = line.Steps.Select(s => s.Where(byName.ContainsKey).Select(n => byName[n]).ToArray()).Where(s => s.Length > 0).ToArray();
            if (steps.Length == 0)
                continue;   // nothing of this state is in the folder: no state folder is made for it
            Guid stateFolder = await StateFolderAsync(set, line.State);
            if (stateFolder == Guid.Empty)
                continue;
            foreach (var members in steps)
            {
                int order = NextSortOrder(stateFolder);
                if (members.Length == 1)
                {
                    if (await _writer.LinkAsync(stateFolder, members[0], order.ToString(CultureInfo.InvariantCulture)))
                        linked++;
                    continue;
                }
                // animations that play together: a group folder, "Group <n>:OR<order>", as Firestorm names it
                int groups = _inventory.Folders(stateFolder).Count(f => AoFolderName.Parse(f.Name).Options.Any(o => o.StartsWith("OR", StringComparison.Ordinal)));
                Guid group = await _writer.CreateFolderAsync(stateFolder, $"Group {groups + 1}:OR{order.ToString(CultureInfo.InvariantCulture)}");
                if (group == Guid.Empty)
                    continue;
                for (int m = 0; m < members.Length; m++)
                {
                    if (await _writer.LinkAsync(group, members[m], m.ToString(CultureInfo.InvariantCulture)))
                        linked++;
                }
            }
        }
        return new AoImportResult(set, linked, missing, card.UnknownStates);
    }

    // ---------------- helpers ----------------

    /// <summary>The parsed name of a set folder in #AO; null when the folder is not a set.</summary>
    private AoFolderName? SetName(Guid set) => SetFolders().Where(s => s.Id == set).Select(s => s.Name).FirstOrDefault();

    private Guid Child(Guid parent, string name) =>
        parent == Guid.Empty ? Guid.Empty : _inventory.Folders(parent).FirstOrDefault(f => f.Name == name).Id;

    private bool NameUsable(string name, Guid except) =>
        name.Length > 0 && !name.Contains(':') && !SetFolders().Any(s => s.Id != except && string.Equals(s.Name.Name, name, StringComparison.OrdinalIgnoreCase));

    private string UniqueSetName(string name)
    {
        name = name.Replace(':', ' ').Trim();
        if (name.Length == 0)
            name = "Imported";
        string candidate = name;
        for (int n = 2; !NameUsable(candidate, Guid.Empty); n++)
            candidate = $"{name} {n}";
        return candidate;
    }

    private string NameOf(Guid parent, Guid folder) => _inventory.Folders(parent).FirstOrDefault(f => f.Id == folder).Name ?? "";

    /// <summary>The folder of a state in a set (Unsorted for null), made when missing.</summary>
    private async Task<Guid> StateFolderAsync(Guid set, AoStateId? state)
    {
        foreach (var folder in _inventory.Folders(set))
        {
            string first = AoFolderName.Parse(folder.Name).Name;
            bool match = state is { } s ? StateNames.GetStateByName(first)?.Num == s : first == UnsortedFolder;
            if (match)
                return folder.Id;
        }
        string name = state is { } id ? StateNames.GetState(id).Name : UnsortedFolder;
        return await _writer.CreateFolderAsync(set, name);
    }

    /// <summary>The play order a new step of the state gets: one after the highest number its links and groups carry.</summary>
    private int NextSortOrder(Guid stateFolder)
    {
        int highest = -1;
        foreach (var item in _inventory.Items(stateFolder).Where(i => i.IsLink))
        {
            if (int.TryParse(item.Description, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
                highest = Math.Max(highest, n);
            else
                highest++;
        }
        foreach (var folder in _inventory.Folders(stateFolder))
        {
            foreach (string option in AoFolderName.Parse(folder.Name).Options)
            {
                if (option.StartsWith("OR", StringComparison.Ordinal) && int.TryParse(option[2..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
                    highest = Math.Max(highest, n);
            }
        }
        return highest + 1;
    }
}
