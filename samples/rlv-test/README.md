<!-- Copyright (c) 2026 Legion Builds -->
<!-- SPDX-License-Identifier: LGPL-2.1-or-later -->

# RLV test object

`rlv-test.lsl` is a small script for trying RLV in this viewer. Put it in a box you own, wear the box, and send it words on
channel 7171 (for example `/7171 lock`). It sends the matching RLV commands to the viewer as owner-say lines starting with
`@`, as any RLV device does. Replies the viewer sends on channel 7373, and chat it redirects to channel 7272, are said back
to you as plain lines, so you can follow the whole exchange in local chat.

| Word | RLV command | What you should see |
|---|---|---|
| `lock` | `@detach=n` | Take Off and Detach are greyed for the box; detaching it is refused with a notice |
| `unlock` | `@detach=y` | the box can be taken off again |
| `notp` | `@tploc=n,tplm=n` | teleports to a location or a landmark are refused with "Unable to initiate teleport due to RLV restrictions" |
| `redir` | `@redirchat:7272=add` | what you say in local chat no longer shows there; the box says it back as `redirected: …` |
| `sit <key>` | `@sit:<key>=force` | you sit on the object with that key |
| `stand` | `@unsit=force` | you stand up |
| `version` | `@versionnew=7373` | the box says `reply: RestrainedLove viewer v3.4.3 (… RLV 1.0.0)` |
| `status` | `@getstatus=7373` | the box says the box's restrictions, for example `reply: /detach/tploc/tplm` |
| `clear` | `@clear` | every restriction of the box goes |

RLV must be on: **Me > Add-ons**, tick **RLV**, then log in again (the switch takes effect at the next login).
**Me > RLV > Restrictions…** lists each object restricting you, what it holds, whether this viewer enforces it yet and when
it was set. The way out of any restriction is to switch RLV off and log in again.

The script is licensed LGPL-2.1-or-later, like the viewer.
