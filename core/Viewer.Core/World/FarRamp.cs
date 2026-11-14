// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.World;

/// <summary>The far distance sent in the agent's updates. Behaviour taken from the official viewer's agent update (llviewermessage.cpp, send_agent_update):
/// while the agent is arriving (logging in, teleporting) it is half the draw distance (at least 50 m, at most the draw distance), so the region sends
/// what is closest first; afterwards it grows by a tenth of the draw distance each second until it reaches it; a lower draw distance is sent at once.</summary>
public sealed class FarRamp(TimeSpan start)
{
    private float _step = 1024f;
    private TimeSpan _lastStepAt = start;

    public float Next(TimeSpan now, bool arriving, float drawDistance)
    {
        if (arriving)
        {
            _step = Math.Min(Math.Max(drawDistance / 2f, 50f), drawDistance);
            return _step;
        }
        if (_step < drawDistance)
        {
            if ((now - _lastStepAt).TotalSeconds > 1.0)
            {
                _lastStepAt = now;
                _step = Math.Min(_step + drawDistance * 0.1f, drawDistance);
            }
            return _step;
        }
        _step = drawDistance;
        return drawDistance;
    }
}
