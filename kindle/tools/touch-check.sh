#!/bin/sh
# Name: Touch Check
# Author: Thorsten Kummerow
LOG=/mnt/us/touch-check.log
{
echo "== input devices"; cat /proc/bus/input/devices
echo "== event nodes"; ls -la /dev/input/
} > "$LOG" 2>&1
echo "Touch Check: Tippe jetzt 10 Sekunden lang"
echo "ein paar Mal auf verschiedene Stellen"
echo "(z. B. oben links, unten rechts)."
PIDS=""
for e in /dev/input/event*; do
  n=$(basename "$e")
  cat "$e" > "/tmp/$n.bin" 2>/dev/null &
  PIDS="$PIDS $!"
done
sleep 10
kill $PIDS 2>/dev/null
{
for f in /tmp/event*.bin; do
  echo "== $f: $(wc -c < "$f") bytes"
  od -A d -t x1 "$f" | head -60
done
} >> "$LOG" 2>&1
rm -f /tmp/event*.bin
echo "Fertig. Log: touch-check.log"
