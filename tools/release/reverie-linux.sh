#!/bin/sh
# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later
#
# Starts Reverie Viewer on Linux. Shipped at the top of each Linux package as "reverie".
#
# When no display driver can start (no desktop session, or the X11 and Wayland libraries the engine loads at run
# time are missing), the engine prints "Unable to create DisplayServer, all display drivers failed" and then ends
# on a segmentation fault. This launcher checks first and, when a window cannot open, says what is missing and
# exits 1 without starting the engine. Otherwise it hands over to the engine with the same arguments.
#
# The library lists follow what the engine loads for each display driver (Godot's platform/linuxbsd x11 and
# wayland dynamic loaders); the package names are Debian and Ubuntu's. A library counts as present when ldconfig's
# cache lists it or it is in a folder of LD_LIBRARY_PATH. When ldconfig is missing or its cache lists none of them
# (not even the C library), the check is left to the engine. REVERIE_SKIP_DISPLAY_CHECK=1 skips it.

self=$(readlink -f "$0" 2>/dev/null) || self=$0
[ -n "$self" ] || self=$0
here=$(dirname "$self")
exe=""
for f in "$here/Reverie.x86_64" "$here/Reverie.arm64"; do
    [ -f "$f" ] && exe=$f
done
if [ -z "$exe" ]; then
    echo "Reverie Viewer: the viewer's program file (Reverie.x86_64 or Reverie.arm64) is not beside this launcher in $here." >&2
    echo "Unpack the whole archive and keep the files together." >&2
    exit 1
fi

[ "${REVERIE_SKIP_DISPLAY_CHECK:-}" = 1 ] && exec "$exe" "$@"
for a in "$@"; do
    case "$a" in
        --headless) exec "$exe" "$@" ;;
    esac
done

x11_libs="libX11.so.6:libx11-6 libXcursor.so.1:libxcursor1 libXext.so.6:libxext6 libXi.so.6:libxi6 libXinerama.so.1:libxinerama1 libXrandr.so.2:libxrandr2 libXrender.so.1:libxrender1 libxkbcommon.so.0:libxkbcommon0"
wayland_libs="libwayland-client.so.0:libwayland-client0 libwayland-cursor.so.0:libwayland-cursor0 libxkbcommon.so.0:libxkbcommon0"

ldc=$(command -v ldconfig 2>/dev/null)
for c in /sbin/ldconfig /usr/sbin/ldconfig; do
    [ -z "$ldc" ] && [ -x "$c" ] && ldc=$c
done
if [ -n "$ldc" ]; then
    known=$("$ldc" -p 2>/dev/null)
else
    known=""
fi

# present <library>: exit 0 when ldconfig's cache lists it or a folder of LD_LIBRARY_PATH holds it.
present() {
    printf '%s\n' "$known" | grep -q "^[[:space:]]*$1[[:space:]]" && return 0
    old_ifs=$IFS
    IFS=:
    for d in ${LD_LIBRARY_PATH:-}; do
        if [ -n "$d" ] && [ -e "$d/$1" ]; then IFS=$old_ifs; return 0; fi
    done
    IFS=$old_ifs
    return 1
}

# missing <list>: prints "library:package" for each library of the list the system does not have.
missing() {
    for pair in $1; do
        present "${pair%%:*}" || printf '%s ' "$pair"
    done
}

if [ -z "$DISPLAY" ] && [ -z "$WAYLAND_DISPLAY" ]; then
    echo "Reverie Viewer cannot open a window: there is no desktop session (neither DISPLAY nor WAYLAND_DISPLAY is set)." >&2
    echo "Start it from a terminal inside your desktop (Wayland or X11)." >&2
    exit 1
fi

# Without ldconfig, or with a cache that does not even list the C library (a system that keeps its libraries
# elsewhere), missing libraries cannot be told apart from libraries kept elsewhere; the engine then reports on its own.
[ -z "$ldc" ] && exec "$exe" "$@"
printf '%s\n' "$known" | grep -q '^[[:space:]]*libc\.so\.6[[:space:]]' || exec "$exe" "$@"

x11_missing=""
wayland_missing=""
[ -n "$DISPLAY" ] && x11_missing=$(missing "$x11_libs")
[ -n "$WAYLAND_DISPLAY" ] && wayland_missing=$(missing "$wayland_libs")
if { [ -n "$DISPLAY" ] && [ -z "$x11_missing" ]; } || { [ -n "$WAYLAND_DISPLAY" ] && [ -z "$wayland_missing" ]; }; then
    exec "$exe" "$@"
fi

libs=""
pkgs=""
for pair in $x11_missing $wayland_missing; do
    case " $libs " in *" ${pair%%:*} "*) continue ;; esac
    libs="$libs ${pair%%:*}"
    pkgs="$pkgs ${pair#*:}"
done
echo "Reverie Viewer cannot open a window: these display libraries are missing:$libs" >&2
echo "On Ubuntu or Debian, install them with:" >&2
echo "    sudo apt install$pkgs" >&2
echo "The README's Linux section lists everything a minimal install needs." >&2
exit 1
