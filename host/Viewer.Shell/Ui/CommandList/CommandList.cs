// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Reflection;
using System.Text;

namespace Viewer.Shell;

/// <summary>What a command-list result opens: a window, a menu command, or a setting (a Preferences page or one of its rows).</summary>
public enum CommandKind { Window, Command, Setting }

/// <summary>
/// One feature of the feature map: where the official viewer (SL) and Firestorm keep it and the other names people search it by.
/// Places are written as the menus write them ("World > About land"); a cell holds several places separated by " ; ".
/// </summary>
public sealed record FeatureRow(int Row, string Area, string Feature, string Status, string SlPath, string SlShortcut, string FsLocation, IReadOnlyList<string> Aliases)
{
    /// <summary>The official viewer's places, first the main one; "none" and automatic behaviour are left out.</summary>
    public IReadOnlyList<string> SlPlaces => CommandText.Places(SlPath);

    /// <summary>Firestorm's places, first the main one.</summary>
    public IReadOnlyList<string> FsPlaces => CommandText.Places(FsLocation);

    /// <summary>The names the official viewer gives the feature: the last step of each of its places.</summary>
    public IReadOnlyList<string> SlNames => Names(SlPlaces);

    /// <summary>The names Firestorm gives the feature.</summary>
    public IReadOnlyList<string> FsNames => Names(FsPlaces);

    /// <summary>The official viewer's shortcut for the main place, or null.</summary>
    public string? SlKey => CommandText.Places(SlShortcut).FirstOrDefault();

    private static IReadOnlyList<string> Names(IEnumerable<string> places) =>
        places.Select(CommandText.Leaf).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}

/// <summary>The feature map that travels inside the assembly (generated from the map; see the file's first line).</summary>
public static class FeatureMap
{
    private static readonly Lazy<IReadOnlyList<FeatureRow>> _bundled = new(() => Parse(CommandText.Resource("Viewer.Shell.CommandList.feature-map.tsv")));

    public static IReadOnlyList<FeatureRow> Bundled => _bundled.Value;

    /// <summary>Reads the map: a comment line, a header line, then one tab-separated row per feature.</summary>
    public static IReadOnlyList<FeatureRow> Parse(string text)
    {
        var rows = new List<FeatureRow>();
        string[]? header = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            var cells = line.Split('\t');
            if (header is null)
            {
                header = cells;
                continue;
            }
            string Cell(string name) => Array.IndexOf(header, name) is var i and >= 0 && i < cells.Length ? cells[i] : "";
            if (!int.TryParse(Cell("row"), out int row))
                continue;
            rows.Add(new FeatureRow(row, Cell("area"), Cell("feature"), Cell("reverie_status"), Cell("sl_menu_path"), Cell("sl_shortcut"),
                Cell("fs_location"), CommandText.Places(Cell("aliases"))));
        }
        return rows;
    }
}

/// <summary>Where each feature of the map lives in this viewer (targets.tsv): row to targets, plus the official place of entries with no row.</summary>
public sealed record CommandTargets(IReadOnlyDictionary<int, IReadOnlyList<string>> ByRow, IReadOnlyDictionary<string, (string Place, string Shortcut)> SlOf)
{
    private static readonly Lazy<CommandTargets> _bundled = new(() => Parse(CommandText.Resource("Viewer.Shell.CommandList.targets.tsv")));

    public static CommandTargets Bundled => _bundled.Value;

    public static CommandTargets Parse(string text)
    {
        var byRow = new Dictionary<int, IReadOnlyList<string>>();
        var sl = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            var cells = line.Split('\t');
            if (cells[0] == "sl" && cells.Length >= 4)
                sl[cells[1]] = (cells[2], cells[3]);
            else if (cells.Length >= 2 && int.TryParse(cells[0], out int row))
                byRow[row] = cells[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        }
        return new CommandTargets(byRow, sl);
    }
}

