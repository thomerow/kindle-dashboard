# Kindle dashboard: turns raw touch events into "tap <x> <y> <ms>" lines.
# <ms> is the time of the touch in milliseconds since the first event this script saw (fits into 32 bits).
# Author: Thorsten Kummerow <thorsten.kummerow (at) gmail (dot) com>
#
# Input: the output of `od -An -v -tx1` on /dev/input/event1 (hex bytes, any number per line).
# A Linux input event on the Kindle (32-bit ARM) has 16 bytes:
#   0-7 time, 8-9 type, 10-11 code, 12-15 value (little endian, signed)
# The touch screen (cyttsp4_mt) reports in display coordinates, 0..1071 x 0..1447.
# A finger that moves more than MOVE pixels is not a tap (no swiping on the dashboard).

BEGIN {
   MOVE = 60
   for (i = 0; i < 256; i++) hex[sprintf("%02x", i)] = i
   n = 0; x = -1; y = -1; down = 0; started = 0; base = -1
}

{
   for (f = 1; f <= NF; f++) {
      b[n++] = hex[$f]
      if (n == 16) { event(); n = 0 }
   }
}

function event(   type, code, value, sec, usec) {
   sec = b[0] + b[1] * 256 + b[2] * 65536 + b[3] * 16777216
   usec = b[4] + b[5] * 256 + b[6] * 65536 + b[7] * 16777216
   if (base < 0) base = sec
   ms = (sec - base) * 1000 + int(usec / 1000)
   type = b[8] + b[9] * 256
   code = b[10] + b[11] * 256
   value = b[12] + b[13] * 256 + b[14] * 65536 + b[15] * 16777216
   if (value >= 2147483648) value -= 4294967296

   if (type == 3 && code == 53) x = value                  # ABS_MT_POSITION_X
   else if (type == 3 && code == 54) y = value             # ABS_MT_POSITION_Y
   else if (type == 1 && code == 330) {                    # BTN_TOUCH
      if (value == 1) { down = 1; started = 0; moved = 0; downms = ms }
      else if (down) {
         down = 0
         if (started && !moved) { print "tap", sx, sy, downms; fflush() }
      }
   } else if (type == 0 && down && x >= 0 && y >= 0) {     # SYN_REPORT: one complete position
      if (!started) { sx = x; sy = y; started = 1 }
      else if ((x - sx) * (x - sx) + (y - sy) * (y - sy) > MOVE * MOVE) moved = 1
   }
}
