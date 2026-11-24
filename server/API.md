# API of the dashboard server

Base URL: wherever the server runs, e.g. `https://example.org/dashboard` (locally: `http://127.0.0.1:5091/dashboard`).
All paths below are relative to it. Data is JSON in UTF-8 with field names in camelCase, except for the Kindle
endpoints, which answer in plain text so that a shell script can read them with `awk`.

The API has two sides with different authentication:

| Side | Endpoints | Used by | Authentication |
|---|---|---|---|
| Web page | `/api/session`, `/api/login`, `/api/logout`, `/api/items…`, `/api/settings`, `/api/state`, `/api/preview.png`, `/api/battery`, `/api/geo/…` | browser (phone) | login cookie |
| Kindle | `/api/device/…` | `dashboard.sh` on the Kindle | `Authorization: Bearer <device token>` |

The password and the device token are set on the server with command line tools, see the
[server README](README.md#first-start-password-and-device-token).

## Limits

| What | Limit | Answer when exceeded |
|---|---|---|
| `POST /api/login` | 10 per minute and IP address | `429` |
| Other web endpoints | 300 per minute and IP address | `429` |
| `/api/geo/…` | 30 per minute and IP address (they call external services) | `429` |
| Kindle endpoints | 600 per minute and IP address | `429` |
| Long polls held open at the same time (`wait` > 0) | 4 | `503` |
| Request body | 16 KB | `413` |
| Text of an item | 200 characters | `400` |

## Data types

### Item

A note on the list, like a Post-it on the door.

```json
{
  "id": 12,
  "text": "Take out the rubbish",
  "created": "2026-10-30T21:11:21.3731062+00:00",
  "done": null
}
```

| Field | Type | Meaning |
|---|---|---|
| `id` | number | unique, assigned by the server |
| `text` | string | at most 200 characters |
| `created` | ISO 8601, UTC | |
| `done` | ISO 8601 \| null | when the note was checked off |

What the picture shows: open notes in their order on the list, then checked-off ones (struck through), in the
largest font size at which all of them fit (at most three lines each). Checked-off notes are deleted automatically
after 12 hours.

The database keeps the columns `kind` and `due` of an earlier version with shopping list, notes and reminders. Items of
those kinds show up as notes; new ones get the kind `todo`. The column `position` (the order on the list) is added at start
when it is missing; notes without one are sorted in by their id.

### Settings

```json
{
  "placeName": "Bremen",
  "latitude": 53.0758,
  "longitude": 8.8072,
  "timeZone": "Europe/Berlin",
  "intervalMinutes": 30,
  "quietFrom": "23:00",
  "quietTo": "06:00"
}
```

| Field | Type | Meaning |
|---|---|---|
| `placeName` | string | name shown on the picture, at most 60 characters |
| `latitude`, `longitude` | number | place of the weather forecast |
| `timeZone` | IANA name | local time for the date, the weather of the day and wake-up times |
| `intervalMinutes` | 5–1440 | the Kindle on battery wakes up every this many minutes, counted from midnight |
| `quietFrom`, `quietTo` | `"HH:mm"` | no regular wake-ups in between; may cross midnight; equal times: no quiet hours |

Defaults before the settings are saved for the first time: Berlin, 30 minutes, 23:00–06:00.

## Web page

### `GET /api/session`

No login needed. `{"loggedIn": false, "hasPassword": true}`. `hasPassword` is false until a password was set on the
server.

### `POST /api/login`

Body `{"password": "…"}`. Sets the login cookie (`dashboard_login`, HttpOnly, SameSite=Strict, valid for a year and
renewed while in use) and answers `204`. Wrong password: `401`. No password set yet: `503`.

### `POST /api/logout`

Removes the cookie, `204`.

### `GET /api/items`

All items in their order on the list (new ones at the end).

### `POST /api/items`

Body `{"text": "…"}`. Answers the new item. Invalid data: `400` with `{"error": "…"}`.

### `PATCH /api/items/{id}`

Body with the fields to change, both optional: `{"text": "…", "done": true}`. `"done": false` takes back a check
mark. Answers the changed item, `404` if there is none with this id.

### `DELETE /api/items/{id}`

`204`, or `404`.

### `PUT /api/items/order`

Body `{"ids": [3, 1, 2]}`: these items in this order at the top of the list, all others after them as they were.
Unknown ids are ignored. Answers all items in the new order, `400` without `ids`.

### `POST /api/items/delete-done`

Deletes all checked-off items. `{"deleted": 3}`.

### `GET /api/settings`, `PUT /api/settings`

Reads or replaces the [settings](#settings). `PUT` answers the saved settings or `400` with `{"error": "…"}`.

### `GET /api/state`

What the page shows about the Kindle:

```json
{
  "frame": { "rev": "36558139619c", "renderedAt": "2026-10-30T21:18:31.83+00:00" },
  "device": {
    "lastSeen": "2026-10-30T21:18:31.83+00:00",
    "battery": 78,
    "charging": false,
    "rev": "36558139619c",
    "nextWakeAt": "2026-10-30T21:30:00.83+00:00"
  },
  "weather": { "fetchedAt": "2026-10-30T21:18:21.6+00:00" },
  "settingsSaved": true,
  "nextWakeSeconds": 574
}
```

| Field | Meaning |
|---|---|
| `frame.rev` | revision of the current picture, a hash of its content: equal content, equal `rev` (also after a restart) |
| `device` | from the last contact of the Kindle; all values `null` before the first contact |
| `device.rev` | revision of the picture the Kindle showed at its last contact |
| `weather` | `null` until the first forecast arrived |
| `settingsSaved` | false as long as the settings were never saved (the page then suggests a place from the IP address) |
| `nextWakeSeconds` | when the Kindle would wake up if it asked now |

### `GET /api/preview.png`

The current picture in landscape (1448 × 1072, 8-bit grey), as it looks on the wall.

### `GET /api/battery?days=7`

Battery levels the Kindle reported, at most 90 days back: `[{"time": "…", "level": 78, "charging": false}, …]`.
One entry per change, otherwise at most one every 15 minutes.

### Help for choosing the place

These call external services without an API key (see the [server README](README.md#external-services)).

| Endpoint | Answer |
|---|---|
| `GET /api/geo/ip` | rough position of the caller's IP address: `{"latitude": …, "longitude": …, "name": "Bremen"}`, `404` if unknown |
| `GET /api/geo/search?q=Potsdam` | up to six places: `[{"latitude": …, "longitude": …, "name": "Potsdam, Brandenburg, Deutschland"}, …]` |
| `GET /api/geo/place?lat=52.39&lon=13.06` | name and time zone of a point: `{"name": "Potsdam", "timeZone": "Europe/Berlin"}` |

## Kindle

All three need the header `Authorization: Bearer <device token>`, otherwise `401`.

### `GET /api/device/frame`

Query parameters, all optional:

| Parameter | Meaning |
|---|---|
| `rev` | revision of the picture on the Kindle's screen |
| `battery` | battery level 0–100 |
| `charging` | `1` if the Kindle is on the charger |
| `wait` | seconds (at most 50) to wait for a picture that differs from `rev` (long poll, used on the charger) |

Answer (`text/plain`), one entry per line:

```
rev 36558139619c
wake 1118
zone item:18 768 0 280 1000
zone refresh 0 1000 1072 448
…
```

| Line | Meaning |
|---|---|
| `rev <rev>` | revision of the current picture |
| `wake <seconds>` | when the Kindle should wake up next (at least 120): the next interval slot, moved behind the quiet hours |
| `zone <id> <x> <y> <width> <height>` | a tappable area in **touch coordinates of the display** (portrait, 1072 × 1448), already turned to the orientation the Kindle hangs in |

Zone ids: `item:<id>` checks a note off (or back on), `refresh` only redraws.

The battery level and the charging state are part of the picture, so a change of them can give a new `rev`
(the battery in steps of 5 %).

### `GET /api/device/image`

The current picture turned into the orientation of the display: PNG, 1072 × 1448, 8-bit grey with 16 levels. The
header `X-Rev` carries its revision.

### `POST /api/device/tap?zone=<id>`

Carries out the action of a zone and answers like `GET /api/device/frame` with the new state. Unknown zone or item:
`404`.
