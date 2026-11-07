#!/bin/sh
# NAME STAGED_FILE EXPECTED_SHA256
# TV-side, run as root. Replaces the installed BixbyPromotion.dll with the
# QuietThings API build that was pushed to the staging directory.
#   NAME             backup folder name under the backup root (letters, digits, _ and -)
#   STAGED_FILE      file name (no path) of the new DLL; looked up in the staging directory
#                    (sdk_tools) and then its quiet-tizen subfolder; exactly one must hold it
#   EXPECTED_SHA256  hash of the staged DLL, computed on your PC before the push
# Guards: the staged DLL must match the expected hash. If the backup folder
# does not exist it is created and the installed DLL is copied in. If it
# exists, its DLL must exist and be byte-identical to the installed DLL;
# otherwise the script prints backup_mismatch and exits 1. A backup is never
# overwritten. Runs in about 12 s (8 s is the wait after the app starts).
set -eu
test "$(id -u)" = 0
name=${1:?}; staged=${2:?}; expected=${3:?}
case "$name" in ''|*[!A-Za-z0-9_-]*) echo bad_name; exit 1;; esac
case "$staged" in ""|.|..|*/*) echo bad_staged_name; exit 1;; esac
case "$expected" in *[!0-9a-f]*) echo bad_hash; exit 1;; esac
test "${#expected}" -eq 64 || { echo bad_hash; exit 1; }
A=/opt/usr/apps/com.samsung.tv.bixby-promotion
S=/home/owner/share/tmp/sdk_tools
T=$A/bin/BixbyPromotion.dll
N=/opt/usr/share/quiet-tizen-backups/$name
app=com.samsung.tv.bixby-promotion
hash_of() { sha256sum "$1" | cut -d' ' -f1; }

test -f "$T"
test ! -e "$A/bin/.native_image" || { echo native_image_present_refusing_swap; exit 1; }
if [ -f "$S/$staged" ] && [ -f "$S/quiet-tizen/$staged" ]; then echo staged_ambiguous; exit 1; fi
if [ -f "$S/$staged" ]; then src=$S/$staged; elif [ -f "$S/quiet-tizen/$staged" ]; then src=$S/quiet-tizen/$staged; else echo staged_missing; exit 1; fi
test "$(hash_of "$src")" = "$expected" || { echo staged_hash_mismatch; exit 1; }
cur=$(hash_of "$T")

if [ ! -e "$N" ]; then
    mkdir -p "$N"
    cp -a "$T" "$N/BixbyPromotion.dll"
else
    test -f "$N/BixbyPromotion.dll" || { echo backup_mismatch; exit 1; }
    test "$(hash_of "$N/BixbyPromotion.dll")" = "$cur" || { echo backup_mismatch; exit 1; }
fi
test "$(hash_of "$N/BixbyPromotion.dll")" = "$cur" || { echo backup_mismatch; exit 1; }
echo "$cur  BixbyPromotion.dll" > "$N/installed-before.sha256"
echo backup_ok

app_launcher -u 5001 -k "$app" || true
swap_failed() { echo "SWAP FAILED: the app is STOPPED and the DLL may be damaged. Run: sh rollback-api.sh $name"; exit 1; }
cat "$src" > "$T" || swap_failed
test "$(hash_of "$T")" = "$expected" || swap_failed
echo installed_ok

rm -f "$src"
rm -f /tmp/quietthings-api.txt
app_launcher -u 5001 -s "$app"
sleep 8
ls -l "$T"
echo "== listener note"
cat /tmp/quietthings-api.txt 2>&1 || echo no_listener_note
