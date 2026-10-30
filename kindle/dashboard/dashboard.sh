#!/bin/sh
# Kindle dashboard: client on the Kindle. Fetches the picture from the server, shows it and sleeps until
# the server wants it to wake up again. A press on the power button wakes it for a while to take taps.
# Author: Thorsten Kummerow <thorsten.kummerow (at) gmail (dot) com>
#
# Started by the scriptlet "Dashboard starten" (documents/dashboard-start.sh). Ends with five taps into a corner
# of the screen within 3 seconds (after a press on the power button), which restarts the Kindle, or by holding the
# power button for 40 seconds. Both bring back the normal Kindle interface.
# Settings: config in the same directory, see config.example. Background: kindle/README.md ("How it works").

DIR=/mnt/us/dashboard
. "$DIR/config"

FBINK=/var/local/kmc/bin/fbink          # The copy in /mnt/us/libkh crashes on the Paperwhite 3
RTC=/sys/class/rtc/rtc1/wakealarm
TOUCH=/dev/input/event1                 # cyttsp4_mt, see the touch check in kindle/tools
LOG="$DIR/dashboard.log"
FRAME="$DIR/frame.txt"
IMAGE="$DIR/frame.png"
FIFO=/tmp/dashboard.events
PIDFILE=/tmp/dashboard.pid

INTERACTIVE_SECONDS=${INTERACTIVE_SECONDS:-60}   # how long taps are taken after a press on the power button
FRONTLIGHT_INTERACTIVE=${FRONTLIGHT_INTERACTIVE:-} # front light (0-24) during that time; empty: as set on the Kindle
FULL_REFRESH_EVERY=${FULL_REFRESH_EVERY:-12}     # flashing refresh against ghosting after so many pictures
RETRY_SECONDS=300                                # sleep after a failed contact with the server
CORNER=200                                       # size of the corners for the exit gesture (about 17 mm)
EXIT_TAPS=5                                      # taps into corners within EXIT_MS milliseconds end the dashboard
EXIT_MS=3000

SHOWN=""            # rev of the picture on the screen
PICTURES=0
READER=""
POLLER=""

log() {
   echo "$(date '+%Y-%m-%d %H:%M:%S') $*" >> "$LOG"
}

# --- Start and end ---

# Not called start: that is the upstart command that starts the Kindle interface again
begin() {
   if [ -f "$LOG" ] && [ "$(wc -c < "$LOG")" -gt 262144 ]; then mv "$LOG" "$LOG.old"; fi
   echo $$ > "$PIDFILE"
   log "start, server $URL"
   stop lab126_gui >/dev/null 2>&1
   lipc-set-prop com.lab126.powerd preventScreenSaver 1
   # The light is off while the Kindle only updates the picture, and on during the taps after a press on the button
   FRONTLIGHT_BEFORE=$(lipc-get-prop com.lab126.powerd flIntensity 2>/dev/null)
   [ -z "$FRONTLIGHT_INTERACTIVE" ] && FRONTLIGHT_INTERACTIVE=${FRONTLIGHT_BEFORE:-12}
   light 0
   # Stopping the interface makes its main application crash, and the Kindle writes a crash report into documents
   # (KPPMainAppV2_..._crash_...), which then shows up in the library. They are of no use here.
   ( sleep 20; rm -rf /mnt/us/documents/KPPMainAppV2_*_crash_*; sleep 60; rm -rf /mnt/us/documents/KPPMainAppV2_*_crash_* ) &
   rm -f "$FIFO"
   mkfifo "$FIFO"
   # Keep the FIFO open for reading and writing, so background writers never block and reads can time out
   exec 3<> "$FIFO"
   trap finish EXIT
   trap 'exit 0' INT TERM
}

finish() {
   stop_touch
   stop_poller
   echo 0 > "$RTC" 2>/dev/null
   lipc-set-prop com.lab126.powerd preventScreenSaver 0
   [ -n "$FRONTLIGHT_BEFORE" ] && light "$FRONTLIGHT_BEFORE"
   rm -f "$FIFO" "$PIDFILE"
   log "end, powerd: $(lipc-get-prop com.lab126.powerd state 2>&1), restarting the Kindle"
   # Only starting the interface again (start lab126_gui) left a white screen after suspends that powerd did not
   # know about. A restart always brings the Kindle back into a clean state.
   $FBINK -q -c -f
   $FBINK -q -m -y -2 "Kindle startet neu ..."
   sync
   reboot
}

# --- Kindle state ---

battery() { lipc-get-prop com.lab126.powerd battLevel 2>/dev/null || echo ""; }
charging() { [ "$(lipc-get-prop com.lab126.powerd isCharging 2>/dev/null)" = "1" ] && echo 1 || echo 0; }
light() { lipc-set-prop com.lab126.powerd flIntensity "$1" 2>/dev/null; }

