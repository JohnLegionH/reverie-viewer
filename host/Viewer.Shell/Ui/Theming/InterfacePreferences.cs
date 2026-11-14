// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>What happened when the person pressed Save.</summary>
public enum SaveOutcome
{
    /// <summary>The theme was stored for the account.</summary>
    Saved,
    /// <summary>A readability row is too low: nothing is stored until the person confirms (saving is never blocked).</summary>
    NeedsConfirmation,
    /// <summary>The settings file could not be written.</summary>
    Failed,
}

/// <summary>
/// The Interface page of Preferences without the engine: the saved choice, the choice being edited, what it looks like
/// (tokens, readability), and Save, Cancel, Reset to preset, Import and Export. The page draws from it and sends the person's
/// changes to it; each change raises <see cref="Changed"/> so the whole interface can restyle while the person is still editing.
/// </summary>
public sealed class InterfacePreferences(ThemeChoice saved, UiColour? gridAccent, bool scaleOwn = false, int displayPercent = 100)
{
    /// <summary>True once the person has set a scale of their own (it then wins over the display's scale).</summary>
    public bool ScaleOwn => _scaleOwn;

    /// <summary>The scale the interface uses: the person's own, else the display's scale (within the interface's range).</summary>
    public int EffectiveScale => _scaleOwn ? Working.ScalePercent : Math.Clamp(displayPercent, InterfaceSize.MinPercent, InterfaceSize.MaxPercent);

    private bool _scaleOwn = scaleOwn, _savedScaleOwn = scaleOwn;

    /// <summary>The page again with another grid accent: the saved look, with the size and the display's scale as they were.</summary>
    public InterfacePreferences WithGridAccent(UiColour? accent) => new(Saved, accent, _savedScaleOwn, displayPercent);

    public ThemeChoice Saved { get; private set; } = saved;

    public ThemeChoice Working { get; private set; } = saved;

    /// <summary>The accent the grid published, or null when it has none.</summary>
    public UiColour? GridAccent { get; } = gridAccent;

    public event Action? Changed;

    public ThemeTokens Tokens => ThemeResolver.Resolve(Working, GridAccent);

    public IReadOnlyList<ReadabilityRow> Readability => Viewer.Shell.Readability.Check(Tokens);

    public bool IsChanged => Working != Saved || _scaleOwn != _savedScaleOwn;

    /// <summary>Makes the preset the working one: the person's colour changes are dropped; the scale and the grid choice stay.</summary>
    public void SelectPreset(string id) =>
        Edit(Working with
        {
            Preset = id, Accent = null, Panel = null, Bar = null, Text = null, TextSecondary = null, ImportedPack = null,
        });

    public void SetAccent(UiColour? colour) => Edit(Working with { Accent = colour });

    public void SetPanel(UiColour? colour) => Edit(Working with { Panel = colour });

    public void SetBar(UiColour? colour) => Edit(Working with { Bar = colour });

    public void SetText(UiColour? colour) => Edit(Working with { Text = colour });

    public void SetTextSecondary(UiColour? colour) => Edit(Working with { TextSecondary = colour });

    /// <summary>The range of the interface size the person may choose: the official viewer's UI size slider runs 0.75 to 4.0 times.</summary>
    public const int MinScalePercent = 75, MaxScalePercent = 400;

    public void SetScale(int percent)
    {
        percent = Math.Clamp(percent, MinScalePercent, MaxScalePercent);
        if (!_scaleOwn && percent == EffectiveScale)
            return;   // nothing the person sees changes: the display's scale keeps being followed
        _scaleOwn = true;   // a scale the person sets is theirs from now on
        var next = Working with { ScalePercent = percent };
        if (next == Working)
            Changed?.Invoke();   // the same number, but now the person's own
        else
            Edit(next);
    }

    public void SetUseGridAccent(bool on) => Edit(Working with { UseGridAccent = on });

    /// <summary>Takes a theme file as the working choice. False, with the reason, when the file is refused (nothing changes).</summary>
    public bool Import(string fileText, out string? error)
    {
        var imported = ThemeChoice.Import(fileText, out error);
        if (imported is null)
            return false;
        Edit(imported with { ScalePercent = Working.ScalePercent, UseGridAccent = Working.UseGridAccent });
        return true;
    }

    /// <summary>The working look as the text of a theme file.</summary>
    public string Export(string name) => ThemePack.Write("exported", name, Tokens);

    /// <summary>Back to the preset's own colours: every colour change and the grid accent are dropped; the scale stays.</summary>
    public void ResetToPreset() =>
        Edit(Working with
        {
            Accent = null, Panel = null, Bar = null, Text = null, TextSecondary = null, UseGridAccent = false,
        });

    public SaveOutcome Save(SettingsService settings, string accountKey, bool confirmed)
    {
        if (!confirmed && Readability.Any(r => !r.Readable))
            return SaveOutcome.NeedsConfirmation;
        if (!ThemeStore.Save(settings, accountKey, Working, _scaleOwn))
            return SaveOutcome.Failed;
        Saved = Working;
        _savedScaleOwn = _scaleOwn;
        return SaveOutcome.Saved;
    }

    /// <summary>Drops every unsaved change.</summary>
    public void Cancel()
    {
        bool onlyOwnChanged = _scaleOwn != _savedScaleOwn && Working == Saved;
        _scaleOwn = _savedScaleOwn;
        Edit(Saved);
        if (onlyOwnChanged)
            Changed?.Invoke();
    }

    private void Edit(ThemeChoice next)
    {
        if (next == Working)
            return;
        Working = next;
        Changed?.Invoke();
    }
}