/// <summary>The text rules of the command list: places, names, matching and shortcut text.</summary>
public static class CommandText
{
    internal static string Resource(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name) ?? throw new InvalidOperationException("missing resource " + name);
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }

    /// <summary>The places of a cell ("A > B ; C"), without "none" and without automatic behaviour.</summary>
    public static IReadOnlyList<string> Places(string? cell) =>
        string.IsNullOrWhiteSpace(cell) ? [] :
        cell.Split(" ; ").Select(p => p.Trim())
            .Where(p => p.Length > 0 && !p.Equals("none", StringComparison.OrdinalIgnoreCase) && !p.StartsWith("automatic", StringComparison.OrdinalIgnoreCase))
            .ToArray();

    /// <summary>A place without the hidden-menu mark ("[hidden by default]"), as the window shows it.</summary>
    public static string Shown(string place) => place.Replace(" [hidden by default]", "", StringComparison.Ordinal).Trim();

    /// <summary>The name a place gives the feature: its last step, without a toolbar button's quotes, a trailing "..." or a closing note in brackets.</summary>
    public static string Leaf(string place)
    {
        var p = Shown(place);
        int gt = p.LastIndexOf(" > ", StringComparison.Ordinal);
        var leaf = gt >= 0 ? p[(gt + 3)..] : p;
        const string button = "Toolbar button ";
        if (leaf.StartsWith(button, StringComparison.Ordinal))
            leaf = leaf[button.Length..];
        if (leaf.EndsWith(')') && leaf.IndexOf(" (", StringComparison.Ordinal) is var open and > 0)
            leaf = leaf[..open];
        return leaf.Trim().Trim('"').TrimEnd('.', '…').Trim();
    }

    /// <summary>Lower case, "[APP_NAME]" removed, anything but letters and digits made a space, spaces collapsed.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        var t = text.Replace("[APP_NAME]", " ", StringComparison.OrdinalIgnoreCase);
        var sb = new StringBuilder(t.Length);
        bool space = true;
        foreach (char c in t)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
                space = false;
            }
            else if (!space)
            {
                sb.Append(' ');
                space = true;
            }
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>How well a query matches a term, 0 to 100: the whole term, its start, the start of each word, inside it, or every word inside it.</summary>
    public static double Match(string normalizedTerm, string normalizedQuery)
    {
        if (normalizedQuery.Length == 0 || normalizedTerm.Length == 0)
            return 0;
        if (normalizedTerm == normalizedQuery)
            return 100;
        if (normalizedTerm.StartsWith(normalizedQuery, StringComparison.Ordinal))
            return 85;
        var words = normalizedTerm.Split(' ');
        var asked = normalizedQuery.Split(' ');
        if (asked.All(q => words.Any(w => w.StartsWith(q, StringComparison.Ordinal))))
            return 70;
        if (normalizedTerm.Contains(normalizedQuery, StringComparison.Ordinal))
            return 55;
        if (asked.All(q => normalizedTerm.Contains(q, StringComparison.Ordinal)))
            return 40;
        return 0;
    }

    /// <summary>A shortcut as the menus write it ("control|shift|K") as a person reads it ("Ctrl+Shift+K"); null for none.</summary>
    public static string? ShortcutText(string? shortcut)
    {
        if (string.IsNullOrEmpty(shortcut))
            return null;
        return string.Join("+", shortcut.Split('|').Select(p => p.ToLowerInvariant() switch
        {
            "control" => "Ctrl",
            "shift" => "Shift",
            "alt" => "Alt",
            _ => p.Length == 1 ? p.ToUpperInvariant() : p,
        }));
    }
}

/// <summary>
/// One result of the command list: what it opens (<paramref name="Target"/>), its name and place in this viewer, its shortcut, and where
/// the official viewer keeps it (or Firestorm, when the official viewer has no place for it).
/// </summary>
/// <param name="MenuPath">For a menu entry: the ids of the menu and of each entry down to it (the menu bar opens along it); else empty.</param>
public sealed record CommandItem(string Target, CommandKind Kind, string Name, string Place, string? Shortcut, string? CommandId, bool Enabled,
    IReadOnlyList<FeatureRow> Rows, string? ElsewhereViewer, string? ElsewherePlace, string? ElsewhereShortcut, IReadOnlyList<string> MenuPath)
{
    internal IReadOnlyList<(string Term, double Weight)> Terms { get; init; } = [];

    /// <summary>The setting's key for a "pref:" result, or the page for a "prefs:" result; null otherwise.</summary>
    public string? PrefKey => Target.StartsWith("pref:", StringComparison.Ordinal) ? Target[5..] : null;
    public string? PrefPageId => Target.StartsWith("prefs:", StringComparison.Ordinal) ? Target[6..] : null;
}