# Waits until the server answers at all, at most about a minute. Right after waking up the Kindle may already report
# CONNECTED while names cannot be resolved yet, so this asks the server itself (a tiny request without login).
wait_for_network() {
   start=$(date +%s)
   restarted=0
   while [ $(( $(date +%s) - start )) -lt 60 ]; do
      if [ "$(lipc-get-prop com.lab126.wifid cmState 2>/dev/null)" = "CONNECTED" ] &&
            curl -s -o /dev/null --connect-timeout 4 --max-time 6 "$URL/api/session"; then
         waited=$(( $(date +%s) - start ))
         [ $waited -gt 2 ] && log "network after ${waited}s"
         return 0
      fi
      # Half way through, switch Wi-Fi off and on again
      if [ $restarted = 0 ] && [ $(( $(date +%s) - start )) -ge 30 ]; then
         log "no network after 30 s, restarting Wi-Fi"
         network_report
         lipc-set-prop com.lab126.wifid enable 0 2>/dev/null
         sleep 2
         lipc-set-prop com.lab126.wifid enable 1 2>/dev/null
         restarted=1
      fi
      sleep 2
   done
   log "no network after 60 s, details in network.log"
   network_report
   return 1
}

# Writes what the Kindle knows about its network into network.log, for finding out what went wrong
network_report() {
   {
      echo "== $(date) cmState $(lipc-get-prop com.lab126.wifid cmState 2>&1)"
      ifconfig wlan0
      echo "== resolv.conf"; cat /etc/resolv.conf
      echo "== routes"; route -n
      echo "== system log"; tail -n 150 /var/log/messages
   } > "$DIR/network.log" 2>&1
}

# --- Server ---

# Asks for the current picture. With a wait time the server answers as soon as the picture changes (long poll).
fetch_frame() {
   wait=${1:-0}
   curl -fsS --connect-timeout 10 --max-time $((wait + 20)) -H "Authorization: Bearer $TOKEN" \
      "$URL/api/device/frame?rev=$SHOWN&battery=$(battery)&charging=$(charging)&wait=$wait" \
      -o "$FRAME.tmp" 2>> "$LOG" && mv "$FRAME.tmp" "$FRAME"
}

frame_value() { awk -v key="$1" '$1 == key { print $2; exit }' "$FRAME"; }

# Shows the picture of frame.txt if it is not on the screen yet
show_frame() {
   rev=$(frame_value rev)
   [ -n "$rev" ] && [ "$rev" = "$SHOWN" ] && return 0
   if ! curl -fsS --max-time 30 -H "Authorization: Bearer $TOKEN" "$URL/api/device/image" -o "$IMAGE.tmp" 2>> "$LOG"; then
      return 1
   fi
   mv "$IMAGE.tmp" "$IMAGE"
   PICTURES=$((PICTURES + 1))
   # -w waits until the display has finished: light greys take longest, and a suspend in the middle of the
   # update leaves them much too dark
   if [ $((PICTURES % FULL_REFRESH_EVERY)) -eq 1 ]; then
      $FBINK -q -w -f -g file="$IMAGE"
   else
      $FBINK -q -w -g file="$IMAGE"
   fi
   SHOWN=$rev
   log "picture $rev"
}

# Fetches and shows the picture, tries a few times (Wi-Fi may need a moment after waking up)
update() {
   if wait_for_network; then
      for attempt in 1 2 3; do
         if fetch_frame 0 && show_frame; then
            clear_offline
            return 0
         fi
         sleep 3
      done
   fi
   mark_offline
   return 1
}

# A small black square in the corner shows that the picture could not be updated
mark_offline() { $FBINK -q -B BLACK -k top=0,left=0,width=24,height=24; OFFLINE=1; log "offline"; }
clear_offline() { [ "$OFFLINE" = 1 ] && { SHOWN=""; OFFLINE=0; }; }

# --- Sleeping ---

# Sleeps until the alarm or the power button. Prints "alarm" or "button".
sleep_until() {
   seconds=$1
   echo 0 > "$RTC"
   echo "+$seconds" > "$RTC"
   before=$(date +%s)
   sync
   sleep 1
   echo mem > /sys/power/state
   slept=$(( $(date +%s) - before ))
   echo 0 > "$RTC"
   log "slept ${slept}s of ${seconds}s, powerd: $(lipc-get-prop com.lab126.powerd state 2>&1)"
   if [ $slept -lt $((seconds - 20)) ]; then echo button; else echo alarm; fi
}

# --- Touch ---

start_touch() {
   [ -n "$READER" ] && return
   # The device stays open (fd 4) the whole time: events that arrive while nobody has it open are dropped by the
   # kernel, and opening it anew for every read lost taps when tapping quickly
   ( exec 4< "$TOUCH"; while :; do dd bs=1024 count=1 <&4 2>/dev/null | od -An -v -tx1; done | awk -f "$DIR/touch.awk" >&3 ) &
   READER=$!
}

stop_touch() {
   [ -z "$READER" ] && return
   # The reader is a subshell with a pipeline, end the whole process group of its children
   pkill -P "$READER" 2>/dev/null
   kill "$READER" 2>/dev/null
   killall dd 2>/dev/null
   READER=""
}

