# Fixing problems

This section helps when something goes wrong: the viewer will not start, login fails, the connection drops, or things look grey. It ends with how to report a problem.

## Where the logs are

The viewer writes a log each time it runs. It keeps the current log and the one before it.

| | Windows | Linux |
|---|---|---|
| Log folder | `%LOCALAPPDATA%\ReverieViewer\Logs` | `~/.local/state/ReverieViewer/logs` |

In the folder:

- `viewer.log` is the log of the latest run.
- `viewer.old.log` is the run before it. Starting the viewer again moves the latest log to this name.

To open the folder on Windows, press the Windows key and R, paste `%LOCALAPPDATA%\ReverieViewer\Logs`, and press Enter.

### Watch the log live (Windows)

Start `Reverie.console.exe` instead of `Reverie.exe`. It is in the same folder. It opens a text window next to the viewer and prints the log lines as they happen. It shows the same lines the log file gets. The log leaves out passwords.

Start it from the folder you unpacked the viewer into. The text window also shows the reason when the viewer stops at start, which `Reverie.exe` does not.

## The viewer will not start

Reverie needs a graphics driver with Vulkan 1.3, or with Direct3D 12 (Windows only). If neither is good enough, the viewer stops and prints a message that begins "This viewer requires Vulkan 1.3 or later, or Direct3D 12". You will only see the message if you started `Reverie.console.exe`.

1. Update your graphics driver from the maker's website (NVIDIA, AMD or Intel). Restart the computer.
2. Start `Reverie.console.exe`. Read the last lines in the window. They say which graphics the viewer found.
3. If the viewer opens and then closes by itself, look in `viewer.log` for a line with `startup.refused` or `backend.`.

### Switch the graphics backend

The viewer tries Vulkan first, then Direct3D 12. If your choice is not good enough, it starts the other one by itself. If one backend still gives you trouble, pick the other:

1. Open **Command Prompt**.
2. Type the full path to `Reverie.console.exe` in quotes, then a space, then `-- --backend=d3d12`. For example: `"<folder>\Reverie.console.exe" -- --backend=d3d12`, where `<folder>` is where you unpacked the viewer.
3. Press Enter.

Use `--backend=vulkan` for Vulkan. Use `--backend=auto` to go back to the viewer's own choice. The two dashes before `--backend` matter. This choice lasts for one run.

Once the viewer is open, you can keep the choice: **Me > Preferences…** (Ctrl+P), **Graphics**, **Rendering backend**. Choose **Automatic**, **Vulkan** or **Direct3D 12**, then press **OK** and start the viewer again. The same page says which backend is in use. Direct3D 12 is for Windows. Linux uses Vulkan.

If the viewer has to use another backend than the one you chose, it tells you once in your notifications.

## Login fails

The line under the **Log in** button says how the login is going. When it fails, it starts with "Login failed." and then gives the reason. Some reasons are the grid's own words. These are the ones the viewer writes itself:

- **"Please fill in: ..."** A box is empty. Fill it in.
- **"A username is a first and a last name..."** Type your name the way your grid wants it, for example `Test User`, or a single name.
- **"The grid login address must be a complete http:// or https:// address."** Open the **Grid** list and choose **Manage grids…**, then **Edit** the grid. Check the address.
- **"That address didn't answer as a grid login service."** The address is wrong, or the grid is down. Check the address on your grid's website.
- **Terms of service.** A window shows the grid's terms. Tick **I accept the terms of service.** to go on. Some grids ask you to accept them on their website first.
- **"Message from the grid".** Read it. It comes from the grid, not from the viewer.
- **"Update required".** The grid wants a newer version of the viewer.
- **"Multifactor authentication".** Type a new token from your authentication app in the **Authentication token** box.
- **Certificate problems** ("Untrusted certificate", "Certificate problem"). The grid's security certificate is not known, is out of date, or has changed. Read the window. If the grid's operator says the certificate was replaced, open **Manage grids…**, remove the grid, and add it again.
- **"Sorry, login failed for an unrecognized reason."** Ask your grid's support. Include the log (see the end of this section).

A wrong name or password is shown with the grid's own message. Check them, and check that you chose the right grid in the **Grid** list.

