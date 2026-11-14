// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Globalization;

namespace Viewer.Addon.Rlv;

/// <summary>What the engine asks of the viewer: replies, forced actions and whether an object is still there.</summary>
public interface IRlvSink
{
    /// <summary>Says a reply on a chat channel as the user, past every restriction.</summary>
    void Reply(int channel, string text);

    /// <summary>Sits the user on an object, past the issuing object's own restrictions; false when refused or not sent.</summary>
    bool ForceSit(Guid issuer, Guid objectId);

    /// <summary>Stands the user up, past the issuing object's own restrictions; false when refused or not sent.</summary>
    bool ForceStand(Guid issuer);

    /// <summary>Teleports the user to a global position in metres, past the issuing object's own restrictions.</summary>
    bool ForceTeleport(Guid issuer, double x, double y, double z);

    /// <summary>Whether the object is in the viewer's object list (worn, or rezzed in the region).</summary>
    bool ObjectPresent(Guid objectId);

    /// <summary>The restrictions changed: the host enforces the new set.</summary>
    void Changed();
}

/// <summary>What a command came to, as RLVa's return codes name them.</summary>
public enum RlvResult { Success, SuccessUnset, SuccessDuplicate, FailedSyntax, FailedOption, FailedParam, FailedUnknown }

/// <summary>One restriction an object holds, as the Restrictions window lists it.</summary>
public sealed record RlvRestriction(Guid ObjectId, string ObjectName, string Behaviour, string Option, bool Enforced, double SetAt);

/// <summary>
/// The RLV engine: the owner-say commands of the user's own objects are parsed, their restrictions kept per object, queries
/// answered on their channels and forced actions passed on. The rules follow RLVa (Firestorm's rlvhandler.cpp and
/// rlvhelper.cpp: processCommand, processClearCommand, onGC, the @getstatus and @notify forms), written from that behaviour.
/// </summary>
public sealed class RlvEngine
{
    /// <summary>The RestrainedLove API level answered by @version and @versionnum (RLVa's, rlvdefines.h).</summary>
    public const int ApiMajor = 3, ApiMinor = 4, ApiPatch = 3;

    /// <summary>Seconds between garbage collections (RLVa's GC timer).</summary>
    public const double CollectSeconds = 30;

    /// <summary>Collections an object that was never seen in the object list survives (RLVa: 20, about ten minutes).</summary>
    public const int MaxMisses = 20;

    /// <summary>The behaviours this viewer enforces now. Everything else RLVa knows is accepted and listed as not enforced yet.</summary>
    private static readonly HashSet<string> Enforced = new(StringComparer.Ordinal)
    {
        "detach", "sit", "unsit", "sittp", "standtp", "tplm", "tploc", "tplure", "accepttp",
        "sendchat", "recvchat", "redirchat", "emote", "chatshout", "chatwhisper", "chatnormal", "sendchannel",
        "sendim", "recvim", "startim", "notify",
    };

    /// <summary>The behaviours RLVa takes with =n/=y (its behaviour dictionary, rlvhelper.cpp), by name only.</summary>
    private static readonly HashSet<string> KnownAddRem = new(StringComparer.Ordinal)
    {
        "acceptpermission", "accepttp", "accepttprequest", "addattach", "addoutfit", "allowidle", "alwaysrun", "attachallthis",
        "attachallthis_except", "attachthis", "attachthis_except", "buy", "camavdist", "camdistmax", "camdistmin", "camtextures",
        "camunlock", "camzoommax", "camzoommin", "chatnormal", "chatshout", "chatwhisper", "detach", "detachallthis",
        "detachallthis_except", "detachthis", "detachthis_except", "edit", "editattach", "editobj", "editworld", "emote",
        "fartouch", "fly", "interact", "jump", "notify", "pay", "permissive", "recvchat", "recvchatfrom", "recvemote",
        "recvemotefrom", "recvim", "recvimfrom", "redirchat", "rediremote", "remattach", "remoutfit", "rez", "sendchannel",
        "sendchannel_except", "sendchat", "sendgesture", "sendim", "sendimto", "setcam", "setcam_avdistmax", "setcam_avdistmin",
        "setcam_fovmax", "setcam_fovmin", "setcam_origindistmax", "setcam_origindistmin", "setcam_textures", "setcam_unlock",
        "setdebug", "setenv", "setgroup", "setoverlay", "setoverlay_touch", "share", "showhovertext", "showhovertextall",
        "showhovertexthud", "showhovertextworld", "showinv", "showloc", "showminimap", "shownames", "shownametags",
        "shownearby", "showself", "showselfhead", "showworldmap", "sit", "sitground", "sittp", "standtp", "startim",
        "startimto", "temprun", "touchall", "touchattach", "touchattachother", "touchattachself", "touchfar", "touchhud",
        "touchme", "touchthis", "touchworld", "tplm", "tploc", "tplocal", "tplure", "tprequest", "unsit", "viewnote",
        "viewscript", "viewtexture", "viewtransparent", "viewwireframe",
    };

