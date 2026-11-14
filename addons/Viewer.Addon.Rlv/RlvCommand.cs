// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Addon.Rlv;

/// <summary>What a command's parameter (after the '=') makes of it.</summary>
public enum RlvParamType { Add, Remove, Clear, Force, Reply, Unknown }

/// <summary>
/// One RLV command, <c>behaviour[:option]=param</c>. The parsing follows the RestrainedLove API as RLVa reads it (Firestorm's
/// rlvhelper.cpp, RlvCommand::parseCommand and its constructor): the behaviour ends at the first ':' before the '=', a missing
/// behaviour or parameter makes the command invalid (except <c>clear</c>, which may stand alone), and the parameter decides
/// the type: n or add, y or rem, force, a number (a reply channel), and anything else is invalid. Written from that
/// behaviour; no code was copied.
/// </summary>
public sealed record RlvCommand(string Behaviour, string Option, string Param, RlvParamType Type)
{
    /// <summary>The command as it is kept and reported: the behaviour, and the option after a ':' when there is one.</summary>
    public string AsString => Option.Length > 0 ? Behaviour + ":" + Option : Behaviour;

    /// <summary>Reads one command (already lower case, without the '@'); null when it is not a well-formed command.</summary>
    public static RlvCommand? Parse(string command)
    {
        if (string.IsNullOrEmpty(command))
            return null;
        int idxParam = command.IndexOf('=');
        int idxOption = idxParam > 0 ? command.IndexOf(':') : -1;
        if (idxOption > idxParam - 1)
            idxOption = -1;
        if (idxOption == 0 || idxParam == 0)
            return null;
        string behaviour = command[..(idxOption != -1 ? idxOption : idxParam == -1 ? command.Length : idxParam)];
        if (idxParam == -1 || idxParam == command.Length - 1)
            return behaviour == "clear" ? new RlvCommand("clear", "", "", RlvParamType.Clear) : null;
        string option = idxOption != -1 && idxOption + 1 != idxParam ? command[(idxOption + 1)..idxParam] : "";
        string param = command[(idxParam + 1)..];
        RlvParamType type = param switch
        {
            "n" or "add" => RlvParamType.Add,
            "y" or "rem" => RlvParamType.Remove,
            _ when behaviour == "clear" => RlvParamType.Clear,
            "force" => RlvParamType.Force,
            _ when int.TryParse(param, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out _) => RlvParamType.Reply,
            _ => RlvParamType.Unknown,
        };
        return type == RlvParamType.Unknown ? null : new RlvCommand(behaviour, option, param, type);
    }

    /// <summary>The commands in an owner-say line: null when the line is not an RLV line (it must start with '@' and be longer
    /// than three characters); otherwise the line without its '@', in lower case, split at commas with empty parts dropped.</summary>
    public static IReadOnlyList<string>? Split(string ownerSay)
    {
        if (ownerSay is null || ownerSay.Length <= 3 || ownerSay[0] != '@')
            return null;
        return ownerSay[1..].ToLowerInvariant().Split(',', StringSplitOptions.RemoveEmptyEntries);
    }
}