/// <summary>The results of a search: the best match, then windows, commands and settings, each best first.</summary>
public sealed record CommandGroups(CommandItem? Best, IReadOnlyList<CommandItem> Windows, IReadOnlyList<CommandItem> Commands, IReadOnlyList<CommandItem> Settings)
{
    public static CommandGroups Empty { get; } = new(null, [], [], []);

    /// <summary>Every result in the order the window shows them (the keyboard moves through this order).</summary>
    public IReadOnlyList<CommandItem> Ordered => (Best is { } b ? new[] { b } : []).Concat(Windows).Concat(Commands).Concat(Settings).ToArray();
}

/// <summary>
/// Every window, menu command and setting this viewer has, each with the names the official viewer and Firestorm give it, and the search
/// over them. Built from the menus and Preferences as they are when the list opens, so a result always opens something that is there:
/// a menu entry whose command is not registered, or a target the viewer does not have, is left out.
/// </summary>
public sealed class CommandIndex
{
    private readonly IReadOnlyList<FeatureRow> _map;
    private readonly HashSet<int> _built;

    public IReadOnlyList<CommandItem> Items { get; }

    private CommandIndex(IReadOnlyList<CommandItem> items, IReadOnlyList<FeatureRow> map)
    {
        Items = items;
        _map = map;
        _built = items.SelectMany(i => i.Rows).Select(r => r.Row).ToHashSet();
    }

    /// <summary>Builds the index. <paramref name="registered"/> says whether a command can run at all.</summary>
    public static CommandIndex Build(IReadOnlyList<TopMenuView> menus, Func<string, bool> registered, StringTable strings, IReadOnlyList<PrefDef> prefs,
        IReadOnlyList<string> prefPages, IReadOnlyList<FeatureRow> map, CommandTargets targets)
    {
        var made = new Dictionary<string, (CommandItem Item, List<FeatureRow> Rows)>(StringComparer.Ordinal);
        string prefsTitle = strings.Get("prefs.title");

        void Walk(IEnumerable<TopMenuEntryView> entries, string place, IReadOnlyList<string> path)
        {
            foreach (var e in entries)
            {
                string label = strings.Get(e.LabelKey);
                string here = place + " > " + label;
                var at = path.Append(e.Id).ToArray();
                if (e.Children is { } kids)
                {
                    Walk(kids, here, at);
                    continue;
                }
                if (e.CommandId is not { } command || !registered(command) || made.ContainsKey("menu:" + e.Id))
                    continue;
                var kind = command.StartsWith("floater.", StringComparison.Ordinal) ? CommandKind.Window : CommandKind.Command;
                made["menu:" + e.Id] = (new CommandItem("menu:" + e.Id, kind, label, here, CommandText.ShortcutText(e.Shortcut), command, e.Enabled, [],
                    null, null, null, at), []);
            }
        }
        foreach (var m in menus)
            Walk(m.Entries, strings.Get(m.TitleKey), [m.Id]);

        foreach (var page in prefPages)
        {
            string title = strings.Get("prefs.category." + page);
            made["prefs:" + page] = (new CommandItem("prefs:" + page, CommandKind.Setting, title, prefsTitle + " > " + title, null, null, true, [],
                null, null, null, []), []);
        }
        foreach (var def in prefs)
        {
            string page = def.Page.ToString().ToLowerInvariant();
            if (!prefPages.Contains(page))
                continue;
            string label = strings.Get(def.LabelKey);
            made["pref:" + def.Key] = (new CommandItem("pref:" + def.Key, CommandKind.Setting, label,
                prefsTitle + " > " + strings.Get("prefs.category." + page) + " > " + label, null, null, true, [], null, null, null, []), []);
        }

        var byRow = map.ToDictionary(r => r.Row);
        foreach (var (row, list) in targets.ByRow)
            if (byRow.TryGetValue(row, out var feature))
                foreach (var t in list)
                    if (made.TryGetValue(t, out var m))
                        m.Rows.Add(feature);

        var items = new List<CommandItem>();
        foreach (var (target, (item, rows)) in made)
        {
            string? viewer = null, place = null, key = null;
            if (targets.SlOf.TryGetValue(target, out var own))
                (viewer, place, key) = ("SL", own.Place, own.Shortcut);
            else if (rows.Select(r => r.SlPlaces.FirstOrDefault() is { } p ? (p, r.SlKey) : default).FirstOrDefault(x => x.p is not null) is { p: { } sp } slp)
                (viewer, place, key) = ("SL", sp, slp.SlKey);
            else if (rows.Select(r => r.FsPlaces.FirstOrDefault()).FirstOrDefault(p => p is not null) is { } fp)
                (viewer, place, key) = ("Firestorm", fp, null);
            if (key is not null && key.Equals("none", StringComparison.OrdinalIgnoreCase))
                key = null;

            var terms = new List<(string, double)> { (item.Name, 1.0) };
            if (own.Place is not null)
                terms.Add((CommandText.Leaf(own.Place), 0.95));
            foreach (var r in rows)
            {
                terms.AddRange(r.SlNames.Select(n => (n, 0.95)));
                terms.AddRange(r.Aliases.Select(n => (n, 0.95)));
                terms.AddRange(r.FsNames.Select(n => (n, 0.9)));
                terms.Add((r.Feature, 0.6));
            }
            terms.Add((item.Place, 0.5));
            items.Add(item with
            {
                Rows = rows.Distinct().ToArray(),
                ElsewhereViewer = viewer,
                ElsewherePlace = place is null ? null : CommandText.Shown(place),
                ElsewhereShortcut = key,
                Terms = terms.Select(t => (CommandText.Normalize(t.Item1), t.Item2)).Where(t => t.Item1.Length > 0).ToArray(),
            });
        }
        return new CommandIndex(items, map);
    }

