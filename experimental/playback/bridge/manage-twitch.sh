#!/bin/sh
# SPDX-License-Identifier: MIT
# Root-only, fixed application. Never accepts a shell command or arbitrary app ID.
set -eu
case "${QT_WEB_APP:-twitch}" in
 twitch) APP=Jz7aUrV2Q4.Twitch;CHAIN=QT_TWITCH;GATE=QT_TWITCH_BOOT;PREFIX=Jz7aUrV2Q4;;
 kicktv) APP=CrKckTV001.KickTV;CHAIN=QT_KICKTV;GATE=QT_KICKTV_BOOT;PREFIX=CrKckTV001;;
 *) exit 2;;
esac
test "$(id -u)" = 0
ipv6() { command -v ip6tables >/dev/null 2>&1 && ip6tables -S OUTPUT >/dev/null 2>&1; }
has_inspector() { netstat -lntp 2>/dev/null | grep -E "LISTEN[[:space:]]+[0-9]+/$PREFIX" >/dev/null; }
retire_ports() {
 for tool in iptables ip6tables;do
  command -v "$tool" >/dev/null 2>&1 || continue
  for old in $("$tool" -S INPUT | awk -v chain="$CHAIN" '$1=="-A"&&$NF==chain {for(i=1;i<NF;i++)if($i=="--dport")print $(i+1)}');do
   case "$old" in ''|*[!0-9]*) exit 1;;esac
   if test -z "${1:-}" || test "$old" = "$1";then "$tool" -D INPUT -p tcp --dport "$old" -j "$CHAIN";fi
  done
 done
}
gate_remove() {
 for tool in iptables ip6tables;do
  command -v "$tool" >/dev/null 2>&1 || continue
  "$tool" -D OUTPUT -p tcp --tcp-flags SYN,ACK SYN,ACK -j "$GATE" 2>/dev/null || true
  "$tool" -F "$GATE" 2>/dev/null || true
  "$tool" -X "$GATE" 2>/dev/null || true
 done
}
verify_rules() {
  port=${1:?};case "$port" in ''|*[!0-9]*) exit 1;;esac
  test "$port" -ge 1024;test "$port" -le 65535
  iptables -C INPUT -p tcp --dport "$port" -j "$CHAIN"
  iptables -C "$CHAIN" -s 127.0.0.1/32 -j RETURN
  iptables -C "$CHAIN" -p tcp -j REJECT --reject-with tcp-reset
  ipv6
  ip6tables -C INPUT -p tcp --dport "$port" -j "$CHAIN"
  ip6tables -C "$CHAIN" -s ::1/128 -j RETURN
  ip6tables -C "$CHAIN" -p tcp -j REJECT --reject-with tcp-reset
}
stop_session() {
  # A normal Twitch instance has no listener and must not be stopped by cleanup.
  if has_inspector || iptables -C OUTPUT -p tcp --tcp-flags SYN,ACK SYN,ACK -j "$GATE" 2>/dev/null;then
   app_launcher -u 5001 -k "$APP" >/dev/null 2>&1 || true
  fi
  tries=0
  while has_inspector;do tries=$((tries+1));test "$tries" -lt 15;sleep 0.1;done
  retire_ports
  gate_remove
}
case "${1:-}" in
 verify) verify_rules "${2:?}";exit 0;;
 foreground) app_launcher -u 5001 -s "$APP";exit 0;;
 retire)
  port=${2:?};case "$port" in ''|*[!0-9]*) exit 1;;esac
  test "$port" -ge 1024;test "$port" -le 65535
  retire_ports "$port";exit 0;;
 recover)
  # A completed, protected session survives a bridge restart. An interrupted
  # launch still holding a temporary gate must be closed before gate cleanup.
  if iptables -C OUTPUT -p tcp --tcp-flags SYN,ACK SYN,ACK -j "$GATE" 2>/dev/null || ip6tables -C OUTPUT -p tcp --tcp-flags SYN,ACK SYN,ACK -j "$GATE" 2>/dev/null;then
   stop_session
  else
   gate_remove
  fi
  exit 0;;
 stop) stop_session;exit 0;;
 launch) ;;
 *) exit 2;;
esac
command -v iptables >/dev/null
# This build has IPv6 enabled. Refuse to launch if both families cannot be guarded.
ipv6
ok=0
attempted=0
cleanup() {
 if test "$ok" != 1 && test "$attempted" = 1;then app_launcher -u 5001 -k "$APP" >/dev/null 2>&1 || true;fi
 gate_remove
}
trap cleanup EXIT
trap 'exit 1' INT TERM
# Gate new inbound handshakes to newly created listeners before launch.
# Existing listeners and all outbound connections remain available.
ports=$(netstat -lnt 2>/dev/null | awk '$1 ~ /^tcp/ {n=split($4,a,":");if(a[n] ~ /^[0-9]+$/)print a[n]}' | sort -nu)
for tool in iptables ip6tables;do
 "$tool" -N "$GATE"
 if test "$tool" = iptables;then localnet=127.0.0.0/8;else localnet=::1/128;fi
 "$tool" -A "$GATE" -d "$localnet" -j RETURN
 for port in $ports;do "$tool" -A "$GATE" -p tcp --sport "$port" -j RETURN;done
 "$tool" -A "$GATE" -p tcp -j REJECT --reject-with tcp-reset
 "$tool" -I OUTPUT 1 -p tcp --tcp-flags SYN,ACK SYN,ACK -j "$GATE"
done
attempted=1
app_launcher -u 5001 -k "$APP" >/dev/null 2>&1 || true
# app_launcher can return before the old process has released its listener.
tries=0
while has_inspector;do
 tries=$((tries+1));test "$tries" -lt 15
 sleep 0.1
done
# Only retire our old port rules after the old inspector has been closed.
retire_ports
output=$(app_launcher -u 5001 -w -s "$APP")
port=$(printf '%s\n' "$output" | awk '/port:/ {print $NF;exit}')
pid=$(printf '%s\n' "$output" | awk '{for(i=1;i<=NF;i++)if($i=="pid"&&$(i+1)=="="){print $(i+2);exit}}')
case "$port:$pid" in *[!0-9:]*|:|:*) exit 1;;esac
test "$port" -ge 1024;test "$port" -le 65535;test "$pid" -gt 1
for tool in iptables ip6tables;do
 if ! "$tool" -S "$CHAIN" >/dev/null 2>&1;then
  "$tool" -N "$CHAIN"
  if test "$tool" = iptables;then localnet=127.0.0.1/32;else localnet=::1/128;fi
  "$tool" -A "$CHAIN" -s "$localnet" -j RETURN
  "$tool" -A "$CHAIN" -p tcp -j REJECT --reject-with tcp-reset
 fi
 "$tool" -C INPUT -p tcp --dport "$port" -j "$CHAIN" 2>/dev/null || "$tool" -I INPUT 1 -p tcp --dport "$port" -j "$CHAIN"
done
verify_rules "$port"
# The inspector is never returned to the caller until its LAN access is blocked.
ok=1
printf 'QT_ENDPOINT %s %s\n' "$pid" "$port"
