#!/bin/sh
# SPDX-License-Identifier: MIT
# Optional owner-installed component. Run only after the normal boot root step.
set -eu
test "$(id -u)" = 0
R=${QUIETTHINGS_WEB_ROOT:-/opt/usr/share/quietthings-web-playback}
secure() {
 test ! -L "$1"
 test "$(stat -c %u "$1")" = 0
 mode=$(stat -c %a "$1")
 test "$((0$mode & 022))" -eq 0
}
test -f "$R/enabled"
test -f "$R/QuietThingsWebBridge.dll"
test -f "$R/manage-twitch.sh"
TOKEN=$(cat "$R/token-path")
RUNTIME=$(cat "$R/runtime-path")
for value in "$R" "$TOKEN" "$RUNTIME";do
 case "$value" in /*) ;;*) exit 1;;esac
 case "$value" in *[!A-Za-z0-9_./-]*) exit 1;;esac
done
test -f "$TOKEN";test -x "$RUNTIME"
for file in "$R" "$R/QuietThingsWebBridge.dll" "$R/manage-twitch.sh" "$R/token-path" "$R/runtime-path" "$R/enabled";do secure "$file";done
cat > /run/systemd/system/quietthings-web-playback.service <<UNIT
[Unit]
Description=QuietThings private Twitch playback bridge
[Service]
Type=simple
ExecStart=$RUNTIME $R/QuietThingsWebBridge.dll $TOKEN $R/manage-twitch.sh $R/endpoint.json
ExecStopPost=/bin/sh -c 'cat $R/manage-twitch.sh | QT_WEB_APP=twitch /bin/sh -s -- recover; cat $R/manage-twitch.sh | QT_WEB_APP=kicktv /bin/sh -s -- recover'
Restart=on-failure
RestartSec=3
TimeoutStopSec=5
KillMode=control-group
UNIT
systemctl daemon-reload
systemctl start quietthings-web-playback.service
if test -f "$R/cobalt-key";then
 # Optional observer lives in the existing app-local proxy service.
 app_launcher -u 5001 -s xvvl3S1TT1.StandaloneService >/dev/null 2>&1 || true
fi
