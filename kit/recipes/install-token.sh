#!/bin/sh
# STAGED_TOKEN
# TV-side, run as root. Installs the API token file from the staging directory
# into the app data directory, then removes the staged copy.
#   STAGED_TOKEN  file name (no path); looked up in the staging directory (sdk_tools) and
#                 its quiet-tizen subfolder; exactly one must hold it
# The token value is never printed. Owner uid 5001, group system_share and the
# SMACK label (copied from the data directory) are observations from two TVs,
# not guarantees; the script prints the resulting owner, mode and label so
# you can inspect them. Deleting lan-api.token disables the API.
set -eu
test "$(id -u)" = 0
staged=${1:?}
case "$staged" in ""|.|..|*/*) echo bad_staged_name; exit 1;; esac
S=/home/owner/share/tmp/sdk_tools
D=/opt/usr/home/owner/apps_rw/com.samsung.tv.bixby-promotion/data
if [ -f "$S/$staged" ] && [ -f "$S/quiet-tizen/$staged" ]; then echo staged_ambiguous; exit 1; fi
if [ -f "$S/$staged" ]; then src=$S/$staged; elif [ -f "$S/quiet-tizen/$staged" ]; then src=$S/quiet-tizen/$staged; else echo staged_missing; exit 1; fi
test -s "$src" || { echo staged_empty; exit 1; }
test -d "$D"
label=
for w in $(chsmack "$D"); do
    case $w in access=*) label=${w#access=}; case $label in [!A-Za-z]*) label=${label#?}; label=${label%?};; esac;; esac
done
echo datadir_label="$label"
ls -ldn "$D"
test -n "$label" || { echo empty_label; exit 1; }
cat "$src" > "$D/lan-api.token"
chown 5001:system_share "$D/lan-api.token"
chmod 600 "$D/lan-api.token"
chsmack -a "$label" "$D/lan-api.token"
ls -ln "$D/lan-api.token"
chsmack "$D/lan-api.token"
rm -f "$src"
echo token_installed
