#!/usr/bin/env bash
# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# Start check for a Linux package, run inside a fresh official Ubuntu container by .github/workflows/linux-start-check.yml.
# Inputs: the package archive at /work/$PACKAGE.tar.gz; results go to /out.
# It installs only what the README's Linux steps name, plus Mesa's software Vulkan driver (lavapipe, which is part of
# mesa-vulkan-drivers) and a virtual X display (Xvfb). It starts the viewer with no options, waits for the login screen,
# lets it draw, saves the virtual screen and the logs, then closes the viewer.
# Pass (exit 0): the log says the login screen is up, the screen then moves on from the start-up splash, the viewer is
# still running when the screen is taken, and nothing crashed. The picture itself is kept for a person to look at.
set -u
out=/out
mkdir -p "$out"
trap 'chmod -R a+rwX "$out" 2>/dev/null' EXIT
result() { echo "$1=$2" | tee -a "$out/result.txt"; }
: > "$out/result.txt"
. /etc/os-release
result ubuntu "$VERSION_ID ($VERSION_CODENAME)"

# ---- 1. what the README tells a person to install, plus the virtual display ----
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq > "$out/apt.log" 2>&1
# The packages are read from the README's Linux steps (its "sudo apt install" line, mounted at /readme/README.md), so
# the check fails when those steps are not enough. Mesa's software Vulkan driver is part of mesa-vulkan-drivers, which
# the README names.
readme_packages=$(grep -m1 -E '^ *sudo apt install ' /readme/README.md | sed -E 's/^ *sudo apt install +//')
[ -n "$readme_packages" ] || { result install "no 'sudo apt install' line in the README"; exit 1; }
test_packages="xvfb vulkan-tools"   # the virtual display, and vulkaninfo (the README names it) for the record
result readme_packages "$readme_packages"
result test_packages "$test_packages"
# shellcheck disable=SC2086
if ! apt-get install -y -q --no-install-recommends $readme_packages $test_packages >> "$out/apt.log" 2>&1; then
    result install failed; exit 1
fi
result install ok

# ---- 2. unpack and look at what the package needs ----
tar -xzf "/work/$PACKAGE.tar.gz" -C /work
app="/work/$PACKAGE/Reverie.x86_64"
[ -x "$app" ] || { result unpack "no executable at $app"; exit 1; }
result unpack ok
{
    echo "== ldd Reverie.x86_64"; ldd "$app"
    find "/work/$PACKAGE" -name '*.so' -print | sort | while read -r so; do echo "== ldd $so"; ldd "$so"; done
} > "$out/ldd.txt" 2>&1
missing=$(grep -c 'not found' "$out/ldd.txt")
result ldd_not_found "$missing"
vulkaninfo --summary > "$out/vulkaninfo.txt" 2>&1
result vulkan_device "$(grep -m1 -E 'deviceName' "$out/vulkaninfo.txt" | sed 's/.*= *//')"
result vulkan_api "$(grep -m1 -E 'apiVersion' "$out/vulkaninfo.txt" | sed 's/.*= *//')"

# ---- 3. a virtual display ----
mkdir -p /tmp/fb
Xvfb :99 -screen 0 1280x800x24 -fbdir /tmp/fb -nolisten tcp > "$out/xvfb.log" 2>&1 &
xvfb=$!
for _ in $(seq 1 50); do [ -e /tmp/.X11-unix/X99 ] && break; sleep 0.2; done
[ -e /tmp/.X11-unix/X99 ] || { result display "Xvfb did not start"; exit 1; }
export DISPLAY=:99

# ---- 4. start the viewer as a person would, wait for the login screen ----
export HOME=/work/home
mkdir -p "$HOME"
logs="$HOME/.local/state/ReverieViewer/logs"
started=$(date +%s)
"$app" > "$out/stdout.txt" 2>&1 &
pid=$!
login=no
for _ in $(seq 1 300); do
    if grep -qs 'login.screen' "$logs"/* ; then login=yes; break; fi
    kill -0 "$pid" 2> /dev/null || break
    sleep 1
done
result login_screen_logged "$login"
result seconds_to_login_screen "$(( $(date +%s) - started ))"
# The login screen is built before its first frames are drawn, and with software graphics those frames can take a
# while: the screen is taken when the log line comes (splash.xwd), then every 10 s until the picture is no longer that
# one (at most 240 s), and once more 15 s after it changed (screen.xwd), for the picture to settle.
alive=no
changed=no
if [ "$login" = yes ]; then
    cp /tmp/fb/Xvfb_screen0 "$out/splash.xwd"
    for _ in $(seq 1 24); do
        sleep 10
        kill -0 "$pid" 2> /dev/null || break
        if ! cmp -s /tmp/fb/Xvfb_screen0 "$out/splash.xwd"; then changed=yes; break; fi
    done
    result seconds_until_the_screen_changed "$( [ "$changed" = yes ] && echo $(( $(date +%s) - started )) || echo never)"
    sleep 15
    kill -0 "$pid" 2> /dev/null && alive=yes
    cp /tmp/fb/Xvfb_screen0 "$out/screen.xwd"
fi
result screen_changed_after_login_screen "$changed"
result running_at_screenshot "$alive"

# ---- 5. close it ----
exit_code=running
if kill -0 "$pid" 2> /dev/null; then
    kill -TERM "$pid"
    for _ in $(seq 1 30); do kill -0 "$pid" 2> /dev/null || break; sleep 1; done
    if kill -0 "$pid" 2> /dev/null; then kill -KILL "$pid"; result closed "killed after 30 s"; else result closed "on SIGTERM"; fi
fi
wait "$pid"; exit_code=$?
result exit_code "$exit_code"
crash=$(grep -c -E 'crashed with signal|Unhandled exception|Segmentation fault|Aborted' "$out/stdout.txt")
# a segmentation fault (139) or an abort (134) is a crash even when nothing was printed; 143 is the SIGTERM sent above
case "$exit_code" in 134|139) crash=$((crash + 1)) ;; esac
result crash_lines "$crash"
refused=$(grep -h -E 'startup\.refused' "$logs"/* 2> /dev/null | head -3)
result startup_refused "${refused:-none}"
mkdir -p "$out/logs"; cp -r "$logs"/. "$out/logs/" 2> /dev/null

# ---- 6. the built-in start check (S-1), for information only ----
timeout 600 "$app" -- --s1-out="$out/s1" --s1-tag=ubuntu > "$out/s1-stdout.txt" 2>&1
result s1_exit "$?"
kill "$xvfb" 2> /dev/null

[ "$login" = yes ] && [ "$changed" = yes ] && [ "$alive" = yes ] && [ "$crash" = 0 ]
