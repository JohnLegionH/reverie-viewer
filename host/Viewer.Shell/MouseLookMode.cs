// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
namespace Viewer.Shell;

/// <summary>
/// Whether the view is mouse-look, and what takes it in and out, as the official viewer does it (behaviour only, in our own
/// words): the View menu's Mouselook item, with the M key as its shortcut, toggles it (llviewermenu.cpp, LLViewMouselook,
/// menu_viewer.xml); turning the wheel in past the closest zoom enters it (llagentcamera.cpp, cameraOrbitIn); turning the wheel
/// out leaves it (lltoolcomp.cpp, LLToolCompGun::handleScrollWheel); the window regaining focus leaves it (llagent.cpp,
/// onAppFocusGained) and so does minimising (llviewerwindow.cpp, the minimise handler); the screen tells the user to press
/// Escape to leave, and the official source read for this has no Escape handler we could find, so Escape leaves here as that
/// text says. The EnableMouselook setting (on by default) turns the whole thing off. No engine type; main thread only.
/// </summary>
public sealed class MouseLookMode
{
    /// <summary>Whether mouse-look is allowed (the official viewer's EnableMouselook setting, on by default); when it is off nothing enters it.</summary>
    public bool Allowed { get; set; } = true;

    /// <summary>Whether the view is mouse-look.</summary>
    public bool Active { get; private set; }

    /// <summary>Raised with the new state each time the view changes.</summary>
    public event Action<bool>? Changed;

    private void Set(bool on)
    {
        if (on && !Allowed) return;
        if (!on) Forced = false;
        if (Active == on) return;
        Active = on;
        Changed?.Invoke(on);
    }

    /// <summary>True while the view is mouse-look because a seat asked for it (and the person has not left it since).</summary>
    public bool Forced { get; private set; }

    /// <summary>A seat asks for mouse-look: the view enters it (when allowed).</summary>
    public void Force()
    {
        if (!Allowed || Active) return;   // a mouse-look the person already has is theirs: it is not marked as the seat's
        Forced = true;
        Set(true);
    }

    /// <summary>The seat no longer asks (the avatar stood): the view stays as it is, as in the official viewer, and only the mark that a
    /// seat caused it is cleared; the usual exits end it from here.</summary>
    public void ReleaseForce() => Forced = false;

    /// <summary>The M key.</summary>
    public void Toggle() => Set(!Active);

    /// <summary>The wheel was turned towards the screen; <paramref name="pastClosest"/>: the zoom it asked for is closer than the
    /// closest the third-person camera goes.</summary>
    public void WheelIn(bool pastClosest)
    {
        if (pastClosest && !Active) Set(true);
    }

    /// <summary>The wheel was turned away from the screen.</summary>
    public void WheelOut() => Set(false);

    /// <summary>The Escape key.</summary>
    public void Escape() => Set(false);

    /// <summary>The window gained focus.</summary>
    public void FocusGained() => Set(false);

    /// <summary>The window was minimised.</summary>
    public void Minimized() => Set(false);

    /// <summary>Leaves mouse-look from outside the rules above (a logout, a new session).</summary>
    public void Leave() => Set(false);
}

/// <summary>
/// Puts the view into mouse-look when the own avatar sits on a seat that asks for it. Entry happens once when the seat's request
/// begins (a person who then leaves mouse-look by the usual exits stays out until the next sit). Behaviour: the sit answer carries the
/// request, and the view goes to mouse-look when the sit completes (official viewer: llviewermessage.cpp, llvoavatar.cpp sitOnObject,
/// llagentcamera.cpp). On stand the official viewer clears only the seat's camera (llvoavatar.cpp getOffObject) and walking resets the
/// view without changing the camera mode (llagent.cpp moveAt, llagentcamera.cpp resetView), so mouse-look stays after standing until
/// the usual exits; so it does here. Main thread only.
/// </summary>
public sealed class SeatMouseLook
{
    private readonly MouseLookMode _mode;

    public SeatMouseLook(MouseLookMode mode) => _mode = mode;

    /// <summary>Every frame: whether the avatar sits, and whether the seat forces mouse-look.</summary>
    public void Update(bool sitting, bool seatForcesMouseLook)
    {
        bool wants = sitting && seatForcesMouseLook;
        if (wants && !_wanted)
            _mode.Force();
        else if (!wants && _wanted)
            _mode.ReleaseForce();
        _wanted = wants;
    }

    private bool _wanted;
}