    /// <summary>Exceptions and options that must be an avatar's (or group's) id when given.</summary>
    private static readonly HashSet<string> IdOptions = new(StringComparer.Ordinal)
    {
        "sendim", "recvim", "startim", "recvchat", "tplure", "accepttp", "sendimto", "recvimfrom", "startimto", "recvchatfrom",
    };

    private sealed class Held(string behaviour, string option, double setAt)
    {
        public string Behaviour { get; } = behaviour;
        public string Option { get; } = option;
        public double SetAt { get; } = setAt;
        public string AsString => Option.Length > 0 ? Behaviour + ":" + Option : Behaviour;
    }

    private sealed class Holder(Guid id)
    {
        public Guid Id { get; } = id;
        public string Name { get; set; } = "";
        public List<Held> Commands { get; } = [];
        public bool Seen { get; set; }
        public int Misses { get; set; }
    }

    private readonly IRlvSink _sink;
    private readonly string _viewerName;
    private readonly Version _implementation;
    private readonly List<Holder> _holders = [];
    private double _lastCollect = double.NegativeInfinity;
    private bool _changed;

    public RlvEngine(IRlvSink sink, string viewerName, Version implementation)
    {
        _sink = sink;
        _viewerName = viewerName;
        _implementation = implementation;
    }

    /// <summary>Commands heard that this viewer knows of but has not built yet (force and reply commands), newest last, at most ten.</summary>
    public IReadOnlyList<string> NotBuilt => _notBuilt;
    private readonly List<string> _notBuilt = [];

    /// <summary>Every restriction held, by object in the order first heard, then in the order set.</summary>
    public IReadOnlyList<RlvRestriction> Restrictions =>
        _holders.SelectMany(h => h.Commands.Select(c => new RlvRestriction(h.Id, h.Name, c.Behaviour, c.Option, IsEnforced(c.Behaviour), c.SetAt))).ToArray();

    /// <summary>Processes an owner-say line of one of the user's objects. Empty when the line is not an RLV line.</summary>
    public IReadOnlyList<RlvResult> Process(Guid objectId, string objectName, string ownerSay, double now)
    {
        if (RlvCommand.Split(ownerSay) is not { } parts)
            return [];
        var results = new List<RlvResult>(parts.Count);
        foreach (string part in parts)
            results.Add(ProcessOne(objectId, objectName, part, now));
        Publish();
        return results;
    }

    private RlvResult ProcessOne(Guid objectId, string objectName, string text, double now)
    {
        if (RlvCommand.Parse(text) is not { } command)
            return RlvResult.FailedSyntax;
        return command.Type switch
        {
            RlvParamType.Add => Add(objectId, objectName, command, now),
            RlvParamType.Remove => Remove(objectId, command),
            RlvParamType.Clear => Clear(objectId, command.Param),
            RlvParamType.Force => Force(objectId, command),
            RlvParamType.Reply => Reply(objectId, command),
            _ => RlvResult.FailedSyntax,
        };
    }

    // ---------------- restrictions ----------------

    private RlvResult Add(Guid objectId, string objectName, RlvCommand command, double now)
    {
        if (!IsKnown(command.Behaviour))
            return RlvResult.FailedUnknown;
        if (!OptionOk(command))
            return RlvResult.FailedOption;
        var holder = _holders.FirstOrDefault(h => h.Id == objectId);
        if (holder is null)
        {
            holder = new Holder(objectId);
            _holders.Add(holder);
        }
        if (objectName.Length > 0)
            holder.Name = objectName;
        if (holder.Commands.Any(c => c.Behaviour == command.Behaviour && c.Option == command.Option))
            return RlvResult.SuccessDuplicate;
        holder.Commands.Add(new Held(command.Behaviour, command.Option, now));
        _changed = true;
        Notify(command.AsString, "=" + command.Param);
        return RlvResult.Success;
    }

