// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Threading.Tasks;
using Viewer.Core.Session;
using Viewer.Shell;

namespace Viewer.Godot;

/// <summary>
/// The person's own hover height: kept per account, drawn on their own avatar at once, and stated to the region, again in every region they
/// reach, because a region only knows what it is told. While nothing waits to be sent and the window is closed, a frame costs a few tests of
/// flags; otherwise the region's support is read once a second.
/// </summary>
public partial class Main
{
    private HoverHeightWindow? _hover;
    private ViewerSession? _hoverFor;
    private bool _hoverToSend;
    private float? _hoverKept;
    private ulong _hoverNextLook;
    private bool _hoverSupported;   // as last read from the region; nothing is drawn or kept back from others until the region has said it takes a height
    private bool _hoverRefused;
    private int _hoverTries;
    private const int HoverSendTries = 3;
    private Presentation.AvatarPresenter? _hoverAvatars;

    private bool HoverOpen => _shell is not null && _shell.Shell.Floaters.IsOpen(HoverHeightFeature.FloaterId);

    private void AttachHover(ViewerSession session)
    {
        if (_shell is null)
            return;
        if (_hover is null)
        {
            _hover = HoverHeightFeature.Install(_shell);
            _hover.Opened = ShowHover;
            _hover.Changed = OnHoverChanged;
        }
        _hoverFor = session;
        var settings = UserSettings(null);
        var setting = Settings.HoverHeightOf(session.AgentId);
        _hoverKept = settings.Contains(setting.Key) ? settings.Get(setting) : null;
        _hoverToSend = _hoverKept is not null;
        _hoverSupported = _hoverRefused = false;
        _hoverTries = 0;
        session.RegionChanged += _ => { _hoverSupported = _hoverRefused = false; _hoverTries = 0; _hoverToSend = _hoverKept is not null; };
    }

    private void ShowHover()
    {
        if (_hover is null || _hoverFor is not { } session)
            return;
        _hover.ShowHeight(_hoverKept ?? 0f);
        if (!_hoverRefused)
            _hover.ForgetRefusal();
        _hover.ShowSupport(session.HoverHeightSupported);
    }

    private void OnHoverChanged(float metres, bool finished)
    {
        if (_hoverFor is not { } session || !session.HoverHeightSupported)
            return;
        _hoverKept = metres;
        if (!finished)
            return;
        UserSettings(null).Set(Settings.HoverHeightOf(session.AgentId), metres);
        _hoverSupported = true;
        _hoverRefused = false;
        _hoverTries = 0;
        _hoverToSend = true;
    }

    private void UpdateHover()
    {
        if (_hoverFor is not { } session || session.IsEnded)
            return;
        if (_hoverAvatars is { } avatars)
            avatars.OwnHover = _hoverSupported ? _hoverKept : null;   // a presentation made later (a new region) is given it too
        if (!_hoverToSend && !HoverOpen)
            return;
        ulong now = global::Godot.Time.GetTicksMsec();
        if (now < _hoverNextLook)
            return;
        _hoverNextLook = now + 1000;
        bool supported = _hoverSupported = session.HoverHeightSupported;
        if (HoverOpen && !_hoverRefused)
        {
            _hover?.ForgetRefusal();   // a new region, or a new try: the earlier refusal is no longer the news
            _hover?.ShowSupport(supported);
        }
        if (_hoverToSend && supported && _hoverKept is { } metres)
        {
            _hoverToSend = false;
            _ = SendHover(session, metres);
        }
    }

    private async Task SendHover(ViewerSession session, float metres)
    {
        bool sent = await session.SetHoverHeightAsync(metres);
        if (sent || _hoverFor != session || _hoverKept != metres)
            return;
        if (++_hoverTries < HoverSendTries)
        {
            _hoverNextLook = global::Godot.Time.GetTicksMsec() + 5000UL * (ulong)_hoverTries;
            _hoverToSend = true;
            return;
        }
        _hoverRefused = true;
        _hover?.ShowRefused();   // also while the window is closed: it says so when it is next opened
    }
}
