#!/bin/sh
# Name: Dashboard Check
# Author: Thorsten Kummerow
LOG=/mnt/us/dashboard-check.log
{
echo "== date"; date
echo "== id"; id
echo "== uname"; uname -a
echo "== version"; cat /etc/prettyversion.txt 2>/dev/null; cat /etc/version.txt 2>/dev/null
echo "== ip"; ifconfig wlan0 2>&1 | grep -E 'inet |HWaddr'
echo "== tools"
for t in curl wget fbink eips lipc-get-prop lipc-set-prop python3 python dropbear dropbearmulti sshd busybox crontab; do
  printf '%s: ' "$t"; command -v "$t" || echo missing
done
ls -la /mnt/us/libkh/bin 2>&1
echo "== curl"; curl --version 2>&1 | head -2
echo "== http(s) test"
curl -sS --max-time 15 -o /dev/null -w 'https %{http_code}\n' https://example.com 2>&1
curl -sS --max-time 15 -o /dev/null -w 'http %{http_code}\n' http://example.com 2>&1
echo "== fbink"; /mnt/us/libkh/bin/fbink -e 2>&1 | head -5
echo "== screen"; cat /sys/class/graphics/fb0/virtual_size 2>&1; cat /sys/class/graphics/fb0/bits_per_pixel 2>&1
echo "== powerd"; lipc-get-prop com.lab126.powerd status 2>&1 | head -5
echo "== battery"; lipc-get-prop com.lab126.powerd battLevel 2>&1
echo "== kmc"; ls /var/local/kmc /var/local/kmc/bin 2>&1
echo "== disk"; df -h /mnt/us /var/local 2>&1
echo "== cron"; ls -la /etc/crontab/ 2>&1; cat /etc/crontab/root 2>&1 | head -20
} > "$LOG" 2>&1
echo "Dashboard Check fertig."
echo "Log: dashboard-check.log"
grep -A1 -E '^== (id|ip|https?)' "$LOG"