    private RlvResult Remove(Guid objectId, RlvCommand command)
    {
        if (!IsKnown(command.Behaviour))
            return RlvResult.FailedUnknown;
        var holder = _holders.FirstOrDefault(h => h.Id == objectId);
        int index = holder?.Commands.FindIndex(c => c.Behaviour == command.Behaviour && c.Option == command.Option) ?? -1;
        if (holder is null || index < 0)
            return RlvResult.SuccessUnset;
        holder.Commands.RemoveAt(index);
        if (holder.Commands.Count == 0)
            _holders.Remove(holder);
        _changed = true;
        Notify(command.AsString, "=" + command.Param);
        return RlvResult.Success;
    }

    /// <summary>@clear and @clear=&lt;text&gt;: every command of the object, or those whose text contains the filter.</summary>
    private RlvResult Clear(Guid objectId, string filter)
    {
        if (_holders.FirstOrDefault(h => h.Id == objectId) is { } holder)
        {
            int removed = holder.Commands.RemoveAll(c => filter.Length == 0 || c.AsString.Contains(filter, StringComparison.Ordinal));
            if (holder.Commands.Count == 0)
                _holders.Remove(holder);
            _changed |= removed > 0;
        }
        Notify("clear", "");
        return RlvResult.Success;
    }

    /// <summary>Drops every restriction (the add-on stops).</summary>
    public void ClearAll()
    {
        if (_holders.Count == 0)
            return;
        _holders.Clear();
        _changed = true;
        Publish();
    }

