#!/bin/sh
# Name: Grau-Test
# Author: Thorsten Kummerow
# Shows 16 grey levels (black at the top, white at the bottom) in three ways and saves the framebuffer each time.
# Needs dashboard/grey-ramp.png. Takes about one minute, then the Kindle interface starts again.
LOG=/mnt/us/grey-check.log
FB=/var/local/kmc/bin/fbink
IMG=/mnt/us/dashboard/grey-ramp.png
{
echo "== fb"; cat /sys/class/graphics/fb0/stride /sys/class/graphics/fb0/bits_per_pixel /sys/class/graphics/fb0/virtual_size 2>&1
echo "== fbink -e"; $FB -e 2>&1
echo "== fbink -h"; $FB -h 2>&1 | head -120
} > "$LOG" 2>&1
stop lab126_gui >> "$LOG" 2>&1
lipc-set-prop com.lab126.powerd preventScreenSaver 1
sleep 3
STRIDE=$(cat /sys/class/graphics/fb0/stride 2>/dev/null || echo 1088)
step() {
   $FB -q -c -f
   sleep 1
   $FB -q $2 -g file="$IMG" >> "$LOG" 2>&1
   $FB -q -y -3 "Schritt $1: $3" >> "$LOG" 2>&1
   sleep 2
   dd if=/dev/fb0 of=/mnt/us/grey-fb-$1.raw bs=$STRIDE count=1448 2>> "$LOG"
   echo "step $1 ($3) done" >> "$LOG"
   sleep 15
}
step 1 "" "wie das Dashboard"
step 2 "-f" "mit Blitzen"
step 3 "-f -W GC16" "Blitzen und GC16"
step 4 "-W GL16" "GL16"
lipc-set-prop com.lab126.powerd preventScreenSaver 0
$FB -q -c -f
start lab126_gui >> "$LOG" 2>&1
