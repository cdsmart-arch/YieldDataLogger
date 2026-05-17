# YDL Hetzner deployment runbook

Provisioning a fresh Hetzner CX22 (Ubuntu 22.04) and bringing YDL up. Follow top-to-bottom.

## 0. Prerequisites you do once outside the box

- Domain registered (Cloudflare).
- DNS A record `ydl.{domain} → <Hetzner IP>`, **proxy off** (grey cloud).
- Cloudflare API token: zone-level **Zone:Read + DNS:Edit** scoped to your domain.
- Hetzner Storage Box (BX11) provisioned. Note SSH endpoint `u123456@u123456.your-storagebox.de`.
- A shared secret: `openssl rand -hex 32`. Save in a password manager.
- A GHCR personal access token with `read:packages` (the deploy workflow pushes the image).

## 1. Bootstrap the VPS

```bash
ssh root@<hetzner-ip>

# Updates + Docker
apt update && apt upgrade -y
apt install -y ca-certificates curl gnupg sqlite3 borgbackup ufw
install -m 0755 -d /etc/apt/keyrings
curl -fsSL https://download.docker.com/linux/ubuntu/gpg | gpg --dearmor -o /etc/apt/keyrings/docker.gpg
echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.gpg] https://download.docker.com/linux/ubuntu $(lsb_release -cs) stable" \
    > /etc/apt/sources.list.d/docker.list
apt update && apt install -y docker-ce docker-ce-cli containerd.io docker-compose-plugin

# Caddy (with Cloudflare DNS module — needed for the DNS-01 challenge)
apt install -y debian-keyring debian-archive-keyring apt-transport-https
curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/gpg.key' | gpg --dearmor -o /usr/share/keyrings/caddy-stable-archive-keyring.gpg
curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/debian.deb.txt' > /etc/apt/sources.list.d/caddy-stable.list
apt update && apt install -y caddy
# Replace stock caddy with one that includes the Cloudflare DNS provider plugin.
caddy add-package github.com/caddy-dns/cloudflare
systemctl restart caddy

# UFW: SSH from anywhere (or your IP), HTTP/HTTPS for Caddy/ACME, nothing else.
ufw default deny incoming
ufw default allow outgoing
ufw allow 22/tcp
ufw allow 80/tcp
ufw allow 443/tcp
ufw --force enable
```

## 2. Configure secrets

```bash
# Container env — secret + GHCR pull credentials.
cat > /etc/ydl.env <<'EOF'
YDL_INGEST_SECRET=<paste the openssl rand -hex 32 value>
YDL_IMAGE=ghcr.io/cdsma/yielddatalogger-api:latest
EOF
chmod 600 /etc/ydl.env

# Caddy env — hostname + Cloudflare API token.
mkdir -p /etc/systemd/system/caddy.service.d
cat > /etc/systemd/system/caddy.service.d/override.conf <<'EOF'
[Service]
Environment="YDL_HOSTNAME=ydl.yourdomain.com"
Environment="CLOUDFLARE_API_TOKEN=<paste your CF token>"
EOF
systemctl daemon-reload
```

## 3. Data dir + bring up the API

```bash
# The container's appuser is uid 999 in the playwright image.
mkdir -p /var/lib/ydl
chown 999:999 /var/lib/ydl

# Log in to GHCR so docker can pull the private image.
echo <ghcr-pat> | docker login ghcr.io -u <github-user> --password-stdin

# Copy compose.yml from this repo (or scp it):
mkdir -p /opt/ydl && cd /opt/ydl
# (scp compose.yml root@<hetzner-ip>:/opt/ydl/)

docker compose --env-file /etc/ydl.env pull
docker compose --env-file /etc/ydl.env up -d
docker compose logs -f --tail 50    # watch it come up; ctrl-c when healthy
```

## 4. Caddy reverse proxy

```bash
# Copy deploy/Caddyfile from the repo:
cp /opt/ydl/Caddyfile /etc/caddy/Caddyfile
systemctl reload caddy

# Verify:
curl https://ydl.yourdomain.com/healthz
# expect: {"status":"ok","backend":"sqlite",...}

curl https://ydl.yourdomain.com/api/instruments
# expect: 401 "missing or invalid ingest secret"

curl -H "X-Ingest-Secret: <secret>" https://ydl.yourdomain.com/api/instruments
# expect: 200 + JSON instrument catalog
```

## 5. Historical data import (one-shot)

Run from your local dev machine — needs the Azure Tables connection string. Streams every
row from the Azure table and INSERT OR IGNOREs into the live SQLite file (concurrent with the
new scraper is fine; primary key dedup keeps it correct).

```bash
# From the repo root:
dotnet run -c Release --project src/YieldDataLogger.Migrate -- \
    --connection "DefaultEndpointsProtocol=https;AccountName=...;AccountKey=...;EndpointSuffix=core.windows.net" \
    --sqlite     "/tmp/ydl-import.sqlite"

# Then upload the file and merge in place by running the same tool on the server pointing at
# /var/lib/ydl/ydl.sqlite directly. Same INSERT OR IGNORE; safe to run live.
scp /tmp/ydl-import.sqlite root@<hetzner-ip>:/tmp/
ssh root@<hetzner-ip>
# (run the migrate tool again on the server, target /var/lib/ydl/ydl.sqlite)
```

## 6. Nightly backups

```bash
# Once-off: initialise the Borg repo on the Storage Box.
cat > /etc/borg-ydl.env <<'EOF'
export BORG_REPO=ssh://u123456@u123456.your-storagebox.de:23/./ydl-repo
export BORG_PASSPHRASE=<openssl rand -hex 32>
export BORG_RSH="ssh -i /root/.ssh/id_ed25519"
EOF
chmod 600 /etc/borg-ydl.env

source /etc/borg-ydl.env
borg init --encryption=repokey-blake2 "$BORG_REPO"

# Install the script + systemd units.
install -m 0755 /opt/ydl/borg-backup.sh /usr/local/bin/borg-backup-ydl
install -m 0644 /opt/ydl/borg-ydl.service /etc/systemd/system/borg-ydl.service
install -m 0644 /opt/ydl/borg-ydl.timer   /etc/systemd/system/borg-ydl.timer

systemctl daemon-reload
systemctl enable --now borg-ydl.timer
systemctl start borg-ydl.service     # immediate first snapshot
borg list "$BORG_REPO"               # confirm
```

## 7. Cut over the Windows Agent

On the trading rig, edit `%ProgramData%\YieldDataLogger\Agent\appsettings.json` (or set via
user-secrets on the build machine before publishing the installer):

```json
"Agent": {
  "HubUrl":     "https://ydl.yourdomain.com/hubs/ticks",
  "ApiBaseUrl": "https://ydl.yourdomain.com",
  "AuthToken":  "<the openssl rand -hex 32 secret>"
}
```

Restart the YieldDataLogger.Agent Windows service. Watch
`%ProgramData%\YieldDataLogger\Agent\status.json` for `HubState=Connected` and
`TicksReceived` advancing.

## 8. Rollback

Revert the Agent's `HubUrl`/`ApiBaseUrl`/`AuthToken` to the Azure values. The Azure Container
App is left running for at least 7 days after cutover as warm rollback.