    /// <summary>The result that opens <paramref name="target"/>, or null when the viewer has none now.</summary>
    public CommandItem? Find(string target) => Items.FirstOrDefault(i => i.Target == target);

    private static double Score(IEnumerable<(string Term, double Weight)> terms, string q) =>
        terms.Select(t => CommandText.Match(t.Term, q) * t.Weight).DefaultIfEmpty(0).Max();

    /// <summary>Every result that matches, best first.</summary>
    public IReadOnlyList<(CommandItem Item, double Score)> Rank(string query)
    {
        string q = CommandText.Normalize(query);
        if (q.Length == 0)
            return [];
        // at the same score: a window before a command before a setting, then the result that more of its names match (a feature
        // whose official name the query is, rather than one sharing a name with it), then the shorter name
        return Items.Select(i => (Item: i, Score: Score(i.Terms, q)))
            .Where(x => x.Score > 0)
            .Select(x => (x.Item, x.Score, Hits: x.Item.Terms.Count(t => CommandText.Match(t.Term, q) * t.Weight >= x.Score - 1e-9)))
            .OrderByDescending(x => x.Score).ThenBy(x => x.Item.Kind).ThenByDescending(x => x.Hits)
            .ThenBy(x => x.Item.Name.Length).ThenBy(x => x.Item.Target, StringComparer.Ordinal)
            .Select(x => (x.Item, x.Score))
            .ToArray();
    }

    /// <summary>The results of a search: the best match, then at most <paramref name="perGroup"/> windows, commands and settings.</summary>
    public CommandGroups Search(string query, int perGroup = 6)
    {
        var ranked = Rank(query);
        if (ranked.Count == 0)
            return CommandGroups.Empty;
        var best = ranked[0].Item;
        var rest = ranked.Skip(1).Select(x => x.Item).ToArray();
        return new CommandGroups(best,
            rest.Where(i => i.Kind == CommandKind.Window).Take(perGroup).ToArray(),
            rest.Where(i => i.Kind == CommandKind.Command).Take(perGroup).ToArray(),
            rest.Where(i => i.Kind == CommandKind.Setting).Take(perGroup).ToArray());
    }

