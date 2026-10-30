#!/bin/sh
# Name: Powerd-Test
# Author: Thorsten Kummerow
# Lets powerd (the power manager of the Kindle) put the device to sleep and wake it up with its own RTC alarm,
# and logs every powerd event. Round 1 wakes up by itself, in round 2 the power button is pressed.
# The Kindle interface is stopped during the test and started again at the end.
LOG=/mnt/us/powerd-check.log
FB=/var/local/kmc/bin/fbink
: > "$LOG"
log() { echo "$(date +%H:%M:%S) up=$(cut -d' ' -f1 /proc/uptime) $*" >> "$LOG"; }
say() { $FB -q -c; $FB -q -m -y 10 "$1"; [ -n "$2" ] && $FB -q -m -y 12 "$2"; [ -n "$3" ] && $FB -q -m -y 14 "$3"; }
state() { lipc-get-prop com.lab126.powerd state 2>&1; }
wifi() { lipc-get-prop com.lab126.wifid cmState 2>&1; }

{
echo "== lipc-probe powerd"; lipc-probe -v com.lab126.powerd 2>&1 | head -80
echo "== status"; lipc-get-prop com.lab126.powerd status 2>&1
} >> "$LOG"

# All powerd events into the log, in the background
EVENTS=goingToScreenSaver,outOfScreenSaver,exitingScreenSaver,readyToSuspend,suspending,resuming,wakeupFromSuspend,charging,notCharging,t1TimerReset
lipc-wait-event -m com.lab126.powerd "$EVENTS" 2>&1 | while read line; do log "EVENT $line"; done &
WATCH=$!
sleep 1

# Waits for one of the events (comma separated), at most $2 seconds
wait_event() {
   result=$(lipc-wait-event -s "$2" com.lab126.powerd "$1" 2>&1)
   log "wait_event $1 -> $result (state $(state))"
}

log "start, state $(state), wifi $(wifi), battery $(lipc-get-prop com.lab126.powerd battLevel)"
stop lab126_gui >> "$LOG" 2>&1
sleep 3

say "Powerd-Test, Runde 1 von 2" "Kindle schlaeft gleich ein und wacht" "nach ca. 90 s von selbst auf. NICHTS druecken."
sleep 5
log "round 1: simulated power button"
powerd_test -p >> "$LOG" 2>&1
wait_event readyToSuspend 120
lipc-set-prop -i com.lab126.powerd rtcWakeup 90 >> "$LOG" 2>&1
log "rtcWakeup 90 set, rc=$?"
wait_event wakeupFromSuspend 400
lipc-set-prop -i com.lab126.powerd deferSuspend 60 >> "$LOG" 2>&1
log "deferSuspend rc=$?"
i=0; while [ $i -lt 30 ] && [ "$(wifi)" != CONNECTED ]; do sleep 1; i=$((i+1)); done
log "after wake: wifi $(wifi) after ${i}s, curl $(curl -s -o /dev/null -w '%{http_code}' --max-time 10 https://bummsn.de/)"
say "Runde 1: aufgewacht (Wecker)." "Runde 2: Kindle schlaeft gleich wieder." "Nach ca. 30 s EINMAL kurz den Knopf druecken."
sleep 8

log "round 2: simulated power button"
powerd_test -p >> "$LOG" 2>&1
wait_event readyToSuspend 120
lipc-set-prop -i com.lab126.powerd rtcWakeup 600 >> "$LOG" 2>&1
wait_event outOfScreenSaver,wakeupFromSuspend 900
sleep 2
log "after round 2: state $(state), wifi $(wifi)"

say "Powerd-Test fertig." "Kindle-Oberflaeche startet neu ..."
sleep 3
kill $WATCH 2>/dev/null; killall lipc-wait-event 2>/dev/null
log "starting gui, state $(state)"
start lab126_gui >> "$LOG" 2>&1
