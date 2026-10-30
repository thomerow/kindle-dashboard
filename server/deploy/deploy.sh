#!/bin/bash
# Builds and installs the Kindle dashboard server. Run it on the server in the directory of the sources:
#   deploy/deploy.sh
# Settings come from deploy/deploy.env if it exists (see deploy.env.example), otherwise the defaults below.
# Every step is explained in README.md (section "Installation on a server").
# Author: Thorsten Kummerow <thorsten.kummerow (at) gmail (dot) com>
set -euo pipefail
cd "$(dirname "$0")/.."

[ -f deploy/deploy.env ] && . deploy/deploy.env
NAME=${NAME:-dashboard}                          # service user, /opt/<name>, /var/lib/<name>
SERVICE=${SERVICE:-$NAME}                        # systemd unit <service>.service
PORT=${PORT:-5091}                               # local port behind nginx
PATHBASE=${PATHBASE:-/$NAME}                     # sub-path in the URL
NGINX_DIR=${NGINX_DIR:-/etc/nginx/apps-enabled}  # directory included into the server block on 443
PUBLIC_URL=${PUBLIC_URL:-}                       # e.g. https://example.org, only for the checks at the end

for file in deploy/dashboard.service deploy/dashboard.conf deploy/deploy.sh; do
   if grep -q $'\r' "$file"; then
      echo "$file has Windows line endings (CRLF), convert it first" >&2
      exit 1
   fi
done

id "$NAME" >/dev/null 2>&1 || sudo useradd --system --no-create-home --shell /usr/sbin/nologin "$NAME"

# Framework-dependent on purpose: the .NET runtime then gets its security updates from apt
dotnet publish src/Dashboard -c Release -r linux-x64 --no-self-contained -o publish
sudo mkdir -p "/opt/$NAME"
sudo rsync -a --delete publish/ "/opt/$NAME/"
sudo chown -R root:root "/opt/$NAME"
sudo chmod +x "/opt/$NAME/Dashboard"

unit=$(mktemp)
sed -e "s|@NAME@|$NAME|g" -e "s|@PORT@|$PORT|g" -e "s|@PATHBASE@|$PATHBASE|g" deploy/dashboard.service > "$unit"
sudo install -m 644 "$unit" "/etc/systemd/system/$SERVICE.service"
rm "$unit"
sudo systemctl daemon-reload
sudo systemctl enable "$SERVICE"
sudo systemctl restart "$SERVICE"

# nginx: never leave a file behind that does not pass the test, a broken file would stop nginx at its next restart
target="$NGINX_DIR/$NAME.conf"
previous=$(mktemp)
if [ -f "$target" ]; then sudo cp "$target" "$previous"; else rm "$previous"; fi
sed -e "s|@PORT@|$PORT|g" -e "s|@PATHBASE@|$PATHBASE|g" deploy/dashboard.conf | sudo tee "$target" > /dev/null
if ! sudo nginx -t; then
   if [ -f "$previous" ]; then sudo cp "$previous" "$target"; else sudo rm "$target"; fi
   echo "nginx -t failed, configuration restored" >&2
   exit 1
fi
rm -f "$previous"
sudo systemctl reload nginx

# The application needs about five seconds to start, nginx a moment for the reload
sleep 8
echo "--- checks"
systemctl is-active "$SERVICE"
curl -sI "http://127.0.0.1:$PORT$PATHBASE/" | head -1
if [ -n "$PUBLIC_URL" ]; then
   curl -sI "$PUBLIC_URL$PATHBASE/" | head -1
   curl -sI "$PUBLIC_URL/" | head -1
fi
for check in ${EXTRA_CHECKS:-}; do
   echo "$check: $(curl -sI "$check" | head -1)"
done
sudo test -f "/var/lib/$NAME/secrets.json" || echo "Note: no password yet, see README (set-password, new-device-token)"
