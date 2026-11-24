# Kindle dashboard

An old Kindle Paperwhite 3 on the door instead of Post-its: a list of short notes in large letters and today's weather,
kept up to date from anywhere with a phone. Notes can be checked off right on the Kindle by tapping them.

![The dashboard as it looks on the wall](docs/dashboard.png)

*The picture of the Kindle in landscape (1448 × 1072, 16 grey levels), here with sample data.*

## How it fits together

```
 phone, anywhere                  your server (VPS)                          Kindle on the wall
┌───────────────┐   HTTPS    ┌───────────────────────────┐    HTTPS     ┌──────────────────────┐
│ web page      │ ─────────> │ ASP.NET Core application  │ <─────────── │ dashboard.sh         │
│ notes, place  │  password  │ data (SQLite), weather,   │ device token │ fetch picture, show, │
│ on a map      │            │ draws the picture, taps,  │              │ sleep until told,    │
└───────────────┘            │ wake-up times             │              │ taps after a button  │
                             └───────────────────────────┘              │ press                │
                                        │                               └──────────────────────┘
                                        └─> Bright Sky (DWD weather), OpenStreetMap services
```

- **The server does the work.** It keeps the data, fetches the weather, draws the complete picture and decides when
  the Kindle wakes up next. The Kindle only shows pictures and reports taps, so the layout can change without
  touching the Kindle.
- **The Kindle runs on battery.** It sleeps between updates (every 30 minutes by default, not at night) with the light
  off. A press on the power button turns on the light and takes taps for a minute.
  On the charger it stays awake and updates within seconds.
- **The phone** uses a small web page behind a password: the notes on a yellow Post-it (hold the handle of a note to
  move it), the place of the weather on an OpenStreetMap map, a preview of the picture and the Kindle's battery.

## What you need

- A **Kindle Paperwhite 3** (7th generation) with a firmware that can be jailbroken. Other models should work with
  small changes, see the [Kindle README](kindle/README.md#other-kindle-models).
- A **server** that is reachable from the internet with a domain and HTTPS, e.g. a small VPS with Ubuntu 24.04. The
  server part needs very little (it runs well on one CPU core).
- Wi-Fi where the Kindle hangs.
- Optional: a 3D printer for the [door mount](mount/README.md) and two tesa Powerstrips Large.

## Setting it up

1. **[Server](server/README.md):** install the ASP.NET Core application behind nginx, set the password and create a
   device token for the Kindle.
2. **[Kindle](kindle/README.md):** jailbreak it (WinterBreak2), copy the client, enter the server URL and the token,
   start it.
3. **Phone:** open the web page, log in, choose the place for the weather, add items.

Each README goes through every step, from a Kindle in factory state and an empty server to the running dashboard.

## Repository

| Folder | Content |
|---|---|
| [`server/`](server/README.md) | ASP.NET Core 10, SkiaSharp, SQLite; [API](server/API.md); deployment (systemd, nginx) |
| [`kindle/`](kindle/README.md) | client for the Kindle (`sh` and `awk`), the start scriptlet, diagnostic scriptlets |
| `docs/` | pictures for the documentation |
| [`mount/`](mount/README.md) | 3D-printed mount for a door, OpenSCAD |

## Status

Working: server, web page, Kindle client with sleep, wake-up by the power button, taps and the exit gesture.

Designed, not yet printed: the [door mount](mount/README.md).

Planned:

- information and switches from Home Assistant through a small agent in the home network.

## Credits

- [KindleModding](https://kindlemodding.org) for WinterBreak2, KPM and the scriptlet support, and
  [FBInk](https://github.com/NiLuJe/FBInk) by NiLuJe, which draws on the Kindle's screen.
- [Bright Sky](https://brightsky.dev) for the weather data of the [Deutscher Wetterdienst](https://www.dwd.de).
- [OpenStreetMap](https://www.openstreetmap.org/copyright) contributors for the map, Nominatim for place names,
  [Open-Meteo](https://open-meteo.com) for time zones, [ipwho.is](https://ipwho.is) for the first guess of the place,
  [Leaflet](https://leafletjs.com) for the map on the web page.
- [SkiaSharp](https://github.com/mono/SkiaSharp) for drawing, and the fonts [Noto Sans](https://notofonts.github.io) and
  [Noto Emoji](https://fonts.google.com/noto/specimen/Noto+Emoji) (SIL Open Font License 1.1, see `OFL.txt` and
  `OFL-NotoEmoji.txt` in `server/src/Dashboard/Fonts/`).

## License

[MIT](LICENSE), except for the Noto Sans font files in `server/src/Dashboard/Fonts/`, which are under the
SIL Open Font License 1.1 (`OFL.txt` next to them).