    /// <summary>
    /// A feature this viewer does not have yet that the query names at least as well as any result does, so the list can say where the
    /// official viewer keeps it instead of showing nothing; null when there is none.
    /// </summary>
    public FeatureRow? NotBuilt(string query)
    {
        string q = CommandText.Normalize(query);
        if (q.Length == 0)
            return null;
        double best = Rank(query).Select(x => x.Score).DefaultIfEmpty(0).First();
        var hit = _map.Where(r => !_built.Contains(r.Row) && (r.SlPlaces.Count > 0 || r.FsPlaces.Count > 0))
            .Select(r => (Row: r, Score: Score(r.SlNames.Select(n => (CommandText.Normalize(n), 0.95))
                .Concat(r.Aliases.Select(n => (CommandText.Normalize(n), 0.95)))
                .Concat(r.FsNames.Select(n => (CommandText.Normalize(n), 0.9))), q)))
            .Where(x => x.Score >= 50)
            .OrderByDescending(x => x.Score).ThenBy(x => x.Row.Row)
            .FirstOrDefault();
        return hit.Row is not null && hit.Score >= best ? hit.Row : null;
    }
}

/// <summary>The results picked last, newest first, kept in the settings so they survive a restart.</summary>
public sealed class CommandRecents(SettingsService? settings)
{
    public const string Key = "command_list_recent";
    public const int Max = 6;

    private readonly List<string> _targets = (settings?.GetText(Key) ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(Max).ToList();

    public IReadOnlyList<string> Targets => _targets;

    public void Add(string target)
    {
        _targets.Remove(target);
        _targets.Insert(0, target);
        if (_targets.Count > Max)
            _targets.RemoveRange(Max, _targets.Count - Max);
        settings?.SetText(Key, string.Join('\n', _targets));
    }
}

/// <summary>What a key in the command list asks the window to do.</summary>
public enum CommandListAction { None, Open, ShowPlace, Close }

/// <summary>
/// The command list's state: the query, the results shown, and the selected one. With an empty query it shows the recent picks.
/// Up and Down move (wrapping round), Enter opens the selected result, Ctrl+Enter shows the menu or window it lives in, Esc closes.
/// </summary>
public sealed class CommandListState(CommandIndex index, CommandRecents recents)
{
    public CommandIndex Index { get; private set; } = index;
    public string Query { get; private set; } = "";
    public CommandGroups Groups { get; private set; } = CommandGroups.Empty;

    /// <summary>The recent picks shown while the query is empty, newest first (a pick the viewer no longer has is left out).</summary>
    public IReadOnlyList<CommandItem> Recent => recents.Targets.Select(Index.Find).OfType<CommandItem>().ToArray();

    /// <summary>A feature the viewer does not have that the query names, for the line under the results.</summary>
    public FeatureRow? NotBuilt { get; private set; }

    /// <summary>The results in the order the keyboard moves through them.</summary>
    public IReadOnlyList<CommandItem> Shown => Query.Trim().Length == 0 ? Recent : Groups.Ordered;

    public int Selected { get; private set; }

    public CommandItem? Current => Selected >= 0 && Selected < Shown.Count ? Shown[Selected] : null;

    /// <summary>The menus or Preferences changed while the list is open: the results are read again.</summary>
    public void Rebuild(CommandIndex fresh)
    {
        Index = fresh;
        SetQuery(Query);
    }

    public void SetQuery(string query)
    {
        Query = query ?? "";
        Groups = Index.Search(Query);
        NotBuilt = Index.NotBuilt(Query);
        Selected = 0;
    }

    /// <summary>Up (-1) or Down (+1), wrapping round the ends.</summary>
    public void Move(int step)
    {
        int n = Shown.Count;
        if (n == 0)
            return;
        Selected = ((Selected + Math.Sign(step)) % n + n) % n;
    }

    /// <summary>A pointer on a result.</summary>
    public void Select(int index)
    {
        if (index >= 0 && index < Shown.Count)
            Selected = index;
    }

    /// <summary>A key: "Up", "Down", "Enter" or "Escape" (others do nothing). Enter on a disabled result does nothing.</summary>
    public (CommandListAction Action, CommandItem? Item) Key(string key, bool control = false)
    {
        switch (key)
        {
            case "Up": Move(-1); return (CommandListAction.None, null);
            case "Down": Move(1); return (CommandListAction.None, null);
            case "Escape": return (CommandListAction.Close, null);
            case "Enter":
                if (Current is not { } item || (!control && !item.Enabled))
                    return (CommandListAction.None, null);
                recents.Add(item.Target);
                return (control ? CommandListAction.ShowPlace : CommandListAction.Open, item);
            default: return (CommandListAction.None, null);
        }
    }
}