# On the charger: waits for new pictures in the background and reports them as "changed"
start_poller() {
   [ -n "$POLLER" ] && return
   (
      while :; do
         rev=$SHOWN
         [ -f "$FRAME" ] && rev=$(frame_value rev)
         if curl -fsS --max-time 70 -H "Authorization: Bearer $TOKEN" \
               "$URL/api/device/frame?rev=$rev&battery=$(battery)&charging=1&wait=50" -o /tmp/dashboard.poll 2>/dev/null; then
            new=$(awk '$1 == "rev" { print $2; exit }' /tmp/dashboard.poll)
            [ -n "$new" ] && [ "$new" != "$rev" ] && echo changed >&3
         else
            sleep 30
         fi
      done
   ) &
   POLLER=$!
}

stop_poller() {
   [ -z "$POLLER" ] && return
   pkill -P "$POLLER" 2>/dev/null
   kill "$POLLER" 2>/dev/null
   POLLER=""
}

# Draws a frame around the tapped zone at once, before the server answers
highlight() {
   $FBINK -q -B BLACK -k top=$2,left=$1,width=$3,height=4
   $FBINK -q -B BLACK -k top=$(($2 + $4 - 4)),left=$1,width=$3,height=4
   $FBINK -q -B BLACK -k top=$2,left=$1,width=4,height=$4
   $FBINK -q -B BLACK -k top=$2,left=$(($1 + $3 - 4)),width=4,height=$4
}

# Taps into the corners of the screen only count towards the exit gesture, they trigger no zone. EXIT_TAPS of them
# within EXIT_MS end the dashboard. The time is the one of the touch itself (from touch.awk), not the one of handling.
CORNER_TAPS=0
CORNER_SINCE=-100000
is_corner() {
   { [ $1 -lt $CORNER ] || [ $1 -gt $((1072 - CORNER)) ]; } && { [ $2 -lt $CORNER ] || [ $2 -gt $((1448 - CORNER)) ]; }
}

count_corner_tap() {
   [ $(($3 - CORNER_SINCE)) -gt $EXIT_MS ] && { CORNER_TAPS=0; CORNER_SINCE=$3; }
   CORNER_TAPS=$((CORNER_TAPS + 1))
   log "corner tap $CORNER_TAPS of $EXIT_TAPS at $3 ms"
   [ $CORNER_TAPS -ge $EXIT_TAPS ]
}

handle_tap() {
   x=$1; y=$2
   if is_corner "$x" "$y"; then
      if count_corner_tap "$x" "$y" "$3"; then
         log "exit gesture"
         exit 0
      fi
      return
   fi
   # A tap next to a corner does not reset the count, only the time limit does: the corners are hard to hit exactly
   # because of the frame around the display
   zone=$(awk -v x="$x" -v y="$y" '$1 == "zone" && x >= $3 && x < $3 + $5 && y >= $4 && y < $4 + $6 { print $2, $3, $4, $5, $6; exit }' "$FRAME")
   [ -z "$zone" ] && return
   set -- $zone
   log "tap $x,$y -> $1"
   highlight "$2" "$3" "$4" "$5"
   if curl -fsS --max-time 20 -X POST -H "Authorization: Bearer $TOKEN" "$URL/api/device/tap?zone=$1" -o "$FRAME.tmp" 2>> "$LOG"; then
      mv "$FRAME.tmp" "$FRAME"
      [ "$1" = "refresh" ] && SHOWN=""
      show_frame
   else
      SHOWN=""
      update
   fi
}

# Takes taps (and, on the charger, new pictures) until the time is up. 0 seconds: until the charger is unplugged.
interactive() {
   until=$(( $(date +%s) + $1 ))
   [ "$1" -gt 0 ] && light "$FRONTLIGHT_INTERACTIVE"
   start_touch
   while :; do
      if [ "$1" -eq 0 ]; then
         [ "$(charging)" = 1 ] || break
      else
         [ $(date +%s) -ge $until ] && break
      fi
      if read -t 5 kind x y ms <&3; then
         case "$kind" in
            tap) handle_tap "$x" "$y" "$ms"; until=$(( $(date +%s) + $1 )) ;;
            changed) fetch_frame 0 && show_frame ;;
         esac
      fi
   done
   stop_touch
   light 0
}

# --- Main loop ---

begin
update
while :; do
   if [ "$(charging)" = 1 ]; then
      log "on the charger: staying awake"
      start_poller
      interactive 0
      stop_poller
      update
      continue
   fi
   wake=$(frame_value wake)
   [ -z "$wake" ] || [ "$OFFLINE" = 1 ] && wake=$RETRY_SECONDS
   reason=$(sleep_until "$wake")
   if [ "$reason" = button ]; then
      # Light at once, so that the press visibly does something even while the network is still coming up
      light "$FRONTLIGHT_INTERACTIVE"
      update
      interactive "$INTERACTIVE_SECONDS"
      # Back to sleep with the wake-up time the server gives now (Wi-Fi may have dropped meanwhile, so with retries)
      update
   else
      update
   fi
done
