// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>The pages of Preferences that hold ordinary settings (the Interface page is the theme and has its own logic).</summary>
public enum PrefPage { General, Graphics, Sound, Chat, MoveView, Privacy, Cache, Building }

/// <summary>When a changed setting takes effect.</summary>
public enum PrefApply
{
    /// <summary>At once, with no restart (also as a preview before Save).</summary>
    Live,
    /// <summary>At the next start of the viewer; the page says so.</summary>
    Restart,
    /// <summary>The next time something reads it (the next login).</summary>
    NextUse,
}

/// <summary>Whether one value is kept for every account on this computer or one per account.</summary>
public enum PrefScope { Global, PerAccount }

/// <summary>What kind of control a setting needs.</summary>
public enum PrefKind { Switch, Whole, Real, Choice, Folder, Text }

/// <summary>
/// One setting that appears on a Preferences page: where it is shown, its string-table key, its default and range, whether it is
/// kept per account, and when a change takes effect. Reading and writing go through <see cref="SettingsService"/>; a per-account
/// setting is stored under its key plus the account.
/// </summary>
public abstract class PrefDef(string key, PrefPage page, PrefKind kind, PrefApply apply, PrefScope scope, object defaultValue)
{
    public string Key { get; } = key;
    public PrefPage Page { get; } = page;
    public PrefKind Kind { get; } = kind;
    public PrefApply Apply { get; } = apply;
    public PrefScope Scope { get; } = scope;
    public object Default { get; } = defaultValue;

    /// <summary>The string-table key of the label.</summary>
    public string LabelKey => "prefs.s." + Key;

    /// <summary>Smallest and largest value of a number setting (0 for others).</summary>
    public double Min { get; protected init; }
    public double Max { get; protected init; }

    /// <summary>The ids a choice setting offers (empty for others).</summary>
    public IReadOnlyList<string> Choices { get; protected init; } = [];

    /// <summary>The key the value is stored under: the setting's key, plus the account for a per-account one.</summary>
    public string StoredKey(string? account) => Scope == PrefScope.PerAccount ? Key + "_" + account : Key;

    /// <summary>The value now (a preview counts), or the default.</summary>
    public abstract object Read(SettingsService settings, string? account);

    /// <summary>Sets the value (as a preview when <paramref name="preview"/>). False when the setting refuses it.</summary>
    public abstract bool Write(SettingsService settings, string? account, object value, bool preview);
}

/// <summary>A number or switch setting built on a typed <see cref="Setting{T}"/>, so the rule that features read with is the rule the page edits with.</summary>
public sealed class TypedPref<T> : PrefDef where T : struct
{
    private readonly Setting<T> _setting;

    public TypedPref(Setting<T> setting, PrefPage page, PrefApply apply, PrefScope scope = PrefScope.Global, double min = 0, double max = 0)
        : base(setting.Key, page, KindOf(), apply, scope, setting.Default)
    {
        _setting = setting;
        Min = min;
        Max = max;
    }

    private static PrefKind KindOf() => typeof(T) == typeof(bool) ? PrefKind.Switch : typeof(T) == typeof(int) ? PrefKind.Whole : PrefKind.Real;

    private Setting<T> For(string? account) => Scope == PrefScope.PerAccount ? _setting with { Key = StoredKey(account) } : _setting;

    public override object Read(SettingsService settings, string? account) => settings.Get(For(account));

    public override bool Write(SettingsService settings, string? account, object value, bool preview) =>
        value is T typed && (preview ? settings.SetPreview(For(account), typed) : settings.Set(For(account), typed));
}

/// <summary>A choice (one of a fixed list of ids) or a folder (any text) kept as text under its key.</summary>
public sealed class TextPref : PrefDef
{
    public TextPref(string key, PrefPage page, PrefApply apply, string defaultValue, IReadOnlyList<string>? choices = null, PrefKind kind = PrefKind.Choice)
        : base(key, page, kind, apply, PrefScope.Global, defaultValue)
    {
        Choices = choices ?? [];
    }

    /// <summary>Whether <paramref name="id"/> may be stored (any text for a folder).</summary>
    public bool Accepts(string id) => Kind == PrefKind.Folder || (Kind == PrefKind.Text ? Guid.TryParse(id, out _) : Choices.Contains(id));

    public override object Read(SettingsService settings, string? account) =>
        settings.GetText(Key) is { } text && Accepts(text) ? text : (string)Default;

    public override bool Write(SettingsService settings, string? account, object value, bool preview) =>
        value is string id && Accepts(id) && (preview ? settings.SetTextPreview(Key, id) : settings.SetText(Key, id));
}
