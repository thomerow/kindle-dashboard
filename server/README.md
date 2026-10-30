# Dashboard server

Small ASP.NET Core 10 server: the central part of the [Kindle dashboard](../README.md). It keeps the shopping list,
notes and reminders, fetches the weather, draws the picture for the Kindle and tells the Kindle when to wake up
next. A web page for the phone changes the data from anywhere.

```
phone (web page) ── login cookie ──>  server  <── device token ── Kindle (dashboard.sh)
                                         │
                                         └──> Bright Sky (weather), OpenStreetMap services (place)
```

The interface is described in [`API.md`](API.md).

## Contents

- [What the server does](#what-the-server-does)
- [Structure](#structure)
- [Running it locally](#running-it-locally)
- [Installation on a server](#installation-on-a-server)
- [First start: password and device token](#first-start-password-and-device-token)
- [Updating](#updating)
- [Backup](#backup)
- [Settings](#settings)
- [External services](#external-services)
- [Security](#security)

## What the server does

- **Data:** shopping list, notes, reminders (with date or date and time), settings and a log of the Kindle's battery,
  in an SQLite database.
- **Weather:** current weather, the next hours and the next five days from [Bright Sky](https://brightsky.dev)
  (data of the Deutscher Wetterdienst, so it covers Germany and the regions around it), fetched every 30 minutes.
- **Picture:** drawn with [SkiaSharp](https://github.com/mono/SkiaSharp) in landscape (1448 × 1072), with
  anti-aliasing and reduced to the 16 grey levels of the e-ink display. For the Kindle it is turned into the portrait
  orientation of its display, together with the tappable areas. It is only drawn again when its content changes.
- **Wake-up times:** the Kindle runs on battery and sleeps between updates. The server tells it when to wake up:
  every *n* minutes, not during the quiet hours, and earlier when a reminder with a time becomes due.
- **Taps:** the Kindle reports which area was tapped (check off an item, mark a reminder as done), the server
  changes the data and answers with the new picture.
- **Web page:** one HTML file without a framework, made for a phone in portrait orientation. Lists, a preview of the
  picture, the Kindle's battery, and the place of the weather on an OpenStreetMap map (with a first guess from the IP
  address).

## Structure

```
src/Dashboard/Program.cs          endpoints, configuration, command line tools
src/Dashboard/Store.cs            SQLite: items, settings, battery log
src/Dashboard/Secrets.cs          state directory, password hash, device token
src/Dashboard/Screen.cs           current picture, Kindle state, taps
src/Dashboard/Renderer.cs         drawing the picture (layout), turning it for the display
src/Dashboard/WeatherIcon.cs      weather icons as vector shapes
src/Dashboard/Weather.cs          Bright Sky
src/Dashboard/Schedule.cs         local time, due dates, wake-up times
src/Dashboard/Geo.cs              IP position, place search, names and time zones
src/Dashboard/Fonts/              Noto Sans (SIL Open Font License, see OFL.txt)
src/Dashboard/wwwroot/index.html  web page
deploy/deploy.sh                  build and install on the server
deploy/deploy.env.example         settings for deploy.sh
deploy/dashboard.service          systemd unit (template)
deploy/dashboard.conf             nginx location (template)
API.md                            interface
.editorconfig                     C# code style, also used by "dotnet format"
```

Code style: indentation of 3 spaces, opening braces on the line of the statement, `else`/`catch`/`finally` on the
line of the closing brace, member variables `m_name`, static ones `s_name`. `dotnet format src/Dashboard` applies it.

## Running it locally

Requires the [.NET SDK 10](https://dotnet.microsoft.com/download). On Windows start it from PowerShell (Git Bash
turns `/dashboard` into a Windows path):

```powershell
cd src/Dashboard
'my-local-password' | dotnet run -- set-password
dotnet run -- new-device-token        # prints a token for testing the Kindle endpoints
$env:Urls = 'http://127.0.0.1:5091'
$env:PathBase = '/dashboard'
dotnet run
```

The page is then at `http://127.0.0.1:5091/dashboard/`. Without `STATE_DIRECTORY` the server keeps its data in
`src/Dashboard/data/` (not part of the repository).

Trying the Kindle side with curl:

```bash
TOKEN=...   # from new-device-token
curl -H "Authorization: Bearer $TOKEN" "http://127.0.0.1:5091/dashboard/api/device/frame?battery=80"
curl -H "Authorization: Bearer $TOKEN" -o frame.png http://127.0.0.1:5091/dashboard/api/device/image
```

## Installation on a server

This describes a fresh **Ubuntu 24.04** server with a domain pointing to it (here `example.org`). The application
runs behind nginx, which terminates TLS, under a sub-path (here `/dashboard`) and as its own system user. Other
distributions work the same way with their own package names.

### 1. Packages

```bash
sudo apt update
sudo apt install nginx certbot python3-certbot-nginx dotnet-sdk-10.0 rsync sqlite3 git
```

`dotnet-sdk-10.0` comes from the Ubuntu archive and also brings the ASP.NET Core runtime. Security updates of the
runtime then arrive with the normal system updates, which is why the application is built framework-dependent and
not self-contained.

### 2. TLS certificate

If the domain has no certificate yet:

```bash
sudo certbot --nginx -d example.org
```

certbot adds a `server` block on port 443 to the site of the domain (on Ubuntu in `/etc/nginx/sites-available/`) and
renews the certificate by itself.

### 3. A directory for the nginx part of the application

`deploy.sh` puts a `location` block into a directory that the server block on 443 includes. Create the directory
and add one line inside that `server { … }` block (the one with `listen 443 ssl`):

```bash
sudo mkdir -p /etc/nginx/apps-enabled
sudo nano /etc/nginx/sites-available/default     # or the file certbot changed for your domain
```

```nginx
    include /etc/nginx/apps-enabled/*.conf;
```

```bash
sudo nginx -t && sudo systemctl reload nginx
```

### 4. Sources and settings

```bash
git clone <url of this repository> ~/kindle-dashboard
cd ~/kindle-dashboard/server
cp deploy/deploy.env.example deploy/deploy.env
nano deploy/deploy.env
```

| Setting | Default | Meaning |
|---|---|---|
| `NAME` | `dashboard` | system user, `/opt/<name>` (program), `/var/lib/<name>` (data) |
| `SERVICE` | `$NAME` | name of the systemd unit |
| `PORT` | `5091` | local port behind nginx, bound to 127.0.0.1 only; must be free (`ss -ltn`) |
| `PATHBASE` | `/$NAME` | sub-path in the URL |
| `NGINX_DIR` | `/etc/nginx/apps-enabled` | directory from step 3 |
| `PUBLIC_URL` | empty | e.g. `https://example.org`, only used for the checks at the end |
| `EXTRA_CHECKS` | empty | further URLs that must still answer afterwards (other applications on the server) |

The orientation of the picture is set in `deploy/dashboard.service` (`Orientation=ButtonRight`, see
[Settings](#settings)).

### 5. Build and install

```bash
deploy/deploy.sh
```

The script, step by step:

1. creates the system user (`useradd --system`, no login shell) if needed,
2. builds with `dotnet publish` (on a small server this takes a few minutes),
3. copies the result to `/opt/<name>`, owned by root and only readable for the service user, so the application
   cannot change its own code,
4. installs the systemd unit with a hardening block (read-only system, no access to home directories, private `/tmp`;
   `systemd-analyze security` rates it about 6.8) and `StateDirectory=`, which creates `/var/lib/<name>`,
5. installs the nginx `location` and reloads nginx, but only if `nginx -t` passes; otherwise it restores the previous
   file, because a broken file would keep nginx from starting at its next restart,
6. checks that the service runs and answers, directly and through nginx.

Expected end of the output:

```
--- checks
active
HTTP/1.1 200 OK
HTTP/1.1 200 OK
...
Note: no password yet, see README (set-password, new-device-token)
```

The service logs with `sudo journalctl -u <service> -f`. Right after a start it needs about five seconds before it
answers; a `502` from nginx in that time is normal.

## First start: password and device token

Both are created with the program itself, as the service user, so that the files belong to it. Only hashes are
stored, in `/var/lib/<name>/secrets.json`. Changes take effect without a restart.

**Password of the web page** (asks for it without showing it, at least 8 characters):

```bash
sudo -u dashboard env STATE_DIRECTORY=/var/lib/dashboard /opt/dashboard/Dashboard set-password
```

**Token for the Kindle** (prints it once; a new one replaces the old one):

```bash
sudo -u dashboard env STATE_DIRECTORY=/var/lib/dashboard /opt/dashboard/Dashboard new-device-token
```

The token goes into `dashboard/config` on the Kindle, see the [Kindle README](../kindle/README.md). Then open
`https://example.org/dashboard/`, log in, and choose the place for the weather under **Ort**.

## Updating

```bash
cd ~/kindle-dashboard && git pull
cd server && deploy/deploy.sh
```

The data in `/var/lib/<name>` stays. The Kindle notices nothing except a moment without an answer, which it treats like
any other network problem.

## Backup

Everything the server keeps permanently is in `/var/lib/<name>`:

| File | Content |
|---|---|
| `dashboard.db` (+ `-wal`, `-shm`) | items, settings, battery log; SQLite in WAL mode |
| `secrets.json` | password hash, hash of the device token |
| `keys/` | keys for the login cookie (without them every browser has to log in again) |

Because of the WAL mode, copy the database with SQLite instead of copying the file while the service runs:

```bash
sudo sqlite3 /var/lib/dashboard/dashboard.db ".backup '/root/backup/dashboard.db'"
sudo cp -a /var/lib/dashboard/secrets.json /var/lib/dashboard/keys /root/backup/
```

## Settings

Settings of the program (environment variables, set in the systemd unit):

| Variable | Meaning |
|---|---|
| `Urls` | address the server binds to, e.g. `http://127.0.0.1:5091` |
| `PathBase` | sub-path behind the reverse proxy, e.g. `/dashboard` |
| `Orientation` | `ButtonRight` (default) or `ButtonLeft`: where the power button of the Kindle is when it hangs in landscape. The Paperwhite 3 has it on the short edge below the display. |
| `STATE_DIRECTORY` | directory for the data; systemd sets it through `StateDirectory=`. If unset: `data/` in the project directory |

Everything else (place, wake-up interval, quiet hours) is set on the web page and stored in the database.

## External services

All of them work without an API key. Please keep to their terms; for a single dashboard the load is tiny.

| Service | Used for | Called by |
|---|---|---|
| [Bright Sky](https://brightsky.dev) | weather (data: [Deutscher Wetterdienst](https://www.dwd.de), named on the picture) | server, every 30 minutes |
| [Nominatim](https://nominatim.org) (OpenStreetMap) | place search, name of a point; at most one request per second, which the server enforces | server, when choosing the place |
| [Open-Meteo](https://open-meteo.com) | time zone of a point | server, when choosing the place |
| [ipwho.is](https://ipwho.is) | rough position of the IP address as a first guess | server, once while the place was never saved |
| [OpenStreetMap tiles](https://www.openstreetmap.org/copyright), [Leaflet](https://leafletjs.com) from cdnjs | map on the web page | browser |

The server identifies itself with the user agent `kindle-dashboard/1.0`.

## Security

- **Web page:** one password, stored as PBKDF2-SHA256 hash (600,000 iterations). The login cookie is HttpOnly and
  SameSite=Strict; logins are limited to 10 per minute and IP address.
- **Kindle:** a random token of 256 bits, only its SHA-256 hash is stored. It only allows reading the picture and
  tapping its areas.
- **Server:** the application runs as its own system user without a shell, cannot write its program files, is
  only reachable through nginx (bound to 127.0.0.1) and limits requests per IP address.
- Pages and API answer with `X-Robots-Tag: noindex`.
