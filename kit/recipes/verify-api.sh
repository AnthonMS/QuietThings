#!/bin/sh
# TV-side, read only. Shows the listener note, the launcher state file, the
# app memory use, the DLL hash and whether a token file is present.
D=/opt/usr/home/owner/apps_rw/com.samsung.tv.bixby-promotion/data
T=/opt/usr/apps/com.samsung.tv.bixby-promotion/bin/BixbyPromotion.dll
cat /tmp/quietthings-api.txt 2>&1
echo ==
head -8 /tmp/quiet-home-state.txt 2>&1
p=$(grep '^process_id=' /tmp/quiet-home-state.txt 2>/dev/null | cut -d= -f2)
echo pid="$p"
if [ -n "$p" ] && [ -r "/proc/$p/status" ]; then
    grep -E 'VmRSS|VmHWM' "/proc/$p/status"
fi
sha256sum "$T"
if [ -s "$D/lan-api.token" ]; then echo token_present=yes; else echo token_present=no; fi
