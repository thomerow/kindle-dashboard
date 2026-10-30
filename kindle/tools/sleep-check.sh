#!/bin/sh
# Name: Sleep Check
# Author: Thorsten Kummerow
# Tests suspend with RTC wake-up and wake-up by the power button, measures how long Wi-Fi takes afterwards.
# The normal Kindle UI is stopped during the test and started again at the end.
LOG=/mnt/us/sleep-check.log
: > "$LOG"
log() { echo "$(date +%H:%M:%S) up=$(cut -d' ' -f1 /proc/uptime) $*" >> "$LOG"; }
say() { eips -c >/dev/null 2>&1; y=4; for line in "$@"; do eips 2 $y "$line" >/dev/null 2>&1; y=$((y+2)); done; }

{
echo "== rtc"; for r in /sys/class/rtc/rtc*; do echo "$r: $(cat $r/name 2>&1) wakealarm=$(cat $r/wakealarm 2>&1)"; done
echo "== power"; cat /sys/power/state 2>&1; ls /sys/power 2>&1
echo "== tools"; for t in rtcwake powerd_test initctl hexdump od; do printf '%s: ' $t; command -v $t || echo missing; done
echo "== upstart gui jobs"; initctl list 2>&1 | grep -iE 'gui|framework|blanket|powerd|wifid'
echo "== powerd"; lipc-get-prop com.lab126.powerd status 2>&1 | head -8
echo "== charging"; lipc-get-prop com.lab126.powerd isCharging 2>&1
echo "== wifi"; lipc-get-prop com.lab126.wifid cmState 2>&1
} >> "$LOG" 2>&1

RTC=""
for r in rtc1 rtc0; do
   if [ -w /sys/class/rtc/$r/wakealarm ]; then RTC=/sys/class/rtc/$r/wakealarm; break; fi
done
log "rtc=$RTC battery=$(lipc-get-prop com.lab126.powerd battLevel 2>&1)"
if [ -z "$RTC" ]; then
   echo "Kein RTC-Wecker gefunden, Abbruch. Log: sleep-check.log"
   exit 1
fi

wait_wifi() {
   start=$(cut -d' ' -f1 /proc/uptime)
   i=0
   while [ $i -lt 90 ]; do
      state=$(lipc-get-prop com.lab126.wifid cmState 2>/dev/null)
      if [ "$state" = "CONNECTED" ] && curl -s --max-time 5 -o /dev/null https://example.com; then
         log "wifi ok after ${i}s (cmState=$state)"
         return 0
      fi
      sleep 1; i=$((i+1))
   done
   log "wifi NOT connected after 90s (cmState=$state)"
}

sleep_round() {
   seconds=$1
   echo 0 > "$RTC"
   echo "+$seconds" > "$RTC"
   log "alarm set: +$seconds s, wakealarm=$(cat $RTC)"
   t0=$(date +%s)
   sync
   echo mem > /sys/power/state
   rc=$?
   t1=$(date +%s)
   echo 0 > "$RTC"
   log "resumed rc=$rc slept=$((t1 - t0))s (alarm was $seconds s) battery=$(lipc-get-prop com.lab126.powerd battLevel 2>&1)"
}

GUIJOB=""
for j in lab126_gui framework; do
   if initctl status $j >/dev/null 2>&1; then GUIJOB=$j; break; fi
done
log "gui job=$GUIJOB"

say "Sleep Check startet." "Oberflaeche wird gestoppt." "Bitte NICHTS druecken."
sleep 3
[ -n "$GUIJOB" ] && stop $GUIJOB >> "$LOG" 2>&1
lipc-set-prop com.lab126.powerd preventScreenSaver 1 >> "$LOG" 2>&1
sleep 5

say "Runde 1 von 2:" "Kindle schlaeft jetzt 60 s" "und wacht von selbst auf." "Bitte NICHTS druecken."
sleep 3
sleep_round 60
say "Runde 1: aufgewacht." "Warte auf WLAN ..."
wait_wifi

say "Runde 2 von 2:" "Kindle schlaeft gleich wieder." "Nach ca. 20 s bitte EINMAL" "kurz den Einschaltknopf druecken."
sleep 8
sleep_round 120
say "Runde 2: aufgewacht." "Warte auf WLAN ..."
wait_wifi

lipc-set-prop com.lab126.powerd preventScreenSaver 0 >> "$LOG" 2>&1
log "done, starting gui"
say "Sleep Check fertig." "Oberflaeche startet neu ..."
sleep 2
[ -n "$GUIJOB" ] && start $GUIJOB >> "$LOG" 2>&1
