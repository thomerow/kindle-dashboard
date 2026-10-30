# Kindle client

Turns a jailbroken Kindle Paperwhite 3 into the display of the [Kindle dashboard](../README.md): a shell script that
fetches the picture from the [server](../bummsn.de/README.md), shows it and sleeps until the server wants it to wake up
again. A press on the power button wakes it up for taps.

Tested on a **Kindle Paperwhite 3 (7th generation, 2015), firmware 5.16.2.1.1**, jailbroken with WinterBreak2.
Other models need small changes, see [Other Kindle models](#other-kindle-models).

## Contents

- [1. Jailbreak](#1-jailbreak)
- [2. Installing the client](#2-installing-the-client)
- [3. Using it](#3-using-it)
- [Settings](#settings)
- [How it works](#how-it-works)
- [Diagnostic scriptlets](#diagnostic-scriptlets)
- [Troubleshooting](#troubleshooting)
- [Other Kindle models](#other-kindle-models)

## 1. Jailbreak

A jailbreak lets the Kindle run your own programs. The methods change with the firmware; the authoritative source is
the [KindleModding wiki](https://kindlemodding.org/jailbreaking/). This is the way it went for this project, with the
state of October 2026. Read the wiki's overview page first; a jailbreak is at your own risk.

**Never update the firmware of a Kindle you want to jailbreak.** A firmware that is too new may have no jailbreak,
and you cannot go back without one.

### Which jailbreak?

1. On the Kindle: **Settings → Device Options → Device Info** shows the firmware version (e.g. `5.16.2.1.1`) and the
   serial number. The firmware is also in `system/version.txt` on the Kindle's USB drive.
2. Enter both into the [jailbreak wizard](https://kindlemodding.org/jailbreak-wizard.html). For the Paperwhite 3
   on 5.16.2.1.1 it recommends **WinterBreak2** (firmware 5.6.1.1 up to 5.16.3, no Amazon registration needed). The
   older **WinterBreak** works up to 5.18.0.2 but needs a registered Kindle.

### Prevent automatic updates

Amazon could install an update while you work on the jailbreak. An update needs free space, so fill the Kindle's
storage until only **50–90 MB** are free:

1. Turn on **Airplane Mode** on the Kindle and connect it to the computer by USB.
2. Fill it, e.g. with [`Filler.ps1` from Kindle-Filler-Disk](https://github.com/bastianmarin/Kindle-Filler-Disk/)
   (copy it to the Kindle drive and run it with PowerShell), or by copying large files into a folder of their own.
3. Delete any `update*.bin` or `update.bin.tmp.partial` in the root of the Kindle drive.

The details are on the wiki page [Prevent automatic updates](https://kindlemodding.org/jailbreaking/prevent-auto-update/).

### WinterBreak2

The steps of the wiki page [WinterBreak2](https://kindlemodding.org/jailbreaking/WinterBreak2/):

1. Download [`wb2.zip`](https://github.com/KindleModding/Winterbreak2/releases/latest/download/wb2.zip) and
   extract it into the root of the Kindle drive. A folder `winterbreak2` appears there.
2. Eject the Kindle, unplug it, turn off Airplane Mode and connect to Wi-Fi.
3. Open the **Experimental Browser** (home screen → menu ⋮ → Experimental Browser; on some firmwares under
   Settings → Device Options → Advanced).
4. Open `https://penguins184.xyz/wb2` and press **Jailbreak**.
5. The Kindle prints some text and restarts its interface. On the Paperwhite 3 this took a few minutes, with the tree
   start screen and then a progress bar.

Check: `documents/JAILBROKEN.txt` exists on the Kindle drive and starts with "You are jailbroken!".

Afterwards delete the filler files and the `winterbreak2` folder. Updates are now blocked by the jailbreak. It
installs the package manager KPM and support for *scriptlets*: every `.sh` file in `documents` shows up in the
library like a book and runs as root when tapped. It also brings FBInk, the program this client uses to draw on the
screen.

Optional: `;kpm update` and `;kpm install kterm` in the search bar of the home screen install a terminal on the
Kindle, handy for looking around.

## 2. Installing the client

The server must be running and you need its URL and a device token (see the
[server README](../bummsn.de/README.md#first-start-password-and-device-token)).

1. Connect the Kindle by USB and copy:

   | From the repository | To the Kindle drive |
   |---|---|
   | `kindle/dashboard/dashboard.sh`, `touch.awk`, `config.example` | `dashboard/` |
   | `kindle/documents/dashboard-start.sh` | `documents/` |

2. Copy `dashboard/config.example` to `dashboard/config` and fill in:

   ```sh
   URL="https://example.org/dashboard"
   TOKEN="the-token-from-new-device-token"
   ```

3. The files must have Unix line endings (LF). The repository takes care of that (`.gitattributes`); when you edit
   them on Windows, use an editor that keeps LF.
4. Eject the Kindle. **Dashboard starten** appears in the library.

## 3. Using it

**Start:** tap **Dashboard starten** in the library. The normal Kindle interface disappears and the dashboard appears.
Hang the Kindle in landscape with the power button on the right (or set `Orientation=ButtonLeft` on the server).

**Day to day:** the Kindle sleeps and wakes up by itself when the server says so (every 30 minutes by default, not
during the quiet hours, and when a reminder becomes due). It fetches the picture and goes back to sleep after a few
seconds. The front light stays off. Changes from the phone therefore show up at the next wake-up.

**Taps:** press the power button once. The light turns on, the picture is updated, and for 60 seconds taps are
taken (each tap starts the 60 seconds again):

| Tap on | Effect |
|---|---|
| an item of the shopping list | checks it off, or back on |
| a reminder | marks it as done (it disappears) |
| the left column (date, weather) | updates the picture |

A tapped area gets a black frame at once; the new picture follows after two or three seconds.

**On the charger** the Kindle stays awake: changes from the phone appear within seconds, taps work all the time.

**Ending:** press the power button, then tap **5 times into a corner within 3 seconds**. Each counted tap shows a
small black square in that corner. The Kindle then restarts (about a minute) and comes back with its normal interface.
In an emergency: hold the power button for about 40 seconds, the Kindle restarts as well. Nothing is lost either way.

**Without connection:** if the server cannot be reached, the old picture stays and a small black square appears in
a corner. The Kindle tries again after 5 minutes.

The client writes a log to `dashboard/dashboard.log` on the Kindle drive (the times are in UTC).

## Settings

In `dashboard/config`:

| Setting | Default | Meaning |
|---|---|---|
| `URL` | – | address of the server, without a slash at the end |
| `TOKEN` | – | device token |
| `INTERACTIVE_SECONDS` | `60` | how long taps are taken after a press on the power button |
| `FRONTLIGHT_INTERACTIVE` | brightness set on the Kindle | front light (0–24) during those seconds |
| `FULL_REFRESH_EVERY` | `12` | a flashing full refresh after so many pictures, against ghosting |

Wake-up interval and quiet hours are set on the web page.

## How it works

Everything on the Kindle is plain `sh`, `awk` and the tools that come with the Kindle and the jailbreak; nothing has
to be compiled. Some findings that shaped it:

- **Drawing:** `/var/local/kmc/bin/fbink` from the jailbreak (the copy in `/mnt/us/libkh/bin` crashes on the
  Paperwhite 3). The server sends the picture already turned into the portrait orientation of the display and
  reduced to 16 grey levels. FBInk is called with `-w`, which waits until the display has finished: light greys take
  the longest, and going to sleep in the middle of an update left them almost black.
- **Sleeping:** the script stops the Kindle interface (`stop lab126_gui`), sets the wake-up alarm of the clock
  `/sys/class/rtc/rtc1/wakealarm` and suspends with `echo mem > /sys/power/state`. If it slept clearly shorter than
  the alarm, the power button woke it up. After waking up, Wi-Fi was back within about three seconds.
- **Why not powerd:** the Kindle's power manager can set alarms itself (`rtcWakeup`), but only in its
  *ready to suspend* state, which it never reaches while the interface is stopped (a simulated button press with
  `powerd_test -p` changed nothing). `preventScreenSaver` keeps it from interfering.
- **Ending with a restart:** starting the interface again after suspends that powerd did not know about left a white
  screen. A restart always brings the Kindle back into a clean state.
- **Touch:** the touch screen (`cyttsp4_mt`) is `/dev/input/event1` and reports in display coordinates (1072 × 1448,
  no turning). A loop of `dd` reads the raw input events, `od` turns them into hex, and `touch.awk` makes
  `tap <x> <y> <ms>` lines out of them (moves of more than 60 pixels are not taps). The tappable areas come from the
  server, already in these coordinates.
- **Crash reports:** stopping the interface makes its main application crash, and the Kindle writes
  `KPPMainAppV2_…_crash_…` files into `documents`, which then appear in the library. The script deletes them shortly
  after the start.

```
dashboard.sh
 ├─ start: stop the interface, light off, picture
 └─ loop
     ├─ on the charger: long poll (server answers when the picture changes) + taps, until unplugged
     └─ on battery: alarm from the server's "wake", suspend
          ├─ woken by the alarm: fetch and show the picture
          └─ woken by the power button: light on, picture, 60 s taps, light off
```

## Diagnostic scriptlets

`kindle/tools/` holds the small tests that were used to find out how the Paperwhite 3 behaves. Copy one into
`documents` on the Kindle, tap it in the library, then read its log from the root of the Kindle drive.

| Scriptlet | Log | Shows |
|---|---|---|
| `dashboard-check.sh` | `dashboard-check.log` | root rights, IP address, available tools, HTTPS with curl, display size, battery |
| `touch-check.sh` | `touch-check.log` | input devices and the raw events while you tap for 10 seconds |
| `sleep-check.sh` | `sleep-check.log` | suspend with the RTC alarm, wake-up by the power button, time until Wi-Fi is back |
| `grey-check.sh` (needs `grey-ramp.png` in `dashboard/`) | `grey-check.log`, `grey-fb-*.raw` | 16 grey levels with four FBInk modes, plus the framebuffer contents |
| `powerd-check.sh` | `powerd-check.log` | the attempt to let powerd handle sleeping (see above, it does not work without the interface) |

The checks that stop the interface start it again at the end. If the screen stays white afterwards, restart the
Kindle (hold the power button for 40 seconds).

## Troubleshooting

| Symptom | Cause and remedy |
|---|---|
| "Fehlt: dashboard/config" when starting | create `dashboard/config` from `config.example` |
| Nothing happens when tapping | taps are only taken for 60 seconds after a press on the power button |
| Black square in a corner, old picture | no connection to the server: Wi-Fi, server running, URL in `config`; see `dashboard.log` |
| `curl: (22) … 401` in the log | the token in `config` does not match the server (a new `new-device-token` replaces the old one) |
| Light greys much too dark | FBInk without `-w`, or an old `dashboard.sh` |
| White screen after ending | restart the Kindle (power button 40 seconds) |
| `KPPMainAppV2_…` entries in the library | crash reports, see above; delete them, the dashboard does it at its next start |

## Other Kindle models

What would have to be checked on another model (the diagnostic scriptlets help):

- the touch device (`/proc/bus/input/devices`) and whether it reports in display coordinates,
- the RTC with a writable `wakealarm` (`/sys/class/rtc/rtc*/wakealarm`),
- the resolution: the server draws for 1448 × 1072 (`Renderer.Width`/`Height`) and turns to 1072 × 1448,
- where FBInk is (`/var/local/kmc/bin/fbink`) and that it runs,
- the name of the interface job (`initctl list | grep gui`).

Newer Kindles with a read-only system ("rootless") and the `kindlehf` platform behave differently in places.
