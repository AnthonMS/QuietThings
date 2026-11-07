#!/bin/sh
# NAME
# TV-side, run as root. Restores the DLL saved by install-api.sh under the
# same NAME and checks it against the recorded hash. Takes about 5 s.
set -eu
test "$(id -u)" = 0
name=${1:?}
case "$name" in ''|*[!A-Za-z0-9_-]*) echo bad_name; exit 1;; esac
A=/opt/usr/apps/com.samsung.tv.bixby-promotion
T=$A/bin/BixbyPromotion.dll
N=/opt/usr/share/quiet-tizen-backups/$name
app=com.samsung.tv.bixby-promotion
hash_of() { sha256sum "$1" | cut -d' ' -f1; }

test -f "$N/BixbyPromotion.dll" || { echo no_backup; exit 1; }
test -s "$N/installed-before.sha256" || { echo no_hash_record; exit 1; }
want=$(cut -d' ' -f1 "$N/installed-before.sha256")
test "$(hash_of "$N/BixbyPromotion.dll")" = "$want" || { echo backup_hash_mismatch; exit 1; }
app_launcher -u 5001 -k "$app" || true
cat "$N/BixbyPromotion.dll" > "$T"
test "$(hash_of "$T")" = "$want" || { echo restore_hash_mismatch; exit 1; }
echo restored_ok
app_launcher -u 5001 -s "$app"
sleep 3
ls -l "$T"
