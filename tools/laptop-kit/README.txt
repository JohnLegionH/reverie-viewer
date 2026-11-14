Viewer hardware check kit
=========================

What it is: a self-contained copy of the viewer plus recorded test data. It checks that this PC meets the graphics
floor (Vulkan 1.3 or Direct3D 12 feature level 12_0), runs the viewer's measuring scenes on each graphics backend
that works, and writes one results zip. It makes no login, uses no network and holds no credentials.

It installs nothing and changes no setting. Everything it writes is inside the work\ folder beside this file (the
viewer's user data, its pipeline cache, temporary files, results). The graphics driver and Windows keep their own
shader caches in your user profile; that is outside this kit's control.

To run:
  1. Plug the PC into power and close other programs.
  2. Double-click RUN-LAPTOP.cmd (or run it from Command Prompt by its full path).
  3. Wait for the line "DONE: bring back laptop-results-....zip" (about 20 to 40 minutes).
  4. Bring that zip back.

A graphics backend this PC cannot run is reported as SKIPPED and the check goes on with the other one.
Needs about 4 GiB of free disk space on the drive the kit is on.
