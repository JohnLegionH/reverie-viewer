// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>What the Escape key did.</summary>
public enum EscapeResult { None, ReleasedTextEntry, ClosedFloater, AnsweredModal }

/// <summary>
/// Who gets the keyboard: typing or the avatar. The avatar's movement keys count only while the window has focus and no text
/// field is being typed in; Escape first lets go of a text field, then closes the focused floater; a click on the world takes
/// the keyboard back from a text field. Used from the engine's main thread only.
/// </summary>
public sealed class InputFocus
{
    private readonly FloaterManager _floaters;
    private bool _window, _text;

    public InputFocus(FloaterManager floaters) => _floaters = floaters;

    /// <summary>Esc while a modal alert is up: answers it and returns true. Null or false: no alert took it.</summary>
    public Func<bool>? ModalEscape { get; set; }

    public bool WindowFocused => _window;
    public bool TextEntryActive => _text;

    /// <summary>True while the keyboard drives the avatar.</summary>
    public bool MovementAllowed => _window && !_text;

    /// <summary>Whether the arrow keys still move the avatar while a text field has the keyboard (the official ArrowKeysAlwaysMove).</summary>
    public bool ArrowKeysAlwaysMove
    {
        get => _arrowKeysAlwaysMove;
        set => Change(() => _arrowKeysAlwaysMove = value);
    }

    private bool _arrowKeysAlwaysMove;

    /// <summary>Whether a key press may move the avatar now: any movement key while <see cref="MovementAllowed"/>, an arrow key also while typing when <see cref="ArrowKeysAlwaysMove"/> is on.</summary>
    public bool MovementAllowedFor(bool arrowKey) => _window && (!_text || (arrowKey && ArrowKeysAlwaysMove));

    /// <summary>Whether the avatar's controls accept a key now: the window has the focus and either no text field has the keyboard or arrow keys always move.</summary>
    public bool DriverFocused => _window && (!_text || _arrowKeysAlwaysMove);

    /// <summary>Raised when <see cref="DriverFocused"/> changes, with its new value (false: release every held key).</summary>
    public event Action<bool>? DriverFocusedChanged;

    /// <summary>Raised when <see cref="MovementAllowed"/> changes, with its new value (false: release every held key).</summary>
    public event Action<bool>? MovementAllowedChanged;

    /// <summary>Raised when the shell wants the engine to take the keyboard focus off the text field.</summary>
    public event Action? TextReleaseRequested;

    public void SetWindowFocused(bool focused) => Change(() => _window = focused);

    public void SetTextEntryActive(bool active) => Change(() => _text = active);

    /// <summary>Escape: first let go of a text field in use (the engine reports it lost afterwards), then close the focused floater. A field
    /// that answers Escape itself (a name prompt, which cancels on it) is left to do so: <paramref name="fieldTakesEscape"/>.</summary>
    public EscapeResult OnEscape(bool fieldTakesEscape = false)
    {
        // an alert that must be answered first takes Esc: it answers the alert and never closes the window behind it
        if (ModalEscape?.Invoke() == true)
            return EscapeResult.AnsweredModal;
        if (_text && fieldTakesEscape)
            return EscapeResult.None;
        if (_text)
        {
            TextReleaseRequested?.Invoke();
            return EscapeResult.ReleasedTextEntry;
        }
        return _floaters.CloseFocused() ? EscapeResult.ClosedFloater : EscapeResult.None;
    }

    /// <summary>The pointer went down on the world (not on any panel). True when that took the keyboard from a text field.</summary>
    public bool OnWorldPointerDown()
    {
        if (!_text)
            return false;
        TextReleaseRequested?.Invoke();
        return true;
    }

    private void Change(Action set)
    {
        bool before = MovementAllowed, driverBefore = DriverFocused;
        set();
        bool after = MovementAllowed, driverAfter = DriverFocused;
        if (before != after)
            MovementAllowedChanged?.Invoke(after);
        if (driverBefore != driverAfter)
            DriverFocusedChanged?.Invoke(driverAfter);
    }
}