## The connection drops

When the grid ends your session without you asking, a window called **Logged out** appears. It tells you why. For example, "This region may be experiencing trouble. Please check your connection to the Internet." or "The region you were in has shut down."

The viewer does not reconnect by itself. You choose:

1. Choose **Reconnect** to log in again. Choose **Close** to dismiss the window. Closing it makes the viewer forget your login for this run, so you log in again by hand.
2. The viewer tries up to two times: first to the same spot, then to your last location. A line says "Reconnecting (attempt 1 of 2)...".
3. Sometimes the grid still holds your old session. The viewer then says "The grid still holds your last session. Trying again in ... s" and waits, up to three times. Choose **Cancel** to stop waiting.
4. If it fails, the window says "Could not reconnect" and why. Check your internet connection and try again.

The **Reconnect** button appears only while the viewer still has your login for this run. The viewer keeps that only in memory, never in a file. If it has lost it, the window says "The viewer no longer has the login for this session; log in again." Log in again by hand.

## Avatars or textures look grey

Grey usually means the picture has not arrived yet. Wait a little first, especially in a busy region or right after you arrive.

If things stay grey:

1. Check your internet connection.
2. Open **Me > Preferences…** (Ctrl+P) and choose **Cache**.
3. Choose **Clear cache…**.
4. The viewer asks "Remove everything the viewer has cached on this computer? It is fetched again as it is needed." Choose **Clear cache**.
5. It says how many entries it removed. Log out and log in again.

Clearing the cache is safe. The viewer fetches what it needs again, so the first minutes after it are slower.

Other things on the **Cache** page: **Cache folder** (empty means the default; **Choose...** picks one) and **Cache size limit (MB)**. Both need a restart. A cache that is too small makes the viewer fetch the same things again and again.

If clearing does not help, report it (see below). Say which grid you were on.

## Where settings are kept

| | Windows | Linux |
|---|---|---|
| Settings folder | `%APPDATA%\ReverieViewer` | `~/.config/ReverieViewer` |

The main file is `settings.json`. The folder also holds your grid list (`grids.json`), your saved login names (`saved-names.json`), the certificates you chose to trust (`trusted-certificates.json`) and the add-ons (`Addons` folder and `addons-state.json`). The cache is not here. See the table in [Installing and first login](installing-and-first-login.md#where-the-viewer-keeps-its-files).

### Reset settings

**One page of Preferences.** Open **Me > Preferences…** (Ctrl+P), click the page, and choose **Reset to defaults**. On the **Interface** page it is called **Reset to preset**. Then press **OK**.

**All settings.**

1. Quit the viewer: Ctrl+Q.
2. Open the settings folder.
3. Move `settings.json` to another folder, such as your desktop. Do not touch the other files unless you want them gone too.
4. Start the viewer. It makes a new `settings.json` with the defaults.

This also forgets the window size and your answer to the first-run notice, so the notice shows again. If the new settings are no better, move the old file back.

A file the viewer cannot read is ignored, and the defaults are used. A bad setting does not stop the viewer from starting.

## Report a problem

Support is best effort. The **About this viewer** window (**Help > About this viewer**, or **About Reverie Viewer** in the Reverie menu) says: "report problems on the GitHub issues page". That page is:

<https://github.com/JohnLegionH/reverie-viewer/issues>

Choose **New issue**, then **Bug report**. The form asks for these things. Have them ready:

1. **Viewer version.** It shows on the login screen and in the About window.
2. **Operating system.** For example "Windows 11".
3. **Graphics card and driver version.**
4. **Grid type.** An OpenSimulator grid, or Second Life.
5. **Steps.** What you did, one step per line, and what happened instead of what you expected.
6. **The log.** Attach `viewer.log` from the run where it happened. If you started the viewer again since, the run you want is `viewer.old.log`.

Read the log before you attach it. It leaves out passwords, but it names the grid and the regions you visited. The page is public, so cut out anything you do not want others to see.

A short, exact report is the most useful one. Say what you expected, what happened, and whether it happens every time.

## Next

For the list of what Reverie can do, see the [feature cheat sheet](../FEATURES.md). For the keys, see [Keyboard shortcuts](keyboard-shortcuts.md).
