#!/bin/sh
# Name: Dashboard starten
# Author: Thorsten Kummerow
# Scriptlet: appears as a book in the library, tapping it starts the dashboard.
# The dashboard ends with five taps into a corner within 3 seconds (after a press on the power button).
DIR=/mnt/us/dashboard

if [ ! -f "$DIR/config" ]; then
   echo "Fehlt: dashboard/config"
   echo "Siehe README im Repository."
   exit 1
fi
if [ -f /tmp/dashboard.pid ] && kill -0 "$(cat /tmp/dashboard.pid)" 2>/dev/null; then
   echo "Das Dashboard laeuft bereits."
   exit 0
fi
echo "Dashboard startet ..."
# Detached, so that it keeps running when the Kindle interface is stopped
nohup sh "$DIR/dashboard.sh" > /dev/null 2>&1 &