    private static bool OptionOk(RlvCommand command)
    {
        string option = command.Option;
        string bare = command.Behaviour.EndsWith("_sec", StringComparison.Ordinal) ? command.Behaviour[..^4] : command.Behaviour;
        if (option.Length == 0)
            return bare is not ("redirchat" or "rediremote" or "notify");
        if (IdOptions.Contains(bare))
            return Guid.TryParse(option, out _);
        if (bare is "sendchannel" or "redirchat" or "rediremote" or "sendchannel_except")
            return int.TryParse(option, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _);
        if (bare == "notify")
            return int.TryParse(option.Split(';')[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int channel) && channel > 0;
        return true;
    }

    // ---------------- forced actions ----------------

    private RlvResult Force(Guid objectId, RlvCommand command)
    {
        switch (command.Behaviour)
        {
            case "sit":
                if (!Guid.TryParse(command.Option, out var seat))
                    return RlvResult.FailedOption;
                _sink.ForceSit(objectId, seat);
                return RlvResult.Success;
            case "unsit":
                if (command.Option.Length > 0)
                    return RlvResult.FailedOption;
                _sink.ForceStand(objectId);
                return RlvResult.Success;
            case "tpto":
            {
                var parts = command.Option.Split('/');
                if (parts.Length != 3 || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
                    || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y)
                    || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double z))
                    return RlvResult.FailedOption;
                _sink.ForceTeleport(objectId, x, y, z);
                return RlvResult.Success;
            }
            default:
                NoteNotBuilt(command);
                return RlvResult.FailedUnknown;
        }
    }

    // ---------------- queries ----------------

    private RlvResult Reply(Guid objectId, RlvCommand command)
    {
        if (!int.TryParse(command.Param, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int channel) || channel <= 0 || channel == int.MaxValue)
            return RlvResult.FailedParam;
        string? reply = command.Behaviour switch
        {
            "version" => VersionText(legacy: true),
            "versionnew" => VersionText(legacy: false),
            "versionnum" => command.Option == "impl" ? ImplementationNumber() : command.Option.Length == 0 ? VersionNumber() : null,
            "versionnumbl" => VersionNumber(),   // with no blacklist, the number alone
            "getstatus" => Status(_holders.Where(h => h.Id == objectId), command.Option),
            "getstatusall" => Status(_holders, command.Option),
            _ => null,
        };
        if (reply is null)
        {
            NoteNotBuilt(command);
            return RlvResult.FailedUnknown;
        }
        _sink.Reply(channel, reply);
        return RlvResult.Success;
    }

    /// <summary>@getstatus[:&lt;filter&gt;[;&lt;separator&gt;]]: each command whose text holds the filter, each preceded by the separator ('/').</summary>
    private static string Status(IEnumerable<Holder> holders, string option)
    {
        var tokens = option.Split(';');
        string filter = tokens[0];
        string separator = tokens.Length > 1 && tokens[1].Length > 0 ? tokens[1] : "/";
        return string.Concat(holders.SelectMany(h => h.Commands).Where(c => filter.Length == 0 || c.AsString.Contains(filter, StringComparison.Ordinal))
            .Select(c => separator + c.AsString));
    }

    /// <summary>The @version and @versionnew reply: RLVa's form, naming this viewer and its own RLV version, never another viewer.</summary>
    public string VersionText(bool legacy) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{(legacy ? "RestrainedLife" : "RestrainedLove")} viewer v{ApiMajor}.{ApiMinor}.{ApiPatch} ({_viewerName} RLV {_implementation.Major}.{_implementation.Minor}.{Math.Max(0, _implementation.Build)})");

    /// <summary>The @versionnum reply: the API level as RLVa writes it (major, then minor, patch and build in two digits each).</summary>
    public string VersionNumber() => string.Create(CultureInfo.InvariantCulture, $"{ApiMajor}{ApiMinor:00}{ApiPatch:00}00");

    /// <summary>The @versionnum:impl reply: this RLV's own version in the same form.</summary>
    private string ImplementationNumber() =>
        string.Create(CultureInfo.InvariantCulture, $"{_implementation.Major}{_implementation.Minor:00}{Math.Max(0, _implementation.Build):00}00");

    // ---------------- notify ----------------

    /// <summary>Sends "/&lt;command&gt;&lt;suffix&gt;" to every @notify listener whose filter the command's text holds (RLVa's
    /// RlvBehaviourNotifyHandler::sendNotification).</summary>
    private void Notify(string text, string suffix)
    {
        foreach (var listener in _holders.SelectMany(h => h.Commands).Where(c => c.Behaviour == "notify").ToArray())
        {
            var tokens = listener.Option.Split(';');
            if (!int.TryParse(tokens[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int channel))
                continue;
            string filter = tokens.Length > 1 ? tokens[1] : "";
            if (filter.Length == 0 || text.Contains(filter, StringComparison.OrdinalIgnoreCase))
                _sink.Reply(channel, "/" + text + suffix);
        }
    }

    // ---------------- garbage collection ----------------

    /// <summary>Drops the restrictions of objects that are gone, at most every <see cref="CollectSeconds"/>: an object seen in
    /// the object list and now missing goes at once; one never seen goes after <see cref="MaxMisses"/> collections (RLVa's onGC).</summary>
    public void Collect(double now)
    {
        if (now - _lastCollect < CollectSeconds)
            return;
        _lastCollect = now;
        foreach (var holder in _holders.ToArray())
        {
            if (_sink.ObjectPresent(holder.Id))
            {
                holder.Seen = true;
                continue;
            }
            if (holder.Seen || ++holder.Misses > MaxMisses)
            {
                _holders.Remove(holder);
                _changed = true;
                Notify("clear", "");
            }
        }
        Publish();
    }

    private void Publish()
    {
        if (!_changed)
            return;
        _changed = false;
        _sink.Changed();
    }

    private void NoteNotBuilt(RlvCommand command)
    {
        _notBuilt.Add("@" + command.AsString + "=" + command.Param);
        if (_notBuilt.Count > 10)
            _notBuilt.RemoveAt(0);
    }

    // ---------------- the behaviours ----------------

    /// <summary>Whether this viewer enforces a behaviour now; a known one that is not enforced is accepted and listed as such.</summary>
    public static bool IsEnforced(string behaviour) =>
        Enforced.Contains(behaviour) || behaviour.EndsWith("_sec", StringComparison.Ordinal) && Enforced.Contains(behaviour[..^4]);

    /// <summary>Whether a behaviour is one RLVa takes with =n/=y (with or without its "_sec" form).</summary>
    public static bool IsKnown(string behaviour) =>
        KnownAddRem.Contains(behaviour) || behaviour.EndsWith("_sec", StringComparison.Ordinal) && KnownAddRem.Contains(behaviour[..^4]);
}
